# Future Shock differences

## Ground truth

SKYNET.EXE v1.01 plays Future Shock directly when pointed at its data, so
**Future Shock behaviour = SkyNET's engine on Future Shock maps**. The
standalone Future Shock executable (`shock.exe`, an older engine) is a
cross-check only, never the reference.

## The Future Shock mode flag

`[0x30a50] & 0x200000`, set by the menu (NEW GAME → FUTURE SHOCK,
FUN_0011e6f9) or by a save version < 0x6e. 21 code sites test it.

| What changes | SkyNET | Future Shock mode |
|---|---|---|
| Map archive | MDMDMAP2.BSA | MDMDMAPS.BSA, searched under the FS path (0x5f500); missing → POPUPFS1.IMG error box |
| Everything else (images, sounds, briefing pictures, hi-res set) | SkyNET's own data | **still SkyNET's data** (archive search 0x5f400/0x5f480/0x5f500, 0x13e9c5) |
| Mission table | 0x34846 | **0x341ce**, 17 entries |
| Start weapons | 0x4356c | 0x43538: pipe only + records 14-16, 18-25 |
| Ammo grant between missions | 0x443c4 | 0x4423c |
| Mission stats | | 0x53400 |
| Videos | | BEGIN.VID / END.VID |
| Briefing | per-mission image + palette (0x4f876) | text NNN.TXT (MDMDBRIF.BSA, `[NNN]` tag at the top of each language section), picture BRIEF<tag>.IMG from SkyNET's MDMDIMGS.BSA, palette BRIEF.COL (0x4db91) |

SKYNET.EXE never loads Future Shock's START.IMG or SHOCK.COL: there is no
Future Shock menu in that mode.

## Campaign

Mission table 0x341ce: maps 010, 020, 030, 050, 060, 070, 090, 100, 110,
120, 130, 140, 150, 160, 170, 180, 190 (missions 1-19 without 4 and 8).

| Mode | Maps |
|---|---|
| Jeep | 020, 100, 170 |
| HK | 060, 110, 120, 160 |
| Foot | the rest |

MAP.040 and MAP.080 exist but no table entry or exit leads into them
(loadable with `--map` only). The vehicle mode applies only to the start
map, never to interiors.

## Data differences

- Future Shock archives: BSA variant "Future Shock full"; 143 maps
  (MAP.001-957); WLD.010+; TEXTURE.000-239; palettes are **loose files**
  (SHOCK.COL game, BRIEF.COL UI, START.COL title/menus).
- The archive also holds MAP.JTE and MAP.TXT (text, not levels): accept
  only numeric suffixes.
- **No WLD.040 / WLD.080** although both are outdoor maps. DOS keeps the
  previously loaded heightmap. The Godot port picks the WLD on which most
  of the map's ground markers stand (`LevelLoader._best_fit_wld`):
  MAP.040 → WLD.030, MAP.080 → WLD.170, MAP.001 → WLD.090, MAP.003 →
  WLD.030. MAP.002 and MAP.006 (demo levels) fit none.
- Interiors place start markers up to ≈60 units above the floor; drop the
  player to the floor below.
- Enemy types 0-49 are a subset of SkyNET's table; the enemy archive adds
  only deathmatch avatars. Sprite banks are byte-identical except
  TEXTURE.222.
- Future Shock robots carry acts (marker sub+4/sub+5): 92 robots drive
  objectives and hints on death.

## Action ids

100 distinct acts across the campaign. Extra over SkyNET's use: 0x13,
0x1A, 0x2F, 0x50, 0x58, 0xE5. Under SKYNET.EXE's own table (the reference)
0x1A is undecoded (see [action-handlers.md](action-handlers.md)). For
comparison, shock.exe's table (file 0x899a4, VA 0x47900) has 0x13, 0x2F
and 0x47-0x58 empty, and differs from SkyNET in 85 slots.

## Godot port

`skynet_paths.gd` (`game`, `map_archive`, `variant`, palette accessors,
`switch_game` in-process), `main.gd` (`MISSION_TABLE_SHOCK`,
`MISSION_GRANT_SHOCK`, `_campaign_sequence`), `fly_camera.gd`
(`START_WEAPONS_SHOCK`), `level_loader.gd` (`_best_fit_wld`,
`_lift_to_floor`), `triggers/rules_shock.gd` (same rows as SkyNET; the
running game follows SkyNET's rules for both). Cache root
`shock/converted`, separate from SkyNET's.

## Deviations in the Godot port

- Spawn nudging: DOS never moves a player off a blocked arrival marker;
  the port searches a clear spot with the real body radius, visible from
  the marker.
- Briefing face animation (BLINK/MOUTH CFA) and BEGIN.VID not played.

## Open questions

- Whether weapons/health carry between Future Shock missions differently.
- Values at 0x38dc8 / 0x38dc4 at the grant site.
- MAP.040/080: whether DOS reaches them in any way.
