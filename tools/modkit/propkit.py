"""Approach A prop models: scripted shapes on a DOS model's proportions, textured with its own records.

Each new triangle takes its texture coordinates from the nearest DOS triangle (extrapolated over that
triangle's plane), so the DOS art lands where DOS drew it. Textured faces use the hi-res records
(colour NNN.png, relief NNN_n.png, roughness NNN_r.png); palette-coloured faces get plain materials.

  # the DOS models as .glb (one per name)
  SkyNetEdit --offscreen --export-gltf DMPSTR0 --export-dir <work>/dosglb
  # build the replacements into a mod (models/NAME.glb + sidecar NAME.json)
  python -I tools/modkit/propkit.py --dos <work>/dosglb --hires <hires>/textures --mod <game>/mods/props-a [NAME...]

Python 3 with numpy, scipy and Pillow. Names: see JOBS (python -I propkit.py --list).
"""
import argparse
import importlib.util
import io
import json
import math
import struct
import sys
import time
from pathlib import Path

import numpy as np
from PIL import Image
from scipy.spatial import ConvexHull, HalfspaceIntersection

_spec = importlib.util.spec_from_file_location("modkit", Path(__file__).with_name("modkit.py"))
modkit = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(modkit)

CFG = {"dos": None, "hires": None, "models": None}


# ---------------------------------------------------------------- glb reading
_CT = {5126: np.float32, 5125: np.uint32, 5123: np.uint16, 5121: np.uint8, 5122: np.int16, 5120: np.int8}
_NC = {"SCALAR": 1, "VEC2": 2, "VEC3": 3, "VEC4": 4, "MAT4": 16}


def glb_load(path):
    d = Path(path).read_bytes()
    jl = struct.unpack_from("<I", d, 12)[0]
    doc = json.loads(d[20:20 + jl])
    off = 20 + jl
    bl = struct.unpack_from("<I", d, off)[0]
    return doc, d[off + 8: off + 8 + bl]


def glb_acc(doc, b, i):
    a = doc["accessors"][i]; v = doc["bufferViews"][a["bufferView"]]
    n = _NC[a["type"]]; dt = _CT[a["componentType"]]
    start = v.get("byteOffset", 0) + a.get("byteOffset", 0)
    stride = v.get("byteStride", 0)
    if stride and stride != n * np.dtype(dt).itemsize:
        raw = np.frombuffer(b, np.uint8, count=stride * a["count"], offset=start).reshape(a["count"], stride)
        return raw[:, :n * np.dtype(dt).itemsize].copy().view(dt).reshape(a["count"], n)
    arr = np.frombuffer(b, dt, count=a["count"] * n, offset=start)
    return arr.reshape(a["count"], n) if n > 1 else arr


# ---------------------------------------------------------------- output mesh
def png(im):
    b = io.BytesIO(); im.save(b, "PNG", optimize=True); return b.getvalue(), "image/png"


class Mesh:
    """Triangles per material part, explicit per-vertex normals."""
    def __init__(self):
        self.parts = {}
        self.tags = {}

    def add(self, name, pts, uvs, normals, tags=(None, None, None)):
        p = self.parts.setdefault(name, {"positions": [], "normals": [], "uvs": [], "indices": []})
        self.tags.setdefault(name, []).extend(tags)
        base = len(p["positions"])
        p["positions"] += [tuple(map(float, v)) for v in pts]
        p["uvs"] += [tuple(map(float, u)) for u in uvs]
        p["normals"] += [tuple(map(float, n / np.linalg.norm(n))) for n in normals]
        p["indices"] += [base, base + 1, base + 2]

    def write(self, path, materials, images):
        parts = [{"name": n, **p, "material": materials[n]} for n, p in self.parts.items()]
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(with_emission(modkit.glb(parts, images), [p["material"] for p in parts]))
        return sum(len(p["indices"]) // 3 for p in parts)


def with_emission(data, materials):
    """modkit.glb writes no emissive texture: add one where a material names "emissive_image"."""
    if not any("emissive_image" in m for m in materials):
        return data
    jl = struct.unpack_from("<I", data, 12)[0]
    doc = json.loads(data[20:20 + jl]); rest = data[20 + jl:]
    for gm, m in zip(doc["materials"], materials):
        if "emissive_image" in m:
            gm["emissiveTexture"] = {"index": m["emissive_image"]}   # texture i = image i
            gm["emissiveFactor"] = list(m.get("emissive", (1.0, 1.0, 1.0)))
    js = json.dumps(doc, separators=(",", ":")).encode(); js += b" " * (-len(js) % 4)
    return (struct.pack("<III", 0x46546C67, 2, 12 + 8 + len(js) + len(rest)) + struct.pack("<II", len(js), 0x4E4F534A)
            + js + rest)


def sidecar(name, comment, frames=None):
    """frames: a robot segment's DOS frames it stands in for ("all": every one; None: the prop default, 0)."""
    sc = {"fit": "none", "scale": 1.0, "align": "origin", "comment": comment}
    if frames is not None:
        sc["frames"] = frames
    (CFG["models"] / f"{name}.json").write_text(json.dumps(sc, indent=2) + "\n")


# ---------------------------------------------------------------- the DOS model as triangles
class Dos:
    def __init__(self, name):
        doc, b = glb_load(CFG["dos"] / f"{name}.glb")
        P, UV, K, PRIM, F = [], [], [], [], []
        self.colour = {}
        for pi, pr in enumerate(doc["meshes"][0]["primitives"]):
            pos = glb_acc(doc, b, pr["attributes"]["POSITION"]).astype(float)
            idx = glb_acc(doc, b, pr["indices"]).reshape(-1, 3)
            mat = doc["materials"][pr["material"]]
            key = mat["name"]  # "TEXTURE.436 #000" or "palette 219"
            self.colour[key] = mat["pbrMetallicRoughness"].get("baseColorFactor", [1, 1, 1, 1])
            uv = (glb_acc(doc, b, pr["attributes"]["TEXCOORD_0"]).astype(float)
                  if "TEXCOORD_0" in pr["attributes"] else np.zeros((len(pos), 2)))
            for t in idx:
                P.append(pos[t]); UV.append(uv[t]); K.append(key); PRIM.append(pi)
            frames = [pos] + [pos + glb_acc(doc, b, t["POSITION"]) for t in pr.get("targets", [])]
            F.append(np.stack([f[idx] for f in frames]))
        self.P, self.UV, self.K, self.prim = np.array(P), np.array(UV), K, np.array(PRIM)
        self.F = np.concatenate(F, axis=1)   # (frames, triangles, 3, 3); frame 0 is P
        n = np.cross(self.P[:, 1] - self.P[:, 0], self.P[:, 2] - self.P[:, 0])
        self.N = n / np.linalg.norm(n, axis=1)[:, None]
        self.lo, self.hi = self.P.reshape(-1, 3).min(0), self.P.reshape(-1, 3).max(0)

    def pick(self, keys):
        return np.array([i for i, k in enumerate(self.K) if k in keys])


def bary(P, p):
    """Barycentric coordinates of p projected on triangle P's plane (unclamped: extrapolates)."""
    a, b, c = P; v0, v1, v2 = b - a, c - a, p - a
    d00, d01, d11, d20, d21 = v0 @ v0, v0 @ v1, v1 @ v1, v2 @ v0, v2 @ v1
    den = d00 * d11 - d01 * d01
    v = (d11 * d20 - d01 * d21) / den; w = (d00 * d21 - d01 * d20) / den
    return np.array([1 - v - w, v, w])


def closest_dist(P, p):
    bc = np.clip(bary(P, p), 0, None); bc /= bc.sum()
    return np.linalg.norm(bc @ P - p)


class Out:
    """New geometry, each triangle mapped onto the best DOS triangle among the candidates."""
    def __init__(self, dos):
        self.dos = dos; self.m = Mesh(); self.size = float(np.max(dos.hi - dos.lo))
        self.tag = None   # robots: the rigid cluster the next triangles follow

    def choose(self, tri, n, cand):
        # nearest, facing the same way; a later primitive (DOS decal) wins a tie
        c = tri.mean(0); best, bs = None, 1e18
        for i in cand:
            s = closest_dist(self.dos.P[i], c) + 0.25 * self.size * (1 - n @ self.dos.N[i]) - 0.01 * self.dos.prim[i]
            if s < bs:
                bs, best = s, i
        return best

    def tris(self, tris, cand, normals=None, part=None, exact=None):
        """tris: 3x3 arrays, counter-clockwise from outside; normals: per vertex, or None for flat.
        exact: the DOS triangle these are (robots: its corners follow DOS's frames as they are)."""
        for k, t in enumerate(tris):
            t = np.asarray(t, float)
            fn = np.cross(t[1] - t[0], t[2] - t[0]); l = np.linalg.norm(fn)
            if l < 1e-9:
                continue
            fn /= l
            i = self.choose(t, fn, cand)
            ns = [fn] * 3 if normals is None else [np.asarray(x, float) for x in normals[k]]
            tags = [("dos", exact, c) for c in range(3)] if exact is not None else [self.tag] * 3
            self.m.add(part or self.dos.K[i], list(t), [bary(self.dos.P[i], p) @ self.dos.UV[i] for p in t], ns, tags)

    def write(self, name, mats, images):
        return self.m.write(CFG["models"] / f"{name}.glb", mats, images)


# ---------------------------------------------------------------- materials
def record_mat(key, images, metallic, rough_scale=1.0, normal_scale=1.0, glow=False, tame=False):
    """DOS record 'TEXTURE.436 #000' -> hi-res colour, relief normal (+Y up for glTF) and roughness."""
    bank, r = key.split(" #"); bank = bank.split(".")[1]
    folder = CFG["hires"] / f"TEXTURE.{bank}"
    col = Image.open(folder / f"{r}.png").convert("RGB")
    if tame:   # pull texels brighter than the record's median most of the way back (lit-paint smears)
        a = np.asarray(col).astype(float); med = np.median(a.reshape(-1, 3), axis=0)
        a = np.where(a > med * 1.1, med * 1.1 + (a - med * 1.1) * 0.25, a)
        col = Image.fromarray(np.clip(a, 0, 255).astype(np.uint8))
    images.append(png(col))
    m = {"image": len(images) - 1, "metallic": 1.0, "roughness": 1.0}

    # relief and roughness when the hi-res set has them (a record without: flat, fully rough)
    if (folder / f"{r}_n.png").exists():
        nrm = Image.open(folder / f"{r}_n.png").convert("RGB")
        rr, gg, bb = nrm.split(); nrm = Image.merge("RGB", (rr, gg.point(lambda v: 255 - v), bb))
        images.append(png(nrm))
        m.update(normal=len(images) - 1, normal_scale=normal_scale)
    if (folder / f"{r}_r.png").exists():
        rough = Image.open(folder / f"{r}_r.png").convert("L").point(lambda v: min(255, int(v * rough_scale)))
    else:
        rough = Image.new("L", col.size, 255)
    mr = Image.merge("RGB", (Image.new("L", rough.size, 255), rough, Image.new("L", rough.size, int(255 * metallic))))
    images.append(png(mr))
    m["metallic_roughness"] = len(images) - 1
    if glow:
        images.append(png(glow_image(col)))
        m["emissive_image"] = len(images) - 1
    return m


def glow_image(col):
    """The lit parts of a DOS record (bright, saturated texels: lamps, LEDs, screens) as an emission map."""
    a = np.asarray(col).astype(float) / 255
    mx, mn = a.max(2), a.min(2)
    sat = (mx - mn) / np.maximum(mx, 1e-3)
    mask = np.clip((mx - 0.55) / 0.25, 0, 1) * np.clip((sat - 0.45) / 0.2, 0, 1)
    return Image.fromarray((a * mask[..., None] * 255).astype(np.uint8))


def paint_mat(rgb_linear, images, metallic, rough, seed, size=256):
    """A DOS palette colour as weathered paint: mottled colour and roughness, no relief."""
    rng = np.random.default_rng(seed)

    def noise(cells):
        im = Image.fromarray((rng.random((cells[0], cells[1])) * 255).astype(np.uint8))
        return np.asarray(im.resize((size, size), Image.BICUBIC)) / 255
    n1, n2, streak = noise((size // 16, size // 16)), noise((size // 4, size // 4)), noise((1, size // 2))
    k = 1 + 0.10 * (n1 - 0.5) + 0.06 * (n2 - 0.5) - 0.25 * np.clip(streak - 0.6, 0, 1)
    srgb = np.array([255 * (c ** (1 / 2.2)) for c in rgb_linear[:3]])
    col = np.clip(srgb[None, None, :] * k[..., None], 0, 255).astype(np.uint8)
    r = np.clip(rough + 0.15 * (n2 - 0.5) + 0.1 * np.clip(streak - 0.6, 0, 1), 0, 1)
    mr = np.dstack([np.full((size, size), 255), r * 255, np.full((size, size), 255 * metallic)]).astype(np.uint8)
    images += [png(Image.fromarray(col)), png(Image.fromarray(mr))]
    k = len(images) - 2
    return {"image": k, "metallic_roughness": k + 1, "metallic": 1.0, "roughness": 1.0}


# ---------------------------------------------------------------- shapes
def chamfer_hull(points, bevel):
    """Convex hull of points with every edge chamfered `bevel` units back on each face."""
    h = ConvexHull(points)
    planes = []
    for e in h.equations:
        if not any(np.allclose(e, q, atol=1e-4) for q in planes):
            planes.append(e)
    V = points[h.vertices]
    hs = list(planes)
    for i in range(len(planes)):
        for j in range(i + 1, len(planes)):
            ni, nj = planes[i][:3], planes[j][:3]
            on = [v for v in V if abs(ni @ v + planes[i][3]) < 1e-3 and abs(nj @ v + planes[j][3]) < 1e-3]
            if len(on) < 2:
                continue
            q, edge = on[0], on[1] - on[0]
            u = np.cross(ni, edge); u /= np.linalg.norm(u)
            if u @ nj > 0:
                u = -u
            ne = ni + nj; ne /= np.linalg.norm(ne)
            hs.append(np.append(ne, -(ne @ (q + bevel * u))))
    pts = HalfspaceIntersection(np.array(hs), points.mean(0)).intersections
    h2 = ConvexHull(pts)
    tris = []
    for s, e in zip(h2.simplices, h2.equations):
        t = pts[s]
        if np.cross(t[1] - t[0], t[2] - t[0]) @ e[:3] < 0:
            t = t[[0, 2, 1]]
        tris.append(t)
    return tris


def lathe(axis_o, axis_d, ref, profile, seg):
    """Surface of revolution, smooth: profile [(r, t)] along the axis. Returns (tris, normals)."""
    d = np.asarray(axis_d, float); d /= np.linalg.norm(d)
    e1 = np.asarray(ref, float); e1 -= (e1 @ d) * d; e1 /= np.linalg.norm(e1); e2 = np.cross(d, e1)
    radial = lambda a: math.cos(a) * e1 + math.sin(a) * e2
    ring = lambda r, t, a: axis_o + t * d + r * radial(a)
    prof = np.array(profile, float)
    segn = []
    for k in range(len(prof) - 1):
        dr, dt = prof[k + 1] - prof[k]
        nrm = np.array([dt, -dr]); l = np.linalg.norm(nrm)
        segn.append(nrm / l if l > 0 else np.array([1.0, 0]))
    tris, norms = [], []
    for k in range(len(prof) - 1):
        (r0, t0), (r1, t1) = prof[k], prof[k + 1]
        nr, nt = segn[k]
        for s in range(seg):
            a0, a1 = s / seg * 2 * math.pi, (s + 1) / seg * 2 * math.pi
            n0, n1 = nr * radial(a0) + nt * d, nr * radial(a1) + nt * d
            p00, p01, p10, p11 = ring(r0, t0, a0), ring(r0, t0, a1), ring(r1, t1, a0), ring(r1, t1, a1)
            for t, n in (((p00, p11, p10), (n0, n1, n0)), ((p00, p01, p11), (n0, n1, n1))):
                t = np.array(t); fn = np.cross(t[1] - t[0], t[2] - t[0])
                if np.linalg.norm(fn) < 1e-9:
                    continue
                if fn @ (n[0] + n[1] + n[2]) < 0:
                    t = t[[0, 2, 1]]; n = (n[0], n[2], n[1])
                tris.append(t); norms.append(n)
    return tris, norms


def box_tris(lo, hi):
    """Closed box, outward."""
    lo, hi = np.asarray(lo, float), np.asarray(hi, float)
    c = np.array([[lo[0], lo[1], lo[2]], [hi[0], lo[1], lo[2]], [hi[0], hi[1], lo[2]], [lo[0], hi[1], lo[2]],
                  [lo[0], lo[1], hi[2]], [hi[0], lo[1], hi[2]], [hi[0], hi[1], hi[2]], [lo[0], hi[1], hi[2]]])
    centre, out = (lo + hi) / 2, []
    for f in ((0, 3, 2, 1), (4, 5, 6, 7), (0, 1, 5, 4), (2, 3, 7, 6), (1, 2, 6, 5), (0, 4, 7, 3)):
        a, b, cc, e = (c[i] for i in f)
        for t in (np.array([a, b, cc]), np.array([a, cc, e])):
            n = np.cross(t[1] - t[0], t[2] - t[0])
            out.append(t if n @ (t.mean(0) - centre) >= 0 else t[[0, 2, 1]])
    return out


# ---------------------------------------------------------------- models
def dumpster():
    d = Dos("DMPSTR0"); o = Out(d)
    body = d.pick({"TEXTURE.436 #000"})
    o.tris(chamfer_hull(d.P[body].reshape(-1, 3), 2.2), body)

    # the lid: a slab standing 1.6 u proud of the DOS lid face, a little wider than the body
    lid = d.pick({"TEXTURE.436 #002"})
    n = d.N[lid[0]]; lp = d.P[lid].reshape(-1, 3)
    slab = np.concatenate([lp - n * 0.5, lp + n * 1.6])
    c = slab.mean(0); slab = c + (slab - c) * np.array([1.02, 1.025, 1.02])
    o.tris(chamfer_hull(slab, 0.7), lid)

    images = []
    mats = {"TEXTURE.436 #000": record_mat("TEXTURE.436 #000", images, metallic=0.55, rough_scale=0.9),
            "TEXTURE.436 #002": record_mat("TEXTURE.436 #002", images, metallic=0.45)}
    t = o.write("DMPSTR0", mats, images)
    sidecar("DMPSTR0", "approach A: DOS dumpster chamfered 2.2 u, lid a raised slab; DOS records hi-res + relief")
    return t


def trailer():
    d = Dos("TRCKBOX"); o = Out(d)
    o.tris(chamfer_hull(d.P.reshape(-1, 3), 3.0), np.arange(len(d.K)))
    images = []
    mats = {k: record_mat(k, images, metallic=0.5, normal_scale=0.45) for k in sorted(set(d.K))}
    t = o.write("TRCKBOX", mats, images)
    sidecar("TRCKBOX", "approach A: DOS trailer box chamfered 3 u; DOS records hi-res + relief")
    return t


def tanker():
    d = Dos("TRCKTNK"); o = Out(d)
    body = d.pick({"TEXTURE.459 #000", "TEXTURE.459 #002", "TEXTURE.459 #004"})
    walk = d.pick({"TEXTURE.459 #003"})
    lo, hi = d.lo, d.hi
    R = (hi[0] - lo[0]) / 2; cy = (lo[1] + hi[1]) / 2; z0, z1 = lo[2], hi[2]

    # the octagon as a round shell rolling over a knuckle into shallow dished ends
    kn = 9.0
    prof = [(0.0, z0 - 4), (R * 0.55, z0 - 2.5), (R * 0.85, z0 + 0.5)]
    prof += [(R - kn + kn * math.cos(a), z0 + kn - kn * math.sin(a)) for a in np.linspace(math.pi / 2 * 0.6, 0, 4)]
    prof += [(R, z0 + kn + (z1 - z0 - 2 * kn) * f) for f in np.linspace(0, 1, 7)]
    prof += [(R - kn + kn * math.cos(a), z1 - kn + kn * math.sin(a)) for a in np.linspace(0, math.pi / 2 * 0.6, 4)]
    prof += [(R * 0.85, z1 - 0.5), (R * 0.55, z1 + 2.5), (0.0, z1 + 4)]
    tris, norms = lathe(np.array([0, cy, 0.0]), (0, 0, 1), (1, 0, 0), prof, 32)
    o.tris(tris, body, norms)

    # the catwalk on top: a plate sunk into the curve
    wl = d.P[walk].reshape(-1, 3); wx, wy = wl[:, 0].max(), wl[:, 1].max()
    o.tris(box_tris((-wx, wy - 6, z0 + 6), (wx, wy + 1.2, z1 - 6)), walk)

    images = []
    keys = sorted(set(d.K[i] for i in np.concatenate([body, walk])))
    mats = {k: record_mat(k, images, metallic=0.75, rough_scale=0.75) for k in keys}
    t = o.write("TRCKTNK", mats, images)
    sidecar("TRCKTNK", "approach A: DOS octagonal tank as a round lathe with dished ends + catwalk; DOS records hi-res + relief")
    return t


def guardrail(name, post_key, panel_key, cap_key=None):
    """DOS posts lightly chamfered; the DOS panel at its own size with a shallow W-beam profile,
    drawn on the sides DOS draws (both, for MAP.210's rails). Non-metal, so it keeps the DOS brightness."""
    d = Dos(name); o = Out(d)
    posts = d.pick({post_key} | ({cap_key} if cap_key else set()))
    for side in (-1, 1):
        sel = np.array([i for i in posts if np.sign(d.P[i].mean(0)[0]) == side])
        o.tris(chamfer_hull(d.P[sel].reshape(-1, 3), 0.6), sel)

    panel = d.pick({panel_key}); pp = d.P[panel].reshape(-1, 3)
    front = np.sign(d.N[panel[0]][2])                 # the DOS panel faces +z or -z
    x0, x1 = pp[:, 0].min(), pp[:, 0].max(); y0, y1 = pp[:, 1].min(), pp[:, 1].max(); z = pp[:, 2].mean()
    depth = 0.08 * (y1 - y0)
    N = 16
    prof = []
    for k in range(N + 1):
        s = k / N; s2 = min(max((s - 0.06) / 0.88, 0), 1)
        prof.append((y0 + s * (y1 - y0), z + front * depth * (1 - math.cos(4 * math.pi * s2)) / 2))

    def pn(j, side):  # profile normal from the neighbouring slope, on that side
        j0, j1 = max(j - 1, 0), min(j + 1, N)
        n = np.array([0.0, -(prof[j1][1] - prof[j0][1]), prof[j1][0] - prof[j0][0]])
        n /= np.linalg.norm(n)
        return n if n[2] * side > 0 else -n

    # one sheet per side DOS draws (its panels are quads on both sides), each mapped from that side
    for side in sorted({np.sign(d.N[i][2]) for i in panel}):
        cand = np.array([i for i in panel if np.sign(d.N[i][2]) == side])
        tris, norms = [], []
        for k in range(N):
            (ya, za), (yb, zb) = prof[k], prof[k + 1]
            na, nb = pn(k, side), pn(k + 1, side)
            a, b, c, e = np.array([x0, ya, za]), np.array([x1, ya, za]), np.array([x1, yb, zb]), np.array([x0, yb, zb])
            for t, n in (((a, b, c), (na, na, nb)), ((a, c, e), (na, nb, nb))):
                t = np.array(t)
                if np.cross(t[1] - t[0], t[2] - t[0]) @ n[0] < 0:
                    t = t[[0, 2, 1]]; n = (n[0], n[2], n[1])
                tris.append(t); norms.append(n)
        o.tris(tris, cand, norms)

    images = []
    mats = {post_key: record_mat(post_key, images, metallic=0.0, normal_scale=0.6),
            panel_key: record_mat(panel_key, images, metallic=0.0, normal_scale=0.5)}
    if cap_key:
        mats[cap_key] = {"colour": tuple(d.colour[cap_key]), "metallic": 0.0, "roughness": 0.8}
    t = o.write(name, mats, images)
    sidecar(name, "approach A: guard rail; DOS posts chamfered, DOS panel with a shallow W-beam profile; DOS records hi-res + relief")
    return t


def ibeam(name):
    d = Dos(name); o = Out(d)
    fl = d.pick({"palette 219"}); web = d.pick({"palette 220"})
    h = d.hi[1]; w = d.hi[0]; z0, z1 = d.lo[2], d.hi[2]; tf, tw = 1.5, 1.1

    # flanges on the DOS beam's box, a web between them
    for s in (-1, 1):
        yA, yB = sorted((s * h, s * (h - tf)))
        o.tris(box_tris((-w, yA, z0), (w, yB, z1)), fl, part="palette 219")
    o.tris(box_tris((-tw / 2, -(h - tf), z0), (tw / 2, h - tf, z1)), web, part="palette 220")

    images = []
    mats = {"palette 219": paint_mat(d.colour["palette 219"], images, 0.0, 0.85, 5),
            "palette 220": paint_mat(d.colour["palette 220"], images, 0.0, 0.85, 6)}
    t = o.write(name, mats, images)
    sidecar(name, "approach A: I-beam (flanges + web) on the DOS beam's box; DOS palette colours as matt paint")
    return t


def lever():
    d = Dos("LEVHAN01"); o = Out(d)
    shaft = d.pick({"palette 075"}); cap = d.pick({"palette 074"})
    y_end = d.lo[1]; r = 3.0
    tris, norms = lathe(np.zeros(3), (0, -1, 0), (1, 0, 0), [(0, 0), (r, 0.0), (r, -y_end - 9), (r * 0.8, -y_end - 8)], 16)
    o.tris(tris, shaft, norms, part="palette 075")
    kr = 5.2; kc = -y_end - 4.5
    prof = [(kr * math.sin(a), kc - kr * math.cos(a)) for a in np.linspace(0, math.pi, 9)]
    tris, norms = lathe(np.zeros(3), (0, -1, 0), (1, 0, 0), prof, 16)
    o.tris(tris, cap, norms, part="palette 074")
    mats = {"palette 075": {"colour": (0.55, 0.56, 0.58, 1.0), "metallic": 1.0, "roughness": 0.35},
            "palette 074": {"colour": tuple(np.array(d.colour["palette 074"][:3]) * 1.6) + (1.0,),
                            "metallic": 0.0, "roughness": 0.3}}
    t = o.write("LEVHAN01", mats, [])
    sidecar("LEVHAN01", "approach A: lever as a round steel rod with a knob, DOS length and knob colour")
    return t


# ---------------------------------------------------------------- any DOS model, part by part
def components(d):
    """DOS triangles grouped into connected parts (shared vertex positions)."""
    parent = list(range(len(d.K)))

    def find(i):
        while parent[i] != i:
            parent[i] = parent[parent[i]]; i = parent[i]
        return i
    seen = {}
    for i, t in enumerate(d.P):
        for p in t:
            key = tuple(np.round(p, 1))
            if key in seen:
                parent[find(i)] = find(seen[key])
            else:
                seen[key] = i
    groups = {}
    for i in range(len(d.K)):
        groups.setdefault(find(i), []).append(i)
    return [np.array(g) for g in groups.values()]


def flatness(pts):
    """(normal, thickness) of a point set's best plane."""
    c = pts - pts.mean(0)
    _, sv, vt = np.linalg.svd(c, full_matrices=False)
    n = vt[2]
    return n, float(np.ptp(c @ n))


def is_convex(d, tris, hull_pts):
    h = ConvexHull(hull_pts)
    for i in tris:
        c = d.P[i].mean(0)
        if np.min(np.abs(h.equations[:, :3] @ c + h.equations[:, 3])) > 0.5:
            return False
    return True


def cylinder_fit(pts, seg=16):
    """A round rod on a point set's long axis (radius: mean distance to the axis), flat ends."""
    c = pts.mean(0)
    _, _, vt = np.linalg.svd(pts - c, full_matrices=False)
    ax = vt[0]; t = (pts - c) @ ax
    r = float(np.mean(np.linalg.norm((pts - c) - np.outer(t, ax), axis=1)))
    ref = vt[1]
    prof = [(0.0, t.min()), (r, t.min()), (r, t.max()), (0.0, t.max())]
    return lathe(c, ax, ref, prof, seg)


def glow_auto(d, share=0.004):
    """The records of a lit model (screens, LEDs, lamps) whose bright saturated texels cover at least `share`."""
    out = set()
    for k in set(d.K):
        if k.startswith("palette"):
            continue
        bank, r = k.split(" #")
        col = Image.open(CFG["hires"] / f"TEXTURE.{bank.split('.')[1]}" / f"{r}.png").convert("RGB")
        if (np.asarray(glow_image(col)).max(2) > 40).mean() >= share:
            out.add(k)
    return out


def open_shell(d, g, pts):
    """A convex part DOS leaves open (a box without its lid, a counter open at the back) or draws as
    sheets (a chair's seat and back, whose hull would be a wedge): chamfering would close it into a slab,
    so it keeps the DOS triangles. Open: a DOS triangle faces into the hull (DOS shows the inside), or a
    hull face DOS does not cover is slanted, or there are two of them. One uncovered square face is the
    underside DOS never draws and is closed."""
    h = ConvexHull(pts)
    if sum(np.linalg.norm(np.cross(t[1] - t[0], t[2] - t[0])) / 2 for t in d.P[g]) < 0.8 * h.area:
        return True   # far less surface than its hull: an open box
    facets = {}
    for simp, eq in zip(h.simplices, h.equations):
        t = pts[simp]
        f = facets.setdefault(tuple(np.round(eq, 2) + 0.0), [eq, 0.0, 0.0])
        f[1] += np.linalg.norm(np.cross(t[1] - t[0], t[2] - t[0])) / 2
    total = sum(f[1] for f in facets.values())
    for i in g:
        t = d.P[i]; c = t.mean(0); a = np.linalg.norm(np.cross(t[1] - t[0], t[2] - t[0])) / 2
        on = [f for f in facets.values() if abs(f[0][:3] @ c + f[0][3]) < 0.6]
        if on and all(d.N[i] @ f[0][:3] < -0.9 for f in on):
            return True
        for f in on:
            if d.N[i] @ f[0][:3] > 0.9:
                f[2] += a; break
    bare = [f for f in facets.values() if f[1] > 0.04 * total and f[2] < 0.6 * f[1]]
    return len(bare) > 1 or any(np.max(np.abs(f[0][:3])) < 0.99 for f in bare)


def mixed_faces(d, g, pts):
    """A face of the part's hull that DOS draws with two records or colours (a painted band, a decal
    quad): one chamfered hull triangle would take one of them, split along its diagonal."""
    h = ConvexHull(pts)
    seen = {}
    for i in g:
        c = d.P[i].mean(0)
        for eq in h.equations:
            if abs(eq[:3] @ c + eq[3]) < 0.3 and d.N[i] @ eq[:3] > 0.99:
                if seen.setdefault(tuple(np.round(eq, 2) + 0.0), d.K[i]) != d.K[i]:
                    return True
                break
    return False


def round_prism(pts, min_sides=6):
    """A DOS prism of six or more sides around a model axis (barrels, drums, pipes): a smooth round lathe
    with a small rounded rim between its end planes, else None. Returns (tris, normals)."""
    for ax in range(3):
        o = [i for i in range(3) if i != ax]
        t0, t1 = pts[:, ax].min(), pts[:, ax].max()
        ends = pts[(np.abs(pts[:, ax] - t0) < 0.3) | (np.abs(pts[:, ax] - t1) < 0.3)]
        if len(ends) != len(pts):
            continue
        uv = ends[:, o]; c = (uv.max(0) + uv.min(0)) / 2; rad = np.linalg.norm(uv - c, axis=1)
        if rad.min() < 0.88 * rad.max() or t1 - t0 < 0.3 * rad.max():
            continue
        # both ends the same near-regular polygon: 6+ corners, no gap between them much wider than the rest
        polys = [np.unique(np.round(uv[np.abs(ends[:, ax] - t) < 0.3], 1), axis=0) for t in (t0, t1)]
        angles = np.sort(np.degrees(np.arctan2(*(polys[0] - c).T[::-1])))
        gaps = np.diff(np.append(angles, angles[0] + 360)) if len(angles) else np.array([360.0])
        if min(len(q) for q in polys) < min_sides or len(polys[0]) != len(polys[1]) or gaps.max() > 1.6 * 360 / len(angles):
            continue
        r = float(rad.mean()) * (1 + math.cos(math.pi / len(angles))) / 2
        b = min(0.08 * r, 0.1 * (t1 - t0))
        prof = [(0.0, t0), (r - b, t0), (r - b * 0.3, t0 + b * 0.3), (r, t0 + b), (r, t1 - b),
                (r - b * 0.3, t1 - b * 0.3), (r - b, t1), (0.0, t1)]
        centre = np.zeros(3); centre[o[0]], centre[o[1]] = c
        d = np.zeros(3); d[ax] = 1; ref = np.zeros(3); ref[o[0]] = 1
        return lathe(centre, d, ref, prof, 32)
    return None


def limb_rod(pts, seg=16):
    """A long thin prism (a robot's bone, strut or barrel of 3-5 sides) as a smooth elliptic rod with
    domed ends, tapering as DOS's ends do; else None. Returns (tris, normals)."""
    c = pts.mean(0)
    _, _, vt = np.linalg.svd(pts - c, full_matrices=False)
    q = (pts - c) @ vt.T; L = np.ptp(q[:, 0]); w = np.ptp(q[:, 1:], 0)
    if L < 3 * w.max() or w.min() < 0.8 or w.max() > 1.6 * w.min() + 0.3:   # flat blades stay plates
        return None
    t0, t1 = q[:, 0].min(), q[:, 0].max()
    ends = []
    for t, side in ((t0, 1), (t1, -1)):
        near = q[np.abs(q[:, 0] - t) < 0.25 * L][:, 1:]
        ends.append(((near.max(0) + near.min(0)) / 2, np.maximum((near.max(0) - near.min(0)) / 2 * 0.92, 0.3)))
    # rings (t, centre, radii): a dome, the shaft tapering from one end's section to the other's, a dome
    dome = 0.35 * min(ends[0][1].min(), ends[1][1].min())
    rings = [(t0, ends[0][0], ends[0][1] * 0.0), (t0 + 0.4 * dome, ends[0][0], ends[0][1] * 0.8), (t0 + dome, ends[0][0], ends[0][1])]
    for f in np.linspace(0, 1, 6)[1:-1]:
        rings.append((t0 + dome + f * (t1 - t0 - 2 * dome), ends[0][0] * (1 - f) + ends[1][0] * f, ends[0][1] * (1 - f) + ends[1][1] * f))
    rings += [(t1 - dome, ends[1][0], ends[1][1]), (t1 - 0.4 * dome, ends[1][0], ends[1][1] * 0.8), (t1, ends[1][0], ends[1][1] * 0.0)]
    a = np.linspace(0, 2 * math.pi, seg, endpoint=False)
    G = np.array([[[t, cc[0] + r[0] * math.cos(x), cc[1] + r[1] * math.sin(x)] for x in a] for t, cc, r in rings])
    G = c + G @ vt                       # back to model space: (rings, seg, 3)
    nr = len(rings); N = np.zeros_like(G); tris = []
    for i in range(nr - 1):
        for j in range(seg):
            j2 = (j + 1) % seg
            for tri in (((i, j), (i + 1, j), (i + 1, j2)), ((i, j), (i + 1, j2), (i, j2))):
                P = np.array([G[x] for x in tri]); n = np.cross(P[1] - P[0], P[2] - P[0])
                tris.append((tri, n))
                for x in tri:
                    N[x] += n
    axis = vt[0]; out_t, out_n = [], []
    for tri, n in tris:
        P = np.array([G[x] for x in tri])
        if np.linalg.norm(n) < 1e-9:
            continue
        ns = [N[x] / max(np.linalg.norm(N[x]), 1e-9) for x in tri]
        if n @ (P.mean(0) - (c + ((P.mean(0) - c) @ axis) * axis)) < 0:   # outward from the axis
            P = P[[0, 2, 1]]; ns = [ns[0], ns[2], ns[1]]
        out_t.append(P); out_n.append(ns)
    return out_t, out_n


def generic(name, glow=(), round_keys=(), metal=(), ghost=(), keep=(), tame=(), comment="", groups=None,
            frames=None, rods=False):
    """Each connected DOS part: a flat panel becomes a bevelled slab (two-sided: centred, else behind its
    face), a flat part lying on another part's face stays a decal, a convex solid is chamfered (kept
    when a face has two records: mixed_faces), a part whose material is in round_keys becomes a round
    rod, a prism of 6+ sides a round drum (round_prism), anything else keeps the DOS triangles.
    ghost: records DOS draws see-through, kept as DOS triangles and blended.
    keep: parts kept as DOS triangles (open shells DOS shows from inside too).
    tame: records whose bright smears read too hot under the physical light.
    glow: records with an emission map, or "auto" (glow_auto). A convex part DOS leaves open keeps its
    triangles (open_shell).
    groups: a robot's rigid clusters (robot_clusters) in place of the connected parts; returns the
    unwritten (Out, materials, images, note) then."""
    d = Dos(name); o = Out(d)
    if glow == "auto":
        glow = glow_auto(d)
    comps = components(d) if groups is None else [g for g, _ in groups]
    tags = [None] * len(comps) if groups is None else [t for _, t in groups]
    solids = []
    for g in comps:
        pts = d.P[g].reshape(-1, 3)
        if flatness(pts)[1] > 0.5 and len(np.unique(np.round(pts, 1), axis=0)) >= 4:
            solids.append(ConvexHull(pts).equations)
    kept = rounded = 0
    for g, tag in zip(comps, tags):
        o.tag = tag
        if tag == "dos":   # a triangle DOS bends between frames: kept, its corners follow DOS's
            for i in g:
                o.tris([d.P[i]], [i], exact=i)
            continue
        pts = d.P[g].reshape(-1, 3); ext = np.ptp(pts, 0)
        n, thick = flatness(pts)
        if any(d.K[i] in ghost or d.K[i] in keep for i in g):
            for i in g:
                o.tris([d.P[i]], [i])
            continue
        if any(d.K[i] in round_keys for i in g):
            tris, norms = cylinder_fit(pts); o.tris(tris, g, norms); continue
        rod = limb_rod(pts) if rods else None
        if rod:
            o.tris(rod[0], g, rod[1]); rounded += 1; continue
        if thick <= 0.5:
            on_solid = any(np.min(np.abs(eq[:, :3] @ pts.T + eq[:, 3:4]).max(1)) < 0.6 for eq in solids)
            if on_solid:   # a decal: the DOS triangles, lifted off the face
                for i in g:
                    o.tris([d.P[i] + d.N[i] * 0.3], [i])
                continue
            sides = {int(np.sign(d.N[i] @ n)) for i in g}
            t = float(np.clip(0.04 * np.sort(ext)[-1], 1.0, 4.0))
            slab = np.concatenate([pts - n * t / 2, pts + n * t / 2]) if len(sides) == 2 else \
                np.concatenate([pts, pts - n * t * next(iter(sides))])
            # edges from the bevelled slab; the broad faces are DOS's own triangles moved onto the slab's
            # faces (a hull face spans several DOS triangles and would stretch one's mapping over the rest)
            edges = [h for h in chamfer_hull(slab, min(0.35 * t, 1.2))
                     if abs(np.cross(h[1] - h[0], h[2] - h[0]) @ n) < 0.99 * np.linalg.norm(np.cross(h[1] - h[0], h[2] - h[0]))]
            o.tris(edges, g)
            off = {1: t / 2, -1: -t / 2} if len(sides) == 2 else {next(iter(sides)): 0.0, -next(iter(sides)): -t * next(iter(sides))}
            for i in g:
                sd = int(np.sign(d.N[i] @ n))
                o.tris([d.P[i] + n * off[sd]], [i])
            if len(sides) == 1:   # the hidden back: the front triangles reversed
                sd = next(iter(sides))
                for i in g:
                    o.tris([(d.P[i] + n * off[-sd])[[0, 2, 1]]], [i])
            continue
        if is_convex(d, g, pts) and not open_shell(d, g, pts):
            rp = round_prism(pts)
            if rp:
                o.tris(rp[0], g, rp[1]); rounded += 1; continue
            if mixed_faces(d, g, pts):
                kept += 1
                for i in g:
                    o.tris([d.P[i]], [i])
                continue
            bev = float(np.clip(0.03 * np.sort(ext)[0], 0.5, 3.0))
            o.tris(chamfer_hull(pts, bev), g); continue
        # not convex: kept as DOS drew it (a hull per material fills the gaps between its pieces)
        kept += 1
        for i in g:
            o.tris([d.P[i]], [i])
    images, mats = [], {}
    for k in sorted(set(d.K)):
        if k.startswith("palette"):
            mats[k] = {"colour": tuple(d.colour[k]), "metallic": 0.6 if k in metal else 0.0, "roughness": 0.6}
        else:
            mats[k] = record_mat(k, images, metallic=0.5 if k in metal else 0.0, normal_scale=0.6, glow=k in glow,
                                 tame=k in tame)
            if k in ghost:
                mats[k].update(colour=(1.0, 1.0, 1.0, 0.35), alpha="BLEND", double_sided=True)
    mats = {k: v for k, v in mats.items() if k in o.m.parts}
    if groups is not None:
        return o, mats, images, f"{len(comps)} clusters, {kept} kept as DOS, {rounded} rounded"
    t = o.write(name, mats, images)
    sidecar(name, f"approach A: DOS parts chamfered / panels as slabs{comment}; DOS records hi-res + relief", frames)
    return t, f"{len(comps)} parts, {kept} kept as DOS, {rounded} rounded"


# ---------------------------------------------------------------- robots
# A robot segment of one frame is a prop placed on its pivot: models/NAME.glb + sidecar ("frames": "all").
# An animated root (a walker's 84 frames) is models/NAME.robot.glb, the editor's robot template with its mesh
# swapped: DOS moves rigid pieces between frames, so each new piece is built once on frame 0 and follows
# its DOS piece as morph targets (one file, one set of textures; the template's clips play them).
#
#   DOS frame k --> rigid clusters (Kabsch per frame) --> R_k, t_k per cluster
#   new piece (frame 0) --> target k = R_k v + t_k - v      (normals: R_k n - n)
RIGID_TOL = 0.35   # DOS units a cluster may stray from rigid over all frames


def kabsch(A, B):
    """Rigid (R, t) taking points A onto B, and the largest miss."""
    ca, cb = A.mean(0), B.mean(0)
    U, _, Vt = np.linalg.svd((A - ca).T @ (B - cb))
    D = np.diag([1, 1, np.sign(np.linalg.det(Vt.T @ U.T))])
    R = Vt.T @ D @ U.T; t = cb - R @ ca
    return R, t, float(np.abs((A - ca) @ R.T + cb - B).max())


def robot_clusters(d):
    """The DOS triangles grouped into pieces that move rigidly over every frame (a leg's thigh and shin
    apart): [(triangles, tag)] and per tag its (R, t) per frame; tag "dos" is a triangle DOS bends."""
    F = d.F; nf = len(F)
    if nf == 1:
        comps = components(d)
        return [(g, k) for k, g in enumerate(comps)], {k: [(np.eye(3), np.zeros(3))] for k in range(len(comps))}
    keys = [{tuple(np.round(p, 1)) for p in d.P[i]} for i in range(len(d.K))]

    def misfit(tris):
        return max(kabsch(F[0][tris].reshape(-1, 3), F[k][tris].reshape(-1, 3))[2] for k in range(1, nf))
    groups, motion = [], {}
    for comp in components(d):
        left = set(comp.tolist())
        area = {i: np.linalg.norm(np.cross(d.P[i][1] - d.P[i][0], d.P[i][2] - d.P[i][0])) for i in left}
        while left:
            seed = max(left, key=area.get); S = [seed]; left.discard(seed); vs = set(keys[seed])
            if misfit(S) > RIGID_TOL:
                groups.append((np.array(S), "dos")); continue
            grown = True
            while grown:
                grown = False
                for i in sorted(left, key=lambda i: -area[i]):
                    if keys[i] & vs and misfit(S + [i]) <= RIGID_TOL:
                        S.append(i); left.discard(i); vs |= keys[i]; grown = True
            tag = len(motion)
            A = F[0][S].reshape(-1, 3)
            motion[tag] = [kabsch(A, F[k][S].reshape(-1, 3))[:2] for k in range(nf)]
            groups.append((np.array(S), tag))
    return groups, motion


def robot_glb(name, o, mats, images, motion):
    """models/NAME.robot.glb: the new mesh, skinned to the template's root joint, frames as morph targets,
    the template's joints and clips (dosglb/NAME.robot.glb, SkyNetEdit --export-gltf NAME)."""
    d = o.dos; nf = len(d.F)
    parts = [{"name": n, **pt, "material": mats[n]} for n, pt in o.m.parts.items()]
    data = with_emission(modkit.glb(parts, images), [pt["material"] for pt in parts])
    jl = struct.unpack_from("<I", data, 12)[0]
    doc = json.loads(data[20:20 + jl]); bl = struct.unpack_from("<I", data, 20 + jl)[0]
    bin_ = bytearray(data[28 + jl: 28 + jl + bl])
    tdoc, tb = glb_load(CFG["dos"] / f"{name}.robot.glb")

    def put(arr, kind, comp=5126, bounds=False):
        while len(bin_) % 4:
            bin_.append(0)
        raw = np.ascontiguousarray(arr).tobytes()
        doc["bufferViews"].append({"buffer": 0, "byteOffset": len(bin_), "byteLength": len(raw)})
        bin_.extend(raw)
        acc = {"bufferView": len(doc["bufferViews"]) - 1, "componentType": comp, "count": len(arr), "type": kind}
        if bounds:
            acc["min"] = arr.min(0).tolist(); acc["max"] = arr.max(0).tolist()
        doc["accessors"].append(acc)
        return len(doc["accessors"]) - 1

    def frame_of(P, N, tags, k):
        """Positions and normals of the new mesh at DOS frame k."""
        P2, N2 = P.copy(), N.copy()
        for v, tg in enumerate(tags):
            if isinstance(tg, tuple):   # ("dos", triangle, corner): DOS's own corner, its face normal
                tri = d.F[k][tg[1]]; P2[v] = tri[tg[2]]
                n = np.cross(tri[1] - tri[0], tri[2] - tri[0]); N2[v] = n / max(np.linalg.norm(n), 1e-9)
            elif tg is not None:
                R, t = motion[tg][k]; P2[v] = R @ P[v] + t; N2[v] = R @ N[v]
        return P2, N2

    for prim, (pname, pt) in zip(doc["meshes"][0]["primitives"], o.m.parts.items()):
        P = np.array(pt["positions"], float); N = np.array(pt["normals"], float); tags = o.m.tags[pname]
        prim["attributes"]["JOINTS_0"] = put(np.zeros((len(P), 4), np.uint16), "VEC4", 5123)
        prim["attributes"]["WEIGHTS_0"] = put(np.tile(np.array([1, 0, 0, 0], np.float32), (len(P), 1)), "VEC4")
        prim["targets"] = []
        for k in range(1, nf):
            Pk, Nk = frame_of(P, N, tags, k)
            prim["targets"].append({"POSITION": put((Pk - P).astype(np.float32), "VEC3", bounds=True),
                                    "NORMAL": put((Nk - N).astype(np.float32), "VEC3")})
    doc["meshes"][0]["extras"] = {"targetNames": [f"frame {k}" for k in range(1, nf)]}
    doc["meshes"][0]["weights"] = [0.0] * (nf - 1)

    # the template's joints, skin and clips, their accessors copied over
    copied = {}

    def copy(i):
        if i not in copied:
            a = dict(tdoc["accessors"][i]); v = tdoc["bufferViews"][a.pop("bufferView")]
            n = _NC[a["type"]] * np.dtype(_CT[a["componentType"]]).itemsize * a["count"]
            start = v.get("byteOffset", 0) + a.pop("byteOffset", 0)
            idx = put(np.frombuffer(tb, np.uint8, count=n, offset=start), a["type"], a["componentType"])
            doc["accessors"][idx] = {**doc["accessors"][idx], **a}
            copied[i] = idx
        return copied[i]
    doc["nodes"] = [dict(nd) for nd in tdoc["nodes"]]
    for nd in doc["nodes"]:
        if "mesh" in nd:
            nd["mesh"] = 0
    doc["scenes"] = tdoc["scenes"]; doc["scene"] = tdoc.get("scene", 0)
    doc["skins"] = [{**sk, "inverseBindMatrices": copy(sk["inverseBindMatrices"])} for sk in tdoc["skins"]]
    doc["animations"] = [{"name": an["name"], "channels": an["channels"],
                          "samplers": [{**sm, "input": copy(sm["input"]), "output": copy(sm["output"])}
                                       for sm in an["samplers"]]}
                         for an in tdoc.get("animations", [])]
    while len(bin_) % 4:
        bin_.append(0)
    doc["buffers"] = [{"byteLength": len(bin_)}]
    js = json.dumps(doc, separators=(",", ":")).encode(); js += b" " * (-len(js) % 4)
    (CFG["models"] / f"{name}.robot.glb").write_bytes(
        struct.pack("<III", 0x46546C67, 2, 12 + 8 + len(js) + 8 + len(bin_)) + struct.pack("<II", len(js), 0x4E4F534A)
        + js + struct.pack("<II", len(bin_), 0x004E4942) + bytes(bin_))
    return sum(len(pt["indices"]) // 3 for pt in o.m.parts.values())


def globe():
    """GLOBE: DOS's octagonal band between two eight-sided cones, as a round body: the band a smooth
    cylinder with its lit window, the cones rounded into domes (between cone and ellipse, so the
    silhouette stays DOS's). The domes' records are painted facet highlights; on a round dome the
    light shades it, so they become the record's median colour as matt paint."""
    d = Dos("GLOBE"); o = Out(d)
    band = d.pick({"TEXTURE.426 #000"}); caps = d.pick({"TEXTURE.426 #001"})
    bp = d.P[band].reshape(-1, 3)
    R = float(np.linalg.norm(bp[:, [0, 2]], axis=1).max()) * (1 + math.cos(math.pi / 8)) / 2
    y0, y1 = bp[:, 1].min(), bp[:, 1].max(); ylo, yhi = d.lo[1], d.hi[1]
    tris, norms = lathe(np.zeros(3), (0, 1, 0), (1, 0, 0), [(R, y0), (R, y1)], 32)
    o.tris(tris, band, norms)

    def dome(ya, yb, sign):   # from the band's rim (ya) to the pole (yb): r = R (1 - s^p)^(1/p)
        pw = 1.6
        prof = [(R * (1 - f ** pw) ** (1 / pw), ya + (yb - ya) * f) for f in np.linspace(0, 1, 10)]
        return lathe(np.zeros(3), (0, 1, 0), (1, 0, 0), prof, 32)
    for ya, yb in ((y1, yhi), (y0, ylo)):
        tris, norms = dome(ya, yb, 1)
        o.tris(tris, caps, norms, part="dome")
    images = []
    cap = np.asarray(Image.open(CFG["hires"] / "TEXTURE.426" / "001.png").convert("RGB")).reshape(-1, 3)
    med = np.median(cap[cap.max(1) > 40], axis=0) / 255
    mats = {"TEXTURE.426 #000": record_mat("TEXTURE.426 #000", images, metallic=0.0, normal_scale=0.6, glow=True),
            "dome": {"colour": tuple((np.clip(med * 1.2, 0, 1) ** 2.2).tolist()) + (1.0,), "metallic": 0.0, "roughness": 0.7}}
    t = o.write("GLOBE", mats, images)
    sidecar("GLOBE", "robot: DOS octagonal globe as a round band with domes; band record hi-res + glowing window", "all")
    return t


def robot(name, **kw):
    """A robot segment model. One frame: generic, drawn for every frame ("frames": "all"). More frames:
    generic per rigid cluster, written as NAME.robot.glb (an animated root; its children stay DOS's)."""
    d = Dos(name)
    if len(d.F) == 1:
        return generic(name, frames="all", **kw)
    groups, motion = robot_clusters(d)
    o, mats, images, note = generic(name, groups=groups, **kw)
    return robot_glb(name, o, mats, images, motion), f"{len(d.F)} frames as morph targets; {note}"


JOBS = {
    "DMPSTR0": dumpster, "TRCKBOX": trailer, "TRCKTNK": tanker,
    "RAIL": lambda: guardrail("RAIL", "TEXTURE.390 #012", "TEXTURE.430 #007", "palette 059"),
    "RAIL01J": lambda: guardrail("RAIL01J", "TEXTURE.390 #001", "TEXTURE.420 #000"),
    "IBEM256": lambda: ibeam("IBEM256"), "IBEM128": lambda: ibeam("IBEM128"),
    "IBEM64": lambda: ibeam("IBEM64"), "IBEM32": lambda: ibeam("IBEM32"),
    "LEVHAN01": lever,
    "GENER0": lambda: generic("GENER0", glow={"TEXTURE.423 #000", "TEXTURE.423 #001", "TEXTURE.423 #002"},
                                keep={"TEXTURE.474 #001"}),
    "MALBOXS": lambda: generic("MALBOXS"),
    "210TOWER": lambda: generic("210TOWER"),
    "RLYTOWER": lambda: generic("RLYTOWER"),
    "BUTTON01": lambda: generic("BUTTON01", glow={"TEXTURE.222 #000", "TEXTURE.222 #001"}),
    "BUTTON02": lambda: generic("BUTTON02", glow={"TEXTURE.222 #002", "TEXTURE.222 #003"}),
    "COMPTR9": lambda: generic("COMPTR9", glow={"TEXTURE.224 #000", "TEXTURE.224 #004"}),
    "COMPTR6S": lambda: generic("COMPTR6S", glow={"TEXTURE.454 #001", "TEXTURE.454 #003", "TEXTURE.454 #009"}),
    "BILBRD2S": lambda: generic("BILBRD2S", round_keys={"TEXTURE.390 #001"}, comment=", round pole"),
    "LPAD01": lambda: generic("LPAD01", glow={"TEXTURE.370 #000"}),
    "210FNCE0": lambda: generic("210FNCE0", tame={"TEXTURE.370 #004"}),
}


# The campaign's other box, panel and post props (the eight missions' maps, most-used first), through
# `generic`. Lit ones (buttons, screens, lamps) take their glow records from the art (glow_auto).
CAMPAIGN = """
    BIGBOX1 DOCKSUP1 PUMP2S PUMP1S CRATE01J PICTUR01 COMPTR0 CRATE03J CHAIR6S BIGBOX2 IBEM32 PUMP0S CRATB03
    DOCKSUP0 280GNLEG WIPEBORD 232CRAT2 282EXBAR CRATB02 232KYPD1 CABINT3S 281VLITE CRATC03 IBEM64 232BUTT1
    232BUTTO BUTTON03 SHELFS8 CABINT5S PICTUR02 210RBLK2 210RBLK1 232ELBUT 248GRDR BUTTON04 BIGBOX4 TABLE1S
    CABINT4S PICTUR05 232MS2 NOIRCHAR CRATE04J BILBRD0S DESK2S 285GNLEG BUTTONX SWLEV01 GASSIGN2 PICTUR06
    280LIGHT SHELFS6 CDRACK 232CRAT3 CHAIR1S CRATE02J PICTUR03 281GNLEG NOIRP04 PICTUR04 COUNTER2 24CNTAIN
    CRATC02 CABINT2S NOIRP01 CYBSIGN2 DESK0S FRIDGE DMPSTR1 DESK1S BED1S CHAIR1BS CRATD03 GASSIGN1 COFTABLE
    232CRAT1 24CHART 24TRHCH2 254PILL MONITOR PRESPAD1 BUTTON07 COMPTR3 CHAIR1CS BASELIP 232UNI NOIRP08
    TABLE2S SHELFS15 232MAINS O9CR00 CWSIGN COUNTER7 BTNB CABINT1S COUNTER1
""".split()
CAMPAIGN_LIT = set("""
    COMPTR0 232KYPD1 281VLITE 232BUTT1 232BUTTO BUTTON03 BUTTON04 232MS2 BUTTONX SWLEV01 GASSIGN2 280LIGHT
    CDRACK CYBSIGN2 GASSIGN1 MONITOR BUTTON07 COMPTR3 232MAINS BTNB
""".split())
JOBS["RAIL00J"] = lambda: guardrail("RAIL00J", "TEXTURE.390 #012", "TEXTURE.430 #007", "palette 059")
for _n in CAMPAIGN:
    JOBS.setdefault(_n, lambda n=_n: generic(n, glow="auto" if n in CAMPAIGN_LIT else ()))

# The campaign's robots, most-seen first (segment models; animated roots as NAME.robot.glb).
ROBOTS = """
    ENDORFL RAPTOR HVYPLSM GLOBE SMLTRRT SMLPLSM T600RFL HVYTRRT3 GUNTWR1 SCOUT ENDOSKEL HVYBRRL HVYTRRT SPIDBOT
    MSSLPODR HK_FTR SMLCANON HVYTNK FLENCER TRIBRRL MSSLPODL MANTNK HVRTNK HVYTRRT2 HMSLPODR
""".split()
JOBS["GLOBE"] = globe
for _n in ROBOTS:
    JOBS.setdefault(_n, lambda n=_n: robot(n, glow="auto", rods=True))


def main():
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("names", nargs="*", help="models to build (default: all)")
    p.add_argument("--dos", help="folder of the DOS models as .glb (SkyNetEdit --export-gltf)")
    p.add_argument("--hires", help="hi-res textures folder: TEXTURE.NNN/RRR.png, RRR_n.png, RRR_r.png")
    p.add_argument("--mod", help="mod folder to write models/ into")
    p.add_argument("--list", action="store_true", help="list the models and exit")
    a = p.parse_args()
    if a.list:
        print(" ".join(JOBS)); return
    if not (a.dos and a.hires and a.mod):
        p.error("--dos, --hires and --mod are required")
    unknown = [n for n in a.names if n not in JOBS]
    if unknown:
        p.error(f"unknown model(s): {' '.join(unknown)} (known: {' '.join(JOBS)})")
    CFG.update(dos=Path(a.dos), hires=Path(a.hires), models=Path(a.mod) / "models")
    CFG["models"].mkdir(parents=True, exist_ok=True)
    for n in a.names or JOBS:
        t0 = time.time()
        r = JOBS[n]()
        tris, note = r if isinstance(r, tuple) else (r, "")
        print(f"{n}: {tris} tris ({time.time() - t0:.1f} s) {note}", flush=True)


if __name__ == "__main__":
    sys.exit(main())
