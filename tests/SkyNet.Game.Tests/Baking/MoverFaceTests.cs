using System.Numerics;
using RetroEng.Render;
using SkyNet.Formats;
using SkyNet.Game.Baking;
using SkyNet.Game.Behaviour;
using SkyNet.Game.Tests.Behaviour;

namespace SkyNet.Game.Tests.Baking;

/// <summary>A door's face on the same plane as a wall's, facing the same way, flickers in a depth
/// buffer: DOS sorts its polygons and never shows it (#72). MAP.254's sewer door and MAP.281's
/// vent door slide flush along a wall.</summary>
public class MoverFaceTests
{
    private const float Coplanar = 0.25f;
    private const float Parallel = 0.999f;
    private static readonly float[] PathSteps = [0f, 0.25f, 0.5f, 0.75f, 1f];

    private readonly record struct Tri(Vector3 A, Vector3 B, Vector3 C)
    {
        public Vector3 Normal => Vector3.Normalize(Vector3.Cross(B - A, C - A));
        public Vector3 Centre => (A + B + C) / 3f;
        public Tri Moved(Vector3 by) => new(A + by, B + by, C + by);
    }

    private static IEnumerable<Tri> Tris(IEnumerable<BlockBucket> buckets)
    {
        foreach (var b in buckets)
        {
            for (var i = 0; i + 2 < b.Indices.Length; i += 3)
            {
                yield return new Tri(b.Vertices[b.Indices[i]].Position, b.Vertices[b.Indices[i + 1]].Position, b.Vertices[b.Indices[i + 2]].Position);
            }
        }
    }

    /// <summary>Whether <paramref name="p"/>, put on <paramref name="t"/>'s plane, lies inside it.</summary>
    private static bool Inside(Vector3 p, Tri t, Vector3 n)
    {
        p -= n * Vector3.Dot(p - t.A, n);
        float Side(Vector3 a, Vector3 b) => Vector3.Dot(Vector3.Cross(b - a, p - a), n);
        var (s1, s2, s3) = (Side(t.A, t.B), Side(t.B, t.C), Side(t.C, t.A));
        return (s1 > 0f && s2 > 0f && s3 > 0f) || (s1 < 0f && s2 < 0f && s3 < 0f);
    }

    /// <summary>Whether <paramref name="a"/> and <paramref name="b"/> face the same way on one plane
    /// and cover each other (a point well inside one lies inside the other).</summary>
    private static bool Fight(Tri a, Tri b)
    {
        var (na, nb) = (a.Normal, b.Normal);
        if (float.IsNaN(na.X) || float.IsNaN(nb.X) || Vector3.Dot(na, nb) < Parallel
            || MathF.Abs(Vector3.Dot(a.Centre - b.A, nb)) > Coplanar)
        {
            return false;
        }

        static IEnumerable<Vector3> Probes(Tri t) =>
            [t.Centre, Vector3.Lerp(t.Centre, t.A, 0.8f), Vector3.Lerp(t.Centre, t.B, 0.8f), Vector3.Lerp(t.Centre, t.C, 0.8f)];
        return Probes(a).Any(p => Inside(p, b, nb)) || Probes(b).Any(p => Inside(p, a, na));
    }

    /// <summary>Where a mover's slide takes it (DOS axis X, Y down, Z → +X, −Y, −Z); the rest pose alone otherwise.</summary>
    private static IEnumerable<Vector3> Path(MapEntity e)
    {
        if (!ActRules.IsMover(e.ActType) || ActRules.Mover(e.ActType) is not { Family: MoverFamily.Slide } m)
        {
            return [Vector3.Zero];
        }

        var axis = m.Axis switch { 1 => -Vector3.UnitY, 2 => -Vector3.UnitZ, _ => Vector3.UnitX };
        return PathSteps.Select(f => axis * m.Span * m.Sign * f);
    }

    /// <summary>The dynamic meshes' triangles that fight the world's along their slides.</summary>
    private static List<string> Fights(int number)
    {
        var block = MapBaker.Bake(BehaviourData.Data, number)!;
        var map = BehaviourData.Data.LoadMap(number)!;
        var world = Tris(block.World).ToList();
        var fights = new List<string>();
        foreach (var d in block.Dynamic)
        {
            map.TryGetEntityAt(d.Offset, out var e);
            var own = Tris(d.Stages[0] ?? []).ToList();
            foreach (var at in Path(e))
            {
                var hits = own.Count(t => world.Any(w => Fight(t.Moved(at), w)));
                if (hits > 0)
                {
                    fights.Add($"MAP.{number} {map.Names[e.NameIndex]} @0x{d.Offset:X} moved {at}: {hits} triangles");
                }
            }
        }

        return fights;
    }

    /// <summary>A quad facing <paramref name="normal"/> (+X or −X) on the plane x = <paramref name="x"/>,
    /// spanning y and z from <paramref name="from"/> to <paramref name="to"/>, wound to face that way.</summary>
    private static BlockBucket Quad(float x, float normal, float from, float to)
    {
        var packed = Vertex.PackNormal(new Vector3(normal, 0f, 0f));
        Vector3[] corners = normal > 0f
            ? [new(x, from, from), new(x, to, from), new(x, to, to), new(x, from, to)]
            : [new(x, from, from), new(x, from, to), new(x, to, to), new(x, to, from)];
        return new BlockBucket(new BlockKey(null, 1), [.. corners.Select(c => new Vertex(c, Vector2.Zero, Vertex.White, packed))], [0, 1, 2, 0, 2, 3], []);
    }

    private static float XOf(BlockBucket b) => b.Vertices.Average(v => v.Position.X);

    // A door 16 thick flush with the walls on both sides: each side lifts out of its wall; a side
    // with no wall on its plane stays.
    [Fact]
    public void FlushDoorLiftsBothSides()
    {
        BlockBucket[] world = [Quad(0f, -1f, -200f, 200f), Quad(16f, 1f, -200f, 200f)];
        var door = new BlockDynamic(1, [[Quad(0f, -1f, 0f, 100f), Quad(16f, 1f, 0f, 100f), Quad(40f, 1f, 0f, 100f)]]);

        var lifted = MoverFaces.Separate(world, [(door, [Vector3.Zero])])[0].Stages[0]!;

        Assert.Equal(-MoverFaces.Gap, XOf(lifted[0]), 3);
        Assert.Equal(16f + MoverFaces.Gap, XOf(lifted[1]), 3);
        Assert.Equal(40f, XOf(lifted[2]), 3);
    }

    // A door that slides onto a wall's plane only part way along its travel lifts too; one that
    // only touches the wall along an edge does not.
    [Fact]
    public void SlideIntoWallLifts()
    {
        BlockBucket[] world = [Quad(0f, -1f, 200f, 400f)];
        var sliding = new BlockDynamic(1, [[Quad(0f, -1f, 0f, 100f)]]);
        var touching = new BlockDynamic(2, [[Quad(0f, -1f, 100f, 200f)]]);

        var lifted = MoverFaces.Separate(world, [(sliding, [Vector3.Zero, new(0f, 150f, 150f)]), (touching, [Vector3.Zero])]);

        Assert.Equal(-MoverFaces.Gap, XOf(lifted[0].Stages[0]![0]), 3);
        Assert.Equal(0f, XOf(lifted[1].Stages[0]![0]), 3);
    }

    // MAP.254's DORBSEW and MAP.281's 281VENTD sink flush along a wall.
    [BehaviourDataTheory]
    [InlineData(254)]
    [InlineData(281)]
    public void DoorsDoNotShareAPlane(int number) => Assert.Empty(Fights(number));
}
