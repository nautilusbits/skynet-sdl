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
    internal sealed class Wire(DmServer server) : IBotOutput
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

    internal static float Flat(Vector3 v) => new Vector2(v.X, v.Z).Length();

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
        Assert.Equal(BotState.Ride, bot.State);

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
        Assert.Equal(BotState.SeekItem, bot.State);
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

    /// <summary>Owner: the overpass HKs of Highway City are reached on foot by jumping down from a
    /// roof. The bake finds the drops itself; a bot on the roof whose drop lands on the first HK's
    /// deck walks off, lands unhurt and boards it (the only vehicle, nothing else to seek).</summary>
    [BehaviourDataFact]
    public void A_bot_drops_off_a_roof_onto_the_overpass_and_takes_its_hk()
    {
        var data = BehaviourData.Data;
        var session = new SkyNet.Game.Campaign.GameSession(data);
        Assert.True(session.StartDeathmatch(605));
        var arena = DmBots.Cleared(DmArena.Load(data, 605)!, session);
        var mesh = DmNav.For(session)!;
        var items = ImmutableDictionary<string, int>.Empty.Add("hks", 1);
        var server = new DmServer(new DmSettings { Map = 605, Items = items }, new Random(1)) { LimitTravel = false };
        server.SetArena(arena);
        session.World.Add(new ParkedVehicleColliders(() => server.Vehicles.Values, new SkyNet.Game.Enemies.EnemyModels(data)));
        var b = server.AddBot(DmClass.Human);
        var bot = new BotBrain(b.Id, 1, session.World, session.Weapons.Table, new Random(3), mesh, DmNav.Rides(session, server.Vehicles.Values));

        // The roof: the start of the nearest drop onto the HK's deck from which the bot finds a way to it.
        var hk = server.Vehicles[1].Position;
        var (roof, deck) = RoofDrop(session, server, mesh, hk);
        output.WriteLine($"{mesh.Drops().Count()} drops; roof {roof:0} -> deck {deck:0}, HK at {hk:0}");
        bot.Spawn(roof, 0f);
        b.Position = bot.Body.Position;
        var wire = new BotRideTests.Wire(server);
        var lowest = roof.Y;
        for (var t = 0f; t < 30f && bot.Vehicle == VehicleMode.Foot; t += Dt)
        {
            server.Update(Dt);
            bot.Step(Dt, server, wire);
            b.Position = bot.Body.Position;
            lowest = MathF.Min(lowest, bot.Body.Position.Y);
        }

        Assert.Equal(VehicleMode.Hk, bot.Vehicle);
        Assert.True(lowest < roof.Y - SkyNet.Game.Player.PlayerController.StepHeight, $"never left the roof ({lowest:0})");
        Assert.True(lowest > deck.Y - 100f, $"fell to {lowest:0}, below the deck"); // the drop, not the street
        Assert.Equal(DmProtocol.DosHealth, server.Players[b.Id].Health); // landed unhurt
    }
    /// <summary>The overpass HK by the street (HK 5) is reached on foot from every street spawn:
    /// up the steps beside it, not across the bake's false joins to the deck (the bake cuts them,
    /// <see cref="NavJoins"/>).</summary>
    [BehaviourDataFact]
    public void Bots_walk_from_the_street_up_to_the_overpass_hk()
    {
        var data = BehaviourData.Data;
        var session = new SkyNet.Game.Campaign.GameSession(data);
        Assert.True(session.StartDeathmatch(605));
        var arena = DmBots.Cleared(DmArena.Load(data, 605)!, session);
        var mesh = DmNav.For(session)!;
        var items = ImmutableDictionary<string, int>.Empty.Add("hks", 2); // the two overpass HKs
        var server = new DmServer(new DmSettings { Map = 605, Items = items }, new Random(1)) { LimitTravel = false };
        server.SetArena(arena);
        session.World.Add(new ParkedVehicleColliders(() => server.Vehicles.Values, new SkyNet.Game.Enemies.EnemyModels(data)));
        var deck = server.Vehicles.Values.Min(v => v.Position.Y);
        var misses = new List<string>();
        foreach (var sp in arena.Spawns)
        {
            var b = server.AddBot(DmClass.Human);
            var bot = new BotBrain(b.Id, 1, session.World, session.Weapons.Table, new Random(3), mesh, DmNav.Rides(session, server.Vehicles.Values));
            bot.Spawn(sp.Feet, 0f);
            b.Position = bot.Body.Position;
            var wire = new BotRideTests.Wire(server);
            var t = 0f;
            for (; t < 30f && bot.Vehicle == VehicleMode.Foot; t += Dt)
            {
                server.Update(Dt);
                bot.Step(Dt, server, wire);
                b.Position = bot.Body.Position;
            }

            output.WriteLine($"from {sp.Feet:0}: {(bot.Vehicle == VehicleMode.Hk ? $"in the HK at {t:0.0} s" : "never")}");
            if (bot.Vehicle != VehicleMode.Hk || bot.Body.Position.Y < deck - 40f)
            {
                misses.Add($"{sp.Feet:0}");
            }

            if (server.VehicleOf(b.Id) is { } v)
            {
                server.LeaveVehicle(b.Id, v.Home, v.HomeYaw); // parked back for the next spawn
            }

            server.RemovePlayer(b.Id);
        }

        Assert.True(misses.Count == 0, $"no HK from {string.Join(", ", misses)}");
    }

    /// <summary>The far overpass HK (the roof-drop test's) is reached on foot from every street
    /// spawn, from most up the side stairs to the roof and off it onto the deck (the east spawns
    /// have a ramp). The bake cuts its false joins (street to deck a tile border apart), which
    /// path search took before.</summary>
    [BehaviourDataFact]
    public void Bots_walk_from_the_street_over_a_roof_to_the_far_hk()
    {
        var data = BehaviourData.Data;
        var session = new SkyNet.Game.Campaign.GameSession(data);
        Assert.True(session.StartDeathmatch(605));
        var arena = DmBots.Cleared(DmArena.Load(data, 605)!, session);
        var mesh = DmNav.For(session)!;
        var items = ImmutableDictionary<string, int>.Empty.Add("hks", 1);
        var server = new DmServer(new DmSettings { Map = 605, Items = items }, new Random(1)) { LimitTravel = false };
        server.SetArena(arena);
        session.World.Add(new ParkedVehicleColliders(() => server.Vehicles.Values, new SkyNet.Game.Enemies.EnemyModels(data)));
        var hk = server.Vehicles[1].Position;
        var (roof, _) = RoofDrop(session, server, mesh, hk);
        var misses = new List<string>();
        var overRoof = 0;
        foreach (var sp in arena.Spawns)
        {
            var b = server.AddBot(DmClass.Human);
            var bot = new BotBrain(b.Id, 1, session.World, session.Weapons.Table, new Random(3), mesh, DmNav.Rides(session, server.Vehicles.Values));
            bot.Spawn(sp.Feet, 0f);
            b.Position = bot.Body.Position;
            var wire = new BotRideTests.Wire(server);
            var highest = sp.Feet.Y;
            var t = 0f;
            for (; t < FarHkBy && bot.Vehicle == VehicleMode.Foot; t += Dt)
            {
                server.Update(Dt);
                bot.Step(Dt, server, wire);
                b.Position = bot.Body.Position;
                highest = MathF.Max(highest, bot.Body.Position.Y);
            }

            output.WriteLine($"from {sp.Feet:0}: {(bot.Vehicle == VehicleMode.Hk ? $"in the HK at {t:0.0} s" : "never")}, highest {highest:0} (roof {roof.Y:0})");
            if (bot.Vehicle != VehicleMode.Hk)
            {
                misses.Add($"{sp.Feet:0}");
            }

            overRoof += highest >= roof.Y - RoofSlack ? 1 : 0;

            if (server.VehicleOf(b.Id) is { } v)
            {
                server.LeaveVehicle(b.Id, v.Home, v.HomeYaw); // parked back for the next spawn
            }

            server.RemovePlayer(b.Id);
        }

        Assert.True(misses.Count == 0, $"no HK from {string.Join(", ", misses)}");
        Assert.True(overRoof * 2 > arena.Spawns.Count, $"over the roof from {overRoof} of {arena.Spawns.Count} spawns");
    }

    /// <summary>The street-to-far-HK walk's time limit (s), and how near the roof's height it counts as on it.</summary>
    private const float FarHkBy = 60f, RoofSlack = 20f;

    /// <summary>The nearest drop onto <paramref name="hk"/>'s deck (its landing's way there keeps
    /// to the deck: another deck at its height may lead down to the street) from whose start a bot
    /// finds a way to it.</summary>
    private static (Vector3 Roof, Vector3 Deck) RoofDrop(SkyNet.Game.Campaign.GameSession session, DmServer server, NavMesh mesh, Vector3 hk)
    {
        var step = SkyNet.Game.Player.PlayerController.StepHeight;
        var query = new NavQuery(mesh);
        var way = new List<Vector3>();
        var b = server.AddBot(DmClass.Human);
        var found = mesh.Drops()
            .Where(d => d.From.Y - d.To.Y > step && MathF.Abs(d.To.Y - hk.Y) < 40f)
            .OrderBy(d => BotRideTests.Flat(d.To - hk))
            .First(d =>
            {
                if (query.FindPath(d.To, hk, way) != PathResult.Found || way.Min(c => c.Y) < d.To.Y - step)
                {
                    return false;
                }

                var probe = new BotBrain(b.Id, 1, session.World, session.Weapons.Table, new Random(3), mesh, null);
                probe.Spawn(d.From, 0f);
                probe.PickGoal(server);
                return probe.Goal == hk;
            });
        server.RemovePlayer(b.Id);
        return found;
    }

    /// <summary>HEAD (cbcf474) stuck spells on foot on Highway City over <see cref="Seeds"/> × 3 min × 4 bots.</summary>
    private const int HeadFootStuck = 88;

    /// <summary>Phase 3 first stuck 219 times here: goals through spots under the overpass's edge and by
    /// posts the soldier-sized mesh hugged. The bots' mesh has room to spare now; never worse than HEAD.</summary>
    [BehaviourDataFact]
    public void Bots_stick_on_foot_no_more_than_before_on_highway_city()
    {
        var stuck = Seeds.Sum(seed => Match(605, seed).FootStuck);
        output.WriteLine($"on-foot stuck {stuck} (HEAD {HeadFootStuck})");
        Assert.True(stuck <= HeadFootStuck, $"stuck {stuck} times, HEAD {HeadFootStuck}");
    }
    /// <summary>Owner playtest 2026-11-26: on Highway City (MAP.605: 3 jeeps, 3 HKs) bots rarely took
    /// the vehicles. Four bots, each seed: within <see cref="FirstRideBy"/> s some bot boards a jeep
    /// and some bot an HK.</summary>
    private const float FirstRideBy = 60f;

    [BehaviourDataFact]
    public void Bots_take_jeeps_and_hks_on_highway_city()
    {
        var misses = new List<string>();
        foreach (var seed in Seeds)
        {
            var (jeep, hk) = FirstRides(605, seed, FirstRideBy);
            output.WriteLine($"seed {seed}: first jeep {jeep?.ToString("0.0") ?? "never"} s, first HK {hk?.ToString("0.0") ?? "never"} s");
            if (jeep == null || hk == null)
            {
                misses.Add($"seed {seed}");
            }
        }

        Assert.True(misses.Count == 0, $"no jeep or no HK boarded within {FirstRideBy} s: {string.Join(", ", misses)}");
    }

    /// <summary>When a bot first sat in a jeep and in an HK (s; null: not within <paramref name="limit"/>).</summary>
    private static (float? Jeep, float? Hk) FirstRides(int map, int seed, float limit)
    {
        using var m = Arena(map, seed);
        float? jeep = null, hk = null;
        var now = 0f;
        m.Host.Server.VehicleChanged += (v, why) =>
        {
            if (why != DmVehicleEvent.Changed || v.Driver >= 0)
            {
                return;
            }

            if (v.Kind == VehicleMode.Jeep)
            {
                jeep ??= now;
            }
            else
            {
                hk ??= now;
            }
        };

        for (; now < limit && (jeep == null || hk == null); now += Dt)
        {
            m.Host.Update(Dt);
        }

        return (jeep, hk);
    }

    private sealed record ArenaMatch(DmHost Host, DmBots Bots) : IDisposable
    {
        public void Dispose() => Host.Dispose();
    }

    /// <summary>A host on <paramref name="map"/> with its NETLEVEL items, four skill-1 bots seeded
    /// <paramref name="seed"/>, the parked vehicles solid, the host's own player dead.</summary>
    private static ArenaMatch Arena(int map, int seed)
    {
        var data = BehaviourData.Data;
        var session = new SkyNet.Game.Campaign.GameSession(data);
        Assert.True(session.StartDeathmatch(map));
        var arena = DmBots.Cleared(DmArena.Load(data, map)!, session);
        var net = new RetroEng.Net.LoopbackNetwork();
        var items = SkyNet.Formats.NetLevelList.For(SkyNet.Formats.NetLevelList.Load(data), map).Items;
        var settings = new DmSettings { Map = map, Bots = 4, BotSkill = 1, Items = items };
        var host = new DmHost(new RetroEng.Net.NetEndpoint(net.Listen(0, DmProtocol.MaxPeers)!), new DmServer(settings, new Random(11)), "HOST", DmClass.Human);
        host.Server.SetArena(arena);
        host.ReportLevelReady();
        host.Server.Players[host.LocalId].Alive = false;
        session.World.Add(new ParkedVehicleColliders(() => host.Server.Vehicles.Values, new SkyNet.Game.Enemies.EnemyModels(data)));
        var bots = DmBots.Start(host, session.World, session.Weapons.Table, seed, DmNav.For(session), DmNav.Rides(session, host.Server.Vehicles.Values));
        return new ArenaMatch(host, bots);
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
