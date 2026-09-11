"""生成 Thunderstore / r2modman 的包图标（icon.png，必须正好 256×256）。

和 make_icons.py 分开：那个是**游戏内**的物品图标（80/480），这个是**包**的图标，
尺寸和用途都不一样，混在一起以后容易误改。

    python tools/make_pack_icon.py     # 输出 ProjectEden/icon.png

图案是原创的：一颗被戴森环围住的恒星 + 一片新芽（Eden）。
不使用任何来自创世之书的美术资源。
"""

import pathlib

import drawsvg as dw
import resvg_py

SIZE = 256
CANVAS = 100
# 和 manifest.json 同级：它是随包发布的源文件，不是构建产物
OUT = pathlib.Path(__file__).parent.parent / "ProjectEden"


def icon():
    d = dw.Drawing(CANVAS, CANVAS, origin=(-CANVAS / 2, -CANVAS / 2))

    # 底：深蓝圆角方块。图标在 r2modman 里是方的，留一圈深色边比铺满好认
    d.append(dw.Rectangle(-50, -50, 100, 100, rx=14, fill="#161c26"))
    d.append(dw.Rectangle(-47, -47, 94, 94, rx=12, fill="none",
                          stroke="#2b3646", stroke_width=1.5))

    # 恒星：三层同心，由外向内提亮，撑出发光感（渐变在小尺寸下会糊）
    for r, c in ((21, "#5a3f14"), (16, "#c98b21"), (11, "#ffd873")):
        d.append(dw.Circle(0, -4, r, fill=c))

    # 戴森环：两道倾斜的椭圆环，一道在星前一道在星后，绕出立体感
    back = dw.Ellipse(0, -4, 40, 13, fill="none", stroke="#3d6ea8",
                      stroke_width=3.5, transform="rotate(-20)")
    d.append(back)

    front = dw.Path(fill="none", stroke="#78b4f0", stroke_width=3.5,
                    stroke_linecap="round", transform="rotate(-20)")
    front.M(-40, -4)
    front.A(40, 13, 0, 0, 0, 40, -4)
    d.append(front)

    # 新芽：Eden 的那半边意思。压在环下方，不和恒星抢中心
    stem = dw.Path(fill="none", stroke="#5ec46a", stroke_width=3.5,
                   stroke_linecap="round")
    stem.M(0, 42)
    stem.C(0, 30, -2, 26, -2, 22)
    d.append(stem)

    for sx in (-1, 1):
        leaf = dw.Path(fill="#5ec46a", transform=f"scale({sx}, 1)")
        leaf.M(-2, 26)
        leaf.C(-14, 26, -18, 18, -17, 13)
        leaf.C(-9, 12, -3, 18, -2, 26)
        leaf.Z()
        d.append(leaf)

    return d


if __name__ == "__main__":
    svg = icon().as_svg()

    (OUT / "icon.svg").write_text(svg, encoding="utf-8")
    (OUT / "icon.png").write_bytes(bytes(resvg_py.svg_to_bytes(
        svg_string=svg, width=SIZE, height=SIZE)))

    print(f"ProjectEden/icon.png  {SIZE}x{SIZE}")
