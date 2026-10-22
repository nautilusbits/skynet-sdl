# Throwables (second weapon slot)

DOS keeps **two selected-weapon registers**: `WeaponSelectPri`
(0x1254c6 → [0x4433a], v1.00 addresses) for the fire key and
`WeaponSelectScn` (0x125585 → [0x4434a]; v1.01 FUN_00125a85) for the
THROW key. Both are saved in a save game.

## Records 14-19

| Rec | Item | Pool | Ammo type | Damage = blast S | Sprite (bank/rec) | Impact bank | Impact sound | Bounce |
|---|---|---|---|---|---|---|---|---|
| 14 | PIPE BOMB | 5 | 12 | 200 | 217/4 | 366 | 34 | yes |
| 15 | MOLOTOV | 6 | 13 | 125 | 217/1 | 366 | 34 | no |
| 16 | GRENADE | 2 | 3 | 200 | 217/2 | 356 | 33 | no |
| 17 | (unused) | 7 | — | — | — | — | — | — |
| 18 | CANISTER BOMB | 8 | 14 | 800 | 217/0 | 356 | 36 | yes |
| 19 | SATCHEL | 9 | 20 | 15000 | 200/4 | 356 | 36 | no |

Ammo type rows are in the v1.01 table at 0x40828. Fire sound (+0x1c) is −1
for all five; record 17 is never owned.

## Rules

- **Selection** is on F1-F5 (key poller FUN_0011ad8a, block at 0x11b0f5):
  F1 MOLOTOV (15), F2 PIPE BOMB (14), F3 GRENADE (16), F4 CANISTER (18),
  F5 SATCHEL (19). Gated on the owned bit only (an empty item can be
  selected), 20-tick debounce. Not rebindable; CONTROLS.DAT's 17 rows bind
  the THROW act (row 7, default right Shift), not the choice.
- **No weapon keys at all in a vehicle.** In a vehicle the secondary is the
  rocket pod (default per mode at 0x4449a = {15, 25, 22} foot/jeep/HK).
- **Start**: all five owned, MOLOTOV selected; pipe 25, molotov 20,
  grenade 0, canister 2, satchel 1.
- Pickups never switch the secondary.
- The throw key (FUN_001263cc) throws only the selected item and **fails
  silently** when empty (dry sound −1). Rate 1/s on its own cooldown,
  separate from the gun's.
- **Launch**: 500 u/s ahead plus 392 u/s up (weapon +0x1c); the satchel is
  dropped at 50 u/s ahead.
- **No fuse**: they go off on the first thing they touch.
- **Bounce** (ammo flag +0x28 bit 0, routine 0x1233ea): velocity mirrored
  in the surface and scaled by 0xAAAB/0x10000 = 2/3 (FUN_0011c19a →
  FUN_0011c100); detonates instead when the bounced speed is under 50 u/s,
  or at most 100 u/s on a floor (normal up ≥ 252/256). Each bounce plays
  sound 0x18.
- **No fire pool** from the molotov in DOS.
- Blast: [damage-hud.md](damage-hud.md#radial-blast), S = the damage
  value; DOS also deals that value as a direct hit.

## Godot port

`fly_camera.gd`: `THROWABLES` (rows above), `THROW_KEYS`,
`THROW_DEFAULT`, `THROW_RATE`, `HAND_GRENADE_SPEED` 500, `THROW_LIFT` 392,
`THROW_MUZZLE`, `_throw_secondary()`, `cycle_throwable()`. `grenade.gd`
flies, bounces (`BOUNCE_KEEP`, `BOUNCE_DEAD_SPEED`, `BOUNCE_REST_SPEED`,
`BOUNCE_FLOOR_UP`, `BOUNCE_SOUND`) and explodes.

## Deviations in the Godot port

- The port also cycles the secondary on `0` and the middle mouse button
  (DOS has only F1-F5).
- Thrown-item gravity and the impact pieces are not ported; the
  GRENADE LAUNCHER shell flies at 3400 u/s under 1400 u/s² with a 2.5 s
  fuse (`grenade.gd` `SPEED`, `GRAVITY`, `FUSE_TIME`), a playtest-tuned
  look, not DOS (DOS: launch 392 per weapon +0x1c, world gravity 392).
- An earlier port version left a burning pool after a molotov; it was
  removed (no DOS basis).

## Open questions

- Exact gravity the gravity-family callback (0x000f3414) applies.
- Record 17: whether any data path ever grants it.
