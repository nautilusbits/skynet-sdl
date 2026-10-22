# Player movement

The on-foot soldier: speeds, acceleration, gravity, jump, crouch, turning,
collision body and fall damage. Vehicles are in [vehicles.md](vehicles.md),
water in [water.md](water.md).

## Frame-rate scaling

The mover table at **0x38e53** (v1.01) holds `{tick, post-move, init}` for
each player mode:

| Mode | tick | post-move | init |
|---|---|---|---|
| foot | 0x12a900 | 0x12ac8c | 0x12bb2b |
| jeep | 0x134eec | 0x135495 | 0x134e00 |
| HK | 0x131100 | 0x1316e0 | 0x131aea |

The on-foot input-to-velocity step is **0x11af95**. The global
**[0x43100]** holds `65536 / fps` (dt in 16.16 seconds, fps capped at 50).
Every `imul k, [0x43100]` therefore multiplies by a **per-second** rate:
all figures below are units/s or units/s², nothing is per tick.

## Constants

| Quantity | Value | Notes |
|---|---|---|
| Walk, forward and back | 250 u/s | table at 0x38c48 |
| Run (RUN modifier), forward only | 400 u/s | back stays 250 |
| Strafe walk / run | 210 / 310 u/s | separate caps |
| Ground acceleration = friction | 1500 u/s² | reaches walk cap in 0.17 s |
| Diagonal | not normalised | forward and strafe caps apply per axis |
| Gravity | 392 u/s² | |
| Jump launch speed | 177 u/s | apex v²/2g ≈ 40 u, hop ≈ 0.9 s; running does not change it |
| Eye height | 75 u | 0x38ce5 = 0x4B; start markers are eye positions |
| Crouch eye | 35 u, moves at 100 u/s | speed and body unchanged |
| Keyboard turn | starts 5.6 °/s, ramps at 720 °/s² to 315 °/s | |
| Keyboard look up/down | 180 °/s flat | |
| Pitch clamp | ±67.5° (HK ±45°) | |
| Mouse | ≈ 0.176 °/pixel at the default setting | measured, not read |
| Foot slope limit | normal Y ≥ 196/256 (cos ≈ 40°) | 0x12b192; steeper faces are walls |

## Collision body

- DOS collides the player as **three spheres of radius 15** (on-foot
  table 0x38df9, copied by 0x1228ae), tested against a mover's real rotated
  polygons (0x13ac3e / 0x14e111).
- The player **ignores ceilings** and re-finds the floor within **±80
  units** of the feet every move. Doorways as small as 70 × 105 units exist
  and stairs/sills up to 80 units are walked over.
- Movement routine **FUN_0012b18c** (v1.01): tries the full move, then
  slides along the most opposing face, then fallbacks, else stays. The
  floor polygon under the feet is stored at [0x4d124] (probe 0x138d00);
  see walk-on pads in [action-handlers.md](action-handlers.md).
- DOS cannot crawl: FUN_0012ae00 zeroes the speed when the eye is not at 75
  (UNCERTAIN how this interacts with crouch; the port lets crouched players
  move).

## Slopes

- **No slope slowdown.** FUN_0012b18c adds the walk speed × dt to the feet's
  x/z (0x138b5e gives the step from the yaw and the speeds 0x38d00/0x38d08),
  then asks the floor probe **0x138d00** for the highest floor under the new
  spot and puts the feet on it (0x12b734). Nothing scales the horizontal
  speed by the gradient; a ramp is climbed at 250 / 400 u/s across, and the
  climb rate is that times tan(slope).
- **Terrain and meshes alike.** 0x138d00 builds the WLD ground at the spot
  as one more polygon (0x14575d, kept at 0x5a818) beside the mesh faces of
  the 4 nearby cells (0x138ebf) and takes the highest. The ground has no
  rule of its own.
- **Limits** (poly normal Y at +0x18, DOS Y down, so floors are negative;
  walkable = past −196, i.e. ≥ 196/256 ≈ 40°), while the body is not rising:
  a floor **up to 25 above** the feet is stepped onto only when walkable,
  else (or when higher than 25) the move is refused and the slide fallbacks
  run, most opposing face first; a floor **up to 25 below** is snapped to
  whatever its slope, but a steep one allows no jump and sets 0x2000, and
  **FUN_0012bed0** pushes the body down it (2·392·n per second); a floor
  more than 25 below is a fall.
- The port (2026-10-10) keeps the horizontal motion on any walkable face
  and lifts it onto the plane; it used to project the motion and the
  velocity onto the plane, which cost cos² of the slope every tick
  (a 25° slope: 139 u/s, the 37–38° flights of MAP.210's 210TOWER ramp:
  ≈ 65 u/s). The Godot port climbs at 250 along the surface
  (`floor_constant_speed`), 227 across on 25°.

## Fall damage

`damage = (v − 396) × 1113 / 256` points when the landing speed `v`
exceeds 396 u/s (FUN_00122d76). With gravity 392 that is a drop of about
200 units: nothing at 195, ≈1 point at 200, ≈41 points at 300. Points are on
the 1000-point scale of [damage-hud.md](damage-hud.md). Not scaled by
difficulty.

## Godot port

`scripts/fly_camera.gd`: `walk_speed` 250, `run_speed` 400,
`STRAFE_SPEED`/`STRAFE_RUN_SPEED`, `GROUND_ACCEL`, `gravity` 392,
`jump_speed` 177, `CROUCH_EYE`/`CROUCH_RATE`, `KEY_TURN_*`,
`KEY_LOOK_RATE`, `PITCH_MAX`, `FOOT_MAX_SLOPE_DEG` 40, `STEP_HEIGHT` 80
(with step rises 80/48/24/12), `FALL_SAFE_SPEED` 396,
`FALL_POINTS_PER_UNIT` 1113/256. Body: `VEH_CAPSULE[0]` = radius 11 +
4 safe margin (= the DOS 15), height 76.

## Deviations in the Godot port

- A capsule plus step-up, not DOS's spheres + floor re-find. Close in
  practice, but it is the source of most "sealed passage" bugs; see
  [../pitfalls.md](../pitfalls.md#collision).
- Proximity triggers add a 26-unit pad (`RulesSkynet.PLAYER_RADIUS`) to
  DOS radii because the capsule cannot stand where the DOS player did.
- Mouse rate and invert are player settings (`Settings.mouse_*`).
- Bots in deathmatch use the same numbers.

## Open questions

- Exact DOS step-up: the floor re-find is ±80 but no separate step limit
  was read. UNCERTAIN.
- Whether crouch-walking is possible in DOS (see the eye ≠ 75 note above).
