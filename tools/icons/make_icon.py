"""Draws the SkyNET port's icons (a red machine eye on a dark metal plate; the editor's adds a wrench)
and writes every size the builds use. Run from the repo root: python tools/icons/make_icon.py
Outputs: assets/icons/*.ico + *.png (Windows exe, window), src/SkyNet.Android/Resources/mipmap-* (launcher)."""
import math
from PIL import Image, ImageDraw, ImageFilter, ImageChops

S = 1024  # master size


def radial(size, inner, outer, center=None, radius=None):
    """RGBA radial gradient from inner colour at the centre to outer colour at radius."""
    cx, cy = center or (size / 2, size / 2)
    r = radius or size / 2
    img = Image.new("RGBA", (size, size))
    px = img.load()
    for y in range(size):
        for x in range(size):
            t = min(1.0, math.hypot(x - cx, y - cy) / r)
            px[x, y] = tuple(int(inner[i] + (outer[i] - inner[i]) * t) for i in range(4))
    return img


def linear(size, top, bottom):
    img = Image.new("RGBA", (size, size))
    d = ImageDraw.Draw(img)
    for y in range(size):
        t = y / (size - 1)
        d.line([(0, y), (size, y)], fill=tuple(int(top[i] + (bottom[i] - top[i]) * t) for i in range(4)))
    return img


def mask_circle(size, box):
    m = Image.new("L", (size, size), 0)
    ImageDraw.Draw(m).ellipse(box, fill=255)
    return m


def shield_mask(size, inset=0):
    """Rounded octagon plate."""
    m = Image.new("L", (size, size), 0)
    d = ImageDraw.Draw(m)
    a = 60 + inset
    b = size - 60 - inset
    c = 210 + inset * 0.4  # chamfer
    pts = [(a + c, a), (b - c, a), (b, a + c), (b, b - c), (b - c, b), (a + c, b), (a, b - c), (a, a + c)]
    d.polygon(pts, fill=255)
    return m.filter(ImageFilter.GaussianBlur(6)).point(lambda v: 255 if v > 128 else 0)


def plate():
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))

    # Outer rim: bright brushed edge, then the darker face inside it.
    rim = linear(S, (150, 156, 164, 255), (38, 40, 46, 255))
    img.paste(rim, (0, 0), shield_mask(S))
    face = linear(S, (70, 74, 82, 255), (22, 23, 27, 255))
    img.paste(face, (0, 0), shield_mask(S, 26))

    # Bevel highlight along the top inner edge.
    hl = Image.new("RGBA", (S, S), (255, 255, 255, 0))
    hm = ImageChops.subtract(shield_mask(S, 26), shield_mask(S, 40))
    top = linear(S, (255, 255, 255, 90), (255, 255, 255, 0))
    hl.paste(top, (0, 0), hm)
    img = Image.alpha_composite(img, hl)

    # Rivets in the chamfer corners.
    d = ImageDraw.Draw(img)
    for (x, y) in [(205, 205), (S - 205, 205), (205, S - 205), (S - 205, S - 205)]:
        d.ellipse((x - 18, y - 18, x + 18, y + 18), fill=(40, 42, 47, 255))
        d.ellipse((x - 14, y - 16, x + 12, y + 10), fill=(140, 146, 154, 255))
    return img


def eye(img):
    c = S / 2
    d = ImageDraw.Draw(img)

    # Socket: dark recessed ring.
    d.ellipse((c - 300, c - 300, c + 300, c + 300), fill=(12, 12, 14, 255))
    ring = radial(S, (110, 114, 122, 255), (30, 31, 35, 255), radius=300)
    rm = ImageChops.subtract(mask_circle(S, (c - 290, c - 290, c + 290, c + 290)),
                             mask_circle(S, (c - 236, c - 236, c + 236, c + 236)))
    img.paste(ring, (0, 0), rm)

    # Targeting ticks on the ring.
    for k in range(12):
        ang = k * math.pi / 6
        r1, r2 = (244, 284) if k % 3 == 0 else (258, 280)
        w = 14 if k % 3 == 0 else 7
        d.line([(c + r1 * math.cos(ang), c + r1 * math.sin(ang)),
                (c + r2 * math.cos(ang), c + r2 * math.sin(ang))], fill=(200, 40, 30, 255), width=w)

    # Glow behind the lens.
    glow = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    ImageDraw.Draw(glow).ellipse((c - 230, c - 230, c + 230, c + 230), fill=(255, 30, 10, 200))
    glow = glow.filter(ImageFilter.GaussianBlur(60))
    img = Image.alpha_composite(img, glow)

    # Lens: red radial with a hot core.
    lens = radial(S, (255, 70, 30, 255), (110, 0, 0, 255), radius=200)
    img.paste(lens, (0, 0), mask_circle(S, (c - 200, c - 200, c + 200, c + 200)))
    mid = radial(S, (255, 40, 10, 0), (40, 0, 0, 200), radius=200)
    img = Image.alpha_composite(img, Image.composite(mid, Image.new("RGBA", (S, S), (0, 0, 0, 0)),
                                                     mask_circle(S, (c - 200, c - 200, c + 200, c + 200))))
    core = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    ImageDraw.Draw(core).ellipse((c - 55, c - 55, c + 55, c + 55), fill=(255, 210, 160, 255))
    core = core.filter(ImageFilter.GaussianBlur(22))
    img = Image.alpha_composite(img, core)

    # Crosshair across the lens and a specular glint.
    d = ImageDraw.Draw(img)
    for dx, dy in [(1, 0), (0, 1)]:
        d.line([(c - 330 * dx, c - 330 * dy), (c - 215 * dx, c - 215 * dy)], fill=(255, 60, 40, 255), width=10)
        d.line([(c + 215 * dx, c + 215 * dy), (c + 330 * dx, c + 330 * dy)], fill=(255, 60, 40, 255), width=10)
    shine = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    ImageDraw.Draw(shine).ellipse((c - 150, c - 160, c - 40, c - 90), fill=(255, 255, 255, 150))
    img = Image.alpha_composite(img, shine.filter(ImageFilter.GaussianBlur(10)))
    return img


def wrench_badge(img):
    """Editor mark: a wrench on a dark disc in the lower right."""
    b = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    d = ImageDraw.Draw(b)
    cx, cy, r = 760, 760, 210
    d.ellipse((cx - r - 14, cy - r - 14, cx + r + 14, cy + r + 14), fill=(10, 10, 12, 255))
    d.ellipse((cx - r, cy - r, cx + r, cy + r), fill=(235, 180, 40, 255))
    # Wrench: shaft rotated 45 degrees, open jaw at the top left.
    w = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    wd = ImageDraw.Draw(w)
    wd.rounded_rectangle((cx - 34, cy - 120, cx + 34, cy + 150), radius=30, fill=(25, 25, 28, 255))
    wd.ellipse((cx - 90, cy - 220, cx + 90, cy - 40), fill=(25, 25, 28, 255))
    wd.rectangle((cx - 36, cy - 240, cx + 36, cy - 120), fill=(0, 0, 0, 0))
    w = w.rotate(-45, center=(cx, cy), resample=Image.BICUBIC)
    b = Image.alpha_composite(b, w)
    return Image.alpha_composite(img, b)


# Android launcher sizes per density: legacy icon (48 dp) and adaptive foreground (108 dp).
DENSITIES = {"mdpi": 1, "hdpi": 1.5, "xhdpi": 2, "xxhdpi": 3, "xxxhdpi": 4}
ICO_SIZES = [(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)]
ADAPTIVE_XML = """<?xml version="1.0" encoding="utf-8"?>
<adaptive-icon xmlns:android="http://schemas.android.com/apk/res/android">
  <background android:drawable="@color/ic_launcher_background" />
  <foreground android:drawable="@mipmap/ic_launcher_foreground" />
</adaptive-icon>
"""
COLORS_XML = """<?xml version="1.0" encoding="utf-8"?>
<resources>
  <color name="ic_launcher_background">#16171B</color>
</resources>
"""


def save_android(game, res):
    import os

    for name, scale in DENSITIES.items():
        folder = os.path.join(res, f"mipmap-{name}")
        os.makedirs(folder, exist_ok=True)
        legacy = round(48 * scale)
        game.resize((legacy, legacy), Image.LANCZOS).save(os.path.join(folder, "ic_launcher.png"))

        # Adaptive foreground: the plate inside the 66 dp safe zone of the 108 dp layer.
        full = round(108 * scale)
        inner = round(72 * scale)
        fg = Image.new("RGBA", (full, full), (0, 0, 0, 0))
        off = (full - inner) // 2
        fg.paste(game.resize((inner, inner), Image.LANCZOS), (off, off))
        fg.save(os.path.join(folder, "ic_launcher_foreground.png"))

    any_dpi = os.path.join(res, "mipmap-anydpi-v26")
    os.makedirs(any_dpi, exist_ok=True)
    open(os.path.join(any_dpi, "ic_launcher.xml"), "w", encoding="utf-8").write(ADAPTIVE_XML)
    values = os.path.join(res, "values")
    os.makedirs(values, exist_ok=True)
    open(os.path.join(values, "ic_launcher_colors.xml"), "w", encoding="utf-8").write(COLORS_XML)


def main():
    import os

    out = os.path.join("assets", "icons")
    os.makedirs(out, exist_ok=True)
    game = eye(plate())
    editor = wrench_badge(game)
    for name, img in (("skynet", game), ("skynetedit", editor)):
        img.save(os.path.join(out, f"{name}.ico"), sizes=ICO_SIZES)
        img.resize((256, 256), Image.LANCZOS).save(os.path.join(out, f"{name}-256.png"))
    save_android(game, os.path.join("src", "SkyNet.Android", "Resources"))


if __name__ == "__main__":
    main()
