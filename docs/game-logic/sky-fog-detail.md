# Sky, stars, depth cue and RENDER DETAIL

Applies to outdoor maps (MAP header +0x2344 == 1, see
[../formats/map.md](../formats/map.md)).

## Dusk dome or night

- FUN_00133b67 draws **SKY_SKY.3D** (MDMDOBJS.BSA, textured from
  TEXTURE.046) only for maps in the list at **0x52d51 = {250, 260, 270,
  280}** (missions 5-8; the Godot port treats every map of those decades
  the same). FUN_00134067 sets [0x30a54] bit 0 for them.
- The dome is authored tiny (≈1000-unit radius after the 1/256 scale) and
  re-centred on the camera every frame (FUN_00133bbb), drawn first with
  fog disabled.
- **Every other outdoor map is night**: a flat sky of palette index
  **0x11** plus a **moon sprite** (TEXTURE.359 record 2, FUN_00139932) at
  the fixed bearing atan2(−6400, 4000) (DOS x, z), drawn only when the
  dome is not.
- The moon joke (FUN_00125caf): a shot with the crosshair within 15 px of
  the moon prints a short message (string at 0x44504); after enough hits
  ([0x5ba10] = 0x18800 fall velocity) it drops below the horizon for the
  rest of the map (FUN_00139a10 integrates, FUN_001399e7 resets on load).
  The hit count is 24 or 25 (UNCERTAIN; the port uses 24).

## Stars

FUN_0013a000, once at start-up, fills 256 entries at **0x5ad04** (12 bytes:
a DOS x, y, z direction) and a colour each at 0x5b904, from the game RNG
FUN_00146b8b (`x = ((x & 0xffff) × 0x31d) % 0x1003`):

- colour = clamp((r & 0xf) + 0x55, 0x50, 0x5f);
- angles a = r & 0x7ff, b = r & 0x7ff, redrawn until a < 0x400 and
  b ≥ 0x400;
- direction = (sin a cos b, sin a sin b, cos a) × 0x100000 (FUN_0013a295),
  always in the upper half of the sky.

FUN_0013a0a2 draws them every frame on the night-only gate: rotated by the
camera matrix (0x6cf00) without translation, one pixel each
(FUN_0001699a), before the moon and the world. The RNG is seeded from the
BIOS tick count (FUN_00146b76), so the sky differs per launch.

## RENDER DETAIL and depth cue

FUN_00143300 / FUN_00143314 index the 3 × 20-byte table at **0x61a00**
(v1.01; the v1.00 table at 0x61800 has other values):

| Setting | +0 far clip (<< 8) | +4 (unused) | +8 cue start | +0xc span renderer | +0x10 |
|---|---|---|---|---|---|
| LOW | 1176 | 768 | 720 | 4 | 1 (drops a pass) |
| MED | 1944 | 1792 | 1744 | 4 | 2 |
| HIGH (default) | 3992 | 4400 | 1744 | 8 | 2 |

- Far clip ([0x6e224]): every terrain vertex, polygon and sprite deeper
  than it in camera z is dropped (FUN_00153dc8 outcode 0x20, FUN_0014ef20).
- Depth cue (FUN_0014a63c): from the cue start to the far clip the shade
  falls linearly through the 64 rows of **HAZE.000** to row 0, where every
  colour is palette index **31** (black). See
  [../formats/palette.md](../formats/palette.md).
- Settings live in CONTROLS.DAT (difficulty +0x58, render detail +0x64).

C# port: DETAIL also sizes the mods' HD textures (HIGH full, MED 1/2,
LOW 1/4 each side), from the next map's load; the first start guesses
DETAIL from the GPU. See
[../modding.md](../modding.md#texture-size-detail).

## Godot port

`main.gd`: `DUSK_MISSIONS`, `NIGHT_SKY_INDEX`, `MOON_*`
(`MOON_ELEVATION_DEG` 32 is a visual fit), `STAR_COUNT` 256,
`STAR_SEED` 1, `HAZE_INDEX` 31, `_apply_fog_distances`; `settings.gd`:
`DETAIL_FAR`, `DETAIL_CUE_START`, `draw_far()`. Console command `moon`.
The dome is scaled ≈132× and pinned to the camera as opaque unshaded
geometry (no `no_depth_test`).

## Deviations in the Godot port

- Fixed star seed (identical sky every run).
- Linear fog in a modern renderer approximates the palette ramp.
- Colour grading constants `DOS_BRIGHTNESS` 1.15, `DOS_CONTRAST` 1.06,
  `DOS_SATURATION` 1.10 are tuning, not DOS values.
- Stars are drawn as points sized to one pixel of a 640 × 480 frame.

## Open questions

- Moon elevation in DOS.
- Exact moon hit count.
