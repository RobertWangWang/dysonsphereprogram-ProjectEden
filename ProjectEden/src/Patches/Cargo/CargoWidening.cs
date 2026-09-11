using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using System.Threading;
using HarmonyLib;
using UnityEngine;

namespace ProjectEden.Patches
{
    /// <summary>
    /// 运行时这一半的「<c>Cargo.inc</c> 加宽」。preloader 改的是类型结构，这里管三件它管不了的事：
    /// <b>认出加宽有没有生效</b>、<b>把渲染那一路接回去</b>、<b>让本 mod 自己的传送带调用两边都能跑</b>。
    ///
    /// <b>为什么本 mod 自己也得处理。</b> 加宽改的是<b>方法签名</b>：
    /// <c>TryPickItemAtRear(…, out byte stack, out byte inc)</c> 变成了 <c>out short inc</c>。
    /// 我们的巨型建筑传送带 I/O 正好在用这套 API，而我们的 DLL 是按<b>原始</b>签名编译的——
    /// IL 里那条 MemberRef 写着 <c>uint8&amp;</c>，运行时的方法是 <c>int16&amp;</c>，
    /// 解析不上就是 <c>MissingMethodException</c>，而且是<b>第一次 tick 才炸</b>。
    ///
    /// <b>解法：委托类型声明在我们自己的程序集里。</b> 委托签名只提到基元类型和游戏的类，
    /// 不提 <c>Cargo</c> 的字段宽度，所以两套签名各绑一个、运行时挑一个。
    /// 这样既不用拿「改写过的 Assembly-CSharp」当编译引用（那会把构建和一个生成物绑死），
    /// 也没有反射装箱进热路径。
    ///
    /// <b>加宽没生效时一切照旧</b>——走字节那套委托，夹取补丁继续兜底。
    /// 这条很重要：preloader 可能因为游戏更新而整体放弃改写，那时 mod 必须还能用。
    /// </summary>
    internal static class CargoWidening
    {
        /// <summary>preloader 是否真的把 <c>Cargo.inc</c> 加宽了。<b>查的是结果，不是我们自己的声明。</b></summary>
        internal static readonly bool IsActive =
            AccessTools.Field(typeof(Cargo), nameof(Cargo.inc))?.FieldType == typeof(short);

        /// <summary>
        /// <c>Cargo.stack</c> 是不是也加宽了。<b>单独查，不假设跟 inc 同进退</b>——
        /// 两个字段是分两次加宽的，而且 preloader 可能只成功一半就整体放弃。
        /// </summary>
        internal static readonly bool StackIsWide =
            AccessTools.Field(typeof(Cargo), nameof(Cargo.stack))?.FieldType == typeof(short);

        /// <summary>
        /// 分拣器的两个堆叠层数字段是不是也加宽了。单独查，理由同上。
        /// </summary>
        internal static readonly bool InserterIsWide =
            AccessTools.Field(typeof(InserterComponent), nameof(InserterComponent.stackInput))?.FieldType == typeof(short);

        internal static void Report()
        {
            if (IsActive)
            {
                ProjectEdenPlugin.Log.LogWarning(
                    $"Cargo.inc{(StackIsWide ? " / Cargo.stack" : "")} 已加宽为 Int16（结构体 {Stride} 字节）：" +
                    "255 层集装 + 满级增产剂可以同时成立，传送带上的增产点数不再被一个字节卡住。" +
                    "存档从此绑定本 mod——卸载后新存档打不开（老存档仍可读，货物块按版本分流）。");

                return;
            }

            ProjectEdenPlugin.Log.LogInfo(
                "Cargo.inc 仍是 Byte：preloader 没装或整体放弃了改写。" +
                "传送带集装超过 63 层时，增产点数按 CargoIncClampPatches 夹在 255——" +
                "是确定的降级，不是失真。要满级增产就把 stations.json 的三个集装值调回 63。");
        }

        // ── 运行时读出来的结构体布局 ──────────────────────────
        //
        // **不写死偏移。** 加宽后 Cargo 变成 36 字节、字段位置全挪了，
        // 而这份代码是按 32 字节那版编译的——硬编码等于猜。
        // Marshal 查的是运行时的真实布局，加宽与否都对。

        internal static readonly int Stride = Marshal.SizeOf(typeof(Cargo));

        private static readonly int OffStack = (int)Marshal.OffsetOf(typeof(Cargo), nameof(Cargo.stack));
        private static readonly int OffInc = (int)Marshal.OffsetOf(typeof(Cargo), nameof(Cargo.inc));
        private static readonly int OffItem = (int)Marshal.OffsetOf(typeof(Cargo), nameof(Cargo.item));
        private static readonly int OffPos = (int)Marshal.OffsetOf(typeof(Cargo), nameof(Cargo.position));
        private static readonly int OffRot = (int)Marshal.OffsetOf(typeof(Cargo), nameof(Cargo.rotation));

        /// <summary>
        /// 传给 GPU 的 32 字节副本，字段顺序和原版 <c>Cargo</c> 一模一样。
        /// 着色器按这个布局解析，<b>它不能变</b>。
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct CargoRender
        {
            public byte stack;
            public byte inc;
            public short item;
            public Vector3 position;
            public Quaternion rotation;
        }

        [ThreadStatic] private static CargoRender[] _render;

        private static int _warned;

        /// <summary>
        /// 把加宽后的 36 字节 <c>Cargo[]</c> 重打包成 32 字节再上传。
        ///
        /// <b>为什么非重打包不可。</b> <c>CargoContainer.Draw</c> 建的是
        /// <c>new ComputeBuffer(poolCapacity, 32, …)</c>——stride 是<b>写死的字面量 32</b>，
        /// 然后 <c>SetData</c> 把 <c>Cargo[]</c> 整个裸传上去。结构体一变成 36 字节，
        /// Unity 当场抛 stride 不匹配。
        ///
        /// <b>inc 那个字节写什么都行</b>——探针实测着色器根本不读它
        /// （两个 stack 下把 inc 从 0 扫到 255，画面差异全在噪声底以内）。
        /// 这里仍然写夹在 255 的真值，纯粹为了让这份副本自洽、以后好排查。
        /// </summary>
        internal static unsafe void UploadRepacked(ComputeBuffer buffer, Array data, int from, int to, int count)
        {
            // **没东西要传就直接返回，绝不能"原样转发"。**
            // Unity 在看 count 之前就先校验 stride：拿 36 字节的 Cargo[] 去喂一个 stride=32 的
            // 缓冲区，哪怕 count 是 0 也照样抛
            //   SetData(): One of C# data stride (36 bytes) and Buffer stride (32 bytes)
            //   should be multiple of other.
            // 而 CargoContainer.Draw 是**先**调 SetData、**后**才判断 cursor <= 0 返回，
            // 所以开局货物池还空着的那几帧必然走到这里。实测炸过。
            if (count <= 0) return;

            if (!(data is Cargo[] pool))
            {
                if (Interlocked.Exchange(ref _warned, 1) == 0)
                    ProjectEdenPlugin.Log.LogError(
                        $"货物渲染：拿到的不是 Cargo[] 而是 {data?.GetType().Name ?? "null"}，" +
                        "这一帧不上传。加宽后原样转发必定因 stride 不匹配报错，所以宁可不画。");

                return;
            }

            CargoRender[] render = _render;

            if (render == null || render.Length < count)
                render = _render = new CargoRender[Mathf.NextPowerOfTwo(count)];

            GCHandle h = GCHandle.Alloc(pool, GCHandleType.Pinned);

            try
            {
                var src = (byte*)h.AddrOfPinnedObject();

                for (var i = 0; i < count; i++)
                {
                    byte* c = src + (long)i * Stride;

                    int inc = *(short*)(c + OffInc);

                    // 渲染副本的 stack / inc 都只有一个字节（着色器认的就是这个布局），
                    // 所以超过 255 的值在这里**夹住**而不是截断——截断会回绕成垃圾值。
                    // 只影响画面：逻辑走的是 36 字节那份，一个字都不会丢。
                    int st = StackIsWide ? *(short*)(c + OffStack) : *(c + OffStack);

                    render[i].stack = (byte)(st > 255 ? 255 : st < 0 ? 0 : st);
                    render[i].inc = (byte)(inc > 255 ? 255 : inc < 0 ? 0 : inc);
                    render[i].item = *(short*)(c + OffItem);
                    render[i].position = *(Vector3*)(c + OffPos);
                    render[i].rotation = *(Quaternion*)(c + OffRot);
                }
            }
            finally
            {
                h.Free();
            }

            buffer.SetData(render, from, to, count);
        }

        /// <summary>
        /// 按<b>运行时真实宽度</b>读一件货的层数。
        ///
        /// 本文件是按<b>未加宽</b>的布局编译的，所以 <c>pool[i].stack</c> 这种写法不能用——
        /// IL 里那条 MemberRef 写着 uint8，运行时字段是 int16，解析不上就是
        /// <c>MissingFieldException</c>。偏移一律走 <c>Marshal.OffsetOf</c>，加宽与否都对。
        /// </summary>
        internal static unsafe int StackOf(Cargo[] pool, int index)
        {
            if (pool == null || index < 0 || index >= pool.Length) return 0;

            GCHandle h = GCHandle.Alloc(pool, GCHandleType.Pinned);

            try
            {
                byte* c = (byte*)h.AddrOfPinnedObject() + (long)index * Stride;

                return StackIsWide ? *(short*)(c + OffStack) : *(c + OffStack);
            }
            finally
            {
                h.Free();
            }
        }

        // ── 本 mod 自己用的传送带 API：两套签名各绑一个 ────────

        private delegate bool InsertHeadWide(CargoPath path, int itemId, short stack, short inc);

        private delegate bool InsertHeadByte(CargoPath path, int itemId, byte stack, byte inc);

        private delegate int PickRearPathWide(CargoPath path, int[] needs, out int needIdx, out short stack, out short inc);

        private delegate int PickRearPathByte(CargoPath path, int[] needs, out int needIdx, out byte stack, out byte inc);

        private delegate int PickRearTrafficWide(CargoTraffic t, int beltId, int filter, int[] needs, out short stack, out short inc);

        private delegate int PickRearTrafficByte(CargoTraffic t, int beltId, int filter, int[] needs, out byte stack, out byte inc);

        private static readonly InsertHeadWide _insertWide;
        private static readonly InsertHeadByte _insertByte;
        private static readonly PickRearPathWide _pickPathWide;
        private static readonly PickRearPathByte _pickPathByte;
        private static readonly PickRearTrafficWide _pickTrafficWide;
        private static readonly PickRearTrafficByte _pickTrafficByte;

        static CargoWidening()
        {
            try
            {
                if (IsActive)
                {
                    _insertWide = Bind<InsertHeadWide>(typeof(CargoPath), "TryInsertItemAtHeadAndFillBlank");
                    _pickPathWide = Bind<PickRearPathWide>(typeof(CargoPath), "TryPickItemAtRear");
                    _pickTrafficWide = Bind<PickRearTrafficWide>(typeof(CargoTraffic), "TryPickItemAtRear");
                }
                else
                {
                    _insertByte = Bind<InsertHeadByte>(typeof(CargoPath), "TryInsertItemAtHeadAndFillBlank");
                    _pickPathByte = Bind<PickRearPathByte>(typeof(CargoPath), "TryPickItemAtRear");
                    _pickTrafficByte = Bind<PickRearTrafficByte>(typeof(CargoTraffic), "TryPickItemAtRear");
                }
            }
            catch (Exception e)
            {
                ProjectEdenPlugin.Log.LogError($"传送带 API 绑定失败，巨型建筑的传送带收发会停摆：{e.Message}");
            }
        }

        private static T Bind<T>(Type owner, string name) where T : Delegate
        {
            MethodInfo m = AccessTools.Method(owner, name);

            if (m == null) throw new MissingMethodException($"{owner.Name}.{name}");

            return AccessTools.MethodDelegate<T>(m);
        }

        internal static bool InsertAtHead(CargoPath path, int itemId, int stack, int inc) =>
            IsActive
                ? _insertWide != null && _insertWide(path, itemId, (short)stack, (short)inc)
                : _insertByte != null && _insertByte(path, itemId, Clamp(stack), Clamp(inc));

        private static byte Clamp(int v) => (byte)(v > 255 ? 255 : v < 0 ? 0 : v);

        internal static int PickAtRear(CargoPath path, int[] needs, out int needIdx, out int stack, out int inc)
        {
            if (IsActive && _pickPathWide != null)
            {
                int id = _pickPathWide(path, needs, out needIdx, out short ws, out short w);

                stack = ws;
                inc = w;

                return id;
            }

            if (!IsActive && _pickPathByte != null)
            {
                int id = _pickPathByte(path, needs, out needIdx, out byte bs, out byte b);

                stack = bs;
                inc = b;

                return id;
            }

            needIdx = 0;
            stack = 0;
            inc = 0;

            return 0;
        }

        internal static int PickAtRear(CargoTraffic traffic, int beltId, int filter, int[] needs, out int stack, out int inc)
        {
            if (IsActive && _pickTrafficWide != null)
            {
                int id = _pickTrafficWide(traffic, beltId, filter, needs, out short ws, out short w);

                stack = ws;
                inc = w;

                return id;
            }

            if (!IsActive && _pickTrafficByte != null)
            {
                int id = _pickTrafficByte(traffic, beltId, filter, needs, out byte bs, out byte b);

                stack = bs;
                inc = b;

                return id;
            }

            stack = 0;
            inc = 0;

            return 0;
        }

        // ── 渲染接管 ──────────────────────────────────────────

        /// <summary>
        /// 只在加宽生效时才挂。没加宽的话结构体还是 32 字节，原版上传本来就是对的。
        /// </summary>
        [HarmonyPatch]
        internal static class DrawPatch
        {
            private static bool Prepare() => IsActive;

            [HarmonyTranspiler]
            [HarmonyPatch(typeof(CargoContainer), nameof(CargoContainer.Draw))]
            private static IEnumerable<CodeInstruction> Draw_Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                MethodInfo setData = AccessTools.Method(
                    typeof(ComputeBuffer), nameof(ComputeBuffer.SetData),
                    new[] { typeof(Array), typeof(int), typeof(int), typeof(int) });

                MethodInfo repack = AccessTools.Method(typeof(CargoWidening), nameof(UploadRepacked));

                if (setData == null || repack == null)
                {
                    ProjectEdenPlugin.Log.LogError("解析不到 ComputeBuffer.SetData，货物渲染未接管——加宽后会因 stride 不匹配报错");

                    return instructions;
                }

                var code = new List<CodeInstruction>(instructions);

                var hit = 0;

                foreach (CodeInstruction ins in code)
                {
                    if (ins.opcode != OpCodes.Callvirt || !setData.Equals(ins.operand)) continue;

                    // 栈形状本来就是 [buffer, data, from, to, count]，
                    // 换成同样五个参数的静态方法即可，一条指令的事
                    ins.opcode = OpCodes.Call;
                    ins.operand = repack;

                    hit++;
                }

                if (hit == 0)
                    ProjectEdenPlugin.Log.LogError("CargoContainer.Draw 里没找到 SetData，货物渲染未接管");
                else
                    ProjectEdenPlugin.Log.LogInfo($"货物渲染已改为 32 字节重打包上传（{hit} 处），着色器看到的布局不变");

                return code;
            }
        }
    }
}
