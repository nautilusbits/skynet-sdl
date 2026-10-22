# Map block

The baked, game-ready form of one DOS map: everything derived from the
map's files that takes time to build. Code: `src/SkyNet.Game/Baking/`
(`MapBlock` the format, `MapGround` the shared ground layer, `MapBaker` the
bake, `BlockStore` the system ZIP and the mods). The level (`Level.Load`), the collision world
(`LevelCollision.FromBlock`) and the enhanced lamps are built from it.

A block holds no game data of its own kind: it is generated on the
player's machine from the player's files, and textures are referenced by
bank and record, never stored. The MAP records stay the source of
behaviour; the behaviour model reads `MapFile` as before.

## Where blocks live

- `maps/MAP.NNN.block` inside the system mod of the data set, the ZIP
  `mods/skynet.zip` (SkyNET) or `mods/shock.zip` (Future Shock), beside
  the game's exe ([zip-mods.md](zip-mods.md)). Stored uncompressed, so a
  block is one positioned read with nothing to inflate.
- A mod overrides one by carrying the same path: `mods/skynet/maps/MAP.210.block`
  (the system mod's folder) or any user mod. A block that does not read is
  logged and the map is built from the raw files.
- The ZIP manifest's source hash for the entry is the block's source hash
  (below).
- The ground of an outdoor map is not in its block but in a **ground layer**
  shared by every map on the same WLD: `maps/ground/<key>.ground` beside the
  blocks (below). The block names the layer it uses.

### First run and staleness

At start (and when NEW GAME → FUTURE SHOCK switches data) the game checks
every map of the map archive: the ZIP entry must exist, its source hash
must equal the block's, the ground layer it names must be in the ZIP, and
the block's inputs, each read and hashed again from the data now, must give
the same hash. Missing or stale blocks are baked in parallel and written
with `ZipPackWriter.Update` (the mods are unmounted for the commit, then
mounted again), each baked block's ground layer with them (once per layer);
a ground layer no block in the ZIP names any more is removed. The title shows
"PREPARING GAME DATA n/N" while a bake runs; the log has the same lines.

- `--bake` bakes what is missing or stale for SkyNET and for Future Shock
  (when its data is beside) and exits; `--rebake` bakes every map again.
- The check reads only each block's header and source section; about
  0.2 s per data set when nothing is stale.
- When the mods folder cannot be written, the game builds each map from
  the raw files (the same bake, in memory).

## Source hash

`mapblock <format version> tool <MapBaker.ToolVersion> md5 <hex>`: the MD5
of the lines `name=md5\n` of every input, in name order. Inputs:

| Name | Read from |
|---|---|
| `MAP.NNN` | the map archive (MDMDMAP2.BSA / MDMDMAPS.BSA) |
| `WLD.NNN` | loose (outdoor maps) |
| `TEXTURE.NNN` | loose: every bank the bake read (the ground's tiles, the models', the sprites') |
| `HAZE.000` | loose |
| `PALETTE` | the game palette (`GameData.PaletteBytes`) |
| `TRANSFRM.PRS` | MDMDBRIF.BSA |
| `MODEL:NAME.3D` | a prop's model: MDMDOBJS.BSA, then MDMDENMS.BSA |
| `ROBOT:NAME.3D` | a robot's model: MDMDENMS.BSA, then MDMDOBJS.BSA |
| `TABLE:enemy_types` | the shipped table `enemy_types.json` ([exe-tables.md](exe-tables.md)): only what the bake reads of it, each type's index, model and child segments as text lines; only maps with robots (marker type 2) list it |

An absent input hashes as the empty string. Bump `MapBaker.ToolVersion`
whenever the bake's output changes for the same inputs; bump
`MapBlock.FormatVersion` when the layout changes. Either makes every block
stale. A change of the shipped enemy table's models or segments makes the
blocks with robots stale (87 of SkyNET's 122, 102 of Future Shock's 140); a
change of its AI fields (hit points, speeds, fire) makes none stale. No other
table feeds a block, and none feeds a mission file
([mission.md](mission.md)): the mission table, the music and vehicle rules are
code, covered by `MissionGenerator.ToolVersion`.

### Tables and mods

The system ZIP is always baked from the **shipped** tables
(`BakeTables.Shipped`), whatever `--mod` is in effect, so a mod never writes
into it and switching mods never makes it stale. When a mod's
`tables/enemy_types.json` (folder or ZIP) changes what a block holds (a robot's model or
segments), `BlockStore.Load` refuses the blocks whose `TABLE:` inputs differ
from the tables in effect (logged once per map) and the level bakes that map
in memory with the mod's tables (`MapBaker.Bake` defaults to
`BakeTables.Current`), as without the ZIP. A mod that changes only AI fields
keeps every block. The cost is the in-memory bake at each entry of a map with
robots (about 0.4 s for MAP.210 in a Debug build); a per-mod cache ZIP would
remove it if table mods become common.

## Layout (version 3)

Little-endian. A header, a section table, then the sections back to back.
New sections can be added; a reader looks sections up by tag. Arrays are
an `i32` count followed by the elements; strings are `i32` byte length +
UTF-8.

### Header (32 bytes)

| Off | Type | Field |
|---|---|---|
| 0 | char[4] | magic `SNMB` |
| 4 | u16 | format version (3; a block of another version is stale and baked again) |
| 6 | u16 | header size (32) |
| 8 | i32 | map number |
| 12 | u32 | flags: bit 0 outdoor |
| 16 | i32 | section count |
| 20 | u8[12] | reserved (0) |

### Section table (24 bytes per section)

| Type | Field |
|---|---|
| char[4] | tag |
| u32 | reserved (0) |
| i64 | offset from the start of the block |
| i64 | length |

`SRC ` is always first, so the staleness check reads only the start.

### Sections

| Tag | Content |
|---|---|
| `SRC ` | source hash (string); inputs: [name (string), md5 (string, lower-case hex)]; the ground layer's path (string, "" without one) |
| `INFO` | i32 mesh instances, robots, sprites, pickups, models (without robots), models (with robots); missing (strings: WLD, props' models, in load order); robots' missing models (strings) |
| `TERR` | i32 col, row, width, height of the heightmap cells built (the crop; 0,0,0,0: the whole field); u8 has ground (the ground itself is in the layer `SRC ` names) |
| `TEXR` | three texture lists, each [i32 bank, i32 record, u8 opaque]: the props phase, the robots phase, the sprites phase, each in the order the build first made them |
| `WRLD` | the static batch: buckets |
| `ROBT` | the robots in their rest pose (marker type 2 with their segments) at the marker's DOS Y + 0x10 (see below): buckets |
| `DYNM` | [i32 MAP file offset, stages [u8 present, buckets]] for every mesh that moves, changes stage or vanishes (`BehaviourModel.IsDynamic`) |
| `SPRT` | [i32 sprite index, i32 animation phase, sprites [f32×3 centre, f32×2 size, i32 MAP file offset]] in draw order |
| `COLL` | [i32 MAP file offset, corners (f32×3 each, three per triangle)] for every placed variant-1 mesh, in record order; the models collision missed (strings) |
| `LAMP` | [f32×3 position, f32×3 linear colour, f32 range, f32 energy, u8 flicker, f32 phase, u8 lifted, f32×3 facing, f32 coherence, string model, i32 texture word, i32 MAP offset]: the art lamps and the fire lamps (`EmissiveLamps.ForMap`) |

A **vertex** is 28 bytes, the renderer's `Vertex`: f32×3 position (world
axes, Y up), f32×2 texel coordinates, u32 RGBA colour (the indoor shade
level in alpha), u32 packed normal.

A **bucket** is one surface key's placed geometry: u8 textured; then
[i32 bank, i32 record, u8 opaque] when textured; i32 flat palette index
(−1 when textured); vertices; indices (u32, into the bucket's own
vertices); pieces [i32 cell x, i32 cell z, i32 first index, i32 index
count, f32×3 min, f32×3 max]. In `WRLD` and `ROBT` a bucket is split per
1024-unit map cell (cell = floor(x / 1024), floor(z / 1024) of the
placed record or the robot's marker), cells in (z, x) order, so each
cell's part is one index range with its box. The level hands each part's
pieces to the renderer as `MeshChunk`s: every pass (the camera's scene
and depth, each shadow cascade) draws the runs of pieces its own clip
transform can see (`RetroEng.Render/Culling.cs`, retroeng repo). Dynamic stages have no
pieces; they, the robots and the ground chunks are culled by their box.

### Ground layer

`maps/ground/<key>.ground` (`MapGround`): the whole heightmap field of one
WLD as the ground's vertices. It depends only on the WLD and the ground's
tiles, so every map on the same WLD shares it; `<key>` is the MD5 (hex) of
`mapground <ground format> tool <MapBaker.ToolVersion>\nTEXTURE.302=<md5>\nWLD=<md5>\n`,
and the ZIP manifest's source hash is `mapground <ground format> tool <tool> md5 <key>`.
A changed WLD or tile bank gives another key, so a layer is never stale: a
block's inputs (its `WLD.NNN` and `TEXTURE.302`) make the block stale, its
bake names the new layer, and the old one goes when no block names it.

| Off | Type | Field |
|---|---|---|
| 0 | char[4] | magic `SNMG` |
| 4 | u16 | format version (1) |
| 6 | u16 | header size (32) |
| 8 | i32 | cells across (255) |
| 12 | i32 | cells down (255) |
| 16 | u8[16] | reserved (0) |
| 32 | string | key |
| | inputs | [name (string): `TEXTURE.302`, `WLD`; md5 (string)] |
| | vertices | i32 count (4 per cell), then the vertices: per cell NW, NE, SE, SW, cells row by row |

The vertices are `Terrain.Build`'s for the whole field (the same position,
texel, colour and corner normal), reordered row by row. A block's ground is
cut from them (`MapGround.Crop`) in `Terrain.Geometry`'s order: the crop
in chunks of 16 × 16 cells, each chunk's cells row by row, two triangles
per cell, each chunk's box over its corners; the result equals the ground
built from the raw files vertex for vertex (a test checks every outdoor map
of the data).

Maps sharing a WLD (from the data; WLD.NNN files byte for byte equal):

| Data | Maps on one WLD |
|---|---|
| SkyNET | 200 = 202, 216 = 217, 230 = 234 = 235, 240 = 250, 609 = 616 |
| Future Shock | none |

SkyNET has 19 distinct grounds for 24 outdoor maps, Future Shock 16 for 16.
WLD.210 differs from WLD.216, so MAP.210 and its variants share nothing. A
ground layer is 7.3 MB; a block's cropped ground in version 1 was 4-8.9 MB
(vertices and indices), so a map on its own WLD with a small crop costs a
little more, and the indices are no longer stored.

## Loading

The `ROBT` robots stand at their marker, 0x10 units lower (DOS Y + 0x10,
`EnemyLift`), not where the game stands them: in play `Robots.StandOnFloor`
moves each robot to DOS's surface query (FUN_00138d00: the highest upward
face crossed within the stand window, or the WLD ground) by its type's stand
height (the AIS type record's last dword), which puts turrets on their floor
and hung guns under their slab. The bake has the meshes' collision triangles
but not the AIS stand heights, so it does not repeat that; `ROBT` only
serves the static-robot views (`Level.Load` with `staticRobots: true`:
viewers and tools), never the game (it draws the robots live from
`Robots`). A robot there may sit a few units off where the game shows it,
most visibly a turret with a large stand height (guntwr3 160) or a gun hung
under a ceiling.

`Level` repeats what the raw build did on the GPU, so the frame is the
same byte for byte (the draw sort keys hold texture and mesh handle
indices):

- textures are made in the recorded order (`TEXR`), the props phase, the
  robots phase (only when the robots are drawn static) and the sprites
  phase, between the same other uploads as before;
- the buckets are sorted by the textures' real handle index, then the flat
  index (`StaticBatch.Build`), and the robots' bucket appended to the
  props' of the same key;
- the dynamic meshes are made before the world mesh, the sprite quad
  after it.

Not baked (cheap, done at load): the palette and HAZE tables, the ground's
atlas (from TEXTURE.302), the indoor shading rows, the water, the start,
the dusk dome, the stars and the moon. The ground is cut from its layer when
the block is read (`MapBlock.Read` takes a resolver for the layer's path;
`BlockStore.Load` reads it through the mods, so a mod may carry its own
layer beside its block). A block whose layer is missing does not read and the
map is built from the raw files.

## Measurements

One development machine, Debug build, SkyNET 122 maps and Future Shock 140
maps:

| | SkyNET | Future Shock |
|---|---|---|
| bake, all maps (parallel) | 7.5 s | 8.4 s |
| system ZIP, stored, version 1 (ground in each block) | 214.3 MB | 234.9 MB |
| system ZIP, stored, version 2 (shared ground layers) | 176.0 MB | 228.8 MB |
| of which ground layers | 19 × 7.3 MB | 16 × 7.3 MB |
| the same, Deflate fastest (not used; version 1) | 67 MB | 78 MB |
| check, nothing stale | 0.2 s | 0.2 s |

Version 2 saves 38 MB on SkyNET (the five shared WLDs and the indices) and
6 MB on Future Shock (the indices only). Reading MAP.210's block with its
ground (Debug, best of four, `MapBlockTests.Benchmark`): 2.0 ms in version 1,
6.0 ms in version 2 (the ground layer is read whole and cropped).

Map entry (`EnterLevel`: the level, the robots' meshes, warm-up, uploads;
best of 2-3 runs), before → after:

| Map | entry ms | level ms |
|---|---|---|
| 210 | 469 → 371 | 248 → 153 |
| 220 | 526 → 377 | 312 → 170 |
| 250 | 510 → 392 | 295 → 181 |
| 214 | 170 → 151 | 63 → 47 |

Reading and parsing a block: 0.3-11 ms Deflate (0.1-3.4 ms raw); the
collision world from a block: 0-7 ms against 2-9 ms from the raw files.
