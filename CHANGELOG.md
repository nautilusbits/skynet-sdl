# Changelog

Version in `Directory.Build.props`. Each batch of changes raises it: a fix
the patch number, a feature the minor. Releases on GitHub start at 1.0.0.

## 1.2.0 (2026-10-28)

Android setup, touch controls, speed, and fixes from GitHub issues.

- Android: the first start asks for All files access and plays from
  the SkyNET folder in place (data, mods, settings, saves), as on the
  PC. Without it the folder is copied into Android/data and new or
  changed mods are synced at every start. Setup (long-press the icon)
  switches between the two. Older installs move over once.
- Touch buttons redrawn in the HUD's style; CONTROLS > TOUCH LAYOUT
  moves and resizes them (touch screens only).
- A loading screen with a progress bar for long loads (deathmatch
  player models, map loads, bakes); on Android the start shows the
  backdrop instead of a black screen.
- IMPROVED robot AI is about 3x cheaper: Future Shock mission 1 went
  from 7 to 60 fps on a phone.
- Mod models decode a shared texture atlas once: deathmatch with an
  avatar mod loads several times faster, with ~300 MB less GPU memory.
- Less per-frame garbage (12-bot deathmatch: ~31 to ~3 MB/s); the map
  block check is skipped when nothing changed; phones start with
  shadows off.
- TEXTURE FILTER in ENHANCED OPTIONS, NEAREST to ANISOTROPIC 16X (#7).
- Bolts glow again without casting shadows (#4); no depth pre-pass,
  which hid robots on AMD (#1).
- Deathmatch players hold their gun and scream and grunt as in DOS
  (#1).
- The jeep leans toward the lower side of a slope, not the higher
  (#10).
- A thrown satchel lies 15 s before it goes off, as in DOS (#10).
- ENGRAVING renderer; RetroEng 0.47.0; Android code compiled ahead of
  time; network permissions on Android; the phone keyboard opens only
  for a text box; a performance line in the log every 5 s.

## 1.1.0 (2026-10-25)

Fixes from the first GitHub issues.

- Scenery sprites (trees, posts, rubble, fires) are drawn at DOS's
  1:1 scale; they stood twice their size (#2). Maps re-bake once.
  skynet-hd's sprite models follow (new skynet-hd.zip).
- The 640 x 480 HUD and weapon art is the default; DISPLAY has a HUD
  ART row (HI-RES / DOS). Older settings files switch to HI-RES once
  (#3).
- Bullets hitting a robot or another machine show their impact, as in
  DOS (#8); robots' and other players' shots too.
- Laser and rocket shots no longer cast shadows in ENHANCED (#4).
- Dialogs stay below the title's menu bar (DISPLAY covered it at 16:9).
- README: installation steps for every platform; Linux and macOS
  builds from GitHub Actions.

## 1.0.0 (2026-10-22)

The first public release: source (MIT), Windows and Android builds.

- The engine's packages come from its public feed
  (nautilusbits.github.io/retroeng-packages), RetroEng 0.33.1; the
  RetroEng Non-Commercial Binary Licence in README and
  THIRD-PARTY-NOTICES.txt.
- README: downloads, screenshots and animations (docs/media).
- The APK carries the licences (assets/licences/), the engine's too.
- LICENSE: MIT, with a note on the original games' data, data/tables
  and trademarks.
- README rewritten for players and contributors: disclaimer, the data
  needed, running, building, tests, mods, licence.
- THIRD-PARTY-NOTICES.txt titled for the whole project; the zlib text
  names SDL, DotRecast and Recast. SkyNet.Game.csproj: Recast and
  Detour are zlib, not MIT.
- The disclaimer "Unofficial fan port. Not affiliated with Bethesda.
  Requires the original game data." in the game's and the editor's
  --help and the editor's About box.
- The Future Shock data tests (haze, best-fit WLD, editor missions,
  MAP.013 switch, mission generation, trigger graph) are skipped, not
  passed, when its data is missing.
- Example addresses in docs and tests use the documentation ranges
  (203.0.113.x, 198.51.100.x).
- README and Directory.Build.props no longer name a private package
  feed.

## 0.52.0 (2026-10-22)

- The game is skynet-sdl.exe (was SkyNet.exe, which replaced the
  original SKYNET.EXE in a SkyNET folder: Windows ignores case).

## 0.51.0 (2026-10-22)

- Phones: HUD, FPS and console text sized by the screen's physical
  size (at least 2.5 mm a line); DISPLAY > UI SCALE (AUTO, 1X, 1.5X,
  2X). Android starts with the MODERN HUD.
- Screens narrower than 4:3 (a foldable's inner screen): menu screens
  keep 4:3 with bars, the view keeps 4:3's horizontal field, the gun
  its 4:3 size.
- The Android build is named SkyNET-<version>.apk.

## 0.50.0 (2026-10-22)

- Fix: Android crashed on entering any map since 0.48.0 (GC settings
  its runtime does not support).
- Android: logs in Android/data/net.skynetport.skynet/files/logs,
  readable over USB; --log-dir sets the log folder on any platform.

## 0.49.5 (2026-10-22)

- Fix: in the editor's model viewer, Pause and stepping did not stop
  a mod robot's clip.

## 0.49.4 (2026-10-22)

- Fix: the editor's model viewer did not play a mod robot's clips
  (the replaced robot stood still).

## 0.49.3 (2026-10-22)

- Fix: on the MODERN HUD a mod's 3D rocket launcher slid off the
  screen (0.49.1's slide along its almost flat shot line). A gun whose
  shot leaves near eye height now keeps its place.

## 0.49.2 (2026-10-22)

- Muzzle flashes are added to the scene in the enhanced look, so an
  upscaled DOS flare no longer shows as a square round robots' and the
  player's shots (the HD mod ships a soft flare for TEXTURE.219).

## 0.49.1 (2026-10-22)

- Fix: on the MODERN HUD shots left the DOS muzzle point beside a
  mod's 3D gun (laser bolts flew out right of the barrel). The gun now
  slides along the shot's line when the HUD moves it.
- A mod's 3D gun hides the DOS muzzle flash of the player's own shot
  and draws only its own (sidecar flashSize; 0 = none). Robots' and
  other players' flashes stay.
- The deathmatch scoreboard scales with the screen on the MODERN HUD:
  about twice as large at 1080 lines and up, at most 60 % wide.

## 0.49.0 (2026-10-22)

- Deathmatch bots drive the jeep and fly the HK: they board a free
  parked vehicle when no enemy is close, drive the jeep on its own
  navmesh (baked per arena, cached) while the turret aims and fires,
  ram players on foot, fly the HK above the terrain round buildings,
  and get out when the hull is low or the vehicle is stuck (the HK
  lands first). On the 7 arenas with vehicles bots spent 6.5-8.9 of
  36 bot-minutes in vehicles; no HK crashed.
- Fix: getting out of a parked HK could leave the soldier inside its
  hull. Players and bots now step out 90 units to the side (DOS: 20).

## 0.48.0 (2026-10-22)

- Levels of detail for mod models: NAME.lod1.glb / NAME.lod2.glb
  beside a model (props, vehicles, sprite models, robot segments,
  wreck parts, skinned robot templates) are drawn by the model's size
  on screen (sidecar "lod": screen fractions, hysteresis). With
  skynet-hd built with LODs MAP.240's start view draws 3.0 M
  triangles instead of 14.8 M. Weapons in hand, cockpits, avatars
  and drops stay at full detail. See docs/modding.md.

## 0.47.0 (2026-10-22)

- DETAIL (LOW / MED / HIGH) also sets the size of HD and mod textures:
  HIGH full, MED half each side, LOW a quarter; from the next map
  load. With skynet-hd on MAP.210 the GPU holds 2704 / 762 / 319 MB
  of textures. The original look is unchanged.
- First start: the GPU's name picks DETAIL (phones and old Intel HD
  LOW; Intel UHD/Iris, AMD integrated, Apple and 2-4 GB cards MED;
  others HIGH).
- The console's stats and the exit log show GPU texture/mesh MB.
- DotRecast's diagnostics during a navmesh bake go to the log
  instead of the console.

## 0.46.1 (2026-10-22)

- The collection after a map load also compacts the large object
  heap, so the holes a load leaves among decoded textures go back to
  the system instead of staying committed (the after-load GC takes
  ~40 ms instead of ~20 ms, behind the loading screen).

## 0.46.0 (2026-10-22)

- ROBOT AI IMPROVED: walking robots path-find on a navmesh (three
  bodies: walker, terminator, big walker) baked round the robots' and
  the soldier's starts and cached in cache/nav/. On MAP.271's
  catwalks back-offs drop from 64 to 4 and robots reach the soldier
  sooner; they stop at a partial path's end instead of pressing at a
  ledge. CLASSIC is unchanged (never bakes or reads a mesh). Console:
  navmesh on|off draws the meshes and paths in the campaign too.
- The nav cache format is version 2: old .nav files rebake once.

## 0.45.1 (2026-10-22)

- RetroEng 0.33.0 and less RAM with big mods: HD texture pixels are
  dropped once uploaded (engine and game), sprite models are decoded
  only for the current map, mip scratch lives in native memory. The
  managed heap after a MAP.210 load with skynet-hd: 2925 -> 212 MB;
  the process peaks at 2.3 GB instead of 3.2 GB in play.
- The debug overlay shows the renderer's GPU memory (textures and
  meshes) next to the GC heap.

## 0.45.0 (2026-10-22)

- The DOS automap (Tab, pad R3): a paused 3D overview of the meshes
  the player has seen within 5x5 cells, the terrain outdoors, a green
  arrow for the player; arrows turn and tilt, + and - zoom. Flat
  colours at DETAIL LOW/MED as DOS, no haze. The seen set is kept in
  saves (format 9; format 8 still loads) and wiped on a map change or
  by the cheat clearautomap. Campaign only.
- OPTIONS > DETAIL: the RESOLUTION slot is now GAMEPLAY, a screen
  with ROBOT AI (CLASSIC / IMPROVED). The HUD art choice stays in
  the console (hud dos|hires) and the settings.

## 0.44.1 (2026-10-22)

- No full GC during play: each map load ends with one full
  collection behind the loading screen, and play runs in the GC's
  SustainedLowLatency mode. Gen1/gen2 pauses (10-87 ms) now fall
  inside the load instead of the first seconds of play.

## 0.44.0 (2026-10-22)

- Enhanced look: a real sky. Dusk maps (missions 5-8) get a sunset
  instead of the DOS dome (a 24-sided red cylinder): a horizon glow,
  deep red to a dark zenith, a low sun glow and a thin cloud deck lit
  from below; the haze takes the horizon's colour, so the terrain
  fades into the sky with no band. Night maps get stars in depth
  (the DOS stars plus faint ones, a Milky Way band), drifting night
  clouds and a moon made from NASA's LRO map (public domain) with a
  halo. The original look is unchanged; the falling-moon joke stays.
- Mods: tables skies and night_skies set the colours, glow, clouds
  and stars per map (docs/modding.md).

## 0.43.2 (2026-10-21)

- RetroEng 0.31.1: a fresh start on MAP.240 with skynet-hd crashed
  in the texture upload (one staging buffer over 2 GB); the engine now
  uploads in parts of at most 256 MB. MAP.240 loads in 4.4 s.

## 0.43.1 (2026-10-21)

- Fix: an action with two keys (e.g. S / DOWN) could show a different
  key in its CONTROLS box after each restart.

## 0.43.0 (2026-10-21)

- Deathmatch bots on foot walk paths on a navmesh baked from the
  arena's static collision (DotRecast, zlib), instead of steering by
  feel alone: in a 2-minute 3-bot match on MAP.605 they got stuck 8
  times instead of 39 and took 20 pickups instead of 12. Bakes over
  300 ms are cached in cache/nav/ beside the exe. Console:
  navmesh on|off draws the mesh and the bots' paths.

## 0.42.2 (2026-10-21)

- Fix: god mode did not cover the jeep or the HK. The cheat set only
  the soldier's flag, so the vehicle's hull still took damage (own
  rocket blasts near a wall) and an emptied hull killed the soldier.

## 0.42.1 (2026-10-21)

- RetroEng 0.31.0: textures build in parallel. First load of MAP.210
  with skynet-hd: 13.4-16.7 s -> 3.5 s (uploads 10.7-12.8 s ->
  1.3 s). The original look is bit-identical.

## 0.42.0 (2026-10-21)

- Map changes with big graphics mods: mod textures and meshes stay
  loaded across maps (current and last map always, others up to
  1 GiB), and sprite models, robots, wreck parts and owned guns are
  built during the load instead of on first draw. Returning to
  MAP.210 from an interior with skynet-hd: ~3.5 s instead of ~9.5 s,
  no multi-second freezes after a load; the first load is longer
  (the work moved into it).
- The load log shows each step's time and a "first 10 s" line with
  frames over 33 ms and what was built in them.
- Fix: dropped pickups' 3D models could draw from meshes freed by
  the previous map.

## 0.41.1 (2026-10-21)

- 3D guns whose DOS art has a single frame (the machine gun, the
  laser cannon) now kick and flash at the muzzle on a shot too; the
  shot lasts their cooldown (0.06-0.2 s).

## 0.41.0 (2026-10-21)

- 3D weapons in hand read like the DOS art: minLight (default 0.35)
  keeps them lit on night maps, a muzzle point shows the DOS flash
  at the muzzle on a shot (flashSize), pivot sets what recoil, swing
  and the throw dip turn around, and the gun lifts by itself when a
  wide screen raises the HUD panel (offsetWide overrides it).
- --fixed-step: the capture's fixed clock (one tick per frame)
  without saving frames, for repeatable scripted screenshots.
- Editor mission-check test given 480 s (it failed at random).

## 0.40.0 (2026-10-21)

- Mods can replace the weapon in hand with a 3D model:
  models/viewmodels/<WEAPON>.glb (+ optional .json with offset,
  scale, rotation, recoil, swing, sway), enhanced look only. It is
  drawn in its own depth layer under the HUD, so it never sinks into
  walls, and is lit by the scene's lights. Shots kick it back, the
  pipe swings, throws lower it. Without a model, and in stereo, the
  DOS art stays. See docs/modding.md, View-models.

## 0.39.1 (2026-10-21)

- Sprites a mod draws as 3D models stand on the floor under them
  indoors. They used the billboard's foot, 16 units under the
  record, so flat models such as paper litter sank out of sight and
  cans sat a little low. With no floor in reach the old foot stays.

## 0.39.0 (2026-10-20)

- Smooth motion above 60 FPS: robots (all segments, path vehicles),
  shots and the rocket eye, impact pieces, flying wreck parts, doors,
  lifts and other movers, the water level and the torpedo ride are
  drawn between the last two 60 Hz ticks, like the camera already
  was. Presentation only: demos, saves and the network are unchanged;
  teleports, respawns and loads snap instead of gliding.

## 0.38.0 (2026-10-19)

- Splash window while the game and SkyNetEdit load: title art,
  versions, build date, load status; the main window stays hidden
  until its first frames are drawn (no white or black window at
  start).
- RetroEng 0.29.0.

## 0.37.0 (2026-10-19)

- Settings file is created on first start with every setting and
  every key and pad binding, defaults included, so it can be read
  and edited by hand.
- Noclip has no default key (was F).
- Console: Quake-style bind <key> <command> (or +action), unbind,
  unbindall, bindlist; saved as commandBinds.
- modkit: per-texture relief targets, full-strength normal maps,
  relief on ground fill tiles; propkit builds DOS-faithful prop models.

## 0.36.0 (2026-10-19)

- ROBOT AI: IMPROVED (default) - robots steer round obstacles, flyers
  stay out of scaffolding, no idling or firing into walls; CLASSIC
  keeps DOS exactly (console robotai, --robot-ai).
- SkyNetEdit: splash screen, versions and build date in the corner,
  new About window.
- Dev console shows the game and engine versions.
- RetroEng 0.28.0.

## 0.35.0 (2026-10-18)

- Shoot the moon: OW!, and after 25 hits it falls (DOS's joke).
- SkyNetEdit: viewers have their own lit scene (0.34.1 changes).
- Ambient occlusion off by default on Android.
- RetroEng 0.27.0 (draw and canvas batching).

## 0.34.1 (2026-10-18)

- SkyNetEdit: model, robot and effect viewers have their own lit
  scene (no level haze or stars behind them); Model Viewer closes.
- RetroEng 0.25.0.

## 0.34.0 (2026-10-18)

- SkyNetEdit: whole robots in Assets; replace a robot with one glTF
  model (Export / Replace / Revert, clips matched by name).
- SkyNetEdit: robots stand on the floor in the Viewport; panels have
  close buttons.
- Vehicle reticle drawn sharp with the MODERN HUD.
- Dev: --capture frames and audio, SkyNetEdit --script (docs/video.md).
- Removed the louder-blasts and pickup-models sample mods.

## 0.33.0 (2026-10-15)

- MODERN HUD shows whole messages (wrapped, growing upward).
- SkyNetEdit: model, robot and effect viewers show their 3D docked,
  floated and in any layout; trigger graph entity picker and
  whole-level verify.
- Removed the smart-AI and fire-hurts mod tables (old mods still
  load; the tables are ignored).

## 0.32.0 (2026-10-15)

- DISPLAY > HUD STYLE: MODERN, full-frame view with compact corner
  widgets scaled to the screen (CLASSIC stays the DOS panel).
- POINT CLOUD and TERMINATOR VISION brighter: lifted points, the
  scene dim behind them, finer lattice up close.
- RETRO ASCII keeps the same cell grid at every resolution.
- SkyNetEdit: view distance preference (Preferences, View menu,
  view_distance).
- RetroEng 0.24.0.

## 0.31.2 (2026-10-15)

- SkyNetEdit: trigger graph nodes no longer overlap after auto
  layout; adding a node or rewiring a link is one undo step.
- SkyNetEdit: Help > Keys lists every key, Terrain mode included.
- ENHANCED: SkyNET's framed pictures and posters (378/000-006) are
  matte, not stone.

## 0.31.1 (2026-10-15)

- SkyNetEdit: screenshots of a floated Viewport; effect preview no
  longer draws the scene twice.
- RetroEng 0.23.0.

## 0.31.0 (2026-10-15)

- RETRO ASCII: small 3x5 glyphs, scene colours behind them, no
  outline glyphs.
- POINT CLOUD and TERMINATOR VISION without edge lines.
- RetroEng 0.21.0.

## 0.30.0 (2026-10-15)

- SkyNetEdit: the 3D view is a dockable Viewport window (move, resize,
  float to another monitor); only the visible view is rendered.
- RETRO ASCII: full colour by default, finer glyphs, colour per pixel.
- TERMINATOR VISION turns off when a HUMAN is picked.
- Future Shock: no more red specks in the distance (ORIGINAL look).
- RetroEng 0.20.0.

## 0.29.0 (2026-10-15)

- Deathmatch: optional TERMINATOR VISION (NORMAL / POINT CLOUD) on
  the host and join screens; a TERMINATOR's machine vision drawn as
  a red point cloud. Local only, off by default.

## 0.28.0 (2026-10-15)

- RETRO ASCII and POINT CLOUD drawn cleanly in the ORIGINAL look too
  (ASCII edges back, no near-plane change).
- SkyNetEdit MCP: play-test up to 120 s while the editor keeps
  working; undo/redo and tool errors from the engine.
- RetroEng 0.16.1.

## 0.27.0 (2026-10-15)

- DISPLAY: RENDERER (DEFAULT / RETRO ASCII / POINT CLOUD) and ASCII
  COLOUR, applied at once and saved; console `renderer`, `--renderer`.
- Menu pointer held state from the engine's input.
- RetroEng 0.16.0.

## 0.26.0 (2026-10-15)

- SkyNetEdit: MCP server for AI agents (opt-in, localhost; missions,
  entities, triggers, tables, terrain, play-test; all undoable).
- SkyNetEdit: terrain collision follows each brush dab; effect
  preview in its own view; table edits undo after a save.
- Menu lists scroll with the wheel, scrollbar and finger drag (mods,
  gamepad buttons, servers, briefing).
- RetroEng 0.14.0.

## 0.25.0 (2026-10-15)

- SkyNetEdit: duplicate and copy/paste across maps, drop to floor,
  snap/align/distribute; drag models, robots, pickups and lights into
  the view; Outliner search, filters, select all matching, hide and
  lock; the gizmo moves the whole selection.

## 0.24.1 (2026-10-15)

- SkyNetEdit: Make unique and robot-type edits show at once (needed a
  save before; the robot vanished meanwhile).

## 0.24.0 (2026-10-15)

- SkyNetEdit: Details panel edits the selected entities (transform,
  record fields, act parameters, robot type, Make unique, items,
  lights, multi-select); Replace model / Remove replacement for props,
  robots and sprites with a sidecar grid; Space plays/pauses viewers.
- RetroEng 0.12.0.

## 0.23.0 (2026-10-15)

- Mods replace terrain (`maps/WLD.NNN`) and add maps and missions
  (`maps/MAP.NNN`, `missions/MNN.json`).
- SkyNetEdit: terrain mode (brushes, texture paint, heightmap PNG and
  glTF import, generator) and File > New Map / New Mission.

## 0.22.0 (2026-10-15)

- SkyNetEdit: trigger graph — the map's triggers as a node graph over
  the DOS acts, edited by linking nodes and setting parameters, with
  validation and per-chain verify; saved in the mod's map layer.

## 0.21.0 (2026-10-15)

- GAMEPAD controls: Xbox-layout default buttons (most pad buttons did
  nothing, notably on Android) and a screen to rebind them with the
  pad (RetroEng 0.11.0).
- Mods tune ENHANCED effects (`tables/effects.json`, sample mod
  `big-fire`); SkyNetEdit gets an effect editor with live preview.
- Version shown on the main menu.

## 0.20.0 (2026-10-15)

- ENHANCED much cheaper per pixel (RetroEng 0.10.0: plain-surface
  shader, merged light loops, cheaper shadows and sky).
- Android: RENDER SCALE AUTO (default) draws about 1080p's pixels
  instead of native; WINDOW/RESOLUTION hidden.

## 0.19.1 (2026-10-14)

- ENHANCED: switching AMBIENT OCCLUSION or GLOW off now saves their
  cost (their passes kept running; RetroEng 0.8.1).

## 0.19.0 (2026-10-14)

- Mods replace sounds (`sounds/NAME.wav`) and tune them
  (`tables/sounds.json`: volume, range, pitch, priority, loop); sample
  mod `louder-blasts`.
- SkyNetEdit: sound viewer (waveform, uses, replace in mod) and HMI
  music player.

## 0.18.0 (2026-10-14)

- Mods draw sprites (pickups, scenery, drops) as 3D models in ENHANCED
  (`tables/sprite_models.json`, sample mod `pickup-models`).
- SkyNetEdit: sprite viewer with uses on the mission and "Replace with
  3D model".

## 0.17.1 (2026-10-14)

- Lamps glow through dust and haze (were dark inside a dust gust).
- Distant smoke columns stay in place (slid with a strafing player).

## 0.17.0 (2026-10-14)

- SkyNetEdit: Tables window, game tables edited in a grid (weapons,
  ammo, items, difficulty, robots); mods save only changed rows.
- Mod table files patch rows instead of replacing the table.
- Mod robot models: template frames play, full PBR materials.
- Android: tap that starts play doesn't fire (RetroEng 0.8.0).

## 0.16.0 (2026-10-14)

- Robots draw as animated models (RetroEng 0.7.0: one animated-model
  object, GPU skinning); the DOS look is unchanged.
- Mods replace a whole robot with one skinned `models/NAME.robot.glb`;
  the editor exports that template (bones per segment, clips per AIS
  block).

## 0.15.0 (2026-10-14)

- Mods add or change robot types (`robots/*.json`, sample mod
  `flencer-mk2`).
- SkyNetEdit: robot/model viewer with animations and table data;
  glTF (.glb) export of models and robots, `--export-gltf`.

## 0.14.0 (2026-10-14)

- Phone viewer framed like MDK: each eye fills its half, milder lens
  distortion, larger HUD; new default viewer GENERIC (RetroEng 0.6.0).
- Future Shock has its own material table.
- SkyNetEdit: material changes on Edit > Undo; ENHANCED options in
  Preferences; mod weather (dust) shows in the viewport.

## 0.13.1 (2026-10-14)

- A use-key press no longer takes a map exit a floor above or below
  (Future Shock MAP.013 switch teleported out of the building).

## 0.13.0 (2026-10-14)

- Dust comes in gusts: clear air, then a dust cloud rolls through.
- 3D VIEW: BACKLIGHT keeps the phone bright inside a VR viewer.

## 0.12.0 (2026-10-14)

- Weather: clouds (hide the stars), haze, dust and wind (sample mod
  `wasteland`); the rain sound is much quieter (RetroEng 0.5.0).
- Mod `fire-hurts`: fires burn the player (DOS fires never do).
- Mod `smart-ai`: robots hear and alert, strafe, take cover, lead shots.

## 0.11.0 (2026-10-14)

- Weather: rain (sample mod `rain`), combinable with the storm; thunder
  now audible; mods may supply thunder.wav / rain.wav.
- Deathmatch protocol 8: a scanner holder shows on others' scanners and
  they hear its beep.

## 0.10.3 (2026-10-14)

- Gyro: late sensor readings turn the view (was: skipped); the log
  shows the longest gap and the viewer's screen size (RetroEng 0.4.2).
- Deathmatch motion detector: DOS's top-down plot, animated screen and
  sweep beep.

## 0.10.2 (2026-10-14)

- `--help` prints in a real console window (RetroEng 0.4.1).

## 0.10.1 (2026-10-14)

- Phone viewer: menus fit each eye, each eye sits under its lens.
- Gyro head tracking: falls back to other orientation sensors; the log
  lists the phone's sensors and the readings (tag Head).
- Android: no constant firing after a tap starts play; launcher name.
- Icons for the game, the editor and Android.
- SkyNetEdit remembers all its settings.
- `--help` prints in the console it was typed in.
- Tracy profiler removed (RetroEng 0.4.0).

## 0.10.0 (2026-10-14)

- ENHANCED materials: textures are matte by default; a shipped table
  marks stone, concrete, wood, metal, painted, glass and emissive
  surfaces; mods override it with `materials.json`; SkyNetEdit sets a
  texture's material in the asset viewer (RetroEng 0.3.0).
- TACTICAL screen: right palette, the unit's model spins (both games).
- Android: an optional mods folder mirrored into the app at every start.
- Linux fixes in the engine (log file, project paths).

## 0.9.0 (2026-10-13)

First numbered version: the whole C# port (campaign, Future Shock,
deathmatch, ENHANCED look, editor, Android, stereo/VR viewer) up to:

- Engine moved to its own repo and packages (RetroEng 0.2.0).
- Crash on a map exit in split threading mode fixed.
- ENHANCED fire has a volume; the DOS fire sprite no longer shows behind it.
- Storm mod: lightning and thunder (sample mod `storm`).
- SkyNetEdit: Assets panel (textures, sprites, models, sounds, tables).
- `--help` for the game.
