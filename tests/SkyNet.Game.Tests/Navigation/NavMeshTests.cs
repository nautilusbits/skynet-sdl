using System.Diagnostics;
using System.Numerics;
using RetroEng.Physics;
using SkyNet.Game.Collision;
using SkyNet.Game.Navigation;
using SkyNet.Game.Tests.Behaviour;
using Xunit.Abstractions;

namespace SkyNet.Game.Tests.Navigation;

/// <summary>The soldier's nav mesh on small built worlds: around a wall, up a ledge he can step,
/// not up one he cannot, not across a pit; a saved mesh loads back and knows when it is stale.
/// With the data: bake times on the arenas and a big outdoor map.</summary>
public class NavMeshTests(ITestOutputHelper output)
{
    private static readonly NavAgent Agent = NavAgents.Soldier;
    private static readonly NavBuild Cells = NavAgents.SoldierCells;

    internal static IEnumerable<Triangle> Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
    {
        yield return new Triangle(a, b, c);
        yield return new Triangle(a, c, d);
    }

    /// <summary>A flat floor at y = 0 over x, z in ± <paramref name="half"/>.</summary>
    internal static IEnumerable<Triangle> Floor(float half = 3000f) =>
        Quad(new(-half, 0, -half), new(half, 0, -half), new(half, 0, half), new(-half, 0, half));

    /// <summary>A solid block: its top and four sides.</summary>
    internal static IEnumerable<Triangle> Block(Vector3 min, Vector3 max)
    {
        var (x0, y0, z0, x1, y1, z1) = (min.X, min.Y, min.Z, max.X, max.Y, max.Z);
        return Quad(new(x0, y1, z0), new(x1, y1, z0), new(x1, y1, z1), new(x0, y1, z1))
            .Concat(Quad(new(x0, y0, z0), new(x1, y0, z0), new(x1, y1, z0), new(x0, y1, z0)))
            .Concat(Quad(new(x0, y0, z1), new(x1, y0, z1), new(x1, y1, z1), new(x0, y1, z1)))
            .Concat(Quad(new(x0, y0, z0), new(x0, y0, z1), new(x0, y1, z1), new(x0, y1, z0)))
            .Concat(Quad(new(x1, y0, z0), new(x1, y0, z1), new(x1, y1, z1), new(x1, y1, z0)));
    }

    internal static CollisionWorld World(IEnumerable<Triangle> tris) => new CollisionWorld().Add(new TriangleGrid(tris));

    /// <summary>A wall across −Z at z = −500 from x −600 to 600, 300 high, 20 thick.</summary>
    private static IEnumerable<Triangle> Wall() => Block(new(-600, 0, -510), new(600, 300, -490));

    [Fact]
    public void A_path_goes_round_a_wall()
    {
        var mesh = NavMesh.Build(World(Floor().Concat(Wall())), Agent, Cells);
        var corners = new List<Vector3>();
        var result = new NavQuery(mesh).FindPath(new(0, 0, 0), new(0, 0, -1000), corners);

        Assert.Equal(PathResult.Found, result);
        Assert.True(corners.Count >= 3, $"{corners.Count} corners");
        Assert.Contains(corners, c => MathF.Abs(c.X) >= 600f);
        Assert.Equal(-1000f, corners[^1].Z, 0);
    }

    [Fact]
    public void A_ledge_within_step_height_is_walked_one_above_it_is_not()
    {
        var low = Block(new(-300, 0, -1500), new(300, 60, -900));
        var high = Block(new(-300, 0, -1500), new(300, 150, -900));
        var corners = new List<Vector3>();

        var up = new NavQuery(NavMesh.Build(World(Floor().Concat(low)), Agent, Cells));
        Assert.Equal(PathResult.Found, up.FindPath(new(0, 0, 0), new(0, 60, -1200), corners));
        Assert.InRange(corners[^1].Y, 60f, 60f + 2f * Cells.CellHeight);

        var cut = new NavQuery(NavMesh.Build(World(Floor().Concat(high)), Agent, Cells));
        Assert.Equal(PathResult.Partial, cut.FindPath(new(0, 0, 0), new(0, 150, -1200), corners));
        Assert.True(corners[^1].Y < 10f, $"ends at {corners[^1]}");
    }

    [Fact]
    public void No_path_crosses_a_pit()
    {
        // Two floors with nothing between z −600 and 0.
        var near = Quad(new(-1000, 0, 0), new(1000, 0, 0), new(1000, 0, 1500), new(-1000, 0, 1500));
        var far = Quad(new(-1000, 0, -2000), new(1000, 0, -2000), new(1000, 0, -600), new(-1000, 0, -600));
        var query = new NavQuery(NavMesh.Build(World(near.Concat(far)), Agent, Cells));
        var corners = new List<Vector3>();

        Assert.Equal(PathResult.Partial, query.FindPath(new(0, 0, 700), new(0, 0, -1200), corners));
        Assert.True(corners[^1].Z > -50f, $"ends at {corners[^1]}");
        Assert.True(query.Raycast(new(0, 0, 700), new(0, 0, -1200), out var edge));
        Assert.True(edge.Z > -50f, $"stops at {edge}");
        Assert.False(query.Raycast(new(0, 0, 700), new(500, 0, 1200), out _));
    }

    [Fact]
    public void Nearest_snaps_a_point_in_the_air_onto_the_floor()
    {
        var query = new NavQuery(NavMesh.Build(World(Floor()), Agent, Cells));
        Assert.True(query.Nearest(new(100, 50, 100), new(50, 100, 50), out var on));
        Assert.InRange(on.Y, 0f, 2f * Cells.CellHeight);
        Assert.False(query.Nearest(new(9000, 0, 0), new(50, 100, 50), out _));
    }

    [Fact]
    public void A_saved_mesh_loads_back_and_knows_its_source()
    {
        var world = World(Floor().Concat(Wall()));
        var mesh = NavMesh.Build(world, Agent, Cells);
        using var file = new MemoryStream();
        mesh.Save(file);
        file.Position = 0;
        var back = NavMesh.Load(file)!;

        Assert.True(back.IsFor(world, Agent, Cells));
        Assert.False(back.IsFor(World(Floor()), Agent, Cells));
        Assert.False(back.IsFor(world, Agent with { Radius = 40f }, Cells));
        var a = new List<Vector3>();
        var b = new List<Vector3>();
        new NavQuery(mesh).FindPath(new(0, 0, 0), new(0, 0, -1000), a);
        new NavQuery(back).FindPath(new(0, 0, 0), new(0, 0, -1000), b);
        Assert.Equal(a, b);
        Assert.Null(NavMesh.Load(new MemoryStream([1, 2, 3, 4, 5, 6, 7, 8])));
    }

    [Fact]
    public void Two_bakes_of_one_world_are_the_same()
    {
        var world = World(Floor().Concat(Wall()));
        using var a = new MemoryStream();
        using var b = new MemoryStream();
        NavMesh.Build(world, Agent, Cells).Save(a);
        NavMesh.Build(world, Agent, Cells).Save(b);
        Assert.Equal(a.ToArray(), b.ToArray());
    }

    /// <summary>Bake times: the arenas over their spots (as <see cref="SkyNet.Game.Net.DmNav"/> bakes
    /// them) and a big outdoor campaign map (MAP.220) whole.</summary>
    [BehaviourDataFact]
    public void Bake_times_on_the_arenas_and_an_outdoor_map()
    {
        const float margin = 2048f;
        var maps = SkyNet.Formats.NetLevelList.Load(BehaviourData.Data).Select(l => l.Map).Append(220).Distinct();
        foreach (var map in maps)
        {
            if (LevelCollision.Load(BehaviourData.Data, map) is not { } level)
            {
                continue;
            }

            var cells = Cells;
            var arena = SkyNet.Game.Net.DmArena.Load(BehaviourData.Data, map)!;
            var spots = arena.Spawns.Select(s => s.Feet).Concat(arena.ItemSpots).ToList();
            if (map != 220 && spots.Count > 0)
            {
                cells = Cells with
                {
                    AreaMin = new Vector2(spots.Min(p => p.X), spots.Min(p => p.Z)) - new Vector2(margin),
                    AreaMax = new Vector2(spots.Max(p => p.X), spots.Max(p => p.Z)) + new Vector2(margin),
                };
            }

            var clock = Stopwatch.StartNew();
            var mesh = NavMesh.Build(level.World, Agent, cells);
            var built = clock.Elapsed.TotalMilliseconds;
            using var file = new MemoryStream();
            mesh.Save(file);
            clock.Restart();
            file.Position = 0;
            var back = NavMesh.Load(file)!;
            var fresh = back.IsFor(level.World, Agent, cells);
            var loaded = clock.Elapsed.TotalMilliseconds;
            Assert.True(fresh);
            output.WriteLine($"MAP.{map:D3} {(level.Wld != null ? "outdoor" : "indoor ")} {level.Stats.Triangles,6} tris: " +
                $"{mesh.Outlines().Count(),6} polys, bake {built,6:0} ms, {file.Length / 1024,5} KB, load+check {loaded,4:0} ms");
        }
    }
}