# Profiling SkyNET

In-game, F12 (or the console's `debug`) shows the game's zones as milliseconds a frame in the debug
overlay (`RetroEng.Diagnostics.ZoneTimes` becomes the profiler backend while it is on).

What SkyNET sends (static `ZoneId`s, nothing allocated per zone):

| where | zones |
| --- | --- |
| the loop | `Tick` (each 60 Hz tick), `Render` (each frame), the main frame mark |
| a tick (`Tick`) | `Session`: `Player` (with `Vehicle` while riding), `Weapons` (`Projectiles`), `Robots` (the robots and their AI), `Behaviour` (`Path vehicles`), `Pickups`, `Exit` (a map change); then `Audio update`, `HUD tick` |
| a frame (`Render`) | `Draw level`, `Draw robots`, `Draw cockpit`, `Draw weapon`, `Draw HUD`, `Draw menu`, `Draw overlay`, `Draw console`, `Present` |
| entering a map | `Map enter`: `Map parse`, `Terrain`, `Behaviour build`, `Collision build`, `Robots place`, `Behaviour start`, `Pickups place`, `Level load` (`Level build`, `Robot meshes`, `Warm up`, `Upload flush`, `Music start`) |
| saves | `Save game` (`Save capture`), `Load game` (`Save restore`), `Switch game` (NEW GAME -> FUTURE SHOCK and back) |
| other threads | `Mix` on `Audio mixer`, `Music synth` on `Music synth` |
| plots, each frame | `Draw calls`, `Triangles`, `Upload submits`, and the GC's (`GC heap`, `GC allocated/frame`, `GC gen0..2/frame`) |

The game's zones are listed in `SkyNet.Game/DevTools/GameZones.cs`, the app's in
`SkyNet.App/AppZones.cs`.
