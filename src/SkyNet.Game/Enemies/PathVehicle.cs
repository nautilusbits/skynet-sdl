using System.Numerics;
using SkyNet.Formats;
using SkyNet.Game.Behaviour;

namespace SkyNet.Game.Enemies;

/// <summary>What a save keeps of one path vehicle (<see cref="PathVehicle.Snapshot"/>).</summary>
public readonly record struct PathVehicleSnapshot(int Id, bool Started, Vector3 Position, float Yaw, int Target, float Speed,
    float TargetSpeed, bool Finished, bool Dead);

/// <summary>
/// A machine that drives a path of markers: DOS AI state 11, SKYNET.EXE v1.01 handler 0x127400.
/// The cargo truck that drives into MAP.210's base, MAP.260's convoy, MAP.280's boss chase and the
/// HK that lifts the soldier off MAP.234's roof. Ported from the Godot port's
/// scripts/level/path_vehicle.gd, whose rules it follows.
/// <para>Each frame: pick the path up at the actor marker's own link at that segment's speed
/// (<see cref="SpeedK"/> × its length), drive a straight 3D line to the marker (no terrain, no
/// collision), turn the yaw toward it at <see cref="Turn"/> (visual only), and within
/// <see cref="Reach"/> take the next marker: a variant-3 marker whose bit 0 is up is the new target
/// (type 105 loops back to the head); anything else is the end of the path, which is flipped once
/// (ObjFlipLink), and the link to it is cut. A path whose marker bit is down brakes and clears the
/// bit down the whole chain, so a lever has to switch it on again.</para>
/// <para>The enable bits and links are the level behaviour's; this holds only where the machine is,
/// which marker it drives at and how fast it goes.</para>
/// </summary>
public sealed class PathVehicle
{
    /// <summary>Segment speed = k × its length (u/s): (len·80 + 128) >> 8.</summary>
    public const float SpeedK = 80f / 256f;
    /// <summary>Speed change, units/s².</summary>
    public const float Accel = 160f;
    /// <summary>Yaw turn rate: 128/2048 of a turn per second (22.5°/s).</summary>
    public const float Turn = 128f / 2048f * MathF.Tau;
    /// <summary>3D distance that counts as arrived.</summary>
    public const float Reach = 80f;
    /// <summary>DOS ticks actors by map-grid cell (0x12980f): the 5×5 cells (1024 u) round the player (<see cref="TickWindow"/>).</summary>
    public const float TickCell = TickWindow.Cell;
    public const int TickCells = TickWindow.Reach;
    public const int MaxHops = 64;
    /// <summary>Marker type 105: back to the head of the path.</summary>
    public const int MarkerPathLoop = 105;

    private readonly LevelBehaviour _b;

    internal PathVehicle(LevelBehaviour behaviour, MapEntity marker)
    {
        _b = behaviour;
        Id = marker.FileOffset;
        Head = marker.LinkNext;
        VehicleType = marker.EnemyType;
        Convoy = marker.Convoy;
        Position = new Vector3(marker.X, -(marker.Y + Robots.MarkerLift), -marker.Z);
        Spawn = Position;
    }

    /// <summary>The actor marker's file offset.</summary>
    public int Id { get; }
    /// <summary>The first marker of the path: the actor marker's own link.</summary>
    public int Head { get; }
    public int VehicleType { get; }
    /// <summary>MAP.260's convoy trucks (marker sub+0xa bit 7): once ticked in the window they are
    /// on the always-active list and tick wherever the player is, twice while he is near.</summary>
    public bool Convoy { get; }
    public bool Started { get; private set; }
    /// <summary>Where the machine was built: the actor marker 0x10 down (0x12a439), then stood by
    /// FUN_0012a52c (<see cref="StandAt"/>).</summary>
    public Vector3 Spawn { get; private set; }
    public Vector3 Position { get; private set; }
    /// <summary>Radians; 0 faces −Z (DOS +Z).</summary>
    public float Yaw { get; private set; }
    /// <summary>The marker being driven at (0: the path has not been picked up yet).</summary>
    public int Target { get; private set; }
    public float Speed { get; private set; }
    public float TargetSpeed { get; private set; }
    /// <summary>The path ran out on a record that is not a marker, which was fired.</summary>
    public bool Finished { get; private set; }
    /// <summary>The robot was destroyed: the machine no longer drives.</summary>
    public bool Dead { get; set; }

    private MapEntity? Record(int off) => off > 0 && _b.Map.TryGetEntityAt(off, out var e) ? e : null;

    private static Vector3 Pos(MapEntity e) => BehaviourModel.Position(e);

    /// <summary>
    /// The machine is a robot DOS builds through FUN_0012a4f0 like any other, so FUN_0012a52c stands it
    /// on the surface under its marker (<see cref="Robots.StandOnFloor"/>: hvytnk and tranenem 20,
    /// bosschss 76; the trucks, sgtcar00 and hk_ftr have no stand height). Handler 0x127400 then drives
    /// the origin straight at each path marker's own X/Y/Z. A machine that has not picked its path up
    /// yet moves to <paramref name="at"/>.
    /// </summary>
    public void StandAt(Vector3 at)
    {
        if (Target == 0 && Position == Spawn)
        {
            Position = at;
        }

        Spawn = at;
    }

    /// <summary>One frame of the engine's actor sweep with the player's feet at <paramref name="player"/>.</summary>
    public void Tick(float dt, Vector3 player)
    {
        if (Dead || Finished)
        {
            return;
        }

        var runs = TicksFrom(player);
        if (Convoy && InWindow(player))
        {
            Started = true;                         // 0x129d8f → the always-active list
        }

        for (var i = 0; i < runs && !Finished; i++)
        {
            Drive(dt);
        }
    }

    /// <summary>How many times the engine ticks this machine with the player at
    /// <paramref name="player"/>: the grid window, plus the always-active list for a started convoy actor.</summary>
    public int TicksFrom(Vector3 player) => (InWindow(player) ? 1 : 0) + (Convoy && Started ? 1 : 0);

    public bool InWindow(Vector3 player) => TickWindow.Contains(Position, player);

    /// <summary>Handler 0x127400, one run of it.</summary>
    private void Drive(float dt)
    {
        var cur = Record(Target);
        if (cur == null)
        {
            // Pick the path up at its head, without looking at its bit.
            cur = Record(Head);
            if (cur == null)
            {
                return;
            }

            Target = cur.FileOffset;
            Speed = 0f;
            TargetSpeed = SpeedK * Vector3.Distance(Position, Pos(cur));
        }
        else if (!_b.Enabled(cur.FileOffset))
        {
            TargetSpeed = 0f;
            StopPath();
        }
        else if (Vector3.Distance(Position, Pos(cur)) <= Reach)
        {
            var link = _b.Link(cur.FileOffset);
            var next = link > 0 ? Record(link) : null;
            if (next == null)
            {
                TargetSpeed = 0f;
                StopPath();
            }
            else if (next.Variant == MapEntityVariant.Sprite && next.MarkerType >= 0 && _b.Enabled(next.FileOffset))
            {
                if (next.MarkerType == MarkerPathLoop)
                {
                    next = Record(Head);
                }

                if (next != null)
                {
                    TargetSpeed = SpeedK * Vector3.Distance(Pos(cur), Pos(next));
                    Target = next.FileOffset;
                    cur = next;
                }
            }
            else
            {
                // The end of the path: fire it once, cut the link, coast to a stop.
                _b.PathEnded(Id, cur.FileOffset, next.FileOffset);
                Finished = true;
                TargetSpeed = 0f;
                return;
            }
        }

        var to = Pos(cur) - Position;
        var len = to.Length();
        if (len > 0.001f)
        {
            if (TargetSpeed > 0f)
            {
                var want = MathF.Atan2(-to.X, -to.Z);
                var turn = Wrap(want - Yaw);
                Yaw += Math.Clamp(turn, -Turn * dt, Turn * dt);
            }

            Position += to / len * (Speed * dt);
        }

        var dv = TargetSpeed - Speed;
        Speed += Math.Clamp(MathF.Sign(dv) * Accel * dt, -MathF.Abs(dv), MathF.Abs(dv));
    }

    private static float Wrap(float a)
    {
        a %= MathF.Tau;
        return a > MathF.PI ? a - MathF.Tau : a < -MathF.PI ? a + MathF.Tau : a;
    }

    /// <summary>The stop case (ObjFlipLink with "and 0xFE"): the whole path goes off.</summary>
    private void StopPath()
    {
        var cur = Record(Head);
        for (var hops = 0; cur != null && hops < MaxHops; hops++)
        {
            _b.ClearEnableOf(cur.FileOffset);
            var link = _b.Link(cur.FileOffset);
            if (cur.IsActor || link < 1)
            {
                return;
            }

            cur = Record(link);
        }
    }

    /// <summary>How long the machine takes to drive its path from where it stands, in seconds of its
    /// own ticks, every marker taken as on; <paramref name="cap"/> stops a loop.</summary>
    public float DriveSeconds(float dt = 1f / 60f, float cap = 600f)
    {
        var pos = Position;
        var cur = Record(Head);
        if (cur == null)
        {
            return 0f;
        }

        var v = 0f;
        var want = SpeedK * Vector3.Distance(pos, Pos(cur));
        var t = 0f;
        var hops = 0;
        while (t < cap)
        {
            if (Vector3.Distance(pos, Pos(cur)) <= Reach)
            {
                var next = cur.LinkNext > 0 ? Record(cur.LinkNext) : null;
                if (next == null || next.Variant != MapEntityVariant.Sprite || next.MarkerType < 0
                    || next.MarkerType == MarkerPathLoop || hops >= MaxHops)
                {
                    return t;
                }

                want = SpeedK * Vector3.Distance(Pos(cur), Pos(next));
                cur = next;
                hops++;
            }

            var to = Pos(cur) - pos;
            var len = to.Length();
            if (len > 0.001f)
            {
                pos += to / len * MathF.Min(v * dt, len);
            }

            var dv = want - v;
            v += Math.Clamp(MathF.Sign(dv) * Accel * dt, -MathF.Abs(dv), MathF.Abs(dv));
            t += dt;
        }

        return cap;
    }

    /// <summary>Where the machine is on its path (a save).</summary>
    public PathVehicleSnapshot Snapshot() => new(Id, Started, Position, Yaw, Target, Speed, TargetSpeed, Finished, Dead);

    /// <summary>Puts a <see cref="Snapshot"/> back.</summary>
    public void Restore(in PathVehicleSnapshot s)
    {
        Started = s.Started;
        Position = s.Position;
        Yaw = s.Yaw;
        Target = s.Target;
        Speed = s.Speed;
        TargetSpeed = s.TargetSpeed;
        Finished = s.Finished;
        Dead = s.Dead;
    }

    /// <summary>The vehicle back at its marker, standing, the path not picked up.</summary>
    public void Reset()
    {
        Position = Spawn;
        Yaw = 0f;
        Target = 0;
        Speed = TargetSpeed = 0f;
        Finished = Started = false;
    }

    public override string ToString() =>
        $"@{Id:x5} type {VehicleType} head {Head:x5} target {Target:x5} speed {Speed:0}/{TargetSpeed:0}";
}

/// <summary>The path vehicles of one level, by actor-marker offset (<see cref="LevelBehaviour.Paths"/>).
/// <see cref="Robots"/> reads their poses for the robots it built at the same markers.</summary>
public sealed class PathVehicles
{
    private readonly Dictionary<int, PathVehicle> _all = [];

    internal PathVehicles(LevelBehaviour behaviour, IEnumerable<MapEntity> markers)
    {
        foreach (var e in markers)
        {
            if (e.LinkNext > 0)
            {
                _all[e.FileOffset] = new PathVehicle(behaviour, e);
            }
        }
    }

    public IReadOnlyCollection<PathVehicle> All => _all.Values;
    public int Count => _all.Count;

    public PathVehicle? Get(int marker) => _all.GetValueOrDefault(marker);

    /// <summary>One frame of every machine, the player's feet at <paramref name="player"/>.</summary>
    public void Tick(float dt, Vector3 player)
    {
        foreach (var v in _all.Values)
        {
            v.Tick(dt, player);
        }
    }

    /// <summary>Every machine's progress, by actor-marker offset (a save).</summary>
    public PathVehicleSnapshot[] Snapshot() => [.. _all.Values.OrderBy(v => v.Id).Select(v => v.Snapshot())];

    /// <summary>The machines' state for the per-tick state hash (an unordered sum, no allocation).</summary>
    public void HashInto(ref RetroEng.Loop.StateHash h)
    {
        ulong sum = 0;
        foreach (var v in _all.Values)
        {
            var e = new RetroEng.Loop.StateHash();
            e.Add(v.Started);
            e.Add(v.Position);
            e.Add(v.Yaw);
            e.Add(v.Target);
            e.Add(v.Speed);
            e.Add(v.TargetSpeed);
            e.Add(v.Finished);
            e.Add(v.Dead);
            sum += RetroEng.Loop.StateHash.Entry(v.Id, e);
        }

        h.AddUnordered(sum);
    }

    /// <summary>Puts a <see cref="Snapshot"/> back; a machine the map does not have is ignored.</summary>
    public void Restore(IEnumerable<PathVehicleSnapshot> vehicles)
    {
        foreach (var s in vehicles)
        {
            if (_all.TryGetValue(s.Id, out var v))
            {
                v.Restore(s);
            }
        }
    }

    /// <summary>Where the machine of the marker at <paramref name="marker"/> stands and faces.</summary>
    public bool TryGetPose(int marker, out Vector3 position, out float yaw)
    {
        if (_all.TryGetValue(marker, out var v))
        {
            (position, yaw) = (v.Position, v.Yaw);
            return true;
        }

        (position, yaw) = (default, 0f);
        return false;
    }

    /// <summary>The robot of the marker died: its machine stops.</summary>
    public void Kill(int marker)
    {
        if (_all.TryGetValue(marker, out var v))
        {
            v.Dead = true;
        }
    }
}
