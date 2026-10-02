# -*- coding: utf-8 -*-
"""make_icon.py —— 產生 kkbridge / kkmerge 的 exe 圖示。

圖案跟 F6（StudioCharTools）工具列上那顆一模一樣：白色小人（頭 + 肩），
右下角一個 R 的深色小牌子。規則直接照 ToolbarButton.cs 的 MakeIcon()／StampR()
搬過來，數字沒有改：

    頭     圓心 (15.5, 23.5)、半徑 5.2
    肩     y 5~17 的梯形，半寬 = 4.5 + (17 - y) * 0.62
    R 牌   5x7 點陣，貼在 (n - w - 2, 2)，外圍多一圈深色底 (0.08, 0.08, 0.10)

兩個差別，都是因為「工具列小圖」和「桌面 exe 圖示」的處境不一樣：

1. **多尺寸、幾何重畫**。原本只有 32x32。exe 的圖示 Windows 會在 16 到 256
   之間各種場合取用，把 32 放大會糊掉，所以每個尺寸都拿同一組座標按比例
   重畫一次，並用 4x 超取樣把頭和肩的邊緣磨平。R 是點陣字，放大時用整數倍
   最近鄰，筆畫才不會糊。

2. **深色底板（可關）**。F6 那顆是白色小人配透明背景，因為它坐在 Studio
   的深色工具列上。同一張圖放到檔案總管裡，白色配透明＝在白底資料夾視窗上
   幾乎看不見。所以預設版本多了一塊深色圓角底板（用的就是 R 牌子那個顏色，
   看起來還是同一套）。真的想要透明版就跑 --flat，會另外產生一顆。

用法：
    python make_icon.py                 → kkbridge.ico（深色底板）
    python make_icon.py --flat          → 另外再產生 kkbridge_flat.ico（透明）
"""

import sys
from PIL import Image, ImageDraw

# ---- 照抄 ToolbarButton.cs 的常數（以 32 為基準） ----
REF = 32.0
HEAD_CX, HEAD_CY, HEAD_R = 15.5, 23.5, 5.2
SHOULDER_LO, SHOULDER_HI = 5, 17
SHOULDER_HALF0, SHOULDER_SLOPE = 4.5, 0.62

GLYPH_R = [
    "11110",
    "10001",
    "10001",
    "11110",
    "10100",
    "10010",
    "10001",
]

WHITE = (255, 255, 255, 255)
DARK = (20, 20, 26, 255)          # = Color(0.08, 0.08, 0.10) 轉成 0-255
CLEAR = (0, 0, 0, 0)

SIZES = [16, 24, 32, 48, 64, 128, 256]
SS = 4                             # 超取樣倍率


# 圖案在 32 基準座標下的實際外框：
#   x  15.5 ± (4.5 + 12*0.62) = 15.5 ± 11.94
#   y  5（肩底） ~ 28.7（頭頂 23.5 + 5.2）
FIG_CX = HEAD_CX
FIG_CY = (SHOULDER_LO + (HEAD_CY + HEAD_R)) / 2.0
FIG_W = 2.0 * (SHOULDER_HALF0 + (SHOULDER_HI - SHOULDER_LO) * SHOULDER_SLOPE)
FIG_H = (HEAD_CY + HEAD_R) - SHOULDER_LO
FIG_SPAN = max(FIG_W, FIG_H)


def _figure_mask(n, fit):
    """在 n x n 上畫出白色小人的覆蓋率（0.0~1.0），Unity 座標（y 往上）。

    用 SS x SS 超取樣：原本 32 的版本是硬邊，放到 256 硬邊會像鋸齒，
    而形狀本身（圓 + 梯形）是連續的，所以照著算覆蓋率就是同一個形狀的高畫質版。

    fit 是「圖案要佔整張的幾成」。原本那顆 32x32 是貼著邊畫的（工具列上的小圖
    本來就要塞滿），但 exe 圖示有底板和圓角，貼著邊會被圓角切到、也顯得擠，
    所以這裡把同一組座標置中縮放到 fit，形狀不變、只是留了邊。
    """
    m = [[0.0] * n for _ in range(n)]
    scale = (n * fit) / FIG_SPAN
    step = 1.0 / SS
    half = step / 2.0
    c = n / 2.0
    for py in range(n):
        for px in range(n):
            hit = 0
            for sy in range(SS):
                for sx in range(SS):
                    # 取樣點換回 32 基準的座標（置中 + 縮放）
                    x = FIG_CX + ((px + sx * step + half) - c) / scale
                    y = FIG_CY + ((py + sy * step + half) - c) / scale
                    dx = x - HEAD_CX
                    dy = y - HEAD_CY
                    if dx * dx + dy * dy <= HEAD_R * HEAD_R:
                        hit += 1
                        continue
                    if SHOULDER_LO <= y <= SHOULDER_HI:
                        w = SHOULDER_HALF0 + (SHOULDER_HI - y) * SHOULDER_SLOPE
                        if abs(dx) <= w:
                            hit += 1
            m[py][px] = hit / float(SS * SS)
    return m


def _stamp_r(img, n, margin):
    """右下角蓋 R。點陣字用整數倍放大，筆畫保持銳利。

    img 是 Unity 座標（y 往上）的 pixel access，跟 StampR 一樣直接寫。
    margin 是離邊界要留多少 —— 有圓角底板時貼著角落會被切掉一塊。
    """
    k = max(1, int(round(n / REF)))
    w, h = len(GLYPH_R[0]) * k, len(GLYPH_R) * k
    pad = max(1, k)
    x0 = n - w - margin - pad
    y0 = margin + pad

    px = img.load()
    # 先鋪深色底（外擴一圈），字才不會跟小人混在一起
    for y in range(y0 - pad, y0 + h + pad):
        for x in range(x0 - pad, x0 + w + pad):
            if 0 <= x < n and 0 <= y < n:
                px[x, y] = DARK
    # GlyphR[0] 是最上面那列，貼圖 y=0 在下面 —— 跟 C# 一樣要反過來取
    rows = len(GLYPH_R)
    for r in range(rows):
        for c in range(len(GLYPH_R[0])):
            if GLYPH_R[rows - 1 - r][c] != "1":
                continue
            for dy in range(k):
                for dx in range(k):
                    x, y = x0 + c * k + dx, y0 + r * k + dy
                    if 0 <= x < n and 0 <= y < n:
                        px[x, y] = WHITE


def _plate(n):
    """深色圓角底板。半徑抓 22%，跟一般 Windows 應用圖示的圓角差不多。"""
    img = Image.new("RGBA", (n, n), CLEAR)
    d = ImageDraw.Draw(img)
    r = max(2, int(round(n * 0.22)))
    inset = max(0, int(round(n * 0.02)))
    d.rounded_rectangle([inset, inset, n - 1 - inset, n - 1 - inset],
                        radius=r, fill=DARK)
    return img


def render(n, plate=True):
    # 有底板：圖案縮小一點、R 往內縮，免得被圓角切到
    # 沒底板：可以畫得滿一些，比較接近工具列上那顆
    fit = 0.62 if plate else 0.80
    margin = max(1, int(round(n * (0.11 if plate else 0.02))))

    base = _plate(n) if plate else Image.new("RGBA", (n, n), CLEAR)
    fig = Image.new("RGBA", (n, n), CLEAR)
    px = fig.load()
    m = _figure_mask(n, fit)
    for y in range(n):
        for x in range(n):
            a = m[y][x]
            if a > 0.0:
                px[x, y] = (255, 255, 255, int(round(a * 255)))
    _stamp_r(fig, n, margin)
    out = Image.alpha_composite(base, fig)
    # Unity 的 y=0 在下面，PNG/ICO 的第 0 列在上面
    return out.transpose(Image.FLIP_TOP_BOTTOM)


def build(path, plate=True):
    """每個尺寸各畫一張，最大的那張當底，其餘用 append_images 帶進去。

    順序不能顛倒：Pillow 存 ICO 時會把 sizes 裡「比底圖還大」的尺寸直接丟掉，
    拿 16x16 當底的話最後只會存到一張 16 的（實測 342 bytes、只有 16）。
    """
    order = sorted(SIZES, reverse=True)
    imgs = [render(n, plate) for n in order]
    imgs[0].save(path, format="ICO",
                 sizes=[(n, n) for n in order], append_images=imgs[1:])
    return path


if __name__ == "__main__":
    build("kkbridge.ico", plate=True)
    print("寫出 kkbridge.ico（深色底板）尺寸 " + "、".join(str(s) for s in SIZES))
    if "--flat" in sys.argv:
        build("kkbridge_flat.ico", plate=False)
        print("寫出 kkbridge_flat.ico（透明背景，跟 F6 工具列上那顆一樣）")
