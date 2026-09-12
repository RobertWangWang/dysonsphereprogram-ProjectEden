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
