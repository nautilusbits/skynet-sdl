using System.Numerics;
using RetroEng.Physics;
using SkyNet.Game.Campaign;
using SkyNet.Game.Enemies;
using SkyNet.Game.Tests.Behaviour;
using SkyNet.Game.Weapons;
using Xunit.Abstractions;

namespace SkyNet.Game.Tests.Enemies;

/// <summary>ROBOT AI IMPROVED's walkers on a nav mesh (<see cref="RobotNav"/>): small built worlds
/// where the walk alone is slow or lost, and CLASSIC untouched by a mesh.</summary>
public class RobotNavTests(ITestOutputHelper output)
{
    private const float Dt = 1f / 60f;
    private const int RaptorType = 32;

    // ---------------------------------------------------------------- built worlds

    /// <summary>Both windings of a quad (solid from either side).</summary>
    private static IEnumerable<Triangle> Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
    {
        yield return new Triangle(a, b, c);
        yield return new Triangle(a, c, d);
        yield return new Triangle(a, c, b);
        yield return new Triangle(a, d, c);
    }

    private static IEnumerable<Triangle> Box(Vector3 a, Vector3 b)
    {
        Vector3 P(int i) => new((i & 1) != 0 ? b.X : a.X, (i & 2) != 0 ? b.Y : a.Y, (i & 4) != 0 ? b.Z : a.Z);
        int[][] faces = [[0, 1, 3, 2], [4, 6, 7, 5], [0, 4, 5, 1], [2, 3, 7, 6], [0, 2, 6, 4], [1, 5, 7, 3]];
        return faces.SelectMany(f => Quad(P(f[0]), P(f[1]), P(f[2]), P(f[3])));
    }

    /// <summary>A floor at Y 0 (± 4 000) and the given triangles.</summary>
    private static CollisionWorld World(IEnumerable<Triangle> rest)
    {
        const float L = 4000f;
        var floor = new[]
        {
            new Triangle(new Vector3(-L, 0f, -L), new Vector3(-L, 0f, L), new Vector3(L, 0f, L)),
            new Triangle(new Vector3(-L, 0f, -L), new Vector3(L, 0f, L), new Vector3(L, 0f, -L)),
        };
        return new CollisionWorld().Add(new TriangleGrid([.. floor, .. rest], 256f));
    }

    /// <summary>A cup of knee-high walls (45: over a raptor's step, under its eye) round the start,
    /// open at the back; the soldier in sight beyond its front.</summary>
    private static CollisionWorld Cup() => World(
        Box(new Vector3(-600f, 0f, -310f), new Vector3(600f, 45f, -300f))
            .Concat(Box(new Vector3(-610f, 0f, -310f), new Vector3(-600f, 45f, 400f)))
            .Concat(Box(new Vector3(600f, 0f, -310f), new Vector3(610f, 45f, 400f))));

    /// <summary>A platform 150 high (no step) with a ramp up its +X side; the soldier on it.</summary>
    private static CollisionWorld Platform()
    {
        var top = new Vector3(300f, 150f, 0f);
        var ramp = Quad(top with { Z = -1000f }, top with { Z = -800f }, new Vector3(700f, 0f, -800f), new Vector3(700f, 0f, -1000f))
            .Concat([new Triangle(new Vector3(300f, 0f, -1000f), new Vector3(300f, 150f, -1000f), new Vector3(700f, 0f, -1000f))])
            .Concat([new Triangle(new Vector3(300f, 0f, -800f), new Vector3(700f, 0f, -800f), new Vector3(300f, 150f, -800f))]);
        return World(Box(new Vector3(-300f, 0f, -1100f), new Vector3(300f, 150f, -500f)).Concat(ramp));
    }

    private static Robots Setup(CollisionWorld world, RobotAi ai, Vector3 feet)
    {
        var data = BehaviourData.Data;
        var weapons = new WeaponSystem(WeaponTable.Load(data), world) { Player = new Soldier { Feet = feet } };
        return new Robots(SkyNet.Data.GameTables.Ais, new EnemyModels(data), world, weapons, seed: 3) { Ai = ai, HoldFire = true };
    }

    /// <summary>Seconds until the raptor stands by the soldier at <paramref name="feet"/> (flat within
    /// its stop and a margin, its feet at his height); null: never in <paramref name="limit"/>.</summary>
    private (float? Time, int Unstuck) Chase(CollisionWorld world, Vector3 feet, bool nav, float limit)
    {
        var robots = Setup(world, RobotAi.Improved, feet);
        var raptor = robots.Add(RaptorType, Vector3.Zero);
        if (nav)
        {
            robots.Nav = RobotNav.Bake(world, robots, [feet], null);
        }

        var near = (raptor.Type?.Near ?? 100) + 80f;
        for (var t = 0; t < limit / Dt; t++)
        {
            robots.Tick(Dt, feet);
            var flat = Vector2.Distance(new Vector2(raptor.Position.X, raptor.Position.Z), new Vector2(feet.X, feet.Z));
            if (flat <= near && MathF.Abs(raptor.Position.Y + raptor.FootOffset - feet.Y) < 20f)
            {
                return (t * Dt, raptor.Unstuck);
            }
        }

        output.WriteLine($"{(nav ? "nav" : "walk")}: ended at {raptor.Position}");
        return (null, raptor.Unstuck);
    }

    private void Compare(CollisionWorld world, Vector3 feet, float limit)
    {
        var (walk, walkStuck) = Chase(world, feet, nav: false, limit);
        var (nav, navStuck) = Chase(world, feet, nav: true, limit);
        output.WriteLine($"walk: {walk?.ToString("0.0") ?? "never"} s, unstuck {walkStuck}; nav: {nav?.ToString("0.0") ?? "never"} s, unstuck {navStuck}");

        Assert.NotNull(nav);
        Assert.True(walk == null || nav < walk, $"nav {nav:0.0} s, walk {walk:0.0} s");
    }

    // ---------------------------------------------------------------- IMPROVED with a mesh

    [BehaviourDataFact]
    public void AWalkerOnTheMeshLeavesTheCupForTheSoldier() => Compare(Cup(), new Vector3(0f, 0f, -1200f), 90f);

    [BehaviourDataFact]
    public void AWalkerOnTheMeshTakesTheRampUp() => Compare(Platform(), new Vector3(0f, 150f, -600f), 90f);

    /// <summary>Off the mesh (the mesh baked far away): the walk as without one.</summary>
    [BehaviourDataFact]
    public void OffTheMeshAWalkerWalksAsBefore()
    {
        var world = Cup();
        var feet = new Vector3(0f, 0f, -1200f);
        var hashes = new List<int>();
        foreach (var nav in new[] { false, true })
        {
            var robots = Setup(world, RobotAi.Improved, feet);
            var raptor = robots.Add(RaptorType, Vector3.Zero);
            if (nav)
            {
                var far = new Robots(SkyNet.Data.GameTables.Ais, robots.Models, world);
                far.Add(RaptorType, new Vector3(3500f, 0f, 3500f));
                robots.Nav = RobotNav.Bake(world, far, [], null);
            }

            var h = new HashCode();
            for (var t = 0; t < 60 * 10; t++)
            {
                robots.Tick(Dt, feet);
                h.Add(raptor.Position);
                h.Add(raptor.Yaw);
            }

            hashes.Add(h.ToHashCode());
        }

        Assert.Equal(hashes[0], hashes[1]);
    }

    // ---------------------------------------------------------------- CLASSIC

    /// <summary>A mesh given to CLASSIC's robots changes nothing: DOS's golden still; IMPROVED's run does change.</summary>
    [BehaviourDataFact]
    public void ClassicIgnoresTheMesh()
    {
        static void Nav(Robots robots, CollisionWorld world) => robots.Nav = RobotNav.Bake(world, robots, [], null);

        Assert.Equal(DosParityTests.RobotGolden, DosParityTests.RobotRun(RobotAi.Classic, Nav).Hash);
        Assert.NotEqual(DosParityTests.RobotRun(RobotAi.Improved).Hash, DosParityTests.RobotRun(RobotAi.Improved, Nav).Hash);
    }

    /// <summary>A map entered on CLASSIC bakes no mesh; IMPROVED does, and switching to it bakes one.</summary>
    [BehaviourDataFact]
    public void OnlyImprovedBakesTheMesh()
    {
        var session = new GameSession(BehaviourData.Data) { RobotAi = RobotAi.Classic };
        session.StartMission(MissionTable.SkyNet.ByNumber(7)!.Value);
        Assert.True(session.EnterMap(271, 0));
        Assert.Null(session.Robots!.Nav);

        session.RobotAi = RobotAi.Improved;
        Assert.NotNull(session.Robots.Nav);
        Assert.NotEmpty(session.Robots.Nav!.Meshes);
    }
}
