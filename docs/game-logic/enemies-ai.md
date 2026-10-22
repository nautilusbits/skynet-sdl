# Enemies, AI and AIS

## Placement

- Enemies are **variant-3 markers of type 2** (not variant-1 actors). The
  enemy type is the byte at sub+10; **bit 7 is the convoy flag** (mask
  `& 0x7F`, see path vehicles).
- Marker sub+2 ≠ 0 makes a **dormant trap** (state 12): no AI; explodes
  when the player comes within that distance (effect bank TEXTURE.367,
  sound 0x26).
- Marker sub+4 (act) and sub+5 (state) are copied to the actor
  (EnemiesStartMarked 0x12a439, v1.01). State 0x04 = act on death.
- Enemy constructor FUN_0012959d (v1.00). Act 0xF3 sprites reveal robots
  built hidden at map start (SpawnEnemiesInit FUN_00129000, v1.00).

## Enemy type table

Base **0x44d00** in v1.00 notes; **0x44E00** in v1.01 (path-vehicle
decode). 100 records × 0x1C bytes (7 u32):

| Offset | Meaning |
|---|---|
| +0x00 | pointer (+0x30000) to a resource record; the .3D file name is inline at record+4 (meshes in MDMDENMS.BSA) |
| +0x08 | AI state id (below) |
| +0x0C | pointer to the child-segment list (0 = none) |
| +0x14 | pointer to stats / parameter block |

**Child segments**: 32-byte entries `{continue, child_type, ox, oy, oz,
pitch, yaw, roll}`, loop while `continue ≠ 0`. `child_type` indexes the
same table recursively (turret base → gun head → missile pod). Offsets in
mesh-origin units, DOS Y down, rotation 11-bit. 40 multi-segment types:
turrets, tank turrets, twin-barrel heavy turrets (guns at x = ±51), the
T-rex (legs + head + two guns), the boss (chest + head + two turrets).

Types 0x80+ are body-part / wreck types (state 1).

## AI state ids

Handler table **0x59900** (pointers +0x30000), driven by
EnemyGroupDoAI FUN_00129871 (v1.00).

| State | Family | Handler |
|---|---|---|
| 0 | static base (gun tower base, grabber base) | |
| 1 | ballistic wreck part (types 100-170) | |
| 2 | turret / gun segment | FUN_00137965 (v1.01) |
| 6 | hover / ground chaser (globe, drone, scout, hover tank) | 0x136d45 (v1.00) |
| 7 | walker (T-800 family, raptor, spider bot, T-rex legs) | 0x13be00 (v1.00) |
| 8 | head segment with a script (T-rex head) | |
| 9 | flyer (HK) | 0x13c300 (v1.00) |
| 10 | machine, script only (torture, grabber, welder arm) | |
| 11 | path vehicle | 0x127400 (v1.01) |
| 12 | dormant trap | 0x14285e (v1.00) |
| 13 | tank | 0x13c900 (v1.00) |

## Activity window

Actors tick once per frame (dt = 1/fps, fps clamped 5..50) only when they
stand in the **5 × 5 MAP-grid cells** around the player (0x12980f,
`edx = 5`: a cell-index test, so up to ≈3000 units, not a radius), plus
once at spawn. Hidden actors (flag 0x2000) never tick. An **always-active
list** [0x4c810] (max 25) is ticked first, without deduplication.

## Perception

FUN_0013c4da: hard cap **2000 units** (`< 0x7d1`), a 5-sample terrain
line of sight, 4 cells of object test. The damage routine FUN_00139819
sets an **alert bit 0x8000** at record+0xc on the root and every child,
consumed after one tick: a hit gives one free "I see you" tick.

## AIS script VM

AIS is a script VM, not a set of hard-coded behaviours. FUN_0012a500 runs
16-byte ops `{fn, a, b, c}`, once per rendered frame:

| Op (handler) | Meaning |
|---|---|
| 0x12a572 | IF `pred(a) cmp b` goto c (compare variants 0x586 <, 0x59a >, 0x5ae == 0, 0x5c1 ≠ 0) |
| 0x12a5d4 | GOTO |
| 0x12a5d9 | WAIT a/70 s (c: freeze animation) |
| 0x12a60c | start ANIM block |
| 0x12a640 | SOUND |
| 0x12a668 | SET var[a] = b |
| 0x12a682 | SET var[a] = fn(b) |
| 0x12a69e | ADD |

IF, GOTO and SOUND continue in the same tick; the others end it.

Predicates: 0x6b8 **see = 1 when the player is NOT perceived**, 0x6e9
distance, 0x6fd bearing 0..2047, 0x75a |bearing|, 0x7ab random 0..1023,
0x789 angle to player, 0x7c4 animation flags & arg, 0x7d0 actor flags &
arg (0x1000 = movement blocked).

**Animation block**: `{u32 1, u32 n, u32 fps, u16 flags, (u16 frame,
u16 frame_flags)×n}`, last entry flag & 1. Flags: 4 one-shot, 8 loop,
**0x100 firing pose** (walkers fire only during it). Block type 5 = engine
loop sound.

## Parameter blocks (36 dwords)

Base = instance for state 7, instance + 0xc for 2/6/8.

| Family | Fields |
|---|---|
| Walker | dw4 speed u/s (T-800 140), dw6 stop distance, dw7 turn (2048 = 360°/s), dw11-13 muzzle, dw14 ammo type, dw15 bolt speed, dw18 rate, dw19 range, dw20 root-motion table, dw26 script, dw29 frame events `{frame, sound, …, −1}` |
| Turret | dw4 axis (0 pitch, 1 yaw), dw5/6 limits, dw7 turn, dw8 engage range, dw12-14 muzzle, dw15 ammo, dw17 aim cosine, dw19 rate, dw20 range |
| Hover (state 6) | dw4/6/7, dw12-20 fire, dw21 script |
| Flyer (state 9) | p4 max turn, p5 turn accel, p6 min altitude + 100, p7 vertical accel, p8 obstacle radius, p9 forward speed (400/500), p11 = var 56 target speed (50..800, −250 = back off), p12 vertical speed; script var 56; dw17-25 fire, dw26-27 engine |
| Tank (state 13) | dw12 script (var 48 speed, var 44 heading), dw22-23 engine |

## Turrets (state 2)

FUN_00137965, once per frame. Target = the player when within dw8 and
perceived; otherwise a **rest pose** at (x, y − 200, z − 300) — it keeps
turning there, never sweeps. Aim step (FUN_00137a97 + 0x13c001) is
rate-limited by dw7 and clamps (no overshoot). With the perception
flicker over hills and the alert bit, a hit turret snaps toward the
shooter and swings back to rest. Negative bolt speed = alternate muzzle X
(twin barrels).

## Flyers (state 9)

Constant forward flight steered at the player: strafing overflights.
Sinks toward the player's eye at 250 u/s, climbs at 300 u/s, never below
p6 − 100 over the ground.

## Fire

Fire routine 0x137510 (v1.00): in range, aim within the cone (0x8c of
2048 = 24.6°, or the per-type cosine, e.g. 46340 = 45°, relaxed to 45°
inside 140 units), then a random roll `rand & 1023 < (rate × difficulty
[0x5c500]) >> 16`. The shot is the ammo type's projectile
([weapons-ammo.md](weapons-ammo.md)).

## Death

EnemyKill (v1.01 0x129f30) is instant. A mine or trap is an ObjHit on
itself and dies the same way. In order:

1. It recurses into the actor's child segments first (0x129f5b..0x129f99),
   so every segment dies by its own type's rules.
2. It sets the dead bit (+0x0c |= 0x80) and drops the actor from the
   target list (0x1299fd).
3. It reads the **death spec** (table +0x10, copied to actor +0x14):

| spec | parts (0x12a059) | own explosion (0x124793) |
|------|------------------|--------------------------|
| 0 | none | **none** (`je 0x129fff`) |
| 1 | none | yes |
| → `{0, type}` (kind 0) | one part of `type` at the zero offset 0x1ca14 | **none** (`call 0x12a059; jmp 0x129fff`) |
| → `{1, → list}` (kind 1) | one per 32-byte `{continue, type, ox, oy, oz}` entry, up to `continue = 0` | yes, after the parts |
| → any other kind | none | none |

   The explosion is also skipped while the flag at 0x4fdc1 is 1. That flag
   is a silent teardown (level unload, actor purge), not a gameplay kill.
4. 0x124793 spawns the explosion effect (0x12420f), plays one of
   sounds 33-36 (`0x21 + rand & 3`, 0x12f018) and throws the death blast:
   S = 75 after 0.375 s ([damage-hud.md](damage-hud.md#radial-blast)).

In the v1.01 table:

- Every turret root and turret segment (state 2) is kind 0, and its part is
  the segment's own model (types 95-99, 104-108, 146, 156-159). A turret
  kill therefore has no explosion: its head and gun fly off, and each one
  goes off where it lands.
- Spec 1 (the explosion alone) covers types 1-2, 43-44, 46-48, 50, 53, 86-87
  and every part type 88-170. All other robot types are kind 1.
- Type 77 (a state-8 head) is kind 1 with a single entry at the origin. The
  kind cannot be read off the parts, so the table ships it
  (`EnemyType.DeathKind`).
- Multi-segment robots explode once per segment with spec 1 or kind 1. For
  example, type 40 explodes along with its child 77, and types 43/44 along
  with their children 84/85.
- No type has spec 0 or a kind above 1. A kind-0 spec always names its part,
  so a kind-0 death always throws a part.

**Parts (0x12a059).** Each part is built as a state-1 actor:

- Start: the segment's position plus the entry offset turned by the
  segment's angles, pushed out by the part model's radius + 10.
- Direction: `normalize(horizontal unit from the robot root to that point
  + 2.0..4.0 up)`. Every part leaves at least 63° above the horizon.
- Speed: 120 + (`rand & 0x7f`) u/s.
- Angles: 0. Each axis spins ±(1024 + `rand & 0x3ff`)/2048 rev/s, with
  the parameter block at 0x49e73 (+0x10..+0x18).

The state-1 handler (0x1377fe) adds gravity 392 u/s², moves the part and
spins it. The part ObjHits itself (HP 10) when it touches the world
(0x123e36) or ends up 2000 u from where it was thrown (0x13790f). That is
its own EnemyKill. Every part type is spec 1, so that kill is an explosion
plus a blast 75. Parts never bounce or come to rest.

## Path vehicles (state 11)

v1.01 handler **0x127400**. Types 46 box truck (HP 800), 47 tank truck
(800), 48 sergeant car (400), 49 heavy tank (700), 50 transport (0),
51 HK fighter (0 = indestructible), 52 boss chassis (10000). Parameters
(table params 4/5/6): k = 80, accel = 160, turn = 128.

- First tick: target = the actor's path (marker link). Segment speed
  `tspd = (dist3d × 80 + 128) >> 8` ≈ 0.3125 × segment length u/s; speed
  starts at 0.
- If the target marker's bit 0 is clear: tspd 0, clear bit 0 on the whole
  path, brake at accel.
- Within **80 units (3D)** of the target: next = marker sub+6. If that is a
  variant-3 marker with bit 0 → new target (marker type 105 loops back to
  the path start). Otherwise ObjFlipLink(next, xor 1) **once**, cut the
  link (sub+6 = 0), stop.
- Motion: straight 3D line, marker Y, no terrain follow, no collision;
  speed += clamp(sign × 160 × dt). Yaw only, `−atan2(dx, dz)`, at most
  128/2048 rev/s (22.5°/s), visual. Spawn at marker Y + 16, yaw 0. No
  shooting.
- **Always-active convoy**: marker type byte ≥ 0x80 → subtract 0x80, set
  inst+0xc |= 0x4000; next tick (0x129d71) moves it to the always-active
  list. Inside the window such an actor ticks **twice per frame**. Only
  MAP.260's 9 convoy markers use it.

Shipped uses: MAP.210 cargo truck (started by a lever chain), MAP.234 HK
pickup (path authored on), MAP.260 convoy, MAP.280/281 boss loop.

## Godot port

- `enemy.gd` (actor, families, turret aim, fire, death, dormant traps,
  `PERCEPTION_RANGE` 2000, `AIM_CONE`, flyer limits), `enemy_ai.gd` (AIS
  VM, animation stepper, frame events), `enemy_ai_data.gd` (generated by
  `tools/gen_enemy_ai.py` from the executable), `enemy_anim.gd`,
  `debris.gd`.
- `level_loader.gd`: `ENEMY_MESH` (100 types), `ENEMY_SEGMENTS`,
  `_attach_segments()`.
- `scripts/level/path_vehicle.gd` + `RulesSkynet.PATH_*`.

## Deviations in the Godot port

- Scripts evaluate at a fixed 12 Hz (`enemy_ai.gd` `SCRIPT_HZ`) rather
  than once per rendered frame, so random rolls (e.g. the T-800's 10 %
  gesture check) happen at a different rate than in DOS.
- Enemy fire rate uses `FIRE_RATE_DIV` 128 and bolt speed
  `BOLT_SPEED_SCALE` 5 — calibration, not the DOS roll above.
- The turret rest pose and the alert bit are modelled (`enemy.gd`
  `_alert`); an earlier version froze out-of-range turrets instead.
- Ground actors refuse to drive into water and have per-family slope and
  step limits (port additions).
- Types without table data fall back to a heuristic idle/chase/attack FSM.
- The verifier can switch enemy fire off.

## C# port

- `Enemies/Robots.cs`: `Destroyed` throws the parts, then runs the own
  explosion for every segment where `Robots.ExplodesOnDeath` (the table
  above) is true. The root explodes at its body centre (EXPLO1). A child
  explodes at its segment position with sounds 33-36.
- `Enemies/Wreckage.cs`: the part pool. It holds 48 parts and drops a throw
  once full. An 8 s guard covers a part that cannot move.
- The kind comes from `EnemyType.DeathKind` in `enemy_types.json`. An older
  table without it reads a single part at the origin as kind 0.

## Improved robot AI (port option)

ROBOT AI: CLASSIC (DOS, above) or IMPROVED (default in the settings, key
`robotAi`; menu OPTIONS → DETAIL → GAMEPLAY, applied at once; console
`robotai`, `--robot-ai`; `Robots.Ai`, `GameSession.RobotAi`). A demo keeps it
in its header (`robot ai`; absent: CLASSIC) and as `!robotai` events. Deathmatch
has no robots. `GameSession` and `Robots` default to CLASSIC, so the parity
goldens, demos and verifier stay DOS's.

IMPROVED keeps DOS's states, scripts, attacks, weapons and damage; it changes
movement and when robots fire (`Enemies/Enemy.Improved.cs`):

- Causes on MAP.210 (CLASSIC): a flyer tests one ray from its middle and moves
  vertically without any test, so the HK's wings and belly pass through the
  street's scaffolding (≈18 % of its ticks in a 20 s chase); a walker blocked
  while it sees the soldier stands pressing into the obstacle for good; within
  `near` of him it walks in place; unseen it wanders to random spots, often
  into walls.
- Flyers: a hull of spheres (the body's bounds, turned with the yaw) is pushed
  out of the level and moved by a sweep with sliding, so it never enters
  geometry; a look-ahead sweep (0.8 s of flight) turns or climbs round what is
  ahead.
- Walkers and ground robots: probes ahead (middle, both shoulders, knee
  height), then follow the obstacle on one side until the goal's way is clear
  ("bug" walk); 1 s trying with no progress (3 s while turning) → back off;
  robots keep apart.
- Not idle: the last sighting is kept 20 s (go there, then search round the
  soldier); unseen otherwise, wander toward a clear heading; within `near` of
  the soldier, side-step round him.
- Fire: a clear line from the eye and from the muzzle, turrets included.
- Activation: DOS's cells, or within `TickWindow.ImprovedScale` (1.25) × 2.5
  cells (3200 u) on each axis.

## Open questions

- Enemy table v1.01 base: 0x44E00 per one decode vs 0x44d00 in older
  (v1.00) notes. Confirm before reading.
- Mesh resolution of types 0x36..0x39.
- Contact damage per type (record +3).
