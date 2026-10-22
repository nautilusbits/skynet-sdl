# Game logic

How the DOS engine behaves, decoded from SKYNET.EXE v1.01, with the Godot
port as reference implementation. Data layouts the logic reads are under
[../formats](../formats/).

Status: **done** = written in full for what is known (each doc ends with
its open questions).

| Doc | Topic | Godot reference | Status |
|---|---|---|---|
| [player-movement.md](player-movement.md) | walk/run speeds, gravity, jump, step-up, player cylinder, eye height, per-second scaling of frame constants | `fly_camera.gd` | done |
| [damage-hud.md](damage-hud.md) | 1000-point soldier, armour, vehicle damage, radial blast, radiation dose, HUD panel layout, compass | `hud_panel.gd`, `main.gd` | done |
| [weapons-ammo.md](weapons-ammo.md) | weapon record table, ammo type table, fire sounds, impact sounds, damage, lifetime, start weapons | `fly_camera.gd`, `projectile.gd` | done |
| [throwables.md](throwables.md) | second weapon slot, F1-F5 selection, pipe bomb / molotov / grenade / canister / satchel | `grenade.gd` | done |
| [vehicles.md](vehicles.md) | jeep and HK: mission vehicle table, physics, cockpit model, turret camera, HK altitude ceiling | `fly_camera.gd` | done |
| [enemies-ai.md](enemies-ai.md) | enemy type table, multi-segment enemies, AI states, AIS script VM, fire and death, dormant traps, path vehicles (state 11) | `enemy.gd`, `enemy_ai.gd`, `enemy_ai_data.gd` | done |
| [action-handlers.md](action-handlers.md) | the action handler table: doors and lifts (slide/swing/rotate), lights, destructibles 0x18/0x19, demolish 0x1B, objectives 0x26-0x2A, hints 0x1C-0x25, fail 0x2B, proximity gates 0xEF/0xF1/0xF2, interior exits 0xF0 | `level/behaviour.gd`, `triggers/rules_skynet.gd` | done |
| [pickups.md](pickups.md) | item table, pickup sprites, drops from destroyed objects | `pickup.gd`, `pickup_data.gd` | done |
| [objectives.md](objectives.md) | [M1]-[M5] counter, mission end and failure, end screens | `level/objective.gd`, `level/mission.gd`, `main.gd` | done |
| [water.md](water.md) | water level markers 103/104, painted lakes, wading and submerged movement, drowning, moving water | `main.gd` | done |
| [sky-fog-detail.md](sky-fog-detail.md) | night sky and moon, dusk dome missions, depth cue, RENDER DETAIL | `main.gd`, `settings.gd` | done |
| [message-box.md](message-box.md) | in-game message display | `message_box.gd` | done |
| [rocket-eye.md](rocket-eye.md) | rocket camera view | `fly_camera.gd` | done |
| [automap.md](automap.md) | the automap: seen bit 0x80, 5 × 5 cell window, camera, MAPBAR/MAPGRID art, clearautomap | none (the Godot port had no automap) | done |
| [future-shock.md](future-shock.md) | running Future Shock data on SkyNET rules: map archive, palettes, campaign order, missing WLDs, extra action ids, vehicle maps | `skynet_paths.gd`, `level_loader.gd` | done |
