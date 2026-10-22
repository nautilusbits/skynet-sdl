using System.Numerics;
using RetroEng.Physics;
using SkyNet.Game.Weapons;

namespace SkyNet.Game.Vehicles;

/// <summary>What one ram did to the car (<see cref="Ramming.Check"/>).</summary>
public enum RamOutcome
{
    /// <summary>Nothing new touched: the move stands.</summary>
    None,
    /// <summary>The target broke: the car drives on at 7/8 of its speed.</summary>
    DroveThrough,
    /// <summary>The target held: the move is undone, the speed negated, the wheel centred.</summary>
    Bounced,
    /// <summary>The same target again within <see cref="Ramming.Rearm"/>: the move is undone, the car stops, no hit.</summary>
    Stopped,
}

/// <summary>One ram: the target, the points it took, the car's speed and the outcome.</summary>
public readonly record struct RamEvent(IDamageable Target, float Points, float Speed, RamOutcome Outcome);

/// <summary>
/// The jeep driving into things (DOS v1.01 0x135a7e, run once per move from the jeep's post-move
/// 0x135588; the Godot port's fly_camera.gd _ram_check). The car's position and heading are saved
/// before the move; after it, the first target the hull box touches that it did not touch before
/// the move gets ObjHit (0x139019) with the speed field &gt;&gt; 9 — the field is u/s × 256, so
/// ⌊v/2⌋ at v u/s, and a reversing car deals nothing (ObjHit clamps a negative hit to 0). No
/// difficulty factor (that lives in the projectile impact path, not in ObjHit), and the ram does
/// the driver and the hull no harm (what hurts him is the robot's death blast as he rolls over the
/// wreck). ObjHit's carry decides:
/// <list type="bullet">
/// <item>destroyed: the car drives on at 7/8 speed, sound 30;</item>
/// <item>survived, or nothing with hit points: the saved state comes back and the speed is
/// NEGATED (0x1355c5), the wheel centred (0x1355d1), sound 30 — the car bounces off.</item>
/// </list>
/// Targets are the level's <see cref="DamageTargets"/>: robots as their spheres, breakable records
/// (the MAP.260 car-wash door CWDOOR, 60 points: one ram above 120 u/s) through the collision
/// triangles they own. The port's addition (Godot RAM_REARM): one target is hit at most once per
/// 0.3 s; inside that wait the car is only stopped — without it, holding the throttle against a
/// robot re-hits it every few ticks at one tick's worth of speed.
/// </summary>
public sealed class Ramming(CollisionWorld world, DamageTargets targets)
{
    /// <summary>Below this speed (u/s, either way) there is no ram.</summary>
    public const float MinSpeed = 2f;
    /// <summary>The ram box's half size: HUMMER.3D is 106 × 75 × 227, plus a few units (x across, y up, z along).</summary>
    public static readonly Vector3 Half = new(57f, 30f, 118f);
    /// <summary>The box's centre over the hull's bottom, so a flat floor never counts as a touch.</summary>
    public const float Lift = 50f;
    /// <summary>The port's wait before one target can be rammed again (s).</summary>
    public const float Rearm = 0.3f;
    /// <summary>A destroyed target lets the car on at this part of its speed.</summary>
    public const float DriveOnFactor = 0.875f;
    private const int PruneAt = 16;

    private readonly Dictionary<IDamageable, float> _next = new(ReferenceEqualityComparer.Instance);
    private readonly List<Triangle> _tris = [];
    private readonly List<IDamageable> _now = [], _before = [];
    private float _clock;

    /// <summary>The points ObjHit gets at <paramref name="speed"/> u/s: ⌊v/2⌋, 0 backing up.</summary>
    public static float DamageAt(float speed) => MathF.Max(MathF.Floor(speed * 0.5f), 0f);

    /// <summary>A ram happened (the app logs it).</summary>
    public event Action<RamEvent>? Rammed;

    /// <summary>Forgets the waits (a new map, a new ride).</summary>
    public void Reset()
    {
        _next.Clear();
        _clock = 0f;
    }

    /// <summary>One move of the car from (<paramref name="prePos"/>, <paramref name="preYaw"/>) to
    /// (<paramref name="pos"/>, <paramref name="yaw"/>) at <paramref name="speed"/>: deals the hit
    /// and says what the car must do. Positions are the hull's bottom.</summary>
    public RamOutcome Check(Vector3 prePos, float preYaw, Vector3 pos, float yaw, float speed, float dt)
    {
        _clock += dt;
        if (_next.Count > PruneAt)
        {
            foreach (var k in _next.Where(p => _clock >= p.Value).Select(p => p.Key).ToList())
            {
                _next.Remove(k);
            }
        }

        if (MathF.Abs(speed) < MinSpeed)
        {
            return RamOutcome.None;
        }

        Touching(pos, yaw, _now);
        if (_now.Count == 0)
        {
            return RamOutcome.None;
        }

        // Only a touch THIS move made is a ram: something already inside the hull before it (a robot
        // that walked into a standing car) is left alone, or the restore would pin the car for good.
        Touching(prePos, preYaw, _before);
        foreach (var t in _now)
        {
            if (_before.Contains(t))
            {
                continue;
            }

            if (_next.TryGetValue(t, out var due) && _clock < due)
            {
                Rammed?.Invoke(new RamEvent(t, 0f, speed, RamOutcome.Stopped));
                return RamOutcome.Stopped;
            }

            _next[t] = _clock + Rearm;
            var points = DamageAt(speed);
            t.TakeDamage(new Damage(points, DamageKind.Direct, t.Centre, Scaled: false));
            var outcome = t.Alive ? RamOutcome.Bounced : RamOutcome.DroveThrough;
            Rammed?.Invoke(new RamEvent(t, points, speed, outcome));
            return outcome;
        }

        return RamOutcome.None;
    }

    /// <summary>The live targets the ram box overlaps with the car at <paramref name="at"/> facing
    /// <paramref name="yaw"/>, in a stable order (spheres first, then records by triangle order).</summary>
    public void Touching(Vector3 at, float yaw, List<IDamageable> into)
    {
        into.Clear();
        var centre = at + new Vector3(0f, Lift, 0f);
        var right = new Vector3(MathF.Cos(yaw), 0f, -MathF.Sin(yaw));
        var back = new Vector3(MathF.Sin(yaw), 0f, MathF.Cos(yaw));
        foreach (var t in targets.All)
        {
            if (t.Radius > 0f && t.Alive && SphereOverlaps(Local(t.Centre - centre, right, back), t.Radius))
            {
                into.Add(t);
            }
        }

        var reach = new Vector3(Half.Z, Half.Y, Half.Z);
        _tris.Clear();
        world.Gather(new Box(centre - reach, centre + reach), _tris);
        foreach (var tri in _tris)
        {
            if (tri.Owner < 0 || targets.ByOwner(tri.Owner) is not { } t || into.Contains(t))
            {
                continue;
            }

            if (TriangleOverlaps(Local(tri.A - centre, right, back), Local(tri.B - centre, right, back), Local(tri.C - centre, right, back)))
            {
                into.Add(t);
            }
        }
    }

    private static Vector3 Local(Vector3 d, Vector3 right, Vector3 back) => new(Vector3.Dot(d, right), d.Y, Vector3.Dot(d, back));

    private static bool SphereOverlaps(Vector3 c, float r)
    {
        var q = Vector3.Clamp(c, -Half, Half);
        return Vector3.DistanceSquared(q, c) < r * r;
    }

    /// <summary>Triangle against the box (Akenine-Möller's separating axes: the box's 3 axes, the
    /// face normal, the 9 edge cross products), all in the box's frame.</summary>
    private static bool TriangleOverlaps(Vector3 v0, Vector3 v1, Vector3 v2)
    {
        var h = Half;
        if (MathF.Max(v0.X, MathF.Max(v1.X, v2.X)) < -h.X || MathF.Min(v0.X, MathF.Min(v1.X, v2.X)) > h.X) return false;
        if (MathF.Max(v0.Y, MathF.Max(v1.Y, v2.Y)) < -h.Y || MathF.Min(v0.Y, MathF.Min(v1.Y, v2.Y)) > h.Y) return false;
        if (MathF.Max(v0.Z, MathF.Max(v1.Z, v2.Z)) < -h.Z || MathF.Min(v0.Z, MathF.Min(v1.Z, v2.Z)) > h.Z) return false;

        Span<Vector3> edges = [v1 - v0, v2 - v1, v0 - v2];
        var n = Vector3.Cross(edges[0], edges[1]);
        if (MathF.Abs(Vector3.Dot(n, v0)) > h.X * MathF.Abs(n.X) + h.Y * MathF.Abs(n.Y) + h.Z * MathF.Abs(n.Z)) return false;

        Span<Vector3> axes = [Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ];
        foreach (var e in edges)
        {
            foreach (var u in axes)
            {
                var a = Vector3.Cross(u, e);
                if (a.LengthSquared() < 1e-12f) continue;
                float p0 = Vector3.Dot(a, v0), p1 = Vector3.Dot(a, v1), p2 = Vector3.Dot(a, v2);
                var r = h.X * MathF.Abs(a.X) + h.Y * MathF.Abs(a.Y) + h.Z * MathF.Abs(a.Z);
                if (MathF.Min(p0, MathF.Min(p1, p2)) > r || MathF.Max(p0, MathF.Max(p1, p2)) < -r) return false;
            }
        }

        return true;
    }
}
