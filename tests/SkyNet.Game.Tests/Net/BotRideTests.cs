using System.Collections.Immutable;
using System.Numerics;
using RetroEng.Physics;
using SkyNet.Game.Navigation;
using SkyNet.Game.Net;
using SkyNet.Game.Tests.Behaviour;
using SkyNet.Game.Tests.Navigation;
using SkyNet.Game.Vehicles;
using SkyNet.Game.Weapons;
using Xunit.Abstractions;

namespace SkyNet.Game.Tests.Net;

/// <summary>Bots in the arena's vehicles on small built worlds: a jeep driven round a wall to an
/// item, an HK over a ridge, the seat left on a low hull and on a wreck.</summary>
public class BotRideTests(ITestOutputHelper output)
{
    private const float Dt = 1f / 60f;
    private const int Human = 2;

    /// <summary>The bot's calls straight into the server (poses with the vehicle's heading, hits).</summary>
    private sealed class Wire(DmServer server) : IBotOutput
    {
        public void Pose(int id, Vector3 feet, float yaw, float pitch, int flags) => server.Pose(id, feet, yaw, pitch, flags);
        void IBotOutput.Pose(int id, Vector3 feet, float yaw, float pitch, int flags, float carYaw) => server.Pose(id, feet, yaw, pitch, flags, carYaw);
        public void Fire(int id, int weapon, Vector3 from, Vector3 dir) { }
        public void Hit(int id, int victim, float points, int weapon) => server.Hit(victim, points, id, weapon);
        public void SelfHit(int id, float points, int weapon) => server.Hit(id, points, id, weapon);
        public void Pickup(int id, int key) { }
    }

    private static readonly Vector3 Start = new(0, 1, 0);
    private static readonly Vector3 Parked = new(150, 0, 0);

    /// <summary>A wall across the way to the item: x −800..800 at z −1000, 300 high.</summary>
    private static CollisionWorld Walled() => NavMeshTests.World(NavMeshTests.Floor(4000f)
        .Concat(NavMeshTests.Block(new(-800, 0, -1010), new(800, 300, -990))));

    /// <summary>A ridge across the way, 700 high: x −3000..3000, z −1300..−900.</summary>
    private const float RidgeTop = 700f;
    private static CollisionWorld Ridged() => NavMeshTests.World(NavMeshTests.Floor(4000f)
        .Concat(NavMeshTests.Block(new(-3000, 0, -1300), new(3000, RidgeTop, -900))));

    /// <summary>A server with one vehicle of <paramref name="kind"/> parked beside the bot's spawn,
    /// a dead human (the hits' attacker), an item at <paramref name="item"/>; the bot at skill 2.</summary>
    private static (DmServer Server, BotBrain Bot, Wire Out) Setup(CollisionWorld world, VehicleMode kind, Vector3 item, NavMesh? jeep, int seed = 7)
    {
        var settings = new DmSettings { Items = ImmutableDictionary<string, int>.Empty.Add(kind == VehicleMode.Jeep ? "jeeps" : "hks", 1) };
        var server = new DmServer(settings, new Random(1)) { LimitTravel = false };
        server.SetArena(new DmArena
        {
            Map = 605,
            Spawns = [new DmSpawn(Start, 0f)],
            ItemSpots = [],
            JeepSpots = kind == VehicleMode.Jeep ? [Parked] : [],
            HkSpots = kind == VehicleMode.Hk ? [Parked] : [],
        });
        var h = server.AddHuman(Human, "H", DmClass.Human)!;
        (h.Alive, h.Position) = (false, new Vector3(0, 0, 3900)); // a scorer for the hits, nobody to hunt
        var b = server.AddBot(DmClass.Human);
        var bot = new BotBrain(b.Id, 2, world, WeaponTable.Shipped, new Random(seed), null, new BotRides(jeep, float.PositiveInfinity));
        bot.Spawn(b.Position, 0f);
        server.Drop(25601, item);
        return (server, bot, new Wire(server));
    }

    private static void Tick(DmServer server, BotBrain bot, Wire wire)
    {
        server.Update(Dt);
        bot.Step(Dt, server, wire);
        bot.StepShots(Dt, server, wire);
    }

    /// <summary>Ticks until the bot sits in its vehicle; the seconds it took (null: not within <paramref name="limit"/>).</summary>
    private static float? Board(DmServer server, BotBrain bot, Wire wire, float limit)
    {
        for (var t = 0f; t < limit; t += Dt)
        {
            Tick(server, bot, wire);
            if (bot.Vehicle != VehicleMode.Foot)
            {
                return t;
            }
        }

        return null;
    }

    private static float Flat(Vector3 v) => new Vector2(v.X, v.Z).Length();

    [Fact]
    public void A_bot_boards_the_jeep_and_drives_round_the_wall_to_the_item()
    {
        var world = Walled();
        var item = new Vector3(0, 0, -2000);
        var jeep = NavMesh.Build(world, NavAgents.Jeep, NavAgents.JeepCells);
        var (server, bot, wire) = Setup(world, VehicleMode.Jeep, item, jeep);

        var boarded = Board(server, bot, wire, 10f);
        Assert.NotNull(boarded);
        Assert.Equal(bot.Id, server.Vehicles[1].Driver);

        float? reached = null;
        for (var t = 0f; t < 40f && reached == null; t += Dt)
        {
            Tick(server, bot, wire);
            Assert.Equal(VehicleMode.Jeep, bot.Vehicle);
            if (Flat(bot.Body.Position - item) < 250f)
            {
                reached = t;
            }
        }

        output.WriteLine($"boarded {boarded:0.0} s, reached {reached?.ToString("0.0") ?? "never"} s, stuck {bot.RideStuck}");
        Assert.NotNull(reached);
        Assert.True(bot.RideStuck <= 1, $"stuck {bot.RideStuck}");
        Assert.Equal(bot.Body.Position, server.Vehicles[1].Position, new Near(40f));
    }

    [Fact]
    public void A_bot_in_the_jeep_is_repeatable()
    {
        var world = Walled();
        var jeep = NavMesh.Build(world, NavAgents.Jeep, NavAgents.JeepCells);
        Vector3 Run()
        {
            var (server, bot, wire) = Setup(world, VehicleMode.Jeep, new Vector3(0, 0, -2000), jeep);
            for (var t = 0f; t < 15f; t += Dt)
            {
                Tick(server, bot, wire);
            }

            return bot.Body.Position;
        }

        Assert.Equal(Run(), Run());
    }

    [Fact]
    public void An_hk_bot_keeps_its_height_over_a_ridge()
    {
        var world = Ridged();
        var item = new Vector3(0, 0, -2500);
        var (server, bot, wire) = Setup(world, VehicleMode.Hk, item, null);
        Assert.NotNull(Board(server, bot, wire, 10f));

        var over = float.MaxValue;
        var crossed = false;
        for (var t = 0f; t < 20f && !crossed; t += Dt)
        {
            Tick(server, bot, wire);
            var p = bot.Body.Position;
            if (p.Z is < -900f and > -1300f)
            {
                over = MathF.Min(over, p.Y - RidgeTop);
            }

            crossed = p.Z < -1600f;
        }

        output.WriteLine($"crossed {crossed}, lowest over the ridge {over:0}, crashes {bot.Crashes}");
        Assert.True(crossed);
        Assert.True(over > 100f, $"{over:0} over the ridge");
        Assert.Equal(0, bot.Crashes);
    }

    [Fact]
    public void A_bot_leaves_a_wrecked_jeep_on_foot()
    {
        var world = Walled();
        var (server, bot, wire) = Setup(world, VehicleMode.Jeep, new Vector3(0, 0, -2000), null);
        Assert.NotNull(Board(server, bot, wire, 10f));
        for (var t = 0f; t < 2f; t += Dt)
        {
            Tick(server, bot, wire);
        }

        Assert.True(server.Hit(bot.Id, 5000f, Human, 1));
        Assert.True(server.Vehicles[1].Wrecked);
        Tick(server, bot, wire);

        Assert.Equal(VehicleMode.Foot, bot.Vehicle);
        Assert.Null(bot.Body.Mover);
        Assert.True(server.IsAlive(bot.Id));
        var walked = bot.Walked;
        for (var t = 0f; t < 3f; t += Dt)
        {
            Tick(server, bot, wire);
        }

        Assert.Equal(VehicleMode.Foot, bot.Vehicle);
        Assert.True(bot.Walked > walked + 300f, $"walked {bot.Walked - walked:0}");
    }

    [Fact]
    public void A_bot_gets_out_of_a_jeep_with_a_low_hull()
    {
        var world = Walled();
        var (server, bot, wire) = Setup(world, VehicleMode.Jeep, new Vector3(0, 0, -2000), null);
        Assert.NotNull(Board(server, bot, wire, 10f));
        Assert.True(server.Hit(bot.Id, Hull.JeepPoints * 0.9f, Human, 1));

        for (var t = 0f; t < 1.5f && bot.Vehicle != VehicleMode.Foot; t += Dt)
        {
            Tick(server, bot, wire);
        }

        var v = server.Vehicles[1];
        Assert.Equal(VehicleMode.Foot, bot.Vehicle);
        Assert.Equal(0, v.Driver);
        Assert.False(v.Wrecked);
        Assert.True(Flat(bot.Body.Position - v.Position) < 150f);
    }

    [Fact]
    public void An_hk_bot_lands_before_it_gets_out()
    {
        var world = Ridged();
        var (server, bot, wire) = Setup(world, VehicleMode.Hk, new Vector3(0, 0, 2500), null);
        Assert.NotNull(Board(server, bot, wire, 10f));
        for (var t = 0f; t < 4f; t += Dt)
        {
            Tick(server, bot, wire);
        }

        var high = bot.Body.Position.Y;
        Assert.True(server.Hit(bot.Id, Hull.HkPoints * 0.9f, Human, 1));
        for (var t = 0f; t < 8f && bot.Vehicle != VehicleMode.Foot; t += Dt)
        {
            Tick(server, bot, wire);
        }

        var v = server.Vehicles[1];
        output.WriteLine($"flew at {high:0}, parked at {v.Position.Y:0}");
        Assert.Equal(VehicleMode.Foot, bot.Vehicle);
        Assert.True(high > 200f, $"flew at {high:0}");
        Assert.True(v.Position.Y < 50f, $"parked at {v.Position.Y:0}");
        Assert.True(server.IsAlive(bot.Id));
    }

    private sealed class Near(float tolerance) : IEqualityComparer<Vector3>
    {
        public bool Equals(Vector3 a, Vector3 b) => Vector3.Distance(a, b) <= tolerance;
        public int GetHashCode(Vector3 v) => 0;
    }
}

/// <summary>Bots on the arenas that park vehicles: three seeded three-minute host matches each,
/// four bots, the parked vehicles solid; how long they drive, what they kill from the seat, how
/// often a jeep sticks or bounces and an HK crashes.</summary>
public class BotRideArenaTests(ITestOutputHelper output)
{
    private const float Dt = 1f / 60f;
    private const float Minutes = 3f;
    private static readonly int[] Seeds = [42, 7, 1234];

    private sealed record Tally(int Boarded, float Driven, int Kills, int RideKills, int Wrecks, int Stuck, int Bounces, int Crashes, int FootStuck)
    {
        public static Tally operator +(Tally a, Tally b) => new(a.Boarded + b.Boarded, a.Driven + b.Driven, a.Kills + b.Kills,
            a.RideKills + b.RideKills, a.Wrecks + b.Wrecks, a.Stuck + b.Stuck, a.Bounces + b.Bounces, a.Crashes + b.Crashes, a.FootStuck + b.FootStuck);
    }

    [BehaviourDataTheory]
    [InlineData(605)]
    [InlineData(601)]
    [InlineData(602)]
    [InlineData(603)]
    [InlineData(600)]
    [InlineData(606)]
    [InlineData(609)]
    public void Bots_drive_on_a_vehicle_arena(int map)
    {
        var total = new Tally(0, 0f, 0, 0, 0, 0, 0, 0, 0);
        foreach (var seed in Seeds)
        {
            total += Match(map, seed);
        }

        var t = total;
        output.WriteLine($"MAP.{map}: {Seeds.Length} x {Minutes} min x 4 bots: boarded {t.Boarded}, in vehicles {t.Driven / 60f:0.0} of {Seeds.Length * Minutes * 4:0} bot-min, "
            + $"kills {t.Kills} (from vehicles {t.RideKills}), wrecks {t.Wrecks}, vehicle stuck {t.Stuck}, jeep bounces {t.Bounces}, "
            + $"HK crashes {t.Crashes}, on-foot stuck {t.FootStuck}");
        Assert.True(t.Boarded > 0);
    }

    private static Tally Match(int map, int seed)
    {
        var data = BehaviourData.Data;
        var session = new SkyNet.Game.Campaign.GameSession(data);
        Assert.True(session.StartDeathmatch(map));
        var arena = DmBots.Cleared(DmArena.Load(data, map)!, session);
        var net = new RetroEng.Net.LoopbackNetwork();
        var items = SkyNet.Formats.NetLevelList.For(SkyNet.Formats.NetLevelList.Load(data), map).Items;
        var settings = new DmSettings { Map = map, Bots = 4, BotSkill = 1, Items = items };
        using var host = new DmHost(new RetroEng.Net.NetEndpoint(net.Listen(0, DmProtocol.MaxPeers)!), new DmServer(settings, new Random(11)), "HOST", DmClass.Human);
        host.Server.SetArena(arena);
        host.ReportLevelReady();
        host.Server.Players[host.LocalId].Alive = false; // the bots among themselves
        session.World.Add(new ParkedVehicleColliders(() => host.Server.Vehicles.Values, new SkyNet.Game.Enemies.EnemyModels(data)));
        var bots = DmBots.Start(host, session.World, session.Weapons.Table, seed, DmNav.For(session), DmNav.Rides(session, host.Server.Vehicles.Values));
        int kills = 0, rideKills = 0, wrecks = 0;
        host.Server.Died += (victim, killer, weapon) =>
        {
            kills += killer != victim ? 1 : 0;
            rideKills += killer != victim && weapon >= WeaponTable.FirstVehicle ? 1 : 0;
        };
        host.Server.VehicleChanged += (_, why) => wrecks += why == DmVehicleEvent.Wrecked ? 1 : 0;
        for (var i = 0; i < Minutes * 60 * 60; i++)
        {
            host.Update(Dt);
        }

        var b = bots.Brains.Values;
        return new Tally(b.Sum(x => x.Boarded), b.Sum(x => x.RideSeconds), kills, rideKills, wrecks, b.Sum(x => x.RideStuck),
            b.Sum(x => x.Bounces), b.Sum(x => x.Crashes), b.Sum(x => x.Stuck));
    }
}
