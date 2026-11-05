using System.Numerics;
using RetroEng.Physics;

namespace SkyNet.Game.Weapons;

/// <summary>How a hit arrived: a shot or thrown item striking it, a radial blast, or the pipe.</summary>
public enum DamageKind { Direct, Blast, Melee }

/// <summary>One hit: DOS damage points (the table's units; the soldier has 1000), how it came and
/// where. <see cref="Scaled"/> is true for a direct weapon hit, which the difficulty multiplies.</summary>
public readonly record struct Damage(float Points, DamageKind Kind, Vector3 Point, bool Scaled);

/// <summary>
/// Anything the weapons can hurt: robots (later), destructible props (the behaviour layer) and the
/// soldier himself (blasts). <see cref="Centre"/> is where blast distance and line of sight are
/// measured to. A target is struck by a ray either through the collision world (it registers the
/// collision owner its triangles carry, <see cref="DamageTargets.Register"/>) or, with a positive
/// <see cref="Radius"/>, as a sphere (actors that are not in the static world).
/// </summary>
public interface IDamageable
{
    Vector3 Centre { get; }
    /// <summary>The sphere a ray or a flying shot tests; 0: struck only through its collision owner.</summary>
    float Radius { get; }
    bool Alive { get; }
    void TakeDamage(in Damage damage);
    /// <summary>What a blast adds to its distance before the falloff (<see cref="Blast.Damage"/>).</summary>
    float BlastPad => Blast.Pad;
    /// <summary>Where a blast measures its distance to: a robot's or record's origin, else the centre.</summary>
    Vector3 BlastOrigin => Centre;
}

/// <summary>The targets of a level: by collision owner (map entity file offset, <see cref="Hit.Owner"/>)
/// and as free spheres. The weapons look targets up here; whoever owns a target adds and removes it.</summary>
public sealed class DamageTargets
{
    private readonly Dictionary<int, IDamageable> _byOwner = [];
    private readonly List<IDamageable> _spheres = [];

    public IEnumerable<IDamageable> All => _byOwner.Values.Concat(_spheres).Distinct();
    public int Count => _byOwner.Count + _spheres.Count;

    /// <summary>A target struck through the collision world: triangles of <paramref name="owner"/> are it.</summary>
    public void Register(int owner, IDamageable target) => _byOwner[owner] = target;

    /// <summary>A target struck as its sphere (<see cref="IDamageable.Radius"/>).</summary>
    public void Add(IDamageable target)
    {
        if (!_spheres.Contains(target))
        {
            _spheres.Add(target);
        }
    }

    public void Remove(IDamageable target)
    {
        _spheres.Remove(target);
        foreach (var key in _byOwner.Where(p => p.Value == target).Select(p => p.Key).ToList())
        {
            _byOwner.Remove(key);
        }
    }

    public void Unregister(int owner) => _byOwner.Remove(owner);

    public IDamageable? ByOwner(int owner) => _byOwner.TryGetValue(owner, out var t) && t.Alive ? t : null;

    /// <summary>The owner key a target was registered under, or -1.</summary>
    public int OwnerOf(IDamageable target)
    {
        foreach (var (k, v) in _byOwner)
        {
            if (v == target)
            {
                return k;
            }
        }

        return -1;
    }

    /// <summary>The nearest live sphere target along a ray within <paramref name="max"/>.</summary>
    public IDamageable? RaySpheres(Vector3 origin, Vector3 direction, float max, out float distance, IDamageable? ignore = null)
    {
        distance = max;
        IDamageable? best = null;
        foreach (var t in _spheres)
        {
            if (t == ignore || !t.Alive || t.Radius <= 0f)
            {
                continue;
            }

            if (RaySphere(origin, direction, t.Centre, t.Radius, out var d) && d <= distance)
            {
                (distance, best) = (d, t);
            }
        }

        return best;
    }

    /// <summary>Entry distance of a unit ray into a sphere (0 when it starts inside).</summary>
    public static bool RaySphere(Vector3 origin, Vector3 direction, Vector3 centre, float radius, out float distance)
    {
        distance = 0f;
        var m = origin - centre;
        var c = Vector3.Dot(m, m) - radius * radius;
        if (c <= 0f)
        {
            return true;
        }

        var b = Vector3.Dot(m, direction);
        if (b > 0f)
        {
            return false;
        }

        var disc = b * b - c;
        if (disc < 0f)
        {
            return false;
        }

        distance = -b - MathF.Sqrt(disc);
        return true;
    }
}

/// <summary>DOS's radial blast (FUN_00124386, v1.01): strength S, radius R = clamp(S, 5, 500),
/// damage(d) = S·(R − d + 40)/R, dealt only when at least S/2 and with a clear line of sight.</summary>
public static class Blast
{
    /// <summary>The distance pad of a soldier or robot (0x28 in FUN_00124886); a map record pads
    /// with its model's radius instead (<see cref="IDamageable.BlastPad"/>).</summary>
    public const float Pad = 40f;

    public static float Radius(float strength) => Math.Clamp(strength, 5f, 500f);

    /// <summary>The points a blast of <paramref name="strength"/> deals at <paramref name="distance"/>; 0 below S/2.</summary>
    public static float Damage(float strength, float distance, float pad = Pad)
    {
        var r = Radius(strength);
        var d = strength * (r - distance + pad) / r;
        return d >= strength * 0.5f ? d : 0f;
    }

    /// <summary>How far a blast reaches (where it drops to S/2): R/2 + pad.</summary>
    public static float Reach(float strength, float pad = Pad) => Radius(strength) * 0.5f + pad;

    /// <summary>A shot's explosion steps this far back along its flight from the hit (0x12344b, −0x32).</summary>
    public const float StepBack = 50f;
    /// <summary>Outdoors it stays this far over the terrain (0x12347e, ground − 8 in DOS's Y down).</summary>
    public const float OverGround = 8f;

    /// <summary>Where a shot's explosion sits (DOS v1.01 0x12344b-0x123486, issue #36): <see cref="StepBack"/>
    /// back along <paramref name="velocity"/> from <paramref name="hit"/>, then outdoors (<paramref name="ground"/>:
    /// the terrain's height at world x, z) at least <see cref="OverGround"/> over it. A satchel dropped onto a
    /// floor goes off 50 over it.</summary>
    public static Vector3 Placement(Vector3 hit, Vector3 velocity, Func<float, float, float>? ground)
    {
        var at = velocity.LengthSquared() > 1e-6f ? hit - Vector3.Normalize(velocity) * StepBack : hit;
        if (ground == null)
        {
            return at;
        }

        return at with { Y = MathF.Max(at.Y, ground(at.X, at.Z) + OverGround) };
    }
}
