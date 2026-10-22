# FONT00NN.FNT bitmap fonts

XnGine bitmap fonts `GAMEDATA/FONT0000.FNT` .. `FONT0011.FNT`. Every file
is exactly **8644 bytes**.

Godot port: `scripts/loaders/fnt_font.gd` (`FntFont.build(bytes, scale)`
returns a Godot `FontFile`). C# port: `SkyNet.Formats.FntFont`.

SKYNET.EXE v1.01 loads all twelve at start-up (FUN_00117400 calls the
loader FUN_00146728 for 0..11 through the name pattern `????.fnt` at
0x63304) and selects one by number (FUN_00146750; slots at 0x63354).
The 320 × 200 HUD draws its counts, message box and status lines in
**FONT0004**.

### The 640 × 480 fonts (FONT0008-0011)

FONT0010 (and 0008, 0009, 0011) use **exactly this layout**; they parse
like the others (the C# test `The_hi_res_hud_font_parses` checks
FONT0010 on real data). Earlier notes called FONT0010 "not decoded"
because nothing had found where DOS uses it, not because of its bytes.

In hi-res mode (`SKYNET.EXE /g`, flag 0x20000 of [0x30a50]) the HUD's
font setter FUN_0012ad00 maps the requested number through the dword
table at **0x4d000** before selecting it:
`{8, 9, 0, 0, 10, 0, 0, 11, 0, …}` (0 = keep the number), so
0 → FONT0008, 1 → FONT0009, **4 → FONT0010**, 7 → FONT0011. The HUD
text (font 4) is therefore FONT0010 in 640 × 480: 12 × 11, glyphs one
640 × 480 pixel each. Its "5" is 10 wide with a solid two-row top bar
(FONT0004's is 5 × 5). C#: `HudFont.HiResFont`.

## Layout

| Offset | Size | Type | Meaning |
|---|---|---|---|
| 0x00 | 2 | u16 | em width (max glyph width) |
| 0x02 | 2 | u16 | line height (≤ 16) |
| 0x04 | 960 | | 240 glyph entries × `{u16 data_offset, u16 width}` |
| ... | | | glyph bitmaps |

- Entry 0 is codepoint **0x21 ('!')**; the table covers 0x21..0x110.
- Space (0x20) has no glyph. FUN_0014691c returns `em width − spacing`
  for it (spacing [0x63348] = 1) and a glyph's width otherwise; the
  callers add the spacing, so a space advances the em width and a glyph
  its width + 1.
- `data_offset` is a file offset. In all 12 shipped fonts entry *i* points
  at `0x3C4 + 32·i` (the bitmaps follow the table in order), and the em
  width equals the widest glyph.
- A glyph advances by its width + 1 (Godot port).

Shipped headers (em width × line height): FONT0000 5×7, 0001 5×6,
0002 6×6, 0003 8×8, 0004 6×5, 0005 12×13, 0006 6×6, 0007 13×9,
0008 10×14, 0009 10×12, 0010 12×11, 0011 16×16.

**Lower case**: FONT0000-0004, 0006 and 0008-0010 carry all 26
lower-case letters; FONT0005, 0007 and 0011 have none (0011 has one
slot). The Godot port draws an empty lower-case slot with its capital.
Every font except FONT0005 has glyphs above 0x7E (28-73 of them).

## Glyph bitmap

`line_height` rows, one u16 per row, 32-byte stride per glyph (16 rows
max). Pixel at column *c* (0 = left) is set when bit `(15 − c)` of the
row word is 1. In byte terms the **high byte holds the leftmost 8
columns**, so the stored little-endian word must be read as a u16 first.

Glyphs are at most 16 pixels wide; a wider entry is corrupt.

## Notes

- No outline or shadow is stored; the HUD draws a drop shadow if needed.
- Colour comes from the caller; glyphs are 1-bit.

## Open questions

- How SKYNET.EXE maps codepoints above 0x7E (the table reaches 0x110).
  The glyph blitter is FUN_00146844 (one character, colour [0x67a08]),
  strings go through FUN_001467cc.
