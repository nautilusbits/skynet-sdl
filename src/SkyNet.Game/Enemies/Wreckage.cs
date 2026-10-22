using RetroEng.Loop;
using System.Numerics;
using RetroEng.Physics;

namespace SkyNet.Game.Enemies;

/// <summary>One flying wreck part of <see cref="Wreckage"/>: a DOS state-1 actor (types 88-99:
/// engine, gun, fin, wing … all on the shared parameter block 0x49e73).</summary>
public struct WreckPart
{
    public bool Live;
    /// <summary>The part's enemy type (the death list's type).</summary>
    public int Type;
    public EnemyModelInfo? Model;
    public Vector3 Position;
    /// <summary>Where it was thrown from: DOS blows it up 2000 u from here (0x13790f).</summary>
    public Vector3 Origin;
    /// <summary>Units per second, Y up.</summary>
    public Vector3 Velocity;
    /// <summary>Pitch, yaw, roll (radians) and their rates (radians per second).</summary>
    public Vector3 Angles;
    public Vector3 Spin;
    public float Age;
    /// <summary>Model to world, kept by <see cref="Wreckage.Tick"/> for the draw.</summary>
    public Matrix4x4 World;
    /// <summary>Position and angles at the previous tick's end (render interpolation; never simulated).</summary>
    public Vector3 PreviousPosition, PreviousAngles;

    /// <summary>Model to world between the last two ticks (<see cref="Interpolation"/>).</summary>
    public readonly Matrix4x4 WorldAt(float alpha)
    {
        if (alpha >= 1f)
        {
            return World;
        }

        var at = Interpolation.Lerp(PreviousPosition, Position, alpha);
        var a = at == Position ? Angles : Vector3.Lerp(PreviousAngles, Angles, alpha);
        return Matrix4x4.CreateRotationY(a.Y) * Matrix4x4.CreateRotationX(a.X) * Matrix4x4.CreateRotationZ(-a.Z) * Matrix4x4.CreateTranslation(at);
    }
}

/// <summary>
/// The wreck parts a destroyed robot throws: DOS EnemyKill (v1.01 0x129f30) walks the type's death
/// spec (table +0x10; <see cref="SkyNet.Formats.EnemyType.Death"/>) for the robot and each of its
/// segments and builds one state-1 actor per entry (0x12a059):
/// <list type="bullet">
/// <item>at the segment's position plus the entry's offset turned by the segment's angles, pushed
/// out along its flight direction by the part model's radius + 10;</item>
/// <item>flight direction = normalize(horizontal unit from the robot's root to that point, plus
/// 2.0 .. 4.0 up) — a steep lob outwards; speed 120 + (rand &amp; 0x7f) u/s;</item>
/// <item>angles 0, each axis spinning ±(1024 + (rand &amp; 0x3ff)) / 2048 rev/s.</item>
/// </list>
/// The state-1 handler (0x1377fe) adds gravity 392 u/s² (the soldier's), moves and spins the part,
/// and once it touches the world (0x123e36) or is 2000 u from where it was thrown, ObjHits itself
/// (HP 10): its own EnemyKill — the explosion effect, sound 33-36 (0x21 + rand &amp; 3) and the
/// death blast 75 (0x124793). There is no bounce and no rest. Pooled: <see cref="Capacity"/> parts,
/// a throw past it is dropped.
/// </summary>
public sealed class Wreckage
{
    public const int Capacity = 48;
    public const float Gravity = 392f;
    public const float MinSpeed = 120f, SpeedSpread = 127f;
    public const float MinLift = 2f, LiftSpread = 2f;
    public const int MinSpin = 1024, SpinSpread = 1023;
    public const float MaxTravel = 2000f;
    /// <summary>DOS pushes the part out by its model radius plus this.</summary>
    public const float PushOut = 10f;
    /// <summary>A part thrown into a void (nothing under it) still goes off by then: 2000 u of fall
    /// takes 3.2 s, so this only guards a part that cannot move.</summary>
    public const float MaxLife = 8f;

    private readonly WreckPart[] _parts = new WreckPart[Capacity];

    public ReadOnlySpan<WreckPart> Parts => _parts;
    public int Count { get; private set; }
    /// <summary>Parts that went off (a test counter).</summary>
    public int Exploded { get; private set; }

    /// <summary>Throws every part of <paramref name="robot"/>'s death lists (its own and each
    /// segment's, children first as EnemyKill recurses). Returns how many flew.</summary>
    public int Throw(Enemy robot, Robots robots)
    {
        robot.UpdateTransforms();
        return ThrowSegments(robot.Root, robot, robots);
    }

    private int ThrowSegments(EnemyPart seg, Enemy robot, Robots robots)
    {
        var n = 0;
        foreach (var c in seg.Children)
        {
            n += ThrowSegments(c, robot, robots);
        }

        if (seg.Type is not { } type)
        {
            return n;
        }

        foreach (var d in type.Death)
        {
            var at = Vector3.Transform(new Vector3(d.X, -d.Y, -d.Z), seg.World);
            var model = d.Type >= 0 && d.Type < robots.Ais.Types.Length
                ? robots.Models.Get(robots.Ais.Types[d.Type].ModelFile)
                : null;
            if (Add(d.Type, model, at, robot.Position, robots.Rng))
            {
                n++;
            }
        }

        return n;
    }

    /// <summary>One part of <paramref name="type"/> thrown from <paramref name="at"/> away from
    /// <paramref name="root"/> (0x12a059). False when the pool is full.</summary>
    public bool Add(int type, EnemyModelInfo? model, Vector3 at, Vector3 root, SimRandom rng)
    {
        var slot = -1;
        for (var i = 0; i < _parts.Length; i++)
        {
            if (!_parts[i].Live)
            {
                slot = i;
                break;
            }
        }

        if (slot < 0)
        {
            return false;
        }

        var spin = new Vector3(Spin(rng), Spin(rng), Spin(rng));
        var flat = new Vector3(at.X - root.X, 0f, at.Z - root.Z);
        if (flat.LengthSquared() > 1e-6f)
        {
            flat = Vector3.Normalize(flat);
        }

        var dir = Vector3.Normalize(flat + new Vector3(0f, MinLift + LiftSpread * (rng.Next(4096) / 4096f), 0f));
        var speed = MinSpeed + rng.Next((int)SpeedSpread + 1);
        var radius = model is { } m ? MathF.Max(m.Bounds.Max.Length(), m.Bounds.Min.Length()) : 0f;
        var start = at + dir * (radius + PushOut);
        _parts[slot] = new WreckPart
        {
            Live = true,
            Type = type,
            Model = model,
            Position = start,
            PreviousPosition = start,
            Origin = start,
            Velocity = dir * speed,
            Spin = spin,
            World = Matrix4x4.CreateTranslation(start),
        };
        Count++;
        return true;
    }

    private static float Spin(SimRandom rng)
    {
        var rate = (MinSpin + rng.Next(SpinSpread + 1)) / 2048f * MathF.Tau;
        return rng.Next(2) != 0 ? -rate : rate;
    }

    /// <summary>A tick's start: every part's pose now becomes the one the frames glide from.</summary>
    public void KeepPoses()
    {
        for (var i = 0; i < _parts.Length; i++)
        {
            ref var p = ref _parts[i];
            (p.PreviousPosition, p.PreviousAngles) = (p.Position, p.Angles);
        }
    }

    /// <summary>One step of every part (0x1377fe): gravity, move, spin; a part that touches the
    /// world, is <see cref="MaxTravel"/> from where it was thrown or outlives <see cref="MaxLife"/>
    /// goes off at <paramref name="explode"/>.</summary>
    public void Tick(float dt, CollisionWorld world, Action<Vector3, WreckPart> explode)
    {
        if (Count == 0 || dt <= 0f)
        {
            return;
        }

        for (var i = 0; i < _parts.Length; i++)
        {
            ref var p = ref _parts[i];
            if (!p.Live)
            {
                continue;
            }

            p.Age += dt;
            p.Angles += p.Spin * dt;
            p.Velocity.Y -= Gravity * dt;
            var step = p.Velocity * dt;
            var len = step.Length();
            var to = p.Position + step;
            var hit = false;
            if (len > 1e-4f && world.Raycast(p.Position, step / len, len, out var h))
            {
                to = h.Point;
                hit = true;
            }

            p.Position = to;
            p.World = Matrix4x4.CreateRotationY(p.Angles.Y) * Matrix4x4.CreateRotationX(p.Angles.X)
                      * Matrix4x4.CreateRotationZ(-p.Angles.Z) * Matrix4x4.CreateTranslation(to);
            if (hit || p.Age >= MaxLife || Vector3.DistanceSquared(to, p.Origin) >= MaxTravel * MaxTravel)
            {
                var gone = p;
                p.Live = false;
                Count--;
                Exploded++;
                explode(gone.Position, gone);
            }
        }
    }

    /// <summary>Drops every part (the map is left or restored).</summary>
    public void Clear()
    {
        Array.Clear(_parts);
        Count = 0;
    }
}
