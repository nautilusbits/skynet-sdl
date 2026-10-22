# AIS enemy data (in SKYNET.EXE)

The enemy AI is not a data file: the enemy type table, the parameter
blocks, the AIS scripts, the animation blocks and the ammo table are
static data inside SKYNET.EXE. Behaviour, state ids and what the fields do
are in [game-logic/enemies-ai.md](../game-logic/enemies-ai.md); this page
is the byte layout.

Godot port: `tools/gen_enemy_ai.py` (run on a v1.00 executable) generated
`scripts/enemy_ai_data.gd`. C#: `SkyNet.Formats.AisData` (with
`LeExecutable`) parses it, it does not run the VM; `tools/SkyNet.Tables`
runs that parser once and ships the result as `data/tables/enemy_types.json`,
`ais_ops.json`, `ais_anims.json` and `ammo_types.json`, which the game loads
(`SkyNet.Data.GameTables.Ais`); see [exe-tables.md](exe-tables.md).

## Reading the executable

See [pitfalls.md](../pitfalls.md#addresses-and-the-executable): VA to file
offset `+0x53AA4` for v1.01 (the LE data pages are relative to the inner
MZ at 0x352A4); data pointers are stored relative to 0x30000.

| Item | v1.00 | v1.01 |
|---|---|---|
| Enemy type table | 0x44D00 | **0x44E00** |
| Ammo table | 0x40728 | **0x40828** |
| AIS op and predicate handlers | 0x12A572.. | **+0x500** (0x12AA72..) |
| Ammo callbacks (families 1, 2) | 0xF3414, 0xF344D | 0xF3914, 0xF394D |

The data moved by +0x100 here, not the +0x300 seen elsewhere: the shift
is piecewise, so anchor each table by its contents. SKYNET.EXE v1.01 is
identified by the string `SkyNet v1.01`.

## Enemy type table

Records of 7 u32 (0x1C bytes), ending at the first record whose +0x00 is
0 (171 records in v1.01):

| Offset | Meaning |
|---|---|
| +0x00 | → resource record; the .3D name (lower case, `name.3d`) is at record+4 |
| +0x04 | UNCERTAIN |
| +0x08 | AI state id |
| +0x0C | → child-segment list, or 0 |
| +0x10 | death spec: 0/1 none, else → `{i32 kind, i32 arg}` |
| +0x14 | → parameter block (36 i32), or 0 |
| +0x18 | UNCERTAIN |

**Child segments**: 32-byte entries `{continue, child_type, ox, oy, oz,
pitch, yaw, roll}`; the entry with continue = 0 is the last one (it is
still a segment). 46 types have segments in v1.01, all equal to the Godot
port's `ENEMY_SEGMENTS`.

**Death spec**: kind 0 = one debris type `arg` at the origin; kind 1 =
`arg` → list of 32-byte `{continue, type, ox, oy, oz, ...}`, last entry
has continue = 0 (the port reads at most 12).

## Parameter block

36 i32. dw0 = hit points (the port clamps to 0..0x7FFF). The other
indices depend on the state:

| State | speed | near | turn | other | fire (mx, my, mz, ammo, speed, rate, range, aim gate) | script | events | engine |
|---|---|---|---|---|---|---|---|---|
| 7 walker | 4 | 6 | 7 | | 11 12 13 14 15 18 19 16 | 26 | 29 | |
| 6 hover | 4 | 6 | 7 | | 12 13 14 15 16 19 20 17 | 21 | | |
| 2 turret | | | 7 | axis 4, min 5, max 6, range 8 | 12 13 14 15 16 19 20 17 | | | |
| 8 head | | | 7 | axis 4, min 5, max 6, range 8 | 12 13 14 15 16 19 20 17 | 24 | 27 | |
| 9 flyer | 4 | | 5 | alt 6, vaccel 7, avoid 8, fspeed 9 | 17 18 19 20 21 24 25 22 | 14 | | 26-27 |
| 13 tank | 6 | | 5 | | | 12 | | 22-23 |
| 10 machine | | | | | | 6 | | |

- A type fires only when rate > 0 and range > 0.
- Script, events: a pointer (raw value 0x10000..0x40000, + 0x30000).
- Events: pairs `{i32 frame, i32 sound}` until frame = −1 (at most 40).
- Engine: two dwords `{type, sound}`; a loop sound only when type = 5.

## AIS ops

16 bytes `{fn, a, b, c}` (fn is a stored pointer). Handlers (v1.00
addresses; v1.01 adds 0x500):

| fn | Op | Operands |
|---|---|---|
| 0x12A572 / 586 / 59A / 5AE / 5C1 | IF ≤ / < / > / == 0 / ≠ 0 | a predicate, b value, c → target |
| 0x12A5D4 | GOTO | a → target |
| 0x12A5D9 | WAIT | a ticks (1/70 s), c ≠ 0 freezes the animation |
| 0x12A60C | ANIM | a → animation block |
| 0x12A640 | SOUND | a sound id |
| 0x12A668 | SET | var[a] = b |
| 0x12A682 | SETFN | var[a] = predicate b |
| 0x12A69E | ADD | var[a] += b |

Predicates (stored pointers): 0x12A6B8 see, 0x12A6E9 distance, 0x12A6FD
bearing, 0x12A75A |bearing|, 0x12A7AB random, 0x12A789 angle to player,
0x12A7C4 animation flags, 0x12A7D0 actor flags. The next op is at +16.
Any other fn ends the walk. v1.01 holds 364 reachable ops and 64
animation blocks; no IF or SETFN uses an unknown predicate.

## Animation block

`{u32 type, u32 count, u32 fps, u16 flags, (u16 frame, u16 frame_flags)…}`
from +14; the list ends at the entry with frame_flags & 1 (count is not
used). Flags: 4 one-shot, 8 loop, 0x100 firing pose. Only type 1 blocks
have an fps.

## Ammo table

25 records, stride 0x32: +0x00 callback (0 hitscan, family 1 gravity,
family 2 straight bolt), +0x04 → model record (name at +4) when a pointer,
+0x08 impact texture type (bank = >> 7), +0x0C damage (negative = blast),
+0x10 blast radius, +0x18 life ticks, +0x1C fire sound, +0x20 impact
sound. See [game-logic/weapons-ammo.md](../game-logic/weapons-ammo.md).

## Dormant traps

Not in the table: a type-2 marker with sub+2 ≠ 0 becomes a dormant trap
(state 12) that explodes within that distance, effect bank TEXTURE.367,
sound 0x26 (`AisData.TrapEffectBank`, `TrapSound`).

## Open questions

- Record +0x04 and +0x18.
- The per-state layouts for states 0, 1, 11 and 12 (no fields read).
