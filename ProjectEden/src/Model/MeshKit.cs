using System.Collections.Generic;
using UnityEngine;

namespace ProjectEden.Model
{
    /// <summary>
    /// 一个极小的程序化建模工具：攒顶点和三角形，最后吐一个 <see cref="Mesh"/>。
    ///
    /// <b>为什么是「生成」而不是「建模」。</b> 和 <c>tools/make_icons.py</c> 同一套思路——
    /// 图标是用代码画矢量再栅格化的，建筑也用代码算顶点。好处有三条：
    /// 不需要 Unity 编辑器、不需要 AssetBundle、产出是<b>完全原创的几何</b>，
    /// 不牵涉任何第三方美术资源的授权问题。
    ///
    /// <b>这套东西能用，是因为世界里真正被画出来的是 <c>PrefabDesc.lodMeshes</c>。</b>
    /// <c>ObjectRenderer.Init</c> 拿 <c>lodMeshes[i]</c> 和 <c>lodVertas[i]</c> 去构造
    /// <c>BatchRenderer(Mesh, Material[], …, VertaBuffer, …)</c>：Mesh 是几何，
    /// VertaBuffer 是<b>顶点动画</b>缓冲（唯一用法是 <c>SetToAnimMaterial</c>），
    /// 而且构造函数里对它做了判空（IL 0139 / 01EE）。所以静态建筑只要换掉 Mesh 就够了。
    ///
    /// <b>不要试图自己造 VertaBuffer。</b> 它在 <c>ReadPrefab</c> 里是
    /// <c>VertaBuffer.LoadFromFile(lodVertaPaths[i])</c> 从<b>文件</b>读出来的预烘焙资源，
    /// 顶点数和原版网格一一对应。换了几何之后那份数据就对不上了，必须把对应的<b>元素</b>置空
    /// （不是把整个数组置空——<c>Init</c> 会去索引它）。
    ///
    /// <b>细节从哪来。</b> 第一版把所有 UV 收敛到一个点，结果是整栋楼一个纯色面、毫无细节。
    /// 现在每个图元点名一种「表面」（<see cref="BuildingTexture"/> 里的格子），
    /// 而且<b>大面会按板材尺寸切分</b>——一面墙不是一块巨板，是一排板，
    /// 缝、铆钉、格栅因此有了合理的密度。切分同时让顶点变密，光照过渡也好看得多。
    ///
    /// 法线按面算（每个面独立顶点），所以是硬边低多边形风格——工业建筑本来就该是硬边。
    /// </summary>
    internal sealed class MeshKit
    {
        private readonly List<Vector3> _verts = new List<Vector3>();
        private readonly List<Vector3> _normals = new List<Vector3>();
        private readonly List<Vector2> _uvs = new List<Vector2>();
        private readonly List<int> _tris = new List<int>();

        /// <summary>一块板在名义空间里的边长。面比它大就切开。</summary>
        private readonly float _panel;

        /// <summary>一条边最多切几段，防止大面炸出几万个三角形。</summary>
        private const int MaxSplit = 6;

        internal MeshKit(float panelSize = 0.34f)
        {
            _panel = Mathf.Max(0.05f, panelSize);
        }

        public int VertexCount => _verts.Count;

        public int TriangleCount => _tris.Count / 3;

        // ── 图元 ──────────────────────────────────────────────

        /// <summary>轴对齐盒子。<paramref name="side"/> 是四周，<paramref name="top"/> 不给就跟着四周。</summary>
        public void AddBox(Vector3 center, Vector3 size, int side, int top = -1, int bottom = -1)
        {
            if (top < 0) top = side;
            if (bottom < 0) bottom = side;

            Vector3 h = size * 0.5f;

            Vector3 A = center + new Vector3(-h.x, -h.y, h.z), B = center + new Vector3(h.x, -h.y, h.z);
            Vector3 C = center + new Vector3(h.x, h.y, h.z), D = center + new Vector3(-h.x, h.y, h.z);
            Vector3 E = center + new Vector3(h.x, -h.y, -h.z), F = center + new Vector3(-h.x, -h.y, -h.z);
            Vector3 G = center + new Vector3(-h.x, h.y, -h.z), H = center + new Vector3(h.x, h.y, -h.z);

            AddQuad(A, B, C, D, side);
            AddQuad(E, F, G, H, side);
            AddQuad(B, E, H, C, side);
            AddQuad(F, A, D, G, side);
            AddQuad(D, C, H, G, top);
            AddQuad(F, E, B, A, bottom);
        }

        /// <summary>上下底面尺寸不同的棱台。收口让建筑显得有重量。</summary>
        public void AddTaperedBox(Vector3 baseCenter, Vector2 bottom, Vector2 topSize, float height,
                                  int side, int top = -1)
        {
            if (top < 0) top = side;

            Vector2 b = bottom * 0.5f;
            Vector2 t = topSize * 0.5f;
            float y0 = baseCenter.y;
            float y1 = baseCenter.y + height;

            Vector3 B0 = new Vector3(baseCenter.x - b.x, y0, baseCenter.z - b.y);
            Vector3 B1 = new Vector3(baseCenter.x + b.x, y0, baseCenter.z - b.y);
            Vector3 B2 = new Vector3(baseCenter.x + b.x, y0, baseCenter.z + b.y);
            Vector3 B3 = new Vector3(baseCenter.x - b.x, y0, baseCenter.z + b.y);
            Vector3 T0 = new Vector3(baseCenter.x - t.x, y1, baseCenter.z - t.y);
            Vector3 T1 = new Vector3(baseCenter.x + t.x, y1, baseCenter.z - t.y);
            Vector3 T2 = new Vector3(baseCenter.x + t.x, y1, baseCenter.z + t.y);
            Vector3 T3 = new Vector3(baseCenter.x - t.x, y1, baseCenter.z + t.y);

            AddQuad(B3, B2, T2, T3, side);
            AddQuad(B1, B0, T0, T1, side);
            AddQuad(B2, B1, T1, T2, side);
            AddQuad(B0, B3, T3, T0, side);
            AddQuad(T3, T2, T1, T0, top);
            AddQuad(B0, B1, B2, B3, side);
        }

        public void AddCylinder(Vector3 baseCenter, float radius, float height, int sides, int wall, int cap = -1)
        {
            AddCone(baseCenter, radius, radius, height, sides, wall, cap);
        }

        /// <summary>上下半径可不同的圆台；上半径给 0 就是锥。</summary>
        public void AddCone(Vector3 baseCenter, float rBottom, float rTop, float height, int sides,
                            int wall, int cap = -1)
        {
            if (cap < 0) cap = wall;
            if (sides < 3) sides = 3;

            float y0 = baseCenter.y;
            float y1 = baseCenter.y + height;

            // 竖向也要分段，否则高罐子只有一圈板缝
            int rows = Mathf.Clamp(Mathf.RoundToInt(height / _panel), 1, MaxSplit);

            for (var i = 0; i < sides; i++)
            {
                float a0 = Mathf.PI * 2f * i / sides;
                float a1 = Mathf.PI * 2f * (i + 1) / sides;

                for (var r = 0; r < rows; r++)
                {
                    float f0 = (float)r / rows;
                    float f1 = (float)(r + 1) / rows;

                    float yA = Mathf.Lerp(y0, y1, f0), yB = Mathf.Lerp(y0, y1, f1);
                    float rA = Mathf.Lerp(rBottom, rTop, f0), rB = Mathf.Lerp(rBottom, rTop, f1);

                    Vector3 p00 = Ring(baseCenter, a0, rA, yA), p10 = Ring(baseCenter, a1, rA, yA);
                    Vector3 p01 = Ring(baseCenter, a0, rB, yB), p11 = Ring(baseCenter, a1, rB, yB);

                    if (rA > 0.0001f && rB > 0.0001f) AddFlatQuad(p00, p10, p11, p01, wall);
                    else if (rB <= 0.0001f) AddTri(p00, p10, p01, wall);
                    else AddTri(p00, p11, p01, wall);
                }

                Vector3 t0 = Ring(baseCenter, a0, rTop, y1), t1 = Ring(baseCenter, a1, rTop, y1);
                Vector3 b0 = Ring(baseCenter, a0, rBottom, y0), b1 = Ring(baseCenter, a1, rBottom, y0);

                if (rTop > 0.0001f) AddTri(new Vector3(baseCenter.x, y1, baseCenter.z), t0, t1, cap);
                if (rBottom > 0.0001f) AddTri(new Vector3(baseCenter.x, y0, baseCenter.z), b1, b0, cap);
            }
        }

        private static Vector3 Ring(Vector3 c, float angle, float r, float y)
            => new Vector3(c.x + Mathf.Cos(angle) * r, y, c.z + Mathf.Sin(angle) * r);

        /// <summary>水平圆环。</summary>
        public void AddTorus(Vector3 center, float major, float tube, int segs, int sides, int surface)
        {
            if (segs < 3) segs = 3;
            if (sides < 3) sides = 3;

            for (var i = 0; i < segs; i++)
            {
                float u0 = Mathf.PI * 2f * i / segs, u1 = Mathf.PI * 2f * (i + 1) / segs;

                for (var j = 0; j < sides; j++)
                {
                    float v0 = Mathf.PI * 2f * j / sides, v1 = Mathf.PI * 2f * (j + 1) / sides;

                    AddFlatQuad(TorusPoint(center, major, tube, u0, v0),
                                TorusPoint(center, major, tube, u1, v0),
                                TorusPoint(center, major, tube, u1, v1),
                                TorusPoint(center, major, tube, u0, v1), surface);
                }
            }
        }

        private static Vector3 TorusPoint(Vector3 c, float major, float tube, float u, float v)
        {
            float r = major + tube * Mathf.Cos(v);

            return new Vector3(c.x + Mathf.Cos(u) * r, c.y + tube * Mathf.Sin(v), c.z + Mathf.Sin(u) * r);
        }

        // ── 细节件：这些是「有没有细节」的关键 ────────────────

        /// <summary>一圈贴在柱体外侧的竖肋。打破圆筒的光滑感。</summary>
        public void AddRibs(Vector3 baseCenter, float radius, float height, int count, float thickness, int surface)
        {
            for (var i = 0; i < count; i++)
            {
                float a = Mathf.PI * 2f * i / count;
                Vector3 p = Ring(baseCenter, a, radius, baseCenter.y + height * 0.5f);

                AddBox(p, new Vector3(thickness, height, thickness), surface);
            }
        }

        /// <summary>沿一条边排开的小方块（设备箱、散热片）。工业感主要靠这个。</summary>
        public void AddGreebleRow(Vector3 start, Vector3 end, int count, Vector3 size, int surface)
        {
            if (count < 1) return;

            for (var i = 0; i < count; i++)
            {
                float f = count == 1 ? 0.5f : (float)i / (count - 1);

                // 尺寸略微交错，整齐划一反而假
                float k = 0.75f + (i % 3) * 0.18f;

                AddBox(Vector3.Lerp(start, end, f), new Vector3(size.x, size.y * k, size.z), surface);
            }
        }

        /// <summary>护栏：一圈立柱加一道横栏。</summary>
        public void AddRailing(Vector3 center, float halfX, float halfZ, float y, float height, int surface)
        {
            float post = height * 0.12f;

            for (var i = 0; i < 4; i++)
            {
                float x = (i < 2 ? -1f : 1f) * halfX;
                float z = (i % 2 == 0 ? -1f : 1f) * halfZ;

                AddBox(new Vector3(center.x + x, y + height * 0.5f, center.z + z),
                       new Vector3(post, height, post), surface);
            }

            float top = y + height;

            AddBox(new Vector3(center.x, top, center.z - halfZ), new Vector3(halfX * 2f, post, post), surface);
            AddBox(new Vector3(center.x, top, center.z + halfZ), new Vector3(halfX * 2f, post, post), surface);
            AddBox(new Vector3(center.x - halfX, top, center.z), new Vector3(post, post, halfZ * 2f), surface);
            AddBox(new Vector3(center.x + halfX, top, center.z), new Vector3(post, post, halfZ * 2f), surface);
        }

        // ── 底层 ──────────────────────────────────────────────

        /// <summary>
        /// 一个四边形，<b>按板材尺寸切成网格</b>再逐块贴图。
        /// 每个小块完整映射一次所选格子，所以缝和铆钉的密度是跟着实际尺寸走的。
        /// </summary>
        private void AddQuad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, int surface)
        {
            int nu = Mathf.Clamp(Mathf.RoundToInt(Vector3.Distance(a, b) / _panel), 1, MaxSplit);
            int nv = Mathf.Clamp(Mathf.RoundToInt(Vector3.Distance(a, d) / _panel), 1, MaxSplit);

            for (var i = 0; i < nu; i++)
            {
                for (var j = 0; j < nv; j++)
                {
                    float u0 = (float)i / nu, u1 = (float)(i + 1) / nu;
                    float v0 = (float)j / nv, v1 = (float)(j + 1) / nv;

                    AddFlatQuad(Bilerp(a, b, c, d, u0, v0), Bilerp(a, b, c, d, u1, v0),
                                Bilerp(a, b, c, d, u1, v1), Bilerp(a, b, c, d, u0, v1), surface);
                }
            }
        }

        private static Vector3 Bilerp(Vector3 a, Vector3 b, Vector3 c, Vector3 d, float u, float v)
            => Vector3.Lerp(Vector3.Lerp(a, b, u), Vector3.Lerp(d, c, u), v);

        /// <summary>不再切分的四边形，四角映射到格子的四角。</summary>
        private void AddFlatQuad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, int surface)
        {
            Rect r = BuildingTexture.CellRect(surface);

            AddTri(a, b, c, surface, new Vector2(r.xMin, r.yMin), new Vector2(r.xMax, r.yMin), new Vector2(r.xMax, r.yMax));
            AddTri(a, c, d, surface, new Vector2(r.xMin, r.yMin), new Vector2(r.xMax, r.yMax), new Vector2(r.xMin, r.yMax));
        }

        private void AddTri(Vector3 a, Vector3 b, Vector3 c, int surface)
        {
            Rect r = BuildingTexture.CellRect(surface);
            Vector2 mid = new Vector2(r.center.x, r.yMax);

            AddTri(a, b, c, surface, new Vector2(r.xMin, r.yMin), new Vector2(r.xMax, r.yMin), mid);
        }

        private void AddTri(Vector3 a, Vector3 b, Vector3 c, int surface, Vector2 ua, Vector2 ub, Vector2 uc)
        {
            Vector3 n = Vector3.Cross(b - a, c - a);

            // 退化三角形会让法线变成 NaN，进而整块渲染消失
            n = n.sqrMagnitude < 1e-12f ? Vector3.up : n.normalized;

            int i0 = _verts.Count;

            _verts.Add(a); _verts.Add(b); _verts.Add(c);
            _normals.Add(n); _normals.Add(n); _normals.Add(n);
            _uvs.Add(ua); _uvs.Add(ub); _uvs.Add(uc);
            _tris.Add(i0); _tris.Add(i0 + 1); _tris.Add(i0 + 2);
        }

        // ── 收尾 ──────────────────────────────────────────────

        /// <summary>
        /// 把整只模型摆到位：等比缩放、水平居中于局部原点、<b>底面压在 y = 0</b>。
        ///
        /// <b>为什么是 y = 0 而不是原版包围盒的盒底。</b> 上一版对齐的是
        /// <c>reference.min.y</c>，那等于假设「原版网格的盒底就在地面」——
        /// 实测结果是建筑<b>飘在半空</b>，假设不成立。建筑的局部原点本来就落在地面上，
        /// 直接压 y = 0 不依赖任何关于原版网格的假设。
        ///
        /// <b>为什么要再乘一个 <paramref name="scaleMul"/>。</b>
        /// <b>包围盒不是占地面积。</b> 原版物流运输站的盒子是 14.6×17.9×14.6，
        /// 但它是细塔配细腿，<b>盒子里绝大部分是空的</b>；把实心体量撑满整个盒子，
        /// 看上去会比原版大一大圈。所以默认缩到六成，并且留成配置项随时可调。
        ///
        /// 水平方向只按 X/Z 算比例、再用高度封顶，是因为玩家在意的是占地，不是高度。
        /// </summary>
        /// <summary>
        /// 把生成的形体摆进原版的包围盒。
        ///
        /// <b><c>heightMul</c> 是后加的，而它修的是「九座长得都差不多」这个问题的根因。</b>
        /// 原先的逻辑是：先按填满原版占地定缩放，再拿原版高度封顶——于是一个细高的设计
        /// 会被压两次（高度封顶把缩放压下来，占地跟着一起缩），最后和一个矮胖的设计
        /// 落到几乎一样的体量上。细节画得再不一样，体量一归一化就全抹平了。
        ///
        /// 现在每座可以自己声明允许多高（相对原版包围盒）：精馏塔那种就该细高，
        /// 对撞机那种就该矮宽。
        /// </summary>
        public void Place(Bounds reference, float scaleMul, float heightMul = 1f)
        {
            if (_verts.Count == 0) return;

            var src = new Bounds(_verts[0], Vector3.zero);

            for (var i = 1; i < _verts.Count; i++) src.Encapsulate(_verts[i]);

            float sx = Mathf.Max(src.size.x, 1e-4f);
            float sy = Mathf.Max(src.size.y, 1e-4f);
            float sz = Mathf.Max(src.size.z, 1e-4f);

            float scale = Mathf.Min(reference.size.x / sx, reference.size.z / sz);

            // 高度封顶：细高的造型不能因为占地够宽就顶到天上去。
            // heightMul 让每座自己定这个上限——默认 1 就是原版包围盒的高度
            float maxHeight = reference.size.y * Mathf.Max(0.2f, heightMul);

            if (sy * scale > maxHeight) scale = maxHeight / sy;

            scale *= Mathf.Max(0.05f, scaleMul);

            for (var i = 0; i < _verts.Count; i++)
            {
                Vector3 v = _verts[i];

                _verts[i] = new Vector3(
                    (v.x - src.center.x) * scale,
                    (v.y - src.min.y) * scale,
                    (v.z - src.center.z) * scale);
            }
        }

        public Mesh ToMesh(string name)
        {
            var mesh = new Mesh { name = name };

            if (_verts.Count > 65000) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;

            mesh.SetVertices(_verts);
            mesh.SetNormals(_normals);
            mesh.SetUVs(0, _uvs);
            mesh.SetTriangles(_tris, 0);
            mesh.RecalculateBounds();

            // 运行时生成的东西不属于任何场景，Resources.UnloadUnusedAssets 会把它回收掉
            mesh.hideFlags = HideFlags.HideAndDontSave;

            return mesh;
        }
    }
}
