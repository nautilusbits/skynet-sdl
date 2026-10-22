# Weapons and ammo

Two static tables drive every gun: the **weapon record table** and the
**ammo type table**. Thrown items (records 14-19) are covered in
[throwables.md](throwables.md), vehicle guns in [vehicles.md](vehicles.md).

## Addresses

| Table | v1.01 | v1.00 / older notes | Stride |
|---|---|---|---|
| Weapon records | **0x43714** | 0x4361c | 0x60, 26 records |
| Ammo types | **0x40828** | 0x40728 | 0x32 |
| Ammo pools `{cur, initial, max}` | **0x440ec** | 0x43ff4 | 12, 13 pools |
| Campaign start list | **0x4356c** | 0x43470 | i32 list, −1 ends |
| Primary defaults per mode | UNCERTAIN (v1.00 0x44372/0x4437e/0x4438a) | | |
| Secondary default per mode | **0x4449a** = {15, 25, 22} | 0x44396 | |
| Weapon cycle (`;` / `'`) | UNCERTAIN (v1.00 0x435e0 = records 0..12) | | |
| Number-key groups | UNCERTAIN (v1.00 0x43570) | | |
| Sound id → file name | 0x4ff00 (v1.00), 126 × 20 B | | |

Data pointers in these tables are stored relative to 0x30000 (see
[../pitfalls.md](../pitfalls.md#addresses-and-the-executable)).

## Weapon record (stride 0x60)

| Offset | Meaning |
|---|---|
| +0x00 | viewmodel screen X |
| +0x14 | ammo type id |
| +0x1c | launch speed << 16 as a local (0, v, 0) velocity: 392 for the grenade launcher and every throwable, 0 for rockets and lasers |
| +0x24 | **rate**: shots per second. Cooldown starts at `0x10000 / rate` and drops by [0x43100] (dt) every frame |
| +0x28 | runtime cooldown |
| +0x2c | recoil-kick velocity (not an animation rate) |
| +0x30/+0x34/+0x38 | muzzle offset from the eye: x right, y down, z ahead (DOS adds 20 to z, FUN_00125caf) |
| +0x48 | fire sound id (−1 = from the ammo type's +0x1c) |
| +0x4c | ammo pool index |
| +0x50 | rounds per shot |
| +0x54 | select sound id (9 ballistic, 17 energy) |
| +0x58 | dry-fire sound id (10 / 16) |
| +0x5c | flags: bit 0 owned, bit 1 aim at the point under the crosshair (else straight along the view), bit 2 vehicle-only |

`FUN_00125207` loads `WEAPON%02d.CFA` per record: the CFA index equals the
record index.

## Records

| Rec | Name | Rate /s | Pool | Cost | Notes |
|---|---|---|---|---|---|
| 0 | PIPE | 2 | — | — | melee, ammo type 17: 50 damage, reach 120, impact sound id 0 (PIPEHIT1.RAW) |
| 1 | UZI | 5 | 0 | 1 | |
| 2 | ASSAULT RIFLE | 4 | 0 | 3 | |
| 3 | MACHINE GUN | 8 | 0 | 4 | |
| 4 | SHOTGUN | 1 | 1 | 1 | 50 direct + 50 blast |
| 5 | GRENADE LAUNCHER | 1 | 2 | 1 | gravity family |
| 6 | ROCKET LAUNCHER | 1 | 3 | 1 | ammo type 5, follow-camera flag ([rocket-eye.md](rocket-eye.md)) |
| 7 | LASER RIFLE | 6 | 4 | 2 | |
| 8 | LASER CANNON | 6 | 4 | 5 | |
| 9 | PLASMA PISTOL | 3 | 4 | 1 | |
| 10 | PLASMA RIFLE | 3 | 4 | 2 | |
| 11 | PLASMA CANNON | 3 | 4 | 10 | |
| 12 | "super uzi" | 20 | 12 | 1 | STRINGS.PRS names it MINI ROCKET; the `superuzi` cheat (0x141fc8) owns and selects record 12; 50 direct + 50 blast |
| 13 | motion detector | — | — | — | multiplayer only; a plot on the scanner's screen (FUN_0011ff00); a machine avatar loses it (0x134a39). 0x132b00 is the machine vision, not the scanner |
| 14-19 | thrown items | 1 | 5-9 | 1 | [throwables.md](throwables.md) |
| 20-23 | jeep plasma / rockets | 8 / 1 | 10 / 11 | 100 / 1 | odd records = other barrel |
| 24, 25 | HK laser / rockets | 8 / 1 | 10 / 11 | 100 / 1 | |

Rates, pools and costs are as the Godot port carries them (`_weapons` in
`fly_camera.gd`), read from the table.

## Ammo pools (initial / max)

| Pool | Content | Start | Max |
|---|---|---|---|
| 0 | bullets | 500 | 750 |
| 1 | shells | 50 | 200 |
| 2 | grenades | 0 | 99 |
| 3 | rockets | 0 | 99 |
| 4 | energy | 500 | 800 |
| 5 | pipe bombs | 25 | 99 |
| 6 | molotovs | 20 | 99 |
| 7 | (unused thrown) | 0 | 99 |
| 8 | canisters | 2 | 99 |
| 9 | satchel | 1 | 99 |
| 10 | vehicle energy | 1000 | 2000 |
| 11 | vehicle rockets | 40 | 50 |
| 12 | super uzi | 9999 | 9999 |

**Between missions** the game grants ammo (FUN_00126605, list per mission
at **0x443c4** SkyNET / 0x4423c Future Shock; each entry NULL or
`{pool, amount}` pairs ending at pool −1, capped at the pool max). Not on a
restart or a new game. SkyNET grants: missions 3, 4, 8 = bullets +200,
energy +200, molotov +20, pipe +25, canister +2, satchel +1; missions 6, 7
= vehicle rockets +40.

## Start weapons

Campaign list 0x4356c = {0, 1, 2, 4, 7, 14, 15, 16, 18, 19, 20, 21, 22,
24, 25}: PIPE, UZI, ASSAULT RIFLE, SHOTGUN, LASER RIFLE plus the thrown and
vehicle records. Future Shock mode uses 0x43538 (pipe only + 14-16,
18-25). The `arnold` cheat list holds records 0..11 (not 12).

## Ammo type (stride 0x32, v1.01 base 0x40828)

| Offset | Meaning |
|---|---|
| +0x00 | callback selecting the family: 0 = instant (hitscan), 0x000f3414 = gravity projectile, 0x000f344d = straight bolt with a .3D model (this one is a **draw** callback, +0x30000 → 0x12344d) |
| +0x04 | model name pointer ("rocket.3d", "laser1.3d", "laser2.3d", "laser3.3d") or sprite index (bank << 7 \| record) |
| +0x08 | impact effect sprite index (>> 7 = bank: 363 rockets, 364 laser/plasma, 365 bullets, 356 grenades, 366 some thrown) |
| +0x0c | damage (negative = blast) |
| +0x10 | explosion **sprite size**, not a radius |
| +0x18 | lifetime in ticks (≈35 Hz: 120 = 3.4 s rockets, 40 = 1.1 s bolts) |
| +0x1c | fire sound id |
| +0x20 | impact sound id |
| +0x28 | u16 flags: bit 0 bounce, bit 3 (0x08) follow camera, bit 4 (0x10) homing (FUN_0011f8f0) |
| +0x2a | number of pieces spawned at impact (rocket 16, grenade 15) |
| +0x2c | sprite animation (0 = one still frame) |
| +0x2e | type of those pieces (rocket 3, grenade 1) |

Models ROCKET.3D and LASER1-3.3D live in MDMDENMS.BSA. DOS lays no trail
behind a flying shot.

## Firing

- The throw key and the fire key have separate cooldowns.
- Empty pool: dry-fire sound, no shot.
- Bullet family: instant hit along the view (or to the crosshair point
  with flag bit 1), impact puff from the ammo's effect bank.
- Damage to enemies is multiplied by the difficulty factor
  ([damage-hud.md](damage-hud.md#difficulty)); a hit on a prop goes
  through ObjHit ([action-handlers.md](action-handlers.md#destructibles)).

## Godot port

`fly_camera.gd`: `_weapons` (per-slot table), `MUZZLE`, `POOL_TABLE`,
`START_WEAPONS`, `FIRE_RATE_SCALE` 1.0, `MELEE_RANGE` 120,
`_projectile_cfg`, `MISSION_GRANT_*` in `main.gd`. `projectile.gd`: visible
projectiles (`setup(from, dir, damage, cfg, shooter)`), `tracer.gd`,
`muzzle_flash.gd` (TEXTURE.219, 17 × 17 px at 2 units/pixel),
`explosion.gd`.

## Deviations in the Godot port

- **Projectile speeds are not in the table** and were tuned by eye:
  rocket 3000, laser 9000, plasma 4000 u/s (DOS rocket ≈1200, lasers ≈1800
  per one measurement; UNCERTAIN). Candidate routine FUN_00126154.
- Impact pieces (+0x2a/+0x2e) are not spawned; the port shows only the
  effect bank.
- `POOL_TABLE` holds the DOS start values (grenades and rockets 0); an
  older comment beside it still speaks of seeding a few.
- The HUD prints weapon names in some modes where DOS shows icons.

## Open questions

- Where the bolt and rocket flight speed comes from.
- Homing flag 0x10: which ammo types use it in the shipped table.
- Primary default selections per mode in v1.01.
