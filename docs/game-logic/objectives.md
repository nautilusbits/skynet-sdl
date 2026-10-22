# Objectives and mission end

## The counter

- A mission ends on an **objective counter** ([0x4e4c2] in v1.00 notes;
  gate in FUN_00121ae3). Its start value is the number of
  `####`-terminated entries in the `[M1]`..`[M5]` sections of the mission's
  briefing text `<start map>.TXT` in MDMDBRIF.BSA. **Blank entries count.**
  Examples: 210 → 3, 220 → 1, 270 → 1.
- Act **0x26 + n** (handler 0x1377d0, v1.00) decrements the counter once,
  prints the next entry of section `[M(n+1)]` in the message box (only when
  the text is longer than 2 characters), plays sound 0x51 and writes 0xFF
  into its act byte.
- Acts **0x1C-0x25** print `[G1]`.. hints and change nothing.
- Act **0x2B** fails the mission at once (sets the same 0x1000 bit as
  death).
- Act **0x2C** (countdown relay) fires its chain when the counter equals
  its p4 word (1) and is above zero.
- The counter belongs to the **mission**, not the map: it survives
  interior trips and variant maps (mission 1's objectives sit on MAP.215
  and MAP.217). It resets on a mission restart.
- The objective message table at 0x4dc9e is filled at runtime (zeros in
  the static file).
- Robots may carry an act too: a marker's sub+4 act and sub+5 state are
  copied to the actor (0x12a439); state 0x04 = act on death. Future Shock
  uses this for objectives (92 robots); shipped SkyNET maps have none.

## Pitfalls

- **Marker type 4 is a radiation source**, not an extraction zone (one
  older note calls it an evacuation zone; the consumers and the maps say
  radiation).
- Enemy markers keep other data where a sprite keeps its act byte; read as
  acts they look like 0x28/0x29/0x2B. Exclude markers when collecting
  objectives.
- Proximity triggers measure true 3D distance. A 2D test lets MAP.250's
  jeep ring fire from the water below the quay.

## Mission table and campaign

Mission table **0x34846** (v1.01; one note gives 0x3484a), 0x5C bytes per
entry: +0x00 mission number, +0x04 start map, +0x08 player mode (0 foot,
4 jeep, 8 HK), then loading-screen / video / briefing file names.

| Mission | Start map | Mode |
|---|---|---|
| 1 | 210 | foot |
| 2 | 220 | jeep |
| 3 | 230 | foot |
| 4 | 240 | foot |
| 5 | 252 | foot |
| 6 | 260 | jeep |
| 7 | 270 | HK |
| 8 | 280 | foot |

Future Shock mode uses 0x341ce ([future-shock.md](future-shock.md)).

## End screens

- **Won**: WELLDONE.IMG over the frozen frame, then the next briefing.
  Between missions the ammo grant applies
  ([weapons-ammo.md](weapons-ammo.md#ammo-pools-initial--max)).
- **Failed / dead**: FAILED.IMG ≈2.4 s, then RESTART MISSION yes/no
  ([damage-hud.md](damage-hud.md#death)).
- State never carries between missions except inventory: mission start =
  full health and armour.

## Godot port

- `scripts/level/objective.gd` (the act node), `scripts/level/mission.gd`
  (counter: `objectives_total`, `objectives`; mission key = start map
  suffix).
- `main.gd`: `MISSION_TABLE`, `CAMPAIGN_SEQUENCE`, end screens
  (`AUTO_ADVANCE_SEC` 6, `FAILED_HOLD_SEC` 2.4), restart box.
- `tools/objective_census.gd`: lists every objective source per mission.

## Deviations in the Godot port

- The WELL DONE screen auto-advances after 6 s.
- The port has a deathmatch respawn view; single-player follows the DOS
  restart.

## Open questions

- Census gaps: SkyNET mission 240 and Future Shock mission 100 lack a
  found source for one `[M1]` entry (not investigated).
