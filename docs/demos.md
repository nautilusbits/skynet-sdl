# Demos and determinism

A demo records a campaign run as its start state plus the input of every
60 Hz tick. Playing it back runs the same simulation on the recorded input and
compares a state hash after every tick, so a demo is a replay and a regression
check at once. A network game is recorded differently, as Quake 3 does it: the
messages the client receives, played back with no server and no simulation (see
"Network demos" below). The network game keeps its own random generators
(`src/SkyNet.Game/Net`).

## Commands

| Where | Command | What it does |
|---|---|---|
| console | `record <name>` | Records from the game as it stands. The game is saved, the save is loaded into a fresh session, and recording starts there. |
| console | `record <name> mission <n>` / `record <name> map <n>` | Records from a mission's start: its first map, a side map of it, or a loose map. The soldier carries the start kit. |
| console | `stop` | Ends the recording and writes the file, or ends the playback. |
| console | `playdemo <name>` | Plays a demo and reports the first desync. |
| console | `timedemo <name>` | Plays a demo at one tick per frame with VSync off, then reports the frames per second and ticks per second. |
| console | `demospeed [0.25-8]` | Sets the playback speed (no argument: x1). |
| console | `demopause` | Holds or resumes the playback. The Esc menu also holds it. |
| command line | `--record NAME` with `--mission N` or `--map N` | Records from that start. The file is written when the run ends. |
| command line | `--playdemo NAME`, `--timedemo NAME` | Plays at startup. `--timedemo` quits when the demo ends. |

`NAME` is a file in `demos/` beside the game (`demos/NAME.sndemo`, in
`SettingsStore.UserFolder`: the app-data folder when the game's folder cannot be
written). A path with a folder in it is used as given. The result goes to the
log (`[Demo]` lines) and to the status line (`DEMO OVER`, `DEMO DESYNC AFTER TICK n`).

A recording stops and is written when a game is loaded, a new game is started,
the game is left for the title, or a network game starts. While a demo plays,
the mission screen and the RESTART MISSION? box stay closed. The demo's own
events stand in for them.

The player's keys do nothing to the game during playback. A game-changing
console command typed during playback does change it, and that causes a desync.
DIFFICULTY stays as recorded until the demo ends.

Campaign playback has pause and speed only; network demos also seek and move the camera
(see "Network demos").

## How it works

```
record:   fresh session ─► start (mission | embedded save) ─► per tick: input ─► CampaignControls.Tick ─► SessionHash ─► file
playback: fresh session ─► CampaignDemo.Begin(header)      ─► per tick: events, recorded input ─► CampaignControls.Tick ─► compare
```

- `CampaignControls` (SkyNet.Game) registers the actions and turns one
  `TickInput` into a session tick: skip the end screen, toggle noclip, read the
  weapon keys, run `GameSession.Tick`. The app, the demo player and the tests all
  call it, so the same input runs the same tick.
- Both sides start from a **fresh session**. In the app this is a new
  `GameContext`, made the same way as for NEW GAME → FUTURE SHOCK. A recording
  "from here" saves the game and loads the save into the fresh session before
  the first recorded tick. Playback rebuilds exactly that, so leftover state in
  the soldier, weapons or robots that a save does not carry cannot differ.
- Only ticks that reach the session are recorded. Ticks frozen under a menu, the
  console or the mission screen are not in the demo.
- **Events** are things done between ticks that the input does not carry. They
  are applied before the tick they precede:
  - a console command that changes the game, kept by its words: `god on`,
    `give all`, `tp ...`, `restart`, `win`, `map`, `mission` and the other
    `SessionCommands` (the ones that only print are left out);
  - `!restart`: RESTART MISSION chosen on the box;
  - `!difficulty High`: DIFFICULTY changed during the recording.
- `HeadlessCampaign` (SkyNet.Game.Demos) is the same set-up with no window or
  sound: the session, the vehicle rig boarded on each map entry, the controls,
  the session commands, and RESTART held for the player as in the app. The tests
  record and play on it. `HeadlessCampaign.Play(data, demo)` plays a demo as
  fast as it runs, with an optional callback after each tick for analysis.

## The state hash

`SessionHash.Compute` runs after every tick and produces six 32-bit components.
It also stores six probes, values shown when a desync is reported:

| Component | Contents |
|---|---|
| `player` | feet, velocity, yaw, pitch, eye height, floor, noclip, health, armour, god, the ride (mode, hull, speed, headings, energy, heat) |
| `weapons` | owned weapons, ammo pools, primary and thrown weapon, cooldowns, every projectile (record, position, velocity, age, bounces, followed) |
| `robots` | every robot (position, yaw, hit points, dead, hidden, sees, shots fired, dormant distance, root yaw and pitch, AIS pc, wait, frame), pending blasts, wreck parts |
| `level` | `LevelBehaviour.HashInto`: record bytes, hit points, spent and armed sets, movers, triggers, doorways, wrecks, lights, switches, path vehicles; the water surface; the breath |
| `random` | the simulation generator's state |
| `session` | mission, map, phase, clock, objective counter, pickups left, drops, torpedo ride |

The probes are the feet x, y and z, the yaw, the health and the armour.

Hash tables (dictionaries, sets) are hashed as **unordered sums** of per-entry
hashes, so two equal tables give the same value whatever order they were
filled in. Cosmetic state is left out: effects, impact sparks, sounds and the
cosmetic generator. A desync report names the components that differ and the
probes as recorded and as played, for example
`desync after tick 300: player, robots differ (x 1520 -> 1498.2, ...)`.

## File format

The engine owns the format (`RetroEng.Loop.DemoFile`). The game owns the
fields and the start bytes. All values are little-endian:

```
"RETRODEM"   magic
u16          format version (1); any other version is refused
i32 length, u64 FNV-1a of the payload, payload (deflated):
  fields       i32 n, then (string key, string value)
  actions      i32 n, names in id order (playback maps the bits onto today's ids by name)
  hash names   i32 n, names; probe names: the same
  i32          hash interval (1 = after every tick)
  start        i32 length, bytes
  i32 ticks, then the inputs as deltas:
               a mask byte of the TickInput fields that changed (Down, Pressed, Released,
               LookX, LookY, Wheel, MoveX, MoveY), then those fields (masks as varints,
               axes as raw f32 bits); a 0 byte is a run (varint n: the last input n more times)
  events       i32 n, then (i32 tick, string)
  checks       i32 n, then per check the u32 hashes and the f32 probes
```

SkyNET's fields:

| Field | Contents |
|---|---|
| `game` | `SkyNet` or `FutureShock` |
| `build` | the assembly's informational version |
| `data` | a fingerprint of the data folder (file names and sizes) |
| `start` | `mission` or `save` |
| `mission`, `map` | the start's mission and map |
| `difficulty` | the difficulty level |
| `seed` | the generator state at the start (hex) |
| `mods` | the table mods mounted, `name=hash;...` (tables and missions hashed by content, other files by size, ZIPs by size) |
| `blocks` | `baked` or `raw`: whether map collision came from the baked block |
| `tick rate` | the tick rate (60) |
| `recorded` | the UTC time of the recording |

The start bytes are one of two kinds:

- for `save`: the save file (`SaveFile`, format 7 or later, which includes the
  generator state);
- for `mission`: i32 mission, i32 side map (−1 for none), then the kit
  (i32 n, bools for the owned weapons; i32 n, ints for the pools; i32 primary;
  i32 thrown).

Playback refuses a demo of the other game, of another tick rate, or of an
unknown format version. It warns about another build, other data, other mods,
or another collision source, because any of these may explain a desync.

A minute of play is a few kilobytes of input. The hashes cost 24 bytes per
tick before deflating, and they do not compress.

## Network demos

A network demo records what the server sends one client, the way Quake 3 does. It also records
that player's own poses, shots and hits, and a keyframe every 10 seconds. Playback needs no
server and simulates nothing. A client is fed the recorded messages as they come due, and the
same code that shows a live game shows the replay: avatars, items, vehicles, the map's own
state, the HUD, the scoreboard, the kill feed and the chat. The view follows the recorded own
poses (first person), or another player, or flies free.

### Commands

| Where | Command | What it does |
|---|---|---|
| console, in a network game | `record <name>` | Records the game from now on (client or host). `stop` writes it, and so does leaving the game, the server ending it, or quitting. |
| command line | `--record NAME` with `--host` / `--join` | Records from the welcome on. The file is written when the game ends. |
| `server.json` | `"record_demos": true` (`"demo_dir"`, default `demos` beside the config) | The dedicated server records every round, as a client playing as nobody would receive it: `round-<UTC time>-<round>-MAP.NNN.sndemo`. A round's demo is written when the next round or map starts, or when the server stops. |
| console | `playdemo <name>` | Works for both kinds; the file's magic tells them apart. |
| console | `demopause`, `demospeed [0.25-8]` | Pause and speed, as for campaign demos. |
| console | `demoseek <s>`, `demoseek +s` / `-s` | Jumps to the keyframe at or before that time. Keyframes come every 10 s, written a frame before each 10 s mark, so `demoseek 20` lands at about 19.9 s. |
| console / keys | `democam [first\|chase\|free]`, End | The camera: the followed player's eye, behind and above them (pulled in at walls), or free (the movement keys and the mouse fly it, with no collision). |
| console / keys | `demofollow [next\|prev\|<id>]`, PageDown / PageUp | Which player the camera follows: the recording player, or anybody in the arena. |

`stop` ends the playback and goes to the title. At the demo's end the last frame stays up
with `DEMO OVER`. `--playdemo NAME` plays a network demo at startup. `--timedemo NAME`
plays it at its own pace and quits at the end. Playback is never faster than the demo speed:
nothing is simulated, so there is nothing to time. While a network demo plays, nothing
is sent anywhere. The player's keys reach the game only with the free camera, and then only
the movement. The weapons and the use key are dead. The HUD's own read-outs (health, armour,
weapon) are always the recording player's.

### What is recorded

```
client:     server ──messages──► NetEndpoint.Tap ──► recorder ◄── own pose (60 Hz), shot, hit
host:       DmHost.All(...)  ──► DmHost.Tap      ──► recorder      (what a client as player 1 would get,
                                                                    its Health too; poses and shots of its own are not sent to it)
dedicated:  DmHost.Tap (as player 0: everything)  ──► one recorder a round
playback:   file ─► NetDemoPlayer ─► ReplayTransport ─► DmClient ─► DmGame (replay) ─► the same view code
```

- **Messages** are the wire bytes exactly as received (the id byte first), on whichever channel
  they came. The password challenge is never kept. A welcome is not recorded: a keyframe
  stands in for it. On a client, a second welcome (a map rotation) is recorded as it came.
- **Keyframes** are written as soon as the arena is up, then every 10 s. A keyframe is what a client
  joining now as the recording player would be told: a `Welcome` (roster with scores, items,
  clock, the round running or not, vehicles), a `PoseOf` for everybody alive (their last pose
  and flags), a `LevelState` with the reset flag and the overlay (every record that differs
  from the map's start: `DmLevelServer.Overlay`, or on a client `DmLevelClient.Overlay`), the
  `VehicleState` of the vehicle the player drives, a `MatchOver` while the round is over, and
  the player's own state and pose. Playback starts at the first keyframe. A seek queues a
  keyframe's entries, and the replaying `DmGame` takes its welcome on the arena already up
  as a fresh start of the view, without reloading the map. Its level update puts the map
  back to its start and lays the overlay down, with no blasts or sounds.
- **Own records** (`NetDemoLocal`): the pose at most 60 times a second (feet, view yaw and pitch,
  eye height, pose flags, car yaw, weapon in hand), each shot (drawn on playback as somebody
  else's, harmless), each hit reported (kept for analysis; playback ignores it), and the
  keyframe's state (spawned, health, armour, dead).
- Between keyframes, playback skips the keyframes in the stream: the messages already carry
  that state.

### Playback

- `DmGame.StartReplay` builds a client on a `ReplayTransport`. This transport is connected from
  its first poll, swallows everything the client sends, and is fed by `NetDemoPlayer.Advance`,
  which moves the demo's clock on by the frame's time times the speed. Messages go into the
  transport. Own records are applied after the messages of the same step.
- The session's player is the camera: `PlayerController.Place` puts it, noclip and with no
  glide, at the recorded own pose (interpolated between the samples around the playback time;
  a jump of more than 400 units or a gap of more than 250 ms is held, not glided), at another
  avatar's eye, or behind a body. The recording player's own body is drawn from its poses
  whenever the camera is not its eye. The followed body is hidden in its own first-person view.
  The weapon on screen and the death view are shown only in the recording player's own view.
- A host's demo is played the same way as a client's (the recording player is 1). A dedicated
  server's demo has no recording player: it starts on the chase camera behind the first
  player in the arena.
- Scores: the server sends frags, deaths and points with each death and at the round's end,
  so a demo shows the scores as every client saw them. Points scored by hits between two
  deaths stay on the server, live or replayed.

### File

The same `.sndemo` extension and folder as campaign demos. The magic tells the kinds apart, so
one `playdemo` plays both and a player need not know which kind a file is. The engine owns the
container (`RetroEng.Loop.NetDemoFile`, game-agnostic). The game owns the fields and the
encodings (`SkyNet.Game.Demos.NetDemo`). Little-endian:

```
"RETRONET"   magic
u16          format version (1); any other version is refused
i32 length, u64 FNV-1a of the payload, payload (deflated):
  fields     i32 n, then (string key, string value)
  entries    i32 n, then per entry: varint time delta (ms from the previous entry), u8 kind
             (0 message, 1 own record, 2 keyframe), varint length, the bytes
a keyframe's bytes: varint n, then per entry u8 kind (0 / 1), varint length, the bytes
```

| Field | Contents |
|---|---|
| `game`, `build`, `data` | as for campaign demos |
| `protocol` | `DmProtocol.Version`. A demo of another protocol is refused: "the demo was recorded with network protocol 5; this build plays network protocol 6 only". |
| `role` | `client`, `host` or `dedicated` |
| `local id`, `local name` | the recording player (0 and empty for a dedicated server) |
| `server`, `map`, `arena`, `goal`, `goal limit`, `time limit`, `classes`, `cheats`, `bots`, `bot skill`, `replenish` | the host's rules (the welcome carries them too) |
| `keyframe seconds` | 10 |
| `recorded` | the UTC time of the recording |

Another build or other data only warn. Nothing is simulated, so messages of the same
protocol play back right.
Sizes: about 2 KB a second with five players (own poses at 60 Hz dominate). A 20-second
client recording with four bots was 45 KB.

Tests (`tests/SkyNet.Game.Tests/Demos/NetDemoTests.cs`): a host with three bots and two clients
over loopback, recorded on a client and on the host for 25 s. The client's demo is played on a
session of its own. Every 2.5 s, its players, scores, items, vehicles, level rows, health and
avatars (within a unit, once they have glided in) equal the live client's. A seek to the last
keyframe gives the state the live client had when it was written. The host's demo ends with
the roster and scores every client was sent. `DmDedicatedTests` checks `record_demos`: one
demo per round, and the first plays back to the kill that ended it.

## Benchmark demos

Two demos in `demos/` time the renderer. `--fixed-step` advances the game 1/60 s per frame, so
every run draws the same frames; compare `ms per frame` in the log's `Last frame` line.

| Demo | What | Time it |
|---|---|---|
| `bench-dm12` | network demo, MAP.605, host and 12 bots, 62 s | `--offscreen --novsync --no-sound --look enhanced --fixed-step --timedemo bench-dm12` |
| `bench-m1` | campaign demo, mission 1 start, the soldier standing, 60 s | `--offscreen --novsync --no-sound --look enhanced --timedemo bench-m1` (reports fps) |

A network demo plays on any build with the same `DmProtocol.Version`. A campaign demo plays
only on a build whose simulation matches: a change to the simulation, the tables or the data
desyncs it (`test.sndemo`, recorded by 0.33, desyncs at tick 0). Re-record them then:

```
skynet-sdl --offscreen --novsync --no-sound --host=MAP.605 --bots=12 --record bench-dm12 --fixed-step --frames 3720
skynet-sdl --offscreen --novsync --no-sound --record bench-m1 --mission 1 --no-briefing --fixed-step --frames 3600
```

## Determinism rules for new code

The campaign simulation is deterministic on one machine and build: the same
start and the same `TickInput` sequence give the same state every tick. To keep
it that way:

1. **Randomness comes from `GameSession.Random` (a `SimRandom`).** Draw from it
   only inside a tick, and only for something that changes the game.
   `System.Random`, `Random.Shared`, `Guid` and similar are not allowed in
   simulation code. The generator is PCG32. Its algorithm is ours, so it does
   not change with the .NET runtime, and its state is saved (save format 7) and
   hashed.
2. **Sound and looks draw from `GameSession.Cosmetic`.** This covers things like
   which explosion sample plays or where the sparks fly. Draw the number
   *before* the null-conditional call. In `Sounds?.Play3D(x + rng.Next(4))`
   the draw is skipped when there is no sound bank, so a headless run would
   draw a different sequence.
3. **No clock in the simulation.** Do not use `DateTime`, `Stopwatch` or
   `Environment.TickCount` in code that a tick reaches. Time is the tick count
   times `GameSession.Dt`. The session clock restarts at 0 when a mission starts
   and when a game is loaded. The animation clock (`AnimationMs`) and `--freeze`
   are for drawing only.
4. **No feedback from rendering, audio or the frame rate.** The simulation must
   not read anything the renderer, the mixer or the menus produce. For example,
   `WeaponSystem.EffectFrames` only sizes the visual effects.
5. **Order.** Iterating a `Dictionary` or `HashSet` follows the order of the
   inserts and removes. That is deterministic for the same operations, but do
   not let it depend on hash codes (`GetHashCode` of objects or strings), on
   threads, or on `Parallel`. Sort when the order matters and is not already fixed.
6. **Changes outside a tick are events.** A new way to change the game between
   ticks (a menu entry, a console command) must be recorded as a demo event, or
   it must stop the recording. Session console commands are recorded on their
   own (`CampaignDemo.RecordedCommands`).
7. **New state goes into the hash.** If you add state to the soldier, the
   weapons, the robots or the level, add it to `SessionHash` or
   `LevelBehaviour.HashInto`. Otherwise a desync in it shows up only later,
   through its effects.

Not covered: different CPUs and .NET versions. `MathF` functions and SIMD paths
can round differently on other hardware or runtimes, so demos are reliable on
the machine and build that made them. The `build` field and the warnings say
when this applies. Changing the mods' tables, or switching between baked and raw
map collision, also changes the simulation.

## Non-determinism found (2026-10-11 audit)

| Where | Problem | Now |
|---|---|---|
| `GameSession`, `LevelBehaviour`, `Robots`, every `AisBrain`, `Wreckage`, `WaterBreath`, `ImpactPieces`, `DestructionTable.PickDrop` | Each had its own `System.Random`: a new one per map, per robot (`seed * 7919`) and per brain (seeded by type). Their state was never saved, so a loaded game drew other numbers than the saved one would have. `System.Random`'s algorithm is the runtime's to change. | One `SimRandom` per session, saved (format 7) and hashed |
| explosion sample picks, blast flash scatter, bubble sound position, impact pieces | These drew from the simulation's generators, inside `Sounds?.Play3D(...)` where the draw is skipped with no sound bank. A run with sound and one without drew different sequences. | `GameSession.Cosmetic`, drawn unconditionally |
| `GameSession` clock | It ran on from earlier missions and loads, and `_completeAt` and `_endAt` were float sums from that base. | Restarts at 0 on a mission start and on a load |
| the app's `Simulate` | The input-to-tick mapping (noclip, weapon keys, end screen) lived in the app, so the tests could not replay what the app ran. | `CampaignControls`, shared |
| console commands, RESTART MISSION, DIFFICULTY | These changed the game between ticks. | Recorded as events |

Checked and left as they are: `Stopwatch` and `DateTime` uses are for loading,
logging, menus and drawing; `Parallel` is used only for baking and decoding mod
textures; dictionary order is insertion order and nothing in the simulation
sorts by hash code; no static mutable state in the simulation code; mouse
sensitivity and inversion are applied before the input is latched, so they are
inside the recorded `TickInput`.
