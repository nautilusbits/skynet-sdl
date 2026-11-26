using System.Numerics;
using RetroEng.Render;

namespace SkyNet.Game.Baking;

/// <summary>
/// Lifts a dynamic mesh's faces off the world faces that lie on their plane and face the same
/// way (#72). DOS sorts its polygons, so a door sliding flush along a wall never flickers; a
/// depth buffer cannot order the two. Every face of the mesh on such a plane moves
/// <see cref="Gap"/> in front of the wall's, along its normal:
/// <code>
///   before: wall ====|==== door        after: wall ====|
///                    (one plane)                       |==== door, 1 unit nearer the eye
/// </code>
/// A slide is checked along its whole travel (<c>path</c>: the offsets it passes). Only the drawn
/// mesh moves; collision is built from the MAP records.
/// </summary>
internal static class MoverFaces
{
    /// <summary>How far in front of the wall's face a lifted face ends (world units): over ORIGINAL's
    /// depth step at the high detail's far distance (about 1 unit at 3992, near plane 1, 32-bit depth).</summary>
    public const float Gap = 1f;

    /// <summary>Faces this close to one plane (world units) share it for the depth buffer.</summary>
    private const float Coplanar = 0.25f;

    /// <summary>Normals at least this aligned (cosine) face the same way.</summary>
    private const float Parallel = 0.999f;

    /// <summary>Faces must cover each other by more than this (world units, across every edge) to show
    /// a fight; neighbours that only touch along an edge don't.</summary>
    private const float MinCover = 0.5f;

    private readonly record struct Face(Vector3 A, Vector3 B, Vector3 C, Vector3 Normal, Vector3 Min, Vector3 Max)
    {
        public static Face? Of(Vector3 a, Vector3 b, Vector3 c, uint packed)
        {
            var n = Vector3.Cross(b - a, c - a);
            if (n.LengthSquared() < 1e-6f)
            {
                return null;
            }

            // The outward side: the stored normal's, should the winding disagree.
            n = Vector3.Normalize(n);
            if (packed != 0 && Vector3.Dot(n, Vertex.UnpackNormal(packed)) < 0f)
            {
                n = -n;
            }

            return new Face(a, b, c, n, Vector3.Min(Vector3.Min(a, b), c), Vector3.Max(Vector3.Max(a, b), c));
        }

        public Vector3 Centre => (A + B + C) / 3f;
    }

    /// <summary>The dynamic meshes, in the order given, with their faces that fight the world's lifted.</summary>
    public static BlockDynamic[] Separate(BlockBucket[] world, IReadOnlyList<(BlockDynamic Mesh, Vector3[] Path)> dynamic)
    {
        var walls = Faces(world).Select(f => f.Face).ToList();
        return [.. dynamic.Select(d => d.Mesh with { Stages = [.. d.Mesh.Stages.Select(s => s == null ? null : Lift(s, d.Path, walls))] })];
    }

    /// <summary>A stage with every face on a fighting plane moved in front of the world faces it fights.</summary>
    private static BlockBucket[] Lift(BlockBucket[] stage, Vector3[] path, List<Face> world)
    {
        var own = Faces(stage).ToList();
        if (own.Count == 0)
        {
            return stage;
        }

        // Only the world faces near the travel's box can fight.
        var min = own.Aggregate(own[0].Face.Min, (m, f) => Vector3.Min(m, f.Face.Min));
        var max = own.Aggregate(own[0].Face.Max, (m, f) => Vector3.Max(m, f.Face.Max));
        var (lo, hi) = (path.Aggregate(min, (m, p) => Vector3.Min(m, min + p)), path.Aggregate(max, (m, p) => Vector3.Max(m, max + p)));
        var near = world.Where(f => Touches(f, lo - new Vector3(Coplanar), hi + new Vector3(Coplanar))).ToList();

        // How far each face must move to clear what it fights (none: it fights nothing).
        var need = own.Select(o => Need(o.Face, path, near)).ToArray();
        if (need.All(n => n == null))
        {
            return stage;
        }

        // Every face on a fighting plane moves with it: no step across one flat side of the door.
        var lift = new float[own.Count];
        for (var i = 0; i < own.Count; i++)
        {
            if (need[i] is not { } amount)
            {
                continue;
            }

            for (var j = 0; j < own.Count; j++)
            {
                if (SamePlane(own[i].Face, own[j].Face))
                {
                    lift[j] = MathF.Max(lift[j], amount);
                }
            }
        }

        var vertices = stage.Select(b => b.Vertices.ToArray()).ToArray();
        for (var i = 0; i < own.Count; i++)
        {
            if (lift[i] <= 0f)
            {
                continue;
            }

            // A face owns its corners (no corner is shared between faces), so each moves once.
            var (bucket, first) = (own[i].Bucket, own[i].First);
            var by = own[i].Face.Normal * lift[i];
            for (var k = 0; k < 3; k++)
            {
                var index = stage[bucket].Indices[first + k];
                var original = stage[bucket].Vertices[index];
                vertices[bucket][index] = original with { Position = original.Position + by };
            }
        }

        return [.. stage.Select((b, i) => b with { Vertices = vertices[i] })];
    }

    /// <summary>The move that puts <paramref name="face"/> <see cref="Gap"/> in front of every world
    /// face it fights anywhere along its path; null when it fights none.</summary>
    private static float? Need(Face face, Vector3[] path, List<Face> world)
    {
        float? need = null;
        foreach (var at in path)
        {
            var moved = Moved(face, at);
            foreach (var other in world)
            {
                if (Fights(moved, other) is { } ahead)
                {
                    need = MathF.Max(need ?? 0f, ahead + Gap);
                }
            }
        }

        return need;
    }

    /// <summary>How far <paramref name="other"/> lies in front of <paramref name="face"/> when the two
    /// share a plane, face the same way and cover each other; null otherwise.</summary>
    private static float? Fights(Face face, Face other)
    {
        if (Vector3.Dot(face.Normal, other.Normal) < Parallel)
        {
            return null;
        }

        var ahead = Vector3.Dot(other.Centre - face.Centre, face.Normal);
        if (MathF.Abs(ahead) > Coplanar || !Touches(other, face.Min - new Vector3(Coplanar), face.Max + new Vector3(Coplanar)))
        {
            return null;
        }

        return Covers(face, other) ? ahead : null;
    }

    /// <summary>Whether two faces on one plane overlap by more than <see cref="MinCover"/> across each
    /// of their edges (separating axes in the plane).</summary>
    private static bool Covers(Face a, Face b)
    {
        Span<Vector3> edges = [a.B - a.A, a.C - a.B, a.A - a.C, b.B - b.A, b.C - b.B, b.A - b.C];
        foreach (var edge in edges)
        {
            var axis = Vector3.Cross(a.Normal, edge);
            if (axis.LengthSquared() < 1e-6f)
            {
                continue;
            }

            axis = Vector3.Normalize(axis);
            var (aMin, aMax) = Extent(a, axis);
            var (bMin, bMax) = Extent(b, axis);
            if (MathF.Min(aMax, bMax) - MathF.Max(aMin, bMin) <= MinCover)
            {
                return false;
            }
        }

        return true;
    }

    private static (float Min, float Max) Extent(Face f, Vector3 axis)
    {
        var (a, b, c) = (Vector3.Dot(f.A, axis), Vector3.Dot(f.B, axis), Vector3.Dot(f.C, axis));
        return (MathF.Min(a, MathF.Min(b, c)), MathF.Max(a, MathF.Max(b, c)));
    }

    private static bool SamePlane(Face a, Face b) =>
        Vector3.Dot(a.Normal, b.Normal) >= Parallel && MathF.Abs(Vector3.Dot(b.Centre - a.Centre, a.Normal)) <= Coplanar;

    private static bool Touches(Face f, Vector3 min, Vector3 max) =>
        f.Max.X >= min.X && f.Min.X <= max.X && f.Max.Y >= min.Y && f.Min.Y <= max.Y && f.Max.Z >= min.Z && f.Min.Z <= max.Z;

    private static Face Moved(Face f, Vector3 by) =>
        by == Vector3.Zero ? f : f with { A = f.A + by, B = f.B + by, C = f.C + by, Min = f.Min + by, Max = f.Max + by };

    /// <summary>The buckets' triangles as faces, with where each sits (bucket, first index).</summary>
    private static IEnumerable<(Face Face, int Bucket, int First)> Faces(BlockBucket[] buckets)
    {
        for (var b = 0; b < buckets.Length; b++)
        {
            var (v, ix) = (buckets[b].Vertices, buckets[b].Indices);
            for (var i = 0; i + 2 < ix.Length; i += 3)
            {
                if (Face.Of(v[ix[i]].Position, v[ix[i + 1]].Position, v[ix[i + 2]].Position, v[ix[i]].Normal) is { } face)
                {
                    yield return (face, b, i);
                }
            }
        }
    }
}
