# Palettes (.COL) and HAZE.000

Everything is drawn in 256-colour mode through one active palette.

Godot port: `scripts/loaders/palette.gd`; palette selection in
`scripts/skynet_paths.gd` (`palette_bytes`, `col_bytes`,
`ui_palette_bytes`); depth cue in `main.gd` / `settings.gd`.

## .COL layout

| Size | Layout |
|---|---|
| 768 bytes | 256 × {u8 r, u8 g, u8 b} |
| 776 bytes | 8-byte header (UNCERTAIN contents) + 256 × RGB |
| other | the port takes the last 768 bytes (fallback, UNCERTAIN) |

Components are VGA DAC values, 6-bit (0..63). If every byte is ≤ 63,
expand to 8 bits with `(c << 2) | (c >> 4)`; otherwise treat them as
8-bit already.

Index 0 is transparent for sprites, CFA frames and cut-out IMG panels; it
is an ordinary colour elsewhere.

## Which palette

| File | Game | Use |
|---|---|---|
| SKYNET.COL | SkyNET | in-game world and HUD |
| BRIEF.COL | both | briefing and UI screens |
| MENU.COL | SkyNET | menus |
| START.COL | both | title and menus (Future Shock) |
| SKYNTRMP.COL | SkyNET | paired with SKYNTRMP.IMG |
| SHOCK.COL | Future Shock | in-game |

SkyNET keeps its palettes inside the archives; Future Shock ships them as
loose files. In Future Shock `LOGOMENU.COL` is not the menu palette.

## Fixed indices

| Index | Use |
|---|---|
| 0x11 | night sky fill (outdoor maps without the dusk dome) |
| 31 | depth-cue (haze) colour: HAZE.000 row 0 maps every colour here |

## HAZE.000

A 256 × 64 colour-remap table: 64 rows of 256 bytes. Row *s* maps each
palette index to its shaded version at depth-cue step *s*. Row 63 is full
brightness (UNCERTAIN: direction). Row 0 maps **every** colour to index 31
(black in the SkyNET palette); checked on the shipped HAZE.000, all 256
entries.

Outdoors, from the depth-cue start distance to the far clip, the shade
falls linearly through the 64 rows to row 0 (v1.01 `FUN_0014a63c` takes
the cue start).

### RENDER DETAIL table (v1.01 0x61a00)

Three 20-byte rows, indexed by the RENDER DETAIL setting
(`FUN_00143300` / `FUN_00143314`):

| Field | LOW | MED | HIGH | Meaning |
|---|---|---|---|---|
| +0x00 | 1176 | 1944 | 3992 | far clip (stored `<< 8` as `DAT_0006e224`) |
| +0x04 | 768 | 1792 | 4400 | read by nothing in v1.01 |
| +0x08 | 720 | 1744 | 1744 | depth-cue start |
| +0x0C | 4 | 4 | 8 | selects the span renderers |
| +0x10 | 1 | 2 | 2 | 1 drops a pass on LOW |

Every terrain vertex, polygon and sprite deeper than the far clip in
camera Z is dropped (outcode 0x20 at `FUN_00153dc8`, `FUN_0014ef20`).

A different table at the v1.00 address 0x61800 gives other numbers (far
1408/2176/2432, cue 720/1744/2000); those are not the v1.01 values.

## Other lookup files

`SHADE.000`..`SHADE.007` and `HAZE.005` exist in GAMEDATA. Their use is
not decoded (UNCERTAIN; believed to be light/distance tables for meshes
and sprites).

## Open questions

- The 8-byte header of 776-byte palettes.
- Row direction of HAZE.000 (which end is unshaded) should be confirmed
  against the code.
- SHADE.NNN and HAZE.005.
