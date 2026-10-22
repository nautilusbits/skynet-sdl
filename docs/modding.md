# Modding the enhanced look: textures, materials and models

How a mod changes what the **ENHANCED** look draws: HD or replacement
textures for the DOS texture records, and glTF 2.0 models standing in for DOS
`.3D` props. The **ORIGINAL** look never changes: it keeps the palette
indices, the DOS models and the DOS shading. Collision and game logic always
use the DOS data, whatever a mod draws. A mod can also replace the game's
tables (weapons, robots, drops...), which changes play in both looks
([Tables](#tables)).

Code: `src/SkyNet.Game/Mods/` (`ModContent`, `ModelReplacement`, `ReplacementSurface`,
`ReplacementMesh`, `ReplacedModels`),
`src/SkyNet.Data/GameTables.cs` (tables),
`src/SkyNet.Game/Level.Replacements.cs`, `EnemyRenderer`, `AvatarRenderer`, `CockpitRenderer`,
`Gltf.cs` (retroeng repo: `src/RetroEng.Core/Models/`)
(the glTF reader), `TangentSpace.cs`, `Jpeg.cs` (retroeng repo), `TextureStore.HdImage`,
`Renderer.ReplaceTexture`, `Material.SurfaceMaps` (`enhanced.hlsl`).
Tools: `tools/modkit/modkit.py` (example mods from CC0 sources),
`tools/upscale/upscale.py` (upscaled DOS art, [hires-textures.md](hires-textures.md)).

## Enabling mods

In the game: **OPTIONS → MODS** ([The MODS screen](#the-mods-screen)). The
list it keeps is saved in the settings file beside the game and used at every
start. For one run, the command line stands in for it:

```
skynet-sdl.exe --mod <name>[,<name>...] --look enhanced
skynet-sdl.exe --no-mods
```

- Each name is a mod in the `mods/` folder beside the executable: a folder
  `mods/<name>/`, a ZIP `mods/<name>.zip`, or both (the folder lies over the
  ZIP file by file). See [formats/zip-mods.md](formats/zip-mods.md).
- Several mods are given in **load order**: a later mod wins when two carry
  the same file.
- Only the mods turned on (or named) are used, for textures, models and
  tables alike. A mod in the folder that is not on does nothing.
- `--mod` (and `--no-mods`, none at all) is for that run only: the MODS
  screen's list stays as saved, and the screen says `--MOD SETS THIS RUN'S
  MODS` while it edits the saved list for the next start.
- **Android**: the setup screen (first start, or the icon's long-press
  **Setup**) takes an optional mods folder, mirrored into the app's
  `files/mods` at every start and by **Sync mods now**: new and changed
  files (size, time) are copied, files gone from it deleted. It only deletes
  what it copied (`files/mods-sync.txt`), never the baked `skynet.zip` /
  `shock.zip`, and skips those names in the picked folder
  (`src/SkyNet.Game/Mods/ModMirror.cs`, `src/SkyNet.Android/ModSync.cs`).
- `logs/skynet.log` lists what was mounted and loaded under `[Mods]` (with
  the tables the mods carry), and any file it refused under `[Mods]` or
  `[Textures]`, with the reason.

## The MODS screen

OPTIONS → MODS, in the title menu and the in-game Esc menu, drawn as the
DISPLAY screen is (OPTIONS.IMG's stone and wells, the menu fonts, EXIT with
its lit frame). Code: `src/SkyNet.Game/Menu/ModsScreen.cs`,
`src/SkyNet.Game/Mods/ModCatalog.cs` (`ModCatalog`, `ModInfo`, `ModOrder`).

- **The list.** Every mod in `mods/`: first the mods turned on, the one that
  wins first (the last in the load order), then the others by name, then the
  system mods (`skynet`, `shock`: the baked maps), which are locked: always
  on, below every user mod. A row shows ON / OFF, the name, and whether the
  mod is a `ZIP`, a folder (`DIR`) or both (`ZIP+DIR`). Seven rows show at a
  time; the list scrolls with the selection (a bar at its right edge shows
  where it stands).
- **Under the list**, the selected mod's `mod.json` (name, version, author,
  description) and what it carries: `CARRIES TEXTURES, MODELS, TABLES` (it
  has a `textures/`, `models/` or `tables/*.json`), or `CARRIES NOTHING THE
  GAME READS`. A `mod.json` that does not read shows its error there, in
  yellow; a mod that does not open (a damaged ZIP) shows its name in yellow
  and the reason.
- **Keys.** Up / down walk the list; Accept (Enter, the pad's A) turns the
  selected mod on or off. A mod turned on goes to the top: it loads last and
  wins over the others. Right goes to **UP** / **DOWN**, which move the
  selected mod one place up (it wins over the one it passes) or down; left
  goes back to it. The mouse: a click on the ON / OFF box turns a mod on or
  off, a click elsewhere on a row selects it (on the top or bottom row it
  scrolls the list on), UP / DOWN and EXIT click. Esc or EXIT steps back.
- **Saved at once.** Every change is written to the settings file
  (`"mods"`: the mods turned on, in load order). A name whose mod has left
  the folder stays in the list (it is skipped, and logged, when mounting) and
  comes back on when the mod does.
- **When it takes effect.**
  - On the title (no game running): at once, all of it.
  - In a game: the **textures and models** at the next map that loads (an
    exit, a new mission, a load, a restart); the **tables** when a game
    starts: NEW GAME, LOAD or RESTART MISSION. A table change never reaches
    a mission halfway (robots already on the maps of it were built from the
    old one). The screen says `FROM THE NEXT MAP; TABLES NEXT GAME` after a
    change in game.

### `mod.json`

Optional, in the mod's root (`mods/<name>/mod.json` or in the ZIP): what the
MODS screen says about the mod. Without it the screen shows the folder's
name and what the mod carries.

```json
{
  "name": "CC0 props",
  "version": "1.0",
  "author": "modkit",
  "description": "Wooden crates, the submarine's mess table and a chair as glTF models.",
  "comment": "free text, not shown"
}
```

| Key | Limit | Shown |
|---|---|---|
| `name` | 40 characters | The title of the info box, instead of the folder's name (the settings and `--mod` keep using the folder's name). |
| `version` | 16 | After the name (`V1.0`). |
| `author` | 40 | `BY ...` under it. |
| `description` | 400 | Word-wrapped under them, cut with `...` where the box ends (two to three lines). |
| `comment` | - | Never: notes for whoever edits the file. |

- Every key is optional and a string; line breaks and tabs become spaces,
  and an empty string counts as absent. The screen shows the text in
  upper case, in the DOS menu font: letters, digits and common punctuation.
- UTF-8 JSON; comments and trailing commas are allowed (as in the model
  sidecars). At most 64 KB.
- An unknown key, a value that is not a string, a string over its limit, a
  duplicate key or text that is not JSON is an **error**, shown with where it
  is: `MOD.JSON: $.COLOUR (LINE 1, COLUMN 27): UNKNOWN KEY "COLOUR" (NAME,
  VERSION, AUTHOR, DESCRIPTION, COMMENT)`. The file is only the mod's label:
  a mod with a bad `mod.json` still loads.
- The folder over the ZIP: a `mod.json` in `mods/<name>/` wins over the
  ZIP's, as every other file does.

### The HI-RES TEXTURES notice

When the look is ENHANCED and no mod with textures is in use, the title (or
DISPLAY's LOOK turned to ENHANCED) shows once a box saying that sharper
textures can be made from the player's own data with GENERATE HI-RES
TEXTURES under OPTIONS → MODS or with `tools/upscale`
([hires-textures.md](hires-textures.md)).
OK or Esc dismisses it for good (`"hiresNoticeSeen"` in the settings); MODS
dismisses it and opens the MODS screen. It never comes up under the ORIGINAL
look, and the game never makes the textures unasked.

## Layout of a mod

```
<name>/
  textures/TEXTURE.NNN/RRR.png    a texture record's replacement
  textures/TEXTURE.NNN/RRR_n.png  its normal map, with RRR_r.png its roughness (optional,
                                  ENHANCED look: hires-textures.md#relief-maps)
  models/NAME.glb                 a DOS model's replacement (or NAME.gltf)
  models/NAME.json                its sidecar (optional)
  tables/<table>.json             a shipped table's replacement
  tables/sprite_models.json       sprites drawn as models/sprites/*.glb (ENHANCED look)
  models/sprites/NAME.glb         a sprite model (apart: models/NAME.glb replaces NAME.3D)
  models/viewmodels/NAME.glb      the gun in hand as a 3D model, NAME.json its sidecar (ENHANCED look)
  robots/<name>.json              a robot type added or changed (one flat row)
  maps/MAP.NNN.layer.json         a map's edits (entities, triggers, links)
  maps/<name>.layer.json          a second edited copy of one map
  maps/WLD.NNN                    a map's terrain, a whole WLD in the DOS format (Terrain overrides)
  maps/MAP.NNN                    a new map in the DOS format (New maps and missions)
  missions/MNN.json               a mission: a DOS one replaced, or a new one (20-99)
  briefings/NNN.TXT               a new mission's briefing (its objectives)
  editor/<layer>.graph.json       SkyNetEdit's trigger graph node positions (the game ignores it)
  materials.json                  the texture records' materials (ENHANCED look)
  lights/shots.json               the laser and plasma shots' lights (ENHANCED look)
  lights/lamps.json               the lamps' spot / omni overrides (ENHANCED look)
  weather/*.json                  weather over the outdoor maps: storm, rain (ENHANCED look)
  weather/thunder.wav, rain.wav   the weather's own sounds, in place of the ones made in code
  sounds/NAME.wav                 a DOS sample's replacement (NAME.RAW or NAME.WAV of MDMDSFXS.BSA)
  tables/sounds.json              how samples play: volume, range, pitch, priority, loop
  tables/effects.json             how fire, smoke, explosions, sparks, lightning draw (ENHANCED look)
  tables/skies.json               the dusk maps' sky: gradient, sunset glow, clouds (ENHANCED look)
  tables/night_skies.json         the night maps' sky: stars, Milky Way, clouds, moon halo (ENHANCED look)
  LICENSE.txt                     credits and licences of the mod's assets (recommended)
```

Paths are case-insensitive inside the game, on every OS.

## Textures

`textures/TEXTURE.NNN/RRR.png` replaces record `RRR` of bank `NNN`, both
zero-padded to three digits ([formats/texture.md](formats/texture.md)).

- A PNG, 8-bit RGB or RGBA (straight alpha, not premultiplied). Palette PNGs
  are read too.
- Its width must be a whole multiple of the record's width, and its height
  the same multiple of the record's height (an animated record: of the
  height of all its frames stacked downwards, each centred in the largest
  frame's box). Any multiple works; 4x and 8x are typical. A file of the
  wrong size is skipped with a warning.
- **Alpha.** On a model face (opaque surface) the alpha is ignored and the
  stored colour is drawn, also where the record has index 0; put the haze
  colour there (palette index 31) to match DOS. On a sprite, alpha 0 makes a
  hole, but the cut-out's shape stays the record's own index-0 mask in every
  look (the depth and shadow passes read the index texture), so keep holes
  where the record has them.
- **Ground tiles.** The outdoor ground's TEXTURE.302 tiles are used only when
  every tile has a replacement at one scale (the game builds one atlas).
- The replacement's colour is used as it is: it is not tinted by the palette
  or the haze table. Lighting, the depth haze and shadows of the enhanced
  look apply as to the DOS textures.

Loading: when a level is built, the game decodes the replacements of every
texture the level uses on all cores at once, then keeps them for that level
(a restart of the same map reuses them). On a 24-thread machine the 27 HD
textures of MAP.210 decode in about 25 ms. The decoded pixels stay in memory
for the level's life (about 256 KB per 64 x 64 record at 4x).

### Texture size: DETAIL

OPTIONS → DETAIL also sets the size the HD textures and the models'
textures are loaded at, so a large mod fits 4 GB cards and phones:

| DETAIL | each side | memory |
|---|---|---|
| HIGH | full | 1 |
| MED | 1/2 | 1/4 |
| LOW | 1/4 | 1/16 |

- Shrunk on the CPU as each image is decoded (a box filter: colour in linear
  light weighted by alpha, normal maps averaged and made unit again,
  roughness, occlusion and metal averaged). The DOS textures and the
  ORIGINAL look are untouched.
- A record's replacement stays a whole multiple of the record: an 8x or 4x
  file shrinks by 2 or 4, a 2x file by 2 at most, a 3x file not at all.
- A change takes effect at the next map's load (the far clip and depth cue
  at once). `--detail low|med|high` sets it for one run.
- With no DETAIL saved (a first start) the game guesses one from the GPU's
  name: phones (Android, Adreno, Mali, ...) and software renderers LOW;
  integrated GPUs (Intel UHD / Iris / Arc Graphics, AMD Radeon Graphics /
  Vega / 780M, Apple) and 2-4 GB cards (GeForce MX, GTX 1050/1650, RX 6500)
  MED; others HIGH (`DetailGuess`). SDL_GPU reports no video memory.

### Material replacement

A *material* replacement is a texture replacement whose pixels come from a
photo texture instead of the DOS art: the record keeps its UV mapping, so the
photo fills the same tile. `tools/modkit/modkit.py materials` makes such a
mod from CC0 photo textures (Poly Haven), colour-matched to each DOS record so
the palette of the original is kept. Its mapping (DOS record to Poly Haven
texture) is the Godot port's: the plain tiling materials among the records
that cover the most world area, plus picks by eye. Building facades, signs,
panels, cut-outs and animated records stay DOS art. With `--table` (below)
every record also gets relief maps ([hires-textures.md](hires-textures.md#relief-maps)).

## Models

`models/NAME.glb` replaces the DOS model `NAME.3D` (the name as the map's
name table or the robot table spells it, without `.3D`; case does not matter)
wherever a map places it or a robot, avatar or vehicle is built of it.

What is replaced:

- **Static props** (the merged world mesh): the DOS prop's triangles are
  left out of a second world mesh that only the enhanced look draws, and the
  glTF model is drawn at each placement.
- **Dynamic props** (movers, doors, destructibles, wrecks): each stage of the
  entity whose model has a replacement is drawn as that replacement,
  following the entity's movement; a stage without one (for example a
  destroyed crate's wreck) stays DOS's.
- **Robots** (and their flying wreck parts), **deathmatch avatars** (the
  bodies, heads and the jeep and HK network models, driven or parked) and the
  **cockpits** of the jeep and the HK: per segment model and per DOS frame
  ([Robots, avatars and vehicles](#robots-avatars-and-vehicles)).
- **Sprites** (pickups, scenery billboards, what destruction drops): by the
  table `sprite_models`, not by name ([Sprite models](#sprite-models)).
- **The gun in hand** (the view-model): per weapon, by name
  ([View-models](#view-models)).
- Not replaced: the sky dome.

The original look, collision, hit tests and the level behaviour always use
the DOS model.

### glTF support

`Gltf.cs` (retroeng repo: `src/RetroEng.Core/Models/`), a small reader with no dependencies
(the engine's own `RetroEng.Json.JsonItem`; NativeAOT-safe):

- `.glb` (binary), or `.gltf` with its buffers and images embedded as data
  URIs or as files beside it in `models/`.
- The default scene's node tree, with matrix or translation/rotation/scale
  transforms, flattened into one model.
- Triangle primitives (also strips and fans): `POSITION`, `NORMAL`,
  `TANGENT` and the base colour texture's `TEXCOORD_n`; float or normalised
  integer attributes; 8-, 16- or 32-bit indices.
- Materials (metallic-roughness): `baseColorFactor`/`baseColorTexture`,
  `metallicFactor`, `roughnessFactor`, `metallicRoughnessTexture` (roughness
  in green, metallic in blue), `normalTexture` (with `scale`),
  `occlusionTexture` (red, with `strength`), `emissiveTexture`,
  `emissiveFactor` and `KHR_materials_emissive_strength`, `alphaMode`
  (OPAQUE, MASK with `alphaCutoff`, BLEND), `doubleSided`. Every map is read
  with the base colour's texture coordinates.
- Images: PNG (decoded by SDL) and JPEG, baseline or progressive, greyscale
  or colour, any chroma subsampling (`Jpeg.cs` in the retroeng repo, no
  native dependency; not read: arithmetic coding, 12-bit, CMYK). An image
  that does not decode is left out with a warning (a base colour then falls
  back to its factor).
- Not read: `KHR_texture_transform` and other extensions (a file that
  *requires* an extension is refused), a second texture coordinate set,
  skins, morph targets, animations, cameras, lights, sparse accessors,
  vertex colours, points and lines.
- A model without normals is drawn without them (the enhanced look then
  shades it flat to the light).

### Materials as drawn

A replacement is drawn in the enhanced look only, lit as the rest of it
(the sun with its shadow cascades, the sky and ground hemisphere, the
clustered point lights, the haze):

- **OPAQUE**: alpha ignored. **MASK**: cut where the alpha is under
  `alphaCutoff` (the cutoff is moved to half cover in the texels, so
  filtering, mips and alpha to coverage behave about it); drawn in the
  alpha-tested queue, and cut alike in the camera's depth and the sun's, so
  a cut-out casts its own shape. **BLEND**: blended over what is behind, in
  the transparent queue (sorted back to front with the game's other
  transparent draws, depth tested but not written), casting no shadow.
  `doubleSided` keeps both faces; a back face is lit as seen from its side.
- A material that names a metallic or roughness factor, or any map, or an
  emission, is *physical*: its base colour, normal map, occlusion-roughness-
  metallic and emission go to the GPU as four layers of one texture, and the
  tangents come from `TANGENT` or are made per vertex as MikkTSpace does
  (angle-weighted, perpendicular to the normal; a vertex shared by mirrored
  and unmirrored triangles is not split). It is lit by a metallic-roughness
  model built on the enhanced look's: the diffuse light as every lit surface
  gets it (times 1 - metallic, the hemisphere times the occlusion), plus the
  sun's GGX highlight and, for smooth or metal surfaces, the hemisphere seen
  along the reflection. A rough non-metal looks as it would without maps,
  with its normal map's relief. Point lights light it diffusely only. The
  emission (texture times factor, times the strength) is added unlit, and
  past the glow threshold it glows.
- A material that says nothing beyond its base colour is drawn as a plain
  lit surface (not with glTF's default metallic 1, roughness 1).
- Maps of another size than the base colour are resampled to its size.

Keep models small: a prop is drawn once per placement and per material.
A few hundred to a few thousand triangles per prop and one or two materials
with textures up to 1024 x 1024 are reasonable.

### Coordinates and units

glTF is Y up, right-handed, with counter-clockwise front faces; so is the
game's world after its DOS-to-world conversion (DOS `(x, y, z)` becomes
`(x, -y, -z)`), so a model needs no axis change. The game's unit is one DOS
world unit: the soldier's eye is 75 units above the floor, so one metre is
roughly 40 units. Rather than modelling in world units, let the sidecar fit
the model to the DOS prop's box.

### Sidecar `models/NAME.json`

Optional. Every key is optional; an unknown key is an error (the model is
then not used, and the log says why).

```json
{
  "rotation": [0, 90, 0],
  "fit": "height",
  "scale": 1.0,
  "align": "base",
  "offset": [0, 0, 0],
  "frames": [0],
  "comment": "free text; also 'source' and 'licence'"
}
```

| Key | Default | Meaning |
|---|---|---|
| `rotation` | `[0, 0, 0]` | Degrees about X, then Y, then Z, applied to the glTF model first (to face it the way the DOS model faces). |
| `fit` | `"height"` | How the model is sized against the DOS model's box (frame 0, in the DOS model's own space, after the rotation): `"height"` its height, `"width"` its larger horizontal side, `"box"` the whole box (uniform, the smallest ratio: it fits inside), `"stretch"` each axis to the box (exact footprint; proportions not kept), `"none"` no fitting. |
| `scale` | `1` | A factor after the fit. With `"fit": "none"` it is world units per glTF unit (about 40 for a model in metres). |
| `align` | `"base"` | `"base"`: the model's bottom centre on the DOS box's bottom centre. `"centre"` (or `"center"`): centre on centre. `"origin"`: the glTF origin on the DOS model's origin. |
| `offset` | `[0, 0, 0]` | World units added last, in the DOS model's space (Y up). |
| `frames` | `[0]` | The DOS model's animation frames the replacement stands in for: a list of frame numbers and `"first-last"` ranges (`[0, "4-7"]`), one range (`"0-11"`), or `"all"`. Props are drawn at frame 0, so a list without 0 disables the replacement. A robot segment's frames covered neither here nor by `frameModels` are drawn as DOS's. |
| `frameModels` | `[]` | Other glTF files of `models/` standing in for some DOS frames each: `[{"model": "NAME_FIRE", "frames": ["5-8"]}]` (`model` without folder or extension; `frames` as above). The first entry that covers a frame wins, then the main model's `frames`. Each is placed with the **main** model's rotation, fit, scale, align and offset (its own sidecar is not read), so author all of a model's files in one space. A file that is missing or does not read is skipped with a warning. |
| `lod` | see below | When the model's [levels of detail](#levels-of-detail) are drawn: `{"screen": [0.25, 0.08], "hysteresis": 0.1}` (each key optional) or `"off"`. |

### Levels of detail

A model may come with coarser copies of itself, drawn when it is small on the
screen: `NAME.lod1.glb`, `NAME.lod2.glb` (up to `lod3`) beside `NAME.glb`. They
are read up to the first missing one. A level is placed with the main file's
sidecar and drawn with the main file's materials, matched by material name
(else by index), so it needs no images of its own.

| Model | Levels | Thresholds from |
|---|---|---|
| Prop, vehicle, wreck stage, robot segment (`models/NAME.glb`, its `frameModels`) | `NAME.lod1.glb`... | the sidecar's `lod` |
| Sprite model (`models/sprites/NAME.glb`) | `models/sprites/NAME.lod1.glb`... | its sidecar's `lod` (static); defaults (with clips) |
| Robot template (`models/NAME.robot.glb`) | `NAME.robot.lod1.glb`... | `models/NAME.robot.json`: `{"lod": {...}}` |

Level k is drawn while the model's largest side covers less than `screen[k-1]`
of the screen's height: by default level 1 below 25 %, level 2 below 8 %. A
switch back needs the size to clear the threshold by `hysteresis` (0.1: level 1
holds until 27.5 %, level 0 until 22.5 %), so a model at the edge does not
flicker. Each copy on the map keeps its own level; a robot's segments each
theirs. Weapons in hand, cockpits and the deathmatch's avatars are always drawn
at level 0; so are drops.

A level of a skinned or animated file (a robot template, a sprite model with
clips) must have the main file's joints, found by node name (`seg2_HVYPLSM`);
its clips are not read (the main file's play). Its parts with morph targets are
left out: the main file's morph parts are drawn in every level. A level that
does not fit is logged and not drawn, with the levels after it.

The original look never draws them. Blender makes them well: import the
`.glb`, a Decimate modifier (collapse, ratio 0.35 and 0.12) first in each
mesh's stack and applied, custom normals cleared, smooth by angle, export
without images or animations (skins kept).

The DOS model's own space is the one the map's placement (position and
yaw/pitch/roll) applies to, so the sidecar never needs to know where a prop
stands on a map.

### Robots, avatars and vehicles

A robot is a hierarchy of segments, each a DOS model (`enemy_types.json`:
the type's `modelFile` and its `segments`' child types; a type-14 gun tower
is `GUNTWR1` with `HVYTRRT3` on it and two `HVYPLSM` barrels on that). Each
segment is drawn at the transform the DOS segment hierarchy, the AIS script
and the AI give it, at the animation frame they pick
([formats/ais.md](formats/ais.md), [formats/3d.md](formats/3d.md)). A
replacement works **per segment model and per DOS frame**:

- `models/HVYTRRT3.glb` replaces every segment drawn from `HVYTRRT3.3D`, of
  any robot type, placed by that segment's transform: the segment turns,
  aims, recoils and falls as DOS's does. Segments of other models stay DOS's,
  so a mod can replace only the parts it wants.
- **Frames.** DOS animates a segment by swapping whole vertex frames of its
  model (a walker's legs, a gun's recoil). A replacement maps them to
  **static meshes per frame or frame range**: the main file stands in for its
  sidecar's `frames`, each `frameModels` entry for its own (a walk cycle:
  `"frames": "0-2"` for the main pose, then
  `{"model": "WALKER_STEP", "frames": ["3-5"]}` and so on). This is the
  simplest mapping that covers DOS's frame swap, which never blends between
  frames; glTF skins, morph targets and animations are not read. A model with
  one frame (most turrets and vehicles) needs only `"frames": "all"`.
- The fit is made against the DOS model's frame-0 box (from its faces), the
  same for every frame and every frame model, so the frames line up.
  `"fit": "stretch", "align": "centre"` maps a unit box onto it exactly;
  `"fit": "none", "align": "origin"` takes the glTF file in DOS world units in
  the segment's own space (Y up, ahead is -Z), which keeps a barrel on its
  pivot.
- **Wreck parts.** A robot that breaks up throws its segments as wreck parts,
  drawn at frame 0, so they are replaced too.
- **Deathmatch.** The other players' bodies and heads (the avatar models,
  T800RFL as the fallback) at their clip's frame, and the network vehicles:
  `NETHUMER` (the driven jeep) with `NETHMTRT` and `NETHMBRL` (its turret and
  barrel), `NETHUMMT` (the parked jeep), `NET_HK`, `NET_HKMT`.
- **Cockpits.** `HUMMER` (the jeep) and `HKCOCKPT` (the HK) are drawn around
  the driver's eye at frame 0; the replacement is lit and hazed as the world
  (the DOS cockpit is drawn flat). Back faces are culled as ever, so from the
  seat only faces turned to the eye show. The jeep's eye is at about
  (-26, 22, 8) in `HUMMER`'s space (the left seat), the HK's at (0, 4, 16).

Only the drawing changes, and only in the enhanced look. Hit spheres, the
segments' hit tests, collision, the AI and its timings, and a segment's
muzzle and tip points all stay the DOS model's: a replacement much larger or
smaller than the DOS model is shot at where the DOS model is. The original
look draws the DOS models.

### Robot template (`NAME.robot.glb`)

`SkyNetEdit --offscreen --export-gltf NAME` (or `model.gltf`) writes a robot
built on model `NAME` twice: `NAME.glb` (the root model alone) and
`NAME.robot.glb`, the template for replacing the whole robot with one skinned
glTF model. Blender opens it as an armature and one mesh. The Model Viewer's
**Export as glTF...** (`robot.gltf TYPE [dir]`) writes the template of the
robot type shown.

**In the editor.** Assets > Robots lists one entry per robot (type id, name),
not its parts; its viewer shows it assembled and plays its blocks.

1. **Export as glTF...**, model it in Blender, export `.glb` or `.gltf`.
2. **Replace with glTF...** picks it: the viewer and the level show it at once
   (ENHANCED look), unsaved; Edit > Undo takes it back. A `.gltf` is packed
   with the files it names into one `.glb`.
3. File > Save writes `models/NAME.robot.glb` into the save mod.
   **Revert to original** (then Save) deletes it.

The viewer's **Clips** table shows which clip each block plays; **Parts**
(collapsed) lists the segments and wreck parts, each opening its `.3D` for a
per-part replacement ([Models](#models)). The file replaces every robot type
built on `NAME`; a `look` ([Robots](#robots)) gives one type its own.

- **Joints.** One per segment, in the order the game assembles them (depth
  first, root first), named `seg<i>_<MODEL>`: `i` the segment's index, `MODEL`
  its `.3D` name in capitals. The T-rex: `seg0_T-REXLEG`, `seg1_T-REXHED`,
  `seg2_T-GUNR`, `seg3_T-GUNL`. Names change only when the robot's
  `segments` change.
- **Rest pose** is the robot as the game assembles it: each joint at its
  segment's offset and angles.
- **Mesh.** Every segment's faces, placed at rest, skinned to their own joint
  with weight 1.
- **Clips.** One per AIS block the robot's script starts, named as the Model
  Viewer names them: `walk` (a walker's loop), `loop`, `fire`, `once`,
  `pose`, numbered when repeated (`once 2`). A robot without blocks but with
  frames gets `frames` (every frame in order, 10 per second); one with
  neither gets none.
- **Frames.** DOS animates by swapping the root model's vertex frames; no
  block moves a segment (turrets aim at run time). So the root's frames are
  morph targets (shape keys `frame 1`...), each clip keys their weights (STEP,
  at the block's fps), and keys every joint at its rest pose: tracks to
  replace with the new model's bone animation. Other segments' frames (a
  machine arm's own script) are not exported.

**In the game.** `models/NAME.robot.glb` in a mod draws every robot built on
`NAME` (its root model, or its `look`) in the enhanced look, in place of the
DOS segments and of their per-segment replacements:

- The file is in the robot's own space, as exported (Y up, ahead −Z, world
  units); no sidecar is read.
- A joint whose name starts `seg<i>_` follows segment `i` (a turret's aim on
  top of the clip's rotation: a joint left at its rest turns as the DOS
  segment does). Other joints move by the clips alone.
- The clip named as the block the robot's script plays runs from the block's
  start (a looping block repeats it); no clip of that name: the rest pose.
  Names match exactly first, then ignoring case, an exporter's `Armature|`
  prefix and `space _ - .` (`Fire_2` plays `fire 2`), then a numbered
  block takes its role's clip (`fire` plays for `fire 1`...`fire 3`).

  | Clip | Plays while |
  |---|---|
  | `walk` | a walker (state 7) loops its walk cycle |
  | `loop` | another robot loops a block (a hover's idle, a machine) |
  | `fire` | a firing pose |
  | `once` | a one-shot block |
  | `pose` | a held pose |

  A robot without blocks plays none; there is no death clip (DOS robots
  explode into their wreck parts).
- Morph targets play as shape keys (positions and normals; tangents stay the
  base's). A clip's weights show, per key, the one shape weighing most (the
  base weighs one minus the weights' sum); LINEAR keys blend
  that shape into the next key's, STEP keys hold. The template's STEP
  one-hot keys play the DOS frames exactly; a key mixing several targets
  shows only its dominant one.
- Materials read as a prop's: base colour, normal, metallic-roughness,
  occlusion and emissive maps; tangents from `TANGENT`, else made per vertex.
- Hit spheres, feet, collision, muzzles and the AI stay DOS's. Wreck parts are
  the DOS segments'.

### Sprite models

`tables/sprite_models.json` draws a sprite as a glTF model of the mod's
`models/sprites/` folder in the enhanced look: the map's billboards (pickups and
scenery) and the sprites destruction drops (and the deathmatch items). A
table row, so SkyNetEdit's Tables grid edits it; no shipped file, the mods'
rows lie over an empty table, keyed by `sprite`.

| column | meaning |
|---|---|
| `sprite` | `"BANK/RECORD"`, the TEXTURE.NNN bank and record: `"214/002"` |
| `model` | the file in `models/sprites/`: `"armour.glb"` (`.glb` or `.gltf`). Kept apart from `models/`, where `DRONE.glb` would replace the DOS model `DRONE.3D` as well |
| `scale` | world units per glTF unit; `0`: fit to the sprite's height |
| `yaw` | degrees about Y |
| `spin` | degrees a second (pickups turn) |
| `bob` | world units up and down, half a bob a second |
| `offsetY` | world units the model is lifted |

```json
{ "table": "sprite_models", "rows": [
    {"sprite": "200/002", "model": "ammo_box.glb", "scale": 0, "yaw": 0, "spin": 90, "bob": 2, "offsetY": 2}
] }
```

- The model's bottom centre stands on the sprite's foot (where the billboard
  touches the ground), turned by `yaw` + `spin` x time, lifted by `offsetY`
  plus the bob. Sprites side by side turn and bob out of step.
- A model without animations is drawn as a prop's replacement (materials as
  in [Materials as drawn](#materials-as-drawn); its sidecar `NAME.json`, if
  any, must read but is not used). A model with animations plays its first
  clip in a loop, every copy in step.
- The original look draws the DOS sprite, pixel for pixel. Picking up,
  collision and the item table are the sprite's, unchanged.
- A row whose sprite or model does not read is logged under `[Mods]` and its
  sprite stays a billboard. A key missing from a row stops the table.
- Not drawn as models: thrown items in flight, impact pieces, effects.

### View-models

`models/viewmodels/NAME.glb` (or `.gltf`) draws the gun in the soldier's hands
as a 3D model in the enhanced look, in place of its DOS art (`WEAPONnn.CFA`).
`NAME` is the weapon's name with underscores for spaces:

| record | NAME | record | NAME |
|---|---|---|---|
| 0 | `PIPE` | 7 | `LASER_RIFLE` |
| 1 | `UZI` | 8 | `LASER_CANNON` |
| 2 | `ASSAULT_RIFLE` | 9 | `PLASMA_PISTOL` |
| 3 | `MACHINE_GUN` | 10 | `PLASMA_RIFLE` |
| 4 | `SHOTGUN` | 11 | `PLASMA_CANNON` |
| 5 | `GRENADE_LAUNCHER` | 12 | `SUPER_UZI` |
| 6 | `ROCKET_LAUNCHER` | | |

- Model the gun as it is held: Y up, **the barrel along -Z** (Blender: along
  +Y, exported with the default +Y up). Its size and origin do not matter: the
  box centre is placed, and without a `scale` the longest side is made 16
  world units (about 40 cm).
- It is drawn through the frame's own camera into a layer of its own, after
  the world and under the HUD panel, so it never sinks into a wall and the
  panel covers its foot as it covers the DOS art's. It is lit as the world is
  around the eye: the map's light and the point lights (muzzle flashes, lamps,
  fires, explosions); emissive parts glow. Materials as in
  [Materials as drawn](#materials-as-drawn). Since the DOS art is full-bright
  and a night map would leave the gun near black, every surface also glows
  with `minLight` of its own base colour (added to its emission; the scene's
  light comes on top).
- A shot's first frame draws the DOS muzzle flare (TEXTURE.219) at `muzzle`,
  in the gun's layer, besides the shot's point light and the flare out in the
  world. Put `muzzle` a little in front of the barrel's end. The pipe has none.
- It moves by itself, from the DOS art's animation clock: a shot kicks it
  back and its muzzle up (fast, then eased out over the shot's animation, so
  held fire keeps it shaking), the pipe swings across to the left and down
  instead, a throw (F1-F5) lowers it, and it sways a little at rest. The
  kick and the swing turn it about `pivot` (the grip, say; the box centre
  without one). The file's own animations are not played.
- `offset` is the placement on a 4:3 frame with the CLASSIC panel. The panel
  takes more of a wide frame's height (its top at 78 % of a 16:9 frame, 83 %
  of a 4:3 one), so on any other frame the gun is lifted by as much as the
  panel's top moved against the view centre, at the gun's depth: its foot
  keeps its place above the panel (with the MODERN HUD, which has no panel,
  it goes down to the frame's foot). A sidecar with `offsetWide` places it
  by hand instead: `offset` at 4:3 and narrower, `offsetWide` at 16:9 and
  wider, a share of each between.
- Not replaceable: the motion detector (its plot is drawn on its art), the
  thrown items (never in hand) and the vehicle guns (the cockpit is the
  vehicle's model). In stereo the DOS art is drawn.
- The original look draws the DOS art, pixel for pixel. A file that does
  not read, or a sidecar that does not, is logged under `[Mods]` and the gun
  keeps its DOS art. Files are read again when the mods are remounted.

The sidecar `models/viewmodels/NAME.json` is optional; every key is
optional, an unknown key is an error (the DOS art stays):

```json
{
  "offset": [6, -9, -24],
  "offsetWide": [6, -8, -24],
  "scale": 0,
  "rotation": [0, 0, 0],
  "pivot": [0, -10, 5],
  "muzzle": [0, 2, -32],
  "flashSize": 6,
  "recoilBack": 2,
  "recoilPitch": 5,
  "swing": 70,
  "sway": 0.25,
  "minLight": 0.35,
  "comment": "free text; also 'source' and 'licence'"
}
```

| Key | Default | Meaning |
|---|---|---|
| `offset` | `[6, -9, -24]` | Where the model's box centre sits from the eye, world units: x right, y up, z ahead is negative. The crosshair's line is x = y = 0. The 4:3 view's (and any narrower). |
| `offsetWide` | none | The placement on a 16:9 view (and any wider), by hand; a view between 4:3 and 16:9 takes the share its width says. None: `offset`, lifted with the panel (above). |
| `scale` | `0` | World units per glTF unit (about 40 for a model in metres); `0`: the longest side is 16 units. |
| `rotation` | `[0, 0, 0]` | Degrees about X, then Y, then Z, applied first (to point a model's barrel along -Z). |
| `pivot` | the box centre | The point the kick and the swing turn about, in the model's own space (the glTF file's coordinates). |
| `muzzle` | none | Where the muzzle flare is drawn, in the model's own space; none: no flare. |
| `flashSize` | `6` | The flare's size, world units. |
| `recoilBack` | `2` | World units a shot pushes the gun back at the kick's peak. |
| `recoilPitch` | `5` | Degrees a shot lifts the muzzle at the kick's peak. |
| `swing` | `70` | Degrees of the pipe's swing (the pipe only). |
| `sway` | `0.25` | World units of the idle sway; `0`: none. |
| `minLight` | `0.35` | 0 to 1: the share of its base colour every surface shows in the dark (added to its emission; materials without maps get them). `0`: lit by the scene alone. |

## Sounds

**One rule:** `sounds/NAME.wav` replaces the DOS sample `NAME` of
`MDMDSFXS.BSA` (`EXPLO1.RAW`, `MOVEDOOR.WAV`: the name without its
extension, any case) wherever the game plays it, in both looks and in
SkyNET and Future Shock alike: weapons, impacts, robots, doors and movers,
sound nodes, ambience, the soldier's voice and steps, the menus' clicks. The
highest mod that has the file wins (mods stack in load order, as textures
and models do). Sounds the DOS game does not have, the weather's, are
replaced in `weather/` instead ([Weather](#weather-enhanced-look)).

- RIFF/WAVE PCM, 8 or 16 bits, mono or stereo, any rate (resampled; the DOS
  clips are 8-bit mono 11025 Hz). No OGG: the engine decodes no compressed
  formats. A file that does not read is logged under `[Mods]` and the DOS
  sample plays.
- The DOS table's own volume and pitch per sound id still apply on top, as
  do the distance fade and the six-voice limit.

`tables/sounds.json` sets how a sample plays. A table row, keyed by
`sample`; no shipped file, the mods' rows lie over an empty table, and a
sample without a row plays exactly as without the table.

| column | default | meaning |
|---|---|---|
| `sample` | - | the DOS name without extension: `"EXPLO1"` |
| `volume` | `1` | gain multiplier, 0-2 |
| `range` | `0` | a placed sound's audible distance in world units, where its fade reaches silence; `0`: the game's own (about 1640 for DOS's placed sounds, 5000 for the port's loops). Sounds without a place ignore it |
| `pitch` | `1` | rate multiplier, 0.25-4 |
| `priority` | `0` | voice stealing: a placed sound takes a playing one's voice only from a lower priority, or from the same priority farther away (DOS's rule); also the mixer's |
| `loop` | `true` | `false`: a sound the game loops (fires, engines, sound nodes, the ambient bed) plays once each time the listener comes near. A one-shot never loops |

```json
{ "table": "sounds", "rows": [
    {"sample": "EXPLO1", "volume": 1.5, "range": 2400, "pitch": 1, "priority": 1, "loop": true}
] }
```

A row has every column. A table that does not read is logged under
`[Mods]` and every sample plays unchanged.

## The example mods (`tools/modkit`)

`tools/modkit/modkit.py` (Python 3, Pillow and numpy, run as `python -I`)
builds two mods on the player's machine. No art is committed: the DOS
records come from the player's data and the photos from a folder of CC0
downloads.

```
# the DOS records as palette PNGs, the colour reference
dotnet run --project tools/SkyNet.Tables -c Release -- textures <game-data> --out <work>/export
# 89 DOS wall and floor records replaced by colour-matched CC0 photos (512 px; ~33 MB)
python -I tools/modkit/modkit.py materials --export <work>/export --src <cc0-textures> --mods <game>/mods
# crates, a mess table and a chair as generated glTF models with CC0 photo textures
# (base colours as JPEG, normal maps, roughness and metallic factors; --no-maps: colours only)
python -I tools/modkit/modkit.py props --src <cc0-textures> --mods <game>/mods
# the gun towers' segments, the jeep's cockpit and the deathmatch jeep
python -I tools/modkit/modkit.py robots --src <cc0-textures> --mods <game>/mods
skynet-sdl.exe --mod cc0-materials,cc0-props,cc0-robots --look enhanced
```

`<cc0-textures>` holds one folder per Poly Haven texture id with its diffuse
map, `<id>/<id>_diff_1k.jpg`, and its OpenGL normal map,
`<id>/<id>_nor_gl_1k.jpg` (the layout the Godot port's `enhanced_pack.py`
downloaded). The props use a roughness map, `<id>_rough_*`, when a folder
has one (the Godot port's download has none: the roughness is a factor per
material, about 0.7-0.85 for wood, 0.45 for the steel, which is also 0.85
metallic). Each mod gets a `LICENSE.txt` listing its
sources. Poly Haven assets are CC0 1.0: free for any use, credit not
required.

`materials --table <file.json>` takes a table of records instead of the
built-in list, each `"T<bank>_<record>"` with a `mode`: `photo` (the photo,
colour-matched, its `id` and real `span` in units), `detail` (the DOS art
smoothly scaled, with the photo's grain and normal plus the DOS art's own
edges as relief) or `blend` (other entries mixed by how the DOS texels look,
`of`). Every record gets relief maps (`RRR_n.png`, `RRR_r.png`): the normal's
tilt is gain-matched (at most 6x) to a mean of `relief` degrees as SURFACE
RELIEF LOW draws it, by default 21 for `blend` and 15 for the others (21 suits
rock, brick and blocks, 10 concrete, 7 asphalt; 0 is flat). `--fill-bank 302`
fills the rest of the ground's bank with the DOS tiles, nearest, with
luminance relief at 13.5 degrees (the atlas takes HD tiles only when every
tile has one).

The props are boxes made in the script (no download) and stretched to each
DOS model's box: `CRATE01`-`CRATE03` (wooden crates, destructible: replaced
until they break), `24MESTBL` (the submarine's mess table) and `CHAIR0S`
(its back slatted with an alpha MASK, two-sided).

`cc0-robots` (about 8 MB) replaces, stretched to each DOS box, the gun
towers' `GUNTWR1` (a concrete tower with steel bands), `HVYTRRT3` and
`HVYTRRT2` (turret heads with a glowing red sensor); in DOS units on the DOS
pivots, `HVYPLSM` (a plasma barrel with cooling rings and a glowing muzzle)
and `HMSLPODR` (a six-tube missile pod), and the jeep's cockpit `HUMMER`
(bonnet, dashboard, windscreen frame and doors around the left seat); fitted
into the DOS box, the deathmatch jeep `NETHUMER`/`NETHUMMT` (chassis, frame,
roll bar, wheels; its turret stays DOS's). All take `"frames": "all"` (these
DOS models have one frame each). They show on MAP.220 (the type-14 towers
around (36200, 740, -39400)) and from the jeep's seat at its start.

### DOS-faithful prop models (`tools/modkit/propkit.py`)

Rebuilds box, cylinder and lathe props on the DOS model's own proportions
(chamfered edges, round where DOS has an octagon, a real I-beam profile) and
textures them with the DOS records: every new triangle takes its texture
coordinates from the nearest DOS triangle, so the art lands where DOS drew
it. Needs numpy, scipy and Pillow.

```
# the DOS models as .glb, one per name
SkyNetEdit --offscreen --export-gltf DMPSTR0 --export-dir <work>/dosglb
# replacements (models/NAME.glb + NAME.json, "fit": "none", "align": "origin") into a mod
python -I tools/modkit/propkit.py --dos <work>/dosglb --hires <hires>/textures --mod <game>/mods/props-a [NAME...]
python -I tools/modkit/propkit.py --list
```

`<hires>/textures` holds `TEXTURE.NNN/RRR.png` per DOS record with its
relief `RRR_n.png` and roughness `RRR_r.png` (the upscaled set; a record
without them is drawn flat). Hand-built models: `DMPSTR0`, `TRCKBOX`,
`TRCKTNK`, `RAIL`, `RAIL01J`, `IBEM256`, `IBEM128`, `LEVHAN01`. The rest
(`GENER0`, `BUTTON01`, `COMPTR9`, `BILBRD2S`, `210FNCE0`, ...) go through
`generic`: per connected DOS part, a flat panel becomes a bevelled slab, a
convex solid is chamfered, a part named round becomes a rod, a non-convex part
keeps the DOS triangles; records listed as `glow` get their bright saturated
texels as an emission map. Painted parts are non-metal so they keep the DOS
brightness.

## Tables

`tables/<table>.json` changes rows of the shipped table of that name
(`data/tables/<table>.json`, [formats/exe-tables.md](formats/exe-tables.md)),
from a folder or a ZIP mod alike.

- A mod's file holds the rows it changes: each row replaces the row of
  its key (`index`; `type` in `impact_pieces`, `va` in the AIS tables,
  `sprite` in `items`, `level` in `difficulty`), a new key adds a row
  after the last. Only `"rows"` is needed; a full copy of the shipped file
  works too. Tables without a key (`start_weapons`, `mission_enemies`,
  `location_names`) are replaced whole.
- The mods lie over each other in load order, row by row: two mods can
  change different rows of one table; for one row the later mod wins.
- A row has every key of the shipped row. A missing key is an error, not a 0.
- `items` (the pickups, SKYNET.EXE 0x35800) and `difficulty` (the four
  multipliers of LOW, MEDIUM, HIGH) have no shipped file: their rows lie
  over the game's own (`PickupTable`, `Difficulty`). An item row must be
  one of the DOS item sprites. `sprite_models` (key `sprite`) and `sounds` (key
  `sample`) lie over an empty table ([Sprite models](#sprite-models),
  [Sounds](#sounds)); `effects` (key `effect`) over the game's own rows
  ([Effects](#effects-enhanced-look)); `skies` and `night_skies` (key `map`)
  over the game's own rows ([Skies](#skies-enhanced-look)).
- SkyNetEdit's **View > Tables** edits weapons, ammo types, pools, impact
  pieces, items, difficulty and robots as grids and saves only the changed
  rows into the save mod (`table.*` commands do the same).

```json
{ "table": "ammo_types", "rows": [
    {"index": 0, "callback": 0, "family": "hitscan", "model": "", "look": 0, "effect": 46720, "damage": 37, "size": 128, "life": 60, "fireSound": 1, "impactSound": -1, "reach": 0, "flags": 0, "pieces": 0, "pieceType": 0}
] }
```
- A table that does not read stops the game with the table, the mod and the
  file, and the place in it: `Table weapons (mymod:tables/weapons.json):
  $.rows[1].viewX (line 9, column 27): expected an integer, found "wide"`.
- Mods without a `tables/` folder leave the shipped tables in effect.

**Robots and the baked maps.** The system ZIP (`mods/skynet.zip`) is always
baked from the shipped tables, so a mod never writes into it. When a mod's
`tables/enemy_types.json` changes what a map block holds (a robot type's
model or child segments), the game does not use that block: it logs
`maps/MAP.NNN.block was baked with other enemy_types than the mod's` under
`[Bake]` and builds that map from the raw files in memory at each entry
(about 0.4 s for MAP.210 in a Debug build). Changing only AI fields
(hit points, speeds, scripts) keeps every block
([formats/map-block.md](formats/map-block.md#tables-and-mods)).

## Robots

`robots/<name>.json` adds a robot type, or changes one of DOS's, as one flat
row over a **base** row of the enemy type table (`ModRobots`): the keys the
file gives replace the base's, a key left out or `null` keeps it. One file per
robot, plain numbers and names, so a grid editor maps it 1:1. The sample mod
`mods-samples/flencer-mk2/` adds type 200, a faster, tougher flencer, and puts
it where MAP.210's first globe stands.

```json
{ "type": 200, "base": 0, "hitPoints": 300, "speed": 350, "turn": 1024, "fireRate": 200 }
```

| Key | Meaning |
|---|---|
| `type` | The type id (0-1023). An id of the table (0-170) changes that row; a higher one adds a type. |
| `base` | The table type the row starts from. Needed for a new type; an existing one is its own base. |
| `model` | The `.3D` file (`"drone.3d"`), from MDMDENMS.BSA or MDMDOBJS.BSA. It also names the type: the walker, flyer and turret rules keyed by model name follow it. |
| `look` | The name its ENHANCED replacement goes by: `models/<look>.glb` (+ sidecar, [Models](#models)) draws this type only; the base model's other users stay as they are. |
| `state` | The AI state (6 hover, 7 walker, 9 flyer, 13 tank, 2 turret...: [formats/ais.md](formats/ais.md)). |
| `hitPoints` | 0-32767. |
| `speed` `near` `turn` | Move speed, approach distance, turn rate (2048 = a full turn a second). A walker's ground speed is its walk cycle's, not `speed`. |
| `axis` `angleMin` `angleMax` `range` | A turret segment's aim axis, limits and range. |
| `altitude` `verticalAccel` `avoidRadius` `forwardSpeed` | A flyer's. |
| `stand` | Stand height (record +0x18): how far the origin goes above the floor it is placed on. |
| `fireAmmo` `fireRate` `fireRange` `fireSpeed` | Ammo type ([formats/exe-tables.md](formats/exe-tables.md)), rate (/128 = shots a second), range, bolt speed. Rate or range 0: it does not shoot. |
| `fireMuzzleX` `fireMuzzleY` `fireMuzzleZ` `fireAimGate` | Muzzle offset and the aim gate. |
| `script` | A table type whose AIS script (and its frame sounds) it runs. |
| `segments` | A table type whose child segments it carries (a turret head, guns). |
| `death` | A table type whose death spec (wreck parts, explosion) it has. |
| `engineSound` | The engine loop's sound. |
| `comment` | Free text. |

- Scripts, segment lists and death specs are DOS data: a robot takes them
  whole from a table type by id (`base`, `script`, `segments`, `death` name
  table types, not other mod robots).
- An unknown key, a value out of range, a new type without `base` or two
  files with one `type` stop the game as a bad table does:
  `Robot mymod:robots/x.json: $.colour (line 1, column 30): "colour" is not a robot key`.
- Files apply in name order, over the table in effect (a mod's
  `tables/enemy_types.json` too). The ids between the table's last and a new
  type are empty. A robot file is part of a demo's mods as the tables are.
- **Models.** A robot's hit spheres, frames, feet and collision always come
  from a `.3D`; a glTF file only draws it, in the ENHANCED look, through
  `look`. A robot made of glTF alone is not possible.

**Placing one.** A map layer ([Map layers](#map-layers)) puts the type on a
robot start marker (sprite 38274: bank 299, marker type 2), with `enemy` up
to 1023, or on a spawn sprite (act `0xF3`, `param16` the type):

```json
{ "op": "set", "key": 50926, "enemy": 200 }
```

DOS's marker byte holds 7 bits; a type above 127 is kept whole by the layer
and the byte (the marker's act) keeps what it had. A new type under an id of
its own leaves every baked block in use (the layer's own edits rebake its
map); changing a table type's `model` or `segments` rebakes as a changed
`enemy_types.json` does. The editor's place palette lists types 0-127 only.

## Retired tables

Two mod tables are gone: the game reads neither, and play is always DOS's. A
mod that still has one loads; the file is ignored, logged once per mount
(`ModContent.Mount`).

- `tables/ai.json` (smart robots): robots are always DOS's.
- `tables/hazards.json` (fires that burn): SKYNET.EXE burns nobody, its fires
  (burning drums, flames, camp fires, the fires a wreck leaves) are scenery
  ([game-logic/damage-hud.md](game-logic/damage-hud.md#other-damage-sources)),
  in the campaign and in a deathmatch alike. Radiation and drowning, which DOS
  does have, never came from this file.

## Materials (ENHANCED look)

`materials.json` in a mod's root says what each DOS texture record is made
of: how lit faces drawn with it shine (`MaterialTable`, `Materials`;
`Renderer.SetSurface`, `Surface`, retroeng repo). A small fixed set of
presets, no material editor:

| Preset | Highlight | Notes |
|---|---|---|
| `matte` | none | Diffuse light only. The default for every record. |
| `stone` | faint, broad | |
| `concrete` | fainter, broader | |
| `wood` | soft, faint | |
| `metal` | strong, tinted by the texel | Half the diffuse light. |
| `painted` | medium, fairly sharp | Lacquer. |
| `glass` | sharp | Also `screen`. Its bright texels glow faintly. |
| `emissive` | none | Its bright texels shine unlit (a lamp's dark housing stays dark). |

```json
{ "default": "matte",
  "banks": { "210": "stone" },
  "textures": { "230/014": "metal", "230/015": { "preset": "painted", "roughness": 0.8, "specular": 1.5 } },
  "patterns": { "23?/01*": "concrete" } }
```

- A record's material is the first of: its `textures` entry (`"BBB/RRR"`),
  the **last** `patterns` entry matching `"BBB/RRR"` (`*` any run of
  characters, `?` one), its bank's `banks` entry, `default`, `matte`. The
  ground's atlas (TEXTURE.302) takes its bank's entry or the default: a
  `textures` or `patterns` entry for one of its records (`302/NNN`) is
  ignored.
- An entry is a preset's name, or `preset` with `roughness` and `specular`
  factors (0 or more, 1 by default) scaling the preset's. Nothing more.
- **Stacking.** The game's own table comes first (`data/materials/skynet.json`,
  or `futureshock.json` in Future Shock mode; embedded), then each turned-on mod's file in
  load order: a later file's `default`, banks and records replace earlier
  ones key by key; its patterns come after theirs (so they win on a tie).
- Comments and trailing commas are allowed; an unknown key or preset, a bad
  record key or pattern makes the whole file not read: logged under `[Mods]`,
  the rest of the stack stays.
- A HD texture's relief map (its roughness, [hires-textures.md](hires-textures.md))
  varies the material's roughness about its value; it gives no highlight to a
  matte record.
- Sprites, replacement models with their own maps (glTF materials), the
  original look and collision never read it. Changes reach a level when it is
  built (the editor applies them at once).

**The shipped table** is a first draft from the DOS colours, then picks by
eye (`MaterialGuess`, its `HandPicks` and `FutureShockPicks`): `SkyNet --gen-materials <file>`
writes it from the player's data and prints each record's guess and colour
figures. Lamps (a few whitish lit texels, the classifier the lamps' lights
use) are `emissive`, green, cyan or blue lights `glass` (screens), brown
textures `wood`, cool, smooth, mid-bright greys `metal`, saturated smooth
ones `painted`, busy greys `stone` or `concrete`; cut-outs (an object's skin)
and the rest `matte`. Of 3265 wall and floor records: 2243 matte, 297
concrete, 203 stone, 190 metal, 138 wood, 73 painted, 63 emissive, 58 glass.
Future Shock's (`--data` its GAMEDATA), of 1846: 1374 matte, 137 metal,
118 concrete, 72 stone, 50 emissive, 44 glass, 27 wood, 24 painted.
A mod's file overrides any of it.

**In the editor**, View > Assets, a texture opened in the viewer: the
**Material** combo writes the record's entry into the save mod's
`materials.json` (`inherit` removes it), Edit > Undo / Redo (Ctrl+Z, Ctrl+Y)
step back and forth with the mission's edits, and the shown map takes the
change at once. The commands `material.get 230/014`,
`material.set 230/014 metal|inherit`, `edit.undo` and `edit.redo` do the same.

## Lights (ENHANCED look)

`lights/shots.json` sets the light a laser or plasma bolt carries as it flies
and the short flash where it strikes (`ShotLightTable`). Without it every bolt
whose model is `LASER*` lights by its ammo record: LASER1 red, LASER2 plasma
blue, LASER3 red-orange; class `vehicle` for a blasting laser (the HK's, the
jeep's), `heavy` for 100 points or more and for every robot's shot, else `hand`.
Every member is optional; a later shot row wins, field by field; a row may also
light a non-laser bolt (a rocket):

```json
{ "classes": { "hand": { "radius": 500, "intensity": 0.7, "impactRadius": 450, "impactIntensity": 1.6, "impactLife": 0.12 } },
  "robotClass": "heavy",
  "shots": [ { "ammo": 6, "model": "LASER1", "colour": [1, 0.3, 0.2], "class": "hand", "radius": 600 } ] }
```

Radii are world units, intensities the light at 150 u, colours sRGB 0-1.

`lights/lamps.json` overrides the lamps (`LampSpots`). An art lamp whose lit
triangles all face one way (a ceiling fitting, a wall fitting, a street lamp's
head) is a shadowless spot along that way, the others, the fires and the MAP
lights are omni. A row matches by any of `map`, `key` (the record's MAP file
offset: an art lamp's mesh, a MAP light's record), `model` (no `.3D`) and
`texture` (a word or `"BANK/RECORD"`; `model` and `texture` never match a MAP
light), all given must match, and sets any of `kind` (`spot` / `omni`),
`direction` ([x, y, z], y up), `cone` and `inner` (half angles, degrees),
`colour`, `intensity`, `range`; rows apply in order:

```json
{ "lamps": [ { "model": "LAMPPOST", "kind": "spot", "direction": [0, -1, 0], "cone": 60, "inner": 30 },
             { "map": 252, "key": 12345, "kind": "omni", "intensity": 0.08 } ] }
```

A file that does not read is logged under `[Mods]` and the defaults stay.

## Weather (ENHANCED look)

Every `weather/*.json` of the mods in use is one weather entry over every
outdoor map (`Weather`). Its `"kind"` says what it brings: `"storm"` (lightning
and thunder), `"rain"`, `"clouds"`, `"haze"`, `"dust"` or `"wind"`. Left out,
the kind is the file's name (`weather/storm.json` is a storm). Kinds combine: a
mod with `storm.json` and `rain.json` brings a rainstorm. A second file of a kind already read is left
out. Without files there is no weather; indoors and in the ORIGINAL look never.

### Storm

`"kind": "storm"` (`StormTable`, `Storm`, `Thunder`). Every key is optional:

```json
{ "kind": "storm", "on": true, "interval": 18, "brightness": 1, "distance": [800, 6000], "thunderVolume": 0.8 }
```

| Key | Default | Meaning |
|---|---|---|
| `kind` | the file's name | `"storm"`. |
| `on` | `true` | `false` turns the storm off (the file stays). |
| `interval` | `20` | Mean seconds between strikes (1 or more). The waits are random (exponential), never under 0.8 s. |
| `brightness` | `1` | The flash's strength (0-4): the sky, the ambient and the light from the bolt. |
| `distance` | `[800, 6000]` | Near and far, in metres: each bolt strikes at a random distance between them. |
| `thunderVolume` | `0.8` | The thunder's gain (0-1), on the Effects bus (SOUND volume). |
| `comment` | - | Free text. |

A strike flashes two or three times in 0.3 s: the sky and the light all around
brighten, the stars wash out, and a strong light from the bolt's way (35 degrees
up) lights the map and throws the sun's shadows. The bolt, a jagged channel with
a few branches, is drawn past the far clip in its direction, sized to look as
far away as it is (its top at the 1500 m cloud base), and fades 0.2 s after the
last flash. A nearer bolt flashes stronger (full up to 1500 m).

The thunder is made in code (filtered noise: a rumble in swells with a body
small speakers play, and under 2000 m a crack before it) and comes after the
sound's travel time: the distance over 343 m/s, at 40 world units a metre (the
soldier's eye is 75 u up). Its gain is `thunderVolume`, full up to 1500 m, then
falling with the root of the distance, never under half (a 6000 m strike is as
loud as a gunshot, a near one as an explosion). It comes from the bolt's side,
both ears hearing it, and pauses with the game. `weather/thunder.wav` (PCM, 8
or 16 bits) plays instead for every strike.

### Rain

`"kind": "rain"` (`RainTable`, `Rain`, `RainHiss`). Every key is optional:

```json
{ "kind": "rain", "on": true, "intensity": 0.7, "windDirection": 30, "windSpeed": 4, "volume": 0.6 }
```

| Key | Default | Meaning |
|---|---|---|
| `kind` | the file's name | `"rain"`. |
| `on` | `true` | `false` turns the rain off (the file stays). |
| `intensity` | `0.6` | How hard it rains (0-1): the streaks' number and the hiss. |
| `windDirection` | `30` | The way the drops are blown, in degrees round the up axis (0 towards +Z, 90 towards +X). |
| `windSpeed` | `4` | The wind in m/s (0-30): how slanted the drops fall. |
| `volume` | `0.6` | The hiss's gain (0-1) at full intensity, on the Effects bus. |
| `comment` | - | Free text. |

Streaks fall at 9 m/s with the wind in a 1000-unit box round the eye (1800 at
PARTICLES LOW, 4500 at HIGH, times the intensity; none at OFF), lit by the
scene's light (a lightning flash too). None falls under cover: a ray down from
high over each 48-unit cell round the eye finds where the rain stops, a roof or
the ground, and no drop shows below it. The hiss (band-passed noise with
pattering drops, a soft low-mid loop: a bed about 20 dB under a gunshot at full
intensity) plays at `volume` times the root of the intensity, a third as loud under a roof with no open sky near the ears, and
pauses with the game. `weather/rain.wav` (PCM, 8 or 16 bits) loops instead.
There is no wet sheen: the materials (`materials.json`) are set per texture,
so a wet look would wet roofs' undersides and floors indoors alike.

A storm with rain is a rainstorm: put `storm.json` and `rain.json` in one mod
(or turn on both sample mods). The flash lights the rain. Rain falls from an
overcast deck: with rain the clouds' coverage is 1 (a deck of the defaults
without `clouds.json`), so no star shows through it.

### Clouds

`"kind": "clouds"` (`CloudTable`, `WeatherSky`, the engine's `CloudLayer`):

```json
{ "kind": "clouds", "coverage": 0.85, "height": 400, "size": 250, "colour": [0.11, 0.1, 0.095], "fireGlow": 1, "drift": 2 }
```

| Key | Default | Meaning |
|---|---|---|
| `coverage` | `0.85` | The share of the sky covered (0-1; 1 overcast). |
| `height` | `400` | The deck's height over the eye (metres, 50-5000). |
| `size` | `250` | A cloud's size (metres). |
| `colour` | `[0.11, 0.1, 0.095]` | Its colour at night (sRGB 0-1), scaled by the map's light around. |
| `fireGlow` | `1` | How much the map's fires near the eye light it from below (0-4). |
| `drift` | `2` | Its drift as a multiple of the wind (0-10); a calm 1.5 m/s without one. |

Value-noise clouds on a flat deck, drawn where nothing else is (over the clear
colour, the moon, a dusk dome; under the rain, the bolt and the far smoke). They
hide the stars behind them and take up to 80 % of the moon's (or dusk sun's)
light. A lightning flash lights them from the bolt's side. Far along the deck
they fade into their mean cover and, at the horizon, into the haze (or the depth
cue's colour). PARTICLES sets the noise's detail (OFF 2, LOW 3, HIGH 5 octaves).

### Haze

`"kind": "haze"` (`HazeAirTable`, `WeatherSky`):

```json
{ "kind": "haze", "colour": [0.17, 0.15, 0.125], "visibility": 260, "density": 1, "horizon": 0.6 }
```

| Key | Default | Meaning |
|---|---|---|
| `colour` | `[0.17, 0.15, 0.125]` | The haze's colour at night (sRGB 0-1); a flash lights it. |
| `visibility` | `60` | Where it hides everything (metres; the far clip at most). |
| `density` | `1` | How much it replaces the palette's haze colour (0-1). |
| `horizon` | `0.75` | How thickly it covers the sky at the horizon (0-1). |

The level's depth cue takes the haze's colour, starts near the eye, thickens
early and is total at `visibility` (held past it). The sky fades into it over
the lowest 10 degrees. No height falloff. Keep it light when there is dust: the
dust clouds close the view in themselves while they pass.

### Dust

`"kind": "dust"` (`DustTable`, `Dust`):

```json
{ "kind": "dust", "amount": 0.2, "colour": [0.42, 0.36, 0.28], "height": 3, "streaks": 0.6,
  "interval": 45, "duration": 24, "peak": 0.8, "size": 45, "rise": 10 }
```

| Key | Default | Meaning |
|---|---|---|
| `amount` | `0.2` | How thick the faint sheets between the clouds are (0-1). |
| `colour` | `[0.42, 0.36, 0.28]` | The dust's colour (sRGB 0-1), lit by the scene's light. |
| `height` | `3` | How high over the ground the sheets and puffs drift (metres). |
| `streaks` | `0.6` | How much fine grit blows past in a cloud (0-1). |
| `interval` | `45` | Mean seconds from one dust cloud to the next (5-600). Each wait is 0.4-1.6 times it. |
| `duration` | `24` | A cloud's life in seconds (3-120): it passes the eye halfway through. |
| `peak` | `0.8` | How thick a cloud's core is (0-1; 1 closes the view to about 6 m). |
| `size` | `45` | A cloud's radius along the wind (metres, 5-200; 1.6 times that across it). |
| `rise` | `10` | How high a cloud climbs over its life (metres, 0-50), spreading and thinning. |

The air is mostly clear. Now and then (every `interval` seconds on average) a
gust raises a dust cloud upwind of the player: it rolls with the wind, reaches
the player halfway through its `duration`, rises, spreads, thins and is gone.
The gust blows with it: the wind (and its howl) is up to 1.9 times its mean
speed at the cloud's peak. While a cloud is over the player the haze (or the
level's depth cue) closes in towards 6 m at a full core and takes the dust's
colour, then opens again; under a roof (the howl's shelter test) and indoors it
does not. Grit streaks (420 at PARTICLES HIGH, 140 at LOW, times `streaks`) blow
only inside a cloud. A cloud is drawn as soft puffs (40 / 14 each, none at OFF;
faded near the eye, where the haze takes over); faint sheets (44 / 16, times
`amount`) drift between the clouds. Every particle sits on the ground (or roof)
of its cell of the rain's cover grid: none under a roof, none indoors. The
editor shows none. The schedule and the clouds use the weather's own clock and
random numbers.

### Wind

`"kind": "wind"` (`WindTable`, `Wind`, `WindHowl`):

```json
{ "kind": "wind", "direction": 60, "speed": 8, "gusts": 0.5, "volume": 0.5 }
```

| Key | Default | Meaning |
|---|---|---|
| `direction` | `60` | The way it blows, degrees round the up axis (0 towards +Z, 90 towards +X). |
| `speed` | `8` | Its mean speed (m/s, 0-30). |
| `gusts` | `0.5` | How gusty (0 steady, 1 wild): the speed swells 0.15-2.2 times, the way wanders up to 20 degrees. |
| `volume` | `0.5` | The howl's gain (0-1) in a 12 m/s wind, on the Effects bus. |

One wind for everything: it slants the rain (in place of the rain's own
`windDirection` and `windSpeed`), blows the dust, drifts the clouds and leans the
ash and smoke. Its howl (noise rung through two wandering band-passes over a
rumble, a stereo loop; about 23 dB under a gunshot) rises in gain and pitch in a
gust and is 40 % as loud under a roof. `weather/wind.wav` loops instead.

### Combinations

- `mods-samples/wasteland/` (clouds, haze, dust, wind; no rain): a low dark deck
  over a light brown haze, a dust cloud rolling through on a gust every 40 s or so.
- wasteland + rain: the deck goes overcast; the rain slants with the wasteland's wind.
- wasteland + rain + storm (`--mod wasteland,rain,storm`): the flashes light the
  deck and the haze from the bolt's side; the bolt shows under the deck.

### Both

The weather is presentation only: its own random numbers and clock, the
collision world only read; the simulation, demos and network games are not
touched (a demo plays back without a desync with it on). It shows in the
campaign, deathmatch, Future Shock and the editor's views (no sound there, the
rain without cover). The console's `weather` says what is on, `storm` prints
the storm's settings, `storm strike` strikes now, ahead of the view
(`--menu-script "wait 20; run storm strike; wait 1; shot flash.bmp; quit"`
takes a screenshot of the flash). `weather gust` raises a dust cloud upwind
now (`--menu-script "wait 90; run weather gust; sleep 12000; shot dust.bmp; quit"`). A file that does not read is logged under
`[Mods]` and left out. The MODS screen says `CARRIES WEATHER`.

The sample mods `mods-samples/storm/`, `mods-samples/rain/` and
`mods-samples/wasteland/` (a release copies them to `mods/`, off until turned on)
carry the files above.

## Effects (ENHANCED look)

`tables/effects.json` sets how the enhanced look's particle effects draw. One
row per effect, keyed by `effect`; no shipped file: the rows lie over the
game's own (`EffectTable`), so without the file every effect draws as before.
A row has every column; a table that does not read is logged under `[Mods]`
and every effect draws as the game's. Rows of an unknown effect are ignored.

| column | game's | meaning |
|---|---|---|
| `effect` | - | the effect's name (below) |
| `low`, `high` | per effect | particles at PARTICLES LOW and HIGH, 0-1024 (OFF draws none) |
| `life` | per effect | seconds one particle (or the effect) lives; `0` where the effect has none |
| `size`, `speed`, `rise`, `spread`, `gravity` | `1` | multipliers over the game's own size, speed, rise, spread and fall |
| `intensity` | `1` | multiplier over brightness (added effects) or cover (smoke, dust) |
| `flicker` | `1` | a fire's flicker swing (0: steady, 2: twice) |
| `r`, `g`, `b` | `1` | a colour filter 0-1 per channel over the effect's own colours; with `intensity` above 1 to keep it bright |

An effect reads only some columns; the others change nothing
(`EffectTable.Reads`):

| effect | what | columns read | game's low/high, life |
|---|---|---|---|
| `fire` | the flame's tongues on fire sprites | low high size speed spread flicker colour | 3/5 |
| `embers` | sparks rising out of fires | low high life size speed spread colour | 5/12, 2.4 |
| `smoke` | puffs over the fires | low high life size speed rise spread colour | 8/16, 6 |
| `smoke_columns` | distant fires' and the horizon's columns | low high life size speed rise spread colour | 10/18, 36 |
| `motes` | ash and dust round the eye | low high size speed colour | 110/280 |
| `explosion` | fireball and white core | low high life size rise spread colour | 7/11, 0.85 |
| `explosion_dust` | the ring of dust blown out | low high life size spread colour | 8/16, 1.6 |
| `explosion_debris` | sparks and debris thrown out | low high life size speed gravity colour | 12/28, 0.9 |
| `explosion_smoke` | dark smoke after it | low high size speed rise spread colour | 6/12 |
| `impact_dust` | a bullet strike's dust | low high size speed rise colour | 4/7 |
| `impact_sparks` | a bullet strike's sparks on metal | low high life size speed gravity colour | 4/8, 0.35 |
| `sparks` | streaks behind flying impact pieces; low/high: more thrown off each | low high life size spread gravity colour | 0/2, 0.45 |
| `lightning` | a storm's bolt ([Storm](#storm)) | size colour | - |

("colour" is `intensity`, `r`, `g`, `b`.) The lights the effects cast (a
fire's lamp, a blast's flash) and the weather's own settings stay as they are:
`weather/*.json` turns weather on and sets its scene (how often, how far, how
much rain), `effects` only how its particles look.

```json
{ "table": "effects", "rows": [
    {"effect": "fire", "low": 4, "high": 7, "life": 0, "size": 1.5, "speed": 1.2, "rise": 1, "spread": 1.3,
     "gravity": 1, "intensity": 1.6, "flicker": 1.5, "r": 0.12, "g": 0.4, "b": 1}
] }
```

The sample mod `mods-samples/big-fire/` burns the fires half again as big,
blue-white, with more and bluer embers.

```
skynet-sdl.exe --mods-dir mods-samples --mod big-fire --map 210 --look enhanced
```

## Skies (ENHANCED look)

`tables/skies.json` (the dusk maps 250-280) and `tables/night_skies.json` (the
night maps 210-240) set the enhanced look's sky per map. A row of a decade's
first map (`250`) covers the decade; a row of an exact map (`251`) wins. The
original look keeps the DOS sky.

| `skies` key | default | what |
|---|---|---|
| `horizonR/G/B` | `0.44 0.1 0.06` | the horizon and the haze the far terrain fades into (sRGB 0-1) |
| `lowR/G/B` | `0.36 0.05 0.035` | the deep red about 12 degrees up |
| `zenithR/G/B` | `0.09 0.018 0.03` | straight up |
| `glowR/G/B` | `1 0.52 0.2` | the sunset's glow (round the sun, along the horizon under it) |
| `sunElevation` | `3` | the glow's height (degrees, -5 to 45); the light keeps its own |
| `glowWidth` | `38` | the glow's half width along the horizon (degrees) |
| `cover`, `cloudR/G/B` | `0.3`, `0.06 0.02 0.02` | the cloud deck's cover (0-1) and colour |

| `night_skies` key | default | what |
|---|---|---|
| `stars` | `1` | faint stars, a multiple of 2200 (0-4); the DOS 256 always show |
| `starBrightness` | `1` | all stars' brightness (0-4) |
| `milkyWay` | `1` | the Milky Way band's strength (0-4) |
| `cover`, `cloudR/G/B` | `0.22`, `0.02 0.022 0.03` | the night clouds' cover (0-1) and colour |
| `moonGlow` | `1` | the moon's halo (0-4) |

```json
{ "table": "night_skies", "rows": [
    {"map": 230, "stars": 2, "starBrightness": 1.3, "milkyWay": 1.5, "cover": 0, "cloudR": 0, "cloudG": 0, "cloudB": 0, "moonGlow": 0.5}
] }
```

The moon's face is the LRO near side (NASA SVS 4720, public domain; see
THIRD-PARTY-NOTICES.txt).

## Map layers

`maps/MAP.NNN.layer.json` changes what a map holds: its objects, lights,
sprites, markers, triggers and the links of their action chains. The DOS
map stays untouched; the game parses it and applies the layer's edits on
top, and everything that reads the map (behaviour, the baked block,
collision, robots, the player start, saves) reads the edited one. A
mission's mission-wide things (map list, objectives, music, start kit) stay
in the mission file; what belongs to a map belongs in its layer.

```json
{ "format": "skynet-layer", "version": 1, "base": "MAP.210",
  "edits": [
    { "op": "set",    "key": 30707, "x": 1200, "y": -40, "z": 5000, "yaw": 512 },
    { "op": "set",    "key": 30839, "hitPoints": 4, "act": 24, "next": 31121 },
    { "op": "delete", "key": 39012 },
    { "op": "add",    "key": 16777217, "variant": 1, "name": "CRATE04", "x": 0, "y": 0, "z": 0 }
  ],
  "names": [ "EXTRA" ] }
```

- **Keys.** An entity is keyed by its MAP file offset (decimal), the key
  behaviour state, saves, mission links and the trigger lock already use.
  An added entity takes a key of 16777216 (2^24) or more, written into the
  file, so saves and links to it stay stable.
- **Ops**, applied in order: `set` changes the fields it lists on an entity
  the map (or an earlier `add`) has; `add` makes a new one (it needs a
  `variant`: 1 mesh, 2 light, 3 sprite or marker); `delete` (only `op` and
  `key`) removes one. A key that is missing, or taken for an `add`, is an
  error.
- **Fields** (integers, DOS units and 11-bit angles): `x` `y` `z`, `pitch`
  `yaw` `roll`, `variant`, `flags` (raw byte), `actor` (true/false),
  `cellX` `cellZ` (default: the 1024-unit cell of `x`/`z` when they change),
  `name` (a name slot, added when new) or `nameIndex`, `hitPoints`, `state`,
  `act`, `next` (the next record's key on the action chain), `destroyType`
  `destroyParam` (a mesh's link record), `light` `lightEnable`, `sprite`
  (bank × 128 + record; bank 299 is a marker), `enemy` `convoy` (a marker's
  robot type: 0-127, or a mod robot's up to 1023, [Robots](#robots)),
  `param16` `param8`. A marker's robot type and its act are the same DOS
  byte: setting `enemy` (0-127) or `convoy` without `act` sets both.
- **Triggers and chains.** A mod may add trigger records and whole new
  chains: add the records and point their `next` (and the first one's
  source) at each other by key.
- `names` adds name slots after the map's own.
- A layer with no edits and no names plays the map as its DOS file and
  keeps the baked block. With edits, the system ZIP's block is not used:
  the map bakes in memory with the layer (logged under `[Bake]`), or a
  mod's own `maps/MAP.NNN.block` is used when it was baked with that very
  layer file (its `LAYER:` input matches).
- A layer that does not read, names another base or does not fit its map
  (a missing key) is logged under `[Mods]` and the map plays as its DOS file.
- The highest mod in the load order that has the layer wins; layers do not
  merge.

**Named layers.** A mission instance's `"layer"` is `"MAP.NNN"` or the
name of a layer file (`"layer": "GATE2"` reads `maps/GATE2.layer.json`), a
second edited copy of one map. Its block must be the layer's base map
(`"block": "MAP.210"` for a layer whose `base` is MAP.210).

## Terrain overrides

`maps/WLD.NNN` is a whole WLD file (DOS format, [formats/wld.md](formats/wld.md))
standing in for the data folder's `WLD.NNN`: MAP.NNN's drawn ground,
collision, spawn heights and the editor all read it. SkyNetEdit's terrain
tools save it (`TerrainEdit.Save`). The highest mod that has it wins; a
file the game does not read as a WLD (not 256 × 256) is logged under
`[Mods]` and the DOS ground stays. A baked block made on another ground
(the system ZIP's) is not used while a mod changes it: the map bakes in
memory (`[Bake] ... was baked with another WLD.NNN`).

Code: `SkyNet.Game.Mods.MapTerrains`, `GameData.WldResolver`, the bake input
`WLD.NNN` (`BakeInputs`), `BlockStore.Load`.

## New maps and missions

A mod adds maps and missions in the DOS formats:

| File | What |
|---|---|
| `maps/MAP.NNN` | a new map (DOS MAP format), under a number the map archive lacks: a DOS map is never replaced, its layer edits it |
| `maps/WLD.NNN` | its ground (an outdoor map), as above |
| `missions/MNN.json` | a new mission ([formats/mission.md](formats/mission.md)), under a number the mission table lacks |
| `briefings/NNN.TXT` | the briefing its `game.briefing` names when MDMDBRIF.BSA lacks it: `[EN]` the objective text, `[M1]`..`[M5]` the counter |

- **Numbers.** New maps take 720-799 and new missions 20-99: free in SkyNET
  (maps 200-696, missions 1-8) and Future Shock (maps 001-719, 803, 900,
  957; missions 1-19). The game reads any number the data lacks; the
  editor refuses one the data or any mod in the folder has.
- **Listed.** The mods turned on add their missions after the table's
  (`MissionTable.Playable`): `--mission N`, the console's `mission`, saves.
  No campaign reaches them (the table's last mission still ends it).
- **Played** as any mission: from the start instance, doorways by their
  links, the counter from the briefing; a map with no baked block bakes in
  memory at entry.
- **SkyNetEdit**: File > New Map... (number, outdoor or interior, size,
  flat or generated ground, height, ground tile, maptype; an interior copies
  another map's walls and props, MAP.211 / MAP.011 by default) writes
  `maps/MAP.NNN` (+ `WLD.NNN`) into the save mod and adds it to the open
  mission. File > New Mission... makes one or more new maps, each ending in
  a doorway (0xF0, use key) to the next, the last in a pad (0xF1) whose
  chain counts the briefing's one objective, and the mission file and
  briefing, then opens it. Commands: `map.new [N] [outdoor|interior]
  [flat|generated]`, `mission.new [N] [outdoor|interior ...]`.

Code: `SkyNet.Game.Mods.ModMaps`, `ModMissions`, `GameData.MapResolver`,
`MapWriter`, `SkyNet.Edit.NewMaps`, `NewMissions`.

## Open points

- Replacement models only: HD textures of DOS records take no normal,
  roughness or emissive maps.
- BLEND surfaces sort by their model's origin as one draw each, not per
  triangle; a model's blended parts can overlap wrongly among themselves.
- Point lights give physical materials no highlight; an emissive surface
  does not light its surroundings (the DOS lamps' point lights do).
- One texture coordinate set; `KHR_texture_transform` is not read.
- A robot segment's replacement is a static mesh per DOS frame or frame
  range: no glTF animation, skin or morph target, no blending between frames
  (DOS has none either). Its hit spheres and points stay DOS's. A whole
  robot's `NAME.robot.glb` is skinned and animated; its morph targets blend
  two at a time (each key's dominant target).
- The sky dome is not replaceable. A 3D view-model has no hands unless the
  model has them.
