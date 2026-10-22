# CONTROLS.DAT

Key bindings for the DOS CONTROL CONFIGURATION screen (`CONTROLS.IMG`,
17 bind boxes). The file lives in the game's **install folder** (next to
SKYNET.EXE), not in the data folder.

Godot port: `scripts/controls.gd` keeps its own modern layout and does not
read this file; it only follows the 17-action list.
C# port: `SkyNet.Formats.ControlsDat`.

## Layout

Both shipped files (SkyNET and Future Shock) are **154 bytes**.

| Offset | Size | Type | Meaning |
|---|---|---|---|
| 0x00 | 68 | i32[17] | scancodes (PC set-1 make codes), one per action |
| 0x44 | 86 | | UNCERTAIN; mostly small values and zeros (joystick / mouse settings?) |

The shipped SkyNET defaults are **W, S, Left, Right, A, D, 0x81, Right
Shift, F, M, Left Shift, Space, Ctrl, Up, Down, Home, Tab** (not A / Z as
an older note said). Future Shock's file differs in slots 2/3 (`,` `.`),
7 (0x82), 13-15 (N, PgDn, End). That sequence fits the Godot port's
action order: forward, back, turn left, turn right, strafe left, strafe
right, fire, throw, activate, slide, sprint, up, down, look up, look down,
centre view, automap. Slot 17 (Tab) is confirmed: its key (`DAT_00062f2e`)
opens the automap in `FUN_0011ad8a` and closes it inside
([automap.md](../game-logic/automap.md)). Values
0x81 / 0x82 are above the keyboard range and sit in the fire / throw
slots: probably mouse buttons 1 / 2 (UNCERTAIN).

## Open questions

- Confirm slots 1-16's order and the 0x8N mouse encoding in the DOS screen code
  (slot 17 = automap is confirmed).

## C# port

The CONTROLS screen's boxes name the port's actions
(`MenuLayout.DefaultControlActions`); AUTOMAP is the `Automap` action (Tab,
pad R3). The automap's own keys are separate actions (`Automap turn left`...,
the arrows and + / −), rebindable with the console's `bind`.
- The 86 bytes after 0x43.
- The DOS routine that reads and writes it.
