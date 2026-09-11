using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace ProjectEden.Compatibility
{
    /// <summary>
    /// 银河尺度（GalacticScale 2）兼容：修掉它读档时会把存档判成「损坏」的那个洞。
    ///
    /// <b>它做了什么</b>：GS2 在 GameDesc.Import 的后置里，从<b>游戏本体的存档流</b>读两个字符串
    /// （版本号 + 一大段 JSON），解析失败就当作「这个存档里没有 GS2 数据」，
    /// 把流位置<b>还原回来</b>，改从存档旁边的 .gs2 边车文件加载星系。这条回退路径本身是好的。
    ///
    /// <b>洞在哪</b>：那两个 ReadString 前面<b>没有任何标记校验</b>。存档里那个位置要是没有
    /// GS2 数据（本机的存档一直都没有——日志里长期是「DSV Contained No GS2 Data」），
    /// 读到的就是随便什么字节：ReadString 先把它们当成 7 位压缩的长度前缀，
    /// 于是「字符串长度」是个随机数。运气好读出一坨乱码，解析失败，走回退，没事；
    /// 运气差长度超过文件剩余长度，直接 EndOfStreamException——
    /// 异常从 GameDesc.Import 一路穿到 GameSave.LoadCurrentGame，读档中止，
    /// 表现就是「存档损坏」。同一个存档换个字节分布就是能不能读的区别，纯看运气。
    ///
    /// <b>怎么修</b>：把那两个 ReadString 换成 <see cref="SafeReadString"/>——
    /// 先把长度前缀解出来，够不够读在分配之前就判掉，不够就还原位置返回空串。
    /// 空串解析必然失败，于是走的正是 GS2 自己那条「没有 GS2 数据」的回退路径：
    /// 位置由它自己还原，星系从 .gs2 边车文件加载。**没有改变它的任何行为，
    /// 只是把「抛异常」换成了它本来就处理得很好的「解析失败」。**
    ///
    /// 边车文件是权威数据，本机所有存档的 .gs2 逐字节相同，所以回退拿到的星系是对的。
    /// </summary>
    internal static class GalacticScaleCompat
    {
        internal const string MODGUID = "dsp.galactic-scale.2";

        internal static bool Installed { get; private set; }

        private static bool _guardFired;

        internal static void Init()
        {
            Installed = CompatibilityRegistry.IsLoaded(MODGUID);
        }

        /// <summary>要在 Harmony 建好之后调用：这是手动打到第三方程序集上的补丁。</summary>
        internal static void ApplyPatches(Harmony harmony)
        {
            if (!Installed || harmony == null) return;

            Type gs2 = AccessTools.TypeByName("GalacticScale.GS2");

            if (gs2 == null)
            {
                ProjectEdenPlugin.Log.LogWarning("检测到银河尺度，但找不到 GalacticScale.GS2 类型，读档保护未安装");

                return;
            }

            // CodeMatch 的老规矩：AccessTools 重载解析失败会安静地返回 null
            MethodInfo target = AccessTools.Method(gs2, "Import", new[] { typeof(BinaryReader), typeof(string) });

            if (target == null)
            {
                ProjectEdenPlugin.Log.LogWarning("检测到银河尺度，但 GS2.Import(BinaryReader, string) 的签名对不上，读档保护未安装");

                return;
            }

            try
            {
                harmony.Patch(target,
                    transpiler: new HarmonyMethod(AccessTools.Method(typeof(GalacticScaleCompat), nameof(Transpiler))));
            }
            catch (Exception e)
            {
                ProjectEdenPlugin.Log.LogError($"给银河尺度装读档保护失败，存档若报 EndOfStreamException 即为此因：{e}");
            }
        }

        /// <summary>把 GS2.Import 里那两个裸的 ReadString 换成带长度校验的版本。</summary>
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo readString = AccessTools.Method(typeof(BinaryReader), nameof(BinaryReader.ReadString));
            MethodInfo safeRead = AccessTools.Method(typeof(GalacticScaleCompat), nameof(SafeReadString));

            var code = new List<CodeInstruction>(instructions);
            var replaced = 0;

            foreach (CodeInstruction instruction in code)
            {
                if (!instruction.Calls(readString)) continue;

                // 原地改，不要换对象——换掉会丢跳转标签
                instruction.opcode = OpCodes.Call;
                instruction.operand = safeRead;
                replaced++;
            }

            if (replaced == 0)
                ProjectEdenPlugin.Log.LogError(
                    "银河尺度读档保护：GS2.Import 里没找到 BinaryReader.ReadString，补丁未生效（GS2 版本变了？）");
            else
                ProjectEdenPlugin.Log.LogInfo($"银河尺度读档保护已安装：GS2.Import 的 ReadString 接管 {replaced} 处");

            return code;
        }

        /// <summary>
        /// 读一个长度前缀字符串，读不动就把位置还原并返回空串，绝不抛异常。
        ///
        /// 长度前缀先自己解（和 BinaryReader.Read7BitEncodedInt 同一套编码），
        /// 这样「声称有 6 亿字节」这种垃圾长度在<b>分配内存之前</b>就被挡掉了——
        /// 直接调 ReadString 的话，垃圾长度不是 EndOfStream 就是 OutOfMemory。
        /// </summary>
        public static string SafeReadString(BinaryReader reader)
        {
            if (reader == null) return string.Empty;

            Stream stream = reader.BaseStream;
            long start = stream.Position;

            try
            {
                var length = 0;
                var shift = 0;

                while (true)
                {
                    // 7 位编码最多 5 个字节；再多就是垃圾
                    if (shift >= 35) return Rewind(stream, start);

                    if (stream.Position >= stream.Length) return Rewind(stream, start);

                    byte b = reader.ReadByte();

                    length |= (b & 0x7F) << shift;
                    shift += 7;

                    if ((b & 0x80) == 0) break;
                }

                if (length < 0 || stream.Position + length > stream.Length) return Rewind(stream, start);

                stream.Position = start;

                return reader.ReadString();
            }
            catch (Exception)
            {
                return Rewind(stream, start);
            }
        }

        private static string Rewind(Stream stream, long position)
        {
            if (!_guardFired)
            {
                _guardFired = true;

                ProjectEdenPlugin.Log.LogWarning(
                    "银河尺度在存档流里读到的不是 GS2 数据（长度前缀越界），已按它自己的「无 GS2 数据」流程处理：" +
                    "位置还原，星系改从 .gs2 边车文件加载。若不拦截，这里会抛 EndOfStreamException 导致读档失败。");
            }

            stream.Position = position;

            return string.Empty;
        }
    }
}
