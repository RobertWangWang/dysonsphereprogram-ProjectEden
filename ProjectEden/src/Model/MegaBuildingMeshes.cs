using ProjectEden.Utils;
using UnityEngine;
using S = ProjectEden.Model.BuildingTexture;

namespace ProjectEden.Model
{
    /// <summary>
    /// 九座巨型建筑各自的程序化几何、贴图，以及把它们换进 <see cref="PrefabDesc"/> 的那一步。
    ///
    /// <b>要解决的问题：五座建筑长得一模一样。</b> <c>megabuildings.json</c> 里
    /// <c>copyFromModelId</c> 是<b>一个全局设置</b>（49，物流运输站），五座克隆的是同一个模型，
    /// 彼此只靠 <c>tintR/G/B</c> 区分。染色改不了轮廓，这里换的是轮廓。
    ///
    /// <b>为什么只换网格，不从零搭 prefab。</b> <c>PrefabDesc.ReadPrefab</c> 一次性读出
    /// 占地、碰撞体、传送带接口（<c>SlotConfig.slotPoses</c>）、LOD 距离、材质……
    /// 自己搭一个 GameObject 全套重来，等于把五样已验证过的东西一起推倒。
    /// 现在是让原有流程照常建好 <c>PrefabDesc</c>，<b>只替换被画出来的那个 Mesh 和它的贴图</b>。
    /// </summary>
    internal static class MegaBuildingMeshes
    {
        private const float U = 1f;

        private static bool _shaderReported;

        // ══ 十座建筑的造型 ════════════════════════════════════

        // ── 天工装配厂：三层收口台座 + 中央塔柱 ──────────────
        private static void SkyAssembler(MeshKit k)
        {
            k.AddTaperedBox(new Vector3(0f, 0f, 0f), new Vector2(2f * U, 2f * U), new Vector2(1.7f * U, 1.7f * U),
                            0.4f * U, S.Concrete, S.PlateDark);
            k.AddTaperedBox(new Vector3(0f, 0.4f * U, 0f), new Vector2(1.62f * U, 1.62f * U), new Vector2(1.2f * U, 1.2f * U),
                            0.5f * U, S.PlateRivet, S.Grating);
            k.AddTaperedBox(new Vector3(0f, 0.9f * U, 0f), new Vector2(1.12f * U, 1.12f * U), new Vector2(0.78f * U, 0.78f * U),
                            0.45f * U, S.PlateLight, S.PlateDark);

            // 每层侧面一条发光带，夜里能认出这是活的
            k.AddBox(new Vector3(0f, 0.42f * U, 0f), new Vector3(1.66f * U, 0.08f * U, 1.66f * U), S.Glow);
            k.AddBox(new Vector3(0f, 0.92f * U, 0f), new Vector3(1.16f * U, 0.07f * U, 1.16f * U), S.Glow);

            k.AddCylinder(new Vector3(0f, 1.35f * U, 0f), 0.26f * U, 0.85f * U, 10, S.Pipe, S.PlateDark);
            k.AddRibs(new Vector3(0f, 1.35f * U, 0f), 0.27f * U, 0.85f * U, 6, 0.05f * U, S.PlateDark);
            k.AddCone(new Vector3(0f, 2.2f * U, 0f), 0.34f * U, 0.06f * U, 0.32f * U, 10, S.Accent);

            // 四角立柱 + 柱顶设备箱
            for (var i = 0; i < 4; i++)
            {
                float x = (i < 2 ? -1f : 1f) * 0.8f * U;
                float z = (i % 2 == 0 ? -1f : 1f) * 0.8f * U;

                k.AddBox(new Vector3(x, 0.62f * U, z), new Vector3(0.18f * U, 1.24f * U, 0.18f * U), S.PlateDark);
                k.AddBox(new Vector3(x, 1.28f * U, z), new Vector3(0.26f * U, 0.16f * U, 0.26f * U), S.Vent);
            }

            k.AddRailing(Vector3.zero, 0.58f * U, 0.58f * U, 1.35f * U, 0.16f * U, S.Grating);
        }

        // ── 冶铸熔炉：细高塔 + 环形散热鳍 ──────────────────
        private static void LysisTower(MeshKit k)
        {
            k.AddTaperedBox(new Vector3(0f, 0f, 0f), new Vector2(1.9f * U, 1.9f * U), new Vector2(1.45f * U, 1.45f * U),
                            0.3f * U, S.Concrete, S.Grating);

            k.AddCylinder(new Vector3(0f, 0.3f * U, 0f), 0.5f * U, 2.3f * U, 14, S.PlateLight, S.PlateDark);
            k.AddRibs(new Vector3(0f, 0.3f * U, 0f), 0.51f * U, 2.3f * U, 8, 0.06f * U, S.PlateDark);

            // 六道散热环，越往上越小
            for (var i = 0; i < 6; i++)
            {
                float y = 0.5f * U + i * 0.34f * U;
                float r = 0.78f * U - i * 0.05f * U;

                k.AddCylinder(new Vector3(0f, y, 0f), r, 0.1f * U, 14, S.Vent, S.PlateDark);
                k.AddCylinder(new Vector3(0f, y + 0.1f * U, 0f), r * 0.97f, 0.03f * U, 14, S.Glow);
            }

            k.AddCone(new Vector3(0f, 2.6f * U, 0f), 0.48f * U, 0.18f * U, 0.5f * U, 14, S.Accent);
            k.AddCylinder(new Vector3(0f, 3.1f * U, 0f), 0.07f * U, 0.28f * U, 6, S.Hazard);

            // 两根外挂竖管，带法兰
            for (var s = -1; s <= 1; s += 2)
            {
                var at = new Vector3(0.78f * U * s, 0.3f * U, 0.35f * U * s);

                k.AddCylinder(at, 0.1f * U, 2.1f * U, 8, S.Pipe);

                for (var i = 0; i < 4; i++)
                    k.AddCylinder(new Vector3(at.x, 0.55f * U + i * 0.5f * U, at.z), 0.14f * U, 0.06f * U, 8, S.PlateDark);
            }
        }

        // ── 化工厂：罐体群 + 横管 ────────────────────────────
        private static void ChemPlant(MeshKit k)
        {
            k.AddBox(new Vector3(0f, 0.15f * U, 0f), new Vector3(2.1f * U, 0.3f * U, 2.1f * U), S.Concrete, S.Grating);

            Tank(k, new Vector3(-0.6f * U, 0.3f * U, -0.5f * U), 0.42f * U, 1.25f * U);
            Tank(k, new Vector3(0.56f * U, 0.3f * U, -0.45f * U), 0.35f * U, 1.0f * U);
            Tank(k, new Vector3(0f, 0.3f * U, 0.62f * U), 0.33f * U, 0.8f * U);

            // 连接罐体的横管：化工厂的标志
            k.AddBox(new Vector3(-0.03f * U, 1.02f * U, -0.48f * U), new Vector3(1.2f * U, 0.1f * U, 0.1f * U), S.Pipe);
            k.AddBox(new Vector3(-0.3f * U, 0.72f * U, 0.06f * U), new Vector3(0.1f * U, 0.1f * U, 1.2f * U), S.Pipe);

            // 泵和阀门：小件最出细节
            k.AddGreebleRow(new Vector3(-0.85f * U, 0.38f * U, 0.75f * U), new Vector3(0.5f * U, 0.38f * U, 0.75f * U),
                            4, new Vector3(0.16f * U, 0.16f * U, 0.16f * U), S.Vent);
            k.AddGreebleRow(new Vector3(0.88f * U, 0.4f * U, -0.8f * U), new Vector3(0.88f * U, 0.4f * U, 0.3f * U),
                            3, new Vector3(0.14f * U, 0.2f * U, 0.14f * U), S.PlateDark);

            // 细烟囱 + 顶部警示环
            k.AddCylinder(new Vector3(0.86f * U, 0.3f * U, 0.72f * U), 0.12f * U, 1.9f * U, 8, S.Pipe);
            k.AddCylinder(new Vector3(0.86f * U, 2.0f * U, 0.72f * U), 0.14f * U, 0.16f * U, 8, S.Hazard);

            k.AddRailing(new Vector3(0f, 0f, 0f), 0.98f * U, 0.98f * U, 0.3f * U, 0.2f * U, S.Grating);
        }

        /// <summary>一只带封头、加强环和爬梯的立罐。三只共用，省得重复写。</summary>
        private static void Tank(MeshKit k, Vector3 at, float r, float h)
        {
            k.AddCylinder(at, r, h, 14, S.PlateRivet, S.PlateDark);
            k.AddCone(new Vector3(at.x, at.y + h, at.z), r, 0.1f * U, r * 0.55f, 14, S.PlateLight);

            // 两道加强环
            k.AddCylinder(new Vector3(at.x, at.y + h * 0.3f, at.z), r * 1.06f, 0.06f * U, 14, S.PlateDark);
            k.AddCylinder(new Vector3(at.x, at.y + h * 0.7f, at.z), r * 1.06f, 0.06f * U, 14, S.PlateDark);

            // 一道液位观察带
            k.AddBox(new Vector3(at.x, at.y + h * 0.5f, at.z + r), new Vector3(0.06f * U, h * 0.5f, 0.04f * U), S.Glow);

            // 爬梯
            k.AddBox(new Vector3(at.x - r, at.y + h * 0.5f, at.z), new Vector3(0.04f * U, h, 0.12f * U), S.Grating);
        }

        // ── 精密加工中心：低矮机身 + 龙门架 ──────────────────
        private static void PrecisionCenter(MeshKit k)
        {
            k.AddBox(new Vector3(0f, 0.28f * U, 0f), new Vector3(2.1f * U, 0.56f * U, 1.7f * U), S.PlateRivet, S.Grating);
            k.AddTaperedBox(new Vector3(0f, 0.56f * U, 0f), new Vector2(1.82f * U, 1.42f * U), new Vector2(1.5f * U, 1.12f * U),
                            0.34f * U, S.PlateLight, S.PlateDark);

            // 机身腰线发光带
            k.AddBox(new Vector3(0f, 0.56f * U, 0f), new Vector3(1.86f * U, 0.06f * U, 1.46f * U), S.Glow);

            // 龙门：两柱一梁
            k.AddBox(new Vector3(-0.84f * U, 0.95f * U, 0f), new Vector3(0.2f * U, 1.0f * U, 0.9f * U), S.PlateDark);
            k.AddBox(new Vector3(0.84f * U, 0.95f * U, 0f), new Vector3(0.2f * U, 1.0f * U, 0.9f * U), S.PlateDark);
            k.AddBox(new Vector3(0f, 1.5f * U, 0f), new Vector3(1.92f * U, 0.24f * U, 0.55f * U), S.Accent, S.Hazard);

            // 悬在梁下的主轴
            k.AddBox(new Vector3(0f, 1.26f * U, 0f), new Vector3(0.32f * U, 0.3f * U, 0.32f * U), S.Vent);
            k.AddCone(new Vector3(0f, 1.0f * U, 0f), 0.14f * U, 0.05f * U, 0.26f * U, 8, S.PlateDark);

            // 梁上的导轨滑块
            k.AddGreebleRow(new Vector3(-0.7f * U, 1.66f * U, 0f), new Vector3(0.7f * U, 1.66f * U, 0f),
                            5, new Vector3(0.14f * U, 0.1f * U, 0.4f * U), S.PlateDark);

            // 两侧料仓
            k.AddBox(new Vector3(-0.72f * U, 1.1f * U, 0.64f * U), new Vector3(0.4f * U, 0.55f * U, 0.3f * U), S.Vent);
            k.AddBox(new Vector3(0.72f * U, 1.1f * U, -0.64f * U), new Vector3(0.4f * U, 0.55f * U, 0.3f * U), S.Vent);
        }

        // ── 粒子加速器：环 + 四支撑 + 中央靶室 ────────────────
        private static void ParticleCollider(MeshKit k)
        {
            k.AddBox(new Vector3(0f, 0.13f * U, 0f), new Vector3(2.1f * U, 0.26f * U, 2.1f * U), S.Concrete, S.Grating);

            k.AddTorus(new Vector3(0f, 0.95f * U, 0f), 0.82f * U, 0.17f * U, 32, 8, S.PlateLight);
            // 内侧一圈细环，做出「束流管」的层次
            k.AddTorus(new Vector3(0f, 0.95f * U, 0f), 0.82f * U, 0.09f * U, 32, 6, S.Glow);

            // 环上均布的磁铁组
            for (var i = 0; i < 8; i++)
            {
                float a = Mathf.PI * 2f * i / 8f;

                k.AddBox(new Vector3(Mathf.Cos(a) * 0.82f * U, 0.95f * U, Mathf.Sin(a) * 0.82f * U),
                         new Vector3(0.26f * U, 0.3f * U, 0.26f * U), S.PlateDark, S.Accent);
            }

            // 四根支撑
            for (var i = 0; i < 4; i++)
            {
                float a = Mathf.PI * 0.5f * i + Mathf.PI * 0.25f;

                k.AddBox(new Vector3(Mathf.Cos(a) * 0.82f * U, 0.54f * U, Mathf.Sin(a) * 0.82f * U),
                         new Vector3(0.16f * U, 0.82f * U, 0.16f * U), S.PlateRivet);
            }

            // 中央靶室
            k.AddCylinder(new Vector3(0f, 0.26f * U, 0f), 0.3f * U, 0.8f * U, 12, S.PlateRivet, S.PlateDark);
            k.AddRibs(new Vector3(0f, 0.26f * U, 0f), 0.31f * U, 0.8f * U, 6, 0.05f * U, S.PlateDark);
            k.AddCone(new Vector3(0f, 1.06f * U, 0f), 0.36f * U, 0.12f * U, 0.44f * U, 12, S.Accent);

            // 两段注入管
            k.AddBox(new Vector3(0f, 0.95f * U, 0f), new Vector3(1.5f * U, 0.1f * U, 0.1f * U), S.Pipe);
            k.AddBox(new Vector3(0f, 0.95f * U, 0f), new Vector3(0.1f * U, 0.1f * U, 1.5f * U), S.Pipe);
        }

        // ── 熔岩冷却厂：敞开的发光熔池 + 四座翼片冷却塔 + 粒化环 ──
        //
        // 和生物温室一样，三组形体各对应一件事，不是随手堆的：
        //   敞开的熔池  = 进料，也是全身唯一把「热」摆在外面的地方
        //   池面上的粒化环 = 冷却速率，三条配方的区别就在这里
        //   四座冷却塔   = 热最后去了哪里
        // 九座巨型建筑里只有它在外表露出发光面，远处看过去就靠这一点认。
        private static void LavaCooler(MeshKit k)
        {
            k.AddBox(new Vector3(0f, 0.13f * U, 0f), new Vector3(2.1f * U, 0.26f * U, 2.1f * U), S.Concrete, S.Grating);

            const float poolR = 0.68f * U;

            k.AddCylinder(new Vector3(0f, 0.26f * U, 0f), poolR, 0.30f * U, 20, S.PlateRivet, S.PlateDark);
            k.AddRibs(new Vector3(0f, 0.26f * U, 0f), poolR + 0.01f * U, 0.30f * U, 12, 0.06f * U, S.PlateDark);

            k.AddCylinder(new Vector3(0f, 0.54f * U, 0f), poolR - 0.08f * U, 0.03f * U, 20, S.Glow);

            k.AddTorus(new Vector3(0f, 0.86f * U, 0f), poolR - 0.12f * U, 0.07f * U, 24, 6, S.Pipe);
            k.AddTorus(new Vector3(0f, 0.86f * U, 0f), poolR - 0.12f * U, 0.035f * U, 24, 5, S.Accent);

            for (var sx = -1; sx <= 1; sx += 2)
            for (var sz = -1; sz <= 1; sz += 2)
            {
                var at = new Vector3(0.72f * U * sx, 0.26f * U, 0.72f * U * sz);

                k.AddCone(at, 0.26f * U, 0.19f * U, 0.92f * U, 12, S.Vent, S.PlateDark);
                k.AddRibs(at, 0.27f * U, 0.92f * U, 8, 0.045f * U, S.PlateDark);
                k.AddCone(new Vector3(at.x, at.y + 0.92f * U, at.z), 0.22f * U, 0.26f * U, 0.10f * U, 12, S.PlateLight);

                k.AddBox(new Vector3(at.x * 0.5f, 0.40f * U, at.z),
                         new Vector3(Mathf.Abs(at.x), 0.09f * U, 0.09f * U), S.Pipe);
            }

            k.AddBox(new Vector3(0f, 0.42f * U, 0.88f * U),
                     new Vector3(0.24f * U, 0.12f * U, 0.34f * U), S.PlateDark, S.Grating);

            k.AddGreebleRow(new Vector3(-0.20f * U, 0.30f * U, 1.00f * U), new Vector3(0.20f * U, 0.30f * U, 1.00f * U),
                            3, new Vector3(0.13f * U, 0.14f * U, 0.13f * U), S.Vent);

            k.AddRailing(Vector3.zero, 0.95f * U, 0.95f * U, 0.30f * U, 0.20f * U, S.Hazard);
        }

        // ── 催化反应器：提升管 + 再生器 + 两条来回的输送管 ────
        //
        // 造型的母题是**那个回路**，不是塔。真实的 FCC 装置一眼能认出来靠的就是
        // 「一细一粗两个容器被两条管接成一个环」——催化剂在里面转圈：反应器里结焦、
        // 送去再生器烧掉、再送回来。这也正好是这座建筑的机制本身，所以造型不是装饰，
        // 它就是在说明这台机器怎么工作。
        //
        // <b>两条输送管刻意一高一低、一后一前（z 差开）。</b> 摆成平行的两根会读成
        // 一副梯子；错开之后才读得出「去」和「回」是两个方向。图标那张也是这么画的。
        //
        // 顶上三只旋风分离器是再生器最好认的特征，也是 80px 图标里唯一能把它和
        // 燔石化工厂那几只普通立罐分开的东西——模型和图标在这一点上必须一致。
        /// <summary>
        /// 综合化学厂：一个明显更宽的底座，上面架着<b>三只形制各不相同</b>的反应单元，
        /// 再由一条共用的进出料横管把它们串起来。
        ///
        /// <b>母题是「三种不相容的反应被塞进同一个壳子」，不是「更大的化工厂」。</b>
        /// 燔石化工厂的模型是三只一模一样的立罐（同一种反应做三遍）；这一座的三只
        /// 必须一眼分得出是三种东西，否则两座在地上就分不开：
        ///
        ///   · 带两根电极的方槽 —— 电化学
        ///   · 细高的精馏塔     —— 化学
        ///   · 矮胖的圆顶罐     —— 氧化还原
        ///
        /// 三只共用一个底座、再被一条横管连起来，「并进一个壳子」才读得出来——
        /// 分开摆就是三座小厂，而那正好是这台机器想取代的东西。
        /// 形制与 <c>tools/make_icons.py</c> 的 <c>omni_chem()</c> 一一对应。
        /// </summary>
        private static void OmniChemPlant(MeshKit k)
        {
            // ── 底座：两层，上层略窄，边缘那圈台阶让它读得出是「壳体」而不是一块板 ──
            k.AddBox(new Vector3(0f, 0.12f * U, 0f), new Vector3(2.3f * U, 0.24f * U, 2.3f * U),
                     S.Concrete, S.Grating);
            k.AddBox(new Vector3(0f, 0.32f * U, 0f), new Vector3(2.0f * U, 0.16f * U, 2.0f * U),
                     S.PlateDark, S.PlateLight);

            const float deck = 0.40f * U;

            // ── 左：电解槽。方的，插两根不等高的电极 ──
            var cell = new Vector3(-0.62f * U, deck, 0.10f * U);

            k.AddBox(new Vector3(cell.x, cell.y + 0.30f * U, cell.z),
                     new Vector3(0.70f * U, 0.60f * U, 0.70f * U), S.PlateRivet, S.PlateLight);

            // 电极不等高：等高就读成栏杆了
            float[] rods = { 0.46f * U, 0.34f * U };

            for (var i = 0; i < rods.Length; i++)
            {
                float x = cell.x + (i == 0 ? -0.20f : 0.20f) * U;

                k.AddCylinder(new Vector3(x, cell.y + 0.60f * U, cell.z), 0.045f * U, rods[i], 8, S.Pipe);
                k.AddCylinder(new Vector3(x, cell.y + 0.60f * U + rods[i], cell.z),
                              0.085f * U, 0.05f * U, 10, S.PlateLight, S.Glow);
            }

            // ── 右：氧化还原罐。矮胖，顶上扣一个圆顶 ──
            var redox = new Vector3(0.60f * U, deck, -0.14f * U);
            const float redoxR = 0.46f * U;
            const float redoxH = 0.56f * U;

            k.AddCylinder(redox, redoxR, redoxH, 20, S.PlateLight, S.PlateDark);
            k.AddRibs(redox, redoxR + 0.01f * U, redoxH, 10, 0.045f * U, S.PlateDark);
            // 圆顶用一截收口的锥体：**半径收到七成就够**，收得太少会顶出一朵蘑菇
            k.AddCone(new Vector3(redox.x, redox.y + redoxH, redox.z),
                      redoxR, redoxR * 0.34f, 0.22f * U, 20, S.PlateLight, S.Accent);

            // ── 中前：精馏塔。**必须比横管高**，前后关系才立得住 ──
            var column = new Vector3(0f, deck, 0.52f * U);
            const float colR = 0.32f * U;
            const float colH = 1.34f * U;

            k.AddCylinder(column, colR, colH, 18, S.PlateRivet, S.PlateDark);
            // 塔盘：精馏塔的识别点，隔一段一道箍
            for (var i = 1; i <= 4; i++)
                k.AddTorus(new Vector3(column.x, column.y + colH * (0.18f * i + 0.10f), column.z),
                           colR + 0.015f * U, 0.035f * U, 20, 6, S.Accent);

            k.AddCylinder(new Vector3(column.x, column.y + colH, column.z),
                          colR * 1.18f, 0.10f * U, 18, S.PlateLight, S.Glow);

            // ── 共用横管：走在电解槽与氧化还原罐之间，从精馏塔背后穿过 ──
            const float pipeY = 1.02f * U;

            k.AddCylinder(new Vector3(cell.x, cell.y + 0.60f * U, cell.z),
                          0.07f * U, pipeY - 0.60f * U, 10, S.Pipe);
            k.AddCylinder(new Vector3(redox.x, redox.y + redoxH, redox.z),
                          0.07f * U, pipeY - redoxH, 10, S.Pipe);
            k.AddGreebleRow(new Vector3(cell.x, deck + pipeY, cell.z),
                            new Vector3(redox.x, deck + pipeY, redox.z),
                            7, new Vector3(0.16f * U, 0.13f * U, 0.13f * U), S.Pipe);

            k.AddRailing(new Vector3(0f, 0f, 0f), 0.95f * U, 0.95f * U, 0.40f * U, 0.16f * U, S.Grating);
        }

        /// <summary>
        /// 氧化还原燃烧厂：<b>一排压机 + 一座矮胖的燃烧筒 + 一根排气塔 + 一间汽机房</b>。
        ///
        /// 造型要回答的问题是「凭什么一眼看出这是电厂而不是化工厂」。前九座里已经有了
        /// 精馏塔（综合化学厂）、催化剂床（催化反应器）、对撞环（观微对撞机）和温室
        /// （生物温室），所以这一座刻意<b>一根竖直塔柱都不放在中间</b>：
        /// 主体是横躺的汽机房，竖直元素只有边上那根细排气塔，加上一排低矮的压机——
        /// 宽而扁的剪影，和精馏塔顶着的综合化学厂（0.56/1.50）正好相反。
        ///
        /// 三处发光带分别落在压机的模腔口、燃烧筒的腰线和排气塔的根部，
        /// 于是「压 → 烧 → 排」这条工艺顺序在剪影上是读得出来的。
        /// </summary>
        private static void RedoxBurner(MeshKit k)
        {
            // ── 底座：两层。上层收窄，边缘那圈台阶让它读得出是壳体而不是一块板 ──
            k.AddBox(new Vector3(0f, 0.11f * U, 0f), new Vector3(2.4f * U, 0.22f * U, 2.4f * U),
                     S.Concrete, S.Grating);
            k.AddBox(new Vector3(0f, 0.30f * U, 0f), new Vector3(2.1f * U, 0.16f * U, 2.1f * U),
                     S.PlateDark, S.PlateLight);

            const float deck = 0.38f * U;

            // ── 后排：汽机房。长条低箱，侧面百叶——发电设备要散热 ──
            var hall = new Vector3(-0.10f * U, deck, -0.66f * U);
            var hallSize = new Vector3(1.86f * U, 0.52f * U, 0.62f * U);

            k.AddBox(new Vector3(hall.x, hall.y + hallSize.y * 0.5f, hall.z), hallSize,
                     S.Vent, S.PlateLight);
            // 屋脊：一道窄一号的盖板，免得长箱子读成一块砖
            k.AddBox(new Vector3(hall.x, hall.y + hallSize.y + 0.06f * U, hall.z),
                     new Vector3(hallSize.x * 0.88f, 0.12f * U, hallSize.z * 0.7f),
                     S.PlateRivet, S.PlateDark);

            // ── 前排：四台压机。矮圆筒 + 顶上的活塞杆，模腔口发光 ──
            const float pressZ = 0.60f * U;
            const float pressR = 0.17f * U;
            const float pressH = 0.30f * U;

            for (var i = 0; i < 4; i++)
            {
                float x = (-0.75f + 0.50f * i) * U;
                var at = new Vector3(x, deck, pressZ);

                k.AddCylinder(at, pressR, pressH, 12, S.PlateRivet, S.PlateDark);
                // 模腔口：压机真正在做事的地方
                k.AddCylinder(new Vector3(x, deck + pressH, pressZ), pressR * 0.62f, 0.04f * U, 12,
                              S.Glow, S.Glow);

                // 活塞杆高度交错，一排等高会读成栏杆
                float rod = (i % 2 == 0 ? 0.34f : 0.26f) * U;

                k.AddCylinder(new Vector3(x, deck + pressH + 0.04f * U, pressZ), 0.05f * U, rod, 8, S.Pipe);
                k.AddBox(new Vector3(x, deck + pressH + 0.04f * U + rod + 0.05f * U, pressZ),
                         new Vector3(0.20f * U, 0.10f * U, 0.20f * U), S.PlateLight, S.Accent);
            }

            // ── 右中：燃烧筒。矮、胖、多箍，顶上一个扁圆顶 ──
            var drum = new Vector3(0.74f * U, deck, -0.02f * U);
            const float drumR = 0.50f * U;
            const float drumH = 0.64f * U;

            k.AddCylinder(drum, drumR, drumH, 22, S.PlateLight, S.PlateDark);
            // 加强箍：承压容器的识别点
            k.AddRibs(drum, drumR + 0.012f * U, drumH, 9, 0.05f * U, S.PlateDark);
            // 腰线：火焰透出来的那一圈
            k.AddTorus(new Vector3(drum.x, drum.y + drumH * 0.42f, drum.z),
                       drumR + 0.025f * U, 0.045f * U, 22, 6, S.Glow);
            // 扁圆顶：半径只收到六成，收太狠会顶出一朵蘑菇（综合化学厂那次的教训）
            k.AddCone(new Vector3(drum.x, drum.y + drumH, drum.z),
                      drumR, drumR * 0.60f, 0.18f * U, 22, S.PlateLight, S.Accent);

            // ── 右后：排气塔。全场唯一的竖直细长件，根部一圈亮带 ──
            var stack = new Vector3(0.74f * U, deck, -0.86f * U);
            const float stackR = 0.15f * U;
            const float stackH = 1.28f * U;

            k.AddCylinder(stack, stackR * 1.5f, 0.16f * U, 14, S.Concrete, S.PlateDark);
            k.AddCylinder(new Vector3(stack.x, stack.y + 0.16f * U, stack.z), stackR, stackH, 14,
                          S.PlateRivet, S.PlateDark);
            k.AddTorus(new Vector3(stack.x, stack.y + 0.30f * U, stack.z),
                       stackR + 0.02f * U, 0.035f * U, 16, 6, S.Glow);
            // 喇叭口：顶端外扩，读得出是排气而不是一根柱子
            k.AddCone(new Vector3(stack.x, stack.y + 0.16f * U + stackH, stack.z),
                      stackR, stackR * 1.55f, 0.20f * U, 14, S.PlateLight, S.Hazard);

            // ── 工艺管路：压机 → 燃烧筒 → 汽机房 ──────────────
            const float pipeY = 0.92f * U;

            k.AddGreebleRow(new Vector3(-0.75f * U, deck + pipeY, pressZ),
                            new Vector3(drum.x, deck + pipeY, pressZ),
                            8, new Vector3(0.15f * U, 0.12f * U, 0.12f * U), S.Pipe);

            k.AddCylinder(new Vector3(drum.x, drum.y + drumH, drum.z), 0.075f * U,
                          pipeY - drumH + 0.06f * U, 10, S.Pipe);

            // 燃烧筒 → 汽机房：蒸汽管，这一根解释了热从哪去到哪
            k.AddGreebleRow(new Vector3(drum.x, deck + 0.62f * U, drum.z),
                            new Vector3(hall.x + hallSize.x * 0.35f, deck + 0.62f * U, hall.z),
                            6, new Vector3(0.14f * U, 0.14f * U, 0.14f * U), S.Pipe);

            k.AddRailing(new Vector3(0f, 0f, 0f), 1.0f * U, 1.0f * U, 0.38f * U, 0.15f * U, S.Grating);
        }

        private static void CatalyticReactor(MeshKit k)
        {
            k.AddBox(new Vector3(0f, 0.13f * U, 0f), new Vector3(2.1f * U, 0.26f * U, 2.1f * U), S.Concrete, S.Grating);

            // ── 再生器：粗矮的那个，右边 ──
            var regen = new Vector3(0.42f * U, 0.26f * U, 0f);
            const float regenR = 0.50f * U;
            const float regenH = 0.78f * U;

            k.AddCylinder(regen, regenR, regenH, 20, S.PlateRivet, S.PlateDark);
            k.AddRibs(regen, regenR + 0.01f * U, regenH, 12, 0.05f * U, S.PlateDark);

            // 腰带：耐火衬里的那道箍。图标上也有，两边要对得上
            k.AddTorus(new Vector3(regen.x, regen.y + regenH * 0.55f, regen.z),
                       regenR + 0.015f * U, 0.045f * U, 24, 6, S.Accent);

            // ── 旋风分离器：三只，锥口朝上坐在罐顶 ──
            float top = regen.y + regenH;

            foreach (var off in new[]
                     {
                         new Vector3(0.26f * U, 0f, 0f),
                         new Vector3(-0.13f * U, 0f, 0.23f * U),
                         new Vector3(-0.13f * U, 0f, -0.23f * U),
                     })
            {
                var at = new Vector3(regen.x + off.x, top, regen.z + off.z);

                k.AddCone(at, 0.06f * U, 0.17f * U, 0.30f * U, 12, S.Vent, S.PlateDark);
                k.AddCylinder(new Vector3(at.x, at.y + 0.30f * U, at.z), 0.17f * U, 0.06f * U, 12, S.PlateLight);
            }

            // ── 提升管：细高的那个，左边。比再生器高出一截，回路才有落差 ──
            var riser = new Vector3(-0.62f * U, 0.26f * U, 0f);
            const float riserR = 0.19f * U;
            const float riserH = 1.30f * U;

            k.AddCylinder(riser, riserR, riserH, 14, S.PlateLight, S.PlateDark);
            k.AddRibs(riser, riserR + 0.01f * U, riserH, 6, 0.035f * U, S.PlateDark);

            // 爬梯：横杠一路上去。管子上有梯子，尺度感才出来
            k.AddGreebleRow(new Vector3(riser.x, riser.y + 0.20f * U, riser.z + riserR),
                            new Vector3(riser.x, riser.y + riserH - 0.12f * U, riser.z + riserR),
                            7, new Vector3(0.16f * U, 0.03f * U, 0.04f * U), S.Hazard);

            // ── 上行管：提升管顶 → 横过去 → 落进再生器（走后侧） ──
            k.AddBox(new Vector3((riser.x + 0f) * 0.5f, riser.y + riserH - 0.10f * U, -0.20f * U),
                     new Vector3(Mathf.Abs(riser.x), 0.12f * U, 0.12f * U), S.Pipe);
            k.AddBox(new Vector3(0f, riser.y + riserH * 0.72f, -0.20f * U),
                     new Vector3(0.12f * U, riserH * 0.56f, 0.12f * U), S.Pipe);

            // ── 下行管：再生器底 → 横回来 → 接进提升管底（走前侧） ──
            k.AddBox(new Vector3(-0.26f * U, 0.42f * U, 0.30f * U),
                     new Vector3(0.76f * U, 0.11f * U, 0.11f * U), S.Pipe);
            k.AddBox(new Vector3(0.10f * U, 0.38f * U, 0.30f * U),
                     new Vector3(0.11f * U, 0.24f * U, 0.11f * U), S.Pipe);

            // 控制间 + 通风口：和别的几座一样，放在正面把体量坐实
            k.AddBox(new Vector3(-0.05f * U, 0.40f * U, 0.86f * U),
                     new Vector3(0.30f * U, 0.16f * U, 0.30f * U), S.PlateDark, S.Grating);
            k.AddGreebleRow(new Vector3(0.45f * U, 0.32f * U, 0.98f * U),
                            new Vector3(0.85f * U, 0.32f * U, 0.98f * U),
                            3, new Vector3(0.12f * U, 0.13f * U, 0.12f * U), S.Vent);

            k.AddRailing(Vector3.zero, 0.95f * U, 0.95f * U, 0.30f * U, 0.20f * U, S.Hazard);
        }

        // ── 生物温室：穹顶温室 + 外挂培养罐 ──────────────────
        //
        // 造型的三件事都对应三条配方，不是随手堆的：
        //   穹顶（发光玻璃面）  = 光合育林，也是它唯一需要晒到太阳的部分
        //   两只外挂立罐        = 藻菌共培养，罐子里不看天
        //   底座上的泵与管      = 溶剂萃取
        // 穹顶用六层圆台逼近半球——网格里没有球，而圆台堆叠出来的分面效果
        // 恰好像玻璃幕墙的分格，比真球面更像温室。
        private static void BioGreenhouse(MeshKit k)
        {
            k.AddBox(new Vector3(0f, 0.16f * U, 0f), new Vector3(2.1f * U, 0.32f * U, 2.1f * U), S.Concrete, S.Grating);

            // 种植床：抬起来一层，穹顶落在它上面
            k.AddBox(new Vector3(0f, 0.36f * U, 0f), new Vector3(1.74f * U, 0.14f * U, 1.74f * U), S.PlateDark, S.Grating);

            const int levels = 6;
            const float radius = 0.95f * U;
            const float domeBase = 0.43f * U;
            const float domeHeight = 1.05f * U;

            for (var i = 0; i < levels; i++)
            {
                float a0 = Mathf.PI * 0.5f * i / levels;
                float a1 = Mathf.PI * 0.5f * (i + 1) / levels;

                float r0 = Mathf.Cos(a0) * radius;
                float r1 = Mathf.Cos(a1) * radius;
                float y0 = domeBase + Mathf.Sin(a0) * domeHeight;
                float y1 = domeBase + Mathf.Sin(a1) * domeHeight;

                k.AddCone(new Vector3(0f, y0, 0f), r0, r1, y1 - y0, 14, S.Glow);

                // 每层底沿套一圈深色框。没有它，穹顶是一坨发光体而不是骨架撑起的玻璃房
                k.AddCylinder(new Vector3(0f, y0, 0f), r0 * 1.03f, 0.05f * U, 14, S.PlateDark);
            }

            // 顶部通风塔：温室要排热，这是它区别于「圆顶基地」的地方
            float top = domeBase + domeHeight;

            k.AddCylinder(new Vector3(0f, top - 0.02f * U, 0f), 0.17f * U, 0.24f * U, 10, S.Vent, S.PlateDark);
            k.AddCone(new Vector3(0f, top + 0.22f * U, 0f), 0.22f * U, 0.05f * U, 0.16f * U, 10, S.Accent);

            // 两只培养罐，对角挂在穹顶外侧
            for (var s = -1; s <= 1; s += 2)
            {
                var at = new Vector3(0.82f * U * s, 0.32f * U, -0.76f * U * s);

                k.AddCylinder(at, 0.24f * U, 0.92f * U, 12, S.PlateRivet, S.PlateDark);
                k.AddRibs(at, 0.25f * U, 0.92f * U, 6, 0.045f * U, S.PlateDark);
                k.AddCone(new Vector3(at.x, at.y + 0.92f * U, at.z), 0.24f * U, 0.09f * U, 0.13f * U, 12, S.PlateLight);

                // 液位观察带：罐里是绿的藻液，贴图上只能靠发光面示意
                k.AddBox(new Vector3(at.x, at.y + 0.46f * U, at.z + 0.24f * U),
                         new Vector3(0.07f * U, 0.56f * U, 0.04f * U), S.Glow);

                // 罐顶接回穹顶的横管，沿 X 走（AddBox 是轴对齐的，斜着接会穿帮）
                k.AddBox(new Vector3(at.x * 0.5f, at.y + 0.86f * U, at.z),
                         new Vector3(Mathf.Abs(at.x), 0.08f * U, 0.08f * U), S.Pipe);
            }

            // 走道 + 泵组：萃取那一段的设备
            k.AddRailing(Vector3.zero, 1.0f * U, 1.0f * U, 0.32f * U, 0.2f * U, S.Grating);

            k.AddGreebleRow(new Vector3(-0.86f * U, 0.42f * U, 0.92f * U), new Vector3(0.86f * U, 0.42f * U, 0.92f * U),
                            4, new Vector3(0.17f * U, 0.18f * U, 0.17f * U), S.Vent);

            k.AddGreebleRow(new Vector3(-0.92f * U, 0.42f * U, -0.86f * U), new Vector3(-0.92f * U, 0.42f * U, 0.4f * U),
                            3, new Vector3(0.14f * U, 0.22f * U, 0.14f * U), S.PlateDark);
        }

        // ══ 换进 PrefabDesc ═══════════════════════════════════

        /// <summary>
        /// 把生成的几何和贴图装进已经建好的 <paramref name="desc"/>。
        ///
        /// 网格三件事：<c>lodMeshes</c>（世界里真正被画的）、<c>mesh</c>/<c>meshes</c>
        /// （建造预览、蓝图、拆除高亮读这两个）、<c>lodVertas</c> 的<b>元素</b>置空
        /// （那是按原版顶点数烘焙的动画缓冲，几何换了就对不上；<b>整个数组不能置空</b>，
        /// <c>ObjectRenderer.Init</c> 会去索引它）。
        ///
        /// 碰撞体、占地、传送带接口一概不动。
        /// </summary>
        /// <summary>
        /// 同位提纯厂：<b>一排电解槽 + 一根竖直的区域熔炼杆，杆腰上一圈发亮的熔区</b>。
        ///
        /// 前十座的剪影已经占掉了细高精馏塔（燔石化工厂、综合化学厂）、圆顶罐、对撞环、
        /// 温室和矮胖燃烧筒，所以这一座的识别点<b>不放在胖瘦上，放在那圈熔区</b>——
        /// 整套建筑里没有第二个会发光的环。
        ///
        /// 电解槽刻意做成<b>方槽而不是圆罐</b>：圆的这套里太多了，方的一眼分得开；
        /// 槽口那一圈亮面是电解液，也是「湿法」这条线的颜色。
        ///
        /// 三台槽的高度是错开的。等高会读成一段栏杆——和燃烧筒那排压机同一个教训。
        /// </summary>
        /// <summary>
        /// 奇点储能厂：**三条弧形扶壁向内斜撑，在半空把一根电浆柱箍住；底层一排弹匣架**。
        ///
        /// <b>母题是「捏住」，不是「托住」。</b> 已有的十一座里已经有细塔（同位提纯厂）、
        /// 精馏柱群（燔石化工厂）、对撞环（观微对撞机）和矮方块，所以这一座必须靠
        /// <b>向内倾斜</b>这件事本身立住——扶壁如果是竖直的，它就退化成又一座塔。
        ///
        /// 三道发光箍越往上越紧，画的是磁约束的收缩点；而底层那排横置弹匣架是**唯一**
        /// 说明它在装卸货而不是在反应的元素——没有它，这座建筑看着像个反应堆。
        ///
        /// 发光只出现在箍和柱心，外壳一律冷灰：这座建筑的颜色标识交给 tint
        /// （图标也从同一个 tint 算，所以两边天生同步）。
        /// </summary>
        private static void SingularityVault(MeshKit k)
        {
            // ── 底座：一层宽台，一层收窄 ──
            k.AddBox(new Vector3(0f, 0.10f * U, 0f), new Vector3(2.5f * U, 0.20f * U, 2.5f * U),
                     S.Concrete, S.Grating);
            k.AddBox(new Vector3(0f, 0.28f * U, 0f), new Vector3(1.9f * U, 0.16f * U, 1.9f * U),
                     S.PlateDark, S.PlateLight);

            const float deck = 0.36f * U;
            const float colH = 1.70f * U;
            const float colR = 0.17f * U;

            // ── 中央电浆柱：石英外管 + 里面更细的芯 ──
            k.AddCylinder(new Vector3(0f, deck, 0f), colR, colH, 16, S.Pipe);
            k.AddCylinder(new Vector3(0f, deck + 0.04f * U, 0f), colR * 0.52f, colH * 0.93f, 12, S.Glow);

            // 三道磁箍：**越往上越紧**，收缩点画在这里。等距会读成装饰，递减才读得出「在捏」
            for (var i = 0; i < 3; i++)
            {
                float t = 0.30f + 0.26f * i;
                float squeeze = 1.95f - 0.45f * i;

                k.AddTorus(new Vector3(0f, deck + colH * t, 0f),
                           colR * squeeze, 0.055f * U, 16, 8, S.Glow);
                k.AddCylinder(new Vector3(0f, deck + colH * t - 0.05f * U, 0f),
                              colR * (squeeze + 0.25f), 0.10f * U, 16, S.Accent, S.PlateDark);
            }

            // 柱顶封头
            k.AddCylinder(new Vector3(0f, deck + colH, 0f), colR * 1.5f, 0.14f * U, 16,
                          S.PlateRivet, S.PlateLight);

            // ── 三条扶壁：从底座外缘斜撑到柱顶附近，**向内倾** ──
            // 用一串逐渐内移、逐渐抬高的短箱拼出弧线；直接一根斜柱会读成脚手架
            for (var leg = 0; leg < 3; leg++)
            {
                double a = System.Math.PI * 2.0 * leg / 3.0 + 0.5;
                float cx = (float)System.Math.Cos(a);
                float cz = (float)System.Math.Sin(a);

                for (var s = 0; s < 5; s++)
                {
                    float t = s / 4f;
                    // 半径按二次曲线收进来：底下张得开、越往上越贴柱子
                    float r = (0.95f - 0.62f * t * t) * U;
                    float y = deck + colH * (0.06f + 0.86f * t);
                    float thick = (0.20f - 0.05f * t) * U;

                    k.AddBox(new Vector3(cx * r, y, cz * r),
                             new Vector3(thick, 0.34f * U, thick),
                             s == 4 ? S.Accent : S.PlateLight, S.PlateDark);
                }
            }

            // ── 底层弹匣架：横置的柜位，**这是唯一说明它在装卸货的元素** ──
            for (var side = 0; side < 2; side++)
            {
                float z = side == 0 ? 0.80f * U : -0.80f * U;

                k.AddBox(new Vector3(0f, deck + 0.20f * U, z),
                         new Vector3(1.5f * U, 0.40f * U, 0.28f * U), S.PlateDark, S.PlateRivet);

                // 三个柜位：留缝，才看得出是一格一格的
                for (var slot = 0; slot < 3; slot++)
                    k.AddBox(new Vector3((-0.46f + 0.46f * slot) * U, deck + 0.20f * U, z),
                             new Vector3(0.34f * U, 0.26f * U, 0.34f * U), S.Glow, S.PlateDark);
            }
        }

        private static void RefineryPlant(MeshKit k)
        {
            // ── 底座：两层，上层收窄留出台阶 ──
            k.AddBox(new Vector3(0f, 0.11f * U, 0f), new Vector3(2.4f * U, 0.22f * U, 2.4f * U),
                     S.Concrete, S.Grating);
            k.AddBox(new Vector3(0f, 0.30f * U, 0f), new Vector3(2.0f * U, 0.16f * U, 2.0f * U),
                     S.PlateDark, S.PlateLight);

            const float deck = 0.38f * U;

            // ── 右后：区域熔炼杆。全场最细最高的一件，它就是这座建筑的记号 ──
            var rod = new Vector3(0.70f * U, deck, -0.30f * U);
            const float rodR = 0.13f * U;
            const float rodH = 1.55f * U;

            // 外壳（石英管）比芯杆粗一圈：熔区要看得出是「套在杆外面走」的
            k.AddCylinder(rod, rodR * 1.55f, rodH, 14, S.Pipe);
            k.AddCylinder(rod, rodR, rodH * 0.98f, 12, S.PlateLight, S.PlateRivet);

            // 熔区：杆腰上那一圈。做成扁圆环而不是一段亮筒——
            // 亮筒会读成「这根杆本身在发光」，环才读得出「有一圈东西正沿着它走」
            k.AddTorus(new Vector3(rod.x, deck + rodH * 0.46f, rod.z),
                       rodR * 1.75f, 0.055f * U, 14, 8, S.Glow);
            // 感应线圈的机壳，压在熔区下面
            k.AddCylinder(new Vector3(rod.x, deck + rodH * 0.40f, rod.z), rodR * 2.0f, 0.10f * U, 14,
                          S.Accent, S.PlateDark);

            // 杆顶夹头
            k.AddBox(new Vector3(rod.x, deck + rodH + 0.07f * U, rod.z),
                     new Vector3(0.42f * U, 0.14f * U, 0.42f * U), S.PlateRivet, S.PlateLight);

            // ── 前排：三只电解槽。矮方槽，槽口一层电解液 ──
            for (var i = 0; i < 3; i++)
            {
                float x = (-0.86f + 0.56f * i) * U;
                float h = (0.46f + (i == 1 ? 0.16f : 0f)) * U;   // 中间那只高一截
                var at = new Vector3(x, deck, 0.52f * U);

                k.AddBox(new Vector3(at.x, at.y + h * 0.5f, at.z),
                         new Vector3(0.48f * U, h, 0.52f * U), S.PlateRivet, S.PlateDark);

                // 槽口的液面
                k.AddBox(new Vector3(at.x, at.y + h + 0.015f * U, at.z),
                         new Vector3(0.40f * U, 0.03f * U, 0.44f * U), S.Glow, S.Glow);

                // 插在液里的极板：露出一截，三片一组
                for (var j = 0; j < 3; j++)
                    k.AddBox(new Vector3(at.x - 0.14f * U + 0.14f * U * j,
                                         at.y + h + 0.12f * U, at.z),
                             new Vector3(0.04f * U, 0.22f * U, 0.38f * U), S.PlateLight, S.Accent);
            }

            // ── 左后：液槽与管廊。把两边连起来，免得读成两座不相干的东西 ──
            k.AddCylinder(new Vector3(-0.66f * U, deck, -0.52f * U), 0.30f * U, 0.70f * U, 16,
                          S.PlateLight, S.PlateDark);
            k.AddRibs(new Vector3(-0.66f * U, deck, -0.52f * U), 0.31f * U, 0.70f * U, 3,
                      0.035f * U, S.Pipe);

            k.AddGreebleRow(new Vector3(-0.36f * U, deck + 0.62f * U, -0.52f * U),
                            new Vector3(0.42f * U, deck + 0.62f * U, -0.30f * U),
                            4, new Vector3(0.12f * U, 0.12f * U, 0.12f * U), S.Pipe);

            // 走道：把前排槽和后面连起来
            k.AddRailing(new Vector3(0f, 0f, 0.10f * U), 1.0f * U, 0.14f * U, deck + 0.02f * U,
                         0.16f * U, S.Grating);
        }

        internal static bool Apply(ref PrefabDesc desc, int itemId, string debugName)
        {
            var kit = new MeshKit();

            switch (itemId)
            {
                case 6500: SkyAssembler(kit); break;
                case 6501: LysisTower(kit); break;
                case 6502: ChemPlant(kit); break;
                case 6503: PrecisionCenter(kit); break;
                case 6504: ParticleCollider(kit); break;
                case 6505: BioGreenhouse(kit); break;
                case 6506: LavaCooler(kit); break;
                case 6507: CatalyticReactor(kit); break;
                case 6508: OmniChemPlant(kit); break;
                case 6509: RedoxBurner(kit); break;
                case 6659: RefineryPlant(kit); break;
                case 6676: SingularityVault(kit); break;
                default: return false;
            }

            Bounds reference = ReferenceBounds(ref desc);

            MegaBuildingEntry entry = MegaBuildingRegistry.EntryOf(itemId);

            float scale = entry != null && entry.modelScale > 0f
                ? entry.modelScale
                : MegaBuildingRegistry.Config.modelScale;

            float height = entry != null && entry.modelHeightScale > 0f ? entry.modelHeightScale : 1f;

            kit.Place(reference, scale, height);

            Mesh mesh = kit.ToMesh("projecteden-" + debugName);

            desc.mesh = mesh;

            if (desc.meshes != null)
                for (var i = 0; i < desc.meshes.Length; i++)
                    desc.meshes[i] = mesh;

            if (desc.lodMeshes != null)
                for (var i = 0; i < desc.lodMeshes.Length; i++)
                    if (desc.lodMeshes[i] != null)
                        desc.lodMeshes[i] = mesh;

            if (desc.lodVertas != null)
                for (var i = 0; i < desc.lodVertas.Length; i++)
                    desc.lodVertas[i] = null;

            ApplyTextures(ref desc, debugName);

            ProjectEdenPlugin.Log.LogInfo(
                $"巨型建筑「{debugName}」已换用程序化模型：{kit.VertexCount} 顶点 / {kit.TriangleCount} 三角形，" +
                $"缩放系数 {scale:0.##}（高度上限 ×{height:0.##}）；" +
                $"原版包围盒 尺寸{reference.size} 中心{reference.center} 底{reference.min.y:0.##} 顶{reference.max.y:0.##}");

            return true;
        }

        /// <summary>
        /// 把自绘图集挂到材质上。
        ///
        /// <b><c>_MS_Tex</c> 默认不动，这是被实测推翻后的结论。</b>
        /// 原先的理由是：UV 一改，原版那张金属度/光滑度图会按新 UV 取到不相干的数值、表面会斑驳，
        /// 所以换成常量图「最坏也只是偏哑光」。<b>实际结果是整座建筑完全不可见。</b>
        /// 那个常量 <c>(70,150,0,150)</c> 是猜的——着色器是 <c>VF Shaders/Forward/PBR Standard</c>，
        /// 它把哪个通道当什么用我们并不知道，而 B=0 落在某个控制不透明度的通道上就会全透。
        /// <b>「斑驳」是推测，「看不见」是观测</b>，所以默认退回原版那张。
        ///
        /// <b>那个常量现在不是猜的了</b>：<c>BuildingTexture.MeasuredMetalSmooth</c> 改成
        /// 读原版那张图取全图平均——通道怎么打包仍然不知道，但不需要知道，
        /// 平均值天然落在原版自己用过的取值范围里，最坏是「像一面普通的原版表面」。
        /// 开关默认仍然关着，要试就开 <c>megabuildings.json</c> 的
        /// <c>overrideMetalSmoothTex</c>；量不出来会自动退回原版那张并打一行 WARNING。
        ///
        /// 每个属性都先 <c>HasProperty</c> 再写：不同 LOD 的材质用的着色器未必一样，
        /// 写一个不存在的属性 Unity 只会静默忽略，那就分不清「写了没生效」和「压根没这属性」。
        /// 首次调用把着色器名字和属性有无报一行，省得下次又靠猜。
        /// </summary>
        /// <summary>
        /// 原版材质上那张金属度/光滑度图。任取第一份有它的即可——九座共用同一个源建筑。
        /// </summary>
        private static Texture FindVanillaMetalSmooth(ref PrefabDesc desc)
        {
            if (desc.lodMaterials == null) return null;

            foreach (Material[] lod in desc.lodMaterials)
            {
                if (lod == null) continue;

                foreach (Material mat in lod)
                {
                    if (mat == null || !mat.HasProperty("_MS_Tex")) continue;

                    Texture t = mat.GetTexture("_MS_Tex");

                    if (t != null) return t;
                }
            }

            return null;
        }

        private static void ApplyTextures(ref PrefabDesc desc, string debugName)
        {
            if (desc.lodMaterials == null) return;

            Texture2D albedo = BuildingTexture.Albedo();

            bool overrideMain = MegaBuildingRegistry.Config.overrideMainTex;
            bool overrideMs = MegaBuildingRegistry.Config.overrideMetalSmoothTex;

            // 中性值要从原版那张图上量，所以先找一份带 _MS_Tex 的材质
            Texture2D ms = null;

            if (overrideMs)
            {
                ms = BuildingTexture.MeasuredMetalSmooth(FindVanillaMetalSmooth(ref desc));

                // 量不出来就当没开这个开关：宁可斑驳，也不要再来一次「整座看不见」
                if (ms == null) overrideMs = false;
            }

            var touched = 0;
            string shaderName = null;
            var hasMain = false;
            var hasMs = false;

            foreach (Material[] lod in desc.lodMaterials)
            {
                if (lod == null) continue;

                foreach (Material mat in lod)
                {
                    if (mat == null) continue;

                    if (shaderName == null && mat.shader != null)
                    {
                        shaderName = mat.shader.name;
                        hasMain = mat.HasProperty("_MainTex");
                        hasMs = mat.HasProperty("_MS_Tex");
                    }

                    if (overrideMain && mat.HasProperty("_MainTex")) mat.SetTexture("_MainTex", albedo);

                    // _MS_Tex 默认不动，见下面那段注释
                    if (overrideMs && mat.HasProperty("_MS_Tex")) mat.SetTexture("_MS_Tex", ms);

                    touched++;
                }
            }

            if (_shaderReported) return;

            _shaderReported = true;

            ProjectEdenPlugin.Log.LogInfo(
                $"巨型建筑贴图：着色器「{shaderName ?? "未知"}」，_MainTex={(hasMain ? "有" : "无")}，" +
                $"_MS_Tex={(hasMs ? "有" : "无")}，本次改写 {touched} 份材质（首座是「{debugName}」）；" +
                $"自绘 _MainTex={(overrideMain ? "开" : "关")}，常量 _MS_Tex={(overrideMs ? "开" : "关")}");
        }

        /// <summary>
        /// 拿原版网格的包围盒当尺寸参照。读不到就退回一个保守的立方体——
        /// 与其猜一个尺寸，不如让它明显偏小，至少不会糊住旁边的建筑。
        /// </summary>
        private static Bounds ReferenceBounds(ref PrefabDesc desc)
        {
            Mesh source = desc.mesh;

            if (source == null && desc.lodMeshes != null)
                for (var i = 0; i < desc.lodMeshes.Length && source == null; i++)
                    source = desc.lodMeshes[i];

            return source != null ? source.bounds : new Bounds(Vector3.zero, new Vector3(3f, 3f, 3f));
        }
    }
}
