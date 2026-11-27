using System.Numerics;
using RetroEng.Physics;
using SkyNet.Game.Enemies;
using SkyNet.Game.Hud;
using SkyNet.Game.Net;
using SkyNet.Game.Tests.Behaviour;
using SkyNet.Game.Vehicles;
using SkyNet.Game.Weapons;

namespace SkyNet.Game.Tests;

/// <summary>What runs every frame allocates nothing once warm (the 12-bot deathmatch made 14 MB/s
/// of garbage, a gen0 GC a second), the ranking keeps DOS's order, and the deathmatch's meshes are
/// all known up front so the map's load can build them.</summary>
public class FrameAllocationTests
{
    private const int Calls = 100;
    /// <summary>HVYTRRT: a turret on a base, three segments.</summary>
    private const int TurretType = 25;
    private const int RaptorType = 32;

    /// <summary>A window measured again when it allocated: the full suite showed one-off allocations
    /// of a few KB in a single call (no GC, same thread) in four different tests, code that allocates
    /// nothing; none in 7 runs with tiered JIT off, so the runtime's tier-up pays them on the calling
    /// thread. A real per-call allocation shows in every window.</summary>
    private const int Windows = 3;

    /// <summary>Bytes <paramref name="call"/> allocates over <see cref="Calls"/> calls after a warm-up
    /// (the least of <see cref="Windows"/> windows).</summary>
    private static long Allocated(Action call)
    {
        call();
        call();
        var least = long.MaxValue;
        for (var w = 0; w < Windows && least != 0; w++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < Calls; i++)
            {
                call();
            }

            least = Math.Min(least, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        return least;
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

    /// <summary>IMPROVED's hearing, alarm and patrol: two raptors on a floor, a shot heard each tick.</summary>
    [BehaviourDataFact]
    public void Improved_hearing_and_patrol_allocate_nothing()
    {
        const float L = 5000f;
        var world = new CollisionWorld().Add(new TriangleGrid(
        [
            new(new Vector3(-L, 0f, -L), new Vector3(-L, 0f, L), new Vector3(L, 0f, L)),
            new(new Vector3(-L, 0f, -L), new Vector3(L, 0f, L), new Vector3(L, 0f, -L)),
        ], 256f));
        var robots = new Robots(SkyNet.Data.GameTables.Ais, new EnemyModels(BehaviourData.Data), world) { Ai = RobotAi.Improved, HoldFire = true };
        robots.Add(RaptorType, Vector3.Zero);
        robots.Add(RaptorType, new Vector3(300f, 0f, 0f));
        var feet = new Vector3(0f, 0f, 4000f);
        for (var i = 0; i < 600; i++)
        {
            robots.Tick(1f / 60f, feet);
        }

        Assert.Equal(0, Allocated(() =>
        {
            robots.Noise(new Vector3(200f, 0f, 200f), NoiseKind.Shot);
            robots.Tick(1f / 60f, feet);
        }));
    }

    /// <summary>IMPROVED's witness and search: a raptor hit every tick, its neighbour witnessing it
    /// and searching on its leads.</summary>
    [BehaviourDataFact]
    public void Improved_witness_and_search_allocate_nothing()
    {
        const float L = 5000f;
        var world = new CollisionWorld().Add(new TriangleGrid(
        [
            new(new Vector3(-L, 0f, -L), new Vector3(-L, 0f, L), new Vector3(L, 0f, L)),
            new(new Vector3(-L, 0f, -L), new Vector3(L, 0f, L), new Vector3(L, 0f, -L)),
        ], 256f));
        var robots = new Robots(SkyNet.Data.GameTables.Ais, new EnemyModels(BehaviourData.Data), world) { Ai = RobotAi.Improved, HoldFire = true };
        var hit = robots.Add(RaptorType, Vector3.Zero, MathF.PI);
        var witness = robots.Add(RaptorType, new Vector3(800f, 0f, 0f), MathF.PI / 2f);
        var feet = new Vector3(0f, 0f, 3000f);
        hit.TakeDamage(new Damage(0f, DamageKind.Direct, hit.Centre, true));
        for (var i = 0; i < 60 * 15; i++)
        {
            robots.Tick(1f / 60f, feet);
        }

        Assert.Equal(RobotAlert.Search, witness.Alertness);
        Assert.Equal(0, Allocated(() => robots.Tick(1f / 60f, feet)));
        Assert.Equal(0, Allocated(() =>
        {
            hit.TakeDamage(new Damage(0f, DamageKind.Direct, hit.Centre, true));
            robots.Tick(1f / 60f, feet);
        }));
    }

    /// <summary>IMPROVED's combat: five raptors and a plasma turret firing at him (no weapons: the
    /// shots go nowhere), tokens dealt round, one hurt raptor looking for cover every tick.</summary>
    [BehaviourDataFact]
    public void Improved_combat_allocates_nothing()
    {
        const float L = 5000f;
        const int PlasmaTurretType = 59;
        var world = new CollisionWorld().Add(new TriangleGrid(
        [
            new(new Vector3(-L, 0f, -L), new Vector3(-L, 0f, L), new Vector3(L, 0f, L)),
            new(new Vector3(-L, 0f, -L), new Vector3(L, 0f, L), new Vector3(L, 0f, -L)),
        ], 256f));
        var robots = new Robots(SkyNet.Data.GameTables.Ais, new EnemyModels(BehaviourData.Data), world) { Ai = RobotAi.Improved };
        var feet = new Vector3(0f, 0f, -900f);
        for (var i = 0; i < 5; i++)
        {
            var at = new Vector3(-600f + i * 300f, 0f, 0f);
            robots.Add(RaptorType, at, MathF.Atan2(-(feet.X - at.X), -(feet.Z - at.Z)));
        }

        robots.Add(PlasmaTurretType, new Vector3(0f, 100f, 0f));
        for (var i = 0; i < 120; i++)
        {
            robots.Tick(1f / 60f, feet);
        }

        var hurt = robots.All[0];
        hurt.TakeDamage(new Damage(hurt.MaxHp * 0.7f, DamageKind.Direct, hurt.Centre, true));
        // Every tick searches for cover from scratch (an open floor has none: each candidate is tried).
        Assert.Equal(0, Allocated(() =>
        {
            hurt.SeekCoverNow();
            robots.Tick(1f / 60f, feet);
        }));
        Assert.True(robots.All.Sum(r => r.ShotsFired) > 0);
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

    /// <summary>A deathmatch bot's thinking (phase 3 of docs/plans/smarter-ai.md: perception, nodes,
    /// goal picks, its shots in flight) fighting with a ray gun and with bolts, then roaming to
    /// items; off the nav mesh (DotRecast's queries are its own). Not its legs: the soldier's
    /// controller allocates in the engine's CollisionWorld.Overlaps (64 B a call, measured
    /// 2026-11-26), the campaign soldier's as much.</summary>
    [Fact]
    public void A_bot_fighting_and_roaming_allocates_nothing()
    {
        var floor = new[]
        {
            new Triangle(new(-8000, 0, -8000), new(8000, 0, -8000), new(8000, 0, 8000)),
            new Triangle(new(-8000, 0, -8000), new(8000, 0, 8000), new(-8000, 0, 8000)),
        };
        var world = new CollisionWorld().Add(new TriangleGrid(floor));
        var server = new DmServer(new DmSettings(), new Random(1));
        server.SetArena(new DmArena { Map = 605, Spawns = [new DmSpawn(new Vector3(0, 1, 0), 0f), new DmSpawn(new Vector3(3000, 1, 0), 0f)], ItemSpots = [] });
        server.Drop(25601, new Vector3(2000, 0, 2000));
        server.Drop(27399, new Vector3(-2000, 0, 2000));
        var human = server.AddHuman(2, "H", DmClass.Human)!;
        (human.Alive, human.Position) = (true, new Vector3(0, 0, -900));
        var b = server.AddBot(DmClass.Human);
        b.Alive = true;
        var bot = new BotBrain(b.Id, 2, world, WeaponTable.Shipped, new Random(7));
        bot.Spawn(new Vector3(0, 1, 0), 0f);
        var quiet = new QuietBot();
        void Tick()
        {
            bot.Step(1f / 60f, server, quiet);
            bot.StepShots(1f / 60f, server, quiet);
        }

        void Mind()
        {
            bot.Think(server, quiet);
            bot.StepShots(1f / 60f, server, quiet);
        }

        foreach (var weapon in new[] { 1, 7 })
        {
            bot.Weapon = weapon;
            for (var i = 0; i < 240; i++)
            {
                Tick();
            }

            Assert.Equal(0, Allocated(Mind));
        }

        Assert.True(bot.Shots > 0);
        human.Alive = false;
        for (var i = 0; i < 240; i++)
        {
            Tick();
        }

        Assert.Equal(0, Allocated(Mind));
        Assert.Equal(0, Allocated(() => bot.PickGoal(server)));
        Assert.NotNull(bot.Goal);
    }

    private sealed class QuietBot : IBotOutput
    {
        public void Pose(int id, Vector3 feet, float yaw, float pitch, int flags) { }
        public void Fire(int id, int weapon, Vector3 from, Vector3 dir) { }
        public void Hit(int id, int victim, float points, int weapon) { }
        public void SelfHit(int id, float points, int weapon) { }
        public void Pickup(int id, int key) { }
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
