# Water

## Where water is

- A map has water when it carries a **marker type 103** (else 104) —
  FUN_00120f85 (v1.01; FUN_00120bf9 in older notes), marker list walk
  FUN_0011c652.
- **Surface = marker.Y − 0x10** in DOS coordinates (Y down), i.e. 16 units
  **above** the marker. In Godot: `−(y − 16)`.
- DOS keeps the fine level at [0x38908] (24.8) and the integer at
  [0x10ed00].
- Five shipped maps: MAP.240 and MAP.250 (harbour), MAP.252/253/254
  (flooded submarine decks). Marker 103 → SHADE.005/HAZE.005, 104 →
  SHADE.006/HAZE.006.
- The terrain under water is ordinary rock: water is not a terrain
  material. Painted lakes (e.g. MAP.270) are terrain tiles, not water.

## The player in water

FUN_0012c15d, every frame from FUN_0012ae00, rewrites the movement
constants (decoded later than the first port; it supersedes any
swimming model):

| State | Condition | Effect |
|---|---|---|
| Wading | surface ≥ 5 units above the feet (flag 0x10000 in [0x33200]) | top speeds halved (250/400/210/310 → 125/200/105/155), accel 1500 → 375 |
| Submerged | surface at or above the eye | additionally gravity 392 → 150, jump 177 → 100, mouse look × ½ (keys unscaled) |

- **No swimming**: no upward force, no buoyancy, no speed cap; the jump
  works only standing on a floor (hops ≈33 units).
- Going under: vertical speed ÷ 16 + splash (0x12eb04 / 0x12eb0b).
- Underwater flag 0x4000000 in [0x30a50] when the level ≤ player Y; head
  under when level ≤ player Y + eye − 5.
- Fall damage unchanged.
- **Air** (FUN_0012ea6b): the breath clock [0x38c00] runs at **70 ticks/s**
  (FUN_00124c63 adds 0x460000 / period, i.e. 70 × dt) and restarts on every
  frame above water; from 0x348 ticks (**12.0 s**) under, drowning adds
  `dt × 0x5500` (24.8) = **85 points/s** through FUN_00122b52 (armour
  applies, no grunt).
- Sounds (FUN_0012ea6b / FUN_0012ec34, each only when not already playing):
  going under 0x73 splash; under, 0x76 bubbles2 at 0x97/0x400 per frame,
  3D within ±128 of the head; drowning 0x74 at 0x15f/0x400 per frame;
  coming up 0x73, plus 0x7a getair2 after 0x23 ticks (0.5 s) under.
  Ids: 115 splash, 116 drown, 117 get air, 118 bubbles, 119 pings,
  120 sub alarm, 122 get air 2, 123/124 left/right water steps.

## Moving water

Acts **0xD6-0xDA** (handler 0x121160, table 0x59e00):

| Act | Target |
|---|---|
| 0xD6 | the entity's own Y |
| 0xD7 | current target rises 170 |
| 0xD8 | current target drops 140 |
| 0xD9 / 0xDA | ±112, and swap their own act byte (toggle) |

Delta = slot+4 << 8; DOS Y grows downward, so a negative delta raises the
surface. The level glides at 16 u/s for a marker-103 map, 8 u/s for 104
(FUN_00121083, every frame). Then the act clears its own bit. MAP.254's
sewer puzzle uses all of them.

## The look

- FUN_00120f85: marker 103 → HAZE.005 row 8, span routine 0x1587a0,
  glide 0x1000; marker 104 → HAZE.006 row 3, span routine 0x15dc00, glide
  0x800. (No HAZE.006 ships loose; no shipped map is known to use 104.)
- No surface polygon. After the 3D frame, FUN_00121148 → FUN_0015859c
  post-processes the pixels **behind** the water plane (below its horizon,
  depth compared, no depth write): each pixel x is read from
  x + T[start + phase + x], then remapped through HAZE.005 row 8, in place.
  T (0x10ef18, filled at start-up) holds `clamp(rand & 63 − 32, −1, 1)`:
  −1 or +1 almost always (0 one time in 64), so nearly every pixel takes a
  neighbour's. Per row: `start` (0x111318) = rand & 127, at least 8;
  `phase` (0x110718) starts at rand & 7 − 4 and moves by its step
  (0x10fb18, ±1) every frame, the step flipping once the phase passes ±4,
  so it swings −5..+5. The span routine 0x1587e0 is unrolled per pixel and
  patched with a `ret` at the span's length.
- HAZE.005 row 8 is a light per-index remap, not a blend: 87 of the 256
  indices stay; the darks and greys go one or two steps lighter and bluer
  (e.g. 120 (47,47,52) → 205 (55,55,75); 58 (83,71,71) → 234 (79,71,91);
  wood 106 (95,67,51) → 58 (83,71,71)); the saturated ramps (yellows,
  magentas, cyans, greens, reds) step one shade darker; the brightest
  colours stay. A linear fit over the palette gives ≈ 0.9 × c + (10, 12,
  19), but that understates it on the dark harbour colours, where the
  blue gain is +12..+24 (15-40 %).
- Underwater the palette swaps to **SKYNTWTR.COL** = 0.5 × c + (35, 35,
  126). Entering/leaving plays the splash/get-air sounds; bubbles at random.

## Godot port

- `main.gd`: `_water_level` (marker − 16 corrected to the DOS sign),
  `_setup_water` (screen-texture shader on a plane, `WATER_TINT`
  (0.275, 0.275, 0.99, 0.5)), `_water_target` / `_water_speed` for the
  moving acts.
- `fly_camera.gd`: `WATER_SPEED_SCALE` 0.5, `WATER_ACCEL` 375,
  `WATER_FEET_DEPTH` 5, `SUBMERGED_GRAVITY` 150, `SUBMERGED_JUMP` 100,
  `SUBMERGED_MOUSE_SCALE` 0.5, `SUBMERGE_DAMP` 16, `AIR_SECONDS` 12,
  `DROWN_DPS` 85.
- `scripts/level/raw_action.gd` carries the 0xD6-0xDA nodes.

## Deviations in the Godot port

- The water look is a shader approximation of the DOS post-process.
- Enemies refuse to drive into water (port rule).
- Earlier versions had buoyancy, swim-up and dive; removed (no DOS basis).
  Earlier versions also placed the surface 32 units too low (sign error).

## C# port

- `Water/WaterLevel.cs`: the marker, the surface, the 0xD6-0xDA target and
  glide; kept per map in the session (not yet in the save format).
- `Water/WaterLook.cs`: the HAZE row itself (`Remap`), and both remaps
  fitted per channel to an alpha blend for the enhanced look (≈ 0.1
  towards (100, 120, 190); 0.5 towards (70, 70, 252)).
- Original look (2026-10-10): `Renderer.PlaneRemap` (palette.hlsl) sends
  the final index (after shade and depth cue) of every fragment on the far
  side of the surface from the eye through the row, from above and from
  below, sprites and robots included, skies not. The shimmer offsets the
  texel lookup by one 200-line picture pixel left or right with the same
  table / start / swinging phase (hashed, not DOS's rand sequence), 24
  steps/s (UNCERTAIN: DOS steps once per drawn frame).
- Enhanced look (2026-10-11): `Renderer.PlaneGrade` in the post pass
  (post.hlsl), after the tone curve, as DOS ran its pass on the finished
  frame: every pixel whose surface (camera depth) lies on the far side of
  the surface from the eye, from above or below, sky excepted, is read a
  picture pixel left or right with the same shimmer (shimmer.hlsli) and
  becomes 0.9 x c + (10, 12, 19) in display colours (the row's fit). The
  earlier disc blended that fit in linear light before the tone curve,
  which left the wood and grey of the sub's cabins almost unchanged
  (wood blue 51 -> 53 instead of 65) and had no shimmer: the flooded
  decks looked dry. Both looks skip the pass during the torpedo ride
  ([0x38904] = 2). The underwater tint covers the canvas
  after the HUD (SKYNTWTR.COL is exactly 0.5 × c + (35, 35, 126), so the
  blend is exact).
- `Player/PlayerController.Water.cs` + `Water/WaterBreath.cs`: wading,
  submerged, breath, drowning, sounds.
- Deviations: the shimmer moves texels within a surface, so the edge
  between two surfaces does not fray as DOS's frame re-read does; a pixel
  read from x − 1 was already remapped in DOS (in place, left to right) and
  is not here.

## Open questions

- Whether marker 104's half speed is intentional or another field.
