# SkyNET SDL

An unofficial, open-source C# + SDL3 port of *The Terminator: SkyNET* (Bethesda Softworks, 1996)
that also plays *The Terminator: Future Shock*, with a mission editor (`SkyNetEdit`).

Unofficial fan port. Not affiliated with or endorsed by Bethesda Softworks, ZeniMax or the owners
of The Terminator. **No game data is included.**

## Download

[Releases](https://github.com/nautilusbits/skynet-sdl/releases): the Windows build
(`skynet-sdl.exe`, the editor `SkyNetEdit.exe`), the Android APK and the optional HD mod
`skynet-hd.zip`. The game's data is not included: you need your own copy (below).

## Screenshots

### The original DOS look, and ENHANCED

![DOS on the left, ENHANCED on the right](docs/media/dos-vs-enhanced.jpg)
*The same moment on MAP.280 at dusk: the DOS renderer on the left, ENHANCED (sky, haze, lighting) on the right.*

| DOS | ENHANCED |
|---|---|
| ![DOS look](docs/media/dos.jpg) | ![ENHANCED look](docs/media/enhanced.jpg) |
| *The original 1996 look, drawn as SKYNET.EXE drew it.* | *The same frame in ENHANCED: lit sky, depth haze, muzzle-flash lighting.* |

![DOS and ENHANCED wipe](docs/media/dos-enhanced-wipe.gif)
*An HK-Aerial shot down over the MAP.280 base, wiping between DOS and ENHANCED.*

![HK explosion](docs/media/enhanced-explosion.jpg)
*The HK goes down in ENHANCED.*

### Other renderers

![Retro ASCII](docs/media/ascii.jpg)
*RETRO ASCII: the world drawn as fine coloured glyphs.*

![Retro ASCII, green](docs/media/ascii-green.jpg)
*RETRO ASCII in green phosphor, with an explosion.*

![Point cloud](docs/media/pointcloud.jpg)
*POINT CLOUD: the scene as a cloud of lit points.*

![TERMINATOR VISION](docs/media/terminator-vision.jpg)
*TERMINATOR VISION in deathmatch: a machine's red point-cloud view with target tags.*

### SkyNetEdit

![Editor map view](docs/media/editor-map.jpg)
*SkyNetEdit on MAP.280: Outliner, a selected robot with its gizmo, and its Details.*

![Editor terrain mode](docs/media/editor-terrain.jpg)
*Terrain mode: raise, lower, smooth and paint the heightmap with a brush.*

![Editor model viewer](docs/media/editor-model-viewer.jpg)
*The Model Viewer playing a robot's animation clips (here the HD mod's T-800).*

### The HD mod (skynet-hd)

![HD robots](docs/media/hd-mod-robots.jpg)
*Raptor walkers in the MAP.210 trench, as HD models.*

![HD HK-Aerial](docs/media/hd-mod-hk.jpg)
*An HK-Aerial over the city ruins, and the 3D gun in hand.*

![HD gun in hand](docs/media/hd-mod-gun.jpg)
*A 3D assault rifle firing at an HK at dusk.*

![HD HK cockpit](docs/media/hd-mod-hk-cockpit.jpg)
*Flying the HK on MAP.270.*

### Deathmatch with bots

![Deathmatch](docs/media/deathmatch.gif)
*Deathmatch on MAP.605 against eight bots: jeeps and HKs included.*

![Deathmatch, a jeep burns](docs/media/deathmatch-jeep.jpg)
*A bot's jeep burning at the gas station.*

![Deathmatch, bots in vehicles](docs/media/deathmatch-bot-jeep.jpg)
*Bots take the vehicles: an HK flies low past a parked jeep.*

### Future Shock

![Future Shock](docs/media/future-shock.jpg)
*Future Shock's data runs in the same engine.*

## You need

A legal copy of SkyNET v1.01 and/or Future Shock: the folder holding `MDMDMAP2.BSA` (SkyNET) or
`MDMDMAPS.BSA` (Future Shock). Future Shock's data is found beside SkyNET's where the installer puts
it (`shock/GAMEDATA`).

## Running

```
skynet-sdl.exe --data <the game's data folder>
```

Without `--data` the game tries `SKYNET_DATA`, then `gamedata` beside the exe or
its parent. The first start bakes the maps ("PREPARING GAME DATA").

Android: on the first start the app asks for the game's folder and copies it into its own storage.

Main options (`skynet-sdl.exe --help` lists all):

| Option | Effect |
| --- | --- |
| `--data <dir>` | the game's data folder |
| `--mission N` / `--map N` | start mission N / on MAP.NNN |
| `--mod <name>[,name]` / `--no-mods` | mods for this run / none |
| `--look original\|enhanced` | DOS look or the enhanced renderer |
| `--gpu d3d12\|vulkan` | the graphics backend |
| `--width N --height N`, `--fullscreen` | the window |
| `--difficulty low\|med\|high` | robots' fire rate and damage |
| `--host=MAP.NNN` / `--join=HOST[:PORT]` | network deathmatch ([docs/multiplayer.md](docs/multiplayer.md)) |
| `--dedicated --config server.json` | dedicated server (`config/server.sample.json`) |
| `--record NAME` / `--playdemo NAME` | demos ([docs/demos.md](docs/demos.md)) |

## Building

.NET 10 SDK. Packages come from nuget.org; the engine (`RetroEng.*`) from its public feed
(`nuget.config`: https://nautilusbits.github.io/retroeng-packages/index.json).

```
dotnet build SkyNet.slnx
```

| Target | Command | Needs |
| --- | --- | --- |
| Release (Native AOT) | `build-release.bat` → `release\skynet-sdl.exe` (`-r linux-x64` for Linux) | VS C++ tools; Vulkan SDK optional (Vulkan/Metal shaders) |
| Editor | `build-editor.bat` | |
| Android | `build-android.bat` → `release-android\SkyNET-<version>.apk` | android workload, Android SDK, JDK 21, Vulkan SDK |

The release publish by hand: `dotnet publish src\SkyNet.App\SkyNet.App.csproj -c Release -r win-x64 -o release`.

Engine work: with a RetroEng checkout at `..\..\retroeng` (or `RetroEngRoot`), build with
`-p:UseLocalRetroEng=true` to use project references instead of the `RetroEng.*` packages.

| Project | What it holds |
| --- | --- |
| `SkyNet.App` / `SkyNet.Android` / `SkyNet.Edit` | Game executable (desktop, Android) and mission editor. |
| `SkyNet.Game`, `SkyNet.Data`, `SkyNet.Formats` | Game logic, extracted tables, DOS file formats. |
| `SkyNet.Upscale` | Optional texture upscaling through an external Real-ESRGAN (ncnn/Vulkan) tool. |

## Tests

```
dotnet test -c Release
```

Set `SKYNET_DATA=<the SkyNET data folder>` for the tests that need the game's data; without it they
are skipped. Future Shock tests also need its data beside SkyNET's.

## Mods

Mods live in `mods/` beside the exe and are turned on under OPTIONS - MODS or with `--mod`.
[docs/modding.md](docs/modding.md) is the reference; `mods-samples/` holds small examples.
The HD pack **skynet-hd** (`skynet-hd.zip` on the Releases page; unzip into `mods/` or copy the zip there)
is released separately under **CC BY-NC 4.0** (non-commercial, own credits).

## Licence

MIT (see `LICENSE`) for the port's code, docs and sample mods. Third-party components:
`THIRD-PARTY-NOTICES.txt`. `data/tables` holds values read from the original SKYNET.EXE v1.01,
included for interoperability; `tools/SkyNet.Tables` rebuilds them from your own copy. The licence
grants no rights to the original games or their data.

The engine, RetroEng, is not part of this repository: its `RetroEng.*` packages are binary-only
under the RetroEng Non-Commercial Binary Licence (`LICENSE-RetroEng.txt` in each package). Builds
that ship the engine may be shared free of charge but not used commercially (no sales, paid access
or advertising) without the engine's copyright holder's written permission. SkyNET, Future Shock, Bethesda and The
Terminator are trademarks of their owners.
