using System.Numerics;
using SkyNet.Formats;
using SkyNet.Game.Behaviour;
using SkyNet.Game.Collision;
using SkyNet.Game.Enemies;
using SkyNet.Game.Tests.Behaviour;

namespace SkyNet.Game.Tests.Enemies;

/// <summary>DOS stands a new robot h + 2·sign h from the surface FUN_00138d00 finds near its marker
/// (FUN_0012a52c): the turrets whose markers sit at their base (MAP.220's guntwr1, 70 u deep before;
/// Future Shock MAP.141's hvytrrt) stand on the floor instead of in it, guns hung under a ceiling go up
/// to it when its top is in reach, and a path machine starts where its robot was stood.</summary>
public class TurretPlacementTests
{
    private static Robots Populate(GameData data, int number) => PopulateWithPaths(data, number).Robots;

    private static (Robots Robots, LevelBehaviour Behaviour) PopulateWithPaths(GameData data, int number)
    {
        var map = data.LoadMap(number)!;
        var behaviour = new LevelBehaviour(map, BehaviourModel.LoadTransfrm(data), new MissionProgress(5));
        var (world, _) = LevelCollision.Build(data, map, map.IsOutdoor ? data.LoadWld(number) : null, behaviour.IsDynamic);
        world.Add(behaviour.BuildColliders(data));
        var robots = new Robots(SkyNet.Data.GameTables.Ais, new EnemyModels(data), world) { Paths = behaviour.Paths };
        robots.Populate(map);
        robots.Tick(1f / 35f, null);
        return (robots, behaviour);
    }

    private static GameData? FutureShock() =>
        GameDataLocator.OtherGameDir(BehaviourData.Dir!) is { } fs ? GameData.Open(fs, BehaviourData.Dir) : null;

    private static Enemy At(Robots robots, int marker) => robots.All.Single(r => r.MarkerOffset == marker);

    /// <summary>The stationary robots with a positive stand height: none has a floor between its feet
    /// and its origin, and at least <paramref name="expectOnFloor"/> stand 2 u above one (a few stand
    /// on a pillar edge or over a gap, with nothing straight under their centre).</summary>
    private static void AssertStanding(Robots robots, int expectOnFloor)
    {
        var standing = robots.All.Where(r => r.IsStationary && r.Type?.Record[6] is > 0 and < 0x8000_0000).ToList();
        Assert.All(standing, r =>
        {
            var feet = r.Position.Y + r.FootOffset;
            var buried = robots.World.Raycast(r.Position, -Vector3.UnitY, r.Position.Y - (feet + 8f), out var hit) && hit.Normal.Y >= 0.5f;
            Assert.False(buried, $"{r.Name} at {r.Position}: floor {hit.Point.Y - feet:F0} u above its feet");
        });
        var onFloor = standing.Count(r =>
        {
            var feet = r.Position.Y + r.FootOffset;
            return robots.World.Raycast(r.Position with { Y = feet + 8f }, -Vector3.UnitY, 32f, out var under)
                && feet - under.Point.Y is >= 0f and <= 4f;
        });
        Assert.True(onFloor >= expectOnFloor, $"{onFloor} of {standing.Count} on a floor");
    }

    [BehaviourDataFact]
    public void Map220GunTowersStandOnTheirFloor() => AssertStanding(Populate(BehaviourData.Data, 220), 19);

    [BehaviourDataFact]
    public void FutureShockMap141TurretsStandOnTheirFloor()
    {
        if (FutureShock() is not { } data)
        {
            return;
        }

        AssertStanding(Populate(data, 141), 6);
    }

    [BehaviourDataFact]
    public void AMarkerFarFromTheFloorKeepsItsY()
    {
        var robots = Populate(BehaviourData.Data, 220);
        var tower = robots.All.First(r => r.Name == "guntwr1");
        var high = tower.Position + new Vector3(0f, 400f, 0f);
        Assert.Equal(high, robots.StandOnFloor(tower.TypeIndex, high));                  // floor out of the window
        var nearFloor = tower.Position + new Vector3(0f, -70f, 0f);
        Assert.Equal(tower.Position.Y, robots.StandOnFloor(tower.TypeIndex, nearFloor).Y, 1f);
    }

    /// <summary>Only faces whose normal points up count (0x138ebf): MAP.281's sentries hang 34 u under
    /// a single ceiling face with no top within 0x50 and stay at their markers (marker Y + 0x10).</summary>
    [BehaviourDataFact]
    public void Map281CeilingSentriesStayAtTheirMarkers()
    {
        var robots = Populate(BehaviourData.Data, 281);
        Assert.Equal(new Vector3(16381f, 224f, -4828f), At(robots, 0x02c0c).Position);
        Assert.Equal(new Vector3(17595f, 480f, -4728f), At(robots, 0x03293).Position);
    }

    /// <summary>A hung gun (h &lt; 0) goes |h| + 2 under the top of the ceiling slab it hangs from:
    /// MAP.285's hvytrrt2 (h −28) under the 8 u slab (382-390) rises from 329 to 360, flush with it.</summary>
    [BehaviourDataFact]
    public void Map285HungGunsGoUpToTheirCeiling()
    {
        var robots = Populate(BehaviourData.Data, 285);
        Assert.Equal(360f, At(robots, 0x02cc4).Position.Y, 0.5f);
        Assert.Equal(360f, At(robots, 0x02ce8).Position.Y, 0.5f);
        Assert.Equal(649f, At(robots, 0x02d0c).Position.Y, 0.5f);               // no slab top in reach
    }

    /// <summary>A ceiling's underside faces down and is not a floor: MAP.254's flencers (h 80) under a
    /// ceiling 56 u up are built 82 u above the floor 72 u down, not on the ceiling (their walk
    /// takes over from there).</summary>
    [BehaviourDataFact]
    public void Map254WalkersUnderALowCeilingStandOnTheFloor()
    {
        var robots = Populate(BehaviourData.Data, 254);
        var marker = new Vector3(15784f, 272f, -17843f);                       // @03d9b, Y + 0x10
        Assert.Equal(200f, robots.StandSurface(marker), 0.5f);
        Assert.Equal(282f, robots.StandOnFloor(At(robots, 0x03d9b).TypeIndex, marker).Y, 0.5f);
    }

    /// <summary>A path machine is built like any robot: Future Shock MAP.100's hvytnk (h 20) start 22 u
    /// above the road (886), not at marker + 0x10 (930); a truck with no stand height keeps its marker.</summary>
    [BehaviourDataFact]
    public void PathMachinesStartWhereTheirRobotWasStood()
    {
        var (robots, behaviour) = PopulateWithPaths(BehaviourData.Data, 210);
        var truck = behaviour.Paths.Get(0x078d3)!;
        Assert.Equal(786f, truck.Position.Y, 0.5f);
        Assert.Equal(truck.Position, At(robots, 0x078d3).Position);

        if (FutureShock() is not { } fs)
        {
            return;
        }

        (robots, behaviour) = PopulateWithPaths(fs, 100);
        foreach (var marker in new[] { 0x3558a, 0x35566, 0x355d2, 0x355ae })
        {
            var tank = behaviour.Paths.Get(marker)!;
            Assert.Equal(908f, tank.Position.Y, 0.5f);
            Assert.Equal(tank.Spawn, tank.Position);
            Assert.Equal(tank.Position, At(robots, marker).Position);
        }
    }
}
