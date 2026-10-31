#!/usr/bin/env python3
"""Generate icon.ico: the word "im" on the top row and "g" under the "m".

Usage: python3 tools/make_icon.py [font.ttf] [out.ico]
Each size is drawn separately at 4x and downsampled, so small sizes stay sharp.
"""
import sys
from PIL import Image, ImageDraw, ImageFont

FONT = sys.argv[1] if len(sys.argv) > 1 else "/mnt/c/Windows/Fonts/segoeuib.ttf"
OUT = sys.argv[2] if len(sys.argv) > 2 else "icon.ico"
SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256]
SS = 4  # supersampling factor
LETTER_BOX = 0.62  # letters fill this fraction of the icon (rest is margin)

TOP = (0x3B, 0x8E, 0xF0)     # background gradient, top
BOTTOM = (0x1C, 0x4E, 0xB8)  # background gradient, bottom
FG = (0xFF, 0xFF, 0xFF)


def ink_box(font, ch):
    """Ink bounding box of ch drawn at origin with anchor 'ls' (left, baseline)."""
    return font.getbbox(ch, anchor="ls")


def render(size):
    n = size * SS
    img = Image.new("RGBA", (n, n), (0, 0, 0, 0))

    # rounded square with a vertical gradient
    grad = Image.new("RGBA", (n, n))
    gd = ImageDraw.Draw(grad)
    for y in range(n):
        t = y / max(1, n - 1)
        c = tuple(round(TOP[i] + (BOTTOM[i] - TOP[i]) * t) for i in range(3))
        gd.line([(0, y), (n, y)], fill=c + (255,))
    mask = Image.new("L", (n, n), 0)
    margin = round(n * 0.03)
    ImageDraw.Draw(mask).rounded_rectangle([margin, margin, n - 1 - margin, n - 1 - margin], radius=round(n * 0.2), fill=255)
    img.paste(grad, (0, 0), mask)

    # letters: "im" set as one word (natural spacing), "g" centered under "m".
    # The group is scaled to fit LETTER_BOX of the inner square and centered.
    inner = n - 2 * margin
    probe = ImageFont.truetype(FONT, 1000)
    w_i = probe.getlength("i")                      # advance of "i" = x offset of "m"
    bi, bm, bg = (ink_box(probe, c) for c in "img")
    row_gap = 0.10 * 1000                           # space between the two rows
    # top row ink (word "im") relative to its origin
    top = (min(bi[0], w_i + bm[0]), min(bi[1], bm[1]), max(bi[2], w_i + bm[2]), max(bi[3], bm[3]))
    # "g": centered under the ink of "m", its x-height top placed row_gap below the top row baseline
    g_x = w_i + (bm[0] + bm[2]) / 2 - (bg[0] + bg[2]) / 2
    g_base = top[3] + row_gap - bg[1]
    group = (min(top[0], g_x + bg[0]), top[1], max(top[2], g_x + bg[2]), g_base + bg[3])
    gw, gh = group[2] - group[0], group[3] - group[1]
    k = LETTER_BOX * inner / max(gw, gh)            # scale from the 1000 px probe to this icon
    font = ImageFont.truetype(FONT, max(1, round(1000 * k)))
    ox = n / 2 - (group[0] + gw / 2) * k            # origin so the group is centered
    oy = n / 2 - (group[1] + gh / 2) * k
    d = ImageDraw.Draw(img)
    d.text((ox, oy), "im", font=font, fill=FG, anchor="ls")
    d.text((ox + g_x * k, oy + g_base * k), "g", font=font, fill=FG, anchor="ls")

    return img.resize((size, size), Image.LANCZOS)


def main():
    images = [render(s) for s in SIZES]
    images[-1].save(OUT, format="ICO", sizes=[(s, s) for s in SIZES], append_images=images[:-1])
    # preview strip for checking by eye
    strip = Image.new("RGBA", (sum(SIZES) + 10 * len(SIZES), 256), (40, 40, 40, 255))
    x = 0
    for im in images:
        strip.paste(im, (x, 0), im)
        x += im.width + 10
    strip.save(OUT.rsplit(".", 1)[0] + "-preview.png")


if __name__ == "__main__":
    main()
