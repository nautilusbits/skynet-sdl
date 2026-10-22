# TEXTURE.NNN texture banks

A TEXTURE file is a bank of up to 256 records: wall/terrain textures,
sprites (billboards, pickups, effects) or flat colours. Pixels are 8-bit
palette indices ([palette.md](palette.md)).

Godot port: `scripts/loaders/texture_nnn.gd`, cached via
`scripts/loaders/texture_cache.gd`.

DOS: texture fetch v1.01 0x149e00; flat-colour faces skip texture set-up
at v1.01 0x15040e; (legacy) wall row addressing `sub_1345C2`.

## Who references a bank

| User | Encoding |
|---|---|
| .3D face | face type: bank = `type >> 7`, record = `type & 0x7F` |
| MAP variant-3 sprite | sprite index: bank = `>> 7`, record = `& 0x7F` (bank 299 = marker, see [map.md](map.md)) |
| Terrain | TEXTURE.302, record = WLD layer-2 id (see [wld.md](wld.md)) |

## File header

| Offset | Size | Type | Meaning |
|---|---|---|---|
| 0 | 2 | u16 | record count (0 = invalid) |
| 2 | 16 | char[16] | bank name, space-padded |
| 18 | 8 | | padding / UNCERTAIN |
| 26 | 20·N | | outer record table |

Outer record (20 bytes, record *r* at `26 + 20·r`):

| Offset | Size | Type | Meaning |
|---|---|---|---|
| +0 | 1 | | UNCERTAIN |
| +1 | 1 | u8 | palette index: the colour of a flat-colour record; for a texture the one colour DOS's flat face mode fills it with (automap at LOW / MED; 0 in many records). `TextureRecord.Colour` |
| +2 | 4 | u32 | descriptor offset (0 = flat colour, no descriptor) |
| +6 | 14 | | UNCERTAIN |

The last record's table entry may end exactly at the end of the file
(TEXTURE.000/001): do not require all 20 bytes.

## Descriptor (28 bytes)

| Offset | Size | Type | Meaning |
|---|---|---|---|
| +0 | 4 | u32 | id / hash (UNCERTAIN) |
| +4 | 2 | u16 | width |
| +6 | 2 | u16 | height |
| +8 | 2 | u16 | mode flag (UNCERTAIN) |
| +10 | 4 | | UNCERTAIN |
| +14 | 4 | u32 | pixel data offset, **relative to the descriptor** |
| +18 | 2 | u16 | row gap |
| +20 | 2 | u16 | depth = frame count (> 1 = animated) |
| +22 | 2 | u16 | milliseconds per frame |
| +24 | 4 | | UNCERTAIN |

The row gap selects one of two pixel layouts.

## Layout A: wall / terrain (row gap ≠ 0)

Rows are stored at a stride of `width + row_gap`, typically
**256 = 64 + 192**. Up to **four records share one interleaved buffer**:
each 256-byte row holds one row of each of four 64-wide records. Row *y*
of a record starts at `pix_off + y · stride`.

UNCERTAIN: that each of the four records' `pix_off` is the shared buffer
plus 64·k. The port only relies on each record's own `pix_off` and the
stride.

**Special case:** a 256-wide wall texture has row gap 0, like a sprite.
Treat a record as layout A at stride `width` when width = 256, the raw
`w × h` block fits in the file, and the sprite frame table does not make
sense (first frame offset < depth·4, or the frame header's width ≠ 256).
Example: a 256 × 128 record in TEXTURE.266.

## Layout B: sprite (row gap = 0)

At `pix_off`: `u32 frame_off[depth]`, each relative to `pix_off`. Frame
*k* starts at `pix_off + frame_off[k]`:

| Offset | Size | Type | Meaning |
|---|---|---|---|
| +0 | 2 | u16 | frame width |
| +2 | 2 | u16 | frame height |
| +4 | ... | | pixel data, one of two forms |

The frame's byte size is the distance to the next frame (for the last
frame, to the end of the file).

- **Uncompressed:** size = `h · (w + 2)`. Each row = 2-byte X-range prefix
  + `w` pixel bytes.
- **Transparency-run (usual):** per row, repeat
  `(u8 transparent_run, u8 opaque_run, opaque_run literal bytes)` until
  `w` columns are covered. Transparent pixels are index 0. Every row
  consumes its bytes exactly on all shipped sprite banks.

Index 0 is transparent for sprites.

The literal/run codec used by CFA files ([img-cfa.md](img-cfa.md)) is
**not** used here; it overruns every compressed sprite.

Multi-frame sprites hold whole cel animations in one record (explosion
banks TEXTURE.358 / 367).

## Flat colours (TEXTURE.000 / TEXTURE.001)

These two banks have 128 records each with descriptor offset 0. The
record's byte +1 is its palette index: record *r* of TEXTURE.000 is colour
*r*, of TEXTURE.001 colour 128 + *r*. So a face type ≤ 0xFF **is** a
palette index, and DOS draws it flat without any texture set-up (v1.01
0x15040e). The port synthesises a 1 × 1 texture.

Reading byte +19 of the record instead (the next record's byte +1) shifts
every colour one palette step and is a known bug.

## Animation

`depth > 1` is the only animation flag. When a caller asks for frame −1
(every world draw does: map billboards v1.01 0x136f28, mesh faces
0x134651), the fetch picks

```
frame = (ms_clock / ms_per_frame) % depth
```

from one free-running millisecond counter (v1.01 0x149e6f). All copies of
a record animate in lockstep, each record at its own rate.

## Notable banks

| Bank | Content |
|---|---|
| 000, 001 | flat colours |
| 046 | dusk sky dome texture (used by SKY_SKY.3D) |
| 200, 201, 214 | pickup sprites |
| 299 | not a bank: marker id in MAP sprite indices |
| 302 | terrain tiles |
| 358, 367 | explosion animations |
| 359 | moon sprite (record 2) |

Future Shock's sprite banks are byte-identical to SkyNET's except
TEXTURE.222.

## Open questions

- Outer record bytes other than +1/+2, descriptor +0/+8/+10/+24.
- Exact `pix_off` relation of the four interleaved records.
- Whether animated mesh-face textures use the same clock (believed yes,
  same fetch).
