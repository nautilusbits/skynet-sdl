using System.Numerics;
using SkyNet.Game.Behaviour;
using SkyNet.Game.Enemies;
using SkyNet.Game.Tests.Behaviour;

namespace SkyNet.Game.Tests.Enemies;

/// <summary>The marker-path vehicles (AI state 11) on the real maps: MAP.210's cargo truck,
/// MAP.234's pick-up HK and MAP.260's convoy.</summary>
public class PathVehicleTests
{
    private const float Dt = 1f / 35f;
    private const int Lever210 = 0x0c084, TruckLast210 = 0x07bd8, Hk234 = 0x0ea0c;

    private static (LevelBehaviour B, EffectLog Log) Load(int map)
    {
        var log = new EffectLog();
        var m = BehaviourData.Data.LoadMap(map) ?? throw new InvalidOperationException($"no MAP.{map}");
        var b = new LevelBehaviour(m, BehaviourData.Transfrm, MissionProgress.Load(BehaviourData.Data, map / 10 * 10), log);
        b.Start(Driver.Away, Driver.Away);
        log.Drain();
        return (b, log);
    }

    private static class Driver
    {
        public static readonly Vector3 Away = new(-1e7f);
    }

    [BehaviourDataFact]
    public void Map210TruckWaitsForTheLeverThenDrivesToItsEndInTime()
    {
        var (b, log) = Load(210);
        var truck = Assert.Single(b.Paths.All);
        Assert.Equal(46, truck.VehicleType);
        var start = truck.Position;

        // Watched before the lever: it picks its path up, finds it off, and stands.
        for (var t = 0f; t < 3f; t += Dt)
        {
            b.Tick(Dt, new PlayerProbe(truck.Position, truck.Position));
        }

        Assert.True(Vector3.Distance(start, truck.Position) < 1f);
        Assert.False(b.Enabled(truck.Head));

        // The lever's chain switches the path on: the vehicle's marker announces it.
        b.Flip(Lever210);
        Assert.Contains($"path@{truck.Id:x5}", log.Drain());
        Assert.True(b.Enabled(truck.Head));
        Assert.True(b.Enabled(TruckLast210));

        // Picked up afresh (the verifier's order: the player comes to it after the lever).
        truck.Reset();
        var expected = truck.DriveSeconds();
        Assert.InRange(expected, 5f, 300f);
        var t0 = 0f;
        var arrived = -1f;
        for (; t0 < expected * 1.5f + 10f; t0 += Dt)
        {
            b.Tick(Dt, new PlayerProbe(truck.Position, truck.Position));
            if (arrived < 0f && Vector3.Distance(truck.Position, BehaviourModel.Position(Entity(b, TruckLast210))) <= PathVehicle.Reach)
            {
                arrived = t0;
            }
        }

        Assert.InRange(arrived, expected * 0.9f, expected * 1.1f);
        // The path ends on a link of −2: nothing fires, the machine brakes and stands, the path goes off.
        Assert.False(truck.Finished);
        Assert.True(truck.Speed < 1f);
        Assert.DoesNotContain(log.Drain(), f => f.StartsWith("obj", StringComparison.Ordinal) || f.StartsWith("path@", StringComparison.Ordinal));
        Assert.False(b.Enabled(truck.Head));
    }

    [BehaviourDataFact]
    public void Map234HkFliesAtLoadAndItsPathEndCountsTheObjective()
    {
        var (b, log) = Load(234);
        var hk = Assert.Single(b.Paths.All, v => v.Id == Hk234);
        Assert.Equal(51, hk.VehicleType);
        Assert.True(b.Enabled(hk.Head));
        var expected = hk.DriveSeconds();
        var player = hk.Position;
        var t = 0f;
        for (; t < expected * 1.5f + 5f && !hk.Finished; t += Dt)
        {
            b.Tick(Dt, new PlayerProbe(player, player));
        }

        Assert.True(hk.Finished);
        Assert.InRange(t, expected * 0.9f, expected * 1.1f);
        var fx = log.Drain();
        Assert.Contains($"path@{hk.Id:x5}", fx);
        Assert.Contains("obj1", fx);
        Assert.Contains("voice23039", fx);
        Assert.Equal(0, b.Link(hk.Target));             // the link to the end is cut: run once
    }

    [BehaviourDataFact]
    public void Map260ConvoyKeepsDrivingOnceStartedAndTwiceAsFastNearThePlayer()
    {
        var (b, _) = Load(260);
        var convoy = b.Paths.All.Where(v => v.Convoy).ToList();
        Assert.Equal(9, convoy.Count);
        var lead = convoy[0];
        Assert.Equal(0, lead.TicksFrom(Driver.Away));
        Assert.Equal(1, lead.TicksFrom(lead.Position));

        b.Tick(Dt, new PlayerProbe(lead.Position, lead.Position));
        Assert.True(lead.Started);
        Assert.Equal(2, lead.TicksFrom(lead.Position));
        Assert.Equal(1, lead.TicksFrom(Driver.Away));

        // Far away it still drives.
        var at = lead.Position;
        for (var t = 0f; t < 5f; t += Dt)
        {
            b.Tick(Dt, new PlayerProbe(Driver.Away, Driver.Away));
        }

        Assert.True(Vector3.Distance(at, lead.Position) > 50f);
    }

    [BehaviourDataFact]
    public void RobotsStandWhereTheirMachineHasDriven()
    {
        var (b, _) = Load(234);
        var hk = b.Paths.Get(Hk234)!;
        var robots = new Robots(SkyNet.Data.GameTables.Ais, new EnemyModels(BehaviourData.Data), new RetroEng.Physics.CollisionWorld())
        {
            Paths = b.Paths,
        };
        robots.Populate(b.Map);
        var robot = Assert.Single(robots.All, r => r.MarkerOffset == Hk234);
        Assert.True(robot.OnPath);
        for (var t = 0f; t < 3f; t += Dt)
        {
            b.Tick(Dt, new PlayerProbe(hk.Spawn, hk.Spawn));
            robots.Tick(Dt, null);
        }

        Assert.True(Vector3.Distance(robot.Position, hk.Spawn) > 1f);
        Assert.Equal(hk.Position, robot.Position);
        Assert.Equal(hk.Yaw, robot.Yaw);
    }

    private static Formats.MapEntity Entity(LevelBehaviour b, int off) =>
        b.Map.TryGetEntityAt(off, out var e) ? e : throw new InvalidOperationException($"no @{off:x5}");
}
