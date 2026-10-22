"""Example mods for the enhanced look, made from CC0 sources on the player's machine.

Run with an isolated interpreter (python -I) from any folder; nothing is imported from the
working folder. Needs Pillow and numpy. No art is shipped or committed: the DOS records come from
the player's own data (the export below), the photo textures from a folder of CC0 downloads the
player fetched (Poly Haven, https://polyhaven.com, CC0 1.0), and the models are generated here.
The formats are in docs/modding.md.

  1. export the DOS records as palette PNGs (the colour reference of step 2):
       dotnet run --project tools/SkyNet.Tables -c Release -- textures <game-data-dir> --out <work>/export
  2. materials: DOS wall/floor records replaced by colour-matched CC0 photo textures
       python -I tools/modkit/modkit.py materials --export <work>/export --src <cc0-textures-dir>
           --mods <mods-dir> [--name cc0-materials] [--size 512] [--only T186_009,T383_000]
     <cc0-textures-dir>/<id>/<id>_diff_1k.jpg (any *_diff_*.jpg/png) per Poly Haven texture id.
  3. props: a few DOS props replaced by generated glTF models (crates, a mess table, a chair with
     a slatted back cut out by an alpha mask), textured from the same CC0 photos (base colours as
     JPEG), with each source's normal map (<id>_nor_gl_*) and roughness map (<id>_rough_*) when
     it has them, and metallic/roughness factors per material
       python -I tools/modkit/modkit.py props --src <cc0-textures-dir> --mods <mods-dir> [--name cc0-props] [--no-maps]
  4. robots: the gun towers' segments (tower, turret heads, plasma barrels, missile pod), the
     jeep's cockpit and the deathmatch jeep as generated glTF models with the same CC0 photos;
     each segment keeps DOS's place, turn and animation
       python -I tools/modkit/modkit.py robots --src <cc0-textures-dir> --mods <mods-dir> [--name cc0-robots] [--no-maps]

Then start the game with --mod cc0-materials,cc0-props,cc0-robots --look enhanced.
"""

from __future__ import annotations

import argparse
import io
import json
import math
import struct
import sys
from pathlib import Path

# DOS texture record (T<bank>_<record>) -> Poly Haven texture id. The Godot port's ENHANCED
# mapping (tools/enhanced_pack.py TEXTURES, 2026-09-04): the plain tiling materials among the
# records that cover the most world area across the 123 maps, then picks by eye. Building
# facades, signs and panels stay DOS art.
MATERIALS: dict[str, str] = {
    "T186_009": "concrete_wall_008", "T195_002": "concrete_wall_003", "T227_003": "concrete_floor_01",
    "T231_000": "concrete_moss", "T235_007": "brown_planks_03", "T239_002": "granular_concrete",
    "T251_001": "old_planks_02", "T287_000": "box_profile_metal_sheet", "T330_000": "rustic_stone_wall",
    "T347_000": "concrete_wall_004", "T362_001": "painted_concrete", "T362_002": "brown_planks_05",
    "T383_008": "concrete_tiles", "T383_010": "metal_plate", "T392_006": "castle_brick_02_white",
    "T406_009": "brick_wall_006", "T415_003": "beige_wall_001", "T416_001": "brushed_concrete_04",
    "T429_000": "concrete_block_wall_03", "T429_002": "concrete_block_wall_02", "T430_000": "brushed_concrete",
    "T430_001": "painted_concrete_02", "T430_002": "cracked_concrete_wall", "T430_003": "blue_metal_plate",
    "T430_007": "concrete_floor_painted", "T430_008": "brushed_concrete_2", "T436_000": "corrugated_iron_02",
    "T445_000": "painted_plaster_wall", "T445_001": "painted_concrete", "T446_003": "dark_wooden_planks",
    "T446_004": "rustic_stone_wall_02", "T469_002": "factory_wall", "T493_003": "checkered_pavement_tiles",
    "T493_004": "brown_mud_dry", "T495_000": "chipped_concrete", "T495_001": "brushed_concrete_03",
    "T508_002": "brick_moss_001",
    # corridors / interiors
    "T383_000": "rusty_metal", "T383_001": "rusty_metal_02", "T383_002": "concrete_wall_006",
    "T383_003": "metal_plate", "T383_005": "concrete_wall_003", "T383_006": "metal_plate_02",
    "T383_007": "blue_metal_plate", "T382_004": "concrete_floor_02", "T382_005": "concrete_wall_004",
    "T382_006": "concrete_wall_004", "T195_001": "metal_plate_02", "T195_006": "concrete_wall_007",
    "T195_007": "concrete_wall_005", "T235_002": "metal_grate_rusty", "T235_009": "concrete_wall_004",
    "T435_001": "concrete_wall_001", "T435_002": "rusty_metal_grid", "T455_000": "concrete_floor_02",
    # city buildings / rubble
    "T462_002": "medieval_blocks_05", "T462_004": "brick_wall_005", "T462_005": "concrete_wall_006",
    "T462_006": "concrete_wall_008", "T464_001": "concrete_moss", "T464_002": "mossy_rock",
    "T407_001": "brick_wall_003", "T407_007": "rusty_corrugated_iron", "T406_002": "castle_brick_07",
    "T406_006": "concrete_block_wall_02", "T406_007": "metal_plate", "T493_001": "brown_mud_dry",
    "T493_002": "rusty_metal_02", "T420_000": "weathered_brown_planks", "T420_001": "weathered_planks",
    "T420_002": "dark_wooden_planks", "T391_000": "damaged_plaster", "T391_001": "grey_plaster_02",
    "T391_003": "dark_rock", "T391_004": "blue_metal_plate", "T391_006": "wood_planks",
    "T391_007": "wood_planks_grey", "T390_001": "dark_wood",
    # sewers / base
    "T508_000": "concrete_moss", "T508_001": "concrete_moss", "T508_004": "mossy_rock",
    "T508_005": "green_metal_rust", "T508_006": "green_metal_rust", "T457_005": "rusty_metal_03",
    "T509_002": "concrete_floor_01", "T509_003": "gravel_concrete", "T509_004": "mud_cracked_dry_03",
    "T509_005": "dirty_tiles", "T509_006": "worn_tile_floor", "T509_012": "dark_rock",
}

# How much of the DOS record's colour statistics the photo takes (the Godot port's default).
KEEP = 0.85


def die(message: str) -> None:
    print(message, file=sys.stderr)
    sys.exit(2)


def imports():
    try:
        import numpy as np
        from PIL import Image
    except ImportError:
        die("needs Pillow and numpy: python -I -m pip install pillow numpy")
    return np, Image


def src_of(srcs, tid: str) -> Path:
    """The first of the CC0 folders (--src a;b, a list or one path) that holds <tid>/; the first when none does."""
    srcs = srcs if isinstance(srcs, (list, tuple)) else [srcs]
    return next((s for s in srcs if (s / tid).is_dir()), srcs[0])


def src_list(text: str) -> list[Path]:
    """--src: one folder, or several separated by the path separator (; on Windows, : elsewhere)."""
    import os
    return [Path(x) for x in text.split(os.pathsep) if x]


def find_photo(src, tid: str) -> Path | None:
    folder = src_of(src, tid) / tid
    if not folder.is_dir():
        return None
    found = sorted(folder.glob("*_diff_*.jpg")) + sorted(folder.glob("*_diff_*.png"))
    return found[0] if found else None


def match_colours(np, Image, photo, dos, keep: float):
    """The photo with the DOS record's mean and spread of luma and chroma, `keep` of the way."""
    def to_ycc(a):
        r, g, b = a[..., 0], a[..., 1], a[..., 2]
        y = 0.299 * r + 0.587 * g + 0.114 * b
        return np.stack([y, (b - y) * 0.564, (r - y) * 0.713], -1)

    def from_ycc(a):
        y, cb, cr = a[..., 0], a[..., 1], a[..., 2]
        r = y + cr / 0.713
        b = y + cb / 0.564
        g = (y - 0.299 * r - 0.114 * b) / 0.587
        return np.stack([r, g, b], -1)

    ph = to_ycc(np.asarray(photo.convert("RGB"), dtype=np.float32) / 255.0)
    ds = to_ycc(np.asarray(dos.convert("RGB"), dtype=np.float32) / 255.0)
    out = ph.copy()
    for c in range(3):
        pm, ps = ph[..., c].mean(), ph[..., c].std() + 1e-6
        dm, dsd = ds[..., c].mean(), ds[..., c].std() + 1e-6
        out[..., c] = (ph[..., c] - pm) / ps * (dsd * keep + ps * (1 - keep)) + (dm * keep + pm * (1 - keep))
    rgb = np.clip(from_ycc(out), 0.0, 1.0)
    return Image.fromarray((rgb * 255.0 + 0.5).astype("uint8"), "RGB")


def crop_to(Image, image, w: int, h: int):
    """The largest centred crop of the aspect w:h, resized to w x h."""
    iw, ih = image.size
    if iw * h > ih * w:
        cw, ch = ih * w // h, ih
    else:
        cw, ch = iw, iw * h // w
    left, top = (iw - cw) // 2, (ih - ch) // 2
    return image.crop((left, top, left + cw, top + ch)).resize((w, h), Image.LANCZOS)


def licence(mod: Path, title: str, ids: set[str], srcs=None) -> None:
    """LICENSE.txt: the CC0 sources by id, with each folder's manifest.json source and licence when
    it has them (Poly Haven otherwise)."""
    entries = {}
    for s in (srcs if isinstance(srcs, (list, tuple)) else [srcs] if srcs else []):
        path = s / "manifest.json"
        if path.exists():
            for k, v in json.loads(path.read_text(encoding="utf-8")).items():
                entries.setdefault(k, v)
    hosts = sorted({"/".join(entries.get(i, {}).get("source", "https://polyhaven.com/").split("/")[:3]) for i in ids})
    lines = [title, "",
             "Photo textures: CC0 1.0 Universal (public domain), from " + ", ".join(hosts) + ".",
             "Credit is not required; the sources are listed anyway:", ""]
    lines += [f"  {i}  {entries.get(i, {}).get('source', f'https://polyhaven.com/a/{i}')}" for i in sorted(ids)]
    lines += ["", "Made on the player's machine by tools/modkit/modkit.py; the colour reference is the",
              "player's own DOS data. Do not redistribute files derived from the DOS art."]
    (mod / "LICENSE.txt").write_text("\n".join(lines) + "\n", encoding="utf-8")


# ---------------------------------------------------------------- materials from a table (--table)
#
# A JSON table {"T302_000": entry, ...} picks the treatment per record:
#   {"id": "dark_rock_02", "span": 256}        photo: the photo tiled at its real size
#   {"id": "metal_plate", "mode": "detail"}    detail: the DOS art kept, the photo's grain on it
#   {"mode": "blend", "of": ["T302_000", ...]} blend: a transition tile mixed from other entries'
#                                              photos where its DOS texels look like their records
# Optional: "span" (world units one record repeat covers, default UNITS_PER_TEXEL x the record),
# "rotate" (90/180/270), "keep" (KEEP), "grain" (detail strength).
#
#   DOS record ──stats──┐
#   photo ──tile at real size──> colour match ──> RRR.png, + photo normal/roughness ──> RRR_n/_r.png

UNITS_PER_METRE = 40.0  # SkyNet.Game Storm.UnitsPerMetre
UNITS_PER_TEXEL = 2.0  # a 64-texel wall record spans about 128 units on MAP.210
NORMAL_SCALE = 1.0  # photo normals as made; the final tilt is set by the relief target below
GRAIN = 0.7
BLEND_SIGMA = 1.5  # texels of DOS blur before the blend weights
DOS_RELIEF = 8.0  # detail mode: normal slope per unit of DOS luma per source texel (upscale.py uses 6)

# --table: each normal map's xy gain-matched to a mean tilt (degrees, as SURFACE RELIEF LOW draws
# it; MEDIUM and HIGH tilt 1.6x and 2.5x more), an entry's "relief" or its mode's default. Measured
# on the pilot (MAP.210/211/213): under ~5 degrees the relief does not show at LOW.
RELIEF_ROCK = 21.0  # rock, brick, blocks: also every "blend" (ground transitions)
RELIEF_PANEL = 15.0  # "detail" and "photo" without a "relief": panels, seams, rivets
RELIEF_FILL = 13.5  # --fill-bank's DOS tiles (luminance relief)
RELIEF_DEFAULTS = {"blend": RELIEF_ROCK, "detail": RELIEF_PANEL, "photo": RELIEF_PANEL}
MAX_RELIEF_GAIN = 6.0  # past this a near-flat photo map is mostly JPEG noise
FILL_ROUGHNESS = 235  # --fill-bank's DOS tiles: rough ground


def photo_metres(src, tid: str) -> tuple[float, float]:
    """The photo's real width and height (m), from the fetch manifest; 2 m when unknown."""
    path = src_of(src, tid) / "manifest.json"
    if not path.exists():
        return 2.0, 2.0
    dims = json.loads(path.read_text(encoding="utf-8")).get(tid, {}).get("dimensions_mm") or [2000, 2000]
    return dims[0] / 1000.0, dims[1] / 1000.0


def tiled(Image, image, w: int, h: int, rx: int, ry: int):
    """A seamless photo repeated rx x ry times over w x h (whole repeats keep the seams invisible)."""
    tile = image.resize((max(1, round(w / rx)), max(1, round(h / ry))), Image.LANCZOS)
    out = Image.new(image.mode, (tile.width * rx, tile.height * ry))
    for y in range(ry):
        for x in range(rx):
            out.paste(tile, (x * tile.width, y * tile.height))
    return out.resize((w, h), Image.LANCZOS)


def layer(Image, src: Path, tid: str, kinds: tuple[str, ...], w: int, h: int, e: dict, span: float):
    """One of the photo's maps tiled over the record at the photo's real size, turned by "rotate"."""
    path = find_map(src, tid, kinds)
    if path is None:
        return None
    pw, ph = photo_metres(src, tid)
    metres = span / UNITS_PER_METRE
    rx = max(1, round(metres / pw))
    ry = max(1, round(metres * h / w / ph))
    image = Image.open(path)
    turn = int(e.get("rotate", 0)) % 360
    if turn:
        image = image.rotate(turn, expand=True)
    return tiled(Image, image, w, h, rx, ry)


def flip_normal(np, Image, nor, rotate: int):
    """GL normal (green up) -> the game's (y down the rows), turned with the image, tilt scaled."""
    n = np.asarray(nor.convert("RGB"), dtype=np.float32) / 127.5 - 1.0
    x, y = n[..., 0], -n[..., 1]
    for _ in range(rotate // 90 % 4):
        x, y = y, -x  # the image turned 90 degrees counter-clockwise: its vectors turn with it
    z = np.maximum(n[..., 2], 0.05)
    x, y = x * NORMAL_SCALE, y * NORMAL_SCALE
    length = np.sqrt(x * x + y * y + z * z)
    out = np.stack([x, y, z], -1) / length[..., None]
    return Image.fromarray(np.clip(128.0 + 127.0 * out, 0, 255).astype("uint8"), "RGB")


def grain(np, Image, photo, strength: float):
    """The photo's luma relative to its local mean: 1 = flat, its fine detail around it."""
    from PIL import ImageFilter
    luma = photo.convert("L")
    blur = luma.filter(ImageFilter.GaussianBlur(max(2, photo.width // 32)))
    ratio = (np.asarray(luma, np.float32) + 8.0) / (np.asarray(blur, np.float32) + 8.0)
    return 1.0 + strength * (ratio - 1.0)


def dos_relief(np, Image, dos, photo_normal, w: int, h: int, scale: int):
    """The photo's normal tilted by the DOS art's own edges (luma as height): panel seams keep relief."""
    from PIL import ImageFilter
    luma = dos.convert("L").resize((w, h), Image.BICUBIC).filter(ImageFilter.GaussianBlur(0.6 * scale))
    hgt = np.asarray(luma, np.float32) / 255.0
    gx = (np.roll(hgt, -1, 1) - np.roll(hgt, 1, 1)) / 2.0 * scale * DOS_RELIEF  # per source texel, wrapped
    gy = (np.roll(hgt, -1, 0) - np.roll(hgt, 1, 0)) / 2.0 * scale * DOS_RELIEF
    n = np.asarray(photo_normal, np.float32) / 127.5 - 1.0
    x, y, z = n[..., 0] - gx, n[..., 1] - gy, n[..., 2]
    length = np.sqrt(x * x + y * y + z * z)
    out = np.stack([x, y, z], -1) / length[..., None]
    return Image.fromarray(np.clip(128.0 + 127.0 * out, 0, 255).astype("uint8"), "RGB")


def mean_tilt(np, n) -> float:
    """Mean angle (degrees) of unit normals n (H x W x 3) from straight out."""
    return float(np.degrees(np.arccos(np.clip(n[..., 2] / np.linalg.norm(n, axis=-1), -1, 1))).mean())


def tilt_gain(np, n, gain: float):
    """Normals with x and y times gain, renormalised."""
    x, y, z = n[..., 0] * gain, n[..., 1] * gain, np.maximum(n[..., 2], 0.05)
    length = np.sqrt(x * x + y * y + z * z)
    return np.stack([x, y, z], -1) / length[..., None]


def relief_to(np, Image, normal, target: float):
    """The normal map (an image, or unit normals H x W x 3) with its tilt gain-matched to a mean of
    target degrees (gain capped; 0: flat)."""
    n = normal if isinstance(normal, np.ndarray) else np.asarray(normal.convert("RGB"), np.float32) / 127.5 - 1.0
    if target <= 0:
        return Image.new("RGB", (n.shape[1], n.shape[0]), (128, 128, 255)), 0.0
    lo, hi = 0.0, MAX_RELIEF_GAIN
    for _ in range(30):  # bisection: the mean tilt grows with the gain
        g = (lo + hi) / 2
        lo, hi = (g, hi) if mean_tilt(np, tilt_gain(np, n, g)) < target else (lo, g)
    out = tilt_gain(np, n, lo)
    return Image.fromarray(np.clip(np.round(128.0 + 127.0 * out), 0, 255).astype("uint8"), "RGB"), lo


def wrapped_blur(np, h, sigma: float):
    """Separable Gaussian (radius 3 sigma), wrapped: the tile repeats."""
    r = int(math.ceil(3 * sigma))
    w = np.exp(-np.arange(-r, r + 1) ** 2 / (2 * sigma * sigma))
    w /= w.sum()
    for axis in (0, 1):
        h = sum(wi * np.roll(h, i - r, axis) for i, wi in enumerate(w))
    return h


def luma_relief(np, Image, rgb, scale: int):
    """SkyNet.Upscale.ReliefMaps' normal: luminance blurred 0.6 source texels, Sobel, 6 source texels
    of depth a unit of luminance, as unit normals (for relief_to)."""
    a = np.asarray(rgb.convert("RGB"), np.float32) / 255.0
    h = wrapped_blur(np, 0.2126 * a[..., 0] + 0.7152 * a[..., 1] + 0.0722 * a[..., 2], 0.6 * scale)
    at = lambda dx, dy: np.roll(np.roll(h, -dy, 0), -dx, 1)
    gx = at(1, -1) + 2 * at(1, 0) + at(1, 1) - at(-1, -1) - 2 * at(-1, 0) - at(-1, 1)
    gy = at(-1, 1) + 2 * at(0, 1) + at(1, 1) - at(-1, -1) - 2 * at(0, -1) - at(1, -1)
    k = 6.0 * scale / 8
    n = np.stack([-gx * k, -gy * k, np.ones_like(gx)], -1)
    return n / np.linalg.norm(n, axis=-1)[..., None]


def blend_weights(np, Image, dos, refs, w: int, h: int):
    """Per output texel, how much the DOS texel looks like each reference record (mean colour)."""
    from PIL import ImageFilter
    soft = dos.filter(ImageFilter.GaussianBlur(BLEND_SIGMA)).resize((w, h), Image.BILINEAR)
    px = np.asarray(soft, np.float32)
    means = [np.asarray(r, np.float32).reshape(-1, 3).mean(0) for r in refs]
    d = np.stack([((px - m) ** 2).sum(-1) for m in means], 0)
    gap = min(((a - b) ** 2).sum() for i, a in enumerate(means) for b in means[i + 1:])
    wts = np.exp(-(d - d.min(0)) / max(gap * 0.15, 1.0))
    return wts / wts.sum(0)


def make_entry(np, Image, export: Path, src: Path, records: dict, table: dict, key: str, size: int, cache: dict):
    """(colour, normal, roughness) of one table record at its mod size; None: skipped."""
    if key in cache:
        return cache[key]
    e, r = table[key], records[key]
    w, h = r["width"], r["height"]
    scale = max(1, size // max(w, h))
    W, H = w * scale, h * scale
    dos = Image.open(export / f"TEXTURE.{r['bank']:03d}" / f"{r['record']:03d}.png").convert("RGB")
    dos = dos.crop((0, 0, w, h))  # first frame
    mode = e.get("mode", "photo")
    span = float(e.get("span", UNITS_PER_TEXEL * max(w, h)))
    rotate = int(e.get("rotate", 0)) % 360
    result = None

    # blend: other entries' finished layers mixed by how the DOS texels look
    if mode == "blend":
        parts = [make_entry(np, Image, export, src, records, table, k, size, cache) for k in e["of"]]
        refs = [Image.open(export / f"TEXTURE.{records[k]['bank']:03d}" / f"{records[k]['record']:03d}.png")
                .convert("RGB").crop((0, 0, records[k]["width"], records[k]["height"])) for k in e["of"]]
        wts = blend_weights(np, Image, dos, refs, W, H)
        mixed = []
        for layer_index in range(3):
            acc = sum(wts[i][..., None] * np.asarray(p[layer_index].convert("RGB").resize((W, H)), np.float32)
                      for i, p in enumerate(parts))
            mixed.append(Image.fromarray(np.clip(acc, 0, 255).astype("uint8"), "RGB"))
        result = (mixed[0], mixed[1], mixed[2].convert("L"))
        cache[key] = result
        return result

    tid = e["id"]
    photo = layer(Image, src, tid, ("diff",), W, H, e, span)
    if photo is None:
        print(f"{key}: no {tid}/*_diff_* under {src}, skipped")
        cache[key] = None
        return None
    nor = layer(Image, src, tid, ("nor_gl",), W, H, e, span)
    rough = layer(Image, src, tid, ("rough",), W, H, e, span)
    normal = flip_normal(np, Image, nor, rotate) if nor else Image.new("RGB", (W, H), (128, 128, 255))
    rough = rough.convert("L") if rough else Image.new("L", (W, H), 220)

    # detail: the DOS art (smoothly scaled) carries the layout, the photo only its grain and relief
    if mode == "detail":
        base = np.asarray(dos.resize((W, H), Image.LANCZOS), np.float32)
        g = grain(np, Image, photo.convert("RGB"), float(e.get("grain", GRAIN)))
        colour = Image.fromarray(np.clip(base * g[..., None], 0, 255).astype("uint8"), "RGB")
        result = (colour, dos_relief(np, Image, dos, normal, W, H, scale), rough)
        cache[key] = result
        return result

    colour = match_colours(np, Image, photo.convert("RGB"), dos, float(e.get("keep", KEEP)))
    result = (colour, normal, rough)
    cache[key] = result
    return result


def cmd_table(a, np, Image, export: Path, src: Path, records: dict) -> None:
    table = json.loads(Path(a.table).read_text(encoding="utf-8"))
    mod = Path(a.mods) / a.name
    cache: dict = {}
    written, used = 0, set()
    scales: dict[int, int] = {}
    for key in sorted(table):
        if key.startswith("_"):
            continue
        r = records.get(key)
        if r is None:
            print(f"{key}: not a record of this data set, skipped")
            continue
        made = make_entry(np, Image, export, src, records, table, key, a.size, cache)
        if made is None:
            continue
        colour, normal, rough = made

        # the relief: tilt gain-matched to the entry's target (blend parts stay as made)
        mode = table[key].get("mode", "photo")
        target = float(table[key].get("relief", RELIEF_DEFAULTS[mode]))
        normal, gain = relief_to(np, Image, normal, target)

        # an animated record: every frame gets the first frame's replacement (stacked downwards)
        frames = r["frames"]
        out = mod / "textures" / f"TEXTURE.{r['bank']:03d}" / f"{r['record']:03d}"
        out.parent.mkdir(parents=True, exist_ok=True)
        for image, suffix in ((colour.convert("RGBA"), ""), (normal, "_n"), (rough, "_r")):
            if frames > 1:
                stack = Image.new(image.mode, (image.width, image.height * frames))
                for f in range(frames):
                    stack.paste(image, (0, f * image.height))
                image = stack
            image.save(out.with_name(out.name + suffix + ".png"), optimize=True)
        scales[r["bank"]] = colour.width // r["width"]
        used.update(x for x in [table[key].get("id")] if x)
        written += 1
        print(f"{key} <- {mode} {table[key].get('id', table[key].get('of'))}  {colour.width}x{colour.height}  relief {target:g} deg (gain {gain:.2f})")

    # the ground's atlas takes HD tiles only when every tile has one at one scale: the rest DOS, nearest,
    # with luminance relief (the atlas would draw them flat)
    if a.fill_bank and a.fill_bank in scales:
        bank, scale = a.fill_bank, scales[a.fill_bank]
        for r in (x for x in records.values() if x["bank"] == bank):
            out = mod / "textures" / f"TEXTURE.{bank:03d}" / f"{r['record']:03d}.png"
            if out.exists():
                continue
            img = Image.open(export / f"TEXTURE.{bank:03d}" / f"{r['record']:03d}.png").convert("RGBA")
            big = img.resize((img.width * scale, img.height * scale), Image.NEAREST)
            big.save(out, optimize=True)
            normal, gain = relief_to(np, Image, luma_relief(np, Image, big, scale), RELIEF_FILL)
            normal.save(out.with_name(out.stem + "_n.png"), optimize=True)
            Image.new("L", big.size, FILL_ROUGHNESS).save(out.with_name(out.stem + "_r.png"), optimize=True)
            print(f"T{bank:03d}_{r['record']:03d} <- DOS nearest x{scale} (fill), relief {RELIEF_FILL:g} deg (gain {gain:.2f})")
    if written:
        licence(mod, f"{a.name}: DOS wall, floor and ground textures with CC0 photo textures", used, src)
    print(f"{written} textures written to {mod}")


def cmd_materials(a) -> None:
    np, Image = imports()
    export, src = Path(a.export), src_list(a.src)
    if a.table:
        manifest = json.loads((export / "manifest.json").read_text())["records"]
        cmd_table(a, np, Image, export, src, {f"T{r['bank']:03d}_{r['record']:03d}": r for r in manifest})
        return
    manifest = json.loads((export / "manifest.json").read_text())["records"]
    records = {f"T{r['bank']:03d}_{r['record']:03d}": r for r in manifest}
    only = {x for x in a.only.split(",") if x}
    mod = Path(a.mods) / a.name
    written, used = 0, set()
    for key, tid in sorted(MATERIALS.items()):
        if only and key not in only and tid not in only:
            continue
        r = records.get(key)
        if r is None:
            print(f"{key}: not a record of this data set, skipped")
            continue
        if r["frames"] != 1 or r["holes"] or r["layout"] != "wall":
            print(f"{key}: animated, cut-out or a sprite, skipped")
            continue
        photo_path = find_photo(src, tid)
        if photo_path is None:
            print(f"{key}: no {tid}/*_diff_*.jpg under {src}, skipped")
            continue
        w, h = r["width"], r["height"]
        scale = max(1, a.size // max(w, h))
        dos = Image.open(export / f"TEXTURE.{r['bank']:03d}" / f"{r['record']:03d}.png").convert("RGB")
        photo = crop_to(Image, Image.open(photo_path).convert("RGB"), w * scale, h * scale)
        graded = match_colours(np, Image, photo, dos, KEEP).convert("RGBA")
        out = mod / "textures" / f"TEXTURE.{r['bank']:03d}" / f"{r['record']:03d}.png"
        out.parent.mkdir(parents=True, exist_ok=True)
        graded.save(out, optimize=True)
        used.add(tid)
        written += 1
        print(f"{key} <- {tid}  {w * scale}x{h * scale}")
    if written:
        licence(mod, "cc0-materials: DOS wall and floor textures replaced by CC0 photo textures", used, src)
    print(f"{written} textures written to {mod}")


# ---------------------------------------------------------------- glTF writer

def glb(meshes: list[dict], images: list[tuple[bytes, str]]) -> bytes:
    """A .glb of one mesh, a primitive per part: each part {name, positions, normals, uvs, indices,
    material}; material {colour, image, normal, metallic_roughness, metallic, roughness, alpha,
    cutoff, double_sided, emissive} (images by index into `images`, (bytes, mime type) each)."""
    bin_ = bytearray()
    views, accessors, prims, materials = [], [], [], []

    def view(data: bytes) -> int:
        while len(bin_) % 4:
            bin_.append(0)
        views.append({"buffer": 0, "byteOffset": len(bin_), "byteLength": len(data)})
        bin_.extend(data)
        return len(views) - 1

    def floats(values: list[tuple], kind: str, bounds: bool = False) -> int:
        flat = [c for v in values for c in v]
        acc = {"bufferView": view(struct.pack(f"<{len(flat)}f", *flat)), "componentType": 5126,
               "count": len(values), "type": kind}
        if bounds:
            acc["min"] = [min(v[i] for v in values) for i in range(3)]
            acc["max"] = [max(v[i] for v in values) for i in range(3)]
        accessors.append(acc)
        return len(accessors) - 1

    for part in meshes:
        attributes = {"POSITION": floats(part["positions"], "VEC3", True),
                      "NORMAL": floats(part["normals"], "VEC3"),
                      "TEXCOORD_0": floats(part["uvs"], "VEC2")}
        idx = part["indices"]
        accessors.append({"bufferView": view(struct.pack(f"<{len(idx)}I", *idx)), "componentType": 5125,
                          "count": len(idx), "type": "SCALAR"})
        m = part.get("material", {})
        pbr = {"baseColorFactor": list(m.get("colour", (1.0, 1.0, 1.0, 1.0))),
               "metallicFactor": m.get("metallic", 0.0), "roughnessFactor": m.get("roughness", 0.9)}
        material = {"name": part["name"], "pbrMetallicRoughness": pbr}
        if "image" in m:
            pbr["baseColorTexture"] = {"index": m["image"]}
        if "metallic_roughness" in m:
            pbr["metallicRoughnessTexture"] = {"index": m["metallic_roughness"]}
        if "normal" in m:
            material["normalTexture"] = {"index": m["normal"], "scale": m.get("normal_scale", 1.0)}
        if "emissive" in m:
            material["emissiveFactor"] = list(m["emissive"])
        if m.get("alpha", "OPAQUE") != "OPAQUE":
            material["alphaMode"] = m["alpha"]
            if m["alpha"] == "MASK":
                material["alphaCutoff"] = m.get("cutoff", 0.5)
        if m.get("double_sided"):
            material["doubleSided"] = True
        materials.append(material)
        prims.append({"attributes": attributes, "indices": len(accessors) - 1, "material": len(materials) - 1})

    image_json = [{"bufferView": view(data), "mimeType": mime} for data, mime in images]
    while len(bin_) % 4:
        bin_.append(0)
    doc = {"asset": {"version": "2.0", "generator": "skynet-sdl tools/modkit"},
           "scene": 0, "scenes": [{"nodes": [0]}], "nodes": [{"mesh": 0}],
           "meshes": [{"primitives": prims}], "materials": materials,
           "textures": [{"source": i} for i in range(len(images))], "images": image_json,
           "buffers": [{"byteLength": len(bin_)}], "bufferViews": views, "accessors": accessors}
    if not images:
        del doc["textures"], doc["images"]
    js = json.dumps(doc, separators=(",", ":")).encode()
    js += b" " * (-len(js) % 4)
    total = 12 + 8 + len(js) + 8 + len(bin_)
    return (struct.pack("<III", 0x46546C67, 2, total) + struct.pack("<II", len(js), 0x4E4F534A) + js
            + struct.pack("<II", len(bin_), 0x004E4942) + bytes(bin_))


class Builder:
    """Boxes in metres (Y up) gathered per material into glb parts."""

    def __init__(self):
        self.parts: dict[str, dict] = {}

    def part(self, material: str) -> dict:
        return self.parts.setdefault(material, {"name": material, "positions": [], "normals": [], "uvs": [], "indices": []})

    def box(self, material: str, lo: tuple, hi: tuple, uv_scale: float = 1.0) -> None:
        """An axis-aligned box, each face its own four corners, counter-clockwise from outside,
        UVs from the face's size in metres times uv_scale (so the texture keeps its scale). A face's
        u runs from its first corner to its second, the image's up from its first to its fourth
        (the game makes the tangents for the normal maps from these)."""
        p = self.part(material)
        x0, y0, z0 = lo
        x1, y1, z1 = hi
        faces = [  # (normal, four corners counter-clockwise seen from outside)
            ((0, 0, 1), [(x0, y0, z1), (x1, y0, z1), (x1, y1, z1), (x0, y1, z1)]),
            ((0, 0, -1), [(x1, y0, z0), (x0, y0, z0), (x0, y1, z0), (x1, y1, z0)]),
            ((1, 0, 0), [(x1, y0, z1), (x1, y0, z0), (x1, y1, z0), (x1, y1, z1)]),
            ((-1, 0, 0), [(x0, y0, z0), (x0, y0, z1), (x0, y1, z1), (x0, y1, z0)]),
            ((0, 1, 0), [(x0, y1, z1), (x1, y1, z1), (x1, y1, z0), (x0, y1, z0)]),
            ((0, -1, 0), [(x0, y0, z0), (x1, y0, z0), (x1, y0, z1), (x0, y0, z1)]),
        ]
        for n, corners in faces:
            a, b, d = corners[0], corners[1], corners[3]
            u = math.dist(a, b) * uv_scale
            v = math.dist(a, d) * uv_scale
            base = len(p["positions"])
            p["positions"] += corners
            p["normals"] += [n] * 4
            p["uvs"] += [(0, v), (u, v), (u, 0), (0, 0)]
            p["indices"] += [base, base + 1, base + 2, base, base + 2, base + 3]

    def cylinder(self, material: str, axis: str, centre: tuple, radius: float, start: float, end: float,
                 sides: int = 12, uv_scale: float = 1.0) -> None:
        """A capped prism of `sides` sides along X or Z (`axis` "x" or "z") from `start` to `end`,
        centred on `centre` (y, z) for X or (x, y) for Z; smooth sides, flat caps, each triangle
        counter-clockwise from outside."""
        p = self.part(material)

        def at(angle: float, t: float, r: float = radius) -> tuple:
            c, s_ = math.cos(angle) * r, math.sin(angle) * r
            return (t, centre[0] + s_, centre[1] + c) if axis == "x" else (centre[0] + c, centre[1] + s_, t)

        def unit_radial(angle: float) -> tuple:
            if axis == "z":
                return (math.cos(angle), math.sin(angle), 0.0)
            return (0.0, math.sin(angle), math.cos(angle))

        def tri(a: int, b: int, c: int, outward: tuple) -> None:
            pa, pb, pc = (p["positions"][i] for i in (a, b, c))
            u = [pb[i] - pa[i] for i in range(3)]
            v = [pc[i] - pa[i] for i in range(3)]
            n = (u[1] * v[2] - u[2] * v[1], u[2] * v[0] - u[0] * v[2], u[0] * v[1] - u[1] * v[0])
            if sum(n[i] * outward[i] for i in range(3)) < 0:
                b, c = c, b
            p["indices"] += [a, b, c]

        step = 2 * math.pi / sides
        around = 2 * math.pi * radius * uv_scale
        for i in range(sides):
            a0, a1 = i * step, (i + 1) * step
            base = len(p["positions"])
            p["positions"] += [at(a0, start), at(a1, start), at(a1, end), at(a0, end)]
            p["normals"] += [unit_radial(a0), unit_radial(a1), unit_radial(a1), unit_radial(a0)]
            u0, u1 = i / sides * around, (i + 1) / sides * around
            p["uvs"] += [(u0, start * uv_scale), (u1, start * uv_scale), (u1, end * uv_scale), (u0, end * uv_scale)]
            mid = unit_radial((a0 + a1) / 2)
            tri(base, base + 1, base + 2, mid)
            tri(base, base + 2, base + 3, mid)
        for t, sign in ((start, -1.0), (end, 1.0)):
            normal = (sign, 0.0, 0.0) if axis == "x" else (0.0, 0.0, sign)
            base = len(p["positions"])
            p["positions"].append(at(0.0, t, 0.0))
            p["normals"].append(normal)
            p["uvs"].append((0.5, 0.5))
            for i in range(sides):
                p["positions"].append(at(i * step, t))
                p["normals"].append(normal)
                p["uvs"].append((0.5 + 0.5 * math.cos(i * step), 0.5 + 0.5 * math.sin(i * step)))
            for i in range(sides):
                tri(base, base + 1 + i, base + 1 + (i + 1) % sides, normal)

    def write(self, path: Path, materials: dict[str, "Surface"]) -> None:
        """The model with each part's material; parts without one are plain white."""
        images: list[tuple[bytes, str]] = []

        def image(entry: tuple[bytes, str] | None) -> int | None:
            if entry is None:
                return None
            images.append(entry)
            return len(images) - 1

        parts = []
        for name, part in self.parts.items():
            s = materials.get(name)
            if s is not None:
                m = {"metallic": s.metallic, "roughness": s.roughness, "alpha": s.alpha, "cutoff": s.cutoff,
                     "double_sided": s.double_sided, "colour": s.factor}
                if s.emissive is not None:
                    m["emissive"] = s.emissive
                for key, entry in (("image", s.colour), ("normal", s.normal), ("metallic_roughness", s.metallic_roughness)):
                    index = image(entry)
                    if index is not None:
                        m[key] = index
                part["material"] = m
            parts.append(part)
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(glb(parts, images))


class Surface:
    """A model material from a CC0 texture: its colour photo (JPEG), its normal map (OpenGL
    convention, +Y up: glTF's) and roughness map when the source folder has them (PNG), the
    metallic and roughness factors, and an alpha mode."""

    def __init__(self, colour, normal=None, metallic_roughness=None, metallic=0.0, roughness=0.9,
                 alpha="OPAQUE", cutoff=0.5, double_sided=False, factor=(1.0, 1.0, 1.0, 1.0), emissive=None):
        self.colour, self.normal, self.metallic_roughness = colour, normal, metallic_roughness
        self.metallic, self.roughness = metallic, roughness
        self.alpha, self.cutoff, self.double_sided = alpha, cutoff, double_sided
        self.factor, self.emissive = factor, emissive


def plain(colour, metallic=0.0, roughness=0.8, emissive=None) -> Surface:
    """An untextured material: a base colour factor (linear RGBA), optionally glowing."""
    return Surface(None, metallic=metallic, roughness=roughness, factor=colour, emissive=emissive)


def find_map(src, tid: str, kinds: tuple[str, ...]) -> Path | None:
    """A Poly Haven map of the texture: <id>/<id>_<kind>_*.jpg/png, the first kind found."""
    folder = src_of(src, tid) / tid
    for kind in kinds:
        found = sorted(folder.glob(f"*_{kind}_*.jpg")) + sorted(folder.glob(f"*_{kind}_*.png"))
        if found:
            return found[0]
    return None


def encode(image, fmt: str) -> tuple[bytes, str]:
    out = io.BytesIO()
    if fmt == "JPEG":
        image.convert("RGB").save(out, "JPEG", quality=90, subsampling=0, optimize=True, progressive=True)
        return out.getvalue(), "image/jpeg"
    image.save(out, "PNG", optimize=True)
    return out.getvalue(), "image/png"


def surface(Image, src: Path, tid: str, size: int = 512, tint=None, frame: float = 0.0, grey: bool = False,
            slats: int = 0, metallic: float = 0.0, roughness: float = 0.9, maps: bool = True) -> Surface:
    """A CC0 photo texture as a square material: `frame` darkens a border of that fraction (crate
    slats), `grey` drops its colour (bare steel), `slats` cuts that
    many gaps across it (an alpha MASK, drawn from both sides). The normal map and a roughness map
    come cropped like the photo; without a roughness map the factor stands alone."""
    from PIL import ImageDraw
    path = find_photo(src, tid)
    if path is None:
        die(f"no {tid}/*_diff_*.jpg under {src}")
    image = crop_to(Image, Image.open(path).convert("RGB"), size, size)
    if grey:
        image = image.convert("L").convert("RGB")
    if tint:
        r, g, b = image.split()
        image = Image.merge("RGB", [ch.point(lambda x, k=k: min(255, int(x * k))) for ch, k in zip((r, g, b), tint)])
    border = int(size * frame)
    inner = (border, border, size - 1 - border, size - 1 - border)
    if frame > 0:
        dark = image.point(lambda x: int(x * 0.55))
        mask = Image.new("L", (size, size), 255)
        ImageDraw.Draw(mask).rectangle(inner, fill=0)
        image = Image.composite(dark, image, mask)
    alpha = "OPAQUE"
    if slats > 0:
        cover = Image.new("L", (size, size), 255)
        draw = ImageDraw.Draw(cover)
        gap = size // (slats * 2 + 1)
        for i in range(slats):
            top = gap * (2 * i + 1)
            draw.rectangle((0, top, size - 1, top + gap - 1), fill=0)
        image = image.convert("RGBA")
        image.putalpha(cover)
        alpha = "MASK"
    colour = encode(image, "PNG" if slats > 0 else "JPEG")

    normal = None
    normal_path = find_map(src, tid, ("nor_gl",)) if maps else None
    if normal_path is not None:
        normal = encode(crop_to(Image, Image.open(normal_path).convert("RGB"), size, size), "PNG")

    metallic_roughness = None
    rough_path = find_map(src, tid, ("rough",)) if maps else None
    if rough_path is not None:
        rough = crop_to(Image, Image.open(rough_path).convert("L"), size, size)
        full = Image.new("L", (size, size), 255)
        metallic_roughness = encode(Image.merge("RGB", (full, rough, full)), "PNG")
    return Surface(colour, normal, metallic_roughness, metallic, roughness, alpha, 0.5, slats > 0)


def sidecar(path: Path, values: dict) -> None:
    path.write_text(json.dumps(values, indent=2) + "\n", encoding="utf-8")


def cmd_props(a) -> None:
    np, Image = imports()
    src = Path(a.src)
    mod = Path(a.mods) / a.name
    models = mod / "models"
    used = set()
    maps = not a.no_maps

    # A wooden crate: one box, the planks with a dark slat frame on every face. The DOS crates are
    # boxes of different proportions, so the model is stretched to each one's box.
    crate = Builder()
    crate.box("planks", (-0.5, 0.0, -0.5), (0.5, 1.0, 0.5))
    planks = surface(Image, src, "weathered_planks", frame=0.09, roughness=0.85, maps=maps)
    used.add("weathered_planks")
    for name in ("CRATE01", "CRATE02", "CRATE03"):
        crate.write(models / f"{name}.glb", {"planks": planks})
        sidecar(models / f"{name}.json", {"fit": "stretch", "align": "centre",
                                           "comment": "generated wooden crate, CC0 texture weathered_planks"})

    # A mess table: a wooden top between two benches on a steel frame, as the DOS one (benches
    # along its long sides), stretched to the DOS table's box.
    steel = surface(Image, src, "metal_plate", grey=True, metallic=0.85, roughness=0.45, maps=maps)
    table = Builder()
    table.box("top", (-0.36, 0.72, -0.9), (0.36, 0.76, 0.9), 1.0)
    for side in (-1, 1):
        table.box("top", (min(side * 0.55, side * 0.85), 0.42, -0.9), (max(side * 0.55, side * 0.85), 0.46, 0.9), 1.0)
        table.box("steel", (side * 0.03 - 0.02, 0.0, -0.75), (side * 0.03 + 0.02, 0.72, -0.71), 2.0)
        table.box("steel", (side * 0.03 - 0.02, 0.0, 0.71), (side * 0.03 + 0.02, 0.72, 0.75), 2.0)
    for z in (-0.75, 0.71):
        table.box("steel", (-0.8, 0.38, z), (0.8, 0.42, z + 0.04), 2.0)
    used |= {"dark_wood", "metal_plate"}
    table.write(models / "24MESTBL.glb", {"top": surface(Image, src, "dark_wood", roughness=0.7, maps=maps), "steel": steel})
    sidecar(models / "24MESTBL.json", {"fit": "stretch", "align": "centre",
                                       "comment": "generated mess table, CC0 textures dark_wood and metal_plate"})

    # A chair: seat, a slatted back (cut out: an alpha MASK) and four steel legs (the back along
    # -X, as the DOS chair's).
    chair = Builder()
    chair.box("wood", (-0.22, 0.42, -0.22), (0.22, 0.46, 0.22), 2.0)
    chair.box("back", (-0.22, 0.46, -0.22), (-0.20, 0.95, 0.22), 2.0)
    for x in (-0.22, 0.18):
        for z in (-0.22, 0.18):
            chair.box("steel", (x, 0.0, z), (x + 0.04, 0.42, z + 0.04), 2.0)
    chair.write(models / "CHAIR0S.glb", {"wood": surface(Image, src, "brown_planks_03", roughness=0.8, maps=maps),
                                          "back": surface(Image, src, "brown_planks_03", roughness=0.8, slats=3, maps=maps),
                                          "steel": steel})
    used |= {"brown_planks_03"}
    sidecar(models / "CHAIR0S.json", {"fit": "stretch", "align": "centre",
                                      "comment": "generated chair, CC0 textures brown_planks_03 and metal_plate"})

    licence(mod, "cc0-props: DOS props replaced by generated glTF models with CC0 photo textures", used)
    print(f"5 models written to {models}" + (" (normal maps from the sources' *_nor_gl_*)" if maps else " (no maps)"))


def cmd_robots(a) -> None:
    np, Image = imports()
    src = Path(a.src)
    mod = Path(a.mods) / a.name
    models = mod / "models"
    maps = not a.no_maps
    used = {"concrete_wall_004", "metal_plate", "green_metal_rust", "rusty_metal"}
    concrete = surface(Image, src, "concrete_wall_004", roughness=0.9, maps=maps)
    steel = surface(Image, src, "metal_plate", grey=True, metallic=0.85, roughness=0.45, maps=maps)
    armour = surface(Image, src, "green_metal_rust", metallic=0.4, roughness=0.6, maps=maps)
    dark = surface(Image, src, "rusty_metal", tint=(0.6, 0.6, 0.6), metallic=0.6, roughness=0.6, maps=maps)
    rubber = plain((0.03, 0.03, 0.03, 1.0), roughness=0.95)
    eye = plain((0.2, 0.0, 0.0, 1.0), emissive=(1.0, 0.1, 0.05))
    plasma = plain((0.1, 0.3, 0.4, 1.0), emissive=(0.3, 0.9, 1.0))

    # Every segment model is the DOS model's whole frame range ("frames": "all"; the gun towers'
    # segments have one frame each). Stretched ones are made in a unit box (-0.5..0.5) and fill
    # the DOS model's box exactly; the others are made in DOS world units in the DOS model's own
    # space (Y up, ahead = -Z), so they sit on the DOS pivots ("fit": "none", "align": "origin").
    stretched = {"fit": "stretch", "align": "centre", "frames": "all"}
    dos_units = {"fit": "none", "align": "origin", "frames": "all"}

    # GUNTWR1: the tower under a gun tower's turret: a concrete shaft on a plinth, steel bands,
    # a steel platform the turret turns on.
    tower = Builder()
    tower.box("concrete", (-0.5, -0.5, -0.5), (0.5, -0.4, 0.5))
    tower.box("concrete", (-0.36, -0.4, -0.36), (0.36, 0.4, 0.36))
    for y in (-0.05, 0.22):
        tower.box("steel", (-0.39, y, -0.39), (0.39, y + 0.05, 0.39), 2.0)
    tower.box("dark", (-0.5, 0.4, -0.5), (0.5, 0.5, 0.5))
    tower.write(models / "GUNTWR1.glb", {"concrete": concrete, "steel": steel, "dark": dark})
    sidecar(models / "GUNTWR1.json", stretched | {"comment": "generated gun tower, CC0 textures concrete_wall_004, metal_plate, rusty_metal"})

    # HVYTRRT3: the twin turret's head (its plasma barrels hang off its sides): a ring, an
    # armoured housing with a sloped front, arms out to the barrels, a red sensor eye.
    head = Builder()
    head.box("dark", (-0.3, -0.5, -0.3), (0.3, -0.3, 0.3))
    head.box("armour", (-0.42, -0.3, -0.25), (0.42, 0.35, 0.5))
    head.box("armour", (-0.36, -0.25, -0.45), (0.36, 0.22, -0.25))
    head.box("steel", (-0.5, -0.12, -0.05), (-0.42, 0.12, 0.25), 2.0)
    head.box("steel", (0.42, -0.12, -0.05), (0.5, 0.12, 0.25), 2.0)
    head.box("eye", (-0.1, 0.05, -0.5), (0.1, 0.14, -0.45))
    head.write(models / "HVYTRRT3.glb", {"armour": armour, "steel": steel, "dark": dark, "eye": eye})
    sidecar(models / "HVYTRRT3.json", stretched | {"comment": "generated twin turret head, CC0 textures green_metal_rust, metal_plate, rusty_metal"})

    # HVYTRRT2: the single turret's head (a missile pod on its side), the same parts narrower.
    head2 = Builder()
    head2.box("dark", (-0.3, -0.5, -0.3), (0.3, -0.3, 0.3))
    head2.box("armour", (-0.5, -0.3, -0.3), (0.5, 0.4, 0.5))
    head2.box("armour", (-0.4, -0.25, -0.5), (0.4, 0.25, -0.3))
    head2.box("eye", (-0.12, 0.08, -0.52), (0.12, 0.18, -0.48))
    head2.write(models / "HVYTRRT2.glb", {"armour": armour, "dark": dark, "eye": eye})
    sidecar(models / "HVYTRRT2.json", stretched | {"comment": "generated turret head, CC0 textures green_metal_rust, rusty_metal"})

    # HVYPLSM: a plasma barrel in DOS units (its box: x +-8, y +-20, z -104..20): a breech block,
    # a round barrel with cooling rings, a muzzle with a glowing core.
    barrel = Builder()
    barrel.box("armour", (-8.0, -14.0, -12.0), (8.0, 14.0, 20.0), 1 / 40)
    barrel.cylinder("steel", "z", (0.0, 0.0), 5.0, -96.0, -12.0, 14, 1 / 20)
    for z in (-80.0, -60.0, -40.0):
        barrel.cylinder("dark", "z", (0.0, 0.0), 6.5, z, z + 5.0, 14, 1 / 20)
    barrel.cylinder("dark", "z", (0.0, 0.0), 7.5, -104.0, -94.0, 14, 1 / 20)
    barrel.cylinder("plasma", "z", (0.0, 0.0), 3.5, -105.0, -103.0, 14)
    barrel.write(models / "HVYPLSM.glb", {"armour": armour, "steel": steel, "dark": dark, "plasma": plasma})
    sidecar(models / "HVYPLSM.json", dos_units | {"comment": "generated plasma barrel in DOS units, CC0 textures"})

    # HMSLPODR: a missile pod in DOS units (box x 1..20, y +-24, z -60..45): a box with six tubes.
    pod = Builder()
    pod.box("armour", (2.0, -22.0, -50.0), (20.0, 22.0, 44.0), 1 / 40)
    for x in (7.0, 15.0):
        for y in (-12.0, 0.0, 12.0):
            pod.cylinder("dark", "z", (x, y), 4.0, -60.0, -50.0, 10, 1 / 20)
            pod.cylinder("eye", "z", (x, y), 2.0, -60.5, -59.5, 8)
    pod.write(models / "HMSLPODR.glb", {"armour": armour, "dark": dark, "eye": eye})
    sidecar(models / "HMSLPODR.json", dos_units | {"comment": "generated missile pod in DOS units, CC0 textures"})

    # HUMMER: the jeep's cockpit as the driver sees it (the eye at (-26, 22, 8) of the model, the
    # left seat): a bonnet, a dashboard, a windscreen frame and the doors, in DOS units. Seen from
    # inside, so only the faces turned to the seat matter.
    cockpit = Builder()
    cockpit.box("olive", (-49.0, -12.0, -105.0), (57.0, 0.0, -48.0), 1 / 60)
    cockpit.box("olive", (-45.0, 0.0, -100.0), (53.0, 2.0, -52.0), 1 / 60)
    cockpit.box("rubber", (-49.0, -12.0, -50.0), (57.0, 5.0, -38.0))
    cockpit.box("steel", (-49.0, 5.0, -50.0), (57.0, 7.0, -47.0), 1 / 12)
    cockpit.box("steel", (-49.0, 40.0, -50.0), (57.0, 42.0, -47.0), 1 / 12)
    for x in (-49.0, 55.0):
        cockpit.box("steel", (x, 7.0, -50.0), (x + 2.0, 40.0, -47.0), 1 / 12)
    cockpit.box("olive", (-53.0, -12.0, -48.0), (-49.0, 4.0, 60.0), 1 / 60)
    cockpit.box("olive", (57.0, -12.0, -48.0), (61.0, 4.0, 60.0), 1 / 60)
    cockpit.write(models / "HUMMER.glb", {"olive": armour, "steel": steel, "rubber": rubber})
    sidecar(models / "HUMMER.json", dos_units | {"frames": [0], "comment": "generated jeep cockpit in DOS units, CC0 textures"})

    # NETHUMER / NETHUMMT: the deathmatch jeep, driven and parked (box x +-57, y -45..52,
    # z -118..116): a chassis, a bonnet, a windscreen frame, a roll bar, four wheels. The turret
    # and barrel on its roof (NETHMTRT, NETHMBRL) stay DOS's.
    jeep = Builder()
    jeep.box("olive", (-50.0, -20.0, -116.0), (50.0, 10.0, 114.0), 1 / 60)
    jeep.box("olive", (-48.0, 10.0, -116.0), (48.0, 18.0, -40.0), 1 / 60)
    jeep.box("steel", (-48.0, 18.0, -40.0), (48.0, 21.0, -36.0), 1 / 30)
    jeep.box("steel", (-48.0, 47.0, -40.0), (48.0, 50.0, -36.0), 1 / 30)
    for x in (-48.0, 45.0):
        jeep.box("steel", (x, 21.0, -40.0), (x + 3.0, 47.0, -36.0), 1 / 30)
        jeep.box("steel", (x, 10.0, 60.0), (x + 3.0, 52.0, 64.0), 1 / 30)
    jeep.box("steel", (-48.0, 49.0, 60.0), (48.0, 52.0, 64.0), 1 / 30)
    for x0, x1 in ((-57.0, -45.0), (45.0, 57.0)):
        for z in (-80.0, 78.0):
            jeep.cylinder("rubber", "x", (-22.0, z), 22.0, x0, x1, 16)
            jeep.cylinder("dark", "x", (-22.0, z), 10.0, x0 - 0.5, x1 + 0.5, 12, 1 / 20)
    jeep.write(models / "NETHUMER.glb", {"olive": armour, "steel": steel, "rubber": rubber, "dark": dark})
    jeep.write(models / "NETHUMMT.glb", {"olive": armour, "steel": steel, "rubber": rubber, "dark": dark})
    for name in ("NETHUMER", "NETHUMMT"):
        sidecar(models / f"{name}.json", {"fit": "box", "align": "base", "frames": "all",
                                          "comment": "generated deathmatch jeep, CC0 textures"})

    licence(mod, "cc0-robots: gun tower segments, the jeep cockpit and the deathmatch jeep as generated glTF models with CC0 photo textures", used)
    print(f"8 models written to {models}" + (" (normal maps from the sources' *_nor_gl_*)" if maps else " (no maps)"))


def main() -> None:
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = p.add_subparsers(dest="command", required=True)
    m = sub.add_parser("materials", help="DOS records replaced by colour-matched CC0 photo textures")
    m.add_argument("--export", required=True, help="the SkyNet.Tables textures export (manifest.json)")
    m.add_argument("--src", required=True, help="CC0 textures, <id>/<id>_diff_*.jpg; several folders separated by ; (: off Windows)")
    m.add_argument("--mods", required=True, help="the mods folder to write the mod into")
    m.add_argument("--name", default="cc0-materials")
    m.add_argument("--size", type=int, default=512, help="largest side of a texture (a whole multiple of the record)")
    m.add_argument("--only", default="", help="records (T186_009) or texture ids, comma separated")
    m.add_argument("--table", default="", help="JSON table of records and treatments (photo/detail/blend) instead of MATERIALS")
    m.add_argument("--fill-bank", type=int, default=0, help="with --table: the bank's other records as DOS nearest (302: the ground)")
    m.set_defaults(run=cmd_materials)
    r = sub.add_parser("props", help="DOS props replaced by generated glTF models")
    r.add_argument("--src", required=True, help="CC0 textures, <id>/<id>_diff_*.jpg")
    r.add_argument("--mods", required=True)
    r.add_argument("--name", default="cc0-props")
    r.add_argument("--no-maps", action="store_true", help="base colours only (no normal or roughness maps)")
    r.set_defaults(run=cmd_props)
    b = sub.add_parser("robots", help="gun tower segments, the jeep cockpit and the deathmatch jeep as generated glTF models")
    b.add_argument("--src", required=True, help="CC0 textures, <id>/<id>_diff_*.jpg")
    b.add_argument("--mods", required=True)
    b.add_argument("--name", default="cc0-robots")
    b.add_argument("--no-maps", action="store_true", help="base colours only (no normal or roughness maps)")
    b.set_defaults(run=cmd_robots)
    a = p.parse_args()
    a.run(a)


if __name__ == "__main__":
    main()
