# Rocket eye (follow camera)

## Rules (SKYNET.EXE v1.01)

- Applies to ammo types whose flags (+0x28) carry **0x08** (follow
  camera): type 5, the rocket of the ROCKET LAUNCHER (record 6) and of the
  jeep and HK pods (records 22/23, 25).
- Starts when the **RUN key** (CONTROLS.DAT +0x28, input bit 0x1000000)
  is held at the moment the rocket leaves the barrel: spawn 0x123028 →
  0x11dc99, flag 0x400000 in [0x30a50].
- Every tick of its flight (0x123322) the camera looks along the rocket's
  velocity (0x1389bb: world-up basis, no roll) from **(0, −10, −120)** in
  that frame: 120 units behind and 10 above (DOS y is down).
- The soldier keeps his own look and position (0x11dd3e saves them,
  0x11dc35 restores them after the frame): he still walks and turns.
- Ends when RUN is released (0x123347 → 0x11dcec), or when **any**
  follow-flagged rocket leaves the list (hit or lifetime out, 0x1230a4 →
  0x11dcec).
- While active no weapon, reticle or cockpit is drawn (0x125d1a / 0x12695c
  skip on 0x400000); the HUD bar stays.
- The death view takes precedence (0x36200 checked first).
- Straight up or down there are no angles (0x138b55): the last orientation
  is kept.

The same "detached camera" mechanism (FUN_0011dc99, view mode [0x30408])
serves the scripted torpedo ride of mission 5
([../missions.md](../missions.md#scripted-transitions)).

## Godot port

`fly_camera.gd`: `ROCKET_EYE_BACK` 120, `ROCKET_EYE_UP` 10,
`rocket_view`, `_rocket_fired`, `_begin_rocket_view`, `end_rocket_view`,
`_step_rocket_view` (a top-level `Camera3D` copying the player camera's
lens and layers). `projectile.gd` draws the rocket model at 120 units for
that camera.

## Deviations in the Godot port

None known.

## Open questions

- Whether any other ammo type in the shipped table carries the flag.
