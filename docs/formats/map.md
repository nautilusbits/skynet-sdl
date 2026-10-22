# MAP.NNN entity files

A MAP file holds one level's objects: meshes, lights, sprites, markers and
the links that wire switches to doors. Terrain is separate
([wld.md](wld.md)). SkyNET maps live in `MDMDMAP2.BSA`, Future Shock maps
in `MDMDMAPS.BSA` (which also holds two text files, `MAP.JTE` and
`MAP.TXT`, that are not levels: accept only numeric suffixes).

Godot port: `scripts/loaders/map_file.gd`; consumers `level_loader.gd`,
`map_dump.gd` (CLI dump tool), `triggers/rules_skynet.gd`.

DOS: map-to-scene walker (v1.00 `FUN_00136519`), marker scan
(v1.00 `FUN_0011c519`), enemy placement `0x12a439` (v1.01).

## Header

| Offset | Size | Type | Meaning |
|---|---|---|---|
| 0x0000 | 4 | u32 | cell count (UNCERTAIN, not used by the port) |
| 0x0004 | 4 | u32 | grid width (cells along X) |
| 0x0008 | 4 | u32 | grid height (cells along Z) |
| 0x000C | 4 | u32 | offset of the cell grid, always 0x253C (used as a signature) |
| 0x0010 | 4 | u32 | 0xFFFFFFFF (signature) |
| 0x0014 | 8·n | char[8][] | mesh name table, **no count, not NUL-padded** (below) |
| 0x233C | 4 | i32 | head of the per-name defaults list (file offset, ≤ 0 = empty) |
| 0x2344 | 4 | u32 | 1 = outdoor (load WLD, draw sky), else interior (usually 2) |
| 0x253C | 4·W·H | u32[] | cell grid: per cell, file offset of its first entity (0, 0xFFFFFFFE or 0xFFFFFFFF = empty) |

Grid cells are 1024 world units; cell = `x >> 10`, `z >> 10`. Typical
grids are 64 × 64 (outdoor) or 4 × 4 (interiors). Entity coordinates are
absolute, so the grid is only a lookup structure.

Grids are **not always square**: MAP.606, 609, 616 and 696 are 64 × 65.
**+4 is the width (X) and +8 the height (Z)**: for all 27,833 entities of
the 122 SkyNET maps, the cell whose chain holds an entity is exactly
(`x >> 10`, `z >> 10`) with the cell index `z_cell · width + x_cell`.

### Name table

8-byte slots from 0x14, indexed by a variant-1 entity's name index.

- There is **no count**. The word at +12 is the grid offset, not a count.
- Short names are **not NUL-padded**: bytes after the NUL are leftovers
  (e.g. a slot reading `PC`, NUL, then five stale letters).
- The data after the last slot can look like text.

The only reliable rule: walk all entities first, then read exactly
`max(name_index) + 1` slots. Cut each name at the first NUL.

A slot is a name only when its first byte is a letter or digit and every
byte before the NUL is a name character; otherwise the slot reads as ""
(empty, not the end of the table). This is the rule both the Godot port
and `SkyNet.Formats.MapFile` apply; it keeps MAP.231's `PC` slot and its
stale tail from cutting the table short. Names map to
`NAME.3D` in `MDMDOBJS.BSA` (props) or `MDMDENMS.BSA` (enemies).

### Per-name defaults list (0x233C)

Linked list of 13-byte nodes (DOS "Map_SetDefault", v1.00):

| Offset | Size | Type | Meaning |
|---|---|---|---|
| +0 | 4 | i32 | next node offset (≤ 0 ends) |
| +4 | 2 | i16 | name index |
| +6 | 4 | i32 | link record offset |
| +10 | 2 | u16 | hit points |
| +12 | 1 | u8 | state byte |

A variant-1 entity that has no link record of its own (sub+0x13 < 1) and
is not an actor (flags & 0x40 clear) takes hp, state byte and link record
from the node for its name. This is how crates, cars and buttons get their
hit points, drops and actions. An editor must not write the resolved
values back into the entity.

## Entity block

Entities form a singly linked list per cell. The block length depends on
the variant: **48 / 35 / 36 bytes** for variants 1 / 2 / 3.

Common head (25 bytes):

| Offset | Size | Type | Meaning |
|---|---|---|---|
| +0 | 4 | u32 | next entity in this cell (0, 0xFFFFFFFE, 0xFFFFFFFF = end) |
| +4 | 4 | u32 | unused / back pointer |
| +8 | 4 | i32 | X |
| +12 | 4 | i32 | Y (DOS Y-down: negative = up) |
| +16 | 4 | i32 | Z |
| +20 | 1 | u8 | flags |
| +21 | 4 | u32 | offset of the sub-record; `entity + 25` for every entity of every shipped SkyNET map |

Flags:

| Bits | Meaning |
|---|---|
| 0-1 | variant: 1 mesh, 2 light, 3 sprite or marker |
| 0x08 | skip the entity entirely |
| 0x40 | actor (variant 1) |
| 0x80 | seen: set at run time when the view drew the object, read by the automap ([automap.md](../game-logic/automap.md)) |
| 0x04, 0x10, 0x20 | not used by the variant switch; UNCERTAIN |

No shipped SkyNET map sets any of 0x08, 0x04, 0x10, 0x20 or 0x80 (checked
on all 27,833 entities), so their meaning cannot be learnt from the data;
only 0x40 and the variant bits occur.

Coordinates are used verbatim: no terrain snap, no bounding-box offset.

The last block of a file is often a 36-byte variant-3 record, and it may
be the head of its cell's chain. A reader that requires 48 bytes for every
block drops whole cells.

### Variant 1: mesh (sub-record 23 bytes)

| Sub offset | Size | Type | Meaning |
|---|---|---|---|
| +0x00 | 4 | i32 | pitch (rotation about X), 11-bit angle |
| +0x04 | 4 | i32 | yaw (about Y) |
| +0x08 | 4 | i32 | roll (about Z) |
| +0x0C | 2 | u16 | name index |
| +0x0E | 2 | u16 | hit points |
| +0x10 | 2 | | UNCERTAIN |
| +0x12 | 1 | u8 | state byte |
| +0x13 | 4 | i32 | link record offset (< 1 = none, use defaults) |

Angles: `a & 0x7FF`, 2048 = 360°. In DOS axes the engine builds
`R = Rz(roll) · Rx(pitch) · Ry(−yaw)`, column vectors, Ry applied first
(v1.00 `FUN_0014e100`, sine/cosine lookup tables indexed
`(a & 0x7FF) * 4`; all nine matrix entries transcribed). **The yaw term is
negated** in the raw matrix. Every variant-1 entity is rotated; there is
no flag gate.

Y-up conversion: conjugating by C = diag(1, −1, −1) (the vertex and
position flip) gives `C·R·C⁻¹ = Rz(−roll) · Rx(+pitch) · Ry(+yaw)`. That is
what the Godot port builds (`level_loader.gd`: `Basis().rotated(UP, yaw)`
then `.rotated(RIGHT, pitch)` then `.rotated(BACK, −roll)`). In Godot Euler
terms it is order **ZXY** with (pitch, +yaw, −roll); the default `rotation`
property (order YXZ) is not the same matrix when more than one angle is
non-zero.

**Link record** (10 bytes, at the link offset):

| Offset | Size | Type | Meaning |
|---|---|---|---|
| +0 | 1 | u8 | destruction type (index into the table at 0x424d6 in v1.01, 0x423d6 in v1.00: effect sprites, drop, sound) |
| +1 | 2 | i16 | blast parameter for destruction type 0 |
| +3 | 2 | | UNCERTAIN |
| +5 | 4 | i32 | next entity in the action chain (≤ 0 ends) |
| +9 | 1 | u8 | action type (index into the handler table, see game-logic) |

### Variant 2: light (sub-record 10 bytes)

| Sub offset | Size | Type | Meaning |
|---|---|---|---|
| +0 | 2 | u16 | intensity |
| +2 | 1 | u8 | state byte |
| +3 | 1 | u8 | action type |
| +4 | 4 | i32 | next entity in the chain |
| +8 | 2 | i16 | enable gate: the light is added only when > 0 (seen 40..540; may also act as a radius, UNCERTAIN) |

The engine adds a point light at the entity position (v1.00
`FUN_0011b733`). Variant 2 is not a spawner.

### Variant 3: sprite or marker (sub-record 11 bytes)

| Sub offset | Size | Type | Meaning |
|---|---|---|---|
| +0 | 2 | u16 | sprite index: bank = `>> 7` (TEXTURE.bank), record = `& 0x7F` |
| +2 | 2 | u16 | per-use value (exit map, marker parameter) |
| +4 | 1 | u8 | per-use value (exit spawn set, act on Future Shock robots) |
| +5 | 1 | u8 | state byte |
| +6 | 4 | i32 | next entity in the chain |
| +10 | 1 | u8 | action type (sprites) / enemy type (enemy markers) |

**Marker:** when `sprite_index >> 7 == 299` the entity is a placement
marker, not a sprite. `marker_type = sprite_index & 0x7F`.

| Marker type | Meaning |
|---|---|
| 0..99 | player spawn sets: position N, facing N+1 (0 = map start) |
| 2 | enemy start: type = sub+10 & 0x7F; bit 7 of sub+10 = convoy vehicle |
| 3 | altitude ceiling for the HK (the first one's Y) |
| 4 | radiation source, strength at sub+2 |
| 6 | map type, sub+2 = music track index |
| 7 | compass north offset (degrees) |
| 8 | background radiation below its Y (unused in shipped maps) |
| 30..39 | player border boxes, read in pairs |
| 100 | multiplayer spawn |
| 101 / 102 | multiplayer jeep / HK |
| 103 / 104 | water level plane |
| 5, 9 | never read |

Note the overlap: types 2..8 are also inside the 0..99 spawn-set range;
the consumers decide which reading applies.

Enemy markers: a non-zero sub+2 makes the enemy a dormant trap that
explodes when the player comes within that distance (see the AIS notes).
On Future Shock maps, robot markers also carry an action at sub+4 and a
state at sub+5; state 0x04 fires the action when the robot dies (v1.01
0x12a439). SkyNET maps have none.

**Interior exit:** a non-marker variant-3 entity with action 0xF0 is a
teleport. sub+2 = target map number (0 = return to the previous map),
sub+4 = spawn set in the target map (facing = set + 1). Teleport handler
0x137881 (version UNCERTAIN).

Variant-3 entities with type byte 0xF3 are dynamic enemy-spawn flats
(v1.00 `FUN_00129000`; v1.01 0x129500).

## Action chains

Every variant has a state byte, a next pointer and an action type. When an
object is hit or used, the engine walks the chain from it, toggling bit 0
of each state byte, and runs each object's handler (DOS "ObjFlipLink"
0x139caa and "ObjDoAction" 0x139e98, v1.01). State bits 1 and 2
(`& 6`) mark an object as switchable/damageable: bit 1 fires on every hit,
bit 2 when hit points run out. Handlers are documented under
[game-logic](../game-logic/README.md).

## Open questions

- Header word 0, and the bytes between the name table and 0x233C, and
  between 0x2348 and 0x253C.
- Variant-1 sub+0x10 and link record +3.
- Meaning of flag bits 0x04 / 0x10 / 0x20 (unused by shipped SkyNET maps).
- Enemy marker sub+2: orientation (one note) or dormant-trap distance
  (another note). Possibly both, depending on the enemy state.
