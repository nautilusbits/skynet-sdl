# Hi-res textures from the DOS art

How to make an HD texture mod automatically from the game's own TEXTURE.NNN
banks, how the game loads it, and how eight upscaling methods compare on
SkyNET's art. No upscaled art is shipped or committed: the DOS textures are
copyrighted, so each player builds the mod from their own data.

Tool: `tools/upscale/upscale.py` (Python) plus the `textures` mode of
`tools/SkyNet.Tables` (C#). Game side: `TextureStore.HdImage`,
`Terrain.HdAtlas`, `Renderer.ReplaceTexture`, `PngImage`.

## The mod layout

```
mods/<name>/textures/TEXTURE.NNN/RRR.png
```

- `NNN` = bank, `RRR` = record, both zero-padded to three digits
  ([texture.md](formats/texture.md)).
- Straight (not premultiplied) RGBA PNG, any whole multiple of the record's
  size (the tool writes 4x). An animated record has its frames stacked
  downwards, each centred in the largest frame's box, as the game uploads it.
- Alpha 0 where the record has index 0. A sprite shows those texels as holes;
  an opaque surface (a model face) ignores alpha and shows the stored RGB, so
  the tool writes the haze colour (palette index 31) there, what DOS draws for
  index 0 through HAZE.000.
- Turned on under OPTIONS → MODS (saved in the settings), or for one run with
  `--mod <name>` (a folder or a ZIP in `mods/`; several mods: `--mod a,b`,
  later wins). See [modding.md](modding.md). Under the ENHANCED look with no
  texture mod on, the game says once that the textures can be made
  ([the notice](modding.md#the-hi-res-textures-notice)); it never makes them
  unasked.
- OPTIONS → DETAIL loads them at full size (HIGH), half (MED) or a quarter
  (LOW) of each side ([modding.md](modding.md#texture-size-detail)).
- HD textures only change the **ENHANCED** look. The original look keeps the
  palette indices, and so do the depth and shadow passes. A cut-out's shape
  therefore stays the low-res index mask in every look.
- The ground's TEXTURE.302 tiles are used only when every tile has an HD file
  at one scale: the game builds a 4x atlas from them (first frame of an
  animated tile). A file of the wrong size is skipped with a warning in
  `logs/skynet.log` (`[Textures]`).

### Relief maps

```
mods/<name>/textures/TEXTURE.NNN/RRR_n.png   tangent-space normal (RGB)
mods/<name>/textures/TEXTURE.NNN/RRR_r.png   roughness (grey, 0-255 = 0-1)
```

Both generators write them for every record but sprites
(`SkyNet.Upscale.ReliefMaps`, `relief_maps` in upscale.py, the same
constants; pixels within 1 of each other), from the upscaled colour, each
frame on its own and wrapped:

- **Normal**: luminance blurred (σ 0.6 source texel, so the old art's dither
  does not turn into noise), Sobel, slope × 6 source texels per unit of
  luminance; x along the image's columns (+u), y down its rows (+v), z out,
  stored as 128 + 127 n (flat = 128, 128, 255).
- **Roughness**: local mean, contrast and saturation (σ 1.5 texels): 0.85 +
  1.5 × contrast, minus up to 0.45 for a grey, fairly bright, even texel (a
  metal panel), clamped to 0.3-1.

In the ENHANCED look a lit face (walls, models, the ground's atlas) whose HD
texture has both files at its size reads them as one more layer of its colour
texture (`Renderer.ReplaceTexture`'s `relief`; single-frame records only):
the normal through the face's own tangent frame (from the screen derivatives
of position and uv), its tilt scaled by ENHANCED OPTIONS → SURFACE RELIEF
(`LookLighting.ReliefOf`: OFF, LOW 1.0x, MEDIUM 1.6x, HIGH 2.5x; default LOW),
the roughness varying
the texture's material's (modding.md, [Materials](modding.md#materials-enhanced-look); matte, the
default, has no highlight at all) about its value, and the
ambient tilted by the mapped normal against a fill from above and the viewer
(an interior's even ambient would otherwise show no relief). ENHANCED OPTIONS
→ SURFACE RELIEF OFF turns it off; the console's `relief [off|low|medium|high]`
switches it live for this run (A/B).

At LOW a map whose mean tilt is under about 5 degrees hardly shows, least
under a night's light; panels, seams and brick read at 10-20 degrees (measured
on MAP.210/211/213). A mod's maps carry the strength: `modkit.py materials
--table` sets it per record (modding.md, [Material replacement](modding.md#material-replacement)). Without the files a face draws
exactly as before; the original look never reads them.

For a mod made before the maps existed: `SkyNet --gen-relief <mod-folder>`
(hidden, from the mod's own PNGs, no upscaling, about 15 s) or
`upscale.py maps --work <work> --mods <mods> --name <mod-name>`.

Before this work the renderer could already take whole colour textures
(`Renderer.Replace`, by material), but nothing in the game loaded any: `--mod`
only replaced tables. The loader adds `Renderer.ReplaceTexture` (per index
texture, applied to whichever palette asks for it) and reads the PNGs through
SDL (`RetroEng.Platform.PngImage`).

## The tool

Python 3.12, in a venv, always run isolated (`python -I`):

```
python -m venv .venv
.venv\Scripts\python -I -m pip install torch --index-url https://download.pytorch.org/whl/cu128
.venv\Scripts\python -I -m pip install -r tools/upscale/requirements.txt
```

The GPU build of torch is needed only for the two OpenModelDB models. Install
it first, because `spandrel` would otherwise pull the CPU build from PyPI.

```
# 1. every record as a palette PNG + manifest.json (3,389 records, 15 MB)
python -I tools/upscale/upscale.py export --data <game-data> --work <work>
#    (= dotnet run --project tools/SkyNet.Tables -- textures <game-data> --out <work>/export)
# 2. a mod per method (binaries and weights are fetched into --cache, SHA-256 checked)
python -I tools/upscale/upscale.py run --work <work> --method esrgan-x4plus --mods <game>/mods --cache <cache>
#    [--name <mod-name>] (default hires-<method>), [--banks 210,302] (a subset)
# 3. contact sheets of 12 representative records, one per method
python -I tools/upscale/upscale.py sheet --mods <game>/mods --methods nearest,esrgan-x4plus --out <dir>
python -I tools/upscale/upscale.py list      # methods and licences
```

Every download goes into its own folder under the cache, which defaults to
`tools/upscale/cache/` and is git-ignored. Each file is checked against a
pinned SHA-256 before use. The Real-ESRGAN archive is the same release, with
the same hash, that `SkyNet.Upscale.UpscalerSource` pins.

What each record goes through:

1. The palette PNG is read. Texels with index 0 are holes.
2. Holes are filled with the mean of their opaque neighbours, growing inwards,
   so no upscaler sees a black border around a cut-out.
3. Each frame is padded by 8 texels: **wrapped** for wall and terrain records
   (layout A: they repeat across a face, and wrapping keeps the seams
   invisible) and **edge-copied** for sprites (layout B).
4. The frame is upscaled 4x. All frames go to the upscaler in one batch: one
   process for ncnn, size-grouped batches of about 256K input texels for torch.
5. The padding is cropped. Alpha is the index-0 mask scaled by nearest (the
   same cut-out as the index texture), and the hole RGB is set to palette
   index 31.

## In the game: GENERATE HI-RES TEXTURES

OPTIONS → MODS → GENERATE HI-RES TEXTURES makes the `esrgan-x4plus` mod
without Python: after a confirm (a download of about 50 MB, a few minutes on a
Vulkan GPU) it fetches the same pinned Real-ESRGAN release into `cache/`
beside the game (`SkyNet.Upscale.RealEsrgan`, SHA-256 checked), runs every
record through the steps above in C# (`SkyNet.Upscale.TexturePipeline`,
`SkyNet.Game.Mods.HiresTextures`) in one ncnn process, and writes
`mods/hires-esrgan-x4plus/` (the same PNGs as the Python tool, byte for byte
in pixels, plus `upscale-stats.json` and a `mod.json`). It runs in the
background with its progress on the screen's note line; Esc cancels it and
leaves the mod as it was. When it is done the mod is turned on. Without a
Vulkan GPU the upscaler fails and the screen says so (the whole error is in
`logs/skynet.log`). For a test run: `SkyNet --gen-hires <mods-dir>
[--banks 200,210]` (hidden; it does not touch the settings).

## Methods compared

All 3,389 records of SkyNET (3,939 frames: 3,265 wall/terrain records and 124
sprites) on an RTX 3060 12 GB, Ryzen with 24 threads, Windows 11.
"Upscale" is the scaler alone; "total" adds preparation and PNG writing.
"Level build" is the game's own log line for MAP.210 / MAP.252 (no mod:
301 / 128 ms), so it includes decoding the HD PNGs, which is serial.

| Method | Kind | Licence | Upscale s | Total s | Mod size | Level build ms (210 / 252) |
|---|---|---|---|---|---|---|
| nearest | Pillow | Pillow: MIT-CMU | 1.4 | 18 | 17 MB | 852 / 458 |
| lanczos | Pillow | Pillow: MIT-CMU | 4.0 | 32 | 125 MB | 1,180 / 517 |
| scale4x | Scale2x applied twice, on palette indices (own numpy code) | algorithm free; code is the tool's | 1.1 | 23 | 28 MB | 961 / 468 |
| esrgan-x4plus | Real-ESRGAN `realesrgan-x4plus`, ncnn-vulkan | BSD-3-Clause | 162 | 204 | 243 MB | 2,686 / 857 |
| esrgan-anime | Real-ESRGAN `realesrgan-x4plus-anime`, ncnn-vulkan | BSD-3-Clause | 66 | 99 | 177 MB | 2,623 / 825 |
| animevideov3 | Real-ESRGAN `realesr-animevideov3` x4, ncnn-vulkan | BSD-3-Clause | 37 | 74 | 269 MB | 2,668 / 856 |
| pixelperfect | 4x-PixelPerfectV4 (ESRGAN), PyTorch + spandrel, fp16 | WTFPL | 81 | 116 | 158 MB | 2,593 / 828 |
| fatalpixels | 4x-Fatal-Pixels (ESRGAN), PyTorch + spandrel, fp16 | CC BY-NC-SA 4.0 (non-commercial) | 81 | 119 | 236 MB | 2,651 / 859 |

xBRZ was the planned pixel-art scaler, but its Python binding (`xbrz.py`)
does not build from its source package: a header is missing. Scale4x is the
same family (xBR grew out of it) and runs on the palette indices, so it can
never invent a colour.

### What each looks like

Judged from contact sheets of the same 12 records (terrain, walls, a road
edge, control panels with lettering, a machine, three sprites with holes) and
from the in-game view at the start of MAP.210 (ground, tree, wreck, jeep) and
MAP.252 (wood panelling, a painting, a metal door, ceiling lights):

- **nearest**: the DOS texels at 4x. In the enhanced look this is *sharper*
  than no mod, because the filtered 64-pixel originals blur up close. It is
  faithful but blocky.
- **lanczos**: the same as no mod in practice, since the game already filters.
  It is pointless except as a reference.
- **scale4x**: almost identical to nearest. SkyNET's textures are dithered and
  noisy, and Scale2x only rounds runs of equal colours, which this art rarely
  has. It helps only the few flat-shaded panels and lettering.
- **esrgan-x4plus**: the most convincing. Wood grain, rock and ground read as
  material, the painting's clouds and mountains gain shape, and panel edges
  are straight. It invents fine detail and sometimes over-sharpens dither into
  speckle. It is also the slowest (about 3 minutes for the full set).
- **esrgan-anime**: cleaner and flatter than x4plus, with strong edges and
  lettering kept legible. Noisy textures such as ground and rock lose their
  grain and look painted. It runs at less than half the time of x4plus.
- **animevideov3**: the fastest AI model. It looks soft and painterly, with
  smeared fine detail.
- **pixelperfect**: made for sprite art with clean outlines. On dithered
  photo-like textures it smooths everything out, and at game distance the
  result reads blurrier than no mod.
- **fatalpixels**: keeps the pixel structure crisp. It is good on sprites and
  panels, but adds ringing on noisy textures, and its licence rules out any
  commercial use.

The two pixel-art models found on OpenModelDB (PixelPerfectV4 and
Fatal-Pixels; deviantPixelHD is CC BY-NC as well) target sprite art with
clean outlines. Almost all of SkyNET's world art is dithered, photo-like
wall and ground textures, where the general model does better.

## Recommendation

- **Default: Real-ESRGAN x4plus** (BSD-3-Clause, the ncnn binary the engine
  already pins in `SkyNet.Upscale`, no Python or CUDA needed). It has the best
  look on walls, ground, panels and pictures, takes about 3 minutes for all of
  SkyNET on a mid-range GPU, and produces a 243 MB mod.
- **Option: "crisp"**, nearest-neighbour 4x (17 MB, seconds). It keeps the DOS
  texels exactly but stops the enhanced look's blur. It is worth offering
  because it is faithful and makes no AI guesses.
- A per-bank mix (anime for panels and lettering, x4plus for the rest) is
  possible with `--banks`, but the gain is small.
- Not recommended: lanczos (no visible change), scale4x (no gain on dithered
  art), animevideov3 and PixelPerfectV4 (blurry), Fatal-Pixels (artefacts and a
  non-commercial licence).

## Open points

- **Cut-out edges stay stair-stepped** with every method: alpha is the nearest
  mask so that it matches the index texture the depth and shadow passes use.
  Smoother silhouettes (trees, fences, sprites) would need an upscaled alpha
  in the colour texture and an alpha test at 0.5 in the enhanced sprite path,
  with the depth pass taking the same mask.
- **Load time**: decoded serially, the PNGs added about 0.5-2.4 s to a level's
  build (MAP.210 with x4plus: 301 → 2,686 ms; the table above). They are now
  decoded on all cores before the level is built (`ModContent.Prefetch`) and
  kept while the same level is rebuilt; not re-measured for every method.
- **CPU memory**: the decoded RGBA of every HD texture is kept for the
  level's lifetime, about 256 KB per 64 x 64 record at 4x.
- The HUD, weapons and menus are not TEXTURE banks. They are untouched
  (the 640 x 480 set covers the HUD).
