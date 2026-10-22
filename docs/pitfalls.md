# Pitfalls

Traps found while porting. Each one cost real debugging time. Newest
findings are added at the end of their section.

## Addresses and the executable

- **The engine is SKYNET.EXE; GAME.EXE is not an original name.** The
  DOS program is `SKYNET.EXE` ("SkyNet v1.01 - 11/19/96", DOS/4G LE; a
  v1.00 also exists). An unofficial DOSBox patch renames it to `GAME.EXE`
  and puts its own small QuickBASIC launcher in its place as `SKYNET.EXE`,
  which writes a batch file running `GAME.EXE /g`. So a `SKYNET.EXE` of
  about 49 KB is that launcher, not the game; the real one is about
  1.8 MB. `/g` selects the 640 × 480 mode.
- **v1.00 vs v1.01 addresses.** Some findings come from a v1.00 decompile
  (10/31/96). The shift to v1.01 is piecewise, not one constant. Known
  anchors: code around 0x129000 moved +0x500, around 0x139000 +0x800,
  data +0x300. Example: the demolish handler (action 0x1B) is 0x1378bf in
  v1.00 and **0x1380bf** in v1.01. Re-anchor by string references before
  using an old address.
- **Data pointers are stored relative to 0x30000.** Pointer-sized values
  stored as data (table entries, struct fields) must have 0x30000 added
  to give the real VA. Call and jump targets in instructions are already
  absolute. Forgetting this makes every pointer table look like garbage.
- **VA to file offset (v1.01).** LE object 2 (VA 0x30000, code + data):
  `file = VA + 0x53AA4`. The LE header is at file 0x37F34 behind an inner
  MZ at 0x352A4, and the LE data-pages field is relative to that inner MZ.
  LE tools that ignore the inner MZ read zeros. (v1.00 used `+0x538A4`.)
- **Decompiler gaps.** Ghidra does not find code reached only through
  tables (action handlers, AIS handlers, ammo callbacks). Create functions
  at the table entries explicitly, or disassemble those ranges by hand.
- **Frame-rate constants.** Many movement figures are multiplied by a
  value holding `65536 / fps`, so they are per-second rates, not
  per-frame. Some effects are genuinely per frame in DOS (radiation dose
  charged at least 1 HP per frame); decide case by case.

## MAP files

- **The name table has no count and is not NUL-padded.** Read exactly
  `max(name_index) + 1` slots after walking the entities. A "looks like a
  name" heuristic stops early on a short name with leftover bytes and
  silently drops every later mesh.
- **Blocks are 48 / 35 / 36 bytes by variant.** A reader that requires
  48 bytes before the end of file drops the last block, which can be the
  head of a whole cell chain.
- **Flag 0x08 means skip.** Honour it.
- **Per-name defaults.** Entities without their own link record take hit
  points, state and actions from the 0x233C list. Missing this leaves
  crates, cars and buttons inert; writing the defaults back into the
  entity corrupts the file.
- **Enemies are variant-3 markers (type 2), not variant-1 actors.**
- **Enemy type bit 7 is the convoy flag.** Mask it (`& 0x7F`), or convoy
  vehicles index past every table and vanish.
- **Enemy markers keep other data where sprites keep their action byte.**
  Read as actions they look like objectives and failures. Exclude markers
  when collecting objectives.
- **Marker type 4 is a radiation source, not an extraction zone.**
- **Variant 2 is a light, not a spawner.**
- **Every variant-1 entity is rotated** (pitch/yaw/roll, order
  `Rz · Rx · Ry`). Earlier readings that statics are unrotated, or that
  the three values are offsets, were wrong.
- **The outdoor flag is at +0x2344 (9028).** One older note says 0x2334;
  the code and every map agree on 0x2344.

## Terrain (WLD)

- **WLD files are chunked** (2 × 2 chunks of 128 × 128, offset table at
  0x90, 22-byte chunk headers), not four flat 64 KB layers.
- **Layer-0 bit 7 is not a diagonal selector.** Every cell splits NW–SE;
  bit 7 is a "non-planar" flag the engine sets at load.
- **Layer-0 value 0 is valid ground** (height 0), not an empty cell.
- **Two grids.** MAP cells are 1024 units, WLD cells 256 units; WLD rows
  run north to south (row 0 = largest Z).
- **Future Shock has outdoor maps without a WLD** (MAP.040, MAP.080).
  DOS keeps the previous heightmap. A port must pick a substitute (the
  Godot port scores each WLD by how many ground markers stand on it) or
  the player falls out of the world.
- **Lakes are terrain tiles**, not water markers.

## Textures

- **Row stride is 256, not the width.** Wall textures store rows at
  `width + row_gap` (64 + 192); four 64-wide records share one buffer.
- **256-wide walls have row gap 0** and look like sprites; test whether
  the sprite frame table makes sense.
- **Sprites use a transparency-run encoding**, not the CFA codec.
- **One bad record must not truncate the bank.** Keep a placeholder so
  later record indices stay correct.
- **Flat colours (TEXTURE.000/001)**: the palette index is the record's
  own byte +1. Reading one record further shifts every colour by one.

## Models (.3D)

- **Culling winding.** Visible side = counter-clockwise; Godot needs
  reversed fans with back-face culling. Drawing both sides makes the
  stored two-sided duplicates z-fight. Keep collision two-sided.
- **Do not cap the face vertex count** at 8, and do not stop at a bad
  face; advance by the real stride.
- **v2.1-v2.5 vertex references are index × 4**, v2.6/v2.7 byte offsets.
- **Header 0x30 is not always frame 0.** Prefer the frame table.
- **No Daggerfall UV wrap.**
- **Vertex scale is 1/256.** An older note said 1/64; that belonged to a
  different cell size convention.

## Audio

- **HMI ticks at the header's timer rate (120 Hz)**, not PPQN × tempo.
- **Note-ons carry durations**; there are no note-offs.
- **The game plays the loose T*.HMI files**, not the XMI files in
  MDMDMUSC.BSA.

## Text

- **PRS: first definition wins.**
- **Blank [M] entries count.** Every `####`-terminated entry in
  `[M1]`..`[M5]` adds one to the objective counter, blank or not, and each
  objective action shows the next entry of its section. Counting sections
  (or dropping blanks) ends missions early or never.

## Rules and logic

- **Chain flips toggle.** Several triggers aimed at one chain can fire in
  the same frame and cancel each other if applied as toggles in a batch.
  DOS runs each object's handler as the chain is flipped.
- **0xEF gates pre-latch around the spawn point.** A gate within its
  radius of where the player starts never trips; place test spawns
  outside it.
- **0xF1 / 0xF2 fire on proximity** (radius 256 / 1024), not on the use
  key.
- **Difficulty changes only four multipliers** (enemy rate of fire,
  damage to enemies, damage to the player, armour regeneration), never
  the number of enemies or pickups.
- **The dusk sky dome is not global.** Only maps 250, 260, 270, 280 draw
  it; every other outdoor map is night sky with a moon sprite.
- **RENDER DETAIL values differ between builds.** Use the v1.01 table at
  0x61a00, not the v1.00 one at 0x61800.

## Collision

- **Sealed doors.** DOS collides players as a cylinder against simple
  object shapes; exact triangle-mesh collision seals passages DOS lets
  through. Seen: a flat door leaf with action 0x00 that sealed a truck
  entrance; a cabin whose exits are narrower than the player capsule;
  small props given boxes that then sealed a corridor. The fix is to model
  DOS collision (cylinder, floor re-find within ±80 units, no ceiling
  test), not to patch individual objects.
- **The DOS player ignores ceilings** and re-finds the floor within ±80
  units, so doorways as small as 70 × 105 units exist. Use a step-up of
  80.
- **Future Shock interiors place start markers above the floor** (up to
  ~60 units). Drop the player to the floor below.
