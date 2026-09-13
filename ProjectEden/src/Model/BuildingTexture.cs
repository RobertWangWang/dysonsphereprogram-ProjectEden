using UnityEngine;

namespace ProjectEden.Model
{
    /// <summary>
    /// 程序化生成建筑贴图：一张 4×4 的图集，每格是一种「表面」。
    ///
    /// <b>为什么必须自己生成。</b> 第一版沿用原版材质，而原版贴图是按原版网格 UV 排布的图集，
    /// 给自建几何乱配 UV 会取到不相干的区域；当时的对策是把所有 UV 收敛到一个点，
    /// 代价是整栋楼只有一个纯色面——<b>完全没有细节</b>。自己出一张图集，
    /// UV 怎么配就由我们说了算，这个约束直接消失。
    ///
    /// <b>细节主要靠钢板格。</b> 这几格覆盖建筑绝大部分面积，第一版把它们画得太素
    /// （一格只有 2×2 块板、缝很淡），远看仍然是平的。现在每格 4×4 块板，
    /// 缝压暗、板边再压一档做出厚度、每块整体明暗随机偏移、四角打铆钉——
    /// <b>缝和铆钉的密度才是「看起来像工业建筑」的来源</b>，不是形状。
    ///
    /// <b>每格自成一体，不跨格采样。</b> UV 映射到某一格内部（留了内缩边距），
    /// 格与格之间不会因双线性插值渗色。
    /// </summary>
    internal static class BuildingTexture
    {
        private const int Size = 256;

        internal const int Grid = 4;

        private const int Cell = Size / Grid;

        /// <summary>采样时向内缩几个像素，避免双线性插值把邻格带进来。</summary>
        private const float Inset = 1.5f / Size;

        // ── 表面种类。几何里用这些常量点名要哪一格 ──────────
        internal const int PlateLight = 0;   // 主体：浅色钢板
        internal const int PlateDark = 1;    // 深色机身
        internal const int Accent = 2;       // 强调色，带一道虚线腰带
        internal const int Hazard = 3;       // 黄黑警示条
        internal const int Grating = 4;      // 格栅 / 走道
        internal const int Glow = 5;         // 亮面：窗、指示带
        internal const int Pipe = 6;         // 管道：横向环纹
        internal const int Concrete = 7;     // 基座：带骨料的混凝土
        internal const int PlateRivet = 8;   // 大块厚板，四角重铆钉
        internal const int Vent = 9;         // 百叶 / 散热格

        private static Texture2D _albedo;
        private static Texture2D _ms;

        /// <summary>算一次存着，五座建筑共用同一张。</summary>
        internal static Texture2D Albedo()
        {
            if (_albedo != null) return _albedo;

            var tex = new Texture2D(Size, Size, TextureFormat.RGBA32, true)
            {
                name = "projecteden-building-atlas",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                hideFlags = HideFlags.HideAndDontSave,
            };

            var px = new Color32[Size * Size];

            for (var c = 0; c < Grid * Grid; c++) PaintCell(px, c);

            tex.SetPixels32(px);
            tex.Apply(true);

            _albedo = tex;

            return tex;
        }

        /// <summary>
        /// 一张常量值的「金属度 / 光滑度」图，<b>常量是从原版那张图上量出来的，不是写死的</b>。
        ///
        /// 换了 UV 之后再让 <c>_MS_Tex</c> 沿用原版那张，会按新 UV 取到毫不相干的数值。
        /// 常量图不管通道怎么打包都不会制造花纹——但<b>前提是那个常量本身站得住</b>。
        ///
        /// <b>上一版栽在这一点上：常量 <c>(70,150,0,150)</c> 是凭空写的，结果整座建筑完全不可见。</b>
        /// 着色器是 <c>VF Shaders/Forward/PBR Standard</c>，它把哪个通道当什么用我们并不知道，
        /// 而某个通道写 0 恰好落在控制不透明度的那一路上就会全透。当时的辩护是
        /// 「最坏也只是偏哑光」——那句话本身就是猜测的一部分，不是猜测的边界。
        ///
        /// <b>现在不猜了：直接读原版那张图，取全图平均。</b> 通道怎么打包仍然不知道，
        /// 但<b>不需要知道</b>——平均值天然落在原版自己用过的取值范围里，
        /// 最坏情况是「看起来像一面普通的原版表面」，不可能是全透。
        /// 原版贴图不能 <c>GetPixels</c>，所以走 Blit 到 RenderTexture 再 ReadPixels
        /// （和 <c>IconTinter</c> 同一个办法）。读不出来就返回 null，调用方退回原版那张。
        /// </summary>
        internal static Texture2D MeasuredMetalSmooth(Texture source)
        {
            if (_ms != null) return _ms;

            Color32 mean;

            if (!MeanOf(source, out mean))
            {
                ProjectEdenPlugin.Log.LogWarning(
                    "巨型建筑贴图：读不出原版 _MS_Tex，无法量出中性值——本次退回原版那张");

                return null;
            }

            var tex = new Texture2D(4, 4, TextureFormat.RGBA32, false)
            {
                name = "projecteden-building-ms",
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave,
            };

            var px = new Color32[16];

            for (var i = 0; i < px.Length; i++) px[i] = mean;

            tex.SetPixels32(px);
            tex.Apply(false);

            _ms = tex;

            ProjectEdenPlugin.Log.LogInfo(
                $"巨型建筑贴图：原版 _MS_Tex 全图平均 = ({mean.r}, {mean.g}, {mean.b}, {mean.a})，" +
                "已用它填一张常量图。**这四个数是量出来的，不是猜的**——" +
                "上一版写死的 (70,150,0,150) 让整座建筑不可见");

            return tex;
        }

        /// <summary>
        /// 原版贴图不可 CPU 读取，先 Blit 进 RenderTexture 再 ReadPixels。
        /// 失败一律返回 false，让调用方退回原版那张，不要拿一个瞎猜的值往上写。
        /// </summary>
        private static bool MeanOf(Texture source, out Color32 mean)
        {
            mean = new Color32(128, 128, 128, 255);

            if (source == null) return false;

            RenderTexture temp = RenderTexture.GetTemporary(
                source.width, source.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);

            RenderTexture previous = RenderTexture.active;
            Texture2D readable = null;

            try
            {
                Graphics.Blit(source, temp);
                RenderTexture.active = temp;

                readable = new Texture2D(temp.width, temp.height, TextureFormat.RGBA32, false)
                {
                    hideFlags = HideFlags.HideAndDontSave,
                };

                readable.ReadPixels(new Rect(0, 0, temp.width, temp.height), 0, 0);
                readable.Apply(false);

                Color32[] px = readable.GetPixels32();

                if (px.Length == 0) return false;

                long r = 0, g = 0, b = 0, a = 0;

                foreach (Color32 c in px)
                {
                    r += c.r;
                    g += c.g;
                    b += c.b;
                    a += c.a;
                }

                mean = new Color32((byte)(r / px.Length), (byte)(g / px.Length),
                                   (byte)(b / px.Length), (byte)(a / px.Length));

                return true;
            }
            catch (System.Exception e)
            {
                ProjectEdenPlugin.Log.LogWarning($"巨型建筑贴图：读原版 _MS_Tex 失败——{e.Message}");

                return false;
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(temp);

                if (readable != null) Object.Destroy(readable);
            }
        }

        /// <summary>某一格在 0..1 UV 空间里的矩形（已内缩）。</summary>
        internal static Rect CellRect(int cell)
        {
            cell = Mathf.Clamp(cell, 0, Grid * Grid - 1);

            float u = (float)(cell % Grid) / Grid + Inset;
            float v = (float)(cell / Grid) / Grid + Inset;
            float s = 1f / Grid - Inset * 2f;

            return new Rect(u, v, s, s);
        }

        // ── 画格子 ────────────────────────────────────────────

        private static void PaintCell(Color32[] px, int cell)
        {
            int ox = cell % Grid * Cell;
            int oy = cell / Grid * Cell;

            switch (cell)
            {
                case PlateLight:
                    Plates(px, ox, oy, new Color32(196, 200, 208, 255), new Color32(112, 118, 130, 255), 16, true, 1);
                    break;
                case PlateDark:
                    Plates(px, ox, oy, new Color32(96, 102, 116, 255), new Color32(48, 52, 62, 255), 16, true, 2);
                    break;
                case Accent:
                    AccentBand(px, ox, oy, new Color32(232, 236, 244, 255), new Color32(150, 156, 168, 255));
                    break;
                case Hazard:
                    Stripes(px, ox, oy, new Color32(232, 190, 40, 255), new Color32(40, 40, 44, 255));
                    break;
                case Grating:
                    Grid2(px, ox, oy, new Color32(120, 126, 138, 255), new Color32(50, 54, 64, 255));
                    break;
                case Glow:
                    Windows(px, ox, oy);
                    break;
                case Pipe:
                    Rings(px, ox, oy, new Color32(178, 183, 192, 255), new Color32(112, 118, 128, 255));
                    break;
                case Concrete:
                    ConcretePattern(px, ox, oy, new Color32(150, 150, 148, 255));
                    break;
                case PlateRivet:
                    Plates(px, ox, oy, new Color32(172, 176, 186, 255), new Color32(88, 93, 104, 255), 32, true, 4);
                    break;
                case Vent:
                    Louvers(px, ox, oy, new Color32(140, 146, 158, 255), new Color32(46, 50, 60, 255));
                    break;
                default:
                    ConcretePattern(px, ox, oy, new Color32(160, 164, 172, 255));
                    break;
            }
        }

        /// <summary>确定性伪随机：同一坐标每次生成结果一致，贴图才是可复现的。</summary>
        private static int Hash(int x, int y, int seed)
        {
            unchecked
            {
                int h = (x * 73856093) ^ (y * 19349663) ^ (seed * 83492791);

                return (h ^ (h >> 13)) & 0x7FFFFFFF;
            }
        }

        private static Color32 Shade(Color32 c, int d) => new Color32(
            (byte)Mathf.Clamp(c.r + d, 0, 255),
            (byte)Mathf.Clamp(c.g + d, 0, 255),
            (byte)Mathf.Clamp(c.b + d, 0, 255), 255);

        private static void Put(Color32[] px, int x, int y, Color32 c)
        {
            if (x < 0 || y < 0 || x >= Size || y >= Size) return;

            px[y * Size + x] = c;
        }

        /// <summary>
        /// 分块钢板。<paramref name="plate"/> 是每块板的边长（像素）。
        /// 缝压暗、板边再压一档做厚度、每块整体明暗随机偏移、四角打铆钉——
        /// 这四件事缺一件，远看就会变回一张平板。
        /// </summary>
        private static void Plates(Color32[] px, int ox, int oy, Color32 face, Color32 seam, int plate,
                                   bool rivets, int seed)
        {
            for (var y = 0; y < Cell; y++)
            {
                for (var x = 0; x < Cell; x++)
                {
                    if (x % plate == 0 || y % plate == 0)
                    {
                        Put(px, ox + x, oy + y, seam);

                        continue;
                    }

                    int d = Hash(x / plate, y / plate, seed) % 5 * 4 - 8;

                    if (x % plate == plate - 1 || y % plate == plate - 1) d -= 6;

                    Put(px, ox + x, oy + y, Shade(face, d));
                }
            }

            if (!rivets) return;

            int n = Cell / plate;

            for (var by = 0; by < n; by++)
            {
                for (var bx = 0; bx < n; bx++)
                {
                    AddRivet(px, ox + bx * plate + 3, oy + by * plate + 3, face, seam);
                    AddRivet(px, ox + bx * plate + plate - 3, oy + by * plate + 3, face, seam);
                    AddRivet(px, ox + bx * plate + 3, oy + by * plate + plate - 3, face, seam);
                    AddRivet(px, ox + bx * plate + plate - 3, oy + by * plate + plate - 3, face, seam);
                }
            }
        }

        /// <summary>一颗铆钉：一个暗点加一个亮点，两像素就有立体感。</summary>
        private static void AddRivet(Color32[] px, int x, int y, Color32 face, Color32 seam)
        {
            Put(px, x, y, seam);
            Put(px, x, y - 1, Shade(face, 26));
        }

        /// <summary>强调面：钢板加一道虚线腰带，用来点出建筑的主色。</summary>
        private static void AccentBand(Color32[] px, int ox, int oy, Color32 face, Color32 seam)
        {
            Plates(px, ox, oy, face, seam, 16, true, 5);

            for (var y = 26; y < 34; y++)
                for (var x = 0; x < Cell; x++)
                    Put(px, ox + x, oy + y, (x / 6 & 1) != 0 ? seam : Shade(face, -30));
        }

        private static void Stripes(Color32[] px, int ox, int oy, Color32 a, Color32 b)
        {
            for (var y = 0; y < Cell; y++)
                for (var x = 0; x < Cell; x++)
                    Put(px, ox + x, oy + y, ((x + y) / 8 & 1) == 0 ? a : b);
        }

        private static void Grid2(Color32[] px, int ox, int oy, Color32 bar, Color32 hole)
        {
            for (var y = 0; y < Cell; y++)
                for (var x = 0; x < Cell; x++)
                    Put(px, ox + x, oy + y, x % 8 < 3 || y % 8 < 2 ? bar : hole);
        }

        /// <summary>发光带：暗底上排一行亮窗，偶尔留一格不亮。</summary>
        private static void Windows(Color32[] px, int ox, int oy)
        {
            var dark = new Color32(34, 38, 48, 255);
            var lit = new Color32(180, 232, 255, 255);
            var dim = new Color32(92, 136, 168, 255);

            for (var y = 0; y < Cell; y++)
            {
                for (var x = 0; x < Cell; x++)
                {
                    bool inWindow = y % 16 >= 4 && y % 16 < 12 && x % 10 >= 2 && x % 10 < 8;
                    bool off = (x / 10 + y / 16 * 3) % 7 == 0;

                    Put(px, ox + x, oy + y, inWindow ? (off ? dim : lit) : dark);
                }
            }
        }

        private static void Rings(Color32[] px, int ox, int oy, Color32 body, Color32 ring)
        {
            for (var y = 0; y < Cell; y++)
                for (var x = 0; x < Cell; x++)
                    Put(px, ox + x, oy + y,
                        y % 16 < 2 || y % 16 == 15 ? ring : Shade(body, Hash(x, y / 16, 3) % 3 * 3 - 3));
        }

        /// <summary>混凝土：细噪声打底，再随机撒一些暗骨料，四边压暗做出浇筑边。</summary>
        private static void ConcretePattern(Color32[] px, int ox, int oy, Color32 baseColor)
        {
            for (var y = 0; y < Cell; y++)
            {
                for (var x = 0; x < Cell; x++)
                {
                    int n = Hash(x, y, 7) % 40 - 20;

                    if (Hash(x, y, 11) % 37 == 0) n -= 34;

                    if (y == 0 || y == Cell - 1 || x == 0 || x == Cell - 1) n -= 18;

                    Put(px, ox + x, oy + y, Shade(baseColor, n));
                }
            }
        }

        private static void Louvers(Color32[] px, int ox, int oy, Color32 blade, Color32 gap)
        {
            for (var y = 0; y < Cell; y++)
                for (var x = 0; x < Cell; x++)
                    Put(px, ox + x, oy + y, y % 6 == 0 ? Shade(blade, 6) : y % 6 < 4 ? blade : gap);
        }
    }
}
