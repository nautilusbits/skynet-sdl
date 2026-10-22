# Video capture (`--capture`)

The game (`SkyNet`) and the editor (`SkyNetEdit`) make a video's frames themselves, without a screen
recorder, at any speed the machine manages:

```
skynet-sdl.exe --capture <dir> [--capture-fps 60] [--capture-frames N] ...
SkyNetEdit.exe --capture <dir> [--capture-fps 60] [--capture-frames N] ...
ffmpeg -framerate 60 -i <dir>\frame_%06d.bmp -c:v libx264 -crf 18 -pix_fmt yuv420p -movflags +faststart out.mp4
```

- **A fixed step.** Every frame is exactly 1/fps s of game time, whatever the wall clock says: the
  loop's clock (`FixedLoop`'s driven clock), the menus' and the network game's frame time, the
  animation clock (the level's records, robots, drops, weather) all read `FrameCapture`'s clock,
  which moves one step per frame taken. A slow frame (a 1920 x 1080 BMP written) costs nothing in
  the video. At 60 fps a frame is one 60 Hz tick.
- **The whole window.** Each presented frame (the HUD, the menus and the console included; the
  editor's panels too) is saved as `<dir>\frame_000000.bmp`, `frame_000001.bmp`, ... (32-bit BMP,
  the window's size) through the screenshot path (`Renderer.Present`'s screenshot). A frame that
  also has a screenshot of its own (`--screenshot`, `shot`) is copied into the capture.
- **Sound** (the game only): the mixer is pulled as the audio thread would (`Mixer.Mix`, by
  `AudioCapture`), 44 100 / fps stereo sample frames after each frame (the fraction carried, so N
  frames are exactly N x rate / fps), into `<dir>udio.wav` (16-bit PCM, the mixer's rate). No
  device is opened, `--offscreen` included; the score stays off under `--offscreen` / `--no-music`.
  `ffmpeg -framerate 60 -i <dir>rame_%06d.bmp -i <dir>udio.wav ... -c:a aac -b:a 192k out.mp4`.
- **Ends** at `--capture-frames N` (the same as `--frames N`), a menu script's `quit`, or when the
  game is closed. The log says `Capture: N frames at F fps and S sound frames at R Hz in <dir>`
  (the editor: `Capture: N frames at F fps in <dir>`).
- **The game** runs a capture on one thread (`--threads single`) with VSync off; `--offscreen`
  (or `SKYNET_OFFSCREEN=1`) works, with `--width` / `--height` for the frame size. The editor
  captures no sound.
- **Still on the wall clock**: the menu script's `sleep MS` (it paces network peers), the frame-time
  statistics. The editor's ImGui (its animations, model viewer, effects preview) follows the capture's clock.

A capture is about 8 MB a frame at 1920 x 1080: encode a shot and delete its frames before the next.

## Scripted shots

`--menu-script` steps run one a frame, so a script is a timeline: `wait N` holds N frames,
`hold Fire` / `release Fire` hold a game action (names in `CampaignControls`), `run <command>` runs a
console command. In a deathmatch hosted with `--cheats`, `run tp x y z yaw` every frame flies the
camera along a path (with `run noclip on`), and `run face` every frame keeps the view on the nearest
living player, one in plain sight first. For example, 20 s of a TERMINATOR's point-cloud vision
hunting bots, at 1920 x 1080:

```
skynet-sdl.exe --host=MAP.605 --port=0 --no-discovery --bots=5 --cheats --class=terminator --vision=pointcloud
  --look enhanced --hud modern --width 1920 --height 1080 --offscreen --capture shot --capture-frames 1200
  --menu-script "run give all; key Weapon 8; wait 30; hold Fire; hold Strafe left; run face; run face; ..."
```

Bots are random from run to run (their brains have no fixed seed), so take a shot a few times and keep
the best.

**One shot, no frames saved: `--fixed-step`.** Without `--capture` an offscreen run follows the wall
clock, and its frames come so fast that `wait N` passes only a tick or two: a shot mid-fire lands
anywhere. `--fixed-step` (with `--capture-fps`, default 60) keeps the capture's clock, one tick a
frame at 60, and saves nothing but `--screenshot`, so a script's shot lands on the same tick every
run. The gun in hand 3 ticks into a shot:

```
skynet-sdl.exe --offscreen --no-briefing --look enhanced --map 210 --fixed-step
  --menu-script "wait 10; hold Fire; wait 3" --frames 14 --screenshot fire.bmp
```

## Scripted editor takes (`--script`)

`SkyNetEdit --script <file|steps>` runs a timeline the same way: a frame's steps run together up to `wait N`
(a file holds one step a line, `#` comments; else `;` between steps). A step is an editor command
(`graph.open`, `asset.open robots 037`, `place.drag robot:endo x y`, ...) or:
`cam x,y,z yaw pitch`, `tool <mcp tool> {json}` (docs/mcp.md, run in-process), `mouse x y` / `down [right]` / `up`
(ImGui's pointer, window pixels), `key F5` / `key ctrl+Z`, `type text`, `window <title|###id> x y w h`,
`preview [camera]` / `stop`, `hold <action>` / `release <action>` (the preview's keys), `quit`.
In a capture ImGui runs on the capture's clock (the viewers and effects keep pace) and draws its pointer.
