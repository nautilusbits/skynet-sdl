# Vehicles: jeep and HK

The player drives the jeep on missions 2 and 6 and flies the HK on
mission 7. The mode comes from the mission table (+0x08: 4 jeep, 8 HK) and
applies **only to the mission's start map** (Future Shock confirmed the
rule: an interior of a jeep mission is on foot).

## Tables

| Item | Address | Notes |
|---|---|---|
| Mover table | 0x38e53 (v1.01) | jeep tick 0x134eec / post 0x135495 / init 0x134e00; HK 0x131100 / 0x1316e0 / 0x131aea |
| Jeep data | ds:0x23700 (v1.01) | read by 0x134eec |
| HUD (cockpit) table | 0x44700 | per mode `{handle, x, y, z, name}`: jeep {0, 26, 22, 8, "hummer.3d"}, HK {0, 0, 4, 16, "hkcockpt.3d"} |
| Aim table | 0x443ca | MDMAIM.IMG on foot, CROSHAIR.IMG in both vehicles |
| Secondary default | 0x4449a | {15, 25, 22}: jeep throw key = rockets |
| Weapons | records 20/21 jeep plasma (ammo 18, pool 10, cost 100, rate 8), 22/23 jeep rockets (ammo 5, pool 11), 24 HK laser (ammo 22), 25 HK rockets | odd records = other barrel |

## Jeep

| Quantity | Value |
|---|---|
| Max speed | 683.59 u/s |
| Throttle | 1148.4 ÷ max(1, v / 21.449) u/s² |
| Brake | 459.4 u/s², reverse to −140 u/s |
| Drag (coasting only) | 153.1 u/s² |
| Slope | adds 1148 × sin(slope) |
| Turn rate (0x135271) | none below 21.45 u/s; else wheel × 721.58 / v rad/s, clamp ±1.0738 (350/2048 per s); reversed in reverse |
| Wheel | lock to lock in 0.305 s |
| Hull | 1000 points |

- **The camera is the turret**: the mouse turns the view, the keys drive,
  the crosshair stays centred. The turret yaw (DAT_00069911) is relative to
  the car and has **no clamp**: it turns the full 360°. The pitch is clamped
  to ±0x100 of 2048 = ±45° (0x135b04). The guns fire along the view; the
  cockpit stays with the car, so a turret turned to the rear looks out past it.
- The cockpit is the vehicle's own .3D model drawn at the camera with the
  vehicle's rotation (FUN_00126415 loads, FUN_00133d26 adds (x,y,z) << 8 to
  every vertex, FUN_0012645c draws). DOS y down / z ahead: the jeep eye sits
  in the **left seat**, offset (26, −22, −8) in Godot axes.
- Nothing hurts the soldier inside; the hull takes all damage
  ([damage-hud.md](damage-hud.md)).
- No weapon keys in a vehicle.

## Ramming (jeep only)

DOS v1.01 0x135a7e, run once per move from the jeep's post-move 0x135588:
the car's position and heading are saved before the move; after it, the
first target the hull box (HUMMER.3D, 57 × 30 × 118 half size, 50 over the
wheels) newly touches gets ObjHit with ⌊v/2⌋ points (the speed field
>> 9; none when backing, none below 2 u/s), no difficulty factor, no damage
to the driver or the hull. Destroyed: the car drives on at 7/8 speed.
Survived: the saved state comes back, the speed is negated and the wheel
centred. Sound 30 either way. The MAP.260 car-wash door (CWDOOR, 60 points)
falls to one ram above 120 u/s. The HK does not ram. MAP.248's girder
(IBEM64) ramming 248WALL is a behaviour chain, not the player's vehicle.

## Map border

FUN_00122711 builds up to 10 boxes from PAIRS of markers of one type 30-39
(record order); FUN_00122789 undoes any move of the player (on foot or in a
vehicle) that ends outside every box and zeroes the speed. Within 64 units
of an edge hint slot 8 ([G9]) prints once. MAP.260 has one box (x 14208..
64378, z −27880..−4898), the town and the inner highway lane.

## HK

| Quantity | Value |
|---|---|
| Forward / back | 800 / 400 u/s, accel 900 u/s² |
| Strafe | 400 u/s (550 with the modifier), accel 700 |
| Climb | 200 u/s, accel 700, no gravity |
| Keyboard yaw | 90 °/s flat |
| Pitch clamp | ±45° |
| Floor | none: no minimum altitude over the terrain |
| Ceiling | the first **marker type 3**'s Y ([0x390df]) |
| Hull | 1500 points |

The move (0x131be0) clamps the eye at the ceiling, then tests the hull. On a
hit (0x13ac3e → 0x13950a) the eye is set 0x50 = 80 units over the hit's top
and the move stands if that is still under the ceiling; otherwise the move is
undone and the speed set to −¼. That is collision, not an altitude band:
under the ceiling the HK flies as low as the ground lets it.

On entry (0x131fea) the eye rises 400 units, no snap to ground. Cockpit
HKCOCKPT.3D at offset (0, −4, −16), riding the camera.

## Path-driven vehicles

Trucks, convoys, the boss and the HK that lifts the player off MAP.234 are
enemies in AI state 11, not player vehicles; see
[enemies-ai.md](enemies-ai.md#path-vehicles-state-11).

## Multiplayer

Markers 101 / 102 place parked jeeps / HKs; the use key enters and leaves.
NETLEVEL.PRS lists them per arena. SKYNET.EXE v1.01: placement 0x11c200 /
0x130b00 (the first min(count, markers) of each, heading +0x80 a vehicle),
boarding within 0x78 (0x130c80), leaving 0x130e36 (parked for good, the driver
out at +20 / 40 up), damage only to the driven one (0x13114a, hull 0x1311a5),
a wreck never returns. See [../multiplayer.md](../multiplayer.md) step 9.

## Godot port

`fly_camera.gd`: `vehicle`, `set_vehicle`, `_drive` (`JEEP_*`), `_hover`
(`HK_*`, `hk_ceiling`, `_hk_ceiling_clamp`), `JEEP_AIM_PITCH` (no yaw limit),
`VEH_COCKPIT`, `VEH_COCKPIT_OFFSET`, `VEH_EYE` {75, 92, 110},
`VEH_CAPSULE`, `_ram_check` (ramming), engine sounds `ENGINE_SOUND_ID`
{69 jeep, 48 HK}, `CAR_START_ID` 123. `main.gd`: `_vehicle_for_map`,
`HK_ENTRY_RISE` 400. Multiplayer: `scripts/net/dm_vehicle.gd`.

## Deviations in the Godot port

- Jeep gravity 4500 (`JEEP_GRAVITY`) and arrow-key car turning 2.4 rad/s
  are the port's hand tuning; DOS jeep gravity was never read.
- Jeep plasma is drawn as a bolt with a halo; DOS shows a thin red streak.
- A rammed target is hit at most once per 0.3 s (`RAM_REARM`); DOS has no wait.
- Vehicle energy recharges (port addition, see damage-hud.md).

## Open questions

- DOS jeep gravity and suspension.
- Flag that picks hull 1000 vs 1500 per vehicle kind.
