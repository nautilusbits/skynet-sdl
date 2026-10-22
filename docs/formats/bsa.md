# BSA archives

XnGine resource archives (`MDMDOBJS.BSA`, `MDMDENMS.BSA`, `MDMDIMGS.BSA`,
`MDMDMAP2.BSA`, `MDMDBRIF.BSA`, `MDMDSFXS.BSA`, `MDMDMUSC.BSA`,
`MDMDHRES.BSA`, ...). The variant with per-entry encryption is sometimes
called the "CTPAX-X" format after the tool that first documented it.

Godot port: `scripts/loaders/bsa_reader.gd`.

## Layout

| Offset | Size | Type | Meaning |
|---|---|---|---|
| 0 | 2 | u16 | entry count N |
| 2 | ... | bytes | entry data, packed back to back in TOC order |
| file_size − 18·N | 18·N | TOC | N entries of 18 bytes |

TOC entry:

| Offset | Size | Type | Meaning |
|---|---|---|---|
| 0 | 13 | char[13] | name, NUL-padded ASCII (8.3 DOS name) |
| 13 | 1 | u8 | flags; bit 0 = encrypted |
| 14 | 4 | u32 | size in bytes |

There is no offset field: entry *i* starts at `2 + sum(size[0..i-1])`.

## Encryption

An encrypted entry is decrypted byte by byte:

```
plain[i] = (cipher[i] - key[i % 21]) mod 256
```

The 21-byte key is derived from a per-game passphrase (one each for the
Future Shock demo, Future Shock full and SkyNET):

```
digits[j] = a-z -> 0..25, 0-9 -> 26..35, A-Y -> 36..60
key[0..20] = 0
repeat 28 rounds r:
    carry = digits[r % len]
    for i in 0..20:
        v = key[i] * 62 + carry
        key[i] = v & 0xFF
        carry = v >> 8
```

The passphrase strings are in `bsa_reader.gd` (`PASSPHRASES`). Which game
the data belongs to is detected from the map archive present
(`MDMDMAP2.BSA` = SkyNET, `MDMDMAPS.BSA` = Future Shock).

## Notes

- Names are matched case-insensitively; when a name occurs twice the first
  entry wins.
- `MDMDHRES.BSA` (SkyNET's 640×480 mode) holds only HUD and weapon art;
  there are no world textures in it. Future Shock has no HRES archive; in
  Future Shock mode the engine reads SkyNET's (archive search code around
  0x13e9c5, name table 0x5f400 / 0x5f480 / 0x5f500).
- `MDMDMUSC.BSA` holds XMI twins of the songs; the game itself plays the
  loose `T*.HMI` files (see [hmi.md](hmi.md)).

## Open questions

- The DOS reader routine address is not recorded in the research notes.
- A fourth passphrase slot (installer archives) is unused.
