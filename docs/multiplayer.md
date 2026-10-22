# Network deathmatch

The DOS game shipped a network deathmatch (NETLEVEL.PRS lists its ten arenas,
maps MAP.600-609, and the maps carry spawn and item markers for it). This port
follows the Godot port's deathmatch (`godot_project/scripts/net/*.gd`) and the
DOS data as closely as possible. The DOS wire protocol is not reproduced: the
port keeps the DOS *game* and speaks its own client-server protocol.

## Design

- **Client-server.** The host is the server and also plays (player 1), or a dedicated server
  plays nobody (see "Dedicated server"). Anyone may join at any time (players 2 and up). Bots will be more players with negative
  ids; id 0 is the world (drowning, radiation).
- **Each peer simulates its own movement** and sends its pose 20 times a second
  over the unreliable channel; the server relays poses to everybody else.
- **The shooter detects its hits** and reports them; the server checks each report
  (alive or killed a moment ago, real positive damage capped at 2000 (`MaxHitDamage`),
  victim within ray range, a damage budget a second) and owns the only
  authoritative state: health and armour, frags, deaths and points, respawns
  (3 s), the items and their return (20 s, DOS's 0x578 ticks), the time and frag limits.
- **Every client input is untrusted**: token-bucket rate limits per kind
  (`DmProtocol.Limits`, the Godot port's table), sane positions, cleaned text.
- **Ports**: game 27015 (UDP), LAN discovery 27016 (UDP broadcast probe, answered
  only to private, loopback and link-local senders, at most every half second each), master server
  27900 (UDP; see "Master server").

### Layers

| Project | Content |
|---|---|
| `src/RetroEng.Net` in the retroeng repo (game-agnostic) | `INetTransport` (client-server, peer ids, reliable-ordered and sequenced-unreliable channels), `UdpTransport` (LiteNetLib, raw byte API only, so NativeAOT- and trim-safe), `LoopbackTransport` (in-process, for tests), `NetEndpoint` (message = byte id + payload, one handler per id, malformed messages dropped and counted), `NetWriter` / `NetReader`, `TokenBucket`, `LanDiscovery` (`DiscoveryResponder`, `DiscoveryBrowser`), `Master/` (the master server's protocol, `MasterServer`, `MasterAnnouncer` + `InfoResponder`, `MasterBrowser`; `UdpTransport.ConnectIntroduced` joins through a NAT introduction). |
| `tools/SkyNet.Master` | The master server's console program (no SDL). |
| `src/SkyNet.Formats/NetLevelList.cs` | NETLEVEL.PRS: name, map, max players, time of day, goal, DOS scoring, item counts, replenish. Missing file: the Godot port's built-in list. |
| `src/SkyNet.Game/Net` | `DmArena` (spawns, item and vehicle spots from the map), `DmItemLayout` (DOS's item layout), `ParkedVehicleColliders` (parked vehicles as solid geometry), `DmServer` (the rules, no network, no clock of its own), `DmHost` (server side of the wire, the host's own player), `DmClient` (client side), `DmProtocol` (ids, constants, encodings), `DmHeadless` (the headless runner and the command line), `DmGame` (the game's session joined to the wire), `DmAvatar` (another player's body: pose glide, clips, a hit target), `AvatarRenderer` (draws them), `DmHud` (the deathmatch's read-outs, the board, the end of the round, the machine vision), `DmLevel` (`LevelUpdate`, `DmLevelServer`, `DmLevelClient`: the map's own state on the wire, step 8). |

### Data used

- NETLEVEL.PRS: one `[levelN]` section per arena; item counts per category
  (bullets, energy, armor, health, slugthrowers, lasers, plasmas, launchers,
  grenades, rockets; jeeps and hks are vehicles), `replenish`, and the DOS scoring
  `scoring_kill` (points per kill), `scoring_hit` (points per hit),
  `scoring_death` (points lost per death).
- Map markers: 10, 12 ... 28 spawn sets, each facing the nearest marker of the next
  type, plus the single-player 0/1 pair; 100 item spots (every item, weapons too); 101 / 102 the
  jeep / HK spots only (SKYNET.EXE v1.01 0x11c200; the pairs of types 30-39 fence the map, they are
  no vehicle spots).
- Items (`DmItemLayout`, SKYNET.EXE v1.01 0x11c34d / 0x11c3b0 / 0x11c407, no random choice): the
  marker-100 spots in marker order, the one at index 7 and then the one at index 4 moved to index 1;
  the categories in DOS's table order (0x4bc30: slugthrowers, lasers, plasmas, launchers, bullets,
  energy, grenades, rockets, armor, health), each with DOS's sprites and 16.16 weights
  (`DmProtocol.DosItems`): one of each of its first min(n, kinds) kinds, the rest spread over the
  weights (step 0x10000 / rest from half a step); each item on the next free spot, one a spot, and
  what does not fit is left out (MAP.605: 56 spots for 65 items, the last 9 health packs stay out).
  Their effects come from the pickup table. Health and armour are server state;
  ammo and weapons belong to the taker.
- Health and armour are DOS's: a soldier in a network game dies after 150 points of damage
  (SKYNET.EXE v1.01 0x122348 sets [0x38dd0] to 0x9600 when [0x30a50] bit 0x40000 is set, 0x3e800 =
  1000 in the campaign; `DmProtocol.DosHealth`), the same for every avatar; the TERMINATOR's
  double bar (300) is the Godot port's rule (see "Classes"). Armour wears by 82/65536 per point of
  a hit and lets (1 - armour) of it through; health packs heal a percentage of the bar.

### Messages

Client to server: `Hello` (protocol version, class, name), `LevelReady`, `Pose`
(unreliable; step 9 adds the car yaw), `Fire`, `Hit`, `Pickup`, `Chat`, `SetClass`, `LevelIntent`
(step 8), `VehicleEnter` (key), `VehicleLeave` (where, heading) (step 9), `Auth` (has a password,
the proof; protocol 5).

Server to client: `Welcome` (your id, settings, roster, items, clock),
`PlayerAdd`, `PlayerRemove`, `PoseOf` (unreliable), `FireOf`, `Health` (to the
victim), `Died` (with every score), `Respawn`, `PickupTaken`, `PickupSpawn`,
`PickupAdd` (a crate's drop), `ChatOf`, `Time` (unreliable), `MatchOver`,
`Restart`, `ClassOf`, `LevelState` (step 8: the joiner's overlay right after the welcome, then
the deltas), `VehicleState` (step 9: why, then the vehicle's whole state), `Challenge` (a 16-byte
nonce, protocol 5). A `ChatOf` from player 0 is the server's own line (SERVER: the message of the
day, the console's `say`). A second `Welcome` is a map rotation: the client drops the old arena and
loads the new one as on a join. The welcome ends with the
vehicles; `PoseOf` carries the car yaw and the vehicle in flag bits 3-4 (the server's word).

A kick is the server closing the connection with the reason as the disconnect
payload (version mismatch, server full, no hello within 10 s, wrong or missing password, idle, the
console's kick and ban); a connection the server's door check turns away (a ban, too many from one
address) is rejected with the reason as LiteNetLib's reject data. The hello's first field is the
version, so a client and a server of different builds always get "Version mismatch: the server speaks
network protocol N, this game M" whichever is older. Protocol version 8 (5: the password challenge; 8: the scanner flag).

## Running it

`--host` and `--join` start the game in its window on the arena; the network
options are split off and the game's own (`--offscreen`, `--frames`,
`--menu-script`, `--console`, ...) work as usual.

```
skynet-sdl.exe --host=MAP.605 [--port=27015] [--name=NAME] [--class=human|terminator] [--avatar=1-13] [--vision=normal|pointcloud] [--time=MIN] [--goal=points|kills|lives|time] [--goal-limit=N | --frags=N] [--cheats] [--classes=dos|balanced] [--password=TEXT] [--bots=N] [--bot-skill=0-2] [--bind=IP] [--no-discovery]
skynet-sdl.exe --join=198.51.100.20[:27015] [--name=NAME] [--class=human|terminator] [--avatar=1-13] [--vision=normal|pointcloud] [--password=TEXT]
skynet-sdl.exe --host=MAP.605 --public --master=HOST[:PORT] ...      (announced to the master server)
skynet-sdl.exe --find=NAME --master=HOST[:PORT] --headless ...       (the master's list, then the introduction)
skynet-sdl.exe --dedicated --config server.json [--data=DIR] [--port=N] [--bind=IP] [--master=HOST[:PORT]] [--classes=dos|balanced] [--quit-after=S]
SkyNet.Master [--config master.json] [--port=N] [--bind=IP] [--quit-after=S]
skynet-sdl.exe --password-hash TEXT
```

`--port=0` hosts on any free port and prints it ("listening on port N").
`--headless` runs either side with no window (step 1's runner). `--smoke` turns
a headless run into the two-process check, and `tools/net-smoke.ps1` runs it: a
host and a client on 127.0.0.1 (any free port, no discovery) see each other's
pose, the client hits the host, the host hits back, the client leaves, and both
exit 0 (the host has a password the client proves, and its log must say "classes BALANCED,
password on"). `tools/net-smoke.ps1 -Dedicated` does the same for the dedicated server (see below), and
`-Master` runs that through a master server (the clients find the server in its list and join through
the introduction; see "Master server").

In the game, the console has `players` (the roster, where each stands) and
`face [id]` (turns the view to another player's body, without an id the nearest living one, one in plain sight first, for scripted shots).

### In the game (steps 2 and 3)

- The session (`GameSession.StartDeathmatch`) plays the arena as a loose map
  with `Deathmatch` on: no robots, none of the map's own pickups (step 4 brings
  the server's), no DIFFICULTY (every hit is the table's points), no saves, and
  no MISSION FAILED; the keys are dead while the soldier is.
- The host starts the server and the arena at once; the spawns are nudged clear
  (`PlayerStart.Clear` on the host's collision world) before the server takes
  them. A client loads the arena on the welcome, then asks for its spawn.
- The local player is the campaign's controller and weapons. The pose goes out
  at 20 Hz (moving and firing flags, dead from the server). Every shot the
  weapons fire (`WeaponSystem.Shot`) goes out as `Fire` (record, from, along).
- The other players are `DmAvatar` sphere targets (radius 40 about the middle
  of an 88-unit body) in the weapons' `DamageTargets`: a ray, a shot or a blast
  that strikes one reports a `Hit` with the points and the weapon in hand. The
  soldier's own hurts (his blast, a fall, the water) are self-hits
  (`Soldier.Remote`), as fly_camera.gd sends them under `Net.active`.
- The server's `Health` sets the bar and the HUD shows it; `Died` lays the
  soldier down ("KILLED BY ..." on the status line); `Respawn` puts him at the
  server's spawn with the class's full bar and no armour, the kit kept.
- Bodies: each player's and bot's own one of DOS's thirteen avatars (`DmAvatarTable`, the avatar
  table at SKYNET.EXE 0x84dd0): its body and head models in its skin (the texture bank remap the host
  screen's preview uses), body and head on one frame; T800RFL with its own clips only when the data
  lacks them. The avatar travels with the player (protocol 7: its index 0..12 in the hello, the
  roster of `Welcome` / `PlayerAdd`, `SetClass` and `ClassOf`); the server keeps it only when it is
  one of the player's class (7 and 13 are the TERMINATOR's), else the class's default (1, or 7 for
  a machine). Bots wear a random avatar of their class. The host and join screens pick it (the host
  screen's turning left box, the join screen's PLAY AS; remembered as `net.avatar`), headless runs
  with `--avatar=1-13` (which sets the class too). Clips as enemy_anim.gd AVATAR
  measures them (stance 1-6, walk 7-14, death 29-34, firing 35-41). The body
  glides to the last pose (rate 14/s, snapping beyond 1500 units), walks while
  the pose says it moves, fires on a shot, falls on a death with the robots'
  death blast, and leaves 2.5 s later.
- Their shots (`FireOf`, `WeaponSystem.RemoteFire`): the fire sound and muzzle
  flash; a bullet's ray to its puff on the wall (none on a body); a bolt, rocket,
  shell or thrown item flying, bouncing and going off as its own, but
  harmless (damage is the shooter's to report).

### Bots (step 7)

`--bots=N` (0-12) and `--bot-skill=0|1|2` (default 1) on `--host`, windowed or
`--headless`. Bots are players -1, -2, ... with the Godot port's names and a random
class, drawn on every machine as any other body (their poses, shots and hits go
through `DmHost` exactly as the host's own). Each is a `BotBrain` (bot_brain.gd):

- **Perception** every 0.2 s: the nearest living player within 7000 units whose eye
  (feet + 75) a ray from the bot's eye reaches through the level's collision. A new
  target costs the skill's reaction (0.9 / 0.45 / 0.15 s) before the first shot. A
  target out of sight is chased to where it was last seen and given up after 3 s
  or when it dies.
- **Movement**: the soldier's `PlayerController` with RUN held (forward 400,
  sidestep 310, back 250, 1500 u/s^2, gravity 392, jump 177, step-up, slopes, the
  campaign's collision). In sight it holds the weapon's range (UZI 700, ASSAULT
  RIFLE 1100, MACHINE GUN 1000, SHOTGUN 350, LASER RIFLE 1400, band +-250) while
  strafing (the side flips every 0.8-2.2 s); otherwise it roams to an item (a health
  item under 45 % health), else a spawn, else a point 1500 away (a new goal every
  6-12 s). A knee-height ray (30 up, 110 ahead) that hits makes it jump; 0.6 s
  without progress veers it aside for 0.8 s. Its fall damage is a self-hit.
- **Fire**: within 12 degrees of facing and in sight, at the weapon's DOS rate, in
  bursts of 3-6 then a pause (1.2 / 0.7 / 0.35 s x 0.7-1.4), each shot spread by
  9 / 4.5 / 1.8 degrees x 0.3-1.0, from the eye (+40 ahead). The shot is the DOS weapon's (the
  owner's rule): the ammo table's points as a human's weapon deals them (no skill scale); a bullet
  is a ray against the level and the players' body spheres (radius 40 about the middle of 88
  units); the SHOTGUN (and the super UZI) also sets off DOS's radial blast where it lands (ammo
  damage -50: a blast of 50 over everybody but the one struck, `Blast.Damage`, clear line); a LASER
  RIFLE, plasma or rocket shot is a bolt flying at the record's speed (laser 1800) for the ammo's
  life, a launcher's shell a lobbed body that bounces (`BotBrain.StepShots`, stepped while the bot
  is dead too). DOS has no shotgun pellets: its shotgun is one ray and a blast. Whatever blasts
  hurts the bot itself as well (a self-hit). Hits go to the server as the host's own do.
- **Paths** (0.43.0): the arena's navmesh (`src/SkyNet.Game/Navigation`, DotRecast) is baked
  from the level's static collision over the spawns, items and vehicle spots plus 2048 units
  round them, for the soldier (radius 15, height 76, step 80, slope 40); bakes over 300 ms are
  cached in `cache/nav/` beside the exe, checked against the collision's hash. A bot walks the
  corners to its goal (its target's last seen place, an item, a roam point) and replans when the
  goal moves over 100, after 1.5 s, 120 off its leg or when stuck; the knee ray and the veer stay
  as the fallback. The console's `navmesh on|off` (host) draws the mesh and the bots' paths (the
  jeep's mesh 24 units over the soldier's, a driving bot's jeep path).
- **Vehicles** (the owner's go-ahead, 2026-10-22; DOS has no bots, so every number here is the
  port's). `BotRide` drives the campaign's `VehicleController` on the bot's body, fed a stick and
  keys as a human's; the server's rules are the human's (`EnterVehicle` within 120, `LeaveVehicle`,
  the hull, the wreck putting the driver out alive); the protocol is unchanged (the driver's pose
  carries the car's heading).
  - *Boarding* (weighed every second): a free vehicle with 40 % of its hull or more, within 2500,
    not under a roof (600 up); none while an enemy in sight is nearer than 1200, under 45 % health,
    indoors, or for 8 s after getting out. Utility: nearness (1 at the bot, 0 at 2500) plus 0.3 when
    the target is out of sight or past 2500; 0.4 or more, at the skill's chance a second (0.15 /
    0.3 / 0.5). It walks to 85 off the hull's middle on its nearer side; given up after 20 s or
    3 stuck spells. On foot, parked vehicles within 220 push it off (they are not on the mesh).
  - *Jeep*: its own mesh (`NavAgents.Jeep`: radius 80, height 120, step 16, slope 35, cells 16 x 4;
    `DmNav.Rides`, cached as `SkyNet-MAP.NNN-jeep.nav`), planned again on a goal moved 200, 250 off
    its leg or every second. Pure pursuit: a point 100-450 ahead on the path (speed x 0.5, held at a
    corner turning over 20 degrees), the wheel 2.5 per radian of heading error; the speed capped by
    the circle that reaches the point (the wheel's 1.074 rad/s, 721.6 / v), the next corner and the
    goal (braking at 0.7 x 459). Feelers (the hull's sphere swept 120-400 ahead, 10 up, and 0.6 rad
    either side) stop at what it cannot drive up (faces steeper than 35 degrees), the parked vehicles
    (75 / 115 about their markers) and the other bodies: it swerves to the freer side and slows.
    The way behind (error over 2.2 rad) is backed round, wheel the other way, up to 4 s, until under
    1 rad, the rear meets something or it stalls; then 1.5 s forward. No progress (40 in 1 s, not
    backing): 1 s of reverse. The turret turns to the target's eye at 2 / 3.5 / 5 rad/s (pitch +-45
    degrees) and fires on its own; a target on foot within 900 is rammed at the skill's chance
    (0 / 0.4 / 0.8, drawn each second) with the campaign's `Ramming` (v/2 points, reported with
    record 20), else the jeep circles it at 700 (0.6 rad ahead of the bot, the sense flips every
    3-6 s); out of sight it drives to the last seen place, else roams as on foot.
  - *HK*: no mesh. The hull's bottom 300 over the highest ground under it and 400 / 900 ahead (rays
    from 900 up), under the map's ceiling (marker 3) less 40; a sweep of the hull's sphere (1.1 x)
    a second's flight plus 250 ahead that hits climbs 400 and slows to a quarter, and where there is
    no room to climb tries 1 / 1.8 rad either side; under a ceiling too low to cruise it flies at half
    speed. The nose faces the target (else the way), pitched at it; the stick, the sidestep (RUN) and
    the climb keys share out the speed wanted. It circles a target at 1000.
  - *Guns*: the jeep's plasma (20) / the HK's laser (24) with `VehicleGuns`' energy (1000 of 2000,
    cost per shot, 450 a second after 0.35 s, overheated until 400), the rockets (22 / 25) every
    3-6 s at a target within 2500; aimed and spread as on foot.
  - *Getting out* (weighed every second): under 20 % of the hull, stuck 3 times in 12 s, or the
    target out of sight for 3 s where the vehicle cannot follow (under a roof, or off the jeep's
    mesh). An HK lands first (straight down, 6 s at most). It steps out 90 right of the hull's middle
    and 40 up, nudged clear (DOS's 20 across is inside the parked hull's collider). A wreck: the
    server puts it out, on foot where it was.
  - Measured (`BotRideArenaTests`: 3 seeded 3-minute matches, 4 bots at skill 1, per arena; 36
    bot-minutes each): in vehicles 6.5-8.9 bot-minutes, 5-20 of the 16-45 kills from vehicles,
    jeep stuck spells 0-9 and wall bounces 0-29 (MAP.603, the tightest), HK crashes
    (a touch of the level over 150 u/s) 0. Bots on foot stick as often as without vehicles.
- **Pickups**: every 0.4 s, whatever stands within 80 across and 220 up or down. A weapon item it
  takes (any on-foot gun 1-12) goes in its hand (`BotBrain.Took`).
- **Bodies**: the other living players block it (`BodyBlock`: upright capsules 22 across, a
  driver's vehicle's sphere), as their avatars block each machine's own soldier; falls hurt it
  (the soldier's fall damage, a self-hit).
- A life starts with one weapon from UZI, ASSAULT RIFLE, MACHINE GUN, SHOTGUN, LASER RIFLE, with
  no ammo count (bot_brain.gd).

Where this differs from bot_brain.gd: the reaction is kept (the Godot code clamps
the same target's clock up to 0 on every think, so a reaction never lasts more than
0.2 s); a lost target is really given up after 3 s and chased to its last seen
place (the Godot timer is undone every frame, so a bot hunts its last target through
walls for good, at its live position); the moves are the soldier's keys (no
omnidirectional 400 u/s, no instant speed change, no class speed factor, which this
port's humans do not have either); bodies block each other; walk-in
triggers are swept for every body on the server (step 8). DOS has no bots, so every number above
but the weapons' is the Godot port's.

### In the game (steps 4 and 5)

- Items: the server's list (`DmGame.Items`) drawn as the map's drops are (an item at 1:1 on its
  foot). An item whose DOS grab box (80 across, 160 up or down) holds the soldier's feet is asked
  for, at most every half second (pickup.gd); the server's `PickupTaken` takes it off every
  screen, the taker's machine adds the ammo and the weapon (the "PICKED UP" line and sound 179),
  others hear it where it lay; health and armour are the server's and come as `Health`. A
  replenished arena puts it back 20 s later (0x11c4a5: 0x578 ticks of the 70 Hz clock). The map's own pickups are hidden in an arena; a
  broken crate's item is a server pickup (step 8).
- HUD (`DmHud`, dm_game.gd's layer on its 1280 x 720 canvas, in the HUD's DOS font): the hit
  flash; FRAGS n[/limit] and RANK at the top left, the clock (timed rounds) in the middle; the
  centre line ("KILLED BY ...", "NEW ROUND", "NEXT LIFE: ..."); the chat log (6 lines, 10 s) at
  the bottom left. TAB held shows the board (game, arena, PLAYER / FRAGS / DEATHS / POINTS / BOT in
  DOS's order for the goal; the console's `scores on` keeps it up). The kill feed is DOS's: see
  "Rules" below.
- Chat: T or ENTER opens the line (the soldier gets no keys while it is up), ENTER sends, Esc
  drops it; the console has `say <text>`.
- End of the round (time or frag limit): the dimmed view, the board, the reason, "ENTER NEW
  ROUND" for the host ("WAITING FOR THE HOST" elsewhere) and "ESC MENU"; the keys are dead until
  the host's new round (ENTER, or the console's `newround`): scores wiped, items back, everybody
  respawned.
- Class: `--class=human|terminator` at the start, `class <human|terminator>` in the console for
  the next life (the server keeps it until the respawn). See "Classes" below for what DOS has.
  Both classes have DOS's 150-point bar. The host's CLASSES (see "Classes: the host's rules")
  decides the pace: DOS, every avatar at DOS's walk and run (speed factor 1); BALANCED (the
  default), the TERMINATOR at 0.9 of it. The TERMINATOR plays through the machine vision: every other player 50 to 2500 units away whose point falls in
  the view is marked, through walls, with a dot (flashing under the crosshair), the name and the
  range (40 units a metre), a dead one "NAME TERMINATED", and the whole screen, HUD included,
  goes through SKYNTTRM.COL (drawn as a blend: 0.70·c + (75, 0, 0)); not while dead, nor with the
  head under water (the water's palette wins); on foot and driving alike. TERMINATOR VISION (the
  port's own, off by default: the VISION row under the class on the host and join screens, kept as
  `net.terminatorVision`, `--vision=pointcloud`, the console's `vision`) draws the main view as
  DISPLAY's POINT CLOUD in machine colours (`ViewStyles.MachineCloud`: red-black background, red
  edges) while the machine vision is on, whatever RENDERER is set; the marks and SKYNTTRM.COL go
  over it as over the plain view, the HUD stays as drawn. A death, a HUMAN's life or leaving the
  match gives DISPLAY's renderer back. This machine's own: nothing of it goes on the wire. A HUMAN carries the
  motion detector (weapon record 13) from every spawn (`;` / `'` and the wheel reach it after the
  super UZI), a TERMINATOR never; held on foot it is DOS's scanner (see "Classes"): its sweep
  runs and beeps, the others plot on its screen.
- The Esc menu and the console no longer pause a deathmatch: the world, the wire and the clock go
  on under them, the soldier gets no keys. QUIT leaves the game (the host's closes it for
  everybody); SAVE and LOAD refuse ("NO SAVING / NO LOADING IN A NETWORK GAME"), and so do the
  console's `map`, `mission`, `restart`, `nextlevel`, `win`, `save` and `load`.
- Death view (fly_camera.gd begin_death_view): the eye sinks from the soldier's 75 to 14 in half a
  second, the head tips 0.35 rad and looks 0.22 rad down, the gun and the crosshair gone, until
  the server's respawn.
- The menu script has `sleep MS` (wall clock) for two-process runs, whose offscreen frames are unpaced.

### Menus (step 6)

- The path is DOS's: NEW GAME (NETGAME1.IMG) → MULTI-PLAYER opens NETJOIN1.IMG (NEW GAME / JOIN
  GAME / EXIT, NETJOIN2.CFA lights them; SKYNET.EXE v1.01 0x11e6a5). Both of its games go on to
  NETMENU1.IMG (0x127bc2, START / EXIT lit by NETMENU2.CFA), JOIN GAME after scanning the network
  with NETSCAN1.IMG (0x127ab7). Screens: `MultiplayerScreen`, `NetHostScreen`, `NetJoinScreen`
  (src/SkyNet.Game/Menu/NetScreens.cs); the boxes are menu.gd's measurements, the values in the
  menus' FONT0004.
- **Host** (NEW GAME): NAME (the last network name, else the new-game dialog's call sign or name),
  SKILL LEVEL = the bots' skill 1-3, AREA cycles NETLEVEL.PRS's arenas (Accept, a click, or left /
  right; DOS has no list), MAX # PLAYERS, TIME (minutes, 0 none), GOAL KILLS and its frag limit,
  the arena's SCORING (shown), the twelve item counts and REPLENISH (from the arena, changeable), the
  big left box = the class (HUMAN / TERMINATOR), the message box BOTS and PORT and menu.gd's hint
  line. PASSWORD (shown as stars) gives the menu-hosted game, or the menu's DEDICATED server, a
  password: the in-process server keeps only its key (`DmRunOptions.HostPolicy`, the dedicated
  server's challenge) and the text is **not stored** anywhere (not in the settings, not as a hash);
  it lasts as long as the screen. LOCKED (DOS's box beside SKILL LEVEL) shows Y while a password is
  set, N otherwise. CLASSES cycles DOS / BALANCED. The message box's second row holds PUBLIC,
  PASSWORD and CLASSES; menu.gd's hint line moved into the left box under the class. START hosts as
  `--host` does (`DmRunOptions.SettingsFor`: max players, replenish and item
  counts override NETLEVEL.PRS's); a port that will not open leaves the screen up with the reason.
- **Join** (JOIN GAME): the left box has two tabs. LAN: the LAN browser (`DmLan`, broadcast plus
  this machine's loopback) refreshes the list (name; map, players/cap, bots; a padlock for a
  passworded game, a flag the discovery reply now carries). INTERNET: the master server's list for
  this game and build (see "Master server"), each game's ping measured straight to it (`---` when it
  does not answer), the padlock; Accept on the tab shown asks the master again. Four rows; up / down
  past the last row scroll. NETSCAN1.IMG below: the bar fills over each probe (or ping round), APPS.
  FOUND counts the tab's games; the highlighted game's arena and cap show in AREA and MAX # PLAYERS,
  and LOCKED says Y for a passworded game, N for an open one (empty with no game chosen; a typed
  address of a listed game counts as chosen).
  A listed game joins on Accept or a click (an INTERNET game through the master's introduction; a
  passworded one asks for the PASSWORD box first); START joins the ADDRESS box (host or ip[:port]).
  The PASSWORD box (shown as stars, kept for the screen only, never in the settings) goes with any
  join. The screen says CONNECTING and waits for the arena, Back gives the connection up; a refused
  or lost join comes back to it with the reason.
- Keyboard, mouse and gamepad as every menu: arrows / d-pad move, text and number boxes take typing
  while highlighted (a number box's first digit replaces it), left / right step numbers and cycle
  choices, so a gamepad sets everything but the name and the address (those keep the remembered
  ones).
- Leaving a network game (QUIT, or the server ending it) comes back to the screen it started from
  over the title.
- The settings file keeps the last host fields (`net` in skynet-csharp-settings.json: name, class,
  arena, max players, time, frags, bots, bot skill, port, replenish and items for that arena,
  CLASSES, PUBLIC and DEDICATED; never a password), the last address joined and the master server (`master`, empty by default);
  they are written on START.
- `--bind IP` on a normal run binds a game started from the menus to that address (checks use
  127.0.0.1).

### The map's own state (step 8)

The Godot port's M5 (net_game.gd `_c_trigger` / `_s_trigger`, dm_game.gd's trigger half), on the
port's own `LevelBehaviour` (`src/SkyNet.Game/Behaviour/LevelBehaviour.Net.cs`,
`src/SkyNet.Game/Net/DmLevel.cs`):

- **Roles.** The campaign's level is `LevelRole.Local` and runs as it always has. The host's arena
  is `Server`: the same level, plus the cues its chains run are kept for the wire. A client's is
  `Client`: it shows the server's state (movers step from their bit, flickers and strobes flicker)
  and writes nothing of its own: no chain, no sweep, no doorway, no destructible of its own.
- **Intents** (`LevelIntent`, client to server, reliable): the use key (the eye and the record under
  the crosshair, -1 none) and a weapon's hit on a record (record, points). The server checks them as
  every client input: an admitted sender within its `Trigger` budget (20/s, burst 30), alive (a hit
  also in the dead-shot grace), a sane eye within 1400 of its last pose (net_game.gd TRIGGER_REACH),
  an aimed record the map has within that reach (else the use is not aimed), a hit on a damageable
  record within ray range, a real positive amount clamped at 2000 (`MaxHitDamage`). Then the level's
  own entry points decide: the use runs what the host's key runs (`RemoteUse`: the aimed wall button
  or prop, else the nearest wall button in its measure, then every 0xEF gate whose measure holds the
  eye; a gate whose chain leads to a doorway is left alone, as an arena is one map), the hit is
  ObjHit (`Hit`).
- **Walk-in triggers** (0xF1/0xF2): the server sweeps every other body itself, the clients from their
  last pose and the bots from theirs, with a latch per body (`BodyProx`, the Godot port's
  Behaviour.prox_body); a body that comes to life is pre-latched where it stands. The host's own
  player is swept by its tick as in the campaign. (The Godot client swept itself and sent an intent;
  here no intent can claim a walk-in.) No shipped arena has a walk-in trigger; MAP.604's three
  DRAINBOX gates (each turning a WATRWEEL, act 0xC5) are its only triggers and movers.
- **Rows** (`LevelNetRecord`): one record's whole live state: state, act and link bytes, flags
  (spent, cue spent, hidden, out of the collision, a switch turned, has hit points, wreck, mover,
  light), ObjHit's pool, a wreck's stage and counter, a mover's travel, direction and running flag,
  a light's enable word and intensity. A mover's travel rides along but is no news by itself:
  every peer animates it from the bit (no per-frame position on the wire, as in the Godot port).
- **`LevelState`** (server to client, reliable): `whole` (0 delta, 1 overlay), the map signature, the
  rows sorted by offset, and the cues the chains ran (sounds, voices, loops: a one-shot's bit goes up
  and down inside one tick). The host sends a delta each frame something changed (`DmLevelServer.Delta`
  compares every record with what was last sent), nothing in a quiet arena; a joiner gets the overlay
  (every record that differs from the map as it started) right after the welcome, laid down before
  it plays (no blasts or stage bangs for what broke before it came; a loop the server stopped stops).
  A client shows a delta through the listener the campaign uses: a wreck's stage, a destruction (its
  blasts, sound and removal), a light switched or faded, a loop stopped, the cues played.
- **Other map data.** The signature is an FNV-1a hash of the map's records (offset, state, act,
  link, variant; the Godot port's graph_sha); a client whose own map differs refuses the server's
  rows whole and its map stays as its files have it ("MAP DATA DIFFERS FROM THE SERVER").
- **Blasts.** A destroyed record's radial blast (the link record's i16) is the server's: see "Rules"
  below.
- **Crate drops.** The host's destruction picks the drop as in the campaign (table 0x423d6); an item
  becomes a server pickup (`DmServer.Drop`, `PickupAdd`), taken once and never back, gone at a new
  round; a client drops no item of its own. A drop that is no item (a wreck's fire) is drawn by each
  machine itself.
- The console's `level` prints the arena's state as this machine has it (records broken, every
  mover's travel), for scripted two-process checks.
- NEW ROUND puts the map back as the arena started: see "Rules" below.

### Vehicles (step 9)

DOS is the reference here (SKYNET.EXE v1.01; the Godot port's dm_vehicle.gd differs where noted):

- **Placement** (0x11c200, 0x130b00): NETLEVEL.PRS `jeeps` / `hks` (0x127dfb reads them). The jeeps
  park on the first min(`jeeps`, markers) **marker 101** spots, then the HKs on the first min(`hks`,
  markers) **marker 102** spots, in marker order, no random choice; the heading starts at 0 and
  turns 0x80 of 2048 (22.5 deg) more for each vehicle across the list; each is dropped on the floor
  under its marker (0x131002; here the host's collision raycast). MAP.605: 3 jeeps and 3 HKs. The
  Godot port shuffled them over free item spots at random headings. No item lies on 101 / 102.
- **Solid** (0x1310cc / 0x13110d): DOS parks each vehicle as a map record of its own (0x13996e, a
  variant-1 mesh record with the parked model, linked into the cell grid at its place and heading),
  so it stops bodies and shots as any placed mesh does; boarding and a wreck move the record 10000
  away (0x130daf, 0x130edf). Here `ParkedVehicleColliders` adds the parked model's triangles
  (NETHUMMT.3D / NET_HKMT.3D where they are drawn) to the session's collision world for every vehicle
  with no driver that is not wrecked: players, bots (host) and every shot. The corners at 0x51F54 /
  0x51FFE (jeep +-57 x +-72 at 45, HK +-50 x +-115 at 125) are the ground probes 0x126d00 /
  0x126f1f set its height and tilt with, not a collision box.
- **Messages** (0x118f2a): when another player's avatar turns into or out of the network jeep / HK
  (0x0E / 0x0F in the peer's avatar byte +0x1F) DOS queues STRINGS.PRS PlayerInJeep "#P JUMPED INTO A JEEP." /
  PlayerOutJeep "#P GOT OUT OF THE JEEP." / PlayerInHK "#P FOUND AN HK." / PlayerOutHK "#P DUMPED
  THE HK." ([english]; [jive] has its own) on the top-left status lines in colour 0x0C; the driver
  gets no line (0x130c80 prints none). This port does the same (a wreck putting the driver out is a
  leave too; a new round's return prints nothing).
- **Engine sound**: another driver's vehicle is heard where it is with the loop the campaign plays
  for the player's own ride (careng1 69 jeep, hk2 48 HK). DOS's own handling of other players'
  engines was not found (0x118f2a starts no sound).
- **Boarding** (0x130c80): the use key on foot asks for the first free, whole vehicle within 120 of
  the feet (DOS's 0x78); in a vehicle it climbs out. The server decides (`DmServer.EnterVehicle`):
  one driver a vehicle, the first asker's (DOS's occupied bit), the asker alive and on foot, the
  server's last pose of it within 1400 (DOS's reach plus pose lag, net_game.gd VEHICLE_REACH). The
  ride is the campaign's `VehicleRig` (controller, guns, cockpit, HUD), entered where the vehicle
  stands, facing its heading (no HK entry rise).
- **Leaving** (0x130e36): the vehicle stays parked at the driver's place and heading for good (no
  return to its marker); the driver steps out 90 right of the hull's middle and 40 up, nudged clear (DOS's 20 across lies inside the parked hull's collider here, which the clear-spawn nudge cannot see from inside: a test caught a human 8 across, 18 along inside a parked HK). A far-off or wild
  exit is replaced by the server's last pose. A driver who dies or leaves the game frees the seat
  where the vehicle is.
- **Driving**: DOS has no vehicle pose; the record follows the driver (0x390C7). Here the driver's
  pose carries the car yaw (the jeep's view is its turret); the server moves the vehicle with it and
  stamps the vehicle in the pose flags, so every machine draws that player as the vehicle
  (interpolated as any body): NETHUMER.3D with NETHMTRT / NETHMBRL turned to the driver's view, or
  NET_HK.3D (the avatar table's records 13 and 14); parked ones as DOS parks them, NETHUMMT.3D /
  NET_HKMT.3D (0x51F04). The guns (jeep plasma and rockets 20/22, HK laser and rockets 24/25) go out
  through `Fire` as any shot and their hits are reported with the gun's record; the jeep's ram
  strikes bodies as the campaign's does.
- **Damage** (0x122b52 -> 0x13114a): a hit on a driver goes whole to the hull (1000 jeep, 1500 HK,
  0x1311a5), the driver takes nothing; the attacker scores the hit. A driving avatar is struck as the
  vehicle's sphere (radius 55 / 110, the campaign's bodies); bots strike it the same. The driver's own
  hurts in the seat (his blast, a level blast) are self-hits and go to the hull too (`Hull.Remote`).
  The hull comes back in `VehicleState`; the driver's ARMOR bar shows it.
- **Wreck**: at zero the server marks it wrecked (DOS bit 8) and puts the driver out **alive** (DOS
  forces the exit); every machine plays the blast where it stood; the wreck is gone for the round.
  **No respawn**: DOS never clears bit 8 and has no vehicle timer (`replenish` is the items' only);
  the vehicles are rebuilt only on a map load, so here a NEW ROUND parks them all on their markers
  again, whole. DOS damages no parked vehicle (0x13114a only hurts the one being driven), so neither
  does this port.
- Bots drive them too (see "Bots (step 7)"). The console's `vehicles` lists them (where, heading, hull, driver).

## Classes: the host's rules (the owner's, 2026-10-11)

- **CLASSES: DOS / BALANCED** (`DmClassRules`, default BALANCED), a host option: the host screen's
  CLASSES box (remembered in the settings, `net.classes`: `"dos"` or `"balanced"`), server.json's
  `classes`, `--classes=dos|balanced` (on `--host` and on `--dedicated`, where it overrides the
  config). It travels in the welcome (`DmSettings.Classes`, protocol 6), so every machine plays the
  host's rules; the scoreboard's title line names them ("BALANCED CLASSES").
- **DOS**: every avatar 150 points and speed 1; the machine (TERMINATOR) loses the motion detector and
  sees through SKYNTTRM.COL with FUN_00132b00's marks (as SKYNET.EXE v1.01 does, below).
- **BALANCED**: the same, but the machine runs at 0.9 of the pace (`DmProtocol.BalancedMachineSpeed`),
  the price of seeing through walls. The Godot port's TERMINATOR rule (twice the bar, 0.85 of the
  pace) is gone.
- What follows the pace: the player's own body (`PlayerController.ClassSpeed`, set at each spawn),
  the bots' bodies (`DmBots`, at each spawn), and the server's travel budget (`DmServer.SpeedOf` in
  `TopSpeed`). The host screen's left box spells the class out under the chosen rules (150 HEALTH,
  MOTION DETECTOR or MACHINE VISION / NO DETECTOR, FULL SPEED or 0.9 SPEED).

## Classes (what SKYNET.EXE v1.01 has)

- **The choice.** NETMENU1's left box cycles thirteen avatars (FUN_00128b3b: [0x31f37] 1..13,
  forwards or backwards with the key's direction, the chosen idle body and head turning in the box
  through 0x134200 / 0x1348e4). The avatar table is at 0x312bf + n·0x6d: +0 the skin's texture bank,
  +4.. five 13-byte model names (body, head, head-shot, idle body, idle head), +0x45 the clip table
  (0x31c37 men, 0x31adb women), +0x49 the body's offset under the eye (65 soldier, 64 terminator,
  62 woman), +0x59 80 (the same for all; the vehicles 14-15 have their own). Avatars 1-3 = AVSOLDER
  (skins 0x132, 0x133, 0x13d), 4-7 = AVTRMNTR (0x13c, 0x13b, 0x13a, 0x130), 8-13 = AVFEMALE
  (0x125, 0x134-0x137, 0x13e).
- **The machines are avatars 7 and 13** (0x134a39, run every frame from the main loop 0x1177f0):
  in a network game ([0x30a50] bit 0x40000), not under water (bit 0x4000000) and not dead (bit
  0x400000), avatar 7 (the last AVTRMNTR) or 13 (the last AVFEMALE) sets [0x30a54] bit 4, clears
  weapon record 13's owned bit (`and [0x43714 + 13*0x60 + 0x5c], ~1`: no motion detector) and
  puts the VGA palette to SKYNTTRM.COL ([0x53700] := [0x5330d], loaded by 0x134a00 at the
  mission start). The bit also travels in the player's network flags (0x400, FUN_00118d4c).
  Every other avatar (the AVTRMNTR skins 4-6 included) plays as a plain soldier.
- **The vision's marks** (FUN_00132b00, after the 3D view while bit 4 is set): every actor of
  the player list that is present, 3D distance from the eye (FUN_0014d775) at least 0x32 and at
  most 0x9c4, in front (depth > 8) and projecting inside the viewport; no line-of-sight test. A
  living one: glyph 0x12 at the point (FUN_00011da0; lit while within DOS x 0x8d..0xb3 and the
  crosshair's rows, 4 frames of 8), Trg_String "#P" at (x+4, y-6) and the range "NNNM" (units /
  0x28) at (x+4, y+2), doubled in 640 x 480. A dead one (its flag 4): Ded_String "#P TERMINATED"
  at (x-0x30, y).
- **Nothing else differs.** No class factor in the damage (the 150-point bar is everyone's,
  0x122348) nor in the movement (walk 250 / run 400 for every avatar); the only per-avatar numbers
  are the model, skin, clips and the 1-3 unit body offset. The weapons, the start kit and the
  sounds are the same.
- **DOS's motion detector** (record 13 in hand: FUN_0011ff00 from the weapon draw 0x125d1a) is a
  plot on the scanner's own screen, not marks over the view: each player rotated into the view's
  frame (FUN_00120265) and drawn in 0xb3 when it drives (0x20), moves or fires (0x10) or is a
  machine (0x400), the nearest one ahead in 0x0c with its name; in the campaign it plots the
  robots. Table 0x38504 places it in WEAPON13.CFA's low frame: the holder at (39, 32) facing up,
  the clip x 19..59, y 12..33 (the green screen), the name centred at (39, 38); 128 units a pixel,
  the distance ahead scaled by 0.9 ([0x5f158] = 0xe666). The target: |x| ≤ ahead and not above the
  screen, the smallest |x|, then the nearest. The sweep: record 13's +0x2c (18 frames a second) over
  the CFA's 7 frames, and on frame 0 MOTLOOP2.RAW (sound 0x61) restarts, also sent to the others
  (FUN_00122e41). The scanner in hand also sets the holder's network flag 0x10 (FUN_00118d4c), so a
  still holder shows on the others' scanners; a still HUMAN without it does not. Ported
  (`MotionDetector.Scan`, `ScannerSweep`, `DmHud`); the others' beeps ride the pose (protocol 8):
  `DmProtocol.FlagScanner`, the owner's word, kept by the server for a HUMAN on foot only, and each
  machine runs a sweep per holder (`ScannerBeeps`), MOTLOOP2.RAW at the holder (`Play3D`, the
  port's 3D falloff as for the others' shots) as the scanner comes into the hand and once a sweep.

## Steps

1. **Done: transport and rules, headless.** RetroEng.Net with UDP and loopback
   transports and LAN discovery; NETLEVEL.PRS; arenas; the server rules, the
   protocol, host and client sessions; unit tests (loopback and real UDP on
   127.0.0.1, the rules on a hand-driven clock); the two-process smoke test.
2. **Done: the game in a window.** `--host` / `--join` start the real game on the arena:
   the local player is the campaign's player controller and weapons; the session
   sends its pose and reports the hits its weapons score on avatars (the
   `IDamageable` path); damage, death and respawn come from the server; the HUD
   shows the server's health and armour. Spawns get the campaign's clear-spawn
   nudge (`PlayerStart.Clear`) on the server.
3. **Done: avatars.** The other players drawn as the Godot port's `dm_avatar.gd` does
   (T800RFL body, walk animation from the pose flags, death pose), interpolated
   between poses; their shots drawn (muzzle flash, tracer, projectiles) from
   `FireOf`.
4. **Done: pickups.** The arena's items as sprites from the server's list, taken and
   returned on the server's word; the taker gets ammo and weapons locally; the map's own
   pickups hidden.
5. **Done: HUD and scoreboard.** Frags, clock, kill feed, the TAB scoreboard, T/ENTER chat,
   the end-of-round screen and the host's restart; HUMAN / TERMINATOR class choice and the
   TERMINATOR's DOS machine vision (marks and SKYNTTRM.COL) and the HUMAN's motion detector;
   no local pause; the death view.
6. **Done: menus.** NEW GAME → MULTI-PLAYER → NETJOIN1.IMG; NEW GAME hosts on NETMENU1.IMG
   (arena from NETLEVEL.PRS, time and frag limits, bots, bot skill, port, items, class) and
   JOIN GAME lists the LAN games over NETSCAN1.IMG plus an address box, as the Godot port's
   menu.gd lays them out. See "Menus (step 6)" above.
7. **Done: bots.** `bot_brain.gd` on the server (`BotBrain`, one per bot; `DmBots`
   steps them at 60 Hz from `DmHost.Update` and sends what they do through the host's
   own calls): hunts the nearest visible player, keeps a weapon-dependent distance
   while strafing, hitscan bursts with a skill-dependent spread (skill 0-2), roams
   item and spawn spots, jumps obstacles, moves with the soldier's own controller.
   Bots report hits like humans. See "Bots" above.
8. **Done: the map's own state.** Doors, levers and destructibles owned by the server:
   clients send intents, the server runs them through its level and sends the
   changes (the Godot port's M5 trigger deltas); a late joiner gets the whole
   overlay after the welcome. Crate drops become server pickups (`PickupAdd`). See
   "The map's own state (step 8)" above.
9. **Done: vehicles.** Jeeps and HKs on markers 101/102 and the NETLEVEL counts (DOS's
   placement), entered and left with the use key on the server's word, the driver's pose drawn as
   the vehicle, the hull taking every hit, wrecks for the round. See "Vehicles (step 9)" above.
10. **Done: dedicated server.** No local player; from the command line with a config file (no
    SDL, no window, GPU or sound: VPS / Linux) or from the host screen's DEDICATED behind SERVER
    STATUS; map rotation; password, bans, connections per address, idle drop; console on stdin. See
    "Dedicated server" below.
11. **Done: master server.** `public` / `master` in the config (or the host screen's PUBLIC) announce
    the server; JOIN GAME's INTERNET tab lists the master's games with their pings; a game behind a
    NAT is joined through LiteNetLib's NAT introduction. See "Master server".

## Dedicated server

The owner's decision (docs/plans/editor-vr-menus.md section 5, item 6): a server with no player of its
own, started from the command line with a config file, where the process must not initialise SDL
(video, GPU or audio) so it runs on a VPS or a Linux box, and from the menu, in-process behind a status
screen. One executable for both.

### Design

- `DmDedicated` (src/SkyNet.Game/Net/DmDedicated.cs) owns a `DmHost` built with no local name
  (`DmHost.Dedicated`: no player 1, `LocalId` 0; clients are players 2 and up as before) and its own
  `GameSession` with no sound bank: the arena's collision, its level in the server role
  (`DmLevelServer`: doors, levers, destructibles, crate drops, level blasts credited to the breaker,
  NEW ROUND resets), the parked vehicles' colliders and the bots. The session's soldier is never
  stepped; `GameSession.TickServer` runs the level's behaviour at 60 Hz with a probe nowhere (the
  walk-in triggers are the server's sweep of the other bodies, step 8) and the water's glide. No clock
  of its own: its owner calls `Update(dt)`.
- **Command line**: `Program.cs` hands `--dedicated` (and `--password-hash`) to `DmDedicatedRunner`
  before anything else runs, so no SDL type is touched: no window, no GPU device, no audio device, no
  textures. The runner logs `SDL: native SDL3 library not loaded (no window, GPU or audio)` at the start
  and again at the exit (it looks for SDL3.dll / libSDL3.so* among the process's modules), which
  `tools/net-smoke.ps1 -Dedicated` checks. The log goes to stdout and to `log_file`; commands come on
  stdin (on Windows only when stdin is redirected: the game is a GUI program whose parent console keeps
  its keyboard; on Linux a terminal works). Ctrl+C and SIGTERM stop it: every client is told "The
  server is shutting down." Exit codes: 0 stopped, 1 the server did not start (port taken, map
  missing), 2 a bad command line, config or missing data.
- **Menu**: the host screen (NETMENU1.IMG) has DEDICATED Y/N in its message box, beside CHEATS (title
  only). START with Y builds a one-map config from the screen (`ServerConfig.FromOptions`: the arena,
  limits, items, bots, port, cheats; no log file of its own (the game's log), no ban file, no idle
  drop; `--bind` applies) and pushes SERVER STATUS (`ServerStatusScreen`, the DOS panel kit): the
  server's name and port; the map, its place in the rotation, the goal and the time left (or "NEXT MAP
  IN n"); the players by rank with kills, deaths, points and address (bots marked); the last four log
  lines; NEXT MAP and QUIT. The screen's tick is the server's frame. QUIT stops the server for
  everybody and returns to the host screen; Back or Esc first ask STOP THE SERVER? in a DOS YES / NO
  box (QUIT.IMG with the port's caption over its own; the server runs on under it). DEDICATED is kept
  in the settings (as Y only on the title's menus).
- **Round end and rotation**: when the round ends (`MatchOver`) the end stays up for
  `intermission_seconds`, then the next entry loads (`DmDedicated.Next`): the session enters the new
  map, a new `DmServer` gets its arena, and `DmHost.ChangeArena` carries every connected client over
  under the same id, name and class and welcomes it again (scores start afresh; the new entry's bots
  start); a one-map rotation starts a NEW ROUND on the same map instead. The windowed client
  (`DmGame.OnWelcome`) drops the seat, the bodies, the engines and the level mirror of the old arena,
  shows NEXT MAP and loads the new one.

### Internet play

- **Password**: `password` (or `password_sha256`, so the file need not hold the text) in the config,
  `--password` on `--join`. The server keeps only `SHA-256("SKYNET-DM-PASSWORD:" + password)`; after
  the hello it sends a fresh 16-byte nonce (`Challenge`) and the client answers with HMAC-SHA256(key,
  nonce) (`Auth`), compared in constant time. The password never travels and a captured answer is no
  good for the next join. Refusals: "This server needs a password." / "Wrong password." (logged). The
  hello timeout (10 s) covers the answer too ("No password answer from this client."). `SkyNet
  --password-hash TEXT` prints the hex for `password_sha256`. The JOIN screen's PASSWORD box (or
  `--password=...`) gives the password.
- **Kick and ban**: the console's `kick <id> [reason]` ("Kicked: reason"), `ban <id|address>
  [reason]` (the player's address; everyone from it is kicked with "Banned: reason"), `unban
  <address>`, `bans`. The bans are saved to `ban_file` (JSON: `{ "bans": [ { "address", "name",
  "reason", "when" } ] }`, written atomically) and checked before a connection is accepted
  (`UdpTransport.Screen`; an IPv4-mapped IPv6 address compares as the IPv4 one): "You are banned from
  this server: reason".
- **Connections per address**: `max_per_address` (default 2) connections from one address at most,
  checked at the door: "Too many connections from your address (at most N)." Players behind one NAT
  share an address; raise it for a LAN party.
- **Idle drop**: a client that neither moves (more than 2 units), turns, fires, hits, speaks, uses nor
  boards anything for `idle_kick_seconds` (default 300, 0 never) is dropped: "Idle for N seconds."
  Poses that stand still do not count. A dead connection (no packet at all) is dropped by the
  transport after `timeout_seconds` (default 10; LiteNetLib's DisconnectTimeout).
- **Version**: the hello's version is checked first, with the message above.

### server.json

`config/server.sample.json` (tracked; `build-release.bat` copies it to `release\server.sample.json`,
never over a real `server.json`) lists every key with its default and a comment: copy it to
`server.json` and change what you need. Every key is optional. Comments (`//`) and trailing commas are allowed. An unknown key, a wrong type or
a value out of range stops the server with the file, the JSON path, the line and the column, e.g.
`server.json: $.rotation[1].goal (line 14, column 33): expected points, kills, lives or time, found "frags"`.

```jsonc
{
  "name": "SKYNET DEDICATED",        // listed in the LAN browser (48 characters at most)
  "port": 27015,                     // 0: any free port (printed as "listening on port N")
  "bind": "",                        // an IP address to serve on; "" every interface
  "max_players": 8,                  // humans, 1-16 (bots do not count)
  "max_per_address": 2,              // connections from one address, 0 no limit
  "password": "",                    // or "password_sha256": "<64 hex digits>", not both
  "cheats": false,                   // the console's cheats for the players
  "classes": "balanced",             // dos (equal classes) | balanced (the machine at 0.9 of the pace)
  "public": false,                   // announce to the master server (see "Master server")
  "master": "",                      // its host[:port] (default port 27900); "" = masters.txt beside the exe
  "discovery": true,                 // answer LAN browsers on UDP 27016
  "motd": "",                        // a chat line from SERVER to every joiner
  "log_file": "logs/server.log",     // relative to this file; "" none (the last five kept)
  "ban_file": "bans.json",           // relative to this file; "" bans until the server stops
  "record_demos": false,             // every round as a network demo (docs/demos.md "Network demos")
  "demo_dir": "demos",               // where they go, relative to this file
  "idle_kick_seconds": 300,          // 0 never
  "timeout_seconds": 10,             // 2-120
  "intermission_seconds": 10,        // the round's end on screen before the next map
  "bots": 0, "bot_skill": 1,         // defaults for every map: 0-12 bots, skill 0-2
  "goal": "kills",                   // points | kills | lives | time; default NETLEVEL.PRS's
  "goal_limit": 10,                  // 0 none; default DOS's for the goal
  "time": 10,                        // minutes, 0 none
  "rotation": [                      // at least one; a plain "MAP.605" takes the defaults
    "MAP.605",
    { "map": "MAP.601", "goal": "kills", "goal_limit": 15, "time": 10, "bots": 3, "bot_skill": 2,
      "max_players": 8, "replenish": true, "items": { "health": 4, "rockets": 0 } }
  ]
}
```

A rotation entry's `items` override NETLEVEL.PRS's counts per category (the host screen's keys:
jeeps, hks, bullets, energy, armor, health, slugthrowers, lasers, plasmas, launchers, grenades,
rockets); a category it leaves out keeps the arena's.

### Console

On stdin (command line): `status` (one line: name, port, map, place in the rotation, time left or the
intermission, players, bots, uptime, rounds, joins), `players` (id, name, kills, deaths, points,
address, idle seconds), `kick`, `ban`, `unban`, `bans`, `map <MAP.NNN>` (now; the rotation goes on from
that map's entry, or stays where it was), `next` (the next map now), `say <text>`, `quit`, `help`.
Replies and the log go to stdout. The menu's status screen offers NEXT MAP and QUIT.

### Linux

`dotnet publish src/SkyNet.App -c Release -r linux-x64 -p:PublishAot=false --self-contained false`
builds a framework-dependent server (run `./skynet-sdl --dedicated --config server.json` with the .NET 10
runtime installed). NativeAOT for Linux has to be published on Linux (the .NET SDK does not
cross-compile native code: "Cross-OS native compilation is not supported"). The server path's only
P/Invoke is `TextLogSink.AttachParentConsole` (kernel32, skipped off Windows); the password hashing
uses the platform's crypto (OpenSSL on Linux). Checked on Windows only: the linux-x64 publish builds;
nothing was run on Linux.

## Master server

The owner's decisions (2026-10-11): the master server is a separate service (its host is not decided
yet, so every address is configurable); a home-hosted game behind a NAT may be listed, so friends can
join it; LiteNetLib's NAT introduction (`NatPunchModule`) is the way in.

### Design

- **Game-agnostic.** The protocol, the service and both clients live in the retroeng repo (`src/RetroEng.Net/Master`)
  (`MasterProtocol`, `MasterServer`, `MasterAnnouncer` + `InfoResponder` for a game server,
  `MasterBrowser` for a join screen); the game only fills in a `ServerInfo` (game id, protocol version,
  name, map, players / cap, bots, password flag). `tools/SkyNet.Master` is the console program around
  `MasterServer` (no SDL, window or sound).
- **Everything is a LiteNetLib unconnected message**, so a game server sends its heartbeats *from its
  game socket*: the address and port the master sees are the NAT mapping players will use. The NAT
  introduction is LiteNetLib's own NAT message with the listed address as the token.
- **A heartbeat** every 60 s (the master's ack can ask for another interval) and at once on a map
  change (`DmHost.ChangeArena`); a goodbye when the server stops (`DmHost.Dispose`). An unanswered
  heartbeat is sent again after 10 s. The master drops an entry after 3 missed beats (190 s:
  `heartbeat_seconds * missed_beats + 10`).
- **Reachability before listing.** A new entry (or a restarted server: a new random 32-bit instance id)
  is challenged twice: a nonce to the address its heartbeat came from (it is the game server, not a
  spoofed sender) and, from the master's second socket, a nonce to the heartbeat's IP at the reported
  game port (the "direct" check: the port is open to anyone). Echoing the first lists it; echoing the
  second marks it *direct*; with only the first after a second's wait it is listed as *NAT*
  (`MasterReach`). No echo within `challenge_seconds` (10): not listed. The ack tells the server the
  address the master sees and how it is listed, and the server logs it ("listed as ADDRESS:27015
  (reachable directly)" or "(behind a NAT: players join through the master's introduction; forward
  UDP port 27015 if they cannot)").
- **The list** is asked per game id and protocol version (a browser sees only games it can join), most
  players first, in pages that fit a datagram (about 1000 bytes, 13 or more entries a page). The first
  request gets an 8-byte cookie (HMAC of the asker's address and the minute) instead of a page, and a
  page goes only to a request carrying a valid cookie: a spoofed sender cannot turn the master into an
  amplifier.
- **Ping.** The browser sends each listed server a query (padded to 96 bytes, so the answer is never
  much bigger) straight to its game port; the server answers with its info now (`InfoResponder`, a few
  answers a second per address). The INTERNET list shows the round trip and the fresh player count. A
  server behind a NAT may not answer (it shows `---`) until a player has been introduced to it.
- **Joining** an INTERNET entry always goes through the introduction (`UdpTransport.ConnectIntroduced`):
  the client asks the master to introduce it to the listed address (every second), the master sends
  both sides each other's internal and external addresses, both punch, and the client connects to
  wherever the server's punch came from. With no introduction within 5 s it connects to the listed
  address directly (an open or forwarded port still works). Joining a direct server this way costs
  one round trip.
- **Rate limits** per IP address (token buckets): heartbeats 0.5/s (burst 8), list pages 4/s (burst
  16), introductions 1/s (burst 5); at most 8 servers per address and 4096 in all (refused with the
  reason in the ack, logged by the server). Internal addresses a server or a client reports are
  believed only when they are LAN addresses (otherwise the external one is used), so an introduction
  cannot make a server punch at a stranger.

### Messages

Every payload starts with `RMS1` and a kind byte; fields as `NetWriter` writes them (little-endian,
strings with a u16 length). An endpoint is 4 IPv4 bytes and a u16 port.

| Kind | From → to | Fields |
|---|---|---|
| 1 Heartbeat | server → master | u32 instance, info, u16 game port, endpoint internal |
| 2 HeartbeatAck | master → server | u32 instance, u8 state (0 checking, 1 listed, 2 refused), endpoint seen, u8 reach (1 direct, 2 NAT), u16 heartbeat seconds, string reason |
| 3 Challenge | master → server | u32 instance, 16-byte nonce |
| 4 ChallengeReply | server → master | u32 instance, the nonce |
| 5 Goodbye | server → master | u32 instance |
| 6 ListRequest | browser → master | string game id, i32 protocol, u16 page, 8-byte cookie (zeros at first) |
| 7 ListCookie | master → browser | 8-byte cookie |
| 8 ListPage | master → browser | u16 page, u16 pages, u16 total, u8 count, then per entry: endpoint, u8 reach, string name, string map, u8 players, u8 cap, u8 bots, u8 flags (bit 0 password) |
| 9 InfoQuery | browser → server | u32 nonce, padding to 96 bytes |
| 10 InfoReply | server → browser | u32 nonce, info |

Info: string game id (`skynet` or `shock`, `DmProtocol.GameId`), i32 protocol (`DmProtocol.Version`),
string name (48), string map (`MAP.605`), u8 players, u8 cap, u8 bots, u8 flags (bit 0 password).

### Running the master

```
SkyNet.Master [--config master.json] [--port N] [--bind IP] [--quit-after S]
```

It prints `listening on port N` first (port 0: any free one), logs listings, map changes, expiries,
goodbyes, refusals and introductions (stdout and `log_file`), takes `status`, `list` and `quit` on
stdin, and stops on Ctrl+C or SIGTERM. Exit codes: 0 stopped, 1 the port did not open, 2 a bad command
line or config. It needs UDP `port` open to the internet (and answers from a second, ephemeral port
for the direct check: allow outgoing UDP).

```jsonc
// master.json: every key optional; comments and trailing commas allowed, unknown keys refused
{
  "bind": "",                       // an IP address to serve on; "" every interface
  "port": 27900,
  "max_servers": 4096,
  "max_servers_per_address": 8,     // several servers behind one NAT
  "heartbeat_seconds": 60,          // what the master asks servers for
  "missed_beats": 3,                // an entry goes after this many (plus 10 s)
  "challenge_seconds": 10,
  "query_rate": 4, "query_burst": 16,         // list pages a second per address
  "heartbeat_rate": 0.5, "heartbeat_burst": 8,
  "introduce_rate": 1, "introduce_burst": 5,
  "games": [],                      // the game ids listed; [] any (e.g. ["skynet", "shock"])
  "log_file": "logs/master.log"     // relative to this file; "" none (the last five kept)
}
```

`config/master.sample.json` (tracked) is that file with every default; `build-master.bat` copies it to
`release\master\<rid>\master.sample.json` (never over a real `master.json`).

Publishing (framework-dependent; the .NET 10 runtime on the box):
`dotnet publish tools/SkyNet.Master -c Release -r linux-x64 --self-contained false -o out` (or
`-r win-x64`), then `./SkyNet.Master --config master.json`. Checked on Windows: both publishes build;
the Linux one was not run.

### The game's side

- **Dedicated server**: `"public": true` and `"master": "host[:port]"` (default port 27900) in
  server.json. The log says what the master answered; `status` and SERVER STATUS show it (LISTED,
  LISTED (NAT), MASTER CHECKING, MASTER REFUSED, MASTER SILENT).
- **Menu host**: the host screen's PUBLIC Y/N (remembered) announces a hosted game or a menu
  DEDICATED server to the settings' master server. START with PUBLIC and no master set says so and
  does not start.
- **The master's address** (`MasterAddresses`): the repo holds none, and the default is empty (the
  INTERNET tab then says NO MASTER SERVER SET, with a hint; PUBLIC refuses to start). It comes from,
  in order: the command line (`--master HOST[:PORT]`, the game's and the dedicated server's) or
  server.json's `master`; the settings file beside the exe (`net.master` in
  skynet-csharp-settings.json); then `masters.txt` beside the exe, one `host[:port]` a line, `#` and
  `//` comments and empty lines skipped, the first usable line used. A release can ship that file:
  `build-release.bat` copies an untracked `release-config\masters.txt` (git ignores `release-config/`)
  into `release\`, and always the tracked sample `config/masters.sample.txt` as
  `release\masters.sample.txt` (the format, comments only, no real host: `# master.example.org:27900`). Use a DNS name (e.g. `master.example.org`) rather than an IP address, so the
  master can move without new builds or settings.
- **Headless**: `--host=MAP.605 --public --master=HOST:PORT`; `--find=NAME --master=HOST:PORT` (instead
  of `--join`) finds the server of that name in the master's list and joins it through the
  introduction (`tools/net-smoke.ps1 -Master`).

### NAT notes

- The introduction works when both NATs keep one mapping per socket whatever the destination
  (endpoint-independent mapping, most home routers). Behind a symmetric NAT (some mobile and carrier
  NATs) the punch fails: the client falls back to a direct connection after 5 s, which works only
  when the server's port is open.
- **Port forwarding is the fallback**: forward UDP port 27015 (or the server's `port`) to the server
  machine. The master's direct check then finds it and lists it as reachable directly; nobody needs
  the introduction.
- Players behind the same NAT as the server: the introduction carries the internal (LAN) addresses
  too, so they reach it without hairpinning. On the LAN the LAN tab is simpler anyway.
- A public server takes part in NAT introductions (LiteNetLib's NatPunchModule is on for its socket):
  anyone who can send it a datagram can make it send two small punch packets to an address; this is
  no amplification (one packet in, two of the same size out) and only for public servers.

## Rules (the owner's, 2026-10-11)

DOS findings are SKYNET.EXE v1.01 addresses.

1. **Scoring and the goal.** NETLEVEL.PRS (parser FUN_00127dfb) has `goal` (table 0x4c2cb: POINTS 0,
   KILLS 1, LIVES 2, TIME 3; every shipped arena says kills), `scoring_kill` / `scoring_hit` /
   `scoring_death` (500 / 100 / 400, copied by FUN_00128488 as +kill, +hit, -death) and no goal
   amount: DOS starts with 10000 points, 10 kills, 10 lives, 5 minutes (FUN_00128449) and the host
   menu sets the amount (FUN_00128f99, at least 1) and cycles the goal (FUN_00128f77). DOS scores on
   the victim's machine (FUN_00121313): a hit that does not kill gives the attacker `scoring_hit`,
   the killing blow a kill and `scoring_kill` instead (not both); every death costs the victim one
   death and `scoring_death` (FUN_00122b52); a suicide or a death to the world (killer 0) gives no
   kill to anybody and takes none away (no -1 frag any more). The host checks the goal every frame
   (FUN_00121370): POINTS the leader's points, KILLS the leader's kills, LIVES the most deaths of
   anybody, TIME the minutes; at the limit the round is over for all. The ranking (FUN_0012159a):
   POINTS and TIME games by points, kills, fewest deaths; KILLS by kills, fewest deaths, points;
   LIVES by fewest deaths, kills, points; then the name. Here: `DmSettings.Goal` / `GoalLimit`
   (`--goal`, `--goal-limit` or `--frags`; default NETLEVEL's goal at DOS's default amount; 0 none),
   `DmServer.CheckGoal`, `DmServer.Rank` (the board and RANK). The menu's GOAL box cycles the goal,
   the box after it is its limit; the port's TIME box stays a time limit (minutes, 0 none) on any
   goal, and a TIME goal takes the shorter of the two. End lines: "X REACHED N POINTS / KILLS",
   "X LOST N LIVES", "TIME LIMIT REACHED".
2. **NEW ROUND resets the map.** The server's level snapshots itself when the arena starts
   (`DmLevelServer`); the host's new round restores it (`LevelBehaviour.ResetTo`; the host's session
   also registers the breakables as weapon targets again and restarts the start's ambient loops,
   `GameSession.ResetLevel`) and sends every client a `LevelState` of kind 2 (reset, protocol 4):
   the client restores its own start snapshot, then lays the (normally empty) overlay over it.
   Crate drops were already gone with the round.
3. **Level blasts are the server's.** The level remembers who last struck each record
   (`LevelBehaviour.BreakerOf`: a client's hit intent, the host's own weapons, a blast's chain
   reaction on the breaker's account, 0 = the world). The host's blast (`DmGame.OwnLevelBlasts`)
   strikes the records as the campaign's does and every living body through `DmServer.Hit`
   (`DmLevelServer.Blast`: DOS's falloff, a clear line, weapon `LevelBlastWeapon` -2), credited to
   the breaker: a death in it is the breaker's kill. A client's blast strikes nothing (no more
   self-reported level-blast hits). DOS for comparison: a blast is owned by the player whose machine
   spawned it (FUN_0012420f broadcasts it with the creator's id; a received one is credited to the
   sender, 0x11eb54; one's own blast is owner 0 locally, so one's own rocket is a suicide).
4. **Travel guard.** DOS checks nothing (the state receiver 0x11909a copies positions as sent).
   Here `DmServer.ClientPose` keeps a travel budget per player: horizontal plus upward units at the
   top speed (on foot the run with the run sidestep, 400 and 310, times the class pace under CLASSES; a jeep 683.6;
   an HK 800 with 550 sidestep) x 1.5 lag slack, saved up for at most 0.5 s plus 64 units; downward
   3000 u/s (falls). A pose beyond it is clamped to what the body could have run and logged (once a
   second a player); a far pose in the second after a respawn (sent before the client saw it) is
   dropped; a dead body stays where it fell. Pickups, use keys, boarding and hits are measured from
   the clamped place. Respawns start the budget afresh (64 units).
5. **Bots** (see "Bots"): DOS weapon and ammo damage, the shotgun's blast and the laser's bolt,
   weapons taken from items into the hand, bodies blocking each other, fall damage.
6. **Cheats.** DOS skips its flagged CHEAT.PRS codes in a network game (FUN_00142658 with
   FUN_001426da's flag: god, every weapon, the super UZI, ammo, health and armour, the tele view,
   supertargeting, cloak, mission complete). Here the console's `tp`, `god`, `noclip`, `give`,
   `arnold`, `superuzi`, `slugs`, `surgery`, `health`, `armor`, `weapon` and `killall` (and their
   CHEAT.PRS names) refuse in a network game unless the host allowed them: `--cheats` or the host
   menu's CHEATS Y/N (`DmSettings.Cheats`, sent in the welcome). Scripted screenshot runs pass `--cheats`.
7. **Kill feed.** DOS's network events on the top-left status lines in colour 0x0C (FUN_001442e4 →
   FUN_0012daa8 → FUN_0012f8f7), STRINGS.PRS's texts with #P the player and #K the killer:
   NewPlayer (0x1191e0), PlayerLeft (0x118c7c), PlayerRestart (0x118f74, another player's respawn),
   PlayerDied1-8 (rand & 7) or PlayerSuicide1-4 (rand & 3) (0x1192c4), and on the victim's own
   screen LocalDied "#K BLOWS YOU AWAY." (0x132279) / LocalSuicide (0x122c72). The Godot port's feed
   at the top right is gone. Not ported: PlayerSkull1-4 (0x119106, the head-shot kill lines: DOS's
   hit type 3 triples the damage, which this port does not model).

## Open questions for the owner

- **Goal box layout.** DOS's NETMENU1 items (0x4bd42) put the goal amount at x 247-311, y 23-30
  (the box this port uses for TIME minutes) and the goal cycle at y 37-44, and DOS's TIME is the
  NIGHT / MORNING toggle (FUN_0012905c), not a limit. This port keeps TIME as minutes and GOAL /
  limit on the second row. Follow DOS's boxes?
- **Bodies blocking.** 22-unit capsules (the Godot port's player capsule); DOS's avatar collision
  was not decoded.
- **Avatars.** DOS picks one of thirteen avatars (three soldiers, four terminators, six women, by
  skin) and only avatars 7 and 13 are machines, a woman among them. Done: the thirteen are chosen,
  sent and drawn in their skins (protocol 7); the console's `class` keeps the avatar when it is of
  the new class, else takes the class's default.
- **Motion detector.** Done: DOS's plot on the scanner's screen, its sweep and beep, heard by the
  others, and a holder shown as a mover (protocol 8; see "Classes").
- **Players.** At most 16 humans (the Godot port's cap), or NETLEVEL `maxplayers`
  when it is set. 8 instead? (A dedicated server's default `max_players` is 8.)
- **Master server.** (a) Several lines in masters.txt: only the first usable one is used (no
  fallback to the next, no listing on all of them). Announce to every one and merge their lists?
  (b) The master's address cannot be typed in the menus (settings file, `--master` or masters.txt
  only). A MASTER box on the INTERNET tab? (Settled 2026-10-11: the host screen's PASSWORD box and
  LOCKED as the password flag, see "Menus".)
- **Protocol.** Not wire-compatible with the Godot port (ENet and Godot RPCs there).
  Fine?
- **LAN discovery reply.** Binary (name, map, players, bots, cap, port) instead of
  the Godot port's JSON.
- **Spawn choice.** The spawn farthest from the living (the Godot port); DOS picks
  at random among its pairs. Keep?
- **Vehicles (step 9).** (a) DOS has no vehicle respawn; a new round parks them again (DOS rebuilds
  them on a map load). A timed return (the Godot port's 30 s) instead? (b) "A JEEP BLEW UP" / "AN HK
  BLEW UP" on the status lines is the port's (DOS prints only the driver's leaving). (c) Bots drive (2026-10-22). Everybody steps out 90 aside instead of DOS's 20 across (see "Leaving").
  (d) The jeep rams other players with the campaign's rule (v/2 points), reported as a hit with the
  gun's record. (e) The DOS facing step is assumed to be an 11-bit angle (22.5 deg); its sense is not
  checked. (f) Other drivers' engines: DOS's way not found; the campaign's loop is played at them.
  Settled by the DOS code: items only on marker 100 in DOS's layout, parked vehicles solid as map
  records, the PlayerIn/Out lines from STRINGS.PRS (see "Vehicles (step 9)").
- **Bodies.** Step 3 was planned with T800RFL for everyone (the Godot port's first
  body); the Godot port has since moved to DOS's own deathmatch bodies
  (AVSOLDER / AVFEMALE / AVTRMNTR with their heads), and this port follows that,
  keeping T800RFL as the fallback. Right?
- **Tracers.** dm_game.gd draws a tracer line for another player's bullet; DOS
  draws none and neither do this port's own shots, so other players' bullets show
  the muzzle flash and the puff where they land. Add the Godot tracer?
- **The map's own pickups** (MAP.605 has 2) are hidden in an arena, as the Godot port leaves
  them out. DOS may show them: keep hidden?
- **Network menu, Godot vs DOS.** (a) The left box of NETMENU1 shows the class as text; the Godot
  port draws the chosen body (AVSOLDER / AVFEMALE / AVTRMNTR) turning there, clickable through the
  three bodies. Port that 3D box? (b) JOIN GAME uses NETMENU1 with the LAN list in its left box and
  NETSCAN1 under it; the Godot port has a plain framed panel instead, and DOS (IPX) scanned first and
  then showed the host's settings on NETMENU1 (LOCKED, read-only). Keep this mix? (c) NETNAME.IMG
  ("ENTER YOUR NAME") is in the archive but SKYNET.EXE v1.01 never names it; the NAME box is typed
  in place. (d) LOCKED shows the password flag (the owner's, 2026-10-11; DOS's own meaning, maybe
  a game that takes no more joins, is not decoded). (e) REPLENISH's 13-pixel box shows Y / N (menu.gd's YES does not fit the DOS font).
- **The map's own state (step 8).** (a) Walk-in triggers are swept on the server from every
  body's pose instead of each client sweeping itself and sending an intent (Godot); no arena has
  one, so nothing plays differently today. Keep? (b) A walk-on pad (state bit 0x10) under a client's
  feet does nothing, as in the Godot port (the server does not know the floor a client stands on;
  no arena has one). (c) A client's lights that a chain switches or fades come from
  the server; flickers and strobes run on each machine with its own random phase.
- **The 150-point bar and the bots.** Every hit now costs 6.7 times as much of the bar as it did
  under 1000 points (a rocket or a satchel kills outright, three pipe blows do); the bots' aim and
  the round's pace were tuned on the old bar.
