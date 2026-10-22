# Damage and HUD

The soldier's health and armour, vehicle hulls, blasts, radiation,
difficulty multipliers, death, and the PANEL0 status bar.

## Health and armour

- Health is **1000 damage points** stored in 8.8 fixed point (maximum
  0x3E800). Multiplayer uses 150.
- Damage routine **0x122652** (v1.01). With no armour a hit of `D` takes
  `D × 256` (8.8), so a table value `D` is `D/10` % of the bar.
- Armour `A` is 0..1 (16.16). On a hit:
  `A' = max(0, A − 82/65536 × D)`, loss `= (1 − A') × D`.
- **No invulnerability frames.**
- Health and armour come back only at mission start (full), on entering
  the next map of the same mission (+½ health, +0.5 armour), and from
  health/armour pickups ([pickups.md](pickups.md)).
- In a vehicle the soldier takes **nothing**; the hull pool (0x130caa)
  takes the damage 1:1 and the ARMOR bar shows the hull (0x13231d). Jeep
  hull 1000, HK hull 1500 (the flag choosing between them is undecoded).

## Difficulty

Table at **0x5c210**, rows {1, 4, 7} for LOW/MED/HIGH, four 16.16 values
(FUN_0013b300). Default MED.

| Multiplier | LOW | MED | HIGH |
|---|---|---|---|
| Enemy rate of fire | 0.25 | 0.625 | 1.0 |
| Damage to enemies | 1.5 | 1.25 | 1.0 |
| Damage to the player | 0.75 | 1.0 | 1.0 |
| Armour regeneration per second | 0.025 | 0.015 | 0.0125 |

Difficulty does **not** change enemy count or pickups. The player
multiplier ([0x5c208]) applies only to **direct projectile hits**
(0x1230d6); blasts, contact, falls, drowning and radiation are unscaled.
The damage-to-enemies factor ([0x5c504]) applies in the impact routine
FUN_0012311f, to props and actors alike.

## Radial blast

FUN_00124386 (v1.01): strength `S`, radius `R = clamp(S, 5, 500)`,
`damage(d) = S × (R − d + 40) / R`, applied **only when ≥ S/2**, and only
with a clear line of sight (0x138a4a). DOS deals the **direct hit and the
blast** (S = |ammo damage|). A rocket (400) therefore reaches 240 units.
A robot's death blast is S = 75 (0x129661), 0.375 s after EnemyKill.
Destructible props blast with their `destroy_param` (cars 200-300,
tanker 600, crates 0).

## Other damage sources

| Source | Rule |
|---|---|
| Enemy contact (machines, claws) | 4 points per collision probe per frame (0x122854); per-type contact word at enemy record +3 |
| Drowning | 85 points/s (0x5500 >> 8) after the air runs out ([water.md](water.md)) |
| Fall | see [player-movement.md](player-movement.md#fall-damage) |
| Radiation | per source (FUN_0013b1e0): skip when `0.75 × dist3d > strength`; `r = strength − horizontal distance`; skip when `r ≤ 0`; `dose += min(r × 50, 12800) / 256` (≤ 50 per source). DOS charges at least 1 HP **per frame** |

Radiation sources are marker type 4, strength at sub+2 (80..1024).

**No fire damage.** FUN_00122b52 (the hit check every hit ends in) has
seven calls in v1.01 (a byte scan of the whole code object agrees with the
decompile): contact FUN_00122d2f (0x12b7f1, actors only), fall FUN_00122d76,
shot FUN_0012311f, blast FUN_00124886, drowning (0x12eb81), radiation
FUN_0013ba9a and a network hit FUN_0013223e. Fire sprites, wrecks' fires and
the molotov burn nobody, and a mod cannot change that.

## Death

FUN_00122b52: HP ≤ 0 sets [0x30a50] |= 0x1010 (dead + mission failed),
sound 108, no death animation. The failure screen FAILED.IMG is shown for
≈2.4 s (FUN_00121fe3), then the RESTART.IMG yes/no dialog (FUN_0011d09e,
Esc = no). **Yes replays the whole mission from its first map, through the
briefing**, restoring the mission-start snapshot (FUN_0011d3b0 ←
FUN_0011d346: damage, armour, 13 pools, 26 per-weapon counts, both
selections) and deleting every map's saved state (`tfs_swap.*`). No = main
menu. There is no single-player respawn and no autosave. Multiplayer
respawns (branch 0x40000, FUN_00121bdf).

## HUD: PANEL0

320 × 40 at y 160 of the 320 × 200 screen. DOS keeps PANEL0 in vehicles
too (PANEL1/PANEL2.IMG exist but the executable never loads them).

| Element | Position (320 × 200) | Notes |
|---|---|---|
| Health bar | 41 × 5 at (172,185) | PANELBAR.IMG; **no numeric health** |
| Armour bar | (172,169) | shows the hull in a vehicle |
| Primary weapon | icon + 4-digit count right-aligned at x 128, box (50,180)-(133,197) | FUN_00132654 ([0x524a1], row [0x524ad] = 31 + panel origin (0,160)), FONT0004, colour 0xB7 |
| Secondary (thrown) | icon + 3-digit count at x 38, box (3,180)-(42,197) | |
| Radiation counter | RADCOUNT.CFA at (50,165) | |
| Message box | (219,164)-(317,196) | [message-box.md](message-box.md) |
| Compass | window 37 px at (96,163) | below |

Compass (FUN_001323a2): COMPASS.IMG ribbon 185 × 14 (hi-res 370 × 28),
N/E/S/W 37 px apart, 148 px per turn, north at column 17.
`scroll = (angle × −148 + 1024) >> 11`, angle = clockwise 11-bit bearing
plus the marker-7 north offset `(deg << 11) / 360`.

### The 640 × 480 HUD (`SKYNET.EXE /g`)

Flag 0x20000 of [0x30a50] = hi-res. DOS chooses it with DETAIL's
RESOLUTION 320 × 200 / 640 × 480; the port's DETAIL has GAMEPLAY there, and
the set is the console's `hud dos|hires` and the settings' `hudArt`. The HUD keeps the 320 × 200 numbers
and passes them through two helpers:

- **FUN_0012ad2e** scales a position: `x × 2`,
  `y = (y × 0x1333 + 0x400) >> 11` (× 2.4 rounded). The panel lands at
  y 384 (640 × 96), the counts' row 191 → 458 (x 128 → 256, 38 → 76),
  the message box's first line 164 → 394 at x 438.
- **FUN_0012ad00** sets a font through the table at 0x4d000, so the
  HUD's font 4 becomes **FONT0010** ([fnt.md](../formats/fnt.md)).

The message box wraps to 0x62 scaled = **196 px** and, with FONT0010's
11 + 1 px lines in a box 33 rows scaled = 79 tall, holds **6 lines**
([message-box.md](message-box.md)). The status lines are not scaled:
(4,3) of the 640 × 480 screen, in FONT0010.

C# port: `DosHud` in the hi-res set draws all HUD text this way; the
positions are mapped back into its 320 × 40 panel (the 640 × 96 art is
drawn there), the font a whole-number half pixel across and the art's
row pitch down. The low set is unchanged.

Crosshair (aim table 0x443ca): MDMAIM.IMG on foot, CROSHAIR.IMG in the jeep
and HK. F6 hides the panel; F7/F8 brightness; F9 auto-aim; F10 HMD.

## Godot port

- `fly_camera.gd`: `take_dos_damage`, `DOS_HEALTH_POINTS` 1000,
  `ARMOR_COST_PER_POINT` 82/65536, `veh_hull`, `VEH_HULL_POINTS(_HK)`.
- `projectile.gd`: `dos_blast`, `blast_clear`, `blast_player` (used by
  projectiles, grenades, hitscan blasts and destructibles).
- `main.gd`: radiation (`_collect_radiation`, `RAD_MAX_DOSE`), compass,
  FAILED/RESTART flow (`FAILED_HOLD_SEC` 2.4).
- `hud_panel.gd` (DOS PANEL0), `hud_modern.gd` (alternative bar),
  `settings.gd` (`DIFF_*` tables).

## Deviations in the Godot port

- Radiation dose is applied as points **per second** (× delta), not per
  frame, to remove the frame-rate dependence.
- Armour regeneration: the difficulty table has a regen column, but the
  regen routine (0x1227f0) has no caller in v1.01, so armour never
  recharges in play. The port follows that (it recharged until a later
  audit). Its own addition: vehicle energy (pool 10) recharges at
  450/s after 0.35 s without firing (`VEH_REGEN_*`, no DOS basis found).
- Per-enemy-type contact damage (enemy record +3) is not ported; machines
  use a flat 4 points per physics step.
- The F6 panel-off mode is not ported.

## Open questions

- Which flag picks a 1000 or 1500 hull.
- Whether the armour-regen routine is reachable through a table the
  decompile missed (no caller found).
