# Message box

DOS has two message systems.

## 1. The panel message box (hints and objectives)

Drawn in the right-hand recess of PANEL0
([damage-hud.md](damage-hud.md#hud-panel0)). SKYNET.EXE v1.01:

- **Start** (FUN_0012fa3d): word-wraps the text to **0x62 = 98 px** of
  FONT0004 (FUN_0012daa8: line breaks and CRs become spaces; a break is
  `\r\n` after the last space that fits; a word with no space before it
  breaks where it is), clears the box (FUN_0012faa0), plays sound **0x51**
  and points the typewriter at it.
- **Callers**: hint acts 0x1C-0x25 (0x137f9d), objective acts 0x26-0x2A
  (0x137fd0, only for a text longer than 2 characters), and the
  border-box hint (0x122789) when the player reaches a map border.
- **Typewriter** (FUN_0012fb4c, once per frame from FUN_0012f77a): types
  **one character per frame** in palette index 0xB7. `\r` returns to x 0;
  `\n` drops a line of 5 + 1 px and, when the next line would pass y 27 of
  the 33 px box, scrolls up a line instead (FUN_0015569c). Five lines show.
- **Blit** (FUN_0012fcf5), every frame, to (219,164)-(317,196) of the
  320 × 200 screen (rectangle at 0x51b11: 218,163,317,196, +1 inside).
- **No expiry**: the text stays until the next message; a level start
  empties it (FUN_0012f745).
- Character advance = glyph width + 1; a space = the em width
  (FUN_0014691c, spacing [0x33348] = 1).
- **640 × 480** (`/g`, flag 0x20000): FUN_0012fa3d scales the wrap
  through FUN_0012ad2e (**196 px**), the typewriter's font 4 becomes
  FONT0010 (11 px lines + 1), the box is (438,394) on, 79 rows tall
  (33 scaled): the scroll test `y + 12 > 79 − 12` gives **6 lines**.
  The general rule: lines = (rows − step) / step + 1 with
  step = line height + 1 (5 low, 6 hi-res).

## 2. The status line queue

SKYNET.EXE v1.01, list at **0x51600**:

- **Entry** 0x2d bytes: `{i32 time, u8 colour, char text[40]}`; time is
  the game clock 0x38c00 at the push, **−1** ends the list, **−2** marks a
  free entry. A level start (FUN_0012f745) writes −1 to the first entry.
- **Push** (FUN_0012f8f7, text in EAX, palette index in DL): only while
  flag 0x30a50 bit 0x2000 is set (set at a level start). Takes the first
  entry that is not live (free or the end); the end may move only up to
  0x519b0, so the list holds **21** entries and a 22nd live line is
  **dropped** (carry set), not the oldest. Copies at most **39** characters
  (0x27).
- **Clock**: 0x38c00 gains round(70 / fps) a frame (FUN_00124c63:
  `0x460000 / fps·256`, next to the 1000 Hz clock 0x6aec0 from
  `0x3e800 / fps·256`): a **70 Hz** clock.
- **Draw** (FUN_0012f953, every frame from FUN_0012f77a): gathers the live
  entries into 0x519de, bubble-sorts them by time (oldest first, ties in
  list order), and for each: when `time − clock ≤ −210` it is freed (−2)
  and draws nothing; otherwise font 4 through FUN_0012ad00 (FONT0010 in
  640 × 480), the shadow in **0x7F** at +1,+1, the text in the entry's
  colour, from **(4,3)** unscaled on either screen, each line the font's
  height **+ 2** (0x33344 + 2) below the last. A line shows **210 ticks =
  3 s**. No other limit on the lines shown.

### Callers and colours

| Colour | Caller | Text |
|---|---|---|
| 0xB3 | 0x11d970 (pickup handler) | the item row's text (+0x1c) |
| 0xB3 | 0x136768 (level start, not with flag 0x200) | "YOU ENTERED" + the location name (table 0x53d00) |
| 0xB3 | 0x1253ad / 0x1253e1 | BrightMessage (brightness), AimOn / AimOff |
| 0xB3 | 0x11ad80 | "HMD Calibrated." |
| 0xB3 | 0x142xxx cheat handlers | cheat replies ("You are now immortal." ...) |
| 0x0C | 0x118c9c-0x1193ba, 0x122c93, 0x13229a | network: NewPlayer, PlayerLeft, PlayerRestart, PlayerSuicide*, PlayerSkull*, PlayerInJeep, LocalSuicide, LocalDied |
| 0x08 | 0x1263a3 | "OW!" (the moon) |
| 0x08 | 0x11b187 | "I DONT THINK SO !" |
| 0x08 | 0x11df3f | JoyCamOff |
| 0x08 | 0x142965 / 0x142975 | "Cheatcode validation completed.", "Extracting brand-information." |

Hints and objectives go to the panel box (section 1), not here.

## Godot port

- `message_box.gd`: `WRAP_PX` 0x62, `LINES` 5, `CHARS_PER_SEC` 30;
  `start`, `clear`, `tick`, `lines`, `finish`, `_wrap`.
- `main.gd` ticks it and hands the lines to the active HUD
  (`hud_panel.gd`); the status line is `main.gd`'s own.

## Deviations in the Godot port

- Types at a fixed 30 characters/s instead of one per rendered frame.

## Open questions

- The Godot port spaces status lines height + 1 and colours them 0xB3;
  DOS uses height + 2 and the queued colour (the C# port follows DOS).
