# Mission file

A small, readable description of one mission: which map blocks are placed
as which **instances**, the doorways between them, where the mission
starts, and the game's own facts (briefing, vehicle, music, next mission).
Generated on the player's machine from the mission table and the MAP
files; a mod may replace it.

Code: the generic part is `WorldDefinition` (retroeng repo: `src/RetroEng.Core/World/`)
(a *world*: instances, links, start; a one-level game is one instance);
the SkyNET reading is `src/SkyNet.Game/Campaign/MissionFile.cs`, the
generator `MissionGenerator.cs`, the store `Baking/BlockStore.cs`
(`StaleMissions`, `BakeMissions`, `LoadMission`). `GameSession` plays a
mission from it.

## Where mission files live

- `missions/MNN.json` (`M01` … `M08` for SkyNET, `M01` … `M19` for Future
  Shock, by mission number) inside the system ZIP, `mods/skynet.zip` or
  `mods/shock.zip`, beside the map blocks ([map-block.md](map-block.md)),
  stored uncompressed.
- A mod overrides one by carrying the same path, for example
  `mods/mymod/missions/M01.json`. A file that does not validate is logged
  and the mission is generated from the data in memory instead.
- A mod adds one under a number the table lacks (`M20.json` ... `M99.json`;
  `ModMissions`): listed after the table's, its row taken from the file
  (start instance's map, `game.vehicle`). See
  [../modding.md](../modding.md#new-maps-and-missions).
- Without the ZIP (the mods folder cannot be written, the headless tools,
  tests) the session generates the file in memory; the result is the same.

### First run and staleness

At start, after the blocks, every mission of the table is checked: the ZIP
entry must exist, parse, and its source hash must equal both the entry's
and the hash its listed inputs give now. Missing or stale files are
generated (8 SkyNET missions in about 0.15 s) and written with
`ZipPackWriter.Update`. `--bake` / `--rebake` do the same for both data sets.

Source hash: `mission <world version>.<game version> tool
<MissionGenerator.ToolVersion> row <number>,<start map>,<mode>,<next> md5
<hex>`, the MD5 of the lines `name=md5\n` of every MAP file the generator
read, in name order (an absent one hashes as ""). Bump
`MissionGenerator.ToolVersion` when the generator's output changes for the
same inputs.

## Layout

UTF-8 JSON, indented, LF line ends, keys in a fixed order (the generator's
output diffs line by line). Comments and trailing commas are accepted on
read.

```json
{
  "format": "retroeng-world",
  "version": 1,
  "id": "skynet/M01",
  "source": { "hash": "mission 1.1 tool 1 row 1,210,0,2 md5 …",
              "inputs": [ { "name": "MAP.210", "md5": "…" } ] },
  "start": { "instance": "210", "arrival": -1 },
  "instances": [
    { "id": "210", "block": "MAP.210", "layer": "MAP.210", "group": "210",
      "properties": { "music": "T200.HMI", "vehicle": "foot" } },
    { "id": "216", "block": "MAP.216", "layer": "MAP.216", "group": "210",
      "properties": { "music": "T200.HMI", "vehicle": "foot" } }
  ],
  "links": [
    { "from": "210", "source": 41234, "to": "211", "arrival": 0 },
    { "from": "211", "source": 2310, "back": true, "arrival": 11 },
    { "from": "230", "source": 5120, "to": null, "arrival": 0 }
  ],
  "game": { "data": "skynet", "version": 1, "mission": 1,
            "briefing": "210.TXT", "vehicle": "foot", "next": 2,
            "torpedo": { "at": "250", "after": "253", "to": "254", "arrival": 0 } }
}
```

(Offsets and hashes above are illustrative.)

### The world (engine part)

| Key | Meaning |
|---|---|
| `format`, `version` | `retroeng-world`, 1; anything else is refused |
| `id` | the world's name (`skynet/M01`, `shock/M13`) |
| `source` | optional: the generator's hash and inputs (absent in a hand-made file, which is then never "stale") |
| `start` | the instance play begins in and its arrival set (−1: the map's own start) |
| `instances[]` | `id` (unique), `block` (the geometry drawn and collided with), `layer` (the entities that drive behaviour), optional `group` (instances that are one place), optional `properties` (strings for the game) |
| `links[]` | `from` instance, `source` (an entity key in its layer), then `to` an instance, `"back": true` (wherever the player came from) or `"to": null` (closed: refused when taken), and `arrival` (the arrival set) |
| `game` | an object the engine does not read |

Validation: ids unique and non-empty, the start instance listed, every
link's `from` and `to` listed, no two links from one source, a link is
not both back and to.

The **same block may be placed by several instances**. Each instance keeps
its own state (per-instance overlay, saved under its id).

### SkyNET's reading

- `block` and `layer` are `MAP.NNN`. Today an entity layer is its MAP file's
  own records, and a block's collision, sprites and dynamic meshes are keyed
  by those records' file offsets, so **an instance's layer must be the
  block's own map** (checked on load). Two instances of one map are allowed.
- A link's `source` is the 0xF0 exit record's file offset; `arrival` is the
  record's marker set (u8 at sub+4). The generator writes one link per 0xF0
  record of every instance: target 0 is `back`, a target the data lacks is
  `"to": null` (none in SkyNET's data: mission 1 has 23 links, 3 of them back).
- `properties.music`: the instance's song (`MusicTracks.ForMap`);
  `properties.vehicle`: `foot`, `jeep` or `hk` (`VehicleModes.ForMission`).
- `game`: `data` (`skynet` / `shock`), `version` (1), `mission`, `briefing`
  (the MDMDBRIF.BSA entry whose `[M1]`..`[M5]` give the objective counter),
  `vehicle` (the mission table's +0x08), `next` (absent at the campaign's
  end), `torpedo` (mission 5's scripted flight: on `at` entered from
  `after`, then `to` at set `arrival`; see [../missions.md](../missions.md#scripted-transitions)).

### What the generator includes

From the table's start map, breadth first through every 0xF0 exit (exits in
record order), plus MAP.254 behind the torpedo ride when MAP.250 and
MAP.253 are both reached. Instance ids are the map numbers. This is the
DOS graph: it also contains maps the mission's route never needs (mission
2's file reaches MAP.216/217 through the shared interiors). SkyNET: 8
missions; Future Shock: 17 (its vehicle missions are a single instance).

### Variant maps and shared blocks

MAP.216 and MAP.217 are MAP.210 later in mission 1, MAP.234 and MAP.235 are
MAP.230 (`MissionGenerator.Variants`, the Godot port's `RulesSkynet.VARIANTS`).
DOS ships each as its own MAP file and its own WLD; the ground is shared
only as identical bytes (WLD.216 = WLD.217, WLD.230 = WLD.234 = WLD.235;
WLD.210 differs from WLD.216). The generator puts a base and its variants in
one `group` (the base's id). Each keeps its own block (its props, robots,
sprites, collision and lamps are keyed by its own map's records), but maps
on the same WLD share one ground layer in the ZIP
([map-block.md](map-block.md#ground-layer)): 216/217 and 230/234/235 here,
200/202, 240/250 and 609/616 beside them.

Blocks shared **between missions** (the interiors MAP.211-215 in missions 1
and 2, 242-248 in 4 and 5, and others) are listed by
`MissionGenerator.SharedBlocks`; each mission places its own instance.

## Runtime

- Starting a mission enters the start instance; `--map NNN` enters the
  mission file's instance of that map.
- An 0xF0 exit looks up its link by (current instance, record offset). An
  exit the file does not list (a mod removed it, or a loose map) falls back
  to its record's target map number, as before.
- Per-instance state (behaviour, pickups taken, robots killed, drops,
  loops) is kept for the mission and dropped on a restart, as DOS's Mst
  overlay per map number.
- Saves (format 6) key each map's state by instance id and store the
  current and previous instance; an older save maps each map number to the
  instance the mission file gives it.
