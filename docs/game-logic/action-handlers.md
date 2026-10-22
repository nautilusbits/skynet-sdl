# Action handlers

Every MAP entity carries three bytes that decide what it does: an
**action id** (act), a **state byte** and a **link** to the next entity of
a chain (see [../formats/map.md](../formats/map.md)). The act indexes the
handler table; the state byte holds the enable bit and the activation
modes; links form chains that one activation walks.

## Dispatch

- Handler table at **0x59b00** (v1.00; **0x59e00** in v1.01, file = VA +
  0x53AA4; 8 bytes per
  slot: handler pointer (+0x30000) and a config dword, low u16 = **p4** at
  +4, high u16 = **p6** at +6). Ids 1..0xFD; ≈150 populated, ≈30 unique
  bodies. Ghidra misses them (table-reached code): create functions by hand.
- `ObjDoAction` FUN_00139698 (v1.00) reads the id per variant (variant 1:
  sub+0x12/+0x13; variant 3: sub+5/+10; variant 2: sub+2/+3) and runs the
  handler **every frame bit 0 of the state is set** (the dispatcher
  FUN_00139e98 skips records without bit 0). An act of 0 returns at once.
- `ObjFlipLink` FUN_001394aa (v1.00) walks next-in-chain and **toggles**
  bit 0 of each link (XOR 1). Several triggers aimed at one chain in one
  frame therefore cancel when applied as a batch; DOS runs each object's
  handler as its link is flipped.
- `0x139644(entity, −2, 0)` (not decompiled) clears the caller's bit /
  cuts the link: the one-shot ending of F1/F2, 0x2C and 0xF3.
- A handler that has fired may write 0xFF into its own act byte ("retire";
  hints and objectives). 0xFE/0xFF are empty slots.

### State byte

| Bit | Meaning |
|---|---|
| 0 (0x01) | enabled / armed; handlers run while set |
| 1 (0x02) | ObjHit: run the action on **every** hit (wrecks keep staging after HP is gone) |
| 2 (0x04) | ObjHit: run the action once when HP < 1 ("act on death") |
| 4 (0x10) | walk-on pad (below) |

HP drains regardless of bits 1/2. Entities without their own link record
take HP/state/link from the map's per-name default list at 0x233C.

## Table

Addresses: v1.01 where a v1.01 read exists, else marked (v1.00).

| Act | Kind | Handler | Rule |
|---|---|---|---|
| 0x00 | none | — | plain prop, or a relay a chain passes through |
| 0x01 | light toggle | 0x137700 (v1.00) | XOR 0x8000 on the variant-2 enable word (sub+8), clear own bit 0 |
| 0x02 | light flicker | 0x137713 (v1.00) | ≈50 % random toggle per tick while bit 0 stays set |
| 0x03 | light strobe | 0x13773b (v1.00) | toggle every tick while bit 0 set |
| 0x0A, 0x0C | inert | — | handler is `ret` |
| 0x0D-0x0F | light fade up | 0x137772 (v1.00) | by (id − 12)/4 of the intensity, clear bit 0 |
| 0x10-0x12 | light fade down | 0x137747 (v1.00) | same, downwards |
| 0x14, 0x17, 0x2D | empty | — | no handler |
| 0x18 | destructible (ramp) | 0x120833 | p4 = 4: adds `p4 << 4 × dt` → ≈4 stages/s while enabled, clears no bit. 15 records in the shipped maps, all unreachable |
| 0x19 | destructible (stage) | 0x120833 | p4 = 0: clears own bit, adds 16 → **one stage per enable** |
| 0x1A | UNCERTAIN | 0x137902 (v1.00) | variant 3 only; calls 0x146600 with sub+2. Future Shock uses it (pickup shrink + delete in shock.exe) |
| 0x1B | demolish | 0x1380bf | variant 1 only: `hp = max(hp, 1); ObjHit(entity, hp + 1)` — killed through the normal destruction path. 243 entities in 56 maps |
| 0x1C-0x25 | hint [G1]-[G10] | 0x13779d (v1.00) | prints the hint line, no counter, retires |
| 0x26-0x2A | objective [M1]-[M5] | 0x1377d0 (v1.00) | counter − 1, prints the [M] line, sound 0x51, retires. [objectives.md](objectives.md) |
| 0x2B | fail | 0x13782d (v1.00) | mission failed at once |
| 0x2C | countdown relay | 0x138038 | when the objective counter is > 0 and equals p4 (1): ObjFlipLink + one-shot |
| 0x30-0x35 | mover jump | 0x137ad0 (v1.00) | instant translate along axis p4 by signed p6 |
| 0x36-0x38 | mover half-turn | 0x137a28 family | turn 1024 (180°) about axis 0/1/2, stop, reverse next time (wall monitors) |
| 0x39-0x3E | continuous rotator | | never stops (fans, radar dishes) |
| 0x3F-0x7E | mover slide | 0x137a28 (v1.00) | along DOS axis p4 by p6 units at 70 u/s (140 when p6 ≥ 0x800); act parity flips at the limit → next trigger reverses |
| 0x5F | slide | 0x137d41 (v1.00) | p4 = 316 speed base, travel p6 << 4 |
| 0x8D-0x92, 0xA7-0xAC, 0xC1-0xC6 | mover swing | 0x137c6b (v1.00) | rotate about axis p4 by p6 (11-bit) at 512/s (90°/s) |
| 0xA5, 0xA6 | slide (lift) | 0x138287 | axis 1, 688 units (MAP.233 elevator) |
| 0xBD-0xC0 | zero slide | 0x137b33 / 0x137bce (v1.00) | diagonal slides whose limit word is 0: flip parity, clear bit, never move |
| 0xD6-0xDA | water level | 0x121160 | [water.md](water.md#moving-water) |
| 0xDB-0xEB | one-shot sound | 0x137dbd (v1.00) | plays sound id p4 (0xDB = 40 … 0xE6 = 109 … 0xEB = 114), clears bit |
| 0xEC | UNCERTAIN | 0x137dbd? | listed in the family, no id read |
| 0xED | voice line | 0x137dfd (v1.00) | VOICE.PRS id at sub+2 |
| 0xEE | ambient loop | 0x12a47a (v1.00) | sound id sub+2, handle sub+4; sprites get it from table 0x4cc00 (FUN_0012a400) |
| 0xEF | use-key gate | **0x1386a0** | below |
| 0xF0 | map exit | **0x138081** (v1.00 0x137881) | below and [../missions.md](../missions.md) |
| 0xF1 / 0xF2 | proximity chain | **0x138223** | radius p4 = 256 / 1024 |
| 0xF3 | spawn reveal | 0x129142 (v1.00) | reveals a hidden robot built at this sprite; lookup 0x1290eb untraced. Mission 8 only (MAP.280/281) |
| 0xFD | pickup | 0x11d670 | [pickups.md](pickups.md) |

### Mover speeds

The handlers step a <<4 fixed-point accumulator per tick. The units per
second above are a reading of the tick rate (the "family" and "travel" are
DOS truth; the speed is calibration). Continuous rotators run at ≈153
11-bit units/s in the Godot port.

## Gates and proximity

**0xEF** (v1.01 0x1386a0):

- Entry test: runs only when `state & 6` is 0 or 6 (`and edx,6; je run;
  cmp edx,6; jne ret`). A prop with bit 2 alone is not a gate.
- It never tests bit 0: a gate is **always live**; ObjFlipLink forces a
  0xEF link back on.
- Fires in the frame the **use key** goes down ([0x32c7f] == 1, written by
  the input poll FUN_0011ad8a; forced to 1 when a vehicle is destroyed,
  0x131162), when the player's **eye** is within **60 units, true 3D**
  (distance routine 0x14d775, globals [0xd49b4/b8/bc]).
- Then ObjFlipLink(1, 0xff, 0) on its chain. A network branch tests
  [0x30a50] & 0x40000.
- **Wall switches** (0x13870b, the handler's tail after the flip): a
  variant-1 record whose state has **bit 3** gets `sub+4 (yaw) += 0x400,
  & 0x7FF` — a half turn on every press, then `state |= 1`. The switch
  models (BUTTON01-04, 232BUTTO, 232KYPD1, BTNA/B/C ...) are one quad with
  the OFF art on the front face and the ON art on the back (BUTTON01
  222/0 → 222/1 lit, BUTTON02 222/2 → 222/3 pressed in, BUTTON03 222/4 →
  222/5 lever up, BUTTON04 222/7 → 222/8 wheel turned), so the turn is the
  whole visual; the next press turns it back. Only this handler turns: a
  walk-on pad (FUN_00139d5e) or a chain reaching the gate does not.
  Port: `BehaviourModel.IsSwitch`, `LevelBehaviour.TurnSwitch`.

**No one-shot guard on a gate's chain.** ObjFlipLink (v1.01 0x139caa)
XORs bit 0 of every link and does not stop at a retired (0xFF) act; a
slide (0x138287) clears its bit and swaps its act to the other parity at
the limit (0x70 ↔ 0x6F); a voice (0x13865c) only clears its own bit; only
objectives and hints retire. So a second press of MAP.215's silo switch
(BUTTON01 @02f8f) after the rise sends HADES01/02 back down, moves DORB
back and says the line again, counting nothing; a press during the rise
stops it. The port does the same (`SwitchTests`).

**0xF1/0xF2** (0x138223): no state check; fire when the eye is within
256 / 1024 units (3D), ObjFlipLink, then the one-shot clear. A chain that
re-arms one fires it again at once, even with the player still inside.

**Walk-on pads**: the foot mover passes the owner record of the floor
polygon under the feet (offset ≥ 0x253c) to FUN_00139d5e (at
0x12bd54..0x12bd70). If `state & 0x10`: clear 0x10, ObjFlipLink(owner,
xor 1). No key, no distance, once. The 20 pads in 8 shipped maps are all
0xEF gates on variant-1 meshes, which remain ordinary use-key gates
afterwards.

**0xF0** (0x138081): target map = u16 sub+2 (0 → the previous map
[0x38b18]), marker set = u8 sub+4. Writes the pending-map register
[0x38b14] and set register [0x38b1c], sets [0x30a50] |= 0x20 and clears
its own bit 0 inline (it does **not** retire the act). The frame loop tests
0x20 at 0x117a2e before the next sweep and tears the level down, so one
exit fires per level instance. Touching the doorway sprite also arms it
(UNCERTAIN radius; the port uses 90). Exits are usually reached as
`0xEF gate → 0xDB-0xEB sound → 0xF0`.

## Destructibles

- `ObjHit` order: bit 1 runs the act on every hit, bit 2 once when HP < 1.
- 0x18/0x19 first look the mesh up in **TRANSFRM.PRS** (0x12078a; carry
  set → exit): a mesh without a template does nothing. Many placed cars are
  another car's later stage and have no template of their own.
- Stage = counter >> 4. The handler receives **no damage value**: one
  stage per qualifying hit, never `damage / 16`.
- A chain that sets the bit of a 0x19 entity advances it one stage (the
  MAP.248 girder must ram the wall several times).
- Destruction FUN_00124293 / FUN_00123fbd: table 0x424d6 (v1.00 0x423d6; 14 bytes: four
  effect sprites from banks 356-366/220, spread, drop type, sound; −2 =
  random 33..36); drop lists 0x42c7e (v1.00 0x42b7e; type 3 ammo, 4 medkits/armour). Type
  0 = single blast from bank 358, no drop. DOS does not remove the mesh of
  a type-0 destruction.
- Crates (CRATE01-03, CRATB/C/D) are HP 50, state 0, act 0, not in
  TRANSFRM.PRS: a plain prop that vanishes at HP ≤ 0 (one pipe blow).

## Path vehicles

Chains that reach a vehicle's path markers start it; see
[enemies-ai.md](enemies-ai.md#path-vehicles-state-11).

## Godot port

- `scripts/triggers/rules_skynet.gd` — one read-only row per act (kind,
  family, p4/p6, fire edge/level, on_fire, modes with measuring rule,
  DOS address, provenance). Shared by the game, the trigger graph and the
  verifier. `rules_shock.gd` is the Future Shock copy.
- `scripts/triggers/trigger_runtime.gd` — the only writer of live state,
  act and link bytes per zone.
- `scripts/level/behaviour.gd` — presentation and the proximity sweep;
  node classes in `scripts/level/`: `trigger.gd` (0xEF/0xF1/0xF2),
  `map_exit.gd` (0xF0), `mover.gd`, `destructible.gd`, `damageable.gd`,
  `objective.gd`, `message_cue.gd`, `sound_cue.gd`, `sound_loop.gd`,
  `voice_cue.gd`, `raw_action.gd` (lights, water, relays, spawns),
  `path_vehicle.gd`.
- `scripts/triggers/trigger_graph.gd` — static chain simulation per map,
  written to `MAP.NNN.triggers.json` and pinned by
  `tests/rules/skynet.triggers.lock`.

## Deviations in the Godot port

- **Wall buttons** (a deliberate port rule) answer the use key by the
  crosshair ray (600 units, `USE_RAY`) instead of proximity; MAP.210's base
  doors are opened by a panel ≈300 units up a tower wall.
- Proximity radii get a 26-unit pad for the capsule (`PLAYER_RADIUS`).
- The port's own 2D tests use a ±512 vertical window (`PROX_VERTICAL_WINDOW`).
- 0xF1/0xF2 carry a port leave/re-enter latch in addition to the DOS
  re-arm.
- Several flips in one tick: the port remembers a node that went up at any
  point in the tick (`_armed`), so a ring of gates cannot cancel an
  objective. A deliberate port rule; DOS order is per handler.
- `on_fire` is taken from the **authored** act byte, not the live one
  (reading the live byte breaks MAP.217's jeep ring).
- 0x18 ramp not modelled (unreachable in shipped data).
- 0x1A, 0xEC, 0xF3 lookup: undecoded.

## Open questions

- 0x139644 (one-shot clear) and 0x1290eb (0xF3 target) are not decoded.
- 0x1A's call 0x146600.
- Door/mover sounds beyond the 0xDB-0xEB chain nodes; locks (probably none).
- Exact touch radius of a 0xF0 sprite.
