using System.Numerics;
using RetroEng.Physics;
using SkyNet.Game.Collision;

namespace SkyNet.Game.Tests.Mods;

/// <summary>Where a sprite drawn as a model stands indoors: the floor under its record, not the
/// billboard's foot 16 below it (paper litter sits 8 above the floor and sank out of sight).</summary>
public class SpriteFloorTests
{
    private const float L = 2000f;

    /// <summary>A quad at height <paramref name="y"/> over x, z in [a, b], facing up or down.</summary>
    private static IEnumerable<Triangle> Quad(float y, float a, float b, bool up = true)
    {
        Vector3 p0 = new(a, y, a), p1 = new(a, y, b), p2 = new(b, y, b), p3 = new(b, y, a);
        return up ? [new(p0, p1, p2), new(p0, p2, p3)] : [new(p0, p2, p1), new(p0, p3, p2)];
    }

    private static CollisionWorld World(params IEnumerable<Triangle>[] parts) =>
        new CollisionWorld().Add(new TriangleGrid([.. parts.SelectMany(p => p)], 256f));

    [Fact]
    public void Paper_eight_above_the_floor_stands_on_it()
    {
        var world = World(Quad(0f, -L, L));
        Assert.Equal(0f, SpriteFloor.Below(world, new Vector3(10f, 8f, 10f))!.Value, 3);
    }

    [Fact]
    public void A_slab_under_the_record_wins_over_the_floor()
    {
        var world = World(Quad(0f, -L, L), Quad(40f, -100f, 100f));
        Assert.Equal(40f, SpriteFloor.Below(world, new Vector3(0f, 48f, 0f))!.Value, 3);
    }

    [Fact]
    public void A_face_pointing_down_is_not_a_floor()
    {
        var world = World(Quad(0f, -L, L), Quad(5f, -100f, 100f, up: false));
        Assert.Equal(0f, SpriteFloor.Below(world, new Vector3(0f, 8f, 0f))!.Value, 3);
    }

    [Fact]
    public void No_floor_in_reach_keeps_the_billboard_foot()
    {
        var world = World(Quad(0f, -L, L));
        Assert.Null(SpriteFloor.Below(world, new Vector3(0f, 100f, 0f)));
    }

    [Fact]
    public void A_floor_a_little_above_the_record_still_counts()
    {
        var world = World(Quad(0f, -L, L));
        Assert.Equal(0f, SpriteFloor.Below(world, new Vector3(0f, -1f, 0f))!.Value, 3);
    }
}
