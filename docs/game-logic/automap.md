# Automap

The full-screen 3D map on the CONTROLS AUTOMAP key (slot 17, Tab). Decoded
from SKYNET.EXE v1.01; C# port: `SkyNet.Game.Automap` (`AutomapSeen`,
`AutomapIndex`, `AutomapCamera`, `AutomapScreen`, `AutomapView`) and
`SkyNet.App/AppHost.Automap.cs`.

## Opening

`FUN_0011ad8a` (the per-frame key reader) calls the automap `FUN_0013dc00`
when slot 17's key is down, only outside a network game. The automap runs its
own loop until Esc or the automap key: the game is paused. Port: campaign
play only (not in a deathmatch, not while dead or after the mission ended).

## Seen tracking

- Storage: flags bit **0x80** of the map entity (+20). A runtime flag, so no
  shipped map has it set ([map.md](../formats/map.md)).
- `FUN_00136fb1`, after each game frame's render, walks the rendered-object
  list and sets 0x80 on every object at least one face of which the face
  rasteriser (`FUN_00164142`) drew. No extra distance test; the view's far
  clip applies. Face type 16 never marks. The automap's own render never marks.
- Cleared on every map load (`FUN_00136c54` from MapStart: new game, mission
  start, map change, load) and by the cheat **`clearautomap`** (internal id
  `ckort`), which prints `Automap cleaered.` (sic, 0x612d1).
- DOS saves do not keep it (the load goes through the map-load clear).

Port: each frame the app marks the meshes whose world box the view frustum
holds within the game's far clip (indoors none), `AutomapIndex.Mark`; no
occlusion test, as DOS's painter has none. The set is kept per current map
in `GameSession.Seen`, wiped on every map entry and by `clearautomap`. Unlike
DOS, a save keeps it (save format 9).

## What is drawn

- Seen entities that are variant 1 (mesh) and not actors (0x40): no robots,
  lights, sprites, pickups or markers. Doors and lifts where they stand now.
- Only records whose **home cell** (x >> 10, z >> 10) is in the 5 × 5 cells
  round the eye kept at opening (clamped at the map's edges, not shifted).
- Outdoors (map type 1) the whole terrain, not limited by seen or the window;
  no sky.
- ARROW.3D (green, palette 0xB6) 40 units under the kept eye, turned the
  soldier's way; it does not move while the map is open.
- Normal filled polygons, no haze (`0x14a63c(-1)` installs a no-op cue), far
  clip 0xC0000. Face mode by RENDER DETAIL (table 0x61a00: 4, 4, 8): HIGH
  textured as in game; LOW / MED (mode 4, `FUN_00150000`) fill each textured
  face with one palette index, **byte +1 of its TEXTURE.NNN outer record**
  (precomputed in the file; TEXTURE.013 records 0-7: 139, 139, 141, 140, 124,
  94, 141, 121; many records hold 0). Flat-colour faces look the same in both.
- One extra directional light, indoors too: `FUN_0014d7f7`(−0x400, 0x2000,
  0x1000) → unit (−0.109, 0.873, 0.436) in DOS axes, added by `FUN_0011b933`
  as type 8 (no falloff) with intensity 24 of 32. The map's variant-2 lights
  are not added. (In game only outdoor maps get a sun: `FUN_0013a132`,
  (−0x1900, 0xFA0, −0xC80), intensity 13.) A lit face is one shade-table row.

Port: `AutomapLight` shades each vertex of the placed meshes to a row,
(ambient + 24/32 · max(0, n · towards the light)) · 63, the ambient's share
0.5 a guess; `AutomapView` draws them out of the haze (`Material.Unfogged`)
and at LOW / MED swaps a textured part for its record's colour
(`TextureRecord.Colour`).

## Camera

On open (`0x13e137`): the kept eye, and the offset (−forward.x, 1 up,
−forward.z) of the view's forward: behind and 45° above for a level look.
Each frame (`0x13e092`) the offset is turned by the orbit, scaled by the
distance and added to the kept eye; the look is the soldier's yaw plus the
orbit, pitched down by its own angle. Up / Down change only the look.

| Key | Action | Rate (per second) | Limit |
|---|---|---|---|
| Left / Right | orbit ± | 256 units (45°) | none |
| Up / Down | look pitch ± | 256 units | 0x80..0x180 (22.5°..67.5°), start 0xC0 |
| keypad + or = | distance − | 250 | min 0x100 |
| keypad − or - | distance + | 250 | max 0x300, start 0x200 |
| Esc, the automap key | close | | |

The distance is a static: kept from one opening to the next. Rates are per
second (`DAT_00043200` = 65536 / fps).

## Screen

All in MDMDIMGS.BSA, drawn in the game palette:

| Art | Place | |
|---|---|---|
| MAPBAR1.IMG | 0,0 320 × 17 | the buttons: ↺ ↻ ↑ ↓ + − EXIT |
| MAPGRID.IMG | 0,17 320 × 166 | graph paper behind the 3D |
| MAPBAR2.IMG | 0,183 320 × 17 | plain stone |
| MAPBTN.CFA | 0,0, 7 frames | the hovered button lit |

The 3D window is the grid's: `FUN_0014fbd0`(half 160 × 83, centre 160,100),
so the scene stays between the bars. Mouse buttons (table 0x5f2c6): x ends
47, 93, 139, 185, 231, 277, 320; held, a button acts every frame. No text
but the normal message line.

## Port bindings

Actions `Automap` (Tab, pad R3), `Automap turn left/right` (Left/Right, pad
d-pad), `Automap tilt up/down` (Up/Down, d-pad), `Automap zoom in/out`
(keypad + and =, keypad − and -; pad RB / LB). Pad B or Esc closes. Console:
`automap` opens it, `clearautomap` / `ckort` clears it.

## Open questions

- The ambient (`DAT_00064b11` = 0x1E00) and the light's exact response; the
  shade row selector of the .3D face (byte +1).
- Whether the terrain also goes flat at LOW / MED (`FUN_00153b00`); the port
  keeps it textured.
