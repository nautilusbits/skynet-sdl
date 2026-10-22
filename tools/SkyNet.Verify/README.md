# SkyNet.Verify

Headless checks of the level behaviour (`src/SkyNet.Game/Behaviour`) against
the Godot port's verification baseline. Two modes:

- `triggers` is the Godot `--verify-triggers=all`. It enumerates every
  trigger node of every map from the MAP records, as the Godot trigger graph
  does. It activates each node by its primary mode (use key, proximity,
  doorway touch + key, shot, death, walk-on pad, counter relay, path end),
  compares the effects with the first and second activation in the trigger
  lock, and writes one row per node.
- `missions` is the Godot `--verify-missions`. It plays the mission specs step
  by step and takes the exits. Nothing is reset between steps, and the mission
  has one objective counter.

There is no collision world here. The eye or the feet are put on the record
itself, and a node is set off through its own handler. Rows that the Godot
run calls UNREACHABLE for physics reasons (no floor in reach, no line of
fire) therefore come out PASS or FAIL here. Only "no mesh was built for it
to be shot" is reproduced.

## Run

```
dotnet build tools/SkyNet.Verify
dotnet tools/SkyNet.Verify/bin/Debug/net10.0/SkyNet.Verify.dll triggers --rules=<godot>/tests/rules [--maps=210,215] [--out=rows.txt] [--expect=<godot rows>]
dotnet tools/SkyNet.Verify/bin/Debug/net10.0/SkyNet.Verify.dll missions --rules=<godot>/tests/rules [--only=210,252] [--out=steps.txt] [--expect=<godot rows>]
```

- `--data=DIR` sets the game data folder. The default is `SKYNET_DATA`.
- `--rules=DIR` is the Godot port's `tests/rules`. The tool reads
  `skynet.triggers.lock`, `skynet.xfail` and `skynet.missions.txt` from it.
- `--out=FILE` writes the rows. A trigger row is `map id act kind mode result
  why`, a mission row `mission n verb map id result tag why`. Both use the
  Godot formats, so `diff` works line by line.
- `--expect=FILE` compares the run with a rows file. That can be a Godot run's
  `--verify-out` (the gate's `triggers.rows` / `missions.map.rows`) or an
  expected file of `<key> <key> <result>` lines. The differences are grouped
  by the pair of results. `--all` lists every row instead of the first six.

The exit code is 1 when a FAIL is new: not in `skynet.xfail`, or not FAIL
in the `--expect` file.

The whole trigger run takes a few seconds and the mission run under one.
`tests/SkyNet.Game.Tests/Behaviour/VerifierTests.cs` runs both on a subset
(seven maps and every mission). It needs `SKYNET_DATA`, and it fails only on
a FAIL that `Behaviour/Verify/*.expected` does not list.
