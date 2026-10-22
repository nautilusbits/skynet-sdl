"""Sprite models: the DOS billboards (pickups and scenery) as glTF models for the enhanced look.

Builds a mod with models/sprites/*.glb and tables/sprite_models.json (docs/modding.md, "Sprite
models"). Each sprite is rebuilt from its own art, one of four ways:

  relief  the silhouette inflated front and back from its distance field: "puff" (round, for
          skulls, bones, rocks, bodies) or "slab" (flat with a round bevel, for weapons, signs);
          front and back take the sprite art, the bevel the edge texels' colours
  lathe   a body of revolution from the row widths (canisters, barrels, bottles, poles, lamps);
          the art unwrapped around it, the top cap from the sprite's visible top ellipse
  box     a box from the sprite's front and top (ammo crates, medkit box, battery)
  cards   crossed alpha-tested cards (plants)

The outline is the upscaled alpha smoothed and cut at half cover (MASK), so it is smooth, not
pixel steps. Model units are DOS sprite texels; the game fits the height to the sprite's.

  python -I spritekit.py --export <work>/export --hires <hires>/textures --mod <mods>/skynet-hd-sprites
  python -I spritekit.py --list
  (--only 214/007,203/000 builds a few; --census <tsv> orders the build by placements)

<export> is SkyNet.Tables' `textures` output (its manifest.json gives each record's frames);
<hires> holds TEXTURE.NNN/RRR.png, the records upscaled 4x (RGBA). Needs numpy, scipy, Pillow.
"""
import argparse
import io
import json
import math
import struct
import sys
from pathlib import Path

import numpy as np
from PIL import Image
from scipy import ndimage

UPSCALE = 4                  # hires texels per DOS texel
EDGE_SIGMA = 2.0             # alpha smoothing (hires texels): rounds the 4x blocks into a contour
MAX_CELLS = 700              # relief grid cells per side, at most
LATHE_SEG = 32
PICKUP = {"spin": 90, "bob": 2, "offsetY": 3}
SCENERY = {"spin": 0, "bob": 0, "offsetY": 0}
ITEM_SCALE, SPRITE_SCALE = 1, 2   # world units per DOS texel: the game's Level.ItemScale, Level.SpriteScale

# ------------------------------------------------------------------ the sprites
# key: (method, options). Options: k (puff thickness vs. inscribed radius), t (slab half
# thickness, DOS texels), glow (emissive texels: "led" saturated bright, "lamp" any bright),
# metal, rough, cap ("auto" cut the top ellipse into a cap, "none"), top (box: top face share),
# n (cards). Pickups (the item table) spin and bob; the rest stands still.
PUFF = ("relief", {"profile": "puff", "k": 1.0})
SKULL = ("relief", {"profile": "puff", "k": 0.95, "rough": 0.7})
BODY = ("relief", {"profile": "puff", "k": 1.6})
ROCK = ("relief", {"profile": "puff", "k": 1.1, "rough": 0.9})
DEBRIS = ("relief", {"profile": "puff", "k": 0.6})
GUN = ("relief", {"profile": "slab", "t": 2.2, "rough": 0.5})
SIGN = ("relief", {"profile": "slab", "t": 1.0})
LATHE = ("lathe", {})
LATHE_CAP = ("lathe", {"cap": "auto"})
CARDS = ("cards", {"n": 3})

SPRITES = {
    # pickups (TEXTURE.200, 201, 214 items)
    "200/000": GUN, "200/001": ("box", {"top": 0.35}), "200/002": ("relief", {"profile": "slab", "t": 2.5}),
    "200/003": LATHE_CAP, "200/004": ("relief", {"profile": "puff", "k": 1.3}),
    "200/005": ("relief", {"profile": "puff", "k": 1.0, "rough": 0.3}), "200/006": GUN,
    "200/007": ("relief", {"profile": "puff", "k": 1.1}), "200/008": ("relief", {"profile": "puff", "k": 1.1}),
    "200/009": GUN, "200/011": GUN, "200/012": GUN, "200/013": GUN,
    "200/014": ("relief", {"profile": "puff", "k": 1.2}), "200/015": ("relief", {"profile": "puff", "k": 1.2}),
    "201/000": LATHE, "201/001": GUN, "201/003": GUN, "201/005": GUN, "201/006": GUN,
    "201/007": ("box", {"top": 0.3}), "201/008": GUN, "201/009": GUN,
    "201/010": ("relief", {"profile": "puff", "k": 1.2}),
    "214/000": ("relief", {"profile": "puff", "k": 0.9}), "214/001": ("relief", {"profile": "puff", "k": 0.9}),
    "214/002": ("relief", {"profile": "puff", "k": 0.8}), "214/003": ("relief", {"profile": "puff", "k": 0.9}),
    "214/007": ("lathe", {"cap": "auto", "glow": "none"}), "214/008": ("relief", {"profile": "puff", "k": 1.3}),
    "214/009": ("relief", {"profile": "puff", "k": 1.3}), "214/010": ("box", {"top": 0.45}),
    "214/013": ("lathe", {"cap": "auto", "glow": "led"}), "214/014": ("box", {"top": 0.3, "glow": "led"}),
    # plants
    "208/000": CARDS, "242/000": CARDS, "242/002": CARDS, "242/003": CARDS,
    # lathe scenery: barrels, bottles, cans, poles, lamps, hydrant, stump
    "204/000": LATHE_CAP, "204/001": LATHE_CAP, "204/002": LATHE_CAP, "206/000": LATHE_CAP,
    "208/004": LATHE_CAP, "205/005": LATHE_CAP, "213/012": LATHE,
    "206/002": LATHE, "206/003": LATHE, "206/004": LATHE, "206/009": LATHE, "207/001": LATHE, "207/002": LATHE,
    "190/009": LATHE, "190/010": LATHE, "190/011": LATHE,
    "209/010": LATHE, "209/011": LATHE, "209/012": LATHE, "215/014": LATHE, "215/019": LATHE,
    "204/005": LATHE, "204/016": LATHE, "204/017": LATHE, "204/018": LATHE,
    "204/007": ("lathe", {"glow": "lamp"}), "204/008": ("lathe", {"glow": "lamp"}),
    "204/009": ("lathe", {"glow": "lamp"}), "204/010": ("lathe", {"glow": "lamp"}),
    "204/011": ("lathe", {"glow": "lamp"}), "204/012": ("lathe", {"glow": "lamp"}),
    "204/013": ("lathe", {"glow": "lamp"}), "204/028": ("lathe", {"glow": "lamp"}),
    "212/006": ("lathe", {"glow": "lamp"}), "212/007": ("lathe", {"glow": "lamp"}),
    "215/002": ("lathe", {"glow": "lamp"}), "215/003": ("lathe", {"glow": "lamp"}),
    # flat designed scenery
    "208/001": SIGN, "208/006": SIGN, "208/007": SIGN, "204/021": SIGN, "204/022": SIGN, "204/030": SIGN,
    "207/013": SIGN, "208/005": SIGN, "212/009": SIGN, "190/007": SIGN, "190/008": SIGN,
    "215/001": ("relief", {"profile": "slab", "t": 2.0, "glow": "led"}),
    "212/004": ("box", {"top": 0.2}), "182/001": ("relief", {"profile": "puff", "k": 0.9}),
    # bodies and skeletons
    **{k: BODY for k in ("202/000", "202/001", "202/002", "202/003", "202/004", "202/006", "202/007", "202/008",
                         "202/009", "202/010", "202/011", "202/012", "202/013", "202/014", "202/015",
                         "190/000", "190/001", "190/002", "190/003", "242/004")},
    # rocks
    **{k: ROCK for k in ("210/000", "210/002", "210/003", "210/004", "210/006", "210/007", "210/008", "210/009",
                         "210/010", "210/011", "210/012", "211/000", "211/001", "211/002", "211/004", "211/005",
                         "215/011", "206/010", "213/009", "209/005", "209/006", "209/009")},
    # flat debris: paper, shards, cloth
    **{k: DEBRIS for k in ("207/011", "207/012", "211/013", "211/014", "211/015",
                           "213/000", "213/001", "213/002", "213/003", "213/004", "213/005")},
}
# animated effects and specks: stay billboards
SKIP = {"218/000", "218/001", "218/002", "218/003", "216/000", "216/001", "216/002", "216/005", "216/006",
        "216/011", "216/012", "216/013", "216/014", "208/002", "208/003", "206/001", "192/003", "215/012",
        "182/000", "182/002", "182/003", "182/004"}
ITEM_BANKS = {200, 201, 214}


def method_of(key: str):
    """The way a sprite is built; skulls, bones, small debris in the junk banks: puff."""
    if key in SKIP:
        return None
    if key in SPRITES:
        return SPRITES[key]
    bank = int(key.split("/")[0])
    if bank == 203:
        return SKULL
    return PUFF


# ------------------------------------------------------------------ glb
def png(im: Image.Image) -> tuple[bytes, str]:
    b = io.BytesIO()
    im.save(b, "PNG", optimize=True)
    return b.getvalue(), "image/png"


def glb(parts: list[dict], images: list[tuple[bytes, str]]) -> bytes:
    """A .glb of one mesh, a primitive per part {positions, normals, uvs, indices, material};
    material {image, emissive_image, colour, metallic, roughness, alpha, cutoff, double_sided}."""
    bin_ = bytearray()
    views, accessors, prims, materials = [], [], [], []

    def view(data: bytes) -> int:
        while len(bin_) % 4:
            bin_.append(0)
        views.append({"buffer": 0, "byteOffset": len(bin_), "byteLength": len(data)})
        bin_.extend(data)
        return len(views) - 1

    def floats(a: np.ndarray, kind: str, bounds: bool = False) -> int:
        a = np.ascontiguousarray(a, dtype="<f4")
        acc = {"bufferView": view(a.tobytes()), "componentType": 5126, "count": len(a), "type": kind}
        if bounds:
            acc["min"], acc["max"] = a.min(0).tolist(), a.max(0).tolist()
        accessors.append(acc)
        return len(accessors) - 1

    for part in parts:
        att = {"POSITION": floats(part["positions"], "VEC3", True), "NORMAL": floats(part["normals"], "VEC3"),
               "TEXCOORD_0": floats(part["uvs"], "VEC2")}
        idx = np.ascontiguousarray(part["indices"], dtype="<u4")
        accessors.append({"bufferView": view(idx.tobytes()), "componentType": 5125, "count": len(idx), "type": "SCALAR"})
        m = part["material"]
        pbr = {"baseColorFactor": list(m.get("colour", (1.0, 1.0, 1.0, 1.0))),
               "metallicFactor": m.get("metallic", 0.0), "roughnessFactor": m.get("roughness", 0.8)}
        mat = {"name": part["name"], "pbrMetallicRoughness": pbr}
        if "image" in m:
            pbr["baseColorTexture"] = {"index": m["image"]}
        if "emissive_image" in m:
            mat["emissiveTexture"] = {"index": m["emissive_image"]}
            mat["emissiveFactor"] = [1.0, 1.0, 1.0]
        if m.get("alpha", "OPAQUE") != "OPAQUE":
            mat["alphaMode"] = m["alpha"]
            mat["alphaCutoff"] = m.get("cutoff", 0.5)
        if m.get("double_sided"):
            mat["doubleSided"] = True
        materials.append(mat)
        prims.append({"attributes": att, "indices": len(accessors) - 1, "material": len(materials) - 1})

    image_json = [{"bufferView": view(d), "mimeType": mime} for d, mime in images]
    while len(bin_) % 4:
        bin_.append(0)
    doc = {"asset": {"version": "2.0", "generator": "skynet-sdl tools/modkit/spritekit"},
           "scene": 0, "scenes": [{"nodes": [0]}], "nodes": [{"mesh": 0}], "meshes": [{"primitives": prims}],
           "materials": materials, "textures": [{"source": i, "sampler": 0} for i in range(len(images))],
           "samplers": [{"magFilter": 9729, "minFilter": 9987, "wrapS": 33071, "wrapT": 33071}],
           "images": image_json, "buffers": [{"byteLength": len(bin_)}], "bufferViews": views, "accessors": accessors}
    js = json.dumps(doc, separators=(",", ":")).encode()
    js += b" " * (-len(js) % 4)
    total = 12 + 8 + len(js) + 8 + len(bin_)
    return (struct.pack("<III", 0x46546C67, 2, total) + struct.pack("<II", len(js), 0x4E4F534A) + js
            + struct.pack("<II", len(bin_), 0x004E4942) + bytes(bin_))


def smooth_normals(pos: np.ndarray, tris: np.ndarray) -> np.ndarray:
    """Area-weighted vertex normals."""
    n = np.zeros_like(pos)
    fn = np.cross(pos[tris[:, 1]] - pos[tris[:, 0]], pos[tris[:, 2]] - pos[tris[:, 0]])
    for c in range(3):
        np.add.at(n, tris[:, c], fn)
    ln = np.linalg.norm(n, axis=1, keepdims=True)
    return np.where(ln > 1e-9, n / np.maximum(ln, 1e-9), np.array([0.0, 0.0, 1.0]))


# ------------------------------------------------------------------ art
class Art:
    """A sprite's first frame, upscaled, cropped to its texels: colour bled under the clear
    texels (no dark fringe when filtered), alpha smoothed into a round contour."""

    def __init__(self, hires: Path, export: Path, key: str, frames: int):
        bank, rec = key.split("/")
        src = hires / f"TEXTURE.{bank}" / f"{rec}.png"
        if not src.exists():
            src = export / f"TEXTURE.{bank}" / f"{rec}.png"
        im = Image.open(src).convert("RGBA")
        if not src.is_relative_to(hires):
            im = im.resize((im.size[0] * UPSCALE, im.size[1] * UPSCALE), Image.NEAREST)
        a = np.asarray(im).astype(np.float32) / 255.0
        fh = a.shape[0] // max(frames, 1)
        a = a[:fh]
        hard = a[..., 3] > 0.5
        ys, xs = np.nonzero(hard)
        pad = 2 * UPSCALE  # whole DOS texels, so the crop keeps the 4 x 4 blocks aligned
        y0, y1 = max(ys.min() // UPSCALE * UPSCALE - pad, 0), min(ys.max() + 1 + pad, a.shape[0])
        x0, x1 = max(xs.min() // UPSCALE * UPSCALE - pad, 0), min(xs.max() + 1 + pad, a.shape[1])
        a = np.pad(a[y0:y1, x0:x1], ((pad, pad), (pad, pad), (0, 0)))
        hard = a[..., 3] > 0.5
        _, (iy, ix) = ndimage.distance_transform_edt(~hard, return_indices=True)
        rgb = a[..., :3][iy, ix]
        # the DOS texels' cover (one value per 4 x 4 block) through a cubic spline: a contour that
        # runs smoothly through the texel centres, not the 4x blocks' steps
        h4, w4 = hard.shape[0] // UPSCALE, hard.shape[1] // UPSCALE
        cover = hard[:h4 * UPSCALE, :w4 * UPSCALE].reshape(h4, UPSCALE, w4, UPSCALE).mean((1, 3))
        alpha = np.zeros(hard.shape, np.float32)
        alpha[:h4 * UPSCALE, :w4 * UPSCALE] = np.clip(ndimage.zoom(cover, UPSCALE, order=3, mode="nearest", grid_mode=True), 0, 1)
        alpha = ndimage.gaussian_filter(alpha, EDGE_SIGMA / 2)
        self.rgb, self.alpha, self.hard = rgb, alpha, alpha > 0.5
        self.h, self.w = alpha.shape
        ys, xs = np.nonzero(self.hard)
        self.top, self.bot, self.left, self.right = ys.min(), ys.max() + 1, xs.min(), xs.max() + 1

    def image(self, alpha: bool = True) -> Image.Image:
        a = np.dstack([self.rgb, self.alpha if alpha else np.ones_like(self.alpha)])
        return Image.fromarray((np.clip(a, 0, 1) * 255 + 0.5).astype(np.uint8), "RGBA" if alpha else "RGBA").convert(
            "RGBA" if alpha else "RGB")

    def glow(self, kind: str) -> Image.Image | None:
        """Emissive texels: "led" bright saturated, "lamp" any bright; None when none."""
        if kind in (None, "none"):
            return None
        mx, mn = self.rgb.max(2), self.rgb.min(2)
        sat = (mx - mn) / np.maximum(mx, 1e-3)
        m = (mx > 0.6) & (sat > 0.45) if kind == "led" else (mx > 0.72)
        m &= self.hard
        if m.sum() < 4:
            return None
        soft = np.clip(ndimage.gaussian_filter(m.astype(np.float32), 1.0) * 1.5, 0, 1)
        e = self.rgb * soft[..., None]
        return Image.fromarray((e * 255).astype(np.uint8), "RGB")


def material(art: Art, opt: dict, images: list, alpha: str, uv_image: Image.Image | None = None,
             glow_image: Image.Image | None = None) -> dict:
    images.append(png(uv_image if uv_image is not None else art.image(alpha != "OPAQUE")))
    m = {"image": len(images) - 1, "metallic": opt.get("metal", 0.0), "roughness": opt.get("rough", 0.8), "alpha": alpha}
    if glow_image is None and uv_image is None:
        glow_image = art.glow(opt.get("glow"))
    if glow_image is not None:
        images.append(png(glow_image))
        m["emissive_image"] = len(images) - 1
    return m


# ------------------------------------------------------------------ relief: puff and slab
def relief(art: Art, opt: dict) -> tuple[list, list]:
    """Front and back sheets lifted off the sprite plane by a profile of the distance to the
    outline; they meet at the outline, where the alpha cut closes them."""
    inside = ndimage.distance_transform_edt(art.hard)
    outside = ndimage.distance_transform_edt(~art.hard)
    sd = ndimage.gaussian_filter(inside - outside, 1.0)
    R = max(float(sd.max()), 1.0)
    step = UPSCALE / 2
    while True:  # coarsen until the cells near the silhouette fit the budget
        gx = np.arange(art.left - step, art.right + 2 * step, step)
        gy = np.arange(art.top - step, art.bot + 2 * step, step)
        YY, XX = np.meshgrid(gy, gx, indexing="ij")
        d = ndimage.map_coordinates(sd, [YY.ravel(), XX.ravel()], order=1, mode="nearest").reshape(YY.shape)
        live = (d[:-1, :-1] > -step) | (d[1:, :-1] > -step) | (d[:-1, 1:] > -step) | (d[1:, 1:] > -step)
        if live.sum() <= MAX_CELLS:
            break
        step *= 1.15

    # snap the nodes next to the outline onto it (along the distance gradient) so that the
    # sheets meet exactly where the alpha cuts: no gap between front and back at the rim. A
    # node whose move would fold one of its cells stays where it was.
    gry, grx = np.gradient(sd)
    gy_n = ndimage.map_coordinates(gry, [YY.ravel(), XX.ravel()], order=1, mode="nearest").reshape(YY.shape)
    gx_n = ndimage.map_coordinates(grx, [YY.ravel(), XX.ravel()], order=1, mode="nearest").reshape(YY.shape)
    g2 = gx_n ** 2 + gy_n ** 2
    near = (np.abs(d) < 0.6 * step) & (g2 > 1e-4)
    move = np.where(near, d / np.maximum(g2, 1e-4), 0.0)
    ny, nx = d.shape
    vid = np.arange(ny * nx).reshape(ny, nx)
    cy, cxs = np.nonzero(live)
    q00, q01, q10, q11 = vid[cy, cxs], vid[cy, cxs + 1], vid[cy + 1, cxs], vid[cy + 1, cxs + 1]
    cells = np.concatenate([np.stack([q00, q10, q01], 1), np.stack([q01, q10, q11], 1)])
    for _ in range(6):
        sx, sy = (XX - gx_n * move).ravel(), (YY - gy_n * move).ravel()
        e1x, e1y = sx[cells[:, 1]] - sx[cells[:, 0]], sy[cells[:, 1]] - sy[cells[:, 0]]
        e2x, e2y = sx[cells[:, 2]] - sx[cells[:, 0]], sy[cells[:, 2]] - sy[cells[:, 0]]
        folded = (e1x * e2y - e1y * e2x) >= -1e-3 * step * step   # cells are (+) one way round
        if not folded.any():
            break
        bad = np.unique(cells[folded])
        bad = bad[near.ravel()[bad]]
        if len(bad) == 0:
            break
        near.ravel()[bad] = False
        move.ravel()[bad] = 0.0
    XX = XX - gx_n * move
    YY = YY - gy_n * move
    d = np.where(near, 0.0, d)

    if opt.get("profile") == "slab":
        T = opt.get("t", 2.0) * UPSCALE
        b = min(T, R)
        t = np.clip(d / b, 0, 1)
        hgt = T * np.sqrt(1 - (1 - t) ** 2)
    else:
        t = np.clip(d / R, 0, 1)
        hgt = opt.get("k", 1.0) * R * np.sqrt(1 - (1 - t) ** 2)

    cx = (art.left + art.right) / 2
    X = (XX - cx) / UPSCALE
    Y = (art.bot - YY) / UPSCALE
    U = XX / art.w
    V = YY / art.h
    Z = hgt / UPSCALE

    vid = np.arange(ny * nx).reshape(ny, nx)
    cy, cxs = np.nonzero(live)
    a, b_, c, e = vid[cy, cxs], vid[cy, cxs + 1], vid[cy + 1, cxs], vid[cy + 1, cxs + 1]
    # image rows go down, model Y up: (a, c, b) is counter-clockwise seen from +Z
    front_t = np.concatenate([np.stack([a, c, b_], 1), np.stack([b_, c, e], 1)])
    used = np.unique(front_t)
    remap = -np.ones(ny * nx, int)
    remap[used] = np.arange(len(used))
    front_t = remap[front_t]
    P = np.stack([X.ravel(), Y.ravel(), Z.ravel()], 1)[used]
    UV = np.stack([U.ravel(), V.ravel()], 1)[used]
    n = len(used)
    Pb = P * np.array([1, 1, -1])
    pos = np.concatenate([P, Pb])
    uv = np.concatenate([UV, UV])
    tris = np.concatenate([front_t, front_t[:, [0, 2, 1]] + n])
    nrm = smooth_normals(pos, tris)
    images = []
    mat = material(art, opt, images, "MASK")
    return [{"name": "relief", "positions": pos, "normals": nrm, "uvs": uv, "indices": tris.ravel(), "material": mat}], images


# ------------------------------------------------------------------ lathe
def lathe(art: Art, opt: dict) -> tuple[list, list]:
    """A body of revolution from the half widths per row; the art unwrapped around it (front and
    back windows of ±90°, the sprite's own shading kept); the top ellipse, when the sprite shows
    one, cut off and laid on the top cap."""
    hard = art.hard
    rows = np.arange(art.top, art.bot)
    ws, cs = [], []
    for r in rows:
        xs = np.nonzero(hard[r])[0]
        ws.append((xs[-1] + 1 - xs[0]) / 2 if len(xs) else 0.0)
        cs.append((xs[0] + xs[-1] + 1) / 2 if len(xs) else np.nan)
    ws = np.array(ws, float)
    c = float(np.nanmedian(cs))
    ws = ndimage.uniform_filter1d(ws, 5, mode="nearest")
    n = len(ws)
    t0 = 0
    if opt.get("cap") == "auto":
        wmax = ws[: max(n // 3, 1)].max()
        t0 = int(next(i for i in range(n) if ws[i] >= 0.95 * wmax))
        if t0 > n // 4:
            t0 = 0
    ring_rows = sorted(set(np.linspace(t0, n - 1, min(28, n - t0)).astype(int)))
    H = art.bot

    # unwrapped texture: x = c + 0.92 w sin(theta), both halves the front's
    TW, TH = 256, max(64, min(512, n * 2))
    vs = (np.arange(TH) + 0.5) / TH
    th = (np.arange(TW) + 0.5) / TW * 2 * math.pi
    loc = np.sin(((th + math.pi / 2) % math.pi) - math.pi / 2) * 0.92
    rr = np.clip(art.top + t0 + vs * (n - 1 - t0), 0, art.h - 1)
    wi = np.interp(rr - art.top, np.arange(n), ws)
    SX = c + wi[:, None] * loc[None, :] - 0.5
    SY = np.repeat(rr[:, None], TW, 1)
    tex = np.dstack([ndimage.map_coordinates(art.rgb[..., k], [SY, SX], order=1, mode="nearest") for k in range(3)])
    body_img = Image.fromarray((np.clip(tex, 0, 1) * 255).astype(np.uint8), "RGB")

    pos, uv, tris = [], [], []
    for i, r in enumerate(ring_rows):
        w = ws[r] / UPSCALE
        y = (H - (art.top + r)) / UPSCALE
        v = (r - t0) / max(n - 1 - t0, 1)
        for s in range(LATHE_SEG + 1):
            a = s / LATHE_SEG * 2 * math.pi
            pos.append((w * math.sin(a), y, w * math.cos(a)))
            uv.append((s / LATHE_SEG, v))
    S = LATHE_SEG + 1
    for i in range(len(ring_rows) - 1):
        for s in range(LATHE_SEG):
            a0, a1, b0, b1 = i * S + s, i * S + s + 1, (i + 1) * S + s, (i + 1) * S + s + 1
            tris += [(a0, b0, a1), (a1, b0, b1)]
    pos, uv, tris = np.array(pos), np.array(uv), np.array(tris)
    images = []
    body_mat = material(art, opt, images, "OPAQUE", uv_image=body_img)
    glow = art.glow(opt.get("glow"))
    if glow is not None:
        g = np.asarray(glow).astype(np.float32) / 255
        gt = np.dstack([ndimage.map_coordinates(g[..., k], [SY, SX], order=1, mode="nearest") for k in range(3)])
        images.append(png(Image.fromarray((np.clip(gt, 0, 1) * 255).astype(np.uint8), "RGB")))
        body_mat["emissive_image"] = len(images) - 1
    parts = [{"name": "body", "positions": pos, "normals": smooth_normals(pos, tris), "uvs": uv,
              "indices": tris.ravel(), "material": body_mat}]

    # caps: the top from the sprite's far half of the top ellipse (or its top rows), the bottom flat
    CR = 64
    rho = (np.arange(CR) + 0.5) / CR * 2 - 1
    PY, PX = np.meshgrid(rho, rho, indexing="ij")
    w0 = ws[t0]
    eh = max(t0, 1.5)
    sy = art.top + t0 - np.abs(PY) * eh
    sx = c + PX * w0 * 0.95
    cap = np.dstack([ndimage.map_coordinates(art.rgb[..., k], [sy, sx], order=1, mode="nearest") for k in range(3)])
    images.append(png(Image.fromarray((np.clip(cap, 0, 1) * 255).astype(np.uint8), "RGB")))
    cap_mat = {"image": len(images) - 1, "metallic": opt.get("metal", 0.0), "roughness": opt.get("rough", 0.8)}
    for top in (True, False):
        r = ring_rows[0] if top else ring_rows[-1]
        w = ws[r] / UPSCALE
        y = (H - (art.top + r)) / UPSCALE
        cp = [(0.0, y, 0.0)] + [(w * math.sin(s / LATHE_SEG * 2 * math.pi), y, w * math.cos(s / LATHE_SEG * 2 * math.pi))
                                for s in range(LATHE_SEG)]
        cu = [(0.5, 0.5)] + [(0.5 + 0.5 * math.sin(s / LATHE_SEG * 2 * math.pi), 0.5 + 0.5 * math.cos(s / LATHE_SEG * 2 * math.pi))
                             for s in range(LATHE_SEG)]
        ct = [(0, 1 + s, 1 + (s + 1) % LATHE_SEG) if top else (0, 1 + (s + 1) % LATHE_SEG, 1 + s) for s in range(LATHE_SEG)]
        cp = np.array(cp)
        mat = cap_mat if top else {"colour": tuple(float(v) for v in np.r_[art.rgb[art.bot - 2, int(c)] ** 2.2, 1.0]), "roughness": 0.9}
        parts.append({"name": "cap" if top else "base", "positions": cp,
                      "normals": np.tile([0.0, 1.0 if top else -1.0, 0.0], (len(cp), 1)), "uvs": np.array(cu),
                      "indices": np.array(ct).ravel(), "material": mat})
    return parts, images


# ------------------------------------------------------------------ box
def box(art: Art, opt: dict) -> tuple[list, list]:
    """A box: the sprite's lower part is the front (and the mirrored back), its top rows the lid,
    the sides the edge columns' colours; depth from how much lid the sprite shows."""
    l, r, t, b = art.left, art.right, art.top, art.bot
    tf = t + opt.get("top", 0.3) * (b - t)
    W = (r - l) / UPSCALE
    Hf = (b - tf) / UPSCALE
    D = float(np.clip(2.0 * (tf - t) / UPSCALE, 0.35 * W, 0.9 * W))
    x0, x1, z0, z1 = -W / 2, W / 2, -D / 2, D / 2
    u = lambda x: x / art.w
    v = lambda y: y / art.h
    eu0, eu1 = u(l + 3), u(r - 3)
    faces = [  # corners counter-clockwise from outside, uv per corner
        ([(x0, 0, z1), (x1, 0, z1), (x1, Hf, z1), (x0, Hf, z1)], [(u(l), v(b)), (u(r), v(b)), (u(r), v(tf)), (u(l), v(tf))], (0, 0, 1)),
        ([(x1, 0, z0), (x0, 0, z0), (x0, Hf, z0), (x1, Hf, z0)], [(u(l), v(b)), (u(r), v(b)), (u(r), v(tf)), (u(l), v(tf))], (0, 0, -1)),
        ([(x0, Hf, z1), (x1, Hf, z1), (x1, Hf, z0), (x0, Hf, z0)], [(u(l), v(tf)), (u(r), v(tf)), (u(r), v(t)), (u(l), v(t))], (0, 1, 0)),
        ([(x0, 0, z0), (x1, 0, z0), (x1, 0, z1), (x0, 0, z1)], [(u(l), v(b - 2))] * 2 + [(u(r), v(b - 2))] * 2, (0, -1, 0)),
        ([(x1, 0, z1), (x1, 0, z0), (x1, Hf, z0), (x1, Hf, z1)], [(eu1, v(b)), (eu1, v(b)), (eu1, v(tf)), (eu1, v(tf))], (1, 0, 0)),
        ([(x0, 0, z0), (x0, 0, z1), (x0, Hf, z1), (x0, Hf, z0)], [(eu0, v(b)), (eu0, v(b)), (eu0, v(tf)), (eu0, v(tf))], (-1, 0, 0)),
    ]
    pos, uv, nrm, tris = [], [], [], []
    for pts, uvs, nn in faces:
        k = len(pos)
        pos += pts
        uv += uvs
        nrm += [nn] * 4
        tris += [(k, k + 1, k + 2), (k, k + 2, k + 3)]
    images = []
    mat = material(art, opt, images, "OPAQUE")
    return [{"name": "box", "positions": np.array(pos, float), "normals": np.array(nrm, float), "uvs": np.array(uv),
             "indices": np.array(tris).ravel(), "material": mat}], images


# ------------------------------------------------------------------ cards
def cards(art: Art, opt: dict) -> tuple[list, list]:
    """n vertical cards crossed at the centre, alpha-tested, two-sided."""
    l, r, t, b = art.left, art.right, art.top, art.bot
    W, H = (r - l) / UPSCALE, (b - t) / UPSCALE
    n = opt.get("n", 3)
    pos, uv, nrm, tris = [], [], [], []
    for i in range(n):
        a = i * math.pi / n
        dx, dz = math.cos(a) * W / 2, -math.sin(a) * W / 2
        k = len(pos)
        pos += [(-dx, 0, -dz), (dx, 0, dz), (dx, H, dz), (-dx, H, -dz)]
        uv += [(l / art.w, b / art.h), (r / art.w, b / art.h), (r / art.w, t / art.h), (l / art.w, t / art.h)]
        nrm += [(math.sin(a), 0, math.cos(a))] * 4
        tris += [(k, k + 1, k + 2), (k, k + 2, k + 3)]
    images = []
    mat = material(art, opt, images, "MASK")
    mat["double_sided"] = True
    mat["roughness"] = opt.get("rough", 0.9)
    return [{"name": "cards", "positions": np.array(pos, float), "normals": np.array(nrm, float), "uvs": np.array(uv),
             "indices": np.array(tris).ravel(), "material": mat}], images


BUILDERS = {"relief": relief, "lathe": lathe, "box": box, "cards": cards}


# ------------------------------------------------------------------ main
def build(key: str, args, frames: dict, items: set) -> dict | None:
    how = method_of(key)
    if how is None:
        return None
    method, opt = how
    art = Art(args.hires, args.export, key, frames.get(key, 1))
    parts, images = BUILDERS[method](art, opt)
    name = f"s{key.replace('/', '_')}.glb"
    out = args.mod / "models" / "sprites" / name
    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_bytes(glb(parts, images))
    pick = key in items
    anim = PICKUP if pick else SCENERY
    tris = sum(len(p["indices"]) // 3 for p in parts)
    return {"row": {"sprite": key, "model": name, "scale": ITEM_SCALE if pick else SPRITE_SCALE, "yaw": 0, **anim}, "method": method,
            "profile": opt.get("profile", ""), "tris": tris, "bytes": out.stat().st_size}


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--export", type=Path)
    ap.add_argument("--hires", type=Path)
    ap.add_argument("--mod", type=Path)
    ap.add_argument("--census", type=Path, help="census.tsv (sprite, placed, ...): build order and coverage")
    ap.add_argument("--only", help="comma-separated keys")
    ap.add_argument("--items", help="comma-separated item keys (spin and bob); default: TEXTURE.200/201/214 rows")
    ap.add_argument("--list", action="store_true")
    args = ap.parse_args()
    if args.list:
        for k, (m, o) in sorted(SPRITES.items()):
            print(k, m, o.get("profile", ""))
        return 0
    manifest = json.loads((args.export / "manifest.json").read_text())
    frames = {f"{r['bank']:03d}/{r['record']:03d}": r["frames"] for r in manifest["records"]}
    keys, census_items = [], set()
    if args.census:  # sprite, placed, maps, drop, item (the item table's message key), ...
        for line in args.census.read_text().splitlines()[1:]:
            cols = line.split("\t")
            keys.append(cols[0])
            if len(cols) > 4 and cols[4]:
                census_items.add(cols[0])
    keys += [k for k in SPRITES if k not in keys]
    if args.only:
        keys = args.only.split(",")
    items = set(args.items.split(",")) if args.items else census_items or {k for k in SPRITES if int(k[:3]) in ITEM_BANKS}
    rows, report = [], []
    old = {}
    tpath = args.mod / "tables" / "sprite_models.json"
    if args.only and tpath.exists():
        old = {r["sprite"]: r for r in json.loads(tpath.read_text())["rows"]}
    for k in keys:
        try:
            res = build(k, args, frames, items)
        except Exception as e:  # one bad record does not stop the rest
            print(f"{k}\tFAILED\t{e}", file=sys.stderr)
            continue
        if res is None:
            continue
        old[k] = res["row"]
        report.append(f"{k}\t{res['method']}\t{res['profile']}\t{res['tris']}\t{res['bytes']}")
        print(report[-1], flush=True)
    tpath.parent.mkdir(parents=True, exist_ok=True)
    tpath.write_text(json.dumps({"table": "sprite_models", "rows": sorted(old.values(), key=lambda r: r["sprite"])}, indent=1))
    mj = args.mod / "mod.json"
    if not mj.exists():
        mj.write_text(json.dumps({"name": args.mod.name, "version": "0.1", "author": "spritekit",
                                  "description": "The DOS sprites (pickups and scenery) as 3D models from their own art (enhanced look)"},
                                 indent=2))
    return 0


if __name__ == "__main__":
    sys.exit(main())
