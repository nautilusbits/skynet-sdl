# PRS text tables and briefing TXT files

Plain-text data in `MDMDBRIF.BSA` (decrypted by the BSA reader). The text
is ASCII; trim each line so either line ending works.

Godot port: `scripts/loaders/prs_file.gd` (generic),
`scripts/loaders/transfrm_prs.gd`, `scripts/loaders/briefing.gd`.
C# port: `SkyNet.Formats.PrsFile`, `TransfrmPrs`, `Briefing`.

SkyNET's MDMDBRIF.BSA holds 4 PRS files (CHEAT, STRINGS, TRANSFRM, VOICE)
and 60 briefing TXT files; NETLEVEL.PRS is loose in the data folder.

## PRS (generic)

INI-like: `key = value` lines, `;` comments, `[section]` headers.

- Trim key and value.
- **The first definition of a key wins.** The DOS parser stops at the
  first match, and some files list a key twice with different text.

| File | Content |
|---|---|
| STRINGS.PRS | UI and pickup messages |
| VOICE.PRS | voice-line table |
| TRANSFRM.PRS | destructible damage stages (below) |
| CHEAT.PRS | cheat codes |
| NETLEVEL.PRS | multiplayer arenas |

## TRANSFRM.PRS

One section per destructible "transform" object:

```
[objectN]
name   = <intact mesh>
type   = <UNCERTAIN>
frames = <count>
frame0 = <intact mesh>
frame1 = <damaged mesh>
...
```

Each hit advances one stage; the mesh is swapped to the next frame.
Keys are matched lower-case and names lower-cased; the `frames` count is
not used (frames are taken in file order); a section with no name or a
single frame is dropped. SkyNET's file has 22 usable objects. Read as a
generic PRS (first key wins) it collapses to 9 keys, so it needs its own
section-aware parser. Used
by action types 0x18 / 0x19. Parsed by (v1.00) "TransformInit"
`FUN_00120000`; handler (v1.00) 0x120433.

UNCERTAIN: the `type` field.

## Briefing NNN.TXT

One per mission start map (`210.TXT`, ...; Future Shock `010.TXT` ..
`190.TXT`, plus `TAC*.TXT`).

- A section opens with a `[XX]` header line and ends at a `%%%%` line.
- Entries inside a section are separated by `####` lines.
- Bracketed lines inside an entry are tags, not text:
  `[vNNNNN]` voice line id, `[NNN]` speaker number, `[000.102...]`
  objective flag (UNCERTAIN).

| Section | Meaning |
|---|---|
| [EN] | mission objectives paragraph |
| [BR] | briefing dialogue, one entry per speaker turn (portrait BRIEF<speaker>.IMG) |
| [M1]..[M5] | mission objectives: each **entry** counts |
| [G1]..[G9] | hint messages (action 0x1C+n), change nothing |
| [TA] | tactical view: asset names, split on spaces |

A section's entry text is its non-tag lines joined with one space; text
after the last `####` (before `%%%%`) is one more entry. In [BR] the
speaker starts at 3, a numeric tag sets it, a `[v...]` tag is skipped and
a tag containing `.` ends the dialogue. The picture is the first numeric
tag of [EN] (else [BR]) that comes before any text.

### Objective counting

At briefing load the engine counts **every `####`-terminated entry** of
`[M1]`..`[M5]` into the objective counter (v1.01 0x12ce73). Blank entries
count too. Action `0x26 + n` decrements the counter and prints the next
entry of section `[M(n+1)]` (v1.01 handler 0x137fd0 steps a text
pointer); a line of two characters or fewer is not shown. The mission ends
when the counter reaches 0. Details in game-logic (TODO).

## Open questions

- TRANSFRM `type`.
- The objective-flag tag format.
- Full key lists of STRINGS.PRS and VOICE.PRS (not reproduced here).
