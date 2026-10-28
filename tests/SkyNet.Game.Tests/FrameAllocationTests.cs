using System.Numerics;
using RetroEng.Physics;
using SkyNet.Game.Enemies;
using SkyNet.Game.Hud;
using SkyNet.Game.Net;
using SkyNet.Game.Tests.Behaviour;
using SkyNet.Game.Vehicles;

namespace SkyNet.Game.Tests;

/// <summary>What runs every frame allocates nothing once warm (the 12-bot deathmatch made 14 MB/s
/// of garbage, a gen0 GC a second), the ranking keeps DOS's order, and the deathmatch's meshes are
/// all known up front so the map's load can build them.</summary>
public class FrameAllocationTests
{
    private const int Calls = 100;
    /// <summary>HVYTRRT: a turret on a base, three segments.</summary>
    private const int TurretType = 25;

    /// <summary>Bytes <paramref name="call"/> allocates over <see cref="Calls"/> calls after a warm-up.</summary>
    private static long Allocated(Action call)
    {
        call();
        call();
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < Calls; i++)
        {
            call();
        }

        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    [BehaviourDataFact]
    public void Catching_a_robot_allocates_nothing()
    {
        var robots = new Robots(SkyNet.Data.GameTables.Ais, new EnemyModels(BehaviourData.Data), new CollisionWorld());
        var robot = robots.Add(TurretType, new Vector3(100f, 0f, 0f));
        var shot = new RobotShot();

        Assert.Equal(0, Allocated(() => shot.Catch(robot, null)));
        Assert.Equal(robot.Root.Walk().Count(), shot.Segments.Count);
    }

    [Fact]
    public void Capturing_avatars_and_parked_vehicles_allocates_nothing()
    {
        var avatars = new Dictionary<int, DmAvatar>();
        for (var id = 1; id <= 12; id++)
        {
            var a = new DmAvatar(id, $"BOT{id}", DmClass.Human);
            a.SpawnAt(new Vector3(id * 100f, 0f, 0f), 0f);
            avatars[id] = a;
        }

        var vehicles = new Dictionary<int, DmVehicle>
        {
            [1] = new DmVehicle { Key = 1, Kind = VehicleMode.Jeep },
            [2] = new DmVehicle { Key = 2, Kind = VehicleMode.Hk },
        };
        var frame = new DmFrame();

        Assert.Equal(0, Allocated(() => AvatarRenderer.Capture(avatars.Values, vehicles.Values, frame)));
        Assert.Equal(12, frame.Bodies.Count);
        Assert.Equal(2, frame.Parked.Count);
    }

    [BehaviourDataFact]
    public void Gathering_parked_vehicles_allocates_nothing()
    {
        var vehicles = new Dictionary<int, DmVehicle>
        {
            [1] = new DmVehicle { Key = 1, Kind = VehicleMode.Jeep, Position = new Vector3(1000, 0, 0) },
            [2] = new DmVehicle { Key = 2, Kind = VehicleMode.Hk, Position = new Vector3(-1000, 0, 0) },
        };
        var parked = new ParkedVehicleColliders(() => vehicles.Values, new EnemyModels(BehaviourData.Data));
        var into = new List<Triangle>(4096);
        var box = new Box(new Vector3(-5000f), new Vector3(5000f));

        Assert.Equal(0, Allocated(() =>
        {
            into.Clear();
            parked.Gather(box, into);
        }));
        Assert.NotEmpty(into);
    }

    [Fact]
    public void Ammo_counts_allocate_nothing()
    {
        Assert.Equal(0, Allocated(() =>
        {
            HudFormat.PrimaryCount(500);
            HudFormat.SecondaryCount(321);
        }));
        Assert.Equal("9999", HudFormat.PrimaryCount(12345));
        Assert.Equal("999", HudFormat.SecondaryCount(1000));
        Assert.Null(HudFormat.PrimaryCount(-1));
    }

    [Fact]
    public void Ranking_into_a_list_allocates_nothing()
    {
        var players = Players(13, new Random(1));
        var into = new List<DmPlayer>(16);

        Assert.Equal(0, Allocated(() => DmServer.Rank(players, DmGoal.Points, into)));
        Assert.Equal(13, into.Count);
    }

    [Theory]
    [InlineData(DmGoal.Points)]
    [InlineData(DmGoal.Kills)]
    [InlineData(DmGoal.Lives)]
    [InlineData(DmGoal.Time)]
    public void Ranking_keeps_dos_order_ties_included(DmGoal goal)
    {
        var rng = new Random(7);
        for (var round = 0; round < 50; round++)
        {
            var players = Players(16, rng);
            var expected = Reference(players.Values, goal).Select(p => p.Id).ToArray();
            var into = new List<DmPlayer>();
            DmServer.Rank(players, goal, into);

            Assert.Equal(expected, DmServer.Rank(players.Values, goal).Select(p => p.Id));
            Assert.Equal(expected, into.Select(p => p.Id));
        }
    }

    [BehaviourDataFact]
    public void Every_avatar_gun_and_vehicle_mesh_is_known_before_play()
    {
        var models = new EnemyModels(BehaviourData.Data);
        var wanted = AvatarRenderer.Wanted(models, DmAvatarTable.All).Select(w => (w.Model.File, w.Frame, w.Skin?.To ?? -1)).ToHashSet();

        foreach (var look in DmAvatarTable.All)
        {
            if (models.Get(look.Body) is not { } body)
            {
                continue;
            }

            var skin = look.SkinRemap?.To ?? -1;
            Assert.Contains((body.File, body.Frames - 1, skin), wanted);
            for (var record = 0; record < 12; record++)
            {
                if (models.Get(DmAvatarTable.Gun(look.Number, record)!) is { } gun)
                {
                    Assert.Contains((gun.File, gun.Frames - 1, -1), wanted);
                }
            }
        }

        Assert.Contains(models.Get(AvatarRenderer.ParkedJeep)!.File, wanted.Select(w => w.File));
    }

    /// <summary>DOS's ranking as DmServer.Rank made it with LINQ (stable sorts, the id last).</summary>
    private static List<DmPlayer> Reference(IEnumerable<DmPlayer> players, DmGoal goal)
    {
        var by = goal switch
        {
            DmGoal.Kills => players.OrderByDescending(p => p.Frags).ThenBy(p => p.Deaths).ThenByDescending(p => p.Points),
            DmGoal.Lives => players.OrderBy(p => p.Deaths).ThenByDescending(p => p.Frags).ThenByDescending(p => p.Points),
            _ => players.OrderByDescending(p => p.Points).ThenByDescending(p => p.Frags).ThenBy(p => p.Deaths),
        };
        return [.. by.ThenBy(p => p.Name, StringComparer.Ordinal).ThenBy(p => p.Id)];
    }

    /// <summary>Players with few distinct scores and names, so most keys tie.</summary>
    private static Dictionary<int, DmPlayer> Players(int count, Random rng)
    {
        string[] names = ["A", "B", "b", "BOT"];
        var players = new Dictionary<int, DmPlayer>();
        for (var i = 0; i < count; i++)
        {
            var id = rng.Next(1, 1000);
            while (players.ContainsKey(id))
            {
                id = rng.Next(1, 1000);
            }

            players[id] = new DmPlayer
            {
                Id = id, Name = names[rng.Next(names.Length)], Frags = rng.Next(3), Deaths = rng.Next(3), Points = rng.Next(3),
            };
        }

        return players;
    }
}
