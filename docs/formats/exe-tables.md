# Tables in SKYNET.EXE (shipped as data/tables)

Much of SkyNET's game data is not in a data file. It is static data inside the
DOS program SKYNET.EXE: the weapons, the ammo types, the enemy types with their
AIS scripts, what a destroyed object drops, the location names, and more.

The port does **not** read the player's SKYNET.EXE at run time. The tool
`tools/SkyNet.Tables` extracts the tables once into `data/tables/*.json`. These
files are kept in the repository and embedded in `SkyNet.Data`, and the game
loads them from there (`SkyNet.Data.GameTables`). The game needs only the data
folder (the BSA archives and loose files).

## Regenerating

```
dotnet run --project tools/SkyNet.Tables -- <path-to-SKYNET.EXE> [--out <dir>] [--check]
```

- The input must be SKYNET.EXE **v1.01**: 1 839 291 bytes, MD5
  `5d1d3d82471cb515db091b4a9045479d`. Any other file is refused. This includes
  the 2017 launcher (about 100 KB) that some installs call SKYNET.EXE while the
  game image is named GAME.EXE. v1.00 is not supported. The AIS parser
  (`AisData.Parse`) still reads v1.00, but the location-name, piece and
  statistics tables have v1.01 addresses only.
- Without `--out`, the tool writes to `data/tables` of the repository that holds
  the working folder (it searches upward for `SkyNet.slnx`).
- `--check` writes nothing. It prints `same` or `DIFFERS` for each file and
  exits 1 when any file differs.
- The output is deterministic: LF line ends, a final newline, and rows in table
  or VA order. Regenerating with an unchanged reader gives byte-identical files.
- Bump `Extractor.ToolVersion` when the shape of a file changes.

Tests: `SkyNet.Formats.Tests.ExeTablesTests` checks that every embedded table
loads and writes back to the same text, and that a mod file replaces its table.
When SKYNET.EXE v1.01 is in `SKYNET_DATA` or in its parent folder (the DOS
install), the tests also re-extract the tables and compare them byte for byte
with `data/tables` and the embedded copy.

## Mods

`GameTables.Mods` is the file system of the mods named by `--mod a,b`
(folders or ZIPs, mounted by `ModContent`). A file `tables/<table>.json` in
them replaces the embedded table of the same name; per table the highest mod
in the load order that has it wins. Tables no mod has stay the shipped ones.
A mod file has the same shape as the shipped file; copy that file and edit its
rows ([../modding.md](../modding.md#tables)).

One table feeds the baked map blocks: the models and child segments of
`enemy_types.json` (the robots' rest pose). The system ZIP is always baked from
the shipped tables; a mod whose `enemy_types.json` changes those fields makes
the game build each map with robots from the raw files at entry instead of
reading its block ([map-block.md](map-block.md#tables-and-mods)).

## File shape

Every file is one JSON object. It has a header and then `rows`:

```
{
  "table": "weapons",
  "address": "0x43714",
  "source": "SKYNET.EXE v1.01",
  "sourceMd5": "5d1d3d82471cb515db091b4a9045479d",
  "tool": "SkyNet.Tables 1",
  "rows": [
    {"index": 0, "viewX": <int>, "ammo": <int>, ... one row per line ...},
    ...
  ]
}
```

`address` is the v1.01 VA of the table, or two VAs separated by a space. File
offset = VA + 0x53AA4. Pointers stored in the data are relative to the object,
so the VA = raw + 0x30000 (see [pitfalls.md](../pitfalls.md#addresses-and-the-executable)).
Property names are camelCase. When a row fits in 260 columns it is written on
one line. An array of plain values is always written on one line.

## The tables

| File | v1.01 address | Layout in the executable | `rows` | Game side |
|---|---|---|---|---|
| `enemy_types.json` | 0x44E00 | 7-dword records (0x1C bytes) up to the first one with +0x00 = 0, each with its 36-dword parameter block, death spec and child segments; see [ais.md](ais.md) | `[EnemyType]`: `index`, `record[7]`, `name`, `modelFile`, `state`, `params[36]`, `hitPoints`, per-state fields (`speed`, `near`, `turn`, `axis`, `angleMin`, `angleMax`, `range`, `altitude`, `verticalAccel`, `avoidRadius`, `forwardSpeed`; null when the state's layout lacks them), `fire` {`muzzleX/Y/Z`, `ammo`, `speed`, `rate`, `range`, `aimGate`} or null, `scriptVa`, `events[{frame, sound}]`, `engineSound`, `death[{type, x, y, z}]`, `deathKind` (the spec's kind word: 0 one part with no explosion of its own, 1 a list plus the explosion; null for the bare spec 0/1), `segments[{childType, x, y, z, pitch, yaw, roll}]` | `GameTables.Ais.Types` |
| `ais_ops.json` | the scripts that `enemy_types` reaches | 16-byte ops `{fn, a, b, c}`; see [ais.md](ais.md) | `[AisOp]` by VA: `va`, `kind` (If, Goto, Wait, Anim, Sound, Set, SetFn, Add, End), `handler`, `a`, `b`, `c`, `predicate`, `predicateHandler`, `compare` (Le, Lt, Gt, Eq0, Ne0, None), `target`, `animVa` | `GameTables.Ais.Ops` |
| `ais_anims.json` | the blocks that `ais_ops` starts | `{u32 type, u32 n, u32 fps, u16 flags, (u16 frame, u16 flags)…}` | `[AisAnimBlock]` by VA: `va`, `type`, `count`, `rawFps`, `flags`, `frames[{frame, flags}]` | `GameTables.Ais.Anims` |
| `ammo_types.json` | 0x40828 | 25 × 0x32 bytes: callback +0x00, look +0x04, effect +0x08, damage +0x0C, size +0x10, life +0x18, fire sound +0x1C, impact sound +0x20, reach +0x24, u16 flags +0x28, u16 pieces +0x2A, u16 piece type +0x2E | `index`, `callback`, `family` ("hitscan", "gravity" = callback 0xF3914, "bolt" = 0xF394D), `model` (the .3D when `look` points at a model handle, else ""), `look` (raw; the sprite when it is not a pointer), `effect` (raw sprite index, bank << 7), `damage`, `size`, `life`, `fireSound`, `impactSound`, `reach`, `flags`, `pieces`, `pieceType` | `WeaponTable.Ammo`, `AisData.Ammo` |
| `weapons.json` | 0x43714 | 26 × 0x60 bytes: view X +0x00, ammo +0x14, speed +0x18, lift +0x1C, rate +0x24, muzzle +0x30/+0x34/+0x38, fire sound +0x48, pool +0x4C, cost +0x50, select sound +0x54, dry sound +0x58, flags +0x5C | `index`, `viewX`, `ammo`, `speed`, `lift`, `rate`, `muzzleX`, `muzzleY`, `muzzleZ`, `fireSound`, `pool`, `cost`, `selectSound`, `drySound`, `flags` | `WeaponTable.Weapons` (names come from the port's list, not from the table) |
| `ammo_pools.json` | 0x440EC | 13 × `{i32 current, initial, max}` | `index`, `current`, `initial`, `max` | `WeaponTable.Pools` |
| `start_weapons.json` | 0x4356C, 0x43538 | dword lists ended by −1 (SkyNET; Future Shock mode) | `{skyNet: [..], futureShock: [..]}` | `WeaponTable.For(game).StartList` |
| `impact_pieces.json` | 0x3844B | 24-byte rows `{type, sprite, life, speed, spread, flags}` ended by type −1 | `type`, `sprite`, `life` (70 Hz ticks), `speed`, `spread` (16.16), `flags` | `ImpactPieces.Types` |
| `destruction.json` | 0x424D6 | 140 × 14 bytes `{u16 effect[4], i16 spread, i16 drop list, i16 sound}` | `index`, `effects[4]`, `spread`, `dropList` (−1 none), `sound` | `DestructionTable.DropList` (only the drop list is used so far) |
| `drop_lists.json` | 0x42C7E | 10 pointers to `{i32 count, u16 sprite[count]}` | `index`, `sprites[]` (0 = nothing) | `DestructionTable.Drops` |
| `location_names.json` | 0x53D00, 0x35D00 | rows of 0x26 bytes `{i16 map, char name[36]}` ended by map ≤ 0; the prefix string | `{prefix, rows: [{map, name}]}` | `LocationNames` ("YOU ENTERED …") |
| `mission_enemies.json` | 0x53520, 0x53400 | 16-byte statistics rows `{shots, hits, enemies, kills}`; only `enemies` (+8) is static | `{skyNet: [8], futureShock: [17]}` | `MissionStats.EnemiesSkyNet` / `EnemiesFutureShock` |

## Not extracted (constants in code)

These tables come from SKYNET.EXE too, but they are compile-time constants in
the C# source. They are checked against the decompile, not read by the tool.
Moving them into `data/tables` would need a reader for each:

| Table | v1.01 address | C# |
|---|---|---|
| Mission table (start map, mode) | 0x34846, Future Shock 0x341CE | `MissionTable` |
| Item table (pickups) | 0x35800 (v1.00) | `PickupTable` |
| HUD cockpit table `{handle, x, y, z}` | 0x44700 | `VehicleController.CockpitWorld`, `CockpitModel` |
| Difficulty multipliers | 0x5C210 | `Difficulty` (the Godot port's numbers) |
| 640 × 480 font swap | 0x4D000 | `HudFont.HiResFont` |
| Status-line queue limits | 0x51600 | `StatusQueue` |
