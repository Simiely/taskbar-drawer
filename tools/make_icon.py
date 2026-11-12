"""生成任务栏收纳的应用图标（Assets/AppIcon.ico）。

设计：Apple 风格蓝色圆角方块 + 三张叠放的白色卡片（代表收纳的窗口组）。
运行：python tools/make_icon.py
"""

from pathlib import Path

from PIL import Image, ImageDraw

OUT_ICO = Path(__file__).resolve().parents[1] / "src" / "TaskbarDrawer" / "Assets" / "AppIcon.ico"
PREVIEW_DIR = Path(__file__).resolve().parent / "preview"

SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256]
MASTER = 1024

TOP_COLOR = (18, 140, 255)
BOTTOM_COLOR = (0, 86, 214)


def vertical_gradient(size, top, bottom):
    grad = Image.new("RGBA", (1, size))
    for y in range(size):
        t = y / max(1, size - 1)
        grad.putpixel((0, y), (
            round(top[0] + (bottom[0] - top[0]) * t),
            round(top[1] + (bottom[1] - top[1]) * t),
            round(top[2] + (bottom[2] - top[2]) * t),
            255,
        ))
    return grad.resize((size, size))


def draw_master():
    s = MASTER
    image = Image.new("RGBA", (s, s), (0, 0, 0, 0))

    # 圆角方块底（squircle 近似）
    radius = round(0.225 * s)
    shape = Image.new("L", (s, s), 0)
    ImageDraw.Draw(shape).rounded_rectangle((0, 0, s - 1, s - 1), radius=radius, fill=255)
    image = Image.composite(vertical_gradient(s, TOP_COLOR, BOTTOM_COLOR), image, shape)

    # 顶部高光，模拟苹果图标的打光
    highlight = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    hd = ImageDraw.Draw(highlight)
    hd.rounded_rectangle((0, 0, s - 1, round(0.62 * s)), radius=radius,
                         fill=(255, 255, 255, 34))
    fade = vertical_gradient(s, (0, 0, 0), (0, 0, 0))
    fade.putalpha(0)
    # 用渐隐遮罩裁掉下半部分高光
    mask = Image.new("L", (s, s), 0)
    for y in range(s):
        alpha = max(0, 255 - int(255 * y / (0.62 * s)))
        mask.paste(alpha, (0, y, s, y + 1))
    highlight.putalpha(Image.composite(highlight.getchannel("A"), Image.new("L", (s, s), 0), mask))
    image = Image.alpha_composite(image, highlight)
    image.putalpha(shape)

    # 三张叠放的卡片
    draw = ImageDraw.Draw(image)
    cards = [
        ((0.295, 0.240, 0.705, 0.405), 0.050, 100),
        ((0.255, 0.375, 0.745, 0.560), 0.055, 170),
        ((0.215, 0.510, 0.785, 0.720), 0.060, 255),
    ]
    for (x0, y0, x1, y1), r, alpha in cards:
        draw.rounded_rectangle(
            (round(x0 * s), round(y0 * s), round(x1 * s), round(y1 * s)),
            radius=round(r * s), fill=(255, 255, 255, alpha))

    # 最前面卡片的「标题栏」细节：大尺寸看得见，16 px 下自然淡出
    draw.rounded_rectangle(
        (round(0.255 * s), round(0.545 * s), round(0.405 * s), round(0.573 * s)),
        radius=round(0.014 * s), fill=(10, 132, 255, 235))

    return image


def main():
    master = draw_master()
    OUT_ICO.parent.mkdir(parents=True, exist_ok=True)
    frames = [master.resize((n, n), Image.LANCZOS) for n in SIZES]
    frames[-1].save(OUT_ICO, format="ICO", sizes=[(n, n) for n in SIZES])

    PREVIEW_DIR.mkdir(parents=True, exist_ok=True)
    for n in (256, 32, 16):
        master.resize((n, n), Image.LANCZOS).save(PREVIEW_DIR / f"icon-{n}.png")
    print(f"written {OUT_ICO}")


if __name__ == "__main__":
    main()
