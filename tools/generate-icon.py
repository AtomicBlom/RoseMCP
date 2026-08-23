"""Generates the tray and window icon.

Kept in the repo so the asset is reproducible rather than a binary someone dropped in years ago
and nobody can regenerate. Each size is drawn at its own resolution rather than downscaled from
one large image: a 16px tray slot is where legibility is won or lost, and downsampling a 256px
glyph turns it to mush at that size.
"""
from PIL import Image, ImageDraw, ImageFont
import os, sys

TILE = (0x1E, 0x88, 0xE5, 0xFF)   # azure, readable against light and dark taskbars alike
GLYPH = (0xFF, 0xFF, 0xFF, 0xFF)
SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256]

def font_for(px):
    for name in ("segoeuib.ttf", "seguisb.ttf", "arialbd.ttf", "calibrib.ttf"):
        path = os.path.join(os.environ.get("WINDIR", r"C:\Windows"), "Fonts", name)
        if os.path.exists(path):
            return ImageFont.truetype(path, px)
    return ImageFont.load_default()

def draw(size):
    image = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    d = ImageDraw.Draw(image)

    # Full-bleed rounded tile. A glyph alone disappears on a taskbar that matches its colour,
    # so the tile is what guarantees the icon is visible at all.
    radius = max(2, round(size * 0.22))
    d.rounded_rectangle([0, 0, size - 1, size - 1], radius=radius, fill=TILE)

    # Grow the glyph until it fills ~62% of the tile height, measured rather than assumed.
    target = size * 0.62
    px, font = size, font_for(size)
    while px > 4:
        font = font_for(px)
        box = d.textbbox((0, 0), "R", font=font)
        if (box[3] - box[1]) <= target:
            break
        px -= 1

    box = d.textbbox((0, 0), "R", font=font)
    x = (size - (box[2] - box[0])) / 2 - box[0]
    y = (size - (box[3] - box[1])) / 2 - box[1]
    d.text((x, y), "R", font=font, fill=GLYPH)

    return image

out = sys.argv[1]
frames = [draw(s) for s in SIZES]
frames[-1].save(out, format="ICO", sizes=[(s, s) for s in SIZES], append_images=frames[:-1])
print(f"wrote {out} ({os.path.getsize(out)} bytes, sizes {SIZES})")
