# HMI music

SkyNET plays loose `GAMEDATA/T*.HMI` (in-game) and `TITLE.HMI` (menus) in
the HMI SOS format, magic `HMI-MIDISONG061595`. `MDMDMUSC.BSA` holds XMI
twins of the same songs (FM / GM); the game does not play them.

Godot port: `scripts/loaders/hmi_file.gd` (parser → sorted event list),
`scripts/midi_synth.gd` (software synth). C#: `SkyNet.Formats.HmiFile`
(the same sorted event list, no synthesis).

DOS: song loader (v1.00) `FUN_000129bc`, header parse `FUN_00017880`,
timer set-up `FUN_000187ff`. These are in LE object 1; their v1.01
addresses are not recorded.

## Song header

| Offset | Size | Type | Meaning |
|---|---|---|---|
| 0x00 | 18 | char[] | `HMI-MIDISONG061595` |
| 0xD2 | 2 | u16 | ticks per beat, 96 or 480 (informational only; 480 in 21 of the 22 loose songs, 96 in one) |
| 0xD4 | 2 | u16 | **timer rate in Hz** (120) |
| 0xE4 | 2 | u16 | track count |
| 0xE8 | 4 | u32 | offset of the track offset table (u32 per track) |

Every song ticks at the timer rate (120 Hz) whatever the division says.
Treating 0xD2 × 0xD4 as PPQN × BPM plays 480-division songs 8× too fast.
The XMI twins (fixed 120 Hz) have the same tick and note counts, which
confirms this.

## Track

| Offset | Size | Meaning |
|---|---|---|
| 0x00 | 13 | `HMI-MIDITRACK` |
| 0x57 | 4 | u32 event stream offset, relative to the track |

A track ends where the next track (in file order) begins.

## Events

Standard MIDI with running status and variable-length deltas, except:

- **Note-on carries a variable-length duration** after the velocity.
  There are no note-off events (as in XMI).
- **0xFE = HMI private event**, length by sub-type (worked out from the
  files, not from code):

| Event | Total extra bytes |
|---|---|
| FE 10 | 4, then `byte[4] + 5` more |
| FE 12 | 4 |
| FE 13 | 12 (possibly loop points, UNCERTAIN) |
| FE 14 | 4 |
| FE 15 | 8 |

Counted over the 22 loose songs: FE 10 ×202, FE 14 ×16, FE 15 ×16,
FE 12 ×1, FE 13 ×1, no other sub-type; with these lengths all 271 tracks
parse to their end-of-track meta without losing sync.

- Controller 0x69 = HMI track enable (ignored by the port).
- Meta 0x51 (tempo) is honoured by the port.

## Track selection

Music per map = marker type 6 (value at sub+2; no marker = 0) indexing a
0x24-byte table at VA 0x4f9f9 (v1.00): twelve entries naming T2xx/T3xx
songs. The 300-series songs are multiplayer arenas. Menus use TITLE.HMI.

## Open questions

- v1.01 addresses of the music code and the track table.
- Exact semantics of the FE sub-events (loops).
- The engine's own instrument set: the DOS game used the sound card's
  synth; the port synthesises General MIDI timbres.
