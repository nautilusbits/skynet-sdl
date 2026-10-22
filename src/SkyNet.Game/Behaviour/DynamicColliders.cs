using System.Numerics;
using RetroEng.Physics;

namespace SkyNet.Game.Behaviour;

/// <summary>Where a movable or breakable entity is drawn and collides now: <see cref="Delta"/> moves
/// its geometry from where the map placed it, <see cref="Stage"/> picks the TRANSFRM.PRS stage
/// mesh, <see cref="Visible"/> / <see cref="Solid"/> are false once it is gone.</summary>
public readonly record struct EntityPose(Matrix4x4 Delta, int Stage, bool Visible, bool Solid)
{
    public static readonly EntityPose Rest = new(Matrix4x4.Identity, 0, true, true);
}

/// <summary>Answers the pose of an entity by its MAP file offset (the level draws from it).</summary>
public interface IEntityPoses
{
    bool TryGetPose(int offset, out EntityPose pose);

    /// <summary>The pose drawn <paramref name="alpha"/> of the way from the previous tick (render
    /// interpolation); by default the latest.</summary>
    bool TryGetPose(int offset, float alpha, out EntityPose pose) => TryGetPose(offset, out pose);
}

/// <summary>
/// The colliders of the entities that move or break, out of the static grid: each body keeps its
/// triangles per stage where the map placed them (owner = the record's file offset) and is gathered
/// through its current pose. Bodies are few (dozens), so a box test per body is enough.
/// </summary>
public sealed class DynamicColliders : ITriangleSource
{
    private sealed class Body(int owner, Triangle[][] stages)
    {
        public readonly int Owner = owner;
        public readonly Triangle[][] Stages = stages;
        public readonly Box[] Bounds = [.. stages.Select(BoundsOf)];
    }

    private readonly List<Body> _bodies = [];
    private readonly IEntityPoses? _poses;

    public DynamicColliders(IEntityPoses? poses = null) => _poses = poses;

    public int Count => _bodies.Count;

    public Box Bounds
    {
        get
        {
            var b = Box.Empty;
            foreach (var body in _bodies)
            {
                foreach (var s in body.Bounds)
                {
                    b = b.Union(s);
                }
            }

            return b;
        }
    }

    /// <summary>Adds a body: one triangle list per stage (stage 0 = intact), placed in level axes.</summary>
    public void Add(int owner, Triangle[][] stages) => _bodies.Add(new Body(owner, stages));

    public void Gather(in Box box, List<Triangle> into)
    {
        foreach (var body in _bodies)
        {
            var pose = EntityPose.Rest;
            if (_poses != null && _poses.TryGetPose(body.Owner, out var p))
            {
                pose = p;
            }

            if (!pose.Solid || body.Stages.Length == 0)
            {
                continue;
            }

            var stage = Math.Clamp(pose.Stage, 0, body.Stages.Length - 1);
            var tris = body.Stages[stage];
            if (pose.Delta.IsIdentity)
            {
                if (!body.Bounds[stage].Overlaps(box))
                {
                    continue;
                }

                foreach (var t in tris)
                {
                    if (new Box(t.Min, t.Max).Overlaps(box))
                    {
                        into.Add(t);
                    }
                }

                continue;
            }

            if (!Transform(body.Bounds[stage], pose.Delta).Overlaps(box))
            {
                continue;
            }

            foreach (var t in tris)
            {
                var moved = new Triangle(Vector3.Transform(t.A, pose.Delta), Vector3.Transform(t.B, pose.Delta),
                    Vector3.Transform(t.C, pose.Delta), t.Owner);
                if (new Box(moved.Min, moved.Max).Overlaps(box))
                {
                    into.Add(moved);
                }
            }
        }
    }

    private static Box BoundsOf(Triangle[] tris)
    {
        var b = Box.Empty;
        foreach (var t in tris)
        {
            b = b.Include(t.A).Include(t.B).Include(t.C);
        }

        return b;
    }

    /// <summary>The box around the eight corners of <paramref name="b"/> moved by <paramref name="m"/>.</summary>
    internal static Box Transform(Box b, Matrix4x4 m)
    {
        if (b.IsEmpty)
        {
            return b;
        }

        var o = Box.Empty;
        for (var i = 0; i < 8; i++)
        {
            var c = new Vector3((i & 1) != 0 ? b.Max.X : b.Min.X, (i & 2) != 0 ? b.Max.Y : b.Min.Y, (i & 4) != 0 ? b.Max.Z : b.Min.Z);
            o = o.Include(Vector3.Transform(c, m));
        }

        return o;
    }
}
