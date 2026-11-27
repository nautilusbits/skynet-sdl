using System.Numerics;
using RetroEng.Physics;
using SkyNet.Game.Enemies;
using SkyNet.Game.Tests.Behaviour;

namespace SkyNet.Game.Tests.Enemies;

/// <summary>Issue #57. CLASSIC robots never roam while the soldier is not perceived (SKYNET.EXE
/// v1.01): a walker (0x13c600) stands and turns to him until it holds a point of his trail, then
/// follows the trail out of sight; an HK (0x13cb00) always steers at him; a tank (0x13d100) turns
/// only by its script's heading (var 44).</summary>
public class UnseenRobotTests
{
    private const float Dt = 1f / 35f;
    private const float L = 20000f;

    /// <summary>Beyond the 2000 sight cap, inside the 5×5 tick window (cells 0 and 2).</summary>
    private static readonly Vector3 FarSoldier = new(2700f, 0f, 0f);

    private static List<Triangle> Floor() =>
    [
        new(new Vector3(-L, 0f, -L), new Vector3(-L, 0f, L), new Vector3(L, 0f, L)),
        new(new Vector3(-L, 0f, -L), new Vector3(L, 0f, L), new Vector3(L, 0f, -L)),
    ];

    private static Robots Make(List<Triangle> level) =>
        new(SkyNet.Data.GameTables.Ais, new EnemyModels(BehaviourData.Data), new CollisionWorld().Add(new TriangleGrid(level, 256f)), seed: 7)
        {
            HoldFire = true,
        };

    private static int TypeOf(string name) => SkyNet.Data.GameTables.Ais.Types.First(t => t.Name == name).Index;

    /// <summary>The angle between the robot's facing and its way to <paramref name="at"/>, radians.</summary>
    private static float Off(Enemy e, Vector3 at)
    {
        var to = at - e.Position;
        to.Y = 0f;
        return MathF.Acos(Math.Clamp(Vector3.Dot(Vector3.Normalize(to), e.Forward), -1f, 1f));
    }

    /// <summary>0x13c7f9 / 0x13c810: no trail point and no sight, the walker skips its move
    /// (0x13c904) and only turns to the soldier (0x13bf8b, turn at 0x13c83a).</summary>
    [BehaviourDataFact]
    public void AWalkerStandsUntilItSees()
    {
        var robots = Make(Floor());
        var walker = robots.Add(TypeOf("endoskel"), new Vector3(100f, 0f, 0f), yaw: MathF.PI / 2f);
        robots.Tick(Dt, FarSoldier);
        var start = walker.Position;
        var moved = 0f;
        for (var t = 0f; t < 8f; t += Dt)
        {
            robots.Tick(Dt, FarSoldier);
            Assert.False(walker.Sees);
            moved = MathF.Max(moved, Vector3.Distance(walker.Position with { Y = 0f }, start with { Y = 0f }));
        }

        Assert.True(moved < 1f, $"moved {moved}");
        Assert.True(Off(walker, FarSoldier) < 0.1f, $"off {Off(walker, FarSoldier)}");
    }

    /// <summary>0x13c66f: a walker that holds a trail point follows it with no sight test, the next
    /// within 25 (0x13b817): round the end of a wall, to where the soldier went.</summary>
    [BehaviourDataFact]
    public void AWalkerFollowsTheTrailOutOfSight()
    {
        // A wall at x = 200 from z = 0 to z = 3000: the soldier walks east, then north behind it, slowly
        // enough that the 150-point ring (750 units) still holds the way back to the walker.
        var a = new Vector3(200f, -500f, 0f);
        var b = new Vector3(200f, -500f, 3000f);
        var c = new Vector3(200f, 3000f, 3000f);
        var d = new Vector3(200f, 3000f, 0f);
        var level = Floor();
        level.AddRange([new Triangle(a, b, c), new Triangle(a, c, d)]);
        var robots = Make(level);
        var walker = robots.Add(TypeOf("endoskel"), new Vector3(-300f, 0f, 300f), yaw: MathF.PI);
        Vector3[] path = [new(0f, 0f, -200f), new(400f, 0f, -200f), new(400f, 0f, 1500f)];
        var soldier = path[0];
        var leg = 1;
        var unseen = 0f;
        for (var t = 0f; t < 90f; t += Dt)
        {
            if (leg < path.Length)
            {
                var to = path[leg] - soldier;
                var step = 80f * Dt;
                soldier = to.Length() <= step ? path[leg++] : soldier + Vector3.Normalize(to) * step;
            }

            robots.Tick(Dt, soldier);
            unseen += walker.Sees ? 0f : Dt;
        }

        Assert.True(unseen > 3f, $"unseen {unseen} s");
        var flat = (walker.Position - soldier) with { Y = 0f };
        Assert.True(flat.Length() < 300f, $"left {flat.Length()}");
    }

    /// <summary>0x13cb5e: the HK steers at the soldier with no sight test (sight only gates its
    /// fire, 0x13cd9d).</summary>
    [BehaviourDataFact]
    public void AnHkFliesAtAnUnseenSoldier()
    {
        var robots = Make(Floor());
        var soldier = new Vector3(-3050f, 0f, 0f);   // cells −3 and −1
        var hk = robots.Add(TypeOf("hk_ftr"), new Vector3(-10f, 600f, 0f), yaw: 1f);
        for (var t = 0f; t < 1f; t += Dt)
        {
            robots.Tick(Dt, soldier);
            Assert.False(hk.Sees);
        }

        Assert.True(Off(hk, soldier) < 0.35f, $"off {Off(hk, soldier)}");
    }

    /// <summary>0x13d100: a tank's heading is its script's (var 44, 0x13d346); unseen, the mantnk
    /// script sets speed 0 and no heading, so it neither moves nor turns.</summary>
    [BehaviourDataFact]
    public void ATankHoldsItsHeadingUnseen()
    {
        var robots = Make(Floor());
        var tank = robots.Add(TypeOf("mantnk"), new Vector3(100f, 0f, 0f), yaw: 1f);
        robots.Tick(Dt, FarSoldier);
        var start = tank.Position;
        for (var t = 0f; t < 6f; t += Dt)
        {
            robots.Tick(Dt, FarSoldier);
            Assert.False(tank.Sees);
        }

        Assert.True(MathF.Abs(tank.Yaw - 1f) < 1e-3f, $"yaw {tank.Yaw}");
        Assert.True(Vector3.Distance(tank.Position with { Y = 0f }, start with { Y = 0f }) < 1f);
    }
}
