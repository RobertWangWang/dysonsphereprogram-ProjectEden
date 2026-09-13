"""生成 ProjectEden 的物品图标。

矢量在这里是<b>作者端的源格式</b>：DSP 只吃位图（Texture2D.LoadImage 只认 PNG/JPG），
但物品图标是 80x80、矿脉图标是 480x480，两套尺寸从同一份源出，改色改形只动参数。

    pip install drawsvg resvg-py

栅格化用 resvg 而不是 cairosvg / reportlab.renderPM：后两者在 Windows 上都要
libcairo-2.dll，pip 给不了（pycairo 把 cairo 静态链进了 .pyd，cairocffi 找不到它）。
resvg 是 Rust 写的，wheel 里自带，还原生支持透明通道。

用法：python tools/make_icons.py        # 输出到 tools/out/
"""

import math
import random
import pathlib

import drawsvg as dw
import resvg_py

# 逻辑画布 100x100，坐标原点在正中；实际输出尺寸与它无关
CANVAS = 100

# 物品图标 80，矿脉图标 480
SIZES = (80, 480)

OUT = pathlib.Path(__file__).parent / "out"


def canvas():
    return dw.Drawing(CANVAS, CANVAS, origin=(-CANVAS / 2, -CANVAS / 2))


def render(drawing, name):
    """一份 SVG 源 + 每个尺寸一张透明底 PNG。"""
    OUT.mkdir(exist_ok=True)

    svg = drawing.as_svg()

    (OUT / f"{name}.svg").write_text(svg, encoding="utf-8")

    for size in SIZES:
        png = resvg_py.svg_to_bytes(svg_string=svg, width=size, height=size)

        (OUT / f"{name}-{size}.png").write_bytes(bytes(png))

    print(f"{name}: svg + {' / '.join(f'{s}px' for s in SIZES)}")


# ── 分子式图标（球棍模型） ────────────────────────────────
# 甲醇链上这几种货都是分子，画结构式比画容器有辨识度：一眼能看出
# 「谁接谁、单键还是双键」。配色借 CPK 那套（碳深、氧红、氢白），
# 但**碳调亮了**——CPK 的碳接近纯黑，放到 DSP 那种深色界面上整个糊掉。
# 氧的红和上面 oxygen() 对齐，两张图摆一起是同一套语言。
#
# 80px 下能读出来的信息量很有限，所以只保留骨架：原子球 + 键，
# 不写元素字母——字母缩到 80px 就是一团糊。

ATOM = {
    "C": ("#6d7885", "#2b3138", 16),
    "O": ("#c8524a", "#7d2f2a", 15),
    "H": ("#eef2f6", "#93a0ae", 9),
    "S": ("#d9b53c", "#7d6512", 16),
    "Li": ("#9d7de0", "#4d3585", 13),
    "N": ("#5b7fe0", "#22366e", 16),
}

# 键要比两端的球都浅：深色底上看得见，压在球上也分得开
BOND = "#aab5c2"


def _rot(x, y, deg):
    import math
    a = math.radians(deg)

    return x * math.cos(a) - y * math.sin(a), x * math.sin(a) + y * math.cos(a)


def _bond(g, x1, y1, x2, y2, order=1, width=4.6, spread=5.8):
    """单键一条线，双键两条平行线，三键三条。偏移方向取键的法线。"""
    dx, dy = x2 - x1, y2 - y1
    length = (dx * dx + dy * dy) ** 0.5 or 1.0
    nx, ny = -dy / length, dx / length

    offsets = {1: (0,), 2: (-spread, spread), 3: (-spread * 1.6, 0, spread * 1.6)}[order]

    for off in offsets:
        g.append(dw.Line(x1 + nx * off, y1 + ny * off, x2 + nx * off, y2 + ny * off,
                         stroke=BOND, stroke_width=width, stroke_linecap="round"))


def _atom(g, kind, cx, cy):
    """球 + 左上角一点高光。高光让球看着是球而不是圆饼。"""
    fill, edge, r = ATOM[kind]

    g.append(dw.Circle(cx, cy, r, fill=fill, stroke=edge, stroke_width=3))
    g.append(dw.Circle(cx - r * 0.35, cy - r * 0.35, r * 0.32, fill="#ffffff", fill_opacity=0.35))


def _molecule(bonds, atoms, rotate=0.0, margin=5.0):
    """
    键先画、原子后画，这样键的端点被球盖住，不用算切点。

    构图**自动居中并缩放到画布**：手写 transform 试出来的那套，一挪原子就得重调，
    还容易出现「一边贴边、另一边留一大片白」。这里按原子球的实际包围盒算。
    """
    atoms = [(k, *_rot(x, y, rotate)) for k, x, y in atoms]
    bonds = [(*_rot(b[0], b[1], rotate), *_rot(b[2], b[3], rotate), *b[4:]) for b in bonds]

    xs = [x + s * ATOM[k][2] for k, x, _ in atoms for s in (-1, 1)]
    ys = [y + s * ATOM[k][2] for k, _, y in atoms for s in (-1, 1)]

    span = max(max(xs) - min(xs), max(ys) - min(ys)) or 1.0
    scale = min(1.0, (CANVAS - 2 * margin) / span)
    tx, ty = -(min(xs) + max(xs)) / 2, -(min(ys) + max(ys)) / 2

    d = canvas()
    g = dw.Group(transform=f"scale({scale:.4f}) translate({tx:.2f}, {ty:.2f})")

    for b in bonds:
        _bond(g, *b[:4], order=b[4] if len(b) > 4 else 1)

    for kind, cx, cy in atoms:
        _atom(g, kind, cx, cy)

    d.append(g)

    return d


# 氨 NH₃：氮在中间，三个氢呈锥形张开（平面画法：一上两下）。哈伯法的产物。
def ammonia():
    return _molecule(
        [(0, 4, 0, -30), (0, 4, -30, 24), (0, 4, 30, 24)],
        [("H", 0, -30), ("H", -30, 24), ("H", 30, 24), ("N", 0, 4)])


# 二氧化氮 NO₂：弯的，不是直的——氮上有一个单电子，把两个氧压到约 134°。
# 这个弯角是它和 CO₂（直线）最直观的区别，画出来一眼能分开。
def nitrogen_dioxide():
    return _molecule(
        [(0, -14, -34, 12, 2), (0, -14, 34, 12, 2)],
        [("O", -34, 12), ("O", 34, 12), ("N", 0, -14)])


# 硝酸 HNO₃：氮接三个氧，其中一个氧再接氢（羟基）。
# 画成「氮居中、两个氧朝上分开、羟基朝下」，比教科书的平面式更认得出来。
def nitric_acid():
    return _molecule(
        [(0, -6, -32, -28, 2), (0, -6, 34, -20), (0, -6, -6, 30), (-6, 30, 24, 44)],
        [("O", -32, -28), ("O", 34, -20), ("O", -6, 30), ("H", 24, 44), ("N", 0, -6)])


# 氮气 N≡N：三键，和一氧化碳同构。氮是 CPK 的蓝，但按这套的惯例调亮了一档。
def nitrogen():
    return _molecule(
        [(-30, 0, 30, 0, 3)],
        [("N", -30, 0), ("N", 30, 0)],
        rotate=-22)


# 一氧化碳 C≡O：和 oxygen() 一样的双原子构图，区别是三键 + 左边的碳是灰的。
# 二氧化碳 O=C=O：一条横带，转 22 度走对角线才填得满方形画布。
def carbon_dioxide():
    return _molecule(
        [(-42, 0, 0, 0, 2), (0, 0, 42, 0, 2)],
        [("O", -42, 0), ("O", 42, 0), ("C", 0, 0)],
        rotate=-22)


# 氧气 O=O：分子家族里最简单的一个。
def oxygen():
    return _molecule(
        [(-28, 0, 28, 0, 2)],
        [("O", -28, 0), ("O", 28, 0)],
        rotate=-22)


def carbon_monoxide():
    return _molecule(
        [(-30, 0, 30, 0, 3)],
        [("C", -30, 0), ("O", 30, 0)],
        rotate=-22)


# 甲醇 CH₃OH：碳在左、氧在右上、羟基氢再往右上，三个氢在碳周围扇开。
# 骨架走左下→右上的对角线，比横平竖直更容易在小尺寸下认出是条链。
def methanol():
    return _molecule(
        [(-18, 8, 20, -12), (20, -12, 46, -30),
         (-18, 8, -42, -14), (-18, 8, -36, 34), (-18, 8, -2, 36)],
        [("O", 20, -12), ("H", 46, -30),
         ("H", -42, -14), ("H", -36, 34), ("H", -2, 36), ("C", -18, 8)])


# 甲醛 H₂C=O：碳氧双键是这张图的主角，两个氢往左边扇开给它让位。
# 碳氧拉开到球面之间还剩十几个像素，双键的两条线才露得出来。
def formaldehyde():
    return _molecule(
        [(-16, 8, 26, -16, 2), (-16, 8, -44, -16), (-16, 8, -32, 38)],
        [("O", 26, -16), ("H", -44, -16), ("H", -32, 38), ("C", -16, 8)])


# 氢氧化锂 LiOH：三个原子一条链，熔盐电解的原料。
def lithium_hydroxide():
    return _molecule(
        [(-30, 6, 4, -8), (4, -8, 34, -24)],
        [("Li", -30, 6), ("O", 4, -8), ("H", 34, -24)])


# 硫酸锂 Li₂SO₄：中间一个硫酸根（硫 + 四个氧），两个锂离子挂在对角上。
# 对角摆而不是左右摆，包围盒方一些，自动缩放之后原子能大一点。
def lithium_sulfate():
    return _molecule(
        [(0, 0, -29, -29), (0, 0, 29, -29), (0, 0, -29, 29), (0, 0, 29, 29)],
        [("O", -29, -29), ("O", 29, -29), ("O", -29, 29), ("O", 29, 29),
         ("S", 0, 0), ("Li", -56, 34), ("Li", 56, -34)])


# 钴酸锂 LiCoO₂：层状氧化物，画的是它的**晶体结构**而不是分子式——
# 它是离子晶体，没有「一个分子」可画，而分层恰恰是它最要紧的特征：
# 锂离子在 CoO₂ 层之间进进出出，这就是锂离子电池充放电的全部原理。
def lithium_cobalt_oxide():
    d = canvas()

    slab, slab_edge = "#3a5878", "#1b2c3e"
    li, li_edge = ATOM["Li"][0], ATOM["Li"][1]

    # 三层 CoO₂，层间两排锂
    for y in (-33, 0, 33):
        d.append(dw.Rectangle(-44, y - 7, 88, 14, rx=4,
                              fill=slab, stroke=slab_edge, stroke_width=2.5))
        # 层顶一道高光，读起来像有厚度的板而不是色块
        d.append(dw.Rectangle(-40, y - 4.5, 80, 3, rx=1.5, fill="#ffffff", fill_opacity=0.18))

    for y in (-16.5, 16.5):
        for x in (-28, 0, 28):
            d.append(dw.Circle(x, y, 7.5, fill=li, stroke=li_edge, stroke_width=2))
            d.append(dw.Circle(x - 2.6, y - 2.6, 2.4, fill="#ffffff", fill_opacity=0.35))

    return d


# 乙烯 H₂C=CH₂：左右对称，中间一道碳碳双键。MTO 的目标产物。
def ethylene():
    return _molecule(
        [(-24, 0, 24, 0, 2),
         (-24, 0, -50, -26), (-24, 0, -50, 26),
         (24, 0, 50, -26), (24, 0, 50, 26)],
        [("H", -50, -26), ("H", -50, 26), ("H", 50, -26), ("H", 50, 26),
         ("C", -24, 0), ("C", 24, 0)])



# ── 铝块 ─────────────────────────────────────────────────
# 等距投影的一块锭：顶面最亮、左面中间调、右面最暗，靠三档明度撑出体积，
# 不用渐变——80px 下渐变基本看不出来，反而在缩图时糊掉边界。

def ingot(top, left, right, edge, gloss=0.55):
    """
    等距投影的一块锭。三档明度撑体积，不用渐变——80px 下渐变基本看不出来，
    缩图时反而糊掉边界。换一套配色就是另一种金属。
    """
    d = canvas()

    # 顶面菱形：上、右、下、左
    d.append(dw.Lines(0, -30, 37, -11, 0, 8, -37, -11,
                      close=True, fill=top, stroke=edge, stroke_width=1.5, stroke_linejoin="round"))

    # 左侧面：顶面的左下两条边向下拉 16
    d.append(dw.Lines(-37, -11, 0, 8, 0, 24, -37, 5,
                      close=True, fill=left, stroke=edge, stroke_width=1.5, stroke_linejoin="round"))

    # 右侧面
    d.append(dw.Lines(0, 8, 37, -11, 37, 5, 0, 24,
                      close=True, fill=right, stroke=edge, stroke_width=1.5, stroke_linejoin="round"))

    # 顶面上一道高光，读起来更像抛光金属
    d.append(dw.Lines(-22, -13, -3, -23, 6, -18, -13, -8,
                      close=True, fill="#ffffff", fill_opacity=gloss))

    return d


# ── 矿石 ─────────────────────────────────────
# 一堆棱角分明的矿块。明暗跟锤是同一套办法（三档明度撑体积），
# 区别在形：锤是规整的等距盒子，矿石是歪的。
#
# 三件事让它读起来像矿而不是宝石：
#   · <b>顶点逐个抖动</b>——规整的多面体怎么调都像切割过的宝石
#   · <b>每块各自旋一个角</b>——三块同朝向就成了阵列，不是堆
#   · <b>前排压住后排</b>——后面那块先画、被遮掉一角，厚度就出来了
#
# 随机数固定种子，每次生成的图完全一样。

def _chunk(g, cx, cy, k, rot, top, left, right, edge, rng):
    """一块矿石。本地坐标里画好，旋转缩放交给 transform。"""

    def v(x, y, j=2.6):
        return x + rng.uniform(-j, j), y + rng.uniform(-j, j)

    apex = v(0, -21)
    tl, tr = v(-19, -8), v(19, -8)
    mid = v(0, 3)
    bl, br = v(-15, 17), v(15, 17)
    bot = v(0, 22)

    sub = dw.Group(transform=f"translate({cx}, {cy}) rotate({rot}) scale({k})")

    common = dict(stroke=edge, stroke_width=1.8, stroke_linejoin="round")

    # 顶面最亮
    sub.append(dw.Lines(*apex, *tr, *mid, *tl, close=True, fill=top, **common))
    # 左面中间调
    sub.append(dw.Lines(*tl, *mid, *bot, *bl, close=True, fill=left, **common))
    # 右面最暗
    sub.append(dw.Lines(*tr, *mid, *bot, *br, close=True, fill=right, **common))

    g.append(sub)

    return sub


def ore(top, left, right, edge, gloss=0.24):
    """三块堆成的矿。换一套配色就是另一种矿。"""
    import random

    d = canvas()
    rng = random.Random(20260910)

    # 整体放大一点再往下挪：三块堆完周围剩的空白比分子式多，不撞满不好认
    g = dw.Group(transform="translate(0, 2) scale(1.08)")

    # 后排那块先画，被前面两块压住一角。
    # 坐标故意不对称，左小右大，避开“三个并排”那种摆法。
    _chunk(g, -3, -13, 1.00, -6, top, left, right, edge, rng)
    _chunk(g, -25, 15, 0.60, 14, top, left, right, edge, rng)
    _chunk(g, 24, 13, 0.70, -19, top, left, right, edge, rng)

    # 大块顶面上一道高光。暗色矿石不加这一笔会糊成一团黑，
    # 但也不能太亮——亮到像抛光面就又回到锤那一挂了。
    g.append(dw.Lines(-13, -22, -4, -27, 2, -24, -7, -19,
                      close=True, fill="#ffffff", fill_opacity=gloss))

    # 脚边几粒碎渣，“敲下来的”那个意思
    for x, y, r in ((-31, 34, 2.2), (30, 33, 1.9), (1, 36, 1.6)):
        g.append(dw.Circle(x, y, r, fill=left, stroke=edge, stroke_width=1.2))

    d.append(g)

    return d


# 软锰矿 MnO₂：发乌的黑，带一点锰盐的紫调
def manganese_ore():
    return ore("#6a5875", "#4a3c58", "#332a40", "#1b1526")


# 铬铁矿 FeCr₂O₄：黑褐色，铁占一半，所以带锈调
def chromite_ore():
    return ore("#7a5e49", "#573f30", "#3d2b20", "#231811")


# 钒钛磁铁矿：本体就是磁铁矿，所以是**灰黑**不是蓝黑——
# 蓝留给钴，两种矿在 80px 下才分得开
def vanadium_ore():
    return ore("#626b74", "#454c54", "#2e343a", "#181c21")


# 水钴矿 CoO(OH)：黑底子，但把钴蓝提出来当主调。
# 现实里水钴矿是褐黑的，这里偏了一档：钴蓝是这种金属最好认的标签，
# 不借这一下，它和锰矿、钒钛磁铁矿在小图标下就是三团黑。
def cobalt_ore():
    return ore("#3f6497", "#2b466d", "#1d2f4a", "#101a2b")


# 锰：偏暖的粉灰。**不能画成紫**——锂块已经占了薰衣草那一档，
# 两块摆一起会分不清；锰本身也确实是带粉调的银灰，不是紫的
def manganese_ingot():
    return ingot("#e6dedd", "#b5a9a8", "#8e8281", "#575050", gloss=0.42)


# 铬：极亮的镜面银，微微偏冷绿——不锈钢表面那层铝膜的颜色。
# 高光拉到全场最高：铬本来就是拿来镀亮面的
def chromium_ingot():
    return ingot("#ffffff", "#c2d2ca", "#8fa298", "#4b5851", gloss=0.85)


# 钒：银白偏冷灰，几乎不带色——和钴块拉开距离
def vanadium_ingot():
    return ingot("#d9ddda", "#a2a9a4", "#767d79", "#464c49", gloss=0.35)


# 钴：银灰泛蓝，全场最蓝的一块锭
def cobalt_ingot():
    return ingot("#d2e0f6", "#94aed6", "#6b82ab", "#3f4e6d", gloss=0.55)


# ── 合金 ─────────────────────────────────────────────────
# 和纯金属同一个锭形，但顶面多一道**嵌条**，颜色取自次要组元。
# 这是个系统性的区分：一眼能看出「这是掺了东西的」，而且嵌条的颜色
# 直接告诉你掺的是什么（镀铬铜的铜底 + 银条最直白）。
#
# 不这么做的话，六种合金里有四种都是各种灰白的锭，80px 下根本分不开——
# 硬质合金那四个牌号靠刻印点数解决，这里靠嵌条。

def alloy(top, left, right, edge, accent, accent2=None, grade=0):
    """底色是主组元，嵌条是次要组元；grade > 0 时再在顶面刻上牌号点数。"""
    d = ingot(top, left, right, edge, gloss=0.45)

    def band(offset, color):
        # 平行于顶面右上棱的一条带，压在顶面中部
        return dw.Lines(-22 + offset, -19.5 + offset * 0.51,
                        16 + offset, 0.0 + offset * 0.51,
                        10 + offset, 3.2 + offset * 0.51,
                        -28 + offset, -16.0 + offset * 0.51,
                        close=True, fill=color, stroke=edge, stroke_width=1.0)

    if accent2 is None:
        d.append(band(4, accent))
    else:
        d.append(band(-6, accent))
        d.append(band(13, accent2))

    # 牌号刻印。和硬质合金那套一致：同一种合金的几个牌号形状色系都一样，
    # 靠数点区分——嵌条已经占了顶面中部，点排到下半区去，别互相压
    for i in range(grade):
        x = -11.0 * (grade - 1) / 2.0 + i * 11.0
        d.append(dw.Circle(x, 5.5 + x * 0.28, 2.6, fill="#f2f6fa", fill_opacity=0.92,
                           stroke=edge, stroke_width=0.8))

    return d


# 锰钢（高锰钢）：深钢灰带一点锰的粉调。受冲击会加工硬化，是最韧的一档
def manganese_steel(grade=0):
    return alloy("#8e8a96", "#666272", "#4a4753", "#2a2830", "#d8c9c8", grade=grade)


# 不锈钢：亮镜银，嵌条用铬的冷白——铬那层钝化膜才是它不锈的原因
def stainless_steel(grade=0):
    return alloy("#e9eeef", "#b6c0c2", "#8d979a", "#545c5e", "#f2fff8", grade=grade)


# 镀铬铜：铜底 + 银镀层，全场最直白的一个——底色和嵌条各说一半
def chrome_plated_copper(grade=0):
    return alloy("#d98c4e", "#a8632f", "#7d4720", "#47270f", "#eef4f6", grade=grade)


# 铬钒工具钢：深冷灰，**两条嵌条**（铬 + 钒），三元合金
def chrome_vanadium_steel(grade=0):
    return alloy("#7e8894", "#59616c", "#3e444d", "#22262c", "#e6efe9", "#b6c3bd", grade=grade)


# 钴铬合金：钴的蓝银底 + 铬的白条
def cobalt_chrome(grade=0):
    return alloy("#c6d6ea", "#93a6c0", "#6d7e97", "#3f4a5c", "#f4faf7", grade=grade)


# 钒钛合金：钛的暖灰底 + 钒的灰绿条。暖调是为了和另外几种冷银分开
def vanadium_titanium(grade=0):
    return alloy("#cfc9be", "#a09a90", "#78736b", "#46423c", "#b9c2bd", grade=grade)


# ── 硬质合金牌号 ─────────────────────────────────────────
# 同一种材料的四个牌号，形状必须一样（它们真是同一种东西），
# 靠两件事区分：**顶面的点数**（1~4 个，牌号），以及随牌号变冷变亮的色调
# （钴少 → 更接近碳化钨的黑灰、更亮的镜面；钴多 → 偏暖偏钝）。
#
# 点数是主信号。四块近乎同色的深色锭放在一起，光靠色调 80px 下分不出来，
# 数点才是可靠的——和现实里牌号靠刻印区分是一个道理。

def carbide_ingot(grade):
    """grade 取 1~4，1 最韧最钝、4 最硬最亮。"""
    t = (grade - 1) / 3.0

    def mix(lo, hi):
        return tuple(int(lo[i] + (hi[i] - lo[i]) * t) for i in range(3))

    def hexs(c):
        return "#%02x%02x%02x" % c

    top = hexs(mix((0x6b, 0x63, 0x5c), (0x5a, 0x60, 0x69)))
    left = hexs(mix((0x4a, 0x44, 0x3f), (0x3c, 0x41, 0x48)))
    right = hexs(mix((0x33, 0x2f, 0x2b), (0x28, 0x2c, 0x31)))
    edge = hexs(mix((0x1a, 0x17, 0x15), (0x12, 0x14, 0x18)))

    d = ingot(top, left, right, edge, gloss=0.35 + 0.35 * t)

    # 顶面刻印：排在顶面菱形的**下半区**，避开左上角那道高光——
    # 压在高光上会被白块吃掉一半，正是第一版翻的车
    pip = "#e8eef5"
    step = 11.0
    x0 = -step * (grade - 1) / 2.0

    for i in range(grade):
        x = x0 + i * step
        d.append(dw.Circle(x, -1 + x * 0.28, 2.8, fill=pip, fill_opacity=0.92,
                           stroke=edge, stroke_width=0.8))

    return d


# 铝：银白，冷调
def aluminum_ingot():
    return ingot("#eef2f6", "#b6c1cc", "#8e9aa6", "#5c6672")


# 锂：同样是银白的活泼金属，但带一点锂辉石那种紫调，好和铝块分得开。
# 高光压低一档——锂在空气里很快失去光泽，画得太亮反而不像。
def lithium_ingot():
    return ingot("#e6e0f4", "#b6abd0", "#8d82ab", "#544a73", gloss=0.4)


# ── 粉末 ─────────────────────────────────────────────────
# 一小堆粉末。第一版是画一条光滑的堆形轮廓再刷明暗，结果读起来是「一块」——
# 光滑剪影怎么调都像块状物。改成<b>用几千个颗粒垒出这堆</b>：
#   · 剪影的毛边是颗粒自己叠出来的，不用去修轮廓
#   · 明暗按颗粒在堆上的位置算，左上受光、右下背光，不再是硬边的色块
#   · 从后往前画（屏幕 y 从小到大），前排颗粒压住后排，堆才有厚度
# 随机数固定种子，保证每次生成的图完全一样。
#
# <b>lumps 是给「煅烧过的料」用的</b>：三氧化钨是从仲钨酸铵烧出来的酥饼，
# 不是自由流动的细粉。堆上压两块碎饼，一眼就能和硫矿粉分开——光靠调色不行，
# 两者都是黄的，80px 下色相差十几度根本读不出来。

def powder(dark, mid, bright, edge, lumps=(), seed=20260908):
    import random

    d = canvas()

    rng = random.Random(seed)

    # 堆的包络：底半宽 HALF、高 HEIGHT，越往上越窄
    HALF, HEIGHT, BASE = 34.0, 44.0, 22.0

    # 颗粒粗细。<b>改半径必须同步改数量</b>：铺满同样的堆面，需要的颗粒数按半径的
    # 平方反比涨——半径减半就得四倍的量，不然堆会变稀、露出底下的空隙。
    GRAIN_MIN, GRAIN_MAX, COUNT = 0.5, 1.2, 4500

    grains = []

    for _ in range(COUNT):
        # h: 0 在底、1 在尖。开方是为了让颗粒往底部聚，堆才是堆而不是柱
        h = 1.0 - (1.0 - rng.random()) ** 0.55
        half = HALF * (1.0 - h) ** 0.72

        x = rng.uniform(-half, half)
        y = BASE - h * HEIGHT + rng.uniform(-1.2, 1.2)

        grains.append((x, y, rng.uniform(GRAIN_MIN, GRAIN_MAX)))

    # 从后往前：屏幕上方的先画
    grains.sort(key=lambda g: g[1])

    for x, y, r in grains:
        d.append(dw.Circle(x, y, r, fill=grain_color(x, y, HALF, HEIGHT, BASE, dark, mid, bright),
                           stroke=edge, stroke_width=GRAIN_MAX * 0.12, stroke_opacity=0.22))

    # 碎饼：压在粉堆上，用矿石那套三面画法，配色比粉深一档才压得住
    if lumps:
        g = dw.Group()
        top = "#%02x%02x%02x" % mid
        left = "#%02x%02x%02x" % tuple(int(c * 0.72) for c in mid)
        right = "#%02x%02x%02x" % tuple(int(c * 0.52) for c in mid)

        for cx, cy, k, rot in lumps:
            _chunk(g, cx, cy, k, rot, top, left, right, edge, rng)

        d.append(g)

    # 堆脚边散落的几粒，「撒出来的粉」这个意思
    for x, y in ((-41, 25), (39, 24), (-31, 30), (27, 30),
                 (-36, 29), (33, 29), (8, 32), (-12, 33)):
        d.append(dw.Circle(x, y, GRAIN_MAX, fill=grain_color(x, y - 10, HALF, HEIGHT, BASE, dark, mid, bright),
                           stroke=edge, stroke_width=GRAIN_MAX * 0.2, stroke_opacity=0.5))

    return d


def grain_color(x, y, half, height, base, dark, mid, bright):
    """
    颗粒的颜色：左上受光最亮，右下背光最暗。

    <b>三段插值，不是两段。</b> 暗色直接线性插到亮色的话，中间那一大片会落在
    土黄和奶黄的连线上——蓝通道被抬起来，整堆看着发橄榄绿。中间钉一个饱和的本色，
    暗→本色→亮分两段走，主体才是黄的。
    """
    nx = x / half
    ny = (y - (base - height / 2)) / (height / 2)

    # 明暗跨度别拉太满：系数给大了背光侧会掉到褐色，一眼看去不像硫了
    t = 0.60 - 0.19 * nx - 0.26 * ny
    t = 0.0 if t < 0.0 else 1.0 if t > 1.0 else t

    lo, hi, k = (dark, mid, t * 2.0) if t < 0.5 else (mid, bright, (t - 0.5) * 2.0)

    return "#%02x%02x%02x" % tuple(int(lo[i] + (hi[i] - lo[i]) * k) for i in range(3))


# 硫矿粉：饱和的硫黄，偏橙金
def sulfur_powder():
    return powder((0x8a, 0x64, 0x04), (0xed, 0xc8, 0x1c), (0xff, 0xf5, 0x8f), "#6b5207")


# ── 钨链 ─────────────────────────────────────────────────

# 白钨矿 CaWO₄：奶油偏蜜的浅黄，半透明的晶体。
# **它是全场唯一的浅色矿石**——其余七种都是深色块，靠明度就先分开了。
def scheelite_ore():
    return ore("#f2e6bc", "#cfba89", "#a89263", "#6d5b36", gloss=0.5)


# 三氧化钨 WO₃：柠檬黄的煅烧粉，堆上压两块碎饼
def tungsten_trioxide():
    return powder((0x77, 0x74, 0x0a), (0xd6, 0xd6, 0x2c), (0xf2, 0xf5, 0xa2), "#585707",
                  lumps=((-13, 6, 0.42, -12), (14, 12, 0.34, 16)), seed=20260911)


# 钨块：**全场最暗的一块锭**。钨是粉末冶金压出来的，不是铸的，
# 表面发乌不反光——正好拿来和铬那块镜面白站在光谱的两端。
def tungsten_ingot():
    return ingot("#9aa3ad", "#6f7883", "#525a64", "#2f353c", gloss=0.3)


# 碳化钨 WC：近黑的陶瓷，但高光拉到很硬很窄——
# 「黑 + 尖锐镜面」是硬质材料的视觉签名，靠这个和暗色矿石区分
def tungsten_carbide():
    return ingot("#4d525a", "#33383f", "#23272c", "#0f1215", gloss=0.8)


# ── 电解水 ───────────────────────────────────────────────
# 一只电解槽：蓝色液面、两根电极、各自冒泡，顶上一道电弧。
# 左边出氢（浅色泡）、右边出氧（红色泡），和氧气图标的配色对得上。
# 80px 下细节会糊，所以只留「槽 + 两根电极 + 两串泡 + 电」这四个信号。

def water_electrolysis():
    d = canvas()

    # 内容从 y=-46 排到 +34，重心偏上、电弧还顶出画布。
    # 整体缩到 0.9 再往下挪，让上下留白对称——图标在格子里居中才不显得歪。
    g = dw.Group(transform="translate(0, 5.4) scale(0.9)")

    glass, liquid = "#8d9aa8", "#3d7fc4"
    cathode, anode = "#454c56", "#b5713a"

    # 液体：槽的下半部分，先画，压在槽壁下面
    body = dw.Path(fill=liquid)
    body.M(-32, -4)
    body.L(-32, 22)
    body.Q(-32, 30, -24, 30)
    body.L(24, 30)
    body.Q(32, 30, 32, 22)
    body.L(32, -4)
    body.Z()
    g.append(body)

    # 液面高光
    g.append(dw.Line(-30, -4, 30, -4, stroke="#8fc4f0", stroke_width=3))

    # 电极：从槽口伸进液面以下
    for x, color in ((-15, cathode), (15, anode)):
        g.append(dw.Rectangle(x - 4.5, -38, 9, 52, rx=3, fill=color,
                              stroke="#2b3038", stroke_width=2))

    # 气泡：左氢右氧，越往上越大
    for cy, r in ((16, 2.4), (8, 3.2), (0, 4.0)):
        g.append(dw.Circle(-24, cy, r, fill="#dceaf7", stroke="#7fa9cc", stroke_width=1))
        g.append(dw.Circle(24, cy, r, fill="#e8a49e", stroke="#a8564e", stroke_width=1))

    # 槽壁：开口的 U 形，画在最上面盖住液体的边
    wall = dw.Path(fill="none", stroke=glass, stroke_width=4,
                   stroke_linejoin="round", stroke_linecap="round")
    wall.M(-32, -20)
    wall.L(-32, 22)
    wall.Q(-32, 30, -24, 30)
    wall.L(24, 30)
    wall.Q(32, 30, 32, 22)
    wall.L(32, -20)
    g.append(wall)

    # 顶上的电弧，说明这是「电」解
    bolt = dw.Path(fill="#ffd642", stroke="#8a6608", stroke_width=1.6, stroke_linejoin="round")
    bolt.M(2, -46)
    bolt.L(-9, -30)
    bolt.L(-2, -30)
    bolt.L(-6, -16)
    bolt.L(9, -34)
    bolt.L(1, -34)
    bolt.Z()
    g.append(bolt)

    d.append(g)

    return d


# ── 巨型建筑 ─────────────────────────────────────
# 画的轮廓和 src/Model/MegaBuildingMeshes.cs 里那五只程序化模型一一对应：
# 图标和地上那座长一样，建造栏里认出来的就是实物。
#
# 用等距投影、三档明度，和上面的锭/矿是同一套语言。建筑比锭复杂得多，
# 所以多两条规矩：
#   · <b>近处压住远处</b>——先画后面的体块，前面的盖上去，层次才出得来
#   · <b>每座留一个独有母题</b>——塔的散热环、化工的罐群、加工的龙门、对撞的圆环。
#     80px 下颜色几乎分不出，<b>能认出来的只有轮廓</b>

def _shade(hex_color, k):
    """按系数提亮/压暗一个 #rrggbb。k>1 提亮，k<1 压暗。"""
    r = int(hex_color[1:3], 16)
    g = int(hex_color[3:5], 16)
    b = int(hex_color[5:7], 16)

    f = lambda v: max(0, min(255, int(v * k)))

    return "#%02x%02x%02x" % (f(r), f(g), f(b))


def _pal(base):
    """(顶面, 左面, 右面, 描边)。顶亮、左中、右暗，光从左上来。"""
    return _shade(base, 1.18), _shade(base, 0.82), _shade(base, 0.58), _shade(base, 0.34)


# 等距：顶面菱形的半高 / 半宽。和 ingot() 那块保持一致（19/37）
ISO = 0.51


def _prism(d, cx, cy, w, h, pal, gloss=0.0):
    """一个等距长方体。(cx, cy) 是顶面菱形的中心，w 是半宽，h 是往下的高度。"""
    top, left, right, edge = pal
    hh = w * ISO

    d.append(dw.Lines(cx, cy - hh, cx + w, cy, cx, cy + hh, cx - w, cy,
                      close=True, fill=top, stroke=edge, stroke_width=1.4, stroke_linejoin="round"))
    d.append(dw.Lines(cx - w, cy, cx, cy + hh, cx, cy + hh + h, cx - w, cy + h,
                      close=True, fill=left, stroke=edge, stroke_width=1.4, stroke_linejoin="round"))
    d.append(dw.Lines(cx, cy + hh, cx + w, cy, cx + w, cy + h, cx, cy + hh + h,
                      close=True, fill=right, stroke=edge, stroke_width=1.4, stroke_linejoin="round"))

    if gloss:
        d.append(dw.Lines(cx - w * 0.6, cy - hh * 0.1, cx - w * 0.1, cy - hh * 0.62,
                          cx + w * 0.12, cy - hh * 0.42, cx - w * 0.38, cy + hh * 0.1,
                          close=True, fill="#ffffff", fill_opacity=gloss))


def _cyl(d, cx, cy, r, h, pal, cap_gloss=0.0):
    """一只等距立罐：顶椭圆 + 筒身。(cx, cy) 是顶面椭圆的中心。"""
    top, left, right, edge = pal
    ry = r * ISO

    # 筒身先画，顶盖盖上去，接缝就藏住了
    d.append(dw.Path(fill=right, stroke=edge, stroke_width=1.4)
             .M(cx - r, cy).L(cx - r, cy + h)
             .A(r, ry, 0, 0, 0, cx + r, cy + h)
             .L(cx + r, cy).A(r, ry, 0, 0, 1, cx - r, cy).Z())
    d.append(dw.Path(fill=left, fill_opacity=0.55)
             .M(cx - r, cy).L(cx - r, cy + h)
             .A(r, ry, 0, 0, 0, cx - r * 0.1, cy + h + ry * 0.92)
             .L(cx - r * 0.1, cy + ry * 0.92).A(r, ry, 0, 0, 1, cx - r, cy).Z())
    d.append(dw.Ellipse(cx, cy, r, ry, fill=top, stroke=edge, stroke_width=1.4))

    if cap_gloss:
        d.append(dw.Ellipse(cx - r * 0.28, cy - ry * 0.22, r * 0.42, ry * 0.4,
                            fill="#ffffff", fill_opacity=cap_gloss))


def _ring(d, cx, cy, r, tube, pal):
    """一圈等距圆环（对撞机的母题）。"""
    top, left, right, edge = pal
    ry = r * ISO

    d.append(dw.Ellipse(cx, cy, r + tube, (r + tube) * ISO,
                        fill=right, stroke=edge, stroke_width=1.4))
    d.append(dw.Ellipse(cx, cy, r - tube, (r - tube) * ISO, fill="#000000", fill_opacity=0.0,
                        stroke=edge, stroke_width=1.4))
    d.append(dw.Ellipse(cx, cy - tube * 0.5, r + tube * 0.62, (r + tube * 0.62) * ISO,
                        fill=top, stroke=edge, stroke_width=1.2))
    d.append(dw.Ellipse(cx, cy - tube * 0.5, r - tube * 0.62, (r - tube * 0.62) * ISO,
                        fill=left, stroke=edge, stroke_width=1.2))


GLOW = "#bfe8ff"


def sky_assembler():
    """天工装配厂：三层收口台座 + 中央塔柱。"""
    d = canvas()
    p = _pal("#e3843b")
    dark = _pal("#8d5327")

    _prism(d, 0, 24, 38, 9, p)
    _prism(d, 0, 8, 29, 9, p)
    _prism(d, 0, -6, 20, 8, p, gloss=0.3)

    # 四角立柱：只画前两根，后两根会被台座挡住，画了也是噪点
    _prism(d, -26, 16, 5, 16, dark)
    _prism(d, 26, 16, 5, 16, dark)

    _cyl(d, 0, -28, 7, 16, dark, cap_gloss=0.35)
    d.append(dw.Lines(0, -40, 9, -34, 0, -28, -9, -34,
                      close=True, fill=_shade("#e3843b", 1.35), stroke=p[3], stroke_width=1.3))

    # 层沿的灯带：只描台面菱形的<b>前两条边</b>。
    # 描一整圈会横贯整张图、看着像杂散线框——灯带本来就只有朝向镜头那面看得见。
    for cy, w in ((8, 29), (-6, 20)):
        hh = w * ISO
        d.append(dw.Lines(-w, cy, 0, cy + hh, w, cy,
                          close=False, fill="none", stroke=GLOW, stroke_width=2.0,
                          stroke_linejoin="round", stroke_opacity=0.85))

    return d


def lysis_tower():
    """冶铸熔炉：细高塔 + 环形散热鳍。五座里唯一的竖向轮廓。"""
    d = canvas()
    p = _pal("#4f80f7")
    dark = _pal("#2b4a94")

    _prism(d, 0, 30, 34, 8, dark)
    _cyl(d, 0, -24, 13, 50, p, cap_gloss=0.32)

    # 六道散热环，越往上越小——塔的辨识度全在这儿
    for i in range(6):
        cy = -14 + i * 8
        r = 20 - i * 1.2
        d.append(dw.Ellipse(0, cy, r, r * ISO, fill=dark[1], stroke=dark[3], stroke_width=1.2))
        d.append(dw.Ellipse(0, cy - 1.6, r * 0.96, r * 0.96 * ISO, fill=p[0], stroke=dark[3],
                            stroke_width=1.0))

    d.append(dw.Ellipse(0, -24, 13, 13 * ISO, fill=p[0], stroke=p[3], stroke_width=1.4))
    d.append(dw.Lines(0, -42, 8, -36, 0, -30, -8, -36,
                      close=True, fill=_shade("#4f80f7", 1.4), stroke=p[3], stroke_width=1.3))

    return d


def chem_plant():
    """燔石化工厂：三只立罐 + 横管 + 细烟囱。"""
    d = canvas()
    p = _pal("#fcdb2b")
    dark = _pal("#9a8213")

    _prism(d, 0, 28, 38, 7, dark)

    _cyl(d, 14, -2, 10, 26, p, cap_gloss=0.3)        # 后排
    _cyl(d, -17, 4, 12, 24, p, cap_gloss=0.34)       # 左前
    _cyl(d, 4, 12, 11, 20, p, cap_gloss=0.3)         # 右前，压住后排

    # 横管：化工厂的母题。<b>两端要落在罐口上</b>——悬空的管子读起来是根飘着的棍子。
    d.append(dw.Line(-17, 4, -17, -10, stroke=dark[2], stroke_width=3.4, stroke_linecap="round"))
    d.append(dw.Line(14, -2, 14, -14, stroke=dark[2], stroke_width=3.4, stroke_linecap="round"))
    d.append(dw.Line(-17, -10, 14, -14, stroke=dark[2], stroke_width=5.0, stroke_linecap="round"))
    d.append(dw.Line(-17, -11.6, 14, -15.6, stroke=p[0], stroke_width=1.8, stroke_linecap="round"))

    # 细烟囱 + 顶部警示环
    _cyl(d, 28, -18, 4, 34, dark)
    d.append(dw.Ellipse(28, -18, 5, 5 * ISO, fill="#e8be28", stroke=dark[3], stroke_width=1.2))

    return d


def mega_assembler():
    """锤锻精工厂：低矮机身 + 龙门架。"""
    d = canvas()
    p = _pal("#d94a59")
    dark = _pal("#7d2833")

    _prism(d, 0, 22, 38, 12, p, gloss=0.26)
    _prism(d, 0, 6, 30, 8, p)

    # 龙门：两柱一梁
    _prism(d, -24, -10, 6, 26, dark)
    _prism(d, 24, -10, 6, 26, dark)
    _prism(d, 0, -20, 26, 6, _pal("#e8dfd0"))

    # 悬在梁下的主轴
    _prism(d, 0, -8, 6, 8, dark)

    return d


def particle_collider():
    """观微对撞机：圆环 + 中央靶室。五座里唯一的圆形轮廓。"""
    d = canvas()
    p = _pal("#6f55a8")
    dark = _pal("#3b2c5c")

    _prism(d, 0, 30, 38, 7, dark)

    _ring(d, 0, 0, 30, 6, p)

    # 环上四组磁铁
    for dx, dy in ((-30, 0), (30, 0), (0, -15), (0, 15)):
        _prism(d, dx, dy - 4, 5, 7, _pal("#8f74cf"))

    # 中央靶室压在环前面
    _cyl(d, 0, -6, 9, 18, p, cap_gloss=0.36)
    d.append(dw.Lines(0, -20, 11, -13, 0, -6, -11, -13,
                      close=True, fill=_shade("#6f55a8", 1.45), stroke=p[3], stroke_width=1.3))

    return d


def bio_greenhouse():
    """生物温室：等距穹顶 + 两只外挂培养罐。六座里唯一的穹顶轮廓。

    <b>轮廓要和别的五座在 80px 下一眼分开</b>——塔是竖的、对撞机是圆环、
    龙门是方框，穹顶是半圆，互相不撞。玻璃分格（三条经线 + 两条纬线）不是装饰：
    没有它，穹顶在小尺寸下就是一坨果冻，看不出是玻璃房。
    """
    d = canvas()
    p = _pal("#46b95c")
    dark = _pal("#1b5c2c")
    glass = "#93efb6"

    _prism(d, 0, 30, 38, 8, dark)

    # 穹顶：上半圆 + 前半椭圆封底，等距里的球顶就是这个形状
    d.append(dw.Path(fill=glass, fill_opacity=0.92, stroke=dark[3], stroke_width=1.8,
                     stroke_linejoin="round")
             .M(-30, 12).A(30, 30, 0, 0, 1, 30, 12)
             .A(30, 30 * ISO, 0, 0, 1, -30, 12).Z())

    # 经线：从顶点拉到三个前沿点
    for x, y in ((-30, 12), (0, 12 + 30 * ISO), (30, 12)):
        d.append(dw.Line(0, -18, x, y, stroke=dark[3], stroke_width=1.3, stroke_opacity=0.75))

    # 纬线：两条前半椭圆
    for t in (0.40, 0.72):
        r = 30 * (1 - t * t) ** 0.5
        d.append(dw.Path(fill="none", stroke=dark[3], stroke_width=1.2, stroke_opacity=0.6)
                 .M(-r, 12 - 30 * t).A(r, r * ISO, 0, 0, 0, r, 12 - 30 * t))

    # 顶部通风塔
    _cyl(d, 0, -26, 5, 9, _pal("#d7e6c4"))

    # 两只培养罐：压在穹顶前面，才看得出是挂在外侧的
    for dx in (-33, 33):
        _cyl(d, dx, 4, 8, 24, p, cap_gloss=0.3)
        d.append(dw.Rectangle(dx - 2, 10, 4, 14, fill=glass, fill_opacity=0.85))

    return d


def microbial_consortium():
    """菌落：培养皿里的藻菌滤饼。

    画皿不画分子：藻菌共培养物是一团生物量，没有结构式可画。
    <b>菌落团的位置写死不随机</b>——图标要可重现，换台机器生成出来不能不一样。
    """
    d = canvas()
    dish = _pal("#c2ced9")
    cake = "#1d4a2a"

    # 皿壁：先画筒身，顶面盖上去藏住接缝（和 _cyl 同一套做法）
    d.append(dw.Path(fill=dish[2], stroke=dish[3], stroke_width=1.6)
             .M(-36, 0).L(-36, 13).A(36, 36 * ISO, 0, 0, 0, 36, 13)
             .L(36, 0).A(36, 36 * ISO, 0, 0, 1, -36, 0).Z())

    d.append(dw.Ellipse(0, 0, 36, 36 * ISO, fill=cake, stroke=dish[3], stroke_width=1.6))

    # 菌落团：三档明度，凑出湿滤饼的团块感
    blobs = ((-14, -3, 9, "#2f7a42"), (7, -6, 7, "#3b9350"), (17, 2, 6, "#2a6b3a"),
             (-4, 5, 8, "#37874a"), (-24, 3, 5, "#245f33"), (2, -1, 4, "#5ec276"))

    for cx, cy, r, fill in blobs:
        d.append(dw.Ellipse(cx, cy, r, r * 0.72, fill=fill, stroke="#12331c", stroke_width=0.9))

    # 高光：湿的，不是干粉
    d.append(dw.Ellipse(-10, -6, 11, 4.4, fill="#ffffff", fill_opacity=0.16))

    return d


def algal_oil():
    """藻油：一滴单细胞油脂。

    不画结构式：甘油三酯是 57 个碳的大分子，缩到 80px 只剩一团糊。
    颜色定在<b>金绿</b>而不是原油那种琥珀，两张图摆一起要能分开。
    """
    d = canvas()
    edge = "#3d5c12"

    d.append(dw.Path(fill="#8fbe2f", stroke=edge, stroke_width=2.2, stroke_linejoin="round")
             .M(0, -40).C(15, -14, 30, 0, 30, 13)
             .A(30, 30, 0, 0, 1, -30, 13)
             .C(-30, 0, -15, -14, 0, -40).Z())

    # 底部压暗：给液滴一点体积
    d.append(dw.Ellipse(0, 21, 25, 11, fill="#5d861a", fill_opacity=0.5))

    # 悬在油里的藻细胞：说明它是藻榨出来的，不是矿物油
    for cx, cy, r in ((-9, 6, 4.2), (8, 12, 3.4), (0, -2, 2.8)):
        d.append(dw.Ellipse(cx, cy, r, r * 0.8, fill="#2f6b2a", fill_opacity=0.7))

    # 高光：液滴的标志，没有它读起来像块石头
    d.append(dw.Ellipse(-9, -12, 6, 9, fill="#ffffff", fill_opacity=0.5))

    return d


def vanadium_residue_oil():
    """钒渣油：一坨挂得住的黑渣，边缘和悬浮颗粒用钒的橙黄。

    <b>不能画成纯黑。</b> 它本来该是沥青那种黑，但 DSP 的界面底色就是深色——
    上一版把原油图标的明度乘 0.55，结果整格看不见了。所以这里的做法是
    <b>暗底 + 亮边 + 亮颗粒</b>：形体仍然读作「很黑很稠」，但轮廓和内容物是亮的。

    橙黄不是随便挑的：五氧化二钒（V₂O₅）就是橙黄色，而它正是这种燃料
    「能量最高但只能低温烧」的原因——熔点低于热通道温度，熔了直接腐蚀热端。
    图标上那几颗亮点画的就是它。

    形状上和藻油刻意分开：藻油是一滴轻快的泪滴，这个是<b>坠着的、底部摊开的稠块</b>，
    右下还挂一条将落未落的丝。两张图并排要一眼分得出「稀」和「稠」。
    """
    d = canvas()

    body = "#2b2334"          # 暗紫黑：留住「渣油」的黑，但不是纯黑
    body_hi = "#453a52"       # 顶部提亮，给一点体积
    rim = "#e59a1f"           # V₂O₅ 的橙黄，轮廓靠它读出来
    speck = "#f5bf47"

    # 主体：上窄下宽的稠块，底部摊开——和泪滴的「下圆上尖」相反
    d.append(dw.Path(fill=body, stroke=rim, stroke_width=2.6, stroke_linejoin="round")
             .M(0, -38).C(11, -16, 26, -4, 30, 10)
             .C(33, 22, 20, 30, 0, 30)
             .C(-20, 30, -33, 22, -30, 10)
             .C(-26, -4, -11, -16, 0, -38).Z())

    # 沿左缘的一道弧光。**不能画成左右对称的顶部提亮**：
    # 那样会在体内押出一个硬三角，整张图读成漏斗而不是稠液。
    # 弧光和下面那点白高光同一个光源（左上）。
    d.append(dw.Path(fill=body_hi, fill_opacity=0.75)
             .M(0, -36).C(-10, -17, -22, -5, -25, 6)
             .C(-17, 4, -9, -3, -3, -14)
             .C(-1, -22, 0, -30, 0, -36).Z())

    # 悬在渣里的 V₂O₅ 颗粒：这才是它「脏」的来源
    for cx, cy, r in ((-11, 9, 3.6), (7, 15, 2.9), (13, 2, 2.2), (-3, 19, 2.0)):
        d.append(dw.Circle(cx, cy, r, fill=speck, fill_opacity=0.92))
        d.append(dw.Circle(cx - r * 0.3, cy - r * 0.3, r * 0.4, fill="#fff0c4", fill_opacity=0.75))

    # 将落未落的一滴：说明它稠，倒不干净
    d.append(dw.Path(fill=body, stroke=rim, stroke_width=2.0, stroke_linejoin="round")
             .M(22, 27).C(26, 33, 27, 38, 24, 41)
             .C(21, 38, 20, 33, 22, 27).Z())

    # 高光：液体的标志，没有它读起来像块矿石
    d.append(dw.Ellipse(-11, -13, 4.6, 7.5, fill="#ffffff", fill_opacity=0.42))

    return d


def proliferator(glow, rim, shell, dense):
    """活性增产剂：一枚会发光的孢子囊。

    <b>刻意不画成原版那种喷漆罐。</b> 这一族是用活性复合材做的，图标语言跟着
    「活的」走：囊体 + 里面的发光核 + 菌丝。摆在原版三档旁边要一眼看出不是同一族。

    <b>同档两个变体靠形态分，不靠颜色分</b>——颜色是用来分档的（Mk.IV 青、Mk.V 紫），
    形态才是用来分性格的：

    <list type="bullet">
    <item><b>浓缩型</b>（dense=True）：囊窄壁厚，核大而集中，囊口封着。
    等级高、喷数少——一份的劲都压在里面。</item>
    <item><b>广延型</b>（dense=False）：囊宽壁薄，核小，菌丝从囊口散出去，末端结孢子。
    等级低、喷数多——一份铺得开。</item>
    </list>

    <b>亮边和亮核都是必须的。</b> DSP 的界面底色是深的，钒渣油那次就是因为整张图
    压得太暗，在格子里直接看不见。这一族本身是发光体，正好不犯那个毛病。
    """
    d = canvas()

    if dense:
        # 窄而挺：上端收成封死的囊口
        d.append(dw.Path(fill=shell, stroke=rim, stroke_width=2.8, stroke_linejoin="round")
                 .M(0, -40).C(13, -30, 20, -10, 20, 6)
                 .A(20, 22, 0, 0, 1, -20, 6)
                 .C(-20, -10, -13, -30, 0, -40).Z())

        # 核：大、居中偏下，占满囊腔
        d.append(dw.Ellipse(0, 6, 12.5, 14, fill=glow, fill_opacity=0.95))
        d.append(dw.Ellipse(0, 6, 7, 8, fill="#ffffff", fill_opacity=0.55))

        # 囊壁上的环纹：三道，紧
        for y, w in ((-22, 7.5), (-15, 10.5), (-8, 13)):
            d.append(dw.Line(-w, y, w, y, stroke=rim, stroke_width=2.0, stroke_opacity=0.75))
    else:
        # 宽而扁：上端敞开
        d.append(dw.Path(fill=shell, stroke=rim, stroke_width=2.6, stroke_linejoin="round")
                 .M(0, -26).C(18, -20, 27, -6, 27, 8)
                 .A(27, 20, 0, 0, 1, -27, 8)
                 .C(-27, -6, -18, -20, 0, -26).Z())

        # 核：小，说明劲被摊薄了
        d.append(dw.Ellipse(0, 8, 9, 9.5, fill=glow, fill_opacity=0.92))
        d.append(dw.Ellipse(0, 8, 4.5, 5, fill="#ffffff", fill_opacity=0.5))

        # 菌丝：从囊口散出去，末端各结一颗孢子
        for dx, dy, ex, ey in ((-3, -30, -26, -42), (-1, -31, -11, -46),
                               (1, -31, 9, -46), (3, -30, 24, -41)):
            d.append(dw.Path(fill="none", stroke=rim, stroke_width=2.4, stroke_linecap="round")
                     .M(dx * 3, -24).Q(dx * 6, dy, ex, ey))
            d.append(dw.Circle(ex, ey, 3.6, fill=glow, fill_opacity=0.95))
            d.append(dw.Circle(ex - 1.1, ey - 1.1, 1.5, fill="#ffffff", fill_opacity=0.7))

        # 囊壁环纹：两道，疏
        for y, w in ((-14, 14), (-6, 21)):
            d.append(dw.Line(-w, y, w, y, stroke=rim, stroke_width=1.9, stroke_opacity=0.6))

    return d


# Mk.IV 青，Mk.V 紫：色阶接着原版三档往上走，同族内部靠形态分
def proliferator_4_dense():
    return proliferator("#3fe0d0", "#7ff0e4", "#16383b", True)


def proliferator_4_spread():
    return proliferator("#3fe0d0", "#7ff0e4", "#16383b", False)


def proliferator_5_dense():
    return proliferator("#c56bff", "#e2aaff", "#2e1b47", True)


def proliferator_5_spread():
    return proliferator("#c56bff", "#e2aaff", "#2e1b47", False)


def moissanite_ore():
    """莫桑石：一簇带刻面的晶体，不是矿块。

    <b>形制上刻意不走 ore() 那条路。</b> 其它矿石都是圆钝的碎块（那个函数画的就是
    「敲下来的一堆石头」），而这一族的卖点是<b>硬</b>——所以画成尖锐的刻面晶体，
    边缘是直线不是弧线，在物品栏里和任何一块矿都不像。

    颜色取蓝绿：天然碳化硅从绿到蓝黑都有，而这个色在本 mod 的矿石里还没人占
    （钨黄、钴蓝、铬绿偏暗）。高光给得很硬——莫桑石的色散比金刚石还高，
    「火彩」是它最出名的特征，图标上就靠这几道白光表示。
    """
    d = canvas()

    body = "#1f6f6a"        # 主晶体：暗青
    face = "#3fb8ad"        # 亮刻面
    face2 = "#6fe0d4"       # 更亮的一面
    edge = "#0d3b38"

    # 主晶体：上尖下宽的六面柱，左右两个刻面亮度不同才有体积
    d.append(dw.Lines(0, -42, 20, -16, 15, 30, -15, 30, -20, -16,
                      close=True, fill=body, stroke=edge,
                      stroke_width=2.6, stroke_linejoin="round"))

    # 左刻面（迎光）
    d.append(dw.Lines(0, -42, -20, -16, -15, 30, -3, 30, -3, -30,
                      close=True, fill=face, fill_opacity=0.95))

    # 右上刻面
    # 收在腰棱（y = -13）以上：越过去会在晶体中部压出一个缺口
    d.append(dw.Lines(0, -42, 18, -14, 4, -14, 0, -30,
                      close=True, fill=face2, fill_opacity=0.85))

    # 腰棱：一条横向的亮线，刻面晶体的标志
    d.append(dw.Lines(-19, -13, 19, -13, stroke=face2,
                      stroke_width=2.2, stroke_opacity=0.8, fill="none"))

    # 旁边一颗小晶体：说明它是成簇产出的颗粒，不是单块大石
    d.append(dw.Lines(26, 6, 38, 18, 32, 32, 20, 26,
                      close=True, fill=body, stroke=edge,
                      stroke_width=2.2, stroke_linejoin="round"))
    d.append(dw.Lines(26, 6, 20, 26, 26, 29, 32, 14,
                      close=True, fill=face, fill_opacity=0.9))

    # 火彩：色散比金刚石还高，靠两道硬白光表示
    d.append(dw.Lines(-9, -26, -5, -8, stroke="#ffffff",
                      stroke_width=3.2, stroke_opacity=0.85, stroke_linecap="round", fill="none"))
    d.append(dw.Circle(9, -20, 3.2, fill="#ffffff", fill_opacity=0.9))

    return d


def drill_bit():
    """钻头：聚晶金刚石复合片（PDC）钻冠。

    <b>画的是真实构造。</b> PDC 钻头就是「碳化钨基体 + 表面镶的聚晶金刚石齿」——
    金刚石负责切削，基体负责撑住它并接钻杆。所以图上是一个灰钢色的冠体，
    上面嵌着几颗亮青白的切削齿。

    <b>形制和其它三族刻意分开：</b> 合金是等距锭块、活性复合材是抛光截面圆片、
    增产剂是发光孢子囊，这一族是<b>带齿的机械件</b>——有轴对称、有齿、有排屑槽，
    一眼看出是个工具而不是一块料。

    颜色取冷灰配青白齿：既不撞莫桑石那身蓝绿（那是矿石），也不撞增产剂的青紫。
    """
    d = canvas()

    body = "#5a6470"       # 碳化钨基体：冷灰
    body_hi = "#7d8794"
    body_lo = "#39424c"
    edge = "#222a33"
    tooth = "#cfeef0"      # 聚晶金刚石齿
    tooth_hi = "#ffffff"

    # 钻杆：上半截，比冠体细
    d.append(dw.Lines(-11, -42, 11, -42, 11, -14, -11, -14,
                      close=True, fill=body_lo, stroke=edge,
                      stroke_width=2.4, stroke_linejoin="round"))
    d.append(dw.Lines(-11, -42, -3, -42, -3, -14, -11, -14,
                      close=True, fill=body_hi, fill_opacity=0.55))

    # 冠体：下宽上窄的钻冠，底缘是齿所在的切削面
    d.append(dw.Path(fill=body, stroke=edge, stroke_width=2.6, stroke_linejoin="round")
             .M(-15, -16).L(15, -16)
             .C(27, -10, 31, 6, 28, 20)
             .L(-28, 20)
             .C(-31, 6, -27, -10, -15, -16).Z())

    # 左侧提亮：给冠体一点圆柱感
    d.append(dw.Path(fill=body_hi, fill_opacity=0.5)
             .M(-15, -16).C(-27, -10, -31, 6, -28, 20)
             .L(-16, 20).C(-18, 6, -14, -8, -6, -16).Z())

    # 排屑槽：两道竖直凹槽，钻头的标志性特征
    for x in (-8, 8):
        d.append(dw.Lines(x, -12, x, 18, stroke=body_lo,
                          stroke_width=3.4, stroke_opacity=0.85,
                          stroke_linecap="round", fill="none"))

    # 切削齿：底缘一排聚晶金刚石，中间两颗略高（钻冠是弧面）
    for cx, cy, r in ((-21, 17, 5.0), (-7, 21, 5.4), (7, 21, 5.4), (21, 17, 5.0)):
        d.append(dw.Circle(cx, cy, r, fill=tooth, stroke=edge, stroke_width=1.8))
        d.append(dw.Circle(cx - r * 0.28, cy - r * 0.3, r * 0.38,
                           fill=tooth_hi, fill_opacity=0.9))

    # 冠体上的高光：一道斜的，说明它是金属不是陶瓷
    d.append(dw.Lines(-19, -6, -14, 8, stroke=tooth_hi,
                      stroke_width=2.6, stroke_opacity=0.45,
                      stroke_linecap="round", fill="none"))

    return d


def photosynthesis():
    """光合育林：叶片 + 落在它上面的日光。

    这是配方图标，画的是<b>工艺</b>不是产物——它同时是全 mod 唯一一条
    「受光照影响」的配方，太阳必须在图上，否则这条规则在界面里毫无提示。
    """
    d = canvas()

    # 日轮在左上，被叶子压住一角
    d.append(dw.Circle(-20, -22, 13, fill="#ffd95e", stroke="#c98f16", stroke_width=1.6))

    for i in range(8):
        a = math.pi * 2 * i / 8
        d.append(dw.Line(-20 + math.cos(a) * 16, -22 + math.sin(a) * 16,
                         -20 + math.cos(a) * 22, -22 + math.sin(a) * 22,
                         stroke="#e8b029", stroke_width=2.6, stroke_linecap="round"))

    # 叶片：两段对称的贝塞尔，尖端朝右上
    d.append(dw.Path(fill="#4aa84f", stroke="#1f5c27", stroke_width=2.0, stroke_linejoin="round")
             .M(-26, 30).C(-26, -4, -2, -26, 30, -28)
             .C(28, 6, 6, 30, -26, 30).Z())

    # 主脉 + 侧脉：叶子的识别点
    d.append(dw.Path(fill="none", stroke="#1f5c27", stroke_width=2.0, stroke_linecap="round")
             .M(-26, 30).C(-8, 14, 8, 0, 29, -27))

    for t in (0.28, 0.5, 0.72):
        x0 = -26 + (29 + 26) * t
        y0 = 30 - (30 + 27) * t * 0.92
        d.append(dw.Line(x0, y0, x0 + 4, y0 - 13, stroke="#1f5c27", stroke_width=1.5,
                         stroke_linecap="round", stroke_opacity=0.8))

    # 叶面高光：光是从左上打过来的，和日轮位置对齐
    d.append(dw.Path(fill="#8fd894", fill_opacity=0.45)
             .M(-18, 22).C(-16, 2, 0, -14, 20, -20)
             .C(6, -6, -6, 8, -18, 22).Z())

    return d


# ── 活性复合材：抛光截面圆片 ──────────────────
#
# <b>形制刻意和合金分开。</b> 九种合金画的是等距锭块（alloy() / ingot()），
# 复合材画的是**金相试样那样的抛光截面**——一个圆片，把内部结构直接剖给你看。
# 两套东西在物品栏里并排时，一眼能分出"锭"和"料"。
#
# 四级的差别全在<b>网络连通度</b>上，这也正是它们四轴属性差异的来源：
#   I  散生   金属颗粒是孤岛，菌丝只在近处搭几根
#   II 渗流   出现第一条贯穿整片的通路（画成高亮的一条）
#   III 贯通  所有颗粒被连成一张网
#   IV 刚化   连接三角化、网眼填实，接近一块整料
#
# 颗粒位置用<b>固定种子</b>的伪随机生成：可重现，换台机器跑出来一模一样。

COMPOSITE_MATRIX = "#1e2f1d"   # 基体：暗绿，和菌落 / 藻油同一条色系
COMPOSITE_HYPHA = "#b8dcae"    # 菌丝：淡青绿

# 每一级用自己的填料合金，颜色跟着填料走——属性上界也是按填料定的
COMPOSITE_FILLER = {
    1: ("#9aa0b4", "#5d6274", "#c6cbdb"),   # 锰钢     钢灰带紫
    2: ("#dd8f4b", "#8c5120", "#f3bf87"),   # 镀铬铜   铜橙
    3: ("#a6bdd0", "#5b7387", "#d6e6f2"),   # 钴铬合金 冷蓝白
    4: ("#565b64", "#23262b", "#8b9099"),   # 硬质合金 近黑钨
}


def _grain_points(seed, count, radius):
    """圆片里的颗粒位置。固定种子 → 可重现，每一级用不同种子。"""
    rng = random.Random(seed)
    pts = []

    for _ in range(count * 8):
        if len(pts) >= count:
            break

        a = rng.uniform(0, math.pi * 2)
        # 开方是为了让点在圆面上均匀，不开方会全挤在圆心
        r = radius * math.sqrt(rng.uniform(0.02, 1.0))
        x, y = math.cos(a) * r, math.sin(a) * r

        # 最小间距，免得颗粒糊成一团
        if all((x - px) ** 2 + (y - py) ** 2 > 62 for px, py in pts):
            pts.append((x, y))

    return pts


def _grain(d, x, y, size, pal, rng):
    """一颗金属颗粒。用多边形不用圆——磨面上的晶粒是有棱角的。"""
    face, edge, hi = pal
    n = rng.choice((5, 6))
    pts = []

    for i in range(n):
        a = math.pi * 2 * i / n + rng.uniform(-0.25, 0.25)
        r = size * rng.uniform(0.72, 1.15)
        pts += [x + math.cos(a) * r, y + math.sin(a) * r]

    d.append(dw.Lines(*pts, close=True, fill=face, stroke=edge,
                      stroke_width=1.0, stroke_linejoin="round"))

    # 一小道高光，让它读起来是金属而不是石子
    d.append(dw.Lines(x - size * 0.45, y - size * 0.30,
                      x + size * 0.10, y - size * 0.62,
                      x + size * 0.30, y - size * 0.32,
                      close=True, fill=hi, fill_opacity=0.55))


def living_composite(grade):
    """活性复合材 I~IV：抛光截面圆片，四级只差一个网络连通度。"""
    d = canvas()
    pal = COMPOSITE_FILLER[grade]
    R = 40.0

    # 镶嵌环（金相试样的镶料），顺便把圆片和背景分开
    d.append(dw.Circle(0, 0, R, fill="#454b52", stroke="#23262b", stroke_width=2.0))
    d.append(dw.Circle(0, 0, R - 4.5, fill=COMPOSITE_MATRIX,
                       stroke="#101c10", stroke_width=1.6))

    inner = R - 8.0
    rng = random.Random(9000 + grade)
    pts = _grain_points(4200 + grade, (10, 13, 17, 22)[grade - 1], inner)

    # 菌丝网络：先画线，颗粒盖上去
    if grade == 1:
        # 散生：只在很近的邻居之间搭，连不成片
        links = [(i, j) for i in range(len(pts)) for j in range(i + 1, len(pts))
                 if (pts[i][0] - pts[j][0]) ** 2 + (pts[i][1] - pts[j][1]) ** 2 < 330]
    else:
        k = {2: 2, 3: 3, 4: 4}[grade]
        links = []

        for i in range(len(pts)):
            near = sorted(range(len(pts)),
                          key=lambda j: (pts[i][0] - pts[j][0]) ** 2
                          + (pts[i][1] - pts[j][1]) ** 2)[1:k + 1]
            links += [(i, j) for j in near]

    for i, j in links:
        (x1, y1), (x2, y2) = pts[i], pts[j]
        mx, my = (x1 + x2) / 2.0, (y1 + y2) / 2.0
        nx, ny = -(y2 - y1), (x2 - x1)
        L = max(1e-6, (nx * nx + ny * ny) ** 0.5)
        bow = 3.2 if grade < 4 else 1.2   # 菌丝不是直的，中点拱一下

        d.append(dw.Path(fill="none", stroke=COMPOSITE_HYPHA,
                         stroke_width=1.5 if grade < 3 else 2.0,
                         stroke_opacity=(0.28 + 0.16 * grade) if grade < 4 else 0.35,
                         stroke_linecap="round")
                 .M(x1, y1).Q(mx + nx / L * bow, my + ny / L * bow, x2, y2))

    # II 渗流：一条贯穿整片的通路——这一级的全部意义就在这条线上。
    #
    # <b>不能按 x 排序顺序连。</b> 第一版就是那么做的，结果线在圆片里上下乱窜，
    # 看着像一道闪电而不是一条路，还把铜颗粒全盖住了。改成贪心地向右走：
    # 每步只在"更靠右、且纵向跳得不远"的点里挑最近的一个。
    if grade == 2:
        cur = min(pts, key=lambda p: p[0])
        route, used = [cur], {cur}

        while True:
            cand = [p for p in pts
                    if p not in used and p[0] > cur[0] and abs(p[1] - cur[1]) < 22]

            if not cand:
                break

            cur = min(cand, key=lambda p: (p[0] - route[-1][0]) ** 2
                      + (p[1] - route[-1][1]) ** 2)
            route.append(cur)
            used.add(cur)

        path = (dw.Path(fill="none", stroke="#ffe08a", stroke_width=3.0,
                        stroke_opacity=0.92, stroke_linecap="round",
                        stroke_linejoin="round")
                .M(-inner - 5, route[0][1]))

        for x, y in route:
            path.L(x, y)

        path.L(inner + 5, route[-1][1])
        d.append(path)

    # IV 刚化：网眼填实成三角面，读起来就是"锁死了"
    if grade == 4:
        for i in range(len(pts) - 2):
            a, b, c = pts[i], pts[i + 1], pts[i + 2]

            if max((a[0] - b[0]) ** 2 + (a[1] - b[1]) ** 2,
                   (b[0] - c[0]) ** 2 + (b[1] - c[1]) ** 2) < 900:
                # 描边也上：只填色在 80px 下会糊成一片，边缘才是"三角化"的证据
                d.append(dw.Lines(a[0], a[1], b[0], b[1], c[0], c[1],
                                  close=True, fill=pal[2], fill_opacity=0.72,
                                  stroke=pal[1], stroke_width=1.1,
                                  stroke_linejoin="round"))

    for x, y in pts:
        _grain(d, x, y, (3.6, 3.9, 4.3, 4.8)[grade - 1], pal, rng)

    # 磨面高光：一道斜的亮弧，圆片才像"抛光过的"而不是一张贴纸
    d.append(dw.Path(fill="#ffffff", fill_opacity=0.10)
             .M(-R + 8, -R + 17).A(R - 6, R - 6, 0, 0, 1, R - 19, -R + 7)
             .A(R - 6, R - 6, 0, 0, 0, -R + 8, -R + 17).Z())

    # 等级刻痕：镶嵌环上 1~4 道。80px 下这是最可靠的区分手段
    for i in range(grade):
        a = math.radians(-66 + i * 15)
        d.append(dw.Line(math.cos(a) * (R - 4.6), math.sin(a) * (R - 4.6),
                         math.cos(a) * (R + 0.6), math.sin(a) * (R + 0.6),
                         stroke="#ffe08a", stroke_width=3.4, stroke_linecap="round"))

    return d


def mycelial_matrix():
    """菌丝基体：一团聚酯化的菌丝絮。

    <b>不做成圆片</b>——它是原料不是成品，形制要和四级复合材分开：
    软塌塌的一团，边缘散着毛丝。
    """
    d = canvas()
    body = "#d8e6c2"
    edge = "#6f8a52"
    rng = random.Random(7331)

    # 外围散出去的菌丝，先画，被主体压住一半
    for i in range(26):
        a = math.pi * 2 * i / 26 + rng.uniform(-0.08, 0.08)
        r0 = 26 + rng.uniform(-3, 3)
        r1 = r0 + rng.uniform(8, 17)

        d.append(dw.Line(math.cos(a) * r0, math.sin(a) * r0 * 0.82,
                         math.cos(a) * r1, math.sin(a) * r1 * 0.82,
                         stroke="#a8c489", stroke_width=1.7,
                         stroke_opacity=0.85, stroke_linecap="round"))

    # 主体：一团不规则的絮，不是正圆
    pts = []

    for i in range(14):
        a = math.pi * 2 * i / 14
        r = 28 + rng.uniform(-4.5, 4.5)
        pts += [math.cos(a) * r, math.sin(a) * r * 0.84]

    d.append(dw.Lines(*pts, close=True, fill=body, stroke=edge,
                      stroke_width=2.2, stroke_linejoin="round"))

    # 内部纹理：菌丝的走向
    for rr, op in ((20, 0.55), (13, 0.45), (7, 0.35)):
        d.append(dw.Ellipse(0, 0, rr, rr * 0.84, fill="none",
                            stroke="#8fae6f", stroke_width=1.6, stroke_opacity=op))

    # 聚酯颗粒：细胞里堆起来的那些油滴状内含物，这是"基体"而非"菌丝"的部分
    for cx, cy, rr in ((-9, -5, 4.2), (6, -9, 3.4), (10, 4, 3.8),
                       (-3, 8, 3.0), (0, -1, 2.4)):
        d.append(dw.Ellipse(cx, cy, rr, rr * 0.86, fill="#f2f7e4",
                            stroke=edge, stroke_width=1.0, stroke_opacity=0.6))

    d.append(dw.Ellipse(-10, -12, 9, 5, fill="#ffffff", fill_opacity=0.28))

    return d


def tab_mega():
    """建造栏的「巨型建筑」页签图标。

    页签图标和物品图标不是一回事：它常年显示在建造栏上、尺寸更小，
    <b>不能有颜色</b>（会和相邻页签打架），也不能有细节（缩到十几像素全糊）。
    所以只保留最抽象的母题——层层收口的巨构剪影，单色描边。
    """
    d = canvas()
    line = "#e8eef6"
    fill_hi = "#cdd8e6"
    fill_lo = "#9aa8bb"

    for cy, w in ((26, 36), (8, 26), (-9, 16)):
        hh = w * ISO
        d.append(dw.Lines(cy and -w or -w, cy, 0, cy + hh, w, cy, 0, cy - hh,
                          close=True, fill=fill_hi, stroke=line, stroke_width=2.0,
                          stroke_linejoin="round"))
        d.append(dw.Lines(-w, cy, 0, cy + hh, 0, cy + hh + 10, -w, cy + 10,
                          close=True, fill=fill_lo, stroke=line, stroke_width=2.0,
                          stroke_linejoin="round"))
        d.append(dw.Lines(0, cy + hh, w, cy, w, cy + 10, 0, cy + hh + 10,
                          close=True, fill=fill_lo, stroke=line, stroke_width=2.0,
                          stroke_linejoin="round"))

    d.append(dw.Lines(0, -34, 7, -29, 0, -24, -7, -29,
                      close=True, fill=fill_hi, stroke=line, stroke_width=2.0,
                      stroke_linejoin="round"))

    return d



def sic_wafer():
    """高纯碳化硅：一片带定位边的晶圆。

    <b>颜色是真的。</b> 掺氮的 n 型 4H-SiC 晶圆本身就是**透绿到琥珀绿**的——
    半绝缘的那种才接近黑灰。这一族的卖点正是「掺了氮所以导电」，
    所以颜色直接把那件事画出来，而不是随便挑一个好看的。

    <b>形制上要和活性复合材那族分开。</b> 那族也是圆片（抛光截面），
    区别靠两样：这里有**定位边**（晶圆那条直边，真实存在，用来标晶向），
    以及一道很硬的镜面高光——晶圆是抛光到原子级的，复合材截面不是。
    """
    d = canvas()

    body = "#4e7a35"        # 掺氮 4H-SiC：透绿
    body_hi = "#87b95f"
    body_lo = "#2f4d20"
    edge = "#1b2c13"
    sheen = "#d8f2b8"

    # 晶圆主体：一个带定位边的圆。定位边切在左下
    d.append(dw.Path(fill=body, stroke=edge, stroke_width=2.6, stroke_linejoin="round")
             .M(-36, 6).A(38, 38, 0, 1, 1, -14, 34).L(-36, 6).Z())

    # 上缘受光
    d.append(dw.Path(fill=body_hi, fill_opacity=0.55)
             .M(-30, -14).A(34, 34, 0, 0, 1, 26, -22).L(18, -8)
             .A(24, 24, 0, 0, 0, -22, -2).Z())

    # 下缘暗部
    d.append(dw.Path(fill=body_lo, fill_opacity=0.5)
             .M(-13, 31).A(34, 34, 0, 0, 0, 31, 12).L(19, 8)
             .A(22, 22, 0, 0, 1, -9, 20).Z())

    # 镜面高光：一道斜扫，抛光晶圆的标志
    d.append(dw.Lines(-20, -26, -6, -30, 22, 14, 8, 18,
                      close=True, fill=sheen, fill_opacity=0.5))
    d.append(dw.Lines(2, -30, 9, -31, 31, 2, 24, 4,
                      close=True, fill=sheen, fill_opacity=0.32))

    # 定位边加一道亮线，免得在小尺寸下看不出那是条直边
    d.append(dw.Lines(-36, 6, -14, 34, stroke=sheen, stroke_width=2.2,
                      stroke_opacity=0.75, fill="none"))

    return d


def aluminium_nitride():
    """氮化铝：一块覆铜的陶瓷基板。

    <b>画的是它在功率模块里的真实样子</b>——DBC（直接覆铜）基板：
    白色氮化铝陶瓷片，上下两面各覆一层铜箔，铜面还蚀刻出线路岛。
    单画一块白瓷片会和任何「白色方块」撞脸，而覆铜这一层既是真的，
    又一眼说明它是干什么用的。

    白瓷色在本 mod 的调色板里没人占——矿石金属都有色相，
    这块是唯一的近白色，所以在物品栏里很好认。
    """
    d = canvas()

    ceramic = "#e6e2d8"     # 氮化铝陶瓷：近白微暖
    ceramic_hi = "#ffffff"
    ceramic_lo = "#b3aea1"
    edge = "#4a473f"
    copper = "#c8763a"
    copper_hi = "#efa869"
    copper_lo = "#8a4c22"

    # 陶瓷片主体：一块略带透视的薄板
    d.append(dw.Lines(-38, -12, 0, -30, 38, -12, 0, 6,
                      close=True, fill=ceramic, stroke=edge,
                      stroke_width=2.4, stroke_linejoin="round"))
    # 厚度：前侧面
    d.append(dw.Lines(-38, -12, 0, 6, 0, 18, -38, 0,
                      close=True, fill=ceramic_lo, stroke=edge,
                      stroke_width=2.4, stroke_linejoin="round"))
    d.append(dw.Lines(38, -12, 0, 6, 0, 18, 38, 0,
                      close=True, fill=ceramic_lo, stroke=edge,
                      stroke_width=2.4, stroke_linejoin="round"))
    d.append(dw.Lines(-38, -12, 0, -30, 0, -24, -38, -6,
                      close=True, fill=ceramic_hi, fill_opacity=0.7))

    # 覆铜层：顶面上蚀刻出的两块线路岛
    d.append(dw.Lines(-22, -12, -4, -20, 8, -14, -10, -6,
                      close=True, fill=copper, stroke=copper_lo, stroke_width=1.6))
    d.append(dw.Lines(-2, -19, 12, -26, 26, -19, 12, -12,
                      close=True, fill=copper, stroke=copper_lo, stroke_width=1.6))
    d.append(dw.Lines(-22, -12, -4, -20, -1, -18.5, -19, -10.5,
                      close=True, fill=copper_hi, fill_opacity=0.6))

    return d


def sic_power_module():
    """碳化硅功率模块：黑封装 ＋ 铜排。

    <b>真实的功率模块就长这样</b>：黑色环氧灌封的方砖，
    两侧伸出铜排端子（直流母排与交流输出），顶面有安装螺孔。
    画成「器件」而不是「一块料」是刻意的——这一族的定位是成品器件，
    和矿石、锭块、陶瓷片都要一眼分开。

    黑配铜在本 mod 里也没人占（钒渣油是深色但带油光，不是这种哑光黑塑）。
    """
    d = canvas()

    case = "#2b2f36"        # 环氧封装：哑光黑
    case_hi = "#4a515c"
    case_lo = "#16191e"
    edge = "#0c0e12"
    copper = "#c8763a"
    copper_hi = "#efa869"
    copper_lo = "#7d4520"
    mark = "#6f7a88"

    # 铜排：先画，让封装压在上面
    for x in (-40, 18):
        d.append(dw.Lines(x, -8, x + 22, -8, x + 22, 6, x, 6,
                          close=True, fill=copper, stroke=copper_lo,
                          stroke_width=2.0, stroke_linejoin="round"))
        d.append(dw.Lines(x, -8, x + 22, -8, x + 22, -4, x, -4,
                          close=True, fill=copper_hi, fill_opacity=0.65))

    # 封装本体
    d.append(dw.Lines(-26, -22, 26, -22, 26, 18, -26, 18,
                      close=True, fill=case, stroke=edge,
                      stroke_width=2.6, stroke_linejoin="round"))
    d.append(dw.Lines(-26, -22, 26, -22, 26, -15, -26, -15,
                      close=True, fill=case_hi, fill_opacity=0.5))
    d.append(dw.Lines(-26, 11, 26, 11, 26, 18, -26, 18,
                      close=True, fill=case_lo, fill_opacity=0.6))

    # 安装螺孔：两个角
    for cx in (-19, 19):
        d.append(dw.Circle(cx, -16, 3.2, fill=case_lo, stroke=mark, stroke_width=1.4))

    # 顶面丝印：三条，像功率模块外壳上的型号标
    for i, w in enumerate((18, 24, 12)):
        d.append(dw.Lines(-w / 2, 0 + i * 6, w / 2, 0 + i * 6,
                          stroke=mark, stroke_width=2.0,
                          stroke_opacity=0.55, fill="none"))

    return d


def _blob(pts, close_path=None, **kw):
    """把一圈点变成平滑的闭合轮廓（Catmull-Rom 转三次贝塞尔）。

    手写 .C() 控制点画有机形状太容易画出硬角——上一版岩浆的底缘就是这么
    变成云朵的。给一圈点、让曲线自己插出来，形状就只由点位决定。
    """
    n = len(pts)
    p = dw.Path(**kw).M(*pts[0])

    for i in range(n):
        p0, p1, p2, p3 = pts[(i - 1) % n], pts[i], pts[(i + 1) % n], pts[(i + 2) % n]
        c1 = (p1[0] + (p2[0] - p0[0]) / 6.0, p1[1] + (p2[1] - p0[1]) / 6.0)
        c2 = (p2[0] - (p3[0] - p1[0]) / 6.0, p2[1] - (p3[1] - p1[1]) / 6.0)
        p.C(c1[0], c1[1], c2[0], c2[1], p2[0], p2[1])

    return p.Z()


def _scaled(pts, k, dx=0.0, dy=0.0):
    return [(x * k + dx, y * k + dy) for x, y in pts]


# 岩浆的轮廓点：上半是圆穹，下半在 y=11/20 之间来回，插出三个浅流舌。
# 舌要浅——做成半圆会读成云朵，这是上一版的教训。
LAVA_OUTLINE = [
    (0, -34), (19, -29), (30, -14), (33, 2),
    (26, 15), (17, 9), (8, 19), (-1, 11), (-10, 19), (-19, 9), (-26, 15),
    (-33, 2), (-30, -14), (-19, -29),
]


def _blob(pts, **kw):
    """把一圈点变成平滑的闭合轮廓（Catmull-Rom 转三次贝塞尔）。

    手写 .C() 控制点画有机形状太容易画出硬角；给一圈点、让曲线自己插出来，
    形状就只由点位决定。<b>但点位不能带周期性</b>——上下交替的一圈点会被
    样条冲成一排尖齿（岩浆的底缘这么画过，整张图读成了南瓜灯）。
    要不规则，就让每个点各不相同，别让它有节奏。
    """
    n = len(pts)
    p = dw.Path(**kw).M(*pts[0])

    for i in range(n):
        p0, p1, p2, p3 = pts[(i - 1) % n], pts[i], pts[(i + 1) % n], pts[(i + 2) % n]
        p.C(p1[0] + (p2[0] - p0[0]) / 6.0, p1[1] + (p2[1] - p0[1]) / 6.0,
            p2[0] - (p3[0] - p1[0]) / 6.0, p2[1] - (p3[1] - p1[1]) / 6.0,
            p2[0], p2[1])

    return p.Z()


def _scaled(pts, k, dx=0.0, dy=0.0):
    return [(x * k + dx, y * k + dy) for x, y in pts]


# 岩浆的轮廓：矮而宽、左右不对称的一摊。
# 和藻油那滴泪（高、尖、对称）、钒渣油那坨稠块（高、底部摊开）都拉开了形体。
LAVA_OUTLINE = [
    (2, -31), (20, -26), (31, -12), (33, 5),
    (23, 19), (6, 25), (-12, 24), (-26, 15),
    (-33, 1), (-28, -16), (-14, -28),
]


def _blob(pts, **kw):
    """把一圈点变成平滑的闭合轮廓（Catmull-Rom 转三次贝塞尔）。

    手写 .C() 控制点画有机形状太容易画出硬角；给一圈点、让曲线自己插出来，
    形状就只由点位决定。<b>但点位不能带周期性</b>——上下交替的一圈点会被
    样条冲成一排尖齿（岩浆的底缘这么画过，整张图读成了南瓜灯）。
    要不规则，就让每个点各不相同，别让它有节奏。
    """
    n = len(pts)
    p = dw.Path(**kw).M(*pts[0])

    for i in range(n):
        p0, p1, p2, p3 = pts[(i - 1) % n], pts[i], pts[(i + 1) % n], pts[(i + 2) % n]
        p.C(p1[0] + (p2[0] - p0[0]) / 6.0, p1[1] + (p2[1] - p0[1]) / 6.0,
            p2[0] - (p3[0] - p1[0]) / 6.0, p2[1] - (p3[1] - p1[1]) / 6.0,
            p2[0], p2[1])

    return p.Z()


def _scaled(pts, k, dx=0.0, dy=0.0):
    return [(x * k + dx, y * k + dy) for x, y in pts]


# 岩浆的轮廓：矮而宽、左右不对称的一摊。
# 和藻油那滴泪（高、尖、对称）、钒渣油那坨稠块（高、底部摊开）都拉开了形体。
LAVA_OUTLINE = [
    (2, -31), (20, -26), (31, -12), (33, 5),
    (23, 19), (6, 25), (-12, 24), (-26, 15),
    (-33, 1), (-28, -16), (-14, -28),
]


def lava_cooler():
    """熔岩冷却厂：敞口熔池 + 四座冷却塔 + 悬在池面上的粒化环。

    <b>七座里唯一把发光面露在外头的。</b> 别的六座要么是实心塔、要么把光关在
    玻璃穹顶里；这一座的身份就是「中间那口烫的池子」。80px 下剩不下别的，
    也正好和它们都不撞。

    <b>粒化环不能用 _ring() 画。</b> 那个辅助函数画的其实是一张<b>实心盘</b>
    （外椭圆填色、内椭圆只描边，SVG 不会把中间挖掉），对撞机能用是因为中央靶室
    压在它前面。这里环悬在池面<b>上方</b>，用 _ring() 会把整口池子盖掉——
    实际画出来才看见的。所以这里用一条只描边、不填充的椭圆，池面才透得出来。

    <b>四座塔只画得出两座半。</b> 等距视角下后两座被池子挡掉大半，照实画反而对；
    硬把四座摆全会挤成一圈栅栏，中间那口池子就读不出来了。后两座先画、压暗一档。

    颜色照建筑的 tint 走（冷玄武岩的炭褐），池面用橙黄——
    **冷与热的对比就是这张图的全部信息**，别的都可以糊掉。
    """
    d = canvas()
    p = _pal("#6b5347")          # 冷玄武岩：池壁与机身
    dark = _pal("#3a2c25")       # 底盘
    tower = _pal("#8f7362")      # 塔身比池壁亮一档，免得糊成一团
    tower_bk = _pal("#54423a")   # 后两座压暗
    melt = "#ff9d1c"
    melt_hi = "#ffdc63"
    ring = "#d98b2b"

    _prism(d, 0, 32, 38, 7, dark)

    # 后面两座塔：先画，下半截被池子挡掉
    for cx in (-23, 23):
        _cyl(d, cx, -18, 8, 24, tower_bk)

    # 熔池：池壁 + 两层池面（外圈橙、中心更亮，读作「中间最烫」）
    _cyl(d, 0, 10, 24, 13, p)
    d.append(dw.Ellipse(0, 10, 19, 19 * ISO, fill=melt, stroke=p[3], stroke_width=1.4))
    d.append(dw.Ellipse(0, 8, 10, 10 * ISO, fill=melt_hi))

    # 粒化环：只描边，池面要从中间透出来
    d.append(dw.Ellipse(0, -3, 17, 17 * ISO, fill="none", stroke=ring, stroke_width=4.2))
    d.append(dw.Ellipse(0, -4, 17, 17 * ISO, fill="none", stroke=_shade(ring, 1.35), stroke_width=1.6))

    # 前面两座塔：压在池沿上，塔口外扩——冷却塔的标志
    for cx in (-29, 29):
        _cyl(d, cx, 2, 9, 30, tower)
        d.append(dw.Ellipse(cx, 2, 11, 11 * ISO, fill=tower[0], stroke=tower[3], stroke_width=1.4))

        # 翅片：三道竖线，说明它散热而不是储液
        for k in (-4.5, 0, 4.5):
            d.append(dw.Lines(cx + k, 8, cx + k, 29,
                              stroke=tower[3], stroke_width=1.3, stroke_opacity=0.6, fill="none"))

    return d


def lava():
    """岩浆：一摊正在淌的硅酸盐熔体，表面浮着裂开的玄武岩结壳。

    <b>配色和钒渣油刻意互为反面。</b> 渣油是「暗底 + 亮边 + 亮颗粒」，
    这张是<b>亮底 + 暗结壳</b>——两种都解决 DSP 深色界面的可见性问题，
    但读起来一个是「很黑很稠」，一个是「烫得发光」，并排摆不会混。

    <b>没有白色高光</b>，这和本文件里其他液体不一样，是故意的：
    岩浆自己发光，打一块镜面高光在物理上就是错的，看着也像一滩果汁。
    「这是液体」靠淌出来的那一滴说，以及矮而宽的摊开形体。

    <b>热层是同一条轮廓缩小后重画的，不是椭圆。</b> 早先用椭圆铺内层，
    边缘和外形对不上，整张读成「橙色身子上浮着一个黄球」。按轮廓缩放之后
    亮色一路贴着边收进去，才是熔体从边缘往心部升温的样子。

    <b>结壳画成「裂开的一整片」，不是散落的几块。</b> 均匀散开的深色多边形
    会在圆形身子上凑成一张脸（上两块当眼睛、下面几块当嘴）——这是实际画出来
    才看见的。现在是左半边一整片壳裂成三瓣、右边浮一小块孤岛，相邻瓣之间
    透出的亮缝才是熔岩流最好认的特征。缝是留出来的空隙，不是描上去的亮线。
    """
    d = canvas()

    deep = "#6f1a06"       # 最凉的边，暗红
    mid = "#cf4410"        # 橙红
    hot = "#f2901c"        # 橙
    core = "#ffd75a"       # 亮黄心
    crust = "#241f1c"      # 玄武岩结壳
    crust_hi = "#574b41"   # 壳的受光面

    # 淌出的一滴：先画，让主体压住它上半截，看着才是从边上流下去的
    d.append(dw.Path(fill=hot, stroke=deep, stroke_width=2.2, stroke_linejoin="round")
             .M(7, 10).C(14, 22, 15, 33, 10, 41)
             .C(4, 33, 1, 22, 7, 10).Z())

    d.append(_blob(LAVA_OUTLINE, fill=mid, stroke=deep,
                   stroke_width=2.4, stroke_linejoin="round"))

    # 热层：按轮廓缩放，只在外圈留一道橙红的边
    d.append(_blob(_scaled(LAVA_OUTLINE, 0.82, -1, -2), fill=hot))
    d.append(_blob(_scaled(LAVA_OUTLINE, 0.58, -2, -3), fill=core, fill_opacity=0.95))

    # 结壳：左半边一整片裂成三瓣（相邻、共缝），右边一小块孤岛
    plates = (
        ((-27, -9), (-17, -15), (-12, -6), (-22, -1)),
        ((-15, -17), (-5, -21), (-1, -12), (-11, -8)),
        ((-20, 4), (-10, 1), (-7, 9), (-17, 13)),
        ((16, -4), (25, -2), (24, 8), (15, 6)),
    )

    for pts in plates:
        d.append(dw.Lines(*[c for p in pts for c in p], close=True,
                          fill=crust, stroke=deep, stroke_width=1.0,
                          stroke_linejoin="round"))
        # 受光面：沿板的上缘压一条窄边，给壳一点厚度
        top = sorted(pts, key=lambda p: p[1])[:2] + [pts[-1]]
        d.append(dw.Lines(*[c for p in top for c in p], close=True,
                          fill=crust_hi, fill_opacity=0.5))

    # 溅起的火星：挨在一起，散开摆会变成两只眼睛
    for cx, cy, r in ((-20, -31, 2.6), (-11, -35, 1.9)):
        d.append(dw.Circle(cx, cy, r, fill=core))

    return d


# ── 丙烯 C₃H₆ ────────────────────────────────────────────
# CH₂=CH–CH₃。骨架走左下→右上的对角线（和甲醇同一条理由：小尺寸下比横平竖直好认），
# 双键落在左端那一段。**六个氢是这张图最难的地方**——乙烯只有四个，多两个就容易把
# 三个碳糊掉。所以六个氢一律朝外扇开，碳链周围一个氢都不留，链子才读得出来是三节。
#
# <b>双键那一段要画得比单键长。</b> 第一版三个碳等距，结果碳碳之间只剩十来个单位
# 露在球外面，两条平行线挤成了一根粗棍——双键就这么没了。原子半径是固定的 16，
# 所以「双键看不看得见」取决于**键长减去两个半径**还剩多少，不是取决于线宽。
def propylene():
    return _molecule(
        [(-54, 30, -6, 4, 2),                       # C1=C2，拉长到 54.6
         (-6, 4, 34, -20),                          # C2—C3
         (-54, 30, -88, 8), (-54, 30, -66, 66),     # C1 的两个氢
         (-6, 4, 6, 46),                            # C2 的一个氢
         (34, -20, 22, -58), (34, -20, 68, -44), (34, -20, 62, 14)],
        [("H", -88, 8), ("H", -66, 66), ("H", 6, 46),
         ("H", 22, -58), ("H", 68, -44), ("H", 62, 14),
         ("C", -54, 30), ("C", -6, 4), ("C", 34, -20)])


# ── 沸石催化剂 / 待生沸石催化剂 ──────────────────────────
# 这两张是**一对**：同一个轮廓、同一处孔位，只有明度和孔里装的东西相反。
# 玩家要一眼看出「是同一样东西的两个状态」而不是两样东西——这是整条
# 反应—再生环里唯一需要靠图标讲清楚的事，别的都能靠文字。

def _iso_face(cx, cy, w, a, b):
    """等距顶面（菱形）上按 (a, b) ∈ [0,1]² 取一点。左顶点当原点，两条棱各是一轴。"""
    hh = w * ISO

    return cx - w + (a + b) * w, cy + (b - a) * hh


def _pore(d, cx, cy, s, fill, edge, gloss=None):
    """顶面上的一个孔（小菱形，躺在顶面那个平面里）。"""
    d.append(dw.Lines(cx, cy - s * ISO, cx + s, cy, cx, cy + s * ISO, cx - s, cy,
                      close=True, fill=fill, stroke=edge, stroke_width=1.0, stroke_linejoin="round"))

    if gloss:
        # 孔口内壁的一道反光：没有它，深色菱形会读成「贴上去的黑片」而不是「洞」
        d.append(dw.Lines(cx, cy - s * ISO * 0.52, cx + s * 0.48, cy - s * ISO * 0.04,
                          cx, cy + s * ISO * 0.12, cx - s * 0.48, cy - s * ISO * 0.04,
                          close=True, fill=gloss, fill_opacity=0.5))


def _sieve(body, pore_fill, pore_edge, pore_gloss, side_pore, lumps=()):
    """分子筛块体：等距方块 + 顶面 3×3 孔阵 + 侧面两排通孔。两态共用。"""
    d = canvas()
    p = _pal(body)

    w, cy, h = 34.0, -14.0, 30.0

    _prism(d, 0, cy, w, h, p, gloss=0.10)

    # 顶面孔阵。**「孔是有序的」才是这张图的信息** ——
    # 分子筛和一块普通多孔石头的全部区别就在这里，孔画得再多、排得不齐也没用。
    for a in (0.26, 0.5, 0.74):
        for b in (0.26, 0.5, 0.74):
            px, py = _iso_face(0, cy, w, a, b)

            _pore(d, px, py, 5.2, pore_fill, pore_edge, pore_gloss)

    # 左侧面两排通孔：说明孔是穿透的，不是表面的坑。
    #
    # <b>斜率必须正好是 ISO。</b> 左面的上棱从左顶点 (-w, cy) 走到前顶点 (0, cy + w·ISO)，
    # 所以那条棱上 y = cy + (x + w)·ISO。第一版写成了 ISO·0.5，方块就一路从面上飘出去，
    # 看着像贴在空气里——等距图里任何「贴在某个面上」的东西都得用那个面自己的斜率。
    for x in (-25, -16, -7):
        edge_y = cy + (x + w) * ISO

        for dy in (8, 18):
            d.append(dw.Rectangle(x - 3.3, edge_y + dy, 6.6, 6.6,
                                  rx=1.2, fill=side_pore, stroke=p[3], stroke_width=0.9))

    # 结焦的碳瘤：只有待生态才有。
    #
    # <b>要骑在轮廓线上，而且不能给高光。</b> 第一版是三个带高光的规则圆点，摆在面中央——
    # 读出来是三颗铆钉。碳瘤得破坏那条干净的等距棱，形状还得不规则，才读得出「长出来的脏东西」。
    for lx, ly, lr in lumps:
        for ox, oy, k in ((0, 0, 1.0), (lr * 0.75, lr * 0.35, 0.72), (-lr * 0.6, lr * 0.5, 0.6)):
            d.append(dw.Circle(lx + ox, ly + oy, lr * k, fill="#14100e",
                               stroke="#2b2420", stroke_width=1.1))

    return d


def zeolite_catalyst():
    """沸石催化剂：淡青灰的块体，孔洞是通的、深的、排得整整齐齐。

    孔口内壁那道反光压得比较淡——调亮会读成「镶了一圈蓝宝石」，那是块首饰不是催化剂。
    """
    return _sieve("#b7ccd0", "#26383d", "#16242a", "#6aa8ba", "#1d2d33")


def spent_catalyst():
    """待生沸石催化剂：同一块，压暗两档，孔被碳填平，棱上糊着碳瘤。

    <b>孔要填成「亮黑」而不是留空。</b> 留空的话这张就只是「更暗的那张干净图」，
    读不出「堵住了」——焦炭那点油亮的反光是这里唯一能表达「填满」的手段。
    """
    return _sieve("#5c5750", "#0f0c0a", "#000000", "#6b5f52", "#0d0a09",
                  lumps=((-17, -22.7, 5.6), (17, -22.7, 4.6), (0, 3.4, 5.0)))


# ── 催化反应器 ───────────────────────────────────────────
def catalytic_reactor():
    """催化反应器：细高的提升管 + 粗矮的再生器 + 两条来回的输送管。

    <b>母题是那个回路，不是塔。</b> 真实的 FCC 装置一眼能认出来，靠的就是
    「一细一粗两个容器被两条斜管接成一个环」——催化剂在里面转圈：反应器里结焦、
    送去再生器烧掉、再送回来。这张图要是只画一座塔，它和燔石化工厂就分不开了。

    <b>两条管必须一上一下、而且不平行。</b> 平行的两条会读成一副梯子；一上一下
    才读得出「去」和「回」是两个方向，也才对得上那个环。

    顶上三只小锥体是旋风分离器。80px 下它们是唯一能把「再生器」和「一只普通立罐」
    分开的东西——燔石化工厂那张已经占了「几只立罐 + 横管」，不能再撞。
    """
    d = canvas()
    p = _pal("#6b9198")          # 建筑 tint：分子筛的灰青
    dark = _pal("#3d565c")       # 底盘与管道
    pipe = dark[2]

    _prism(d, 0, 32, 38, 7, dark)

    # 上行输送管：从提升管顶斜下来搭到再生器顶。先画，被两个容器压住两端，
    # 接口就藏进罐体里了（化工厂那张横管的同一条经验：管子悬空就读成一根棍）。
    #
    # <b>斜度要压住。</b> 第一版两个容器拉得很开、这条管就成了一道长对角线，
    # 整张图读出来是台吊车的臂而不是化工装置。真实的 FCC 两个塔是紧挨着的。
    d.append(dw.Line(-21, -28, 14, -9, stroke=pipe, stroke_width=5.4, stroke_linecap="round"))
    d.append(dw.Line(-21, -29.6, 14, -10.6, stroke=p[0], stroke_width=1.8, stroke_linecap="round"))

    # 再生器：粗矮的那个，右边
    RX, RY, RR = 15.0, -4.0, 17.0

    _cyl(d, RX, RY, RR, 28, p, cap_gloss=0.30)

    # 腰带：把「立罐」读成「工业容器」最省的一笔，也顺手把再生器那一大片平色打断。
    #
    # <b>只能画前半弧。</b> 用整个 dw.Ellipse 描边会把<b>背面那半也画出来</b>，
    # 而背面本该被罐体挡住——画出来立刻读成「罐口上扣了个盖」。这和熔岩冷却厂
    # 那次 _ring() 画出实心盘是同一类错：等距图里凡是绕着圆柱的东西，
    # 都得自己决定哪半被遮住，SVG 不会替你挡。
    for dy, col, wdt in ((13, dark[1], 4.0), (11.8, p[0], 1.4)):
        d.append(dw.Path(fill="none", stroke=col, stroke_width=wdt, stroke_linecap="round")
                 .M(RX - RR, RY + dy).A(RR, RR * ISO, 0, 0, 0, RX + RR, RY + dy))

    # 旋风分离器：三只小锥，锥尖坐在罐顶的<b>穹面上</b>。
    #
    # 第一版把三个锥尖排在同一条水平线上，于是它们整体浮在罐口上方十来个单位——
    # 一眼就是「飘着的三个漏斗」。罐顶是个椭圆，尖端的 y 必须按椭圆算：
    # y = RY − ry·√(1 − (dx/RR)²)。等距图里所有「坐在罐子上」的东西都要过这道算。
    for dx, s in ((-11, 0.95), (0, 1.12), (11, 0.95)):
        tip = RY - RR * ISO * (1 - (dx / RR) ** 2) ** 0.5
        cx, r, hgt = RX + dx, 5.0 * s, 10.5 * s

        d.append(dw.Lines(cx - r, tip - hgt, cx + r, tip - hgt, cx, tip,
                          close=True, fill=p[1], stroke=dark[3], stroke_width=1.2,
                          stroke_linejoin="round"))
        d.append(dw.Ellipse(cx, tip - hgt, r, r * ISO, fill=p[0], stroke=dark[3], stroke_width=1.2))

    # 提升管：细高的那个，左边。它比再生器高出一截，回路才有落差
    _cyl(d, -21, -28, 8.5, 46, p, cap_gloss=0.34)

    # 爬梯：六道横杠。管子上有梯子，尺度感就出来了——没有它，那根柱子可以是任何大小的东西
    for y in range(-18, 14, 6):
        d.append(dw.Line(-21, y, -13.5, y + 1.5, stroke=dark[3], stroke_width=1.5,
                         stroke_opacity=0.75))

    # 下行输送管：从再生器底斜上来接回提升管底。画在最后，从两个容器前面横过去。
    # 两端都<b>咬进</b>容器里（右端在罐身上，左端在管身上），伸到轮廓外面就又是一根悬空的棍子。
    d.append(dw.Line(6, 22, -18, 16, stroke=pipe, stroke_width=5.0, stroke_linecap="round"))
    d.append(dw.Line(6, 20.6, -18, 14.6, stroke=p[0], stroke_width=1.7, stroke_linecap="round"))

    return d


# ── 有机物第一期：尿素与乌洛托品 ─────────────────────────
# 两张图刻意用两种语言：尿素画结构式（它是本文件里分子图标那一族的一员），
# 乌洛托品画实物（笼形分子在 80px 下画不出来，理由写在函数里）。

def urea():
    """尿素 O=C(NH₂)₂：红球在上、灰碳居中、两个蓝氮分开挂在下面，是个 Y。

    <b>氢被刻意省掉了，这是这张图唯一违反本族惯例的地方。</b>
    甲醇、甲醛、乙烯都把氢画全了，尿素画全就是九个原子——自动缩放按包围盒算，
    九个原子把 span 顶到 150 上下，缩完每根键只剩三四个像素的可见段。
    丙烯那轮已经量过这条线：键的可见长度（键长减两端半径）掉到 23 个单位以下就糊。
    所以这里只留重原子骨架，四个 N—H 省掉——O=C(N)(N) 的 Y 形加上红/灰/蓝三色，
    在 80px 下反而比九个挤成一团的球更认得出。

    C=O 拉到 60：减掉两端半径（16 + 15）还剩 29 个可见单位，双键那两条线才分得开。
    """
    return _molecule(
        [(0, 0, 0, -60, 2), (0, 0, -46, 30), (0, 0, 46, 30)],
        [("O", 0, -60), ("N", -46, 30), ("N", 46, 30), ("C", 0, 0)])


def hexamine():
    """乌洛托品：模压成型的六角燃料片。

    <b>为什么不画结构式。</b> 六亚甲基四胺是个笼子——四个氮在四面体顶点、六个碳
    架在棱上，十个重原子。要让相邻的球不重叠，N—N 得拉到 90 上下，整个笼子的
    包围盒就到 180，自动缩放之后每个原子只剩六个像素。80px 下它是一团灰蓝色的糊。
    所以按本仓库「分子画结构式，其余照实物画」那条的后半句走。

    <b>照的是它真实的样子：野外炉具里那种白色燃料片。</b> 市面上就是压成六角的小块，
    无烟无灰，带一道可掰的刻痕。

    <b>第一版画成了一只白纸箱</b>，两个原因：拉得太厚（侧面高度快赶上顶面短轴，
    六棱柱就读成盒子），以及顶面是一整片平色——压片和箱子的区别全在那圈模具倒角上。
    现在厚度压到短轴的七成，顶面套一圈内缩的倒角面，它才读得出是「压出来的」。
    """
    d = canvas()

    top, bevel = "#e9e3d4", "#f7f3e8"
    front, left, right = "#cbc3b0", "#dbd3c2", "#b5ac99"
    edge = "#5f5a4d"

    rx, ry, cy, h = 42.0, 22.0, -2.0, 15.0

    def ring(k_rx, k_ry, dy=0.0):
        pts = []
        for k in range(6):
            a = math.radians(60 * k)
            pts.append((k_rx * math.cos(a), cy + dy + k_ry * math.sin(a)))

        return pts

    v = ring(rx, ry)

    # 三个前侧面。最暗给右前——光从左上来，和本文件所有等距图标一致。
    for (a, b), fill in zip(((3, 2), (2, 1), (1, 0)), (left, front, right)):
        d.append(dw.Lines(v[a][0], v[a][1], v[b][0], v[b][1],
                          v[b][0], v[b][1] + h, v[a][0], v[a][1] + h,
                          close=True, fill=fill, stroke=edge,
                          stroke_width=1.5, stroke_linejoin="round"))

    # 顶面压住三个侧面的上沿，再套一圈内缩的倒角面
    d.append(dw.Lines(*[c for xy in v for c in xy],
                      close=True, fill=top, stroke=edge,
                      stroke_width=1.5, stroke_linejoin="round"))

    inner = ring(rx * 0.74, ry * 0.74)
    d.append(dw.Lines(*[c for xy in inner for c in xy],
                      close=True, fill=bevel, stroke=edge,
                      stroke_width=1.1, stroke_opacity=0.45, stroke_linejoin="round"))

    # 刻痕：一道凹槽要两条线才立体——暗的是槽底，亮的是被照到的槽壁
    d.append(dw.Line(-17, cy + 3.2, 17, cy + 3.2,
                     stroke="#9a9384", stroke_width=1.7, stroke_linecap="butt"))
    d.append(dw.Line(-17, cy + 5.0, 17, cy + 5.0,
                     stroke="#ffffff", stroke_width=1.1,
                     stroke_opacity=0.6, stroke_linecap="butt"))

    # 倒角面左上一小片高光，和锭子那套是同一个手法。压在刻痕上方，
    # 两者一叠就糊成一团「白色的什么东西」，读不出是槽。
    d.append(dw.Lines(-19, cy - 6, -7, cy - 11, -1, cy - 8, -13, cy - 3,
                      close=True, fill="#ffffff", fill_opacity=0.45))

    return d


# ── 有机物第二期：丙烯腈与聚丙烯腈 ───────────────────────

def acrylonitrile():
    """丙烯腈 CH₂=CH—C≡N：一头双键、一头三键，中间一根单键把它们隔开。

    <b>排成 L 形而不是一条直线。</b> 四个重原子连成链，拉直了包围盒是 182×64，
    自动缩放按长边算，缩完每个原子只剩六七个像素。折成 L 之后是 138×116，
    尺度立刻回到和尿素同一档。氢照例省掉（理由见 urea()）。

    三键那段要留够长：偏移是 spread×1.6，两端半径吃掉 32，
    60 的键长减完还剩 28 个可见单位，三条线才分得开。
    """
    return _molecule(
        [(-46, 44, -46, -16, 2), (-46, -16, 0, -40), (0, -40, 60, -40, 3)],
        [("C", -46, 44), ("C", -46, -16), ("C", 0, -40), ("N", 60, -40)])


def pan():
    """聚丙烯腈：纺好的白色纤维束，中间一道扎带。

    <b>不画结构式，画纤维束。</b> 聚合物没有一个「分子」可画——重复单元画出来
    和丙烯腈几乎一样，两张图会分不开。而它在产线上真实的样子就是一束丝，
    这也正好预示了下一步：这束丝进炉子出来就是碳。

    <b>第一版把每根丝画成了各走各的斜线</b>，互相交叉，读出来是一堆缎带不是一束丝。
    束的定义是「方向一致、挨得很近」：现在六根**同向同斜率**，只在弓度上差一点点，
    中间两根最亮、外侧压暗，横截面的圆感就出来了。扎带也从一块大方块缩成一道窄环——
    它只需要是「这是一束」的证据，不需要是画面主体。
    """
    d = canvas()

    dark, mid, light = "#aaa495", "#d6d0c0", "#f7f3e9"

    # (起点 y、弓度、颜色、线宽)。终点一律 +16，同斜率才成束
    tow = [
        (-27, -5, dark, 5.6), (25, 5, dark, 5.6),
        (-17, -3, mid, 5.8), (15, 3, mid, 5.8),
        (-7, -1, light, 6.2), (5, 1, light, 6.2),
    ]

    for y0, bow, color, w in tow:
        d.append(dw.Path(stroke=color, stroke_width=w, fill="none", stroke_linecap="round")
                 .M(-42, y0).Q(0, y0 + 8 + bow, 42, y0 + 16))

    # 扎带：窄环，压在束中间
    d.append(dw.Lines(-6, -34, 6, -34, 6, 42, -6, 42,
                      close=True, fill="#6f6858", stroke="#46412f", stroke_width=1.4,
                      stroke_linejoin="round"))
    d.append(dw.Line(-3.4, -32, -3.4, 40, stroke="#ffffff", stroke_width=1.8, stroke_opacity=0.28))

    return d


# ── 有机物第三期：芳烃 ───────────────────────────────────
# 这一族引入一套**新的画法**，理由和乌洛托品那次同类：球棍模型在 80px 下撑不住。
# 苯环有六个碳，画成六个球之后环的直径就吃掉整张图，再挂个取代基，
# 自动缩放一压每个球只剩五六个像素。而「六边形加一个圈」本来就是全世界通用的
# 苯环写法，辨识度比六个灰球高得多——所以环画成符号，取代基仍然画球。

def _arene(items, ring, atoms, bonds=(), margin=5.0):
    """苯环符号（六边形 + 内圈）＋ 挂在它上面的球棍取代基，整体自动缩放。

    ring 是 (cx, cy, r)；六边形取尖顶朝上，所以正上方永远有一个可取代的顶点。
    自动缩放沿用 _molecule 的办法：按实际包围盒算，挪一个原子不用重调 transform。
    """
    import math as _m

    cx, cy, r = ring
    verts = [(cx + r * _m.cos(_m.radians(90 - 60 * k)),
              cy - r * _m.sin(_m.radians(90 - 60 * k))) for k in range(6)]

    xs = [x + s * ATOM[k][2] for k, x, _ in atoms for s in (-1, 1)] + [v[0] for v in verts]
    ys = [y + s * ATOM[k][2] for k, _, y in atoms for s in (-1, 1)] + [v[1] for v in verts]

    span = max(max(xs) - min(xs), max(ys) - min(ys)) or 1.0
    scale = min(1.0, (CANVAS - 2 * margin) / span)
    tx, ty = -(min(xs) + max(xs)) / 2, -(min(ys) + max(ys)) / 2

    d = canvas()
    g = dw.Group(transform=f"scale({scale:.4f}) translate({tx:.2f}, {ty:.2f})")

    for i in range(6):
        a, b = verts[i], verts[(i + 1) % 6]
        g.append(dw.Line(a[0], a[1], b[0], b[1], stroke=BOND,
                         stroke_width=5.6, stroke_linecap="round"))

    # 内圈是「芳香」的那半个意思，半径压到 0.58——再大就贴边、再小就读成一个点
    g.append(dw.Circle(cx, cy, r * 0.58, fill="none", stroke=BOND, stroke_width=4.4))

    for b in bonds:
        _bond(g, *b[:4], order=b[4] if len(b) > 4 else 1)

    for kind, ax, ay in atoms:
        _atom(g, kind, ax, ay)

    d.append(g)

    return d


def benzene():
    """苯 C₆H₆：只有那个环，什么都不挂。

    本 mod 里最干净的一张图，也是最认得出来的一张——六边形加一个圈是通用符号。
    """
    return _arene(None, (0, 0, 44), [])


def cumene():
    """异丙苯 C₉H₁₂：环顶上一个「人」字形的异丙基。

    三个球摆成 Y 是这张图的识别点：异丙基的分叉正是下一步氧化要撬的那个弱点，
    图上那个居中的碳就是只剩一个氢的叔碳。
    """
    return _arene(
        None, (0, 34, 38),
        [("C", 0, -56), ("C", -46, -82), ("C", 46, -82)],
        [(0, -4, 0, -56), (0, -56, -46, -82), (0, -56, 46, -82)])


def phenol():
    """苯酚 C₆H₅OH：环顶上一个羟基。

    红球直接坐在环的顶点上——羟基长在环上而不是链上，正是它酸得不像醇的原因。
    """
    return _arene(
        None, (0, 20, 40),
        [("O", 0, -70), ("H", 40, -92)],
        [(0, -20, 0, -70), (0, -70, 40, -92)])


def acetone():
    """丙酮 (CH₃)₂C=O：两个甲基左右平摊，羰基朝上，整体是个 T。

    <b>刻意摆成 T 而不是 Y。</b> 尿素也是「一个红球在上、两个球在下」的构型，
    两张图摆在一起只靠蓝氮和灰碳的颜色区分太险；把甲基压成水平就分得开了。
    """
    return _molecule(
        [(0, 10, 0, -50, 2), (0, 10, -58, 10), (0, 10, 58, 10)],
        [("O", 0, -50), ("C", -58, 10), ("C", 58, 10), ("C", 0, 10)])


# ── 原油的四个馏分 ───────────────────────────────────────
# 石脑油 / 精炼油(原版) / 蜡油 / 钒渣油 是同一桶油的四段，图标要能排成一列看。
# 区分它们的不是颜色而是<b>稠度</b>——轮廓越往下越坠、越往下越暗：
#   石脑油  瘦长的泪滴 + 挥发出来的小珠      淡麦秆色
#   蜡油    圆胖、底部微沉的一滴             琥珀棕
#   钒渣油  坠着、底部摊开的稠块 + 挂丝      近黑（见 vanadium_residue_oil）
# 颜色只是辅助；形体本身就要读得出「越来越稠」。

def naphtha():
    """石脑油：瘦长的一滴，淡得近乎无色，头顶几颗挥发出来的小珠。

    <b>它是四段里唯一要画「挥发」的。</b> 石脑油常温下就往外跑（汽油的味道就是它），
    而剩下三段都不会——那几颗越往上越小的珠子是这张图和另外三张唯一的结构差别，
    也是一眼能认出「这是最轻的那一段」的地方。
    """
    d = canvas()

    body = "#d8d1a6"
    body_hi = "#efe9c6"
    rim = "#f7f2d4"

    # 瘦长：半宽只有 20，和蜡油的 27、渣油的 30 排成一列就是稠度序列
    d.append(dw.Path(fill=body, stroke=rim, stroke_width=2.4, stroke_linejoin="round")
             .M(0, -42).C(7, -22, 20, -6, 20, 9)
             .C(20, 25, 11, 33, 0, 33)
             .C(-11, 33, -20, 25, -20, 9)
             .C(-20, -6, -7, -22, 0, -42).Z())

    # 左缘弧光，光源和本文件其余液体一致（左上）
    d.append(dw.Path(fill=body_hi, fill_opacity=0.85)
             .M(0, -40).C(-7, -21, -16, -7, -17, 6)
             .C(-11, 4, -6, -4, -2, -16)
             .C(-1, -25, 0, -34, 0, -40).Z())

    # 挥发出去的小珠：越往上越小、越淡
    for cx, cy, r, op in ((14, -40, 3.4, 0.85), (23, -30, 2.4, 0.6), (28, -44, 1.7, 0.42)):
        d.append(dw.Circle(cx, cy, r, fill=rim, fill_opacity=op))

    d.append(dw.Ellipse(-7, -8, 4.0, 6.8, fill="#ffffff", fill_opacity=0.5))

    return d


def vgo():
    """蜡油：圆胖的一滴，琥珀棕，表面一道蜡质的白霜。

    <b>稠度排在石脑油和渣油之间</b>，所以轮廓也排在中间：比石脑油宽一截、
    底部微微下沉，但还没到渣油那种摊开挂丝的程度。

    那道横过去的浅色带是「蜡」——这一段常温下会析出蜡晶，行话叫蜡油正是因为这个。
    它同时也是这张图和琥珀色树脂之类东西的区别：树脂是透的，它是浑的。
    """
    d = canvas()

    body = "#8e6027"
    body_hi = "#b8853c"
    rim = "#dda54a"

    d.append(dw.Path(fill=body, stroke=rim, stroke_width=2.5, stroke_linejoin="round")
             .M(0, -38).C(10, -18, 27, -3, 27, 12)
             .C(27, 28, 15, 36, 0, 36)
             .C(-15, 36, -27, 28, -27, 12)
             .C(-27, -3, -10, -18, 0, -38).Z())

    d.append(dw.Path(fill=body_hi, fill_opacity=0.8)
             .M(0, -36).C(-9, -18, -21, -4, -23, 8)
             .C(-15, 6, -8, -3, -3, -15)
             .C(-1, -23, 0, -31, 0, -36).Z())

    # 蜡霜：一道横过腹部的浅带，两端收窄——是析出的蜡晶，不是高光
    d.append(dw.Path(fill="#e8d4a8", fill_opacity=0.5)
             .M(-24, 16).C(-12, 11, 12, 11, 24, 16)
             .C(12, 20, -12, 20, -24, 16).Z())

    d.append(dw.Ellipse(-10, -10, 4.2, 7.0, fill="#ffffff", fill_opacity=0.4))

    return d


def omni_chem():
    """综合化学厂：一个宽壳体，三只**形制各不相同**的塔从顶上探出来。

    <b>母题是「三种不相容的反应被塞进同一个壳子」，不是「更大的化工厂」。</b>
    燔石化工厂那张画的是三只一模一样的立罐——同一种反应做三遍；这一张的三只必须
    一眼看出是三种东西，否则它和前一代在建造栏里分不开：

      · 带两根电极的方槽  —— 电化学
      · 细高的精馏塔      —— 化学（沿用燔石化工厂的母题，表示「它也干那个」）
      · 矮胖的圆顶罐      —— 氧化还原

    <b>三只坐在同一个明显更宽的底座上</b>，分开画就成了三座小厂，「并进一个壳子」
    这层意思全丢了——而那正是这台机器存在的理由。

    两处返工记在这里：圆顶第一版用了和罐身同宽的椭圆、还抬高了 4 个单位，读出来是
    顶着一朵蘑菇；横管第一版从左塔穿过中塔顶再到右塔，正好横在最前面那只塔脸上，
    成了一根搁着的棍子。现在圆顶收到罐身的七成，横管改走**所有塔顶之上**、并且在
    最前面那只塔之前画完（燔石化工厂那张的老经验：管子两端要落在罐口上，不能悬空）。
    """
    d = canvas()

    p = _pal("#cd52ba")          # 品红：八座里没有的色相，和观微对撞机的紫（259°）分得开
    dark = _pal("#7a2d6e")
    steel = _pal("#8e94a3")

    # 宽底座：比三只塔的跨度还要宽出一截，它们才读得出是「被装进去的」
    _prism(d, 0, 32, 44, 9, dark)
    _prism(d, 0, 24, 39, 8, p, gloss=0.18)

    # 右后：矮胖圆顶罐 —— 氧化还原
    _cyl(d, 20, 4, 12, 18, p, cap_gloss=0.3)
    d.append(dw.Ellipse(20, 1.5, 8.4, 8.4 * ISO * 1.25, fill=p[0], stroke=dark[3], stroke_width=1.2))

    # 左：带电极的方槽 —— 电化学。两根电极不等高，读得出是「插进去的」而不是栏杆
    _prism(d, -21, 8, 13, 17, p, gloss=0.22)
    for ex, eh in ((-26, 19), (-16, 14)):
        d.append(dw.Line(ex, 6, ex, 6 - eh, stroke=steel[2], stroke_width=3.2, stroke_linecap="round"))
        d.append(dw.Circle(ex, 6 - eh, 2.6, fill=steel[0], stroke=dark[3], stroke_width=1.0))

    # 共用的进出料横管：走在所有塔顶之上，两端落进左右两座的罐口
    for w, col in ((4.6, dark[2]), (1.7, p[0])):
        d.append(dw.Path(stroke=col, stroke_width=w, fill="none",
                         stroke_linecap="round", stroke_linejoin="round")
                 .M(-21, 8).L(-21, -4).L(20, -8).L(20, 4))

    # 中前：细高精馏塔 —— 化学。<b>要比横管高</b>：最后画、又穿过横管，
    # 前后关系才立得住；横管第二版架得太高，中间圈出一大片空白，读成了一副龙门架
    _cyl(d, 0, -13, 9, 42, p, cap_gloss=0.3)
    d.append(dw.Ellipse(0, -13, 10.4, 10.4 * ISO, fill="#e87fd6", stroke=dark[3], stroke_width=1.2))

    return d


if __name__ == "__main__":
    render(aluminum_ingot(), "aluminum-ingot")
    render(carbon_dioxide(), "carbon-dioxide")
    render(sulfur_powder(), "sulfur-powder")
    render(oxygen(), "oxygen")
    render(water_electrolysis(), "water-electrolysis")
    render(carbon_monoxide(), "carbon-monoxide")
    render(methanol(), "methanol")
    render(formaldehyde(), "formaldehyde")
    render(ethylene(), "ethylene")
    render(lithium_ingot(), "lithium-ingot")
    render(lithium_hydroxide(), "lithium-hydroxide")
    render(lithium_sulfate(), "lithium-sulfate")
    render(lithium_cobalt_oxide(), "lithium-cobalt-oxide")
    render(nitrogen(), "nitrogen")
    render(ammonia(), "ammonia")
    render(nitrogen_dioxide(), "nitrogen-dioxide")
    render(nitric_acid(), "nitric-acid")
    render(manganese_ore(), "manganese-ore")
    render(manganese_ingot(), "manganese-ingot")
    render(chromite_ore(), "chromite-ore")
    render(chromium_ingot(), "chromium-ingot")
    render(vanadium_ore(), "vanadium-ore")
    render(vanadium_ingot(), "vanadium-ingot")
    render(cobalt_ore(), "cobalt-ore")
    render(cobalt_ingot(), "cobalt-ingot")
    render(scheelite_ore(), "scheelite-ore")
    render(tungsten_trioxide(), "tungsten-trioxide")
    render(tungsten_ingot(), "tungsten-ingot")
    render(tungsten_carbide(), "tungsten-carbide")
    # 合金一种一个物品，不再分牌号——所以 grade=0，顶面不刻点数。
    # 牌号刻印那套（grade=1~4）留在 alloy()/carbide_ingot() 里没删：
    # 它解决的是「四块近乎同色的深锭在 80px 下分不开」，哪天再要分档还用得上。
    render(carbide_ingot(0), "carbide")
    render(manganese_steel(0), "manganese-steel")
    render(stainless_steel(0), "stainless-steel")
    render(chrome_plated_copper(0), "chrome-plated-copper")
    render(chrome_vanadium_steel(0), "chrome-vanadium-steel")
    render(cobalt_chrome(0), "cobalt-chrome")
    render(vanadium_titanium(0), "vanadium-titanium")

    # 巨型建筑：图标轮廓与 MegaBuildingMeshes.cs 里的程序化模型一一对应
    render(sky_assembler(), "sky-assembler")
    render(lysis_tower(), "lysis-tower")
    render(chem_plant(), "chem-plant")
    render(mega_assembler(), "mega-assembler")
    render(particle_collider(), "particle-collider")
    render(bio_greenhouse(), "bio-greenhouse")
    render(lava_cooler(), "lava-cooler")
    render(omni_chem(), "omni-chem")
    render(tab_mega(), "tab-mega")

    # 生物温室的产物与配方图标
    render(microbial_consortium(), "microbial-consortium")
    render(mycelial_matrix(), "mycelial-matrix")

    # 活性复合材：形制与合金锭刻意分开，理由见 living_composite() 的注释
    for _g in (1, 2, 3, 4):
        render(living_composite(_g), "living-composite-%d" % _g)
    render(algal_oil(), "algal-oil")
    render(photosynthesis(), "photosynthesis")

    # 可燃液体发电：钒渣油
    render(vanadium_residue_oil(), "vanadium-residue-oil")

    # 活性增产剂：颜色分档（Mk.IV 青 / Mk.V 紫），形态分性格（浓缩 / 广延）
    render(proliferator_4_dense(), "proliferator-4-dense")
    render(proliferator_4_spread(), "proliferator-4-spread")
    render(proliferator_5_dense(), "proliferator-5-dense")
    render(proliferator_5_spread(), "proliferator-5-spread")

    # 外星矿脉
    render(moissanite_ore(), "moissanite-ore")
    render(drill_bit(), "drill-bit")

    # 碳化硅下游：晶圆 → 基板 → 功率模块
    render(sic_wafer(), "sic-wafer")
    render(aluminium_nitride(), "aluminium-nitride")
    render(sic_power_module(), "sic-power-module")

    # 岩浆：抽水站在熔岩星上抽出来的东西
    render(lava(), "lava")

    # 催化反应器：催化剂的两个状态是一对，形制相同、明暗相反
    render(propylene(), "propylene")
    render(zeolite_catalyst(), "zeolite-catalyst")
    render(spent_catalyst(), "spent-catalyst")
    render(catalytic_reactor(), "catalytic-reactor")

    # 有机物第一期：给甲醛和二氧化碳各找一个真正的下游
    render(urea(), "urea")
    render(hexamine(), "hexamine")

    # 有机物第二期：丙烯腈 → 聚丙烯腈 → 碳化（终点是原版碳纳米管，不新开物品）
    render(acrylonitrile(), "acrylonitrile")
    render(pan(), "pan")

    # 有机物第三期：芳烃。环画成符号、取代基画球，理由见 _arene()
    render(benzene(), "benzene")
    render(cumene(), "cumene")
    render(phenol(), "phenol")
    render(acetone(), "acetone")

    # 原油的四个馏分：形体按稠度排成一列，理由见 naphtha()
    render(naphtha(), "naphtha")
    render(vgo(), "vgo")
