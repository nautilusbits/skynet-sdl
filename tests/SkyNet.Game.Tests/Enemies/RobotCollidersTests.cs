using System.Numerics;
using RetroEng.Loop;
using RetroEng.Physics;
using SkyNet.Game.Campaign;
using SkyNet.Game.Enemies;
using SkyNet.Game.Player;
using SkyNet.Game.Tests.Behaviour;

namespace SkyNet.Game.Tests.Enemies;

/// <summary>Robots block the soldier by their meshes (SKYNET.EXE v1.01 0x13a95d / 0x13ac3e): a
/// standing robot stops him, an HK above his head does not, dead and hidden ones never; the robots'
/// own world stays robot-free; a machine that drives into him cannot seal him in a wall.</summary>
public class RobotCollidersTests
{
    private const float Dt = 1f / 35f;
    private static readonly PlayerActions Keys = new(Forward: 0, Back: 1, Left: 2, Right: 3, Run: 4, Jump: 5, Crouch: 6);

    private sealed class Rig
    {
        public required Robots Robots;
        public required RobotColliders Colliders;
        public required CollisionWorld World;
        public required PlayerController Player;

        public void Walk(float seconds, params int[] actions)
        {
            ulong down = 0;
            foreach (var a in actions)
            {
                down |= TickInput.Bit(a);
            }

            for (var t = 0f; t < seconds; t += Dt)
            {
                Player.Position = Colliders.Free(Player.Body, Player.Position, World);
                Player.Step(new TickInput(down, 0, 0, 0, 0, 0, 0, 0), Dt, Keys);
            }
        }

        /// <summary>The robots alone touch the body where it stands.</summary>
        public bool InsideRobot() => new CollisionWorld().Add(Colliders).Overlaps(Player.Body, Player.Position);
    }

    private static IEnumerable<Triangle> Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
    {
        yield return new Triangle(a, b, c);
        yield return new Triangle(a, c, d);
    }

    private static IEnumerable<Triangle> Floor() =>
        Quad(new(-4000, 0, -4000), new(4000, 0, -4000), new(4000, 0, 4000), new(-4000, 0, 4000));

    /// <summary>A soldier standing at the origin on a flat floor (plus <paramref name="extra"/>), the
    /// robots on a robot-free world of the same map, the player's world = map + robots.</summary>
    private static Rig Make(params IEnumerable<Triangle>[] extra)
    {
        var map = new TriangleGrid(Floor().Concat(extra.SelectMany(e => e)), 256f);
        var robots = new Robots(SkyNet.Data.GameTables.Ais, new EnemyModels(BehaviourData.Data), new CollisionWorld().Add(map));
        var colliders = new RobotColliders(() => robots);
        var world = new CollisionWorld().Add(map).Add(colliders);
        var player = new PlayerController(world);
        player.Spawn(new Vector3(0f, 1f, 0f), 0f);
        var rig = new Rig { Robots = robots, Colliders = colliders, World = world, Player = player };
        rig.Walk(0.5f);
        Assert.True(player.OnFloor);
        return rig;
    }

    private static int TypeOf(string name) => SkyNet.Data.GameTables.Ais.Types.First(t => t.Name == name).Index;

    /// <summary>A robot whose lowest point is <paramref name="bottom"/> above the floor, 400 ahead (−Z).</summary>
    private static Enemy Place(Rig rig, string name, float bottom, float z = -400f)
    {
        var r = rig.Robots.Add(TypeOf(name), Vector3.Zero);
        var low = r.BoundsCentre.Y - r.BodyHeight * 0.5f;
        r.Position = new Vector3(0f, bottom - low, z);
        r.UpdateTransforms();
        return r;
    }

    [BehaviourDataFact]
    public void WalkingIntoAStandingRobotStopsAtItsMesh()
    {
        // A cargo truck (308 long, 221 high): too wide to slide round, too high to climb.
        var rig = Make();
        var robot = Place(rig, "boxtrck", 0f);
        rig.Walk(4f, Keys.Forward);

        Assert.True(rig.Player.Position.Z > robot.Position.Z, $"walked through: {rig.Player.Position}");
        Assert.True(rig.Player.Position.Z < -150f, $"stopped short: z {rig.Player.Position.Z}");
        Assert.False(rig.InsideRobot());
    }

    [BehaviourDataFact]
    public void AnHkAboveTheHeadLetsHimPassOneAtBodyHeightBlocks()
    {
        var high = Make();
        Place(high, "hk_ftr", PlayerController.BodyHeight + 40f);
        high.Walk(4f, Keys.Forward);
        Assert.True(high.Player.Position.Z < -800f, $"blocked under the HK: z {high.Player.Position.Z}");

        var low = Make();
        Place(low, "hk_ftr", 20f);
        low.Walk(4f, Keys.Forward);
        Assert.True(low.Player.Position.Z > -600f, $"walked through the HK: z {low.Player.Position.Z}");
    }

    [BehaviourDataFact]
    public void DeadHiddenAndRiddenRobotsDoNotBlock()
    {
        foreach (var make in new Action<Enemy>[] { r => r.Die(), r => r.HideUntilSpawned() })
        {
            var rig = Make();
            make(Place(rig, "hvytnk", 0f));
            rig.Walk(4f, Keys.Forward);
            Assert.True(rig.Player.Position.Z < -800f, $"blocked: z {rig.Player.Position.Z}");
        }

        // MAP.234's pick-up HK: a flying path machine is what lifts him, not a wall.
        var ride = Make();
        Place(ride, "hk_ftr", 20f).OnPath = true;
        ride.Walk(4f, Keys.Forward);
        Assert.True(ride.Player.Position.Z < -800f, $"blocked by the ride: z {ride.Player.Position.Z}");
    }

    [BehaviourDataFact]
    public void TheRobotsOwnWorldStaysRobotFree()
    {
        var rig = Make();
        var robot = Place(rig, "hvytnk", 0f);
        var eye = new Vector3(0f, 40f, 0f);
        var dir = Vector3.Normalize(robot.Position with { Y = 40f } - eye);

        Assert.True(rig.World.Raycast(eye, dir, 1000f, out _));
        Assert.False(rig.Robots.World.Raycast(eye, dir, 1000f, out _));
    }

    [BehaviourDataFact]
    public void TheSessionsSharedWorldHasNoRobotsThePlayersHas()
    {
        var session = new GameSession(BehaviourData.Data);
        Assert.True(session.EnterMap(210, 0));
        Assert.NotNull(session.Robots);
        Assert.Same(session.World, session.Robots!.World);
        Assert.DoesNotContain(session.World.Sources, s => s is RobotColliders);
        Assert.Contains(session.PlayerWorld.Sources, s => s is RobotColliders);
    }

    [BehaviourDataFact]
    public void ATruckDrivingIntoHimAgainstAWallDoesNotSealHimIn()
    {
        // A wall across +X at x = 60, the truck driving +X into him from the left.
        var wallX = 60f;
        var rig = Make(Quad(new(wallX, -10, 2000), new(wallX, -10, -2000), new(wallX, 400, -2000), new(wallX, 400, 2000)));
        var truck = Place(rig, "boxtrck", 0f, 0f);
        truck.Position = truck.Position with { X = -600f };
        for (var t = 0f; t < 6f; t += Dt)
        {
            if (truck.Position.X < wallX)
            {
                truck.Position += new Vector3(160f * Dt, 0f, 0f);
                truck.UpdateTransforms();
            }

            rig.Walk(Dt);
            Assert.True(rig.Player.Position.X < wallX - PlayerController.BodyRadius + 1f, $"in the wall: x {rig.Player.Position.X}");
        }

        // Pinned inside the parked truck: he walks out along the wall, and then it blocks again.
        rig.Walk(4f, Keys.Forward);
        Assert.False(rig.InsideRobot(), $"still inside at {rig.Player.Position}");
        var outside = rig.Player.Position.Z;
        rig.Walk(4f, Keys.Back);
        Assert.True(rig.Player.Position.Z < truck.Position.Z, $"walked back into the truck: z {rig.Player.Position.Z} (left at {outside})");
        Assert.False(rig.InsideRobot());
    }
}
