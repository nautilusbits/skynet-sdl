# SkyNET / Future Shock technical documentation

Reference documentation for a C# + SDL3 port of **The Terminator: SkyNET**
(Bethesda, 1996, XnGine) and the data of its predecessor
**The Terminator: Future Shock** (1995).

## Ground rules

- **Ground truth is the DOS executable `SKYNET.EXE` v1.01** ("SkyNet v1.01 -
  11/19/96", DOS/4G LE). This is also true for Future Shock data: the port
  runs Future Shock data through SkyNET's engine rules, not through the
  Win32 Future Shock executable.
- **The Godot port** (`godot_project/`) is the current working reference
  implementation. File names such as `scripts/loaders/map_file.gd` refer to it.
- Addresses are Ghidra virtual addresses in SKYNET.EXE v1.01 unless they are
  marked **(v1.00)** (an earlier build whose decompile some findings came
  from) or **(legacy)** (an older decompile of unknown build, cited by
  function name only). See [pitfalls.md](pitfalls.md#addresses-and-the-executable)
  for how to translate between them.
- All multi-byte values are little-endian.
- No game data is reproduced here. Only layouts, numeric constants and code
  addresses.
- "UNCERTAIN" marks anything that is not verified against the executable
  or against every shipped file.

## Index

Status: **done** = written in full for what is known; **TODO** = planned
but not yet written.

### Formats (`formats/`)

The **C#** column names the reader in `src/SkyNet.Formats` that parses the
format and is tested against every shipped file and against the Godot
port's decode.

| Doc | Subject | Status | C# |
|---|---|---|---|
| [formats/bsa.md](formats/bsa.md) | BSA archives (table of contents, encryption) | done | `BsaArchive` |
| [formats/wld.md](formats/wld.md) | WLD.NNN terrain heightmap; Future Shock's substitute heightmap | done | `WldTerrain`, `WldFit` |
| [formats/map.md](formats/map.md) | MAP.NNN entity files: header, name table, variants 1/2/3, markers, links | done | `MapFile` |
| [formats/texture.md](formats/texture.md) | TEXTURE.NNN banks: wall layout, sprite layout, solid colours, animation | done | `TextureBank` |
| [formats/3d.md](formats/3d.md) | .3D models: header, frame table, faces, UVs, winding | done | `Model3D` |
| [formats/palette.md](formats/palette.md) | .COL palettes, HAZE.000 depth-cue table | done | `Palette`, `HazeTable` |
| [formats/fnt.md](formats/fnt.md) | FONT00NN.FNT bitmap fonts | done | `FntFont` |
| [formats/hmi.md](formats/hmi.md) | HMI music | done | `HmiFile` (no synthesis) |
| [formats/img-cfa.md](formats/img-cfa.md) | IMG still images, CFA cel animations (both layouts) | done | `ImgFile`, `CfaFile` |
| [formats/prs-txt.md](formats/prs-txt.md) | PRS text tables, TRANSFRM.PRS, briefing NNN.TXT | done | `PrsFile`, `TransfrmPrs`, `Briefing` |
| [formats/controls-dat.md](formats/controls-dat.md) | CONTROLS.DAT key bindings | partial | `ControlsDat` (17 scancodes) |
| [formats/ais.md](formats/ais.md) | AIS enemy scripts, parameter blocks, animation and ammo tables (in the executable) | done | `AisData`, `LeExecutable` (parsing only, no VM) |
| [formats/zip-mods.md](formats/zip-mods.md) | ZIP mods (standard ZIPs, the engine's manifest of SHA-256 and source hashes), the mods folder and mount order (system ZIP locked under user mods; a folder over its ZIP) | done | `ZipPack`, `ZipPackWriter`, `VirtualFileSystem`, `ContentRoot` |
| [formats/map-block.md](formats/map-block.md) | The baked map block (`maps/MAP.NNN.block` in `mods/skynet.zip` / `shock.zip`): ground, static batch per texture and map cell, dynamic meshes, sprites, collision, lamps; source hash, first-run bake, `--bake` / `--rebake` | done | `MapBlock`, `MapBaker`, `BlockStore` |
| [formats/mission.md](formats/mission.md) | The mission file (`missions/MNN.json` in the system ZIP, a mod overrides it): map instances on blocks, 0xF0 exits as links, start, briefing, vehicle, music, next mission, the torpedo ride; variant groups; saves by instance | done | `WorldDefinition` (RetroEng.Core), `MissionFile`, `MissionGenerator` |
| formats/raw-vid.md | RAW sound effects, VID video | TODO | |
| formats/save.md | DOS save games (not decoded; the Godot port uses its own format) | TODO | |
| [formats/exe-tables.md](formats/exe-tables.md) | Static tables in SKYNET.EXE shipped as data/tables/*.json (enemy, AIS, ammo, weapon, pools, start lists, pieces, destruction, drops, location names, mission enemy counts) and how to regenerate them | done | `tools/SkyNet.Tables`, `SkyNet.Data.GameTables` |
| [hires-textures.md](hires-textures.md) | HD texture mods made from the DOS art (`mods/<name>/textures/TEXTURE.NNN/RRR.png`), `tools/upscale`, eight upscalers compared | done | `TextureStore.HdImage`, `Renderer.ReplaceTexture` |
| [modding.md](modding.md) | Mods for the enhanced look: `--mod a,b`, textures through the mods' file system (folder or ZIP), material replacement from CC0 photos, glTF 2.0 models replacing DOS props (`models/NAME.glb` + sidecar), `tools/modkit` | done | `ModContent`, `ModelReplacement`, `Gltf` |

### Game logic (`game-logic/`)

See [game-logic/README.md](game-logic/README.md). All 15 topics are done
(automap, player movement, damage/HUD, weapons/ammo, throwables, vehicles,
enemies/AI, action handlers, pickups, objectives, water, sky/fog/detail,
message box, rocket eye, Future Shock differences).

### Other

| Doc | Subject | Status |
|---|---|---|
| [missions.md](missions.md) | How a mission is assembled from maps, the baked mission file, verification | done |
| [pitfalls.md](pitfalls.md) | Traps found so far | done (living list) |
| [demos.md](demos.md) | Demo recording and playback (`.sndemo`), the per-tick state hash, the determinism rules for simulation code | done |
| [video.md](video.md) | `--capture`: the game and the editor write a video's frames at a fixed step; scripted shots | done |

## Coordinate conventions (summary)

| Item | Value |
|---|---|
| World axes (DOS) | X east, **Y down**, Z north (forward) |
| Godot conversion | (x, -y, -z): a 180° rotation about X, no reflection |
| Angles | 11-bit: 2048 units = 360°, value `& 0x7FF` |
| MAP entity grid | 1024 world units per cell |
| WLD heightmap | 256 world units per cell, 256 × 256 cells |
| .3D vertices | 1/256 world unit (divide by 256) |
| Player eye | 75 units above the feet; start markers are eye positions |
