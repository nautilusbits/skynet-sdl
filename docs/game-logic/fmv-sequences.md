# FMV sequences (.VID): when each one plays

Decoded from SKYNET.EXE v1.01 for GitHub issue #6 (plan
[../plans/fmv-playback.md](../plans/fmv-playback.md), step 3). Addresses are
v1.01 code VAs; data addresses as in `strings.txt` (code immediates are
these − 0x30000; file offset = address + 0x53AA4 for both).

## The table

| Moment | VID | DOS evidence | Port hook |
|---|---|---|---|
| Program start, before the title (once) | `OPENING` | FUN_0011c900 (front-end loop, called from 0x1167fa) entry: `mov eax,"gamedata\opening.vid"; mov edx,3; call 0x11d54b` at 0x11c95f, then the title loop starts TITLE.HMI | `AppHost` start-up, the `else { menu.OpenTitle(); PlayTitleMusic(); }` branch (AppHost.cs ~611), before both calls; not on later returns to the title |
| New game, Future Shock only | `BEGIN` | 0x11cb23: new campaign branch (`0xa50 & 0x20` clear), FS bit 0x200000 set, not 0x80040000 → `call 0x11d4a9` with entry 0x355ae | `menuEvents.OnStartGame` (AppHost.cs ~479) when `kind == FutureShock`, before `session.StartMission` |
| Briefing screen, BRIEFING tab chosen (SkyNET only) | `SEQ10`..`SEQ80` by mission | FUN_0012dcad from tab handler 0x12d71a (hotspot table 0x4db2d: x 54-134 of the tab bar); plays the row's +0x1B name, then shows the `[EN]` page. Opening the screen shows `[EN]` without it (FUN_0012c800 → 0x12d70f) | `MissionScreen.Open(MissionTab.Briefing)` (Menu/MissionScreen.cs) for SkyNET; video under the tab bar (clip top y = 14, [0x33abc] = 0xE) over FAKEHEAD.IMG |
| Mission won, before the next mission (SkyNET only) | M2 `1STSCENE`, M3 `SEQ30A`, M4 `SUBSINK`, M6 `SC2FINL`, M8 `SCENE3` | FUN_0011c900 0x11cff8: bit 0x40 still set after the mission (won), not FS, row +0x43 non-empty → `call 0x11d54b` (0x11d025), then `inc [0x341ba]` | `GameSession.TimeTheEnd`, `Phase == Complete` branch, before `StartMission(next)` / `CampaignComplete` (needs a listener event, e.g. "mission done, play X") |
| Campaign over (row index past the end) | SkyNET `CREDITS`; FS `END` | 0x11d043: SkyNET → `call 0x11d54b` "gamedata\credits.vid" (0x11d059); FS → `call 0x11d4a9` entry 0x355b6 (0x11d06a); then back to the title loop | `ISessionListener.CampaignComplete` (GameSession.cs ~1668); `SessionEvents` has no handler for it yet |
| Front-end idle ~20 s (attract) | `CREDITS` | FUN_0011e27c (0x11e30c) and FUN_00141df9 (0x141e10): BIOS ticks [0x46c] − last input ≥ 0x168 (360 ≈ 19.8 s) → FUN_0011d5b2 | title / menu screens' idle clock (`TitleMode` / `MenuSystem`) |
| Hidden title hotspot, bottom-left 10 × 10 (x 0-10, y 190-200) | `CREDITS` | title hotspot record 0x60b4a → 0x13fdf2 sets item 0x2A; FUN_00141726 0x141828 → FUN_0011d5b2 | `MainMenuScreen` (Menu/Screens.cs): an extra item at that rectangle |
| Death / mission failed | none | the failure path (0xa50 bit 0x40 cleared) goes to RESTART.IMG (FUN_0011d09e); no VID call | — |
| Never | `LOGO`, `START` (both games) | only in the {name, sound-cue list} table at 0x3559e (logo 0x33700, start 0x3372f, begin 0x33798, end 0x33d97); no code reads entries 0 and 1. Future Shock's own shock.exe still names them (plus VIRTUAL1-4) | none; play them only as an option, not DOS behaviour |

FUN_0011d5b2 (credits/attract): with [0x355be] == 1 and the CD bit, and
`credits.vid` found on the data path (0x142c0c), it plays it (flags 7);
otherwise the **text scroller** FUN_00143400 (CREDITS.IMG, CREDITS.COL,
SCROLLER.TXT).

## The mission table fields

Row = 0x5C bytes (SkyNET 0x34846, FS 0x341ce, the 0x80000000 mode 0x34b82;
chosen at 0x11cab0): +0 number, +4 start map, +8 mode, +0x0C FIRSTnn.IMG,
+0x1A " gamedata\seqNN.vid" (read from +0x1B), +0x33 " briefNNN.img",
+0x42 second VID (read from +0x43). A row with number 0 ends the campaign.
Every Future Shock row has both VID fields empty, and both reads are gated
by `!(0xa50 & 0x200000)` anyway, so Future Shock's VIDs come only from the
fixed BEGIN/END entries.

| Mission | Map | Briefing tab (+0x1B) | After the win (+0x43) |
|---|---|---|---|
| 1 | 210 | SEQ10 | — |
| 2 | 220 | SEQ20 | 1STSCENE |
| 3 | 230 | SEQ30 | SEQ30A |
| 4 | 240 | SEQ40 | SUBSINK |
| 5 | 252 | SEQ50 | — |
| 6 | 260 | SEQ60 | SC2FINL |
| 7 | 270 | SEQ70 | — |
| 8 | 280 | SEQ80 | SCENE3, then CREDITS |

## Players and gates

- **0x11d54b** (OPENING, after-win, CREDITS at the end): returns at once
  when [0x355be] == 2 or when 0xa50 bit 0x40000000 is clear. That bit is set
  by FUN_0012a710 when the drive in [0x4cc0b] answers (the CD); a missing
  CD skips these videos silently. Path = [0x30abc] data path + name
  (0x13bc95); player 0x133600, flags 3, | 4 when [0x51454] ≠ 1.
- **0x12dcad** (SEQnn): gated by [0x355be] ≠ 2 and not FS, **no** CD-bit
  test; same path and player; key loop 0x11b4d5(0x81) to skip.
- **0x11d4a9** (BEGIN, END): gated by [0x355be] == 2 only; the second
  player 0x143fba, which mixes the entry's RAW sound cues (e.g. BEGIN:
  BAMSTRET.RAW…, END: AMB_CORE.RAW…); a missing file goes to the error
  handler 0x127795.
- **[0x355be]** (just before the "Video-clip." string) is set to 1 at init
  (0x1172bb). Only `/v` (0x116e74) and `/F<non-digit>` (0x116f5e) set it
  to 2, and both sit behind `mov ebx,0x208; and ebx,1; je` — dead in the
  release. The `/?` help lists no video switch; OPTIONS has no video row.
  So DOS gives the player no way to turn videos off.
- **Around a play:** the screen is cleared (0x1471a0(0)) and flipped
  (0x13d89f). SEQ and the credits save the VGA palette (0x149a87 → 0x3529e)
  and restore it afterwards (0x149a51). 0x11d4a9 sets a black palette
  (0x34c9e) before and the menu palette (0x324ac) after. No caller stops
  the HMI music itself; after OPENING the title loop (0x11c99f) starts
  TITLE.HMI.

## In the port

`AppHost.Movies.cs` wires them: OPENING (`PlayOpening`, interactive windowed runs only), BEGIN
(`StartNewGame`, Future Shock's first mission), the BRIEFING tab (`MissionScreen.Open` →
`IMenuActions.PlayMovie`, `MoviePlace.Briefing`: FAKEHEAD.IMG, the movie at y 14, then the first
page; owner's choice, unlike DOS: it also plays by itself on a mission's first briefing,
`MenuSystem.OpenMission`), the won mission's movie (`WinMovieSlot`, before the next briefing;
dropped by the title, a new game, a load), CREDITS / END at the campaign's end then the title,
CREDITS after the title's idle time (focused window only). Any new key, click or pad button skips
a movie after its first 0.5 s (`MovieScreen.ArmSeconds`; auto-repeat does not). The names sit in
`MissionTable.SkyNet` (`BriefingMovie`, `WinMovie`, from the rows above). None plays under a demo,
a recording, a capture or a deathmatch (`MoviesAllowed`). BEGIN / END have no sound blocks: their
RAW cues (0x143fba, table 0x3559e) are mixed into one track (`MovieCues`). Not done: the hidden
title hotspot, the idle clock on the other front-end menus, the text scroller
when CREDITS.VID is missing.

## Open questions

- Whether 0x133600 stops or ducks the HMI music (the owner recalls no music
  during the videos in DOS; the port pauses it); the meaning of flag bit 4
  (set when [0x51454] ≠ 1, always for the idle credits) — likely "no VID
  sound".
- Whether the 0x40000000 bit is set on a full hard-disk install (if it is
  CD-only, a no-CD DOS run never shows OPENING or the after-win scenes).
- The 0x80000000 mode (single row, map 200) plays nothing.
- Check in DOSBox that the BRIEFING-tab trigger is the only SEQ trigger
  (the static code shows no other caller of 0x12dcad).
