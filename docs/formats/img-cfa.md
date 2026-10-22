# IMG images and CFA cel animations

Front-end and HUD art: `.IMG` still images (menus, briefing portraits,
HUD panels) and `.CFA` animations (weapon viewmodels, radiation counter,
network HUD). Mostly in `MDMDIMGS.BSA`; the 640 × 480 set in
`MDMDHRES.BSA`. Palette per screen, see [palette.md](palette.md).

Godot port: `scripts/loaders/img_file.gd`, `scripts/loaders/cfa_file.gd`.
C#: `SkyNet.Formats.ImgFile`, `SkyNet.Formats.CfaFile` (palette indices, both layouts).

## IMG

| Offset | Size | Type | Meaning |
|---|---|---|---|
| 0 | 2 | u16 | x offset (screen placement) |
| 2 | 2 | u16 | y offset |
| 4 | 2 | u16 | width |
| 6 | 2 | u16 | height |
| 8 | 2 | u16 | compression (0 = raw) |
| 10 | 2 | u16 | record size = w·h (pixel byte count) |
| 12 | w·h | u8[] | palette indices, row-major (compression 0) |

Every menu, title and panel image the port uses is uncompressed. Cockpit
panels use index 0 for the see-through windscreen.

Checked on all 218 SkyNET IMGs (181 in MDMDIMGS.BSA, 37 in MDMDHRES.BSA):
every one has compression 0, record size = w·h, and is exactly
12 + w·h bytes long. The compressed variants (compression ≠ 0) do not
occur in SkyNET's data and are not decoded.

Hi-res (`MDMDHRES.BSA`) images are about 2× per axis (PANEL0.IMG 640 × 96
against 320 × 40).

## CFA, 16-bit layout (320 × 200 art)

| Offset | Size | Type | Meaning |
|---|---|---|---|
| 0 | 2 | u16 | frame width |
| 2 | 2 | u16 | frame height |
| 4 | 2 | u16 | width again (equal to +0 in all 98 files) |
| 6 | 2 | u16 | UNCERTAIN, probably screen x |
| 8 | 2 | u16 | UNCERTAIN, probably screen y |
| 10 | 1 | u8 | 8 in all 98 files (bits per pixel?) |
| 11 | 1 | u8 | frame count |
| 12 | 2·n | u16[] | absolute file offset of each frame |

Each frame is one byte stream that expands to exactly `w × h` indices,
row-major:

```
c < 0x80   -> (c + 1) literal bytes follow
c >= 0x80  -> run: the next byte repeated (c - 0x7F) times
```

Index 0 is transparent.

Checked on all 98 16-bit CFAs (86 in MDMDIMGS.BSA, 12 in MDMDHRES.BSA,
468 frames): every frame stream expands to exactly `w × h` and ends
exactly at the next frame's offset (the last at the end of the file).

## CFA, 32-bit layout (640 × 480 art, MDMDHRES.BSA)

| Offset | Size | Type | Meaning |
|---|---|---|---|
| 0 | 4 | u32 | x |
| 4 | 4 | u32 | y |
| 8 | 4 | u32 | width |
| 12 | 4 | u32 | height |
| 16 | 24 | | zero (all 14 files) |
| 40 | 4 | u32 | frame count n |
| 44 | 4·n | u32[] | frame offsets |

**Telling the layouts apart:** in this layout the first frame offset is
always `44 + 4·n`. There is no version byte, and both layouts occur in
the same archive (RADCOUNT.CFA and TNET*.CFA in MDMDHRES.BSA are 16-bit).

Frame body: row-based. Per row, pairs `(u8 skip, u8 count)` followed by
`count` palette bytes, until the row covers the width. A blank row is
`(width, 0)`. Verified on all 14 hi-res WEAPON*.CFA (73 frames).

Hi-res frames cannot be scaled by width alone: some hi-res viewmodels are
narrower than their low-res counterparts. Track which set a frame came
from.

## Open questions

- IMG compression modes (none occur in SkyNET's data).
- 16-bit CFA bytes 6..9 (placement?) and byte 10.
- Viewmodel screen placement rules (x/y fields vs. code constants).
