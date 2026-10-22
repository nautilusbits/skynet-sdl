"""Hi-res texture mods made from the DOS art, by several upscaling methods.

Run with an isolated interpreter (python -I) from any folder; nothing is imported from the
working folder. See docs/hires-textures.md for the comparison and the recommendation.

  1. export   the TEXTURE.NNN records as palette PNGs (through tools/SkyNet.Tables):
       python -I tools/upscale/upscale.py export --data <game-data-dir> --work <work-dir>
  2. fetch    a method's binary or weights into <cache>/<method>/ (SHA-256 checked):
       python -I tools/upscale/upscale.py fetch --method esrgan-x4plus --cache <cache-dir>
  3. run      upscale every exported record 4x into a mod folder the game reads
     (<mods>/<name>/textures/TEXTURE.NNN/RRR.png, straight RGBA, index 0 = alpha 0):
       python -I tools/upscale/upscale.py run --work <work-dir> --method lanczos --mods <mods-dir>
         [--name <mod-name>] [--cache <cache-dir>] [--banks 210,302]
     Walls also get RRR_n.png (normal) and RRR_r.png (roughness), sprites none.
  3b. maps    only the relief maps, for an existing mod's walls from its own PNGs:
       python -I tools/upscale/upscale.py maps --work <work-dir> --mods <mods-dir> --name <mod-name>
  4. sheet    a contact sheet of the same records for each method (run first):
       python -I tools/upscale/upscale.py sheet --mods <mods-dir> --methods nearest,lanczos --out <dir>

Methods: nearest, lanczos, scale4x, esrgan-x4plus, esrgan-anime, animevideov3,
pixelperfect, fatalpixels (`list` prints them with their licences).

Pipeline per record: palette PNG -> holes (index 0) filled from their opaque neighbours ->
each frame padded (wall textures wrap, as they repeat across a face; sprites copy their edge)
-> upscaled 4x -> the padding cropped -> alpha = the index-0 mask scaled by nearest (the same
cut-out as the game's index texture), hole colour = palette index 31 (DOS's haze colour, what an
opaque surface shows for index 0).
"""

from __future__ import annotations

import argparse
import hashlib
import json
import shutil
import subprocess
import sys
import tempfile
import time
import urllib.request
import zipfile
from pathlib import Path

import numpy as np
from PIL import Image

SCALE = 4
PAD = 8               # source texels of padding around each frame
HAZE_INDEX = 31       # HAZE.000 row 0: what index 0 becomes on an opaque surface
REPO = Path(__file__).resolve().parents[2]

NCNN_URL = ("https://github.com/xinntao/Real-ESRGAN/releases/download/v0.2.5.0/"
            "realesrgan-ncnn-vulkan-20220424-windows.zip")
NCNN_SHA = "abc02804e17982a3be33675e4d471e91ea374e65b70167abc09e31acb412802d"

# method -> (kind, model, download, sha256, licence)
METHODS = {
    "nearest": ("pil", Image.Resampling.NEAREST, None, None, "n/a (Pillow, MIT-CMU)"),
    "lanczos": ("pil", Image.Resampling.LANCZOS, None, None, "n/a (Pillow, MIT-CMU)"),
    "scale4x": ("scale4x", None, None, None, "own numpy code (Scale2x/EPX algorithm)"),
    "esrgan-x4plus": ("ncnn", "realesrgan-x4plus", NCNN_URL, NCNN_SHA, "BSD-3-Clause (Real-ESRGAN)"),
    "esrgan-anime": ("ncnn", "realesrgan-x4plus-anime", NCNN_URL, NCNN_SHA, "BSD-3-Clause (Real-ESRGAN)"),
    "animevideov3": ("ncnn", "realesr-animevideov3", NCNN_URL, NCNN_SHA, "BSD-3-Clause (Real-ESRGAN)"),
    "pixelperfect": ("torch", "model.pth",
                     "https://drive.usercontent.google.com/download?id=1x583PgpY5O4T2603gJK8kwrGiIc4aFne"
                     "&export=download&confirm=t",
                     "52bcda86effc93e07d868036f53dd2f4a38016cdd636b66ebbf6c4525223eb5f",
                     "WTFPL (4x-PixelPerfectV4, OpenModelDB)"),
    "fatalpixels": ("torch", "model.pth",
                    "https://objectstorage.us-phoenix-1.oraclecloud.com/n/ax6ygfvpvzka/b/"
                    "open-modeldb-files/o/4x-Fatal-Pixels.pth",
                    "d30ac78343e0a7ffaa805b8ab7840fb1d6b9e6083fe1728c651e2064210e5994",
                    "CC-BY-NC-SA-4.0 (4x-Fatal-Pixels, OpenModelDB)"),
}

# The contact sheet's records: (bank, record, what it shows).
SHEET = [
    (302, 0, "terrain"), (302, 29, "terrain"), (302, 57, "road edge"),
    (252, 1, "wall"), (252, 3, "wood wall"), (401, 7, "wall"),
    (288, 55, "panel + text"), (404, 12, "panel"), (288, 12, "machine"),
    (214, 7, "sprite"), (200, 6, "sprite"), (359, 2, "sprite"),
]


# ---------------------------------------------------------------- export

def cmd_export(a) -> None:
    work = Path(a.work).resolve()
    cmd = ["dotnet", "run", "--project", str(REPO / "tools" / "SkyNet.Tables"), "-c", "Release", "--",
           "textures", str(Path(a.data).resolve()), "--out", str(work / "export")]
    subprocess.run(cmd, check=True)


def load_manifest(work: Path) -> list[dict]:
    return json.loads((work / "export" / "manifest.json").read_text())["records"]


# ---------------------------------------------------------------- fetch

def sha256(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as f:
        for block in iter(lambda: f.read(1 << 20), b""):
            h.update(block)
    return h.hexdigest()


def fetch_dir(method: str, cache: Path) -> Path:
    """The method's own folder in the cache: the ncnn release is shared by its three models."""
    kind = METHODS[method][0]
    return cache / ("realesrgan-ncnn" if kind == "ncnn" else method)


def cmd_fetch(a) -> None:
    fetch(a.method, Path(a.cache).resolve())


def fetch(method: str, cache: Path) -> Path:
    kind, model, url, sha, _ = METHODS[method]
    folder = fetch_dir(method, cache)
    if url is None:
        return folder
    marker = folder / "installed.sha256"
    if marker.exists() and marker.read_text().strip() == sha:
        return folder
    folder.mkdir(parents=True, exist_ok=True)
    target = folder / ("download.zip" if kind == "ncnn" else model)
    print(f"downloading {url}")
    with urllib.request.urlopen(url) as r, target.open("wb") as f:
        shutil.copyfileobj(r, f)
    got = sha256(target)
    if got != sha:
        target.unlink()
        sys.exit(f"{url}: SHA-256 {got}, expected {sha}")
    if kind == "ncnn":
        with zipfile.ZipFile(target) as z:
            z.extractall(folder)
        target.unlink()
    marker.write_text(sha)
    return folder


# ---------------------------------------------------------------- per-record preparation

def read_indexed(path: Path) -> tuple[np.ndarray, np.ndarray]:
    im = Image.open(path)
    if im.mode != "P":
        sys.exit(f"{path}: not a palette PNG")
    pal = np.array(im.getpalette()[:768], dtype=np.uint8).reshape(256, 3)
    return np.array(im, dtype=np.uint8), pal


def fill_holes(rgb: np.ndarray, mask: np.ndarray, wrap: bool) -> np.ndarray:
    """Hole texels take the mean of their opaque neighbours, growing inwards, so an upscaler
    sees no black around a cut-out's edge (the colour is thrown away again afterwards)."""
    rgb = rgb.astype(np.float32).copy()
    known = mask.copy()
    if known.all() or not known.any():
        return rgb
    for _ in range(max(rgb.shape[:2])):
        if known.all():
            break
        total = np.zeros_like(rgb)
        count = np.zeros(known.shape, np.float32)
        for dy, dx in ((-1, 0), (1, 0), (0, -1), (0, 1), (-1, -1), (-1, 1), (1, -1), (1, 1)):
            k = shift(known, dy, dx, wrap)
            total += shift(rgb * known[..., None], dy, dx, wrap)
            count += k
        grow = (~known) & (count > 0)
        rgb[grow] = total[grow] / count[grow][:, None]
        known = known | grow
    return rgb


def shift(arr: np.ndarray, dy: int, dx: int, wrap: bool) -> np.ndarray:
    if wrap:
        return np.roll(arr, (dy, dx), axis=(0, 1))
    pad = [(1, 1), (1, 1)] + [(0, 0)] * (arr.ndim - 2)
    p = np.pad(arr, pad, mode="constant")
    h, w = arr.shape[:2]
    return p[1 - dy:1 - dy + h, 1 - dx:1 - dx + w]


def padded(arr: np.ndarray, wrap: bool) -> np.ndarray:
    pad = [(PAD, PAD), (PAD, PAD)] + [(0, 0)] * (arr.ndim - 2)
    return np.pad(arr, pad, mode="wrap" if wrap else "edge")


def frames_of(rec: dict, arr: np.ndarray) -> list[np.ndarray]:
    h = rec["height"]
    return [arr[k * h:(k + 1) * h] for k in range(rec["frames"])]


# ---------------------------------------------------------------- scalers

def scale2x(idx: np.ndarray) -> np.ndarray:
    """Scale2x (EPX) on palette indices: exact colours, edges follow diagonals."""
    p = np.pad(idx, 1, mode="edge")
    e = p[1:-1, 1:-1]
    b, h = p[:-2, 1:-1], p[2:, 1:-1]
    d, f = p[1:-1, :-2], p[1:-1, 2:]
    e0 = np.where((d == b) & (b != f) & (d != h), d, e)
    e1 = np.where((b == f) & (b != d) & (f != h), f, e)
    e2 = np.where((d == h) & (d != b) & (h != f), d, e)
    e3 = np.where((h == f) & (d != h) & (b != f), f, e)
    out = np.empty((idx.shape[0] * 2, idx.shape[1] * 2), idx.dtype)
    out[0::2, 0::2], out[0::2, 1::2], out[1::2, 0::2], out[1::2, 1::2] = e0, e1, e2, e3
    return out


class Scaler:
    """Upscales a batch of padded RGB frames (float32 0..255) 4x."""

    def __init__(self, method: str, cache: Path):
        self.method = method
        self.kind, self.model, *_ = METHODS[method]
        self.cache = cache
        if self.kind in ("ncnn", "torch"):
            self.folder = fetch(method, cache)
        if self.kind == "torch":
            import torch
            import spandrel
            self.torch = torch
            self.dev = "cuda" if torch.cuda.is_available() else "cpu"
            desc = spandrel.ModelLoader().load_from_file(str(self.folder / self.model))
            if desc.scale != SCALE:
                sys.exit(f"{method}: scale {desc.scale}, expected {SCALE}")
            self.net = desc.model.eval().to(self.dev)
            self.half = self.dev == "cuda"
            if self.half:
                self.net = self.net.half()

    def run(self, frames: list[np.ndarray], indices: list[np.ndarray]) -> list[np.ndarray]:
        if self.kind == "pil":
            return [np.asarray(Image.fromarray(np.clip(f + 0.5, 0, 255).astype(np.uint8)).resize(
                (f.shape[1] * SCALE, f.shape[0] * SCALE), self.model), np.float32) for f in frames]
        if self.kind == "scale4x":
            return [scale2x(scale2x(i)) for i in indices]   # indices out: the caller maps them
        if self.kind == "ncnn":
            return self.run_ncnn(frames)
        return self.run_torch(frames)

    def run_ncnn(self, frames: list[np.ndarray]) -> list[np.ndarray]:
        exe = self.folder / "realesrgan-ncnn-vulkan.exe"
        with tempfile.TemporaryDirectory() as tmp:
            src, dst = Path(tmp) / "in", Path(tmp) / "out"
            src.mkdir()
            dst.mkdir()
            for k, f in enumerate(frames):
                Image.fromarray(np.clip(f + 0.5, 0, 255).astype(np.uint8)).save(src / f"{k:06d}.png")
            subprocess.run([str(exe), "-i", str(src), "-o", str(dst), "-n", self.model, "-s", str(SCALE),
                            "-m", str(self.folder / "models"), "-f", "png"],
                           check=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
            return [np.asarray(Image.open(dst / f"{k:06d}.png").convert("RGB"), np.float32)
                    for k in range(len(frames))]

    def run_torch(self, frames: list[np.ndarray]) -> list[np.ndarray]:
        torch = self.torch
        out: list[np.ndarray | None] = [None] * len(frames)
        by_size: dict[tuple, list[int]] = {}
        for k, f in enumerate(frames):
            by_size.setdefault(f.shape, []).append(k)
        with torch.inference_mode():
            for shape, ks in by_size.items():
                step = max(1, (1 << 18) // (shape[0] * shape[1]))   # ~256 K input texels a batch (RRDB on 12 GB)
                for s in range(0, len(ks), step):
                    part = ks[s:s + step]
                    x = torch.from_numpy(np.stack([frames[k] for k in part]) / 255.0).permute(0, 3, 1, 2)
                    x = x.to(self.dev, torch.float16 if self.half else torch.float32)
                    y = self.net(x).float().clamp(0, 1).permute(0, 2, 3, 1).cpu().numpy() * 255.0
                    for k, img in zip(part, y):
                        out[k] = img
        return out  # type: ignore[return-value]


# ---------------------------------------------------------------- run

def cmd_run(a) -> None:
    work, mods = Path(a.work).resolve(), Path(a.mods).resolve()
    cache = Path(a.cache).resolve() if a.cache else Path(__file__).resolve().parent / "cache"
    name = a.name or f"hires-{a.method}"
    banks = {int(b) for b in a.banks.split(",")} if a.banks else None
    recs = [r for r in load_manifest(work) if banks is None or r["bank"] in banks]
    out_root = mods / name / "textures"
    t0 = time.perf_counter()
    scaler = Scaler(a.method, cache)
    t_load = time.perf_counter() - t0

    # Prepared inputs, all frames of all records, for one batch through the scaler.
    jobs, frames, indices = [], [], []
    for rec in recs:
        idx, pal = read_indexed(work / "export" / f"TEXTURE.{rec['bank']:03d}" / f"{rec['record']:03d}.png")
        wrap = rec["layout"] == "wall"
        for fi in frames_of(rec, idx):
            mask = fi != 0
            rgb = fill_holes(pal[fi].astype(np.float32), mask, wrap) if rec["holes"] else pal[fi].astype(np.float32)
            frames.append(padded(rgb, wrap))
            indices.append(padded(fi, wrap))
        jobs.append((rec, idx, pal))

    t1 = time.perf_counter()
    scaled = scaler.run(frames, indices)
    t_scale = time.perf_counter() - t1

    k, total = 0, 0
    for rec, idx, pal in jobs:
        parts = []
        for fi in frames_of(rec, idx):
            up = scaled[k]
            k += 1
            c = PAD * SCALE
            up = up[c:up.shape[0] - c, c:up.shape[1] - c]
            if scaler.kind == "scale4x":
                up = pal[up].astype(np.float32)
            mask = np.repeat(np.repeat(fi != 0, SCALE, 0), SCALE, 1)
            rgb = np.clip(up + 0.5, 0, 255).astype(np.uint8)
            rgb[~mask] = pal[HAZE_INDEX]
            parts.append(np.dstack([rgb, np.where(mask, 255, 0).astype(np.uint8)]))
        path = out_root / f"TEXTURE.{rec['bank']:03d}" / f"{rec['record']:03d}.png"
        path.parent.mkdir(parents=True, exist_ok=True)
        image = np.vstack(parts)
        Image.fromarray(image, "RGBA").save(path, optimize=False, compress_level=6)
        total += path.stat().st_size
        if rec["layout"] == "wall":
            total += write_relief(path, image, rec["frames"], SCALE)

    elapsed = time.perf_counter() - t0
    stats = {"method": a.method, "licence": METHODS[a.method][4], "records": len(jobs), "frames": len(frames),
             "model_load_s": round(t_load, 2), "upscale_s": round(t_scale, 2), "total_s": round(elapsed, 2),
             "bytes": total}
    (mods / name / "upscale-stats.json").write_text(json.dumps(stats, indent=1))
    print(json.dumps(stats))


# ---------------------------------------------------------------- relief maps
# As SkyNet.Upscale/ReliefMaps.cs (same constants): per frame, luminance blurred (sigma 0.6
# source texel) -> Sobel -> tangent-space normal (x = +u columns, y = +v rows down, z out) as
# RRR_n.png; local mean / contrast / saturation (sigma 1.5 texels) -> roughness as RRR_r.png.

HEIGHT_BLUR, DEPTH, CONTRAST_BLUR = 0.6, 6.0, 1.5
BASE_ROUGH, CONTRAST_ROUGH, METAL_SMOOTH, MIN_ROUGH = 0.85, 1.5, 0.45, 0.3


def blur(a: np.ndarray, sigma: float, wrap: bool) -> np.ndarray:
    r = int(np.ceil(3 * sigma))
    k = np.exp(-np.arange(-r, r + 1) ** 2 / (2 * sigma * sigma))
    k /= k.sum()
    mode = "wrap" if wrap else "edge"
    for axis in (1, 0):
        p = np.pad(a, [(r, r) if ax == axis else (0, 0) for ax in range(2)], mode=mode)
        n = a.shape[axis]
        a = sum(w * np.take(p, np.arange(i, i + n), axis=axis) for i, w in enumerate(k))
    return a


def smooth(e0: float, e1: float, x: np.ndarray) -> np.ndarray:
    t = np.clip((x - e0) / (e1 - e0), 0, 1)
    return t * t * (3 - 2 * t)


def relief_frame(rgba: np.ndarray, scale: int, wrap: bool) -> tuple[np.ndarray, np.ndarray]:
    rgb = rgba[..., :3].astype(np.float32) / 255
    lum = rgb @ np.array([0.2126, 0.7152, 0.0722], np.float32)
    mx, mn = rgb.max(-1), rgb.min(-1)
    sat = np.where(mx > 0, (mx - mn) / np.maximum(mx, 1e-12), 0)
    h = np.pad(blur(lum, HEIGHT_BLUR * scale, wrap), 1, mode="wrap" if wrap else "edge")
    at = lambda dy, dx: h[1 + dy:h.shape[0] - 1 + dy, 1 + dx:h.shape[1] - 1 + dx]
    gx = at(-1, 1) + 2 * at(0, 1) + at(1, 1) - at(-1, -1) - 2 * at(0, -1) - at(1, -1)
    gy = at(1, -1) + 2 * at(1, 0) + at(1, 1) - at(-1, -1) - 2 * at(-1, 0) - at(-1, 1)
    k = DEPTH * scale / 8
    n = np.dstack([-gx * k, -gy * k, np.ones_like(gx)])
    n /= np.linalg.norm(n, axis=-1, keepdims=True)
    normal = np.clip(np.round(128 + 127 * n), 1, 255).astype(np.uint8)
    s = CONTRAST_BLUR * scale
    mean, sq, msat = blur(lum, s, wrap), blur(lum * lum, s, wrap), blur(sat, s, wrap)
    contrast = np.sqrt(np.maximum(sq - mean * mean, 0))
    metal = (1 - smooth(0.12, 0.35, msat)) * smooth(0.2, 0.55, mean) * (1 - smooth(0.04, 0.12, contrast))
    rough = np.clip(BASE_ROUGH + CONTRAST_ROUGH * contrast - METAL_SMOOTH * metal, MIN_ROUGH, 1)
    return normal, np.round(rough * 255).astype(np.uint8)


def write_relief(path: Path, image: np.ndarray, frames: int, scale: int) -> int:
    """RRR_n.png and RRR_r.png beside RRR.png (a wall: wrapped); returns their bytes."""
    fh = image.shape[0] // frames
    parts = [relief_frame(image[f * fh:(f + 1) * fh], scale, True) for f in range(frames)]
    n_path, r_path = path.with_name(path.stem + "_n.png"), path.with_name(path.stem + "_r.png")
    Image.fromarray(np.vstack([p[0] for p in parts]), "RGB").save(n_path, optimize=False, compress_level=6)
    Image.fromarray(np.vstack([p[1] for p in parts]), "L").save(r_path, optimize=False, compress_level=6)
    return n_path.stat().st_size + r_path.stat().st_size


def cmd_maps(a) -> None:
    """The relief maps of an existing mod's walls, from its own PNGs (no upscaling)."""
    root = Path(a.mods).resolve() / a.name / "textures"
    done = 0
    for rec in load_manifest(Path(a.work).resolve()):
        path = root / f"TEXTURE.{rec['bank']:03d}" / f"{rec['record']:03d}.png"
        if rec["layout"] != "wall" or not path.exists():
            continue
        image = np.array(Image.open(path).convert("RGBA"))
        write_relief(path, image, rec["frames"], image.shape[1] // rec["width"])
        done += 1
    print(f"relief maps for {done} records")


# ---------------------------------------------------------------- sheet

def cmd_sheet(a) -> None:
    from PIL import ImageDraw
    mods, out = Path(a.mods).resolve(), Path(a.out).resolve()
    out.mkdir(parents=True, exist_ok=True)
    cell = 256
    for method in a.methods.split(","):
        root = mods / f"hires-{method}" / "textures"
        sheet = Image.new("RGB", (cell * 6, (cell + 14) * 2), (40, 40, 48))
        draw = ImageDraw.Draw(sheet)
        for n, (bank, record, what) in enumerate(SHEET):
            p = root / f"TEXTURE.{bank:03d}" / f"{record:03d}.png"
            x, y = (n % 6) * cell, (n // 6) * (cell + 14)
            if p.exists():
                im = Image.open(p).convert("RGBA")
                checker = Image.new("RGBA", im.size, (255, 0, 255, 255))
                im = Image.alpha_composite(checker, im)
                h = min(im.height, im.width * 2)   # first frames of a tall stack
                im = im.crop((0, 0, im.width, h))
                im.thumbnail((cell, cell), Image.Resampling.NEAREST)
                sheet.paste(im.convert("RGB"), (x, y))
            draw.text((x + 2, y + cell), f"{bank}/{record} {what}", fill=(220, 220, 220))
        sheet.save(out / f"sheet-{method}.png")
        print(out / f"sheet-{method}.png")


def cmd_list(_a) -> None:
    for m, (kind, *_rest, lic) in METHODS.items():
        print(f"{m:15s} {kind:8s} {lic}")


def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = ap.add_subparsers(dest="cmd", required=True)
    p = sub.add_parser("export")
    p.add_argument("--data", required=True)
    p.add_argument("--work", required=True)
    p.set_defaults(fn=cmd_export)
    p = sub.add_parser("fetch")
    p.add_argument("--method", required=True, choices=METHODS)
    p.add_argument("--cache", required=True)
    p.set_defaults(fn=cmd_fetch)
    p = sub.add_parser("run")
    p.add_argument("--work", required=True)
    p.add_argument("--method", required=True, choices=METHODS)
    p.add_argument("--mods", required=True)
    p.add_argument("--name")
    p.add_argument("--cache")
    p.add_argument("--banks")
    p.set_defaults(fn=cmd_run)
    p = sub.add_parser("maps")
    p.add_argument("--work", required=True)
    p.add_argument("--mods", required=True)
    p.add_argument("--name", required=True)
    p.set_defaults(fn=cmd_maps)

    p = sub.add_parser("sheet")
    p.add_argument("--mods", required=True)
    p.add_argument("--methods", required=True)
    p.add_argument("--out", required=True)
    p.set_defaults(fn=cmd_sheet)
    p = sub.add_parser("list")
    p.set_defaults(fn=cmd_list)
    a = ap.parse_args()
    a.fn(a)


if __name__ == "__main__":
    main()
