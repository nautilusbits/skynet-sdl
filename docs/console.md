# Console commands

The game has a console for settings the menus do not reach, cheats, demos and
deathmatch chat.

## Using the console

- **Open / close:** the `` ` `` key (left of `1`). Esc also closes it.
- **Run:** type a line, Enter. `help` lists every command, `help <command>`
  shows one.
- **Case:** names and words are not case-sensitive (`FAR`, `far`).
- **Several commands:** separate them with `;` (`god on; give all`).
- **Quotes:** a `"double-quoted"` word keeps its spaces and `;`
  (`bind f9 "screenshot a.bmp"`).
- **Keys:** Tab completes a command or its argument; Up / Down walk the
  history (kept in `console-history.txt` between runs); PageUp / PageDown /
  End scroll the output.
- **Switches:** `on|off` also take `1|0`, `true|false`, `yes|no`; with no
  argument the switch toggles.
- **Numbers** use a dot for decimals; integers also take `0x` hex.
- **From the command line:** `--console "far 8000; fps on"` opens the
  console and runs the line at start.
- **Persistence:** most settings changed here last **this run only**. Kept in
  the settings file: `workers`, `robotai`, `stereo`, `viewer`, `head` and the
  key binds (`bind`).

In the tables, `<x>` is required, `[x]` optional, `a|b` a choice.

## View and graphics

| Command | Arguments | Effect | Default |
|---|---|---|---|
| `far` | `[units]` (1-1000000) | The far clip (view distance) in world units, any look. The depth cue (fog) keeps RENDER DETAIL's start. No argument: back to RENDER DETAIL's. About 40 units = 1 m. | RENDER DETAIL's: low 1176, med 1944, high 3992 |
| `detail` | `<low\|med\|high>` | RENDER DETAIL: far clip, depth cue and (from the next map) the HD textures' size. | high |
| `renderer` | `[default\|ascii\|pointcloud\|engraving] [source\|green\|amber]` | DISPLAY's RENDERER: the world as is, as ASCII glyphs, as a point cloud or as an engraving; second word: the ASCII colour. No argument: the next renderer. | default |
| `relief` | `[off\|low\|medium\|high]` | ENHANCED OPTIONS' SURFACE RELIEF: how strongly the hi-res textures' normal and roughness maps light them. No argument: the next level. | |
| `hud` | `<dos\|hires>` | The 320 x 200 panel or the 640 x 480 set. | |
| `hudstyle` | `<classic\|modern>` | DOS's panel, or corner widgets over a full-frame view. | |
| `crosshair` | `<current\|dos\|dot\|cross>` | The on-foot crosshair (DISPLAY's CROSSHAIR). | |
| `automap` | | Opens the automap (Tab, pad R3). Not while a campaign map is played. | |
| `look` | `<yaw> [pitch]` | Turns the view (degrees; pitch -90..90). | |
| `screenshot` | `<file.bmp>` | Saves the next frame. | |
| `storm` | `[strike]` | A mod's storm (`weather/storm.json`): its settings, or a lightning strike ahead now. Outdoors, ENHANCED look. | |
| `weather` | `[gust]` | The mods' weather (`weather/*.json`): what is on, or a dust cloud raised upwind now. Outdoors, ENHANCED look. | |
| `moon` | `[hit\|fall\|reset]` | The night moon: its state; one hit (OW!), the fall now, or back in the sky. | |

### Stereo and phone viewers

| Command | Arguments | Effect | Default |
|---|---|---|---|
| `stereo` | `[off\|sbs\|sbsr\|sbsfull\|crossview\|tab\|tabr\|int\|intr\|viewer] [separation] [depth] [canvas depth]` | Both eyes on one screen: the layout, the eyes' distance apart (0-40), the depth shown at the screen (10-10000) and the HUD's depth (0-10000, 0: at the screen). No argument: shows the current values. | off, 3, 200 |
| `viewer` | `[generic\|cardboard1\|cardboard2\|flat\|custom] [k1 k2]` | The phone viewer's lens: a preset, or `custom` with two distortion coefficients. | |
| `head` | `[off\|gyro\|scripted] [coupled\|decoupled]` | Head tracking (phone gyroscope; `scripted`: a slow sweep) and whether it turns the aim. | |

## Gameplay

| Command | Arguments | Effect |
|---|---|---|
| `difficulty` | `<low\|med\|high>` | The robots' fire rate and the damage both ways (this run). |
| `robotai` | `[classic\|improved]` | ROBOT AI: DOS's robots, or improved (route round obstacles, memory, no shots into walls). |
| `save` | `[1-N]` | Saves to a slot (no slot: the F6 quick slot). |
| `load` | `[1-N]` | Loads a slot (no slot: the F7 quick slot). |
| `menu` | | Back to the title. |
| `quit` (`exit`) | | Leaves the game. |
| `maps` | | Lists the maps in the data. |
| `map` | `<NNN \| MAP.NNN>` | Plays the map, in the mission of its decade. |
| `mission` | `<n>` | Starts mission n from its first map. |
| `restart` | | RESTART MISSION. |
| `objectives` (`obj`) | | The mission's objective counter. |
| `pos` (`where`) | | Where the soldier stands, and the command-line options that start there. |
| `whoami` (`chvem`) | | The player's name (DOS's "You are #N."). |
| `clearautomap` (`ckort`) | | The automap forgets what was seen. |

## Cheats

The DOS cheat codes (CHEAT.PRS) are aliases: type them in the console as they
are (e.g. `csej`). `cheats` lists them. In a network game the cheats are
refused unless the host enabled CHEATS (see [Deathmatch](#deathmatch)).

| Command (aliases) | Arguments | Effect |
|---|---|---|
| `cheats` | | Lists the DOS cheat codes. |
| `god` (`willnotstop`, `csej`) | `[on\|off]` | No damage. |
| `noclip` (`fly`) | `[on\|off]` | Flies through walls. |
| `give` | `[all \| ammo \| super \| <record> \| <name>]` | A weapon (record 0-12 or its name) or ammo. No argument: lists what is owned. |
| `weapon` | `<record>` (0-12) | Takes and holds a weapon. |
| `arnold` (`cskydere`) | | Every weapon. |
| `superuzi` (`cskyder`) | | The super UZI in hand. |
| `slugs` (`ammo`, `ckugler`) | | Fills every ammo pool. |
| `surgery` (`heal`, `cfalck`) | | Full health and armour. |
| `health` (`hp`) | `[points]` | Reads or sets the health (0 to max). |
| `armor` (`armour`) | `[0-100]` | Reads or sets the armour (percent). |
| `hurt` | `<points>` | A hit through the armour. |
| `throw` (`secondary`) | `[1-5]` | Picks the thrown item (as F1-F5). |
| `killall` (`kill`) | | Every robot on the map dies. |
| `enemies` (`showspawns`, `cfyr`) | | Lists the map's robots. |
| `tp` (`teleport`) | `<x> <y> <z> [yaw]` | Puts the feet there (world units; yaw in degrees). |
| `nextlevel` (`illbeback`, `cbane`) | | The next mission. |
| `win` (`cslut`) | | MISSION COMPLETE. |

## Audio and movies

| Command | Arguments | Effect |
|---|---|---|
| `music` | `[off \| title \| <song> \| 0-100]` | The score: a song (`T200`, `TITLE.HMI`...), the title's, off (`stop`), or the volume in percent. No argument: what plays. |
| `movie` (`vid`) | `<NAME>` | Plays a .VID movie full screen (`LOGO`, `OPENING`, `SEQ10`...; any key skips). Not in a demo, recording or deathmatch. |

## Key binds

| Command | Arguments | Effect |
|---|---|---|
| `bind` | `<key> [command... \| +action]` | A key runs a console line in play (`bind f noclip`, `bind f9 "screenshot a.bmp"`), or `+action` adds the key to a game action (`bind q "+quick save"`). No line: what the key does. |
| `unbind` | `<key>` | The key runs nothing and leaves every game action. |
| `unbindall` | | Drops every key's console line (game actions keep their keys). |

## Demos

See [demos.md](demos.md).

| Command | Arguments | Effect |
|---|---|---|
| `record` | `<name> [mission <n> \| map <n>]` | Records to `demos/<name>.sndemo`: from here, or from a mission's or map's start. In a network game: what the server sends. |
| `stop` | | Ends the recording (writes it) or the playback. |
| `playdemo` | `<name>` | Plays a demo back (Esc pauses). |
| `timedemo` | `<name>` | Plays a demo as fast as frames render; reports the frame rate. |
| `demospeed` | `[0.25-8]` | Playback speed (no argument: x1). |
| `demopause` | | Holds or resumes the playback. |
| `demoseek` | `<seconds \| +seconds \| -seconds>` | Network demos: jumps to the keyframe at or before a time (keyframes every 10 s). |
| `democam` | `[first\|chase\|free]` | Network demos: the followed player's eye, behind them, or free (no argument: the next; End in the game). |
| `demofollow` | `[next\|prev\|<id>]` | Network demos: the player the camera follows (PageDown / PageUp in the game). |

## Deathmatch

See [multiplayer.md](multiplayer.md). These exist only during a network game.

| Command | Arguments | Effect |
|---|---|---|
| `say` | `<text>` | A chat line to everybody (T or Enter in the game). |
| `players` | | The players: alive or dead, position, kills, deaths, points. |
| `scores` | `[on\|off]` | Keeps the scoreboard up (else it shows while Tab is held). |
| `class` | `[human\|terminator]` | HUMAN (motion detector) or TERMINATOR (machine vision, no detector; slower under BALANCED), from the next spawn. |
| `vision` | `[normal\|pointcloud]` | TERMINATOR VISION as the plain view or as a point cloud (this machine only). |
| `face` | `[id]` | Turns the view to a player (no id: the nearest living one, in sight first). |
| `newround` | | The host starts the round again. |
| `navmesh` | `[on\|off]` | Draws the host's bots' nav mesh and paths. |

Restrictions in a network game:

- Refused: `map`, `mission`, `restart`, `nextlevel`, `win`, `save`, `load`.
- Refused unless the host allowed CHEATS (host menu, or `"cheats": true` in
  the dedicated server's config): `tp`, `god`,
  `noclip`, `give`, `arnold`, `superuzi`, `slugs`, `surgery`, `health`,
  `armor`, `weapon`, `killall`.
- `movie` is refused.

## Information and developer

| Command | Arguments | Effect |
|---|---|---|
| `help` | `[command]` | The commands, or one of them. |
| `clear` | | Empties the console. |
| `echo` | `<text>` | Prints the text. |
| `version` | | The build, the data and the GPU. |
| `stats` | | Draw calls, triangles, frame time (last 60), robots alive, window size, GPU memory. |
| `fps` | `[on\|off]` | The FPS readout in the corner (F11). |
| `debug` (`overlay`) | `[on\|off]` | The debug overlay: frame costs, renderer, position, objectives (F12). |
| `workers` (`jobs`) | `[N\|auto]` | The job pool's worker threads (auto: cores - 1, 2..8); takes effect at the next start. |
| `navmesh` | `[on\|off]` | Draws the walking robots' nav meshes and paths (ROBOT AI IMPROVED). |
| `hint` | `<text>` | A line in the panel's message window. |
| `status` | `<text>` | A status line over the view. |
| `level` | | Deathmatch: the arena's state as this machine has it. |
| `vehicles` | | Deathmatch: the arena's jeeps and HKs as the server has them. |
| `pickups` | | Deathmatch: the arena's items as the server lists them. |

`--menu-script "steps"` (command line, for automated checks) is not the
console: its steps (`up`, `accept`, `select`, `type`, `run <console line>`,
`wait`, `shot`, `key`, `pad`, `hold`, ...) drive the menus one per frame;
`--help` lists them. The editor (SkyNetEdit) has its own commands.
