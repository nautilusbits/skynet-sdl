# Missions

How a DOS mission is put together from MAP files, what state carries
between them, how the Godot port bakes a mission, the design proposed for
the C# port, and how missions are verified.

## 1. A mission in DOS

### The mission table

SKYNET.EXE v1.01 reads the campaign from the mission table at **0x34846**
(Future Shock mode: 0x341ce), 0x5C bytes per entry: +0x00 mission number,
+0x04 start map, +0x08 player mode (0 foot, 4 jeep, 8 HK), then the
loading screen, video and briefing file names. SkyNET's eight missions
start on maps 210, 220, 230, 240, 252, 260, 270, 280
([game-logic/objectives.md](game-logic/objectives.md#mission-table-and-campaign)).
Only the start map is in the table. Every other map of a mission is
reached through **0xF0 doorways** inside the maps.

### Maps of a mission

A mission is a graph of maps:

- the **outdoor world** (the start map);
- **interiors** (their own MAP files, numbered in the mission's decade or
  in the shared 340-599 range) reached through doorways and left through
  return doorways;
- **variant maps**: the same world re-authored for a later phase of the
  mission, with a different set of robots and chains. Two exist in SkyNET:
  MAP.216 and MAP.217 are MAP.210 after the truck ride and after the
  lasers; MAP.234 and MAP.235 are MAP.230. The members share 89-98 % of
  their placed meshes at the same coordinates.

Interiors are **shared**: 54 unique maps serve 79 uses across the eight
missions (211-215 in missions 1 and 2, 242-248 in 4 and 5, some interiors
in three missions).

Example, mission 1: 210 → 211/212/213/214/218 (marker set 0); 211 →
(0, 11); 213 → (0, 13); 218 → (0, 27); 212 → (216, 12); 214 → (215, 0) and
(217, 14); 215 → (214, 10) and (217, 15/18).

### Exits (act 0xF0)

Handler **0x138081** (v1.01; v1.00 0x137881):

- target map = u16 at sub+2; **0 means "back to the previous map"**
  ([0x38b18]);
- marker set = u8 at sub+4;
- writes the pending-map register [0x38b14], the set register [0x38b1c],
  sets [0x30a50] |= 0x20 (map change) and clears its own bit 0 inline.

The session loop (FUN_001216df, v1.00) re-reads the pending-map register
each iteration and **loads the target map from disk** (MapStart
FUN_0012dd00, v1.00). The player is placed by PlrSetPosMarker
(FUN_00121f72 v1.00; FUN_00122472 v1.01): position = marker N of the set,
facing = toward marker N + 1, pitch and roll 0; FUN_0012c02f zeroes the
speed and snaps to a floor −80..+100 around the marker. Markers are
variant-3 entities with `sprite_index >> 7 == 299`, id `& 0x7F`
([formats/map.md](formats/map.md)).

Exits are normally the end of a chain: a **0xEF gate** (use key within 60
units of the eye) → a 0xDB-0xEB sound node → the 0xF0 sprite. Touching the
doorway sprite also arms it.

### The gate pre-latch

Interiors put the player's arrival marker **inside the return gate**
(MAP.211), and MAP.210's marker 27 is 64 units from the bunker gate. Any
port that treats gates as proximity-triggered must not fire a gate that
contains the spawn point until the player has left it, or the player
bounces straight back. The Godot port arms such gates as already
"latched" at spawn and still lets the use key go through a pre-latched
gate whose chain ends in a 0xF0. (In v1.01 the 0xEF gate needs the use key
anyway, see [game-logic/action-handlers.md](game-logic/action-handlers.md#gates-and-proximity);
the pre-latch matters for any auto-firing reading and for 0xF1/0xF2.)

### What carries between maps

- **The Mst overlay**: MstSave FUN_0012e0f4 on leaving a map, MstLoad
  FUN_0012e094 on entering it (v1.00). One file per **map number**,
  `<dir>tfs_swap.NNN` (FUN_0012e17c), records `{u8 type, entity file
  offset, …}` (FUN_0012e244). It holds kills, taken pickups and object
  states (doors, wrecks). Because it is per map number, a variant map
  (216, 217) starts **fresh** in DOS: the dead from 210 are back.
- **Outside the map** and therefore kept: health, armour, weapons, pools,
  both weapon selections, the **objective counter** (per mission).
- On entering the next map of a mission: +½ health, +0.5 armour.
- On a mission restart every `tfs_swap.*` file is deleted and the
  mission-start snapshot is restored
  ([game-logic/damage-hud.md](game-logic/damage-hud.md#death)).
- Between missions nothing but the inventory carries; the ammo grant
  applies ([game-logic/weapons-ammo.md](game-logic/weapons-ammo.md)).

### Scripted transitions

MAP.253 → MAP.250 → MAP.254 (mission 5) is a hard-coded camera flight,
not an exit chain: FUN_00132e00 runs when the current map is 250 and the
previous is 253; the camera leaves the body at marker set 10 and flies
straight along the view at 700 u/s, rolling 90°/s, until it is 2100 units
(0x834) from the body, then requests map 254, set 0. Sound 125, view
input locked, weapon/cockpit/crosshair hidden.

## 2. The Godot port's baked data

All baked data is **derived** and lives in the cache directory
`converted/` (a development checkout uses `res://converted`; a Future
Shock data set has its own). Nothing in it is ever source; it is rebuilt
whenever its stamp no longer matches.

| File | Content | Version / key |
|---|---|---|
| `VERSION` | cache format | `AssetCache.CACHE_VERSION` (12) |
| `maps/MAP.NNN.level.scn` | the level scene (geometry, collision, Behaviour branch with trigger nodes) | `LevelScene.BAKE_VERSION` (15) + hash of the MAP file's bytes |
| `maps/MAP.NNN.level.txt` | plain-text provenance sidecar, checked **before** the scene is loaded (a Godot scene can run code) | |
| `maps/MAP.NNN.triggers.json` | the trigger graph (chains, first/second activation effects) | `TriggerGraph.GRAPH_VERSION` (4) + map hash + rules fingerprint |
| `missions/MISSION.NNN.scn` | one scene per mission: zones + portals + phases | `MissionScene.MISSION_BAKE_VERSION` (2) + the maps' hashes |

A trust manifest in the cache records that this installation wrote each
file; a scene is loaded only then.

### The mission scene

```
MISSION_210
├─ EditorPreview   sky and key light for viewing the scene alone
├─ Zones           Zone_MAP_210 at the origin, interiors east of it in
│                  columns; each zone instances converted/maps/MAP.NNN.level.scn
├─ Portals         one Portal per 0xF0 exit: source map/zone, record offset,
│                  target map, marker set, kind (portal / return / hand-over /
│                  broken), target zone and position, the chain that arms it
└─ Phases          World_MAP_210 → Phase_216, Phase_217 (variant maps)
```

- The outdoor world's ground is **cropped** to its border boxes (marker
  types 30-39, read in pairs) plus the view distance.
- Zones are kept apart by **render layer and physics bit** per zone
  (`scripts/mission/zone_layers.gd`), not by distance; a small gap only has
  to outreach a blast (≤ 540 units).
- A doorway moves the player across the scene; no map is loaded.
- A **phase** swaps which map a zone shows (210 → 216/217). Only the dead
  robots and taken pickups carry into a phase (`main._carry_records`),
  never a mechanism, as in DOS (this deliberately differs from DOS, which
  carries nothing into a variant). Allowed pairs: `RulesSkynet.VARIANTS`
  = {216: 210, 217: 210, 234: 230, 235: 230}.
- The old per-map runtime (load a map per doorway, keep a per-map
  snapshot like the Mst overlay) remains for `--map`, multiplayer and as a
  fallback (`--no-mission-scene`).

### Mods

`mods/` beside the game data:

| File | Effect |
|---|---|
| `mods/maps/MAP.NNN.level.scn` | replaces that map's level scene (no rebake) |
| `mods/maps/MAP.NNN.detail.tscn` | added on top of whichever level plays; must be plain data (scanned before load) |

A mod changes what is presented and where it stands, **never what the map
does**: triggers come from the DOS records. A mission scene always
references the derived level scenes; the mod is substituted when a zone is
built. A modded map is left unpinned by the trigger lock.

## 3. Proposed C# design

**One baked mission file per mission**, built from the player's own data
on first run.

- **Input**: the player's game directory (archives, loose WLD/TEXTURE
  files, SKYNET.EXE tables where needed). No game data ships with the port.
- **Content per mission**:
  - the mission's map graph (start map from the mission table, every map
    reachable through 0xF0 exits, variant phases);
  - per map: parsed entities, the per-name defaults applied, terrain
    (cropped), meshes and textures by reference into a shared asset pack,
    collision data, the trigger graph (nodes, chains, rule rows);
  - the exit table (portals) with resolved arrival positions;
  - the objective count from the briefing's `[M1]`..`[M5]` entries.
- **Versioning**: a header with the bake format version, the rules
  fingerprint (hash of the act-rule table) and a **source hash** over the
  bytes of every input map, heightmap and briefing used. The loader
  rebuilds the file when any of the three differ. Never edit it by hand.
- **Shared maps**: an interior used by several missions may be stored once
  in a shared map pack and referenced, or copied into each mission file.
  The trade-off is size vs. one self-contained file; the Godot port
  instances; a flat file per mission is the simpler alternative. Either is a bake
  decision only.
- **Runtime state** is separate from the baked file: a per-mission state
  (counter, inventory, per-map overlays keyed by map number and entity
  file offset) is what a save game stores.
- **Mod overlay** on top: a mod file per map (or per mission) replacing or
  adding presentation (meshes, placement, lights, detail), applied after
  the baked file is loaded. Decide early whether mods may also add trigger
  records; the Godot port does not allow it (a node without a DOS record
  never fires).
- **Read-only derivation**: the port never writes DOS formats back.
- **Storage and mounting order** (built: [formats/zip-mods.md](formats/zip-mods.md)):
  baked blocks and mission files go into a generated standard ZIP in
  `mods/` (`<dataset>.zip`, one per data set, entries stored), mounted as a
  locked **system mod** at the lowest priority. User mods
  (`mods/<name>.zip` or folder) mount above it
  in load order, later wins; a mod replaces a block, mission, table,
  texture or model by carrying the same path. `mods/<name>/` overrides
  `mods/<name>.zip` file by file, and a ZIP may be unpacked into
  that folder. The loader reads only through the `VirtualFileSystem`, so
  it never knows which mount a file came from.
- **Map blocks** (built: [formats/map-block.md](formats/map-block.md)):
  each DOS map's heavy derived data (ground, static batch, dynamic meshes,
  sprites, collision, lamps) as `maps/MAP.NNN.block` in the system ZIP,
  baked on first run and whenever an input changes. The MAP records stay
  the source of behaviour.
- **Mission files** (built: [formats/mission.md](formats/mission.md)):
  `missions/MNN.json` in the system ZIP, generated from the mission table
  and the MAP exits: map instances (block + entity layer), the exits as
  links, the start, briefing, vehicle, music and next mission. The session
  plays a mission from it; per-map state and saves are keyed by instance.
  Variant maps (216/217, 234/235) are instances of one group, each on its
  own block.

## 4. Verification

The Godot port keeps three layers of automatic checks. Equivalent checks
are strongly recommended for the C# port: the trigger rules are subtle
(toggling chains, edge vs level handlers, one-shot clears, pre-latches,
3D vs 2D distance), and every one of them has broken a mission at least
once. Hand play does not cover 4 700 trigger nodes.

### Trigger lock (static)

`--verify-graph` rebuilds every shipped map's trigger graph and diffs it
against `tests/rules/skynet.triggers.lock` (one line per node: map, id,
act, mode with its measuring rule, chain, first and second activation
effects, hash). Header: graph version, rules hash, 122 maps, 4 763 nodes,
3 538 chains. Runs in about a second and reads no cache. `--accept-lock`
rewrites it deliberately. A rules change that moves the lock is visible
line by line.

### Trigger verifier (runtime)

`--verify-triggers=all` drives the **real game**: for every trigger node
it places the player where the node's mode says he must stand, activates
it through the real input path (`player_driver.gd`) and compares what the
trigger bus reports against the graph. One row per node:

- **PASS**: the runtime did what the graph predicted;
- **FAIL**: it did something else (tags: `chain_silent`, `chain_short`,
  `chain_extra`, `second_activation`, `port_use_reach`,
  `projectile_no_hit`, `path_window`, `mover_absent`, `loaded_state`,
  `exit_forced`);
- **UNREACHABLE**: the node has no way in (no state bits, no key, no
  chain reaches it) or the player cannot be placed there.

Known failures are listed in `tests/rules/skynet.xfail`; the run fails only
on a new one. Baseline at the time of writing: 1 163 PASS / 7 FAIL / 140
UNREACHABLE. Enemy fire is switched off during the run so a stray shot
cannot add a damage stage.

### Mission specs

`--verify-missions` plays `tests/rules/skynet.missions.txt`: one block per
mission, steps in the order a player performs them, across map changes,
with nothing reset between steps. A step is `use | use_below | prox |
shoot | exit | wait | walk | stay <map> <id> expect <effects>`; `must_not`
pins regressions; `counter N` is the objective counter at the end;
`xfail <tag>` excuses a step. Both runtimes (mission scene and per-map)
are played. Baseline: 54 PASS / 0 FAIL / 0 XFAIL.

### Mission solver

`--solve` plays a mission from its start map **with collisions**: it
floods the reachable space with the player's body on a grid (16 units
indoors, 64 outdoors; step ≤ 80, water, pads), fires triggers through the
real action system, shoots as a last resort and takes exits preferring
unseen maps. PASS/FAIL with "why the flood ends at" diagnostics. It found
real passability bugs (body radius, sealed doors, water level) that the
trigger checks cannot see. It is slow on large outdoor maps and some FAILs
are solver gaps, not map bugs: treat a FAIL as a lead.

### The gate

`tools/verify_gate.py quick|full` runs the suites, `--verify-graph`, the
mission specs in both runtimes and the trigger verifier sharded over
several processes (`--verify-shard=I/N`, `--fixed-fps 60` so no frame
waits for the clock). About 3 minutes for `full`.

### Recommendation for the C# port

- Keep the act rules as **data** (one table, one row per act) shared by
  the runtime, a static graph builder and the tests.
- Generate a lock from the graph and diff it in CI.
- Drive the real input path in a headless mode for a per-node verifier
  and for mission scripts; port the mission spec file as is (the ids are
  map file offsets, stable across ports).
- A reachability solver is optional but catches collision bugs early.

## Open questions

- Which maps outside the campaign decades are reachable only through
  hand-overs (e.g. the flight out of mission 7).
- Whether DOS really reloads the previous map's Mst state on a return
  with target 0 in every case (observed, not exhaustively checked).
