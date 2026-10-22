# WLD.NNN terrain heightmap

Outdoor maps have a terrain heightmap `WLD.NNN` with the same number as
`MAP.NNN`. Terrain is **one 256 × 256 heightmap**, two triangles per cell.
It is not built from placed meshes.

WLD files are **loose files in the data folder** (DOS opens
`gamedata\wld.NNN`); they are not in MDMDMAP2.BSA, which holds the MAP
files.

Godot port: `scripts/loaders/wld_terrain.gd` (parse, smoothing passes,
height query, mesh build); loaded from `scripts/level_loader.gd`.
C#: `SkyNet.Formats.WldTerrain`, `GameData.LoadWld`; `SkyNet.Formats.WldFile` reads and
writes the raw layers for the editor, every byte it does not explain kept (every shipped
SkyNET and Future Shock WLD writes back byte for byte), and makes new files laid out as
WLD.601 (263 416 bytes; chunks from 0x4A0, 65 558 bytes apart). Some files carry 4 or 16
trailing bytes after the last chunk.

## When a WLD is loaded

Only when the MAP header u32 at **+9028 (0x2344)** equals 1 (outdoor). Any
other value (usually 2) is an interior and no WLD is read. See
[map.md](map.md#header).

If an outdoor map has no WLD file, DOS keeps the heightmap that was loaded
before. Future Shock ships outdoor maps without a WLD (MAP.040, MAP.080).
The Godot port picks the WLD on which most of the map's ground markers
stand (`LevelLoader._best_fit_wld`). See [pitfalls.md](pitfalls.md).

## File layout

The file is chunked: 2 × 2 chunks of 128 × 128 cells, reassembled into
four flat 256 × 256 layers.

| Offset | Size | Type | Meaning |
|---|---|---|---|
| 0x00 | 4 | ? | UNCERTAIN |
| 0x04 | 4 | u32 | chunks_x (2 in every shipped file) |
| 0x08 | 4 | u32 | chunks_y (2 in every shipped file) |
| 0x0C..0x8F | | | UNCERTAIN, not read. Every shipped file: 0x10 at +0x00, 0xA0 at +0x10 (the offset of the u32 0x7F501E19 at 0xA0), 1 at +0x14, 22 at +0x18 (the chunk header size), at +0x1C 0, 0x404F8 (the end of the last chunk, in the files with 16 trailing bytes) or 0x4A0 |
| 0x90 | 4·cx·cy | u32[] | chunk offset table, row-major (`cy * chunks_x + cx`) |

Each chunk, at its table offset:

| Offset | Size | Meaning |
|---|---|---|
| +0 | 22 | chunk header (contents UNCERTAIN, not read): u16 0x868, 0x238, 0x51C, 0x00A for chunks 0-3, u16 0x12E or 0x12C at +6, 1 at +8, the rest 0 |
| +22 | 16384 | layer 0, 128 × 128 bytes, row-major |
| +22+16384 | 16384 | layer 1 |
| +22+2·16384 | 16384 | layer 2 |
| +22+3·16384 | 16384 | layer 3 |

Reassembly: for each layer, chunk row `cy`, cell row `r` (0..127), chunk
column `cx`, append 128 bytes. The result is indexed `row * 256 + col`.

The chunk layout was established from the Win32 Future Shock loader and
hex dumps, then checked against every SkyNET and Future Shock WLD. An
older reading of the file as four flat 64 KB layers is wrong.

## Layers

| Layer | Bits | Meaning |
|---|---|---|
| 0 | 0-6 | index into the 128-entry height curve |
| 0 | 7 | "cell is non-planar" flag, set by the engine at load time when the four corners differ. **Not** a diagonal selector (see below) |
| 1 | 2-7 (`b >> 2`) | decoration spawn type 0..0x21. (legacy) The engine spawns `((set << 7) | type) - 1` where `set` is a per-map value not yet decoded. Not ported |
| 2 | 0-5 | terrain tile (material) id |
| 2 | 6 | tile orientation: rotate 90° |
| 2 | 7 | tile orientation: rotate 180° |
| 3 | all | UNCERTAIN. A load-time pass moves bytes between layers 0 and 3; no runtime reader found |

## Geometry

- Cell size: 256 world units. Grid 256 × 256 = 65 536 units per side.
- Column = `(world_x >> 8) & 0xFF`.
- Row = `((K - world_z) >> 8) & 0xFF` with K = 65 536: **row 0 is the
  north edge (largest Z)**. The mask wraps out-of-range positions.
- Corner height = `HEIGHT_CURVE[layer0[row][col] & 0x7F]`, in world
  units (Y up in this table; DOS Y is down, so the engine negates).
- A cell uses corners (col, row), (col+1, row), (col, row+1), (col+1, row+1).
  The last row and column start no cell.
- **Every cell is split on the NW–SE diagonal** (v1.00 0x14525d). Triangles
  in (col, row) corner terms: {NW, NE, SE} and {NW, SE, SW}.
- Height at a point: find the cell, take local (u east, v south) in [0,1);
  if `u >= v` use the NW-NE-SE plane, otherwise NW-SE-SW.

The engine has no lighting on terrain. Shading comes only from the
textures and the distance depth cue (see [palette.md](palette.md#haze000)).

### Height curve

128 entries, index = `layer0 & 0x7F`. Non-linear: steps of 40 at the low
end, up to 7760 at index 127. (legacy) curve table `dword_104FA8`.

```
   0,   40,   40,   40,   80,   80,   80,  120,  120,  120,
 160,  160,  160,  200,  200,  200,  240,  240,  240,  280,
 280,  320,  320,  320,  360,  360,  400,  400,  400,  440,
 440,  480,  480,  480,  520,  520,  560,  560,  600,  600,
 600,  640,  640,  680,  680,  720,  720,  760,  760,  800,
 800,  840,  840,  880,  880,  920,  920,  960, 1000, 1000,
1040, 1040, 1080, 1120, 1120, 1160, 1160, 1200, 1240, 1240,
1280, 1320, 1320, 1360, 1400, 1440, 1440, 1480, 1520, 1560,
1600, 1600, 1640, 1680, 1720, 1760, 1800, 1840, 1880, 1920,
1960, 2000, 2040, 2080, 2120, 2200, 2240, 2280, 2320, 2400,
2440, 2520, 2560, 2640, 2680, 2760, 2840, 2920, 3000, 3080,
3160, 3240, 3360, 3440, 3560, 3680, 3800, 3960, 4080, 4280,
4440, 4680, 4920, 5200, 5560, 6040, 6680, 7760
```

The curve values are world units. A `<< 8` seen in the height routine is
fixed-point for the rasteriser, not a world scale (checked against the
player's Y on MAP.210).

## Texturing

- Tiles come from **TEXTURE.302** (SkyNET), record = `layer2 & 0x3F`. One
  whole tile covers one cell, rotated by bits 6/7. Tiles are 64 × 64 wall
  layout records (see [texture.md](texture.md)).
- UNCERTAIN: whether the tile bank differs per map or per game (Future
  Shock). The Godot port uses TEXTURE.302.
- Outdoor lakes are painted with tiles 58, 59 and 61 of TEXTURE.302. They
  are not marked by a water marker. (Godot: `WATER_MATERIALS`.)

### Load-time material smoothing

The engine rewrites layer 2 after load (v1.00 0x153144 and 0x15340a). The
Godot port replicates both passes (`_smooth_materials`):

1. **Neighbour smoothing.** For each interior cell whose remap value is
   not 0xFF: if its four orthogonal neighbours hold the same byte `nb`,
   set the cell to `BLEND[(nb & 3) * 4 + (cell & 3)]`.
2. **2 × 2 corner coalesce.** For each cell (TL) with TR, BL, BR all
   remapped ≠ 0xFF, pattern = `(rTL<<4)|(rBL<<2)|rTR|(rBR<<6)` (2 bits
   each), `out = COALESCE[pattern]`. 0xFE: copy TR onto BR and redo the
   same cell. 0xFF: no change. Otherwise TL = out. (The comment above
   `CORNER_COALESCE` in `wld_terrain.gd` has 0xFE and 0xFF the other way
   round; its code does what is written here.)

Tables (in SKYNET.EXE data; v1.00 VAs): `BLEND` 16 bytes at 0x104742,
`REMAP` 64 bytes at 0x104752, `COALESCE` 256 bytes at 0x116200. The values
are in `wld_terrain.gd`.

UNCERTAIN: the DOS passes assume layer 2 was first quantised by a
height-to-tier pass (v1.00 0x15308f) whose thresholds live in runtime
memory. The port runs the passes on the raw bytes.

## Maps without a WLD (Future Shock)

Future Shock ships outdoor MAP.040 and MAP.080 with no WLD.040/080. DOS
then keeps whatever heightmap is still in memory. The Godot port
(`LevelLoader._best_fit_wld`) and `SkyNet.Formats.WldFit` instead score
every WLD in the folder (in name order) by how many of the map's ground
points lie within **60 units** of its drawn ground:

- points: every marker (variant 3 with a marker type) at height
  `−(y + 16)`; when there are fewer than 8, the variant-1 meshes at `−y`
  are added;
- fewer than 8 points: no fit; the best WLD (first one on a tie) wins only
  when it carries at least **33 %** of the points.

Results: MAP.040 → WLD.030 (179 of 203 points), MAP.080 → WLD.170 (72 of
161). `GameData.LoadWldFor(map)` returns the map's own WLD, else the fit.
For maps that do have a WLD the fit is never used (and does not always
pick the map's own file).

## Draw distance

Terrain, polygons and sprites beyond the RENDER DETAIL far clip are not
drawn. See [palette.md](palette.md#haze000) for the v1.01 table.

## Open questions

- Header bytes 0x00-0x8F and the 22-byte chunk header.
- Layer 1 decoration set base per map, and the spawn table.
- Layer 3 purpose.
- Whether DOS really draws the tile at a fixed per-cell UV or with a
  world-planar projection (the result is the same for whole tiles).
- The v1.01 addresses of the smoothing passes and of the tables.
