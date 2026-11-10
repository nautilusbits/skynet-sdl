using System.Numerics;
using RetroEng.Physics;
using SkyNet.Formats;
using SkyNet.Game.Collision;

namespace SkyNet.Game.Enemies;

/// <summary>
/// The live robots as solid geometry for the soldier alone. SKYNET.EXE v1.01 makes every robot
/// segment a variant-1 map record (0x129a9d → 0x13996e); the player's move (0x12b18c) gathers the
/// records of the 4 nearby cells (0x13a95d, hidden actors skipped) and tests its spheres against
/// their rotated polygons (0x13ac3e / 0x144d94), the driven HK's move (0x131be0) too. Robots never
/// test the player. Here: each segment's frame-0 triangles at its world transform, re-placed only
/// when that transform changes. Dead, hidden and flying path machines (MAP.234's pick-up HK) are
/// left out; wreckage is not a robot.
/// <para>A robot that drove into the body (a path truck) is first pushed off it sideways; when
/// there is no room (a wall behind) that robot lets the body through until it is clear of it.</para>
/// </summary>
public sealed class RobotColliders : ITriangleSource
{
    private const float Skin = 0.25f;
    /// <summary>A push shorter than this is a body resting against a robot, not one driven into.</summary>
    private const float MinPush = 0.5f;

    private sealed class Placed(Triangle[] triangles)
    {
        public Matrix4x4 At = new(float.NaN, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        public readonly Triangle[] Triangles = triangles;
        public Box Bounds = Box.Empty;
    }

    private readonly Func<Robots?> _robots;
    private readonly CollisionWorld _alone;
    private readonly Dictionary<Model3D, Vector3[][]> _faces = [];
    private readonly Dictionary<EnemyPart, Placed> _placed = [];
    private readonly List<Enemy> _passing = [];
    private Robots? _for;
    private Enemy? _only;

    public RobotColliders(Func<Robots?> robots)
    {
        _robots = robots;
        _alone = new CollisionWorld().Add(this);
    }

    public Box Bounds
    {
        get
        {
            var b = Box.Empty;
            if (Current() is not { } robots)
            {
                return b;
            }

            for (var i = 0; i < robots.All.Count; i++)
            {
                var r = robots.All[i];
                if (!Blocks(r))
                {
                    continue;
                }

                foreach (var p in r.Segments)
                {
                    if (Place(p) is { } placed)
                    {
                        b = b.Union(placed.Bounds);
                    }
                }
            }

            return b;
        }
    }

    public void Gather(in Box box, List<Triangle> into)
    {
        if (Current() is not { } robots)
        {
            return;
        }

        for (var i = 0; i < robots.All.Count; i++)
        {
            var r = robots.All[i];
            var wanted = _only == null ? !_passing.Contains(r) : r == _only;
            if (!wanted || !Blocks(r))
            {
                continue;
            }

            foreach (var p in r.Segments)
            {
                Gather(p, box, into);
            }
        }
    }

    /// <summary>Where the body at <paramref name="feet"/> may stand before its move: off a robot
    /// that drove into it, sideways, when <paramref name="world"/> has room there; else in place,
    /// with the robots it touches let through until it is clear of them.</summary>
    public Vector3 Free(SphereBody body, Vector3 feet, CollisionWorld world)
    {
        // Forget the robots it has walked out of: they block again.
        for (var i = _passing.Count - 1; i >= 0; i--)
        {
            if (!Touches(_passing[i], body, feet))
            {
                _passing.RemoveAt(i);
            }
        }

        if (Current() is not { } robots || !_alone.Overlaps(body, feet))
        {
            return feet;
        }

        var push = _alone.Depenetrate(body, feet, Skin);
        if (push.Length() < MinPush)
        {
            return feet;
        }

        push.Y = 0f;
        if (push.Length() >= MinPush && !world.Overlaps(body, feet + push))
        {
            return feet + push;
        }

        // No room: DOS lets a robot overlap the soldier (robots never test him).
        for (var i = 0; i < robots.All.Count; i++)
        {
            var r = robots.All[i];
            if (!_passing.Contains(r) && Blocks(r) && Touches(r, body, feet))
            {
                _passing.Add(r);
            }
        }

        return feet;
    }

    /// <summary>The session's robots; a new set (another map) drops the old placements.</summary>
    private Robots? Current()
    {
        var robots = _robots();
        if (robots != _for)
        {
            _for = robots;
            _placed.Clear();
            _passing.Clear();
        }

        return robots;
    }

    /// <summary>Live and shown; a flying path machine is the soldier's ride, not a wall.</summary>
    private static bool Blocks(Enemy r) => r.Alive && !(r.OnPath && r.IsFlying);

    private bool Touches(Enemy r, SphereBody body, Vector3 feet)
    {
        _only = r;
        var touches = _alone.Overlaps(body, feet);
        _only = null;
        return touches;
    }

    private void Gather(EnemyPart p, in Box box, List<Triangle> into)
    {
        if (Place(p) is not { } placed || !placed.Bounds.Overlaps(box))
        {
            return;
        }

        foreach (var t in placed.Triangles)
        {
            if (new Box(t.Min, t.Max).Overlaps(box))
            {
                into.Add(t);
            }
        }
    }

    /// <summary>The segment's triangles where it stands now, re-placed in place when it moved.</summary>
    private Placed? Place(EnemyPart p)
    {
        if (p.Model is not { } model || p.Gone)
        {
            return null;                              // a shot-off segment's record is gone too
        }

        if (!_placed.TryGetValue(p, out var placed))
        {
            if (!_faces.TryGetValue(model.Model, out var faces))
            {
                _faces[model.Model] = faces = LevelCollision.Faces(model.Model);
            }

            _placed[p] = placed = new Placed(new Triangle[faces.Length]);
        }

        if (placed.At == p.World)
        {
            return placed;
        }

        var shape = _faces[model.Model];
        var bounds = Box.Empty;
        for (var i = 0; i < shape.Length; i++)
        {
            var f = shape[i];
            var t = new Triangle(Vector3.Transform(f[0], p.World), Vector3.Transform(f[1], p.World), Vector3.Transform(f[2], p.World));
            placed.Triangles[i] = t;
            bounds = bounds.Include(t.A).Include(t.B).Include(t.C);
        }

        (placed.At, placed.Bounds) = (p.World, bounds);
        return placed;
    }
}
