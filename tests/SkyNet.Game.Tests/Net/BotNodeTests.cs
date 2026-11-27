using System.Numerics;
using RetroEng.Net;
using RetroEng.Physics;
using SkyNet.Game.Enemies;
using SkyNet.Game.Net;
using SkyNet.Game.Player;
using SkyNet.Game.Weapons;

namespace SkyNet.Game.Tests.Net;

/// <summary>Phase 3 of docs/plans/smarter-ai.md: the bots' nodes (SeekItem, Fight, Chase, Retreat),
/// their reaction to hits and shots, item goals by value over travel time, the weapon order, the
/// vehicle claims, the aim, the traits and the seed, on small built worlds.</summary>
public class BotNodeTests
{
    private const float Dt = 1f / 60f;
    private const int Enemy = 2;
    /// <summary>Item sprites (PickupTable.Dos): bullets, 50% health, UZI, LASER RIFLE.</summary>
    private const int Bullets = 25601, Health50 = 27399, Uzi = 25737, LaserRifle = 25611;

    private sealed class Quiet(DmServer server) : IBotOutput
    {
        public readonly List<Vector3> Shots = [];
        public void Pose(int id, Vector3 feet, float yaw, float pitch, int flags) => server.Pose(id, feet, yaw, pitch, flags);
        public void Fire(int id, int weapon, Vector3 from, Vector3 dir) => Shots.Add(dir);
        public void Hit(int id, int victim, float points, int weapon) { }
        public void SelfHit(int id, float points, int weapon) { }
        public void Pickup(int id, int key) { }
    }

    private static IEnumerable<Triangle> Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
    {
        yield return new Triangle(a, b, c);
        yield return new Triangle(a, c, d);
    }

    private static IEnumerable<Triangle> Floor() =>
        Quad(new(-8000, 0, -8000), new(8000, 0, -8000), new(8000, 0, 8000), new(-8000, 0, 8000));

    /// <summary>A wall across −Z at z = −500 from x −400 to 400, 300 high.</summary>
    private static IEnumerable<Triangle> Wall() =>
        Quad(new(-400, 0, -500), new(400, 0, -500), new(400, 300, -500), new(-400, 300, -500));

    private static CollisionWorld World(bool wall = false) =>
        new CollisionWorld().Add(new TriangleGrid(wall ? Floor().Concat(Wall()) : Floor()));

    /// <summary>A bot (−1) at the origin facing −Z, an enemy human (2) at <paramref name="enemy"/>
    /// (null: dead, far away), the arena's one spawn under the bot.</summary>
    private static (DmServer Server, BotBrain Bot, Quiet Out) Setup(CollisionWorld world, Vector3? enemy, int skill = 1)
    {
        var server = new DmServer(new DmSettings(), new Random(1));
        server.SetArena(new DmArena { Map = 605, Spawns = [new DmSpawn(new Vector3(0, 1, 0), 0f)], ItemSpots = [] });
        var h = server.AddHuman(Enemy, "H", DmClass.Human)!;
        (h.Alive, h.Position) = (enemy != null, enemy ?? new Vector3(0, 0, 7900));
        var b = server.AddBot(DmClass.Human);
        b.Alive = true;
        var bot = new BotBrain(b.Id, skill, world, WeaponTable.Shipped, new Random(7));
        bot.Spawn(new Vector3(0, 1, 0), 0f);
        b.Position = bot.Body.Position;
        return (server, bot, new Quiet(server));
    }

    private static void Run(DmServer server, BotBrain bot, Quiet q, float seconds)
    {
        for (var t = 0f; t < seconds; t += Dt)
        {
            server.Update(Dt);
            bot.Step(Dt, server, q);
            bot.StepShots(Dt, server, q);
        }
    }

    private static float Wrap(float a) => BotAim.Wrap(a);

    // ---- nodes ----

    [Fact]
    public void An_enemy_in_view_starts_a_fight()
    {
        var (server, bot, q) = Setup(World(), new Vector3(0, 0, -1200));
        Assert.Equal(BotState.SeekItem, bot.State);
        bot.Think(server, q);
        Assert.Equal(BotState.Fight, bot.State);
        Assert.Equal(Enemy, bot.Target);
    }

    [Fact]
    public void An_enemy_lost_behind_a_wall_is_chased_then_given_up()
    {
        var (server, bot, q) = Setup(World(wall: true), new Vector3(-1000, 0, -900));
        bot.Think(server, q);
        Assert.Equal(BotState.Fight, bot.State);

        server.Players[Enemy].Position = new Vector3(0, 0, -8000); // behind the wall, then out of sight range
        bot.Think(server, q);
        Assert.Equal(BotState.Chase, bot.State);
        Assert.Equal(new Vector3(-1000, 0, -900), bot.LastSeen);

        // It walks to the last seen place and, nobody there, goes back to seeking.
        var nearest = float.MaxValue;
        for (var t = 0f; t < 8f; t += Dt)
        {
            Run(server, bot, q, Dt / 2f);
            nearest = MathF.Min(nearest, new Vector2(bot.Body.Position.X + 1000f, bot.Body.Position.Z + 900f).Length());
        }

        Assert.Null(bot.Target);
        Assert.Equal(BotState.SeekItem, bot.State);
        Assert.True(nearest < BotBrain.GoalReached + 10f, $"came within {nearest:0}");
    }

    [Fact]
    public void A_hurt_bot_retreats_to_health_away_from_the_enemy_then_comes_back()
    {
        var (server, bot, q) = Setup(World(), new Vector3(0, 0, -1200));
        server.Drop(Health50, new Vector3(0, 0, -900)); // towards the enemy
        server.Drop(Health50, new Vector3(0, 0, 1500)); // away from it
        server.Players[bot.Id].Health = 0.1f * DmProtocol.DosHealth;
        bot.Think(server, q);
        Assert.Equal(BotState.Retreat, bot.State);
        Assert.Equal(new Vector3(0, 0, 1500), bot.Goal);

        // Still in sight while it runs: it shoots back.
        Run(server, bot, q, 3f);
        Assert.Equal(BotState.Retreat, bot.State);
        Assert.NotEmpty(q.Shots);
        Assert.True(bot.Body.Position.Z > 300f, $"at {bot.Body.Position}");

        // Healed: back into the fight.
        server.Players[bot.Id].Health = DmProtocol.DosHealth;
        bot.Think(server, q);
        Assert.Equal(BotState.Fight, bot.State);
    }

    [Fact]
    public void Outgunned_it_retreats()
    {
        var (server, bot, q) = Setup(World(), new Vector3(0, 0, -1200));
        bot.Weapon = 1; // UZI against a PLASMA CANNON, half the bar left
        server.Players[Enemy].Weapon = 11;
        server.Players[bot.Id].Health = 0.5f * DmProtocol.DosHealth;
        bot.Think(server, q);
        Assert.Equal(BotState.Retreat, bot.State);

        // The same health with the same gun: it fights.
        (server, bot, q) = Setup(World(), new Vector3(0, 0, -1200));
        bot.Weapon = 11;
        server.Players[Enemy].Weapon = 11;
        server.Players[bot.Id].Health = 0.5f * DmProtocol.DosHealth;
        bot.Think(server, q);
        Assert.Equal(BotState.Fight, bot.State);
    }

    /// <summary>Defect on HEAD: a shot from out of sight was ignored (no target, the view stayed).
    /// Through the host and its bots, as a match wires it.</summary>
    [Fact]
    public void A_shot_from_behind_turns_it_round_and_after_the_shooter()
    {
        var net = new LoopbackNetwork();
        var server = new DmServer(new DmSettings { Bots = 1 }, new Random(1));
        server.SetArena(new DmArena { Map = 605, Spawns = [new DmSpawn(new Vector3(0, 1, 0), 0f)], ItemSpots = [] });
        using var host = new DmHost(new NetEndpoint(net.Listen(0, DmProtocol.MaxPeers)!), server, "HOST", DmClass.Human);
        host.ReportLevelReady();
        var me = server.Players[host.LocalId];
        (me.Alive, me.Position) = (true, new Vector3(0, 0, BotBrain.SeeRange + 900f)); // behind it, beyond sight
        var bots = DmBots.Start(host, World(), WeaponTable.Shipped, seed: 5);
        var (id, bot) = bots.Brains.Single();
        bot.Spawn(new Vector3(0, 1, 0), 0f);

        Assert.True(server.Hit(id, 10f, host.LocalId, 1));
        for (var i = 0; i < 120; i++)
        {
            host.Update(Dt);
        }

        Assert.Equal(host.LocalId, bot.Target);
        Assert.True(MathF.Abs(Wrap(bot.Body.Yaw - MathF.PI)) < 0.5f, $"yaw {bot.Body.Yaw:0.00}");
        Assert.True(bot.Body.Position.Z > 200f, $"at {bot.Body.Position}");
        Assert.Equal(BotState.Chase, bot.State);
    }
    [Fact]
    public void A_shot_it_hears_is_looked_into_a_far_one_is_not()
    {
        var (server, bot, q) = Setup(World(), null);
        bot.Hear(Enemy, new Vector3(3500, 0, 0), NoiseKind.Shot);
        bot.Think(server, q);
        Assert.Equal(BotState.SeekItem, bot.State);

        bot.Hear(Enemy, new Vector3(1500, 0, 0), NoiseKind.Shot);
        bot.Think(server, q);
        Assert.Equal(BotState.Chase, bot.State);
        Run(server, bot, q, 2f);
        Assert.True(bot.Body.Position.X > 300f, $"at {bot.Body.Position}");
        Assert.True(MathF.Abs(Wrap(bot.Body.Yaw + MathF.PI / 2f)) < 0.5f, $"yaw {bot.Body.Yaw:0.00}"); // facing +X
    }

    [Fact]
    public void It_strafes_only_in_a_fight_and_strolls_to_a_wander_goal()
    {
        var (server, bot, q) = Setup(World(), null);
        server.SetArena(new DmArena { Map = 605, Spawns = [new DmSpawn(new Vector3(0, 1, 0), 0f), new DmSpawn(new Vector3(0, 1, -3000), 0f)], ItemSpots = [] });
        var top = 0f;
        for (var t = 0f; t < 3f; t += Dt)
        {
            Run(server, bot, q, Dt);
            top = MathF.Max(top, new Vector2(bot.Body.Velocity.X, bot.Body.Velocity.Z).Length());
            Assert.Equal(BotState.SeekItem, bot.State);
        }

        Assert.True(top is > 100f and <= PlayerController.WalkSpeed + 1f, $"top {top:0}");

        // Fighting, it runs sideways.
        (server, bot, q) = Setup(World(), new Vector3(0, 0, -700));
        bot.Weapon = 1; // the UZI's range: 700
        Run(server, bot, q, 1f);
        Assert.Equal(BotState.Fight, bot.State);
        var to = Vector3.Normalize(server.Players[Enemy].Position - bot.Body.Position);
        Assert.True(MathF.Abs(Vector3.Dot(Vector3.Normalize(bot.Want), to)) < 0.6f, $"want {bot.Want}");
    }

    // ---- goals ----

    [Fact]
    public void Hurt_it_goes_for_health_well_for_a_better_gun_never_for_health_it_does_not_need()
    {
        var (server, bot, _) = Setup(World(), null);
        bot.Weapon = 1;
        server.Drop(Bullets, new Vector3(0, 0, -600));
        server.Drop(Health50, new Vector3(0, 0, 2500));
        server.Drop(LaserRifle, new Vector3(2500, 0, 0));

        server.Players[bot.Id].Health = 0.3f * DmProtocol.DosHealth;
        bot.PickGoal(server);
        Assert.Equal(new Vector3(0, 0, 2500), bot.Goal);

        server.Players[bot.Id].Health = DmProtocol.DosHealth;
        bot.PickGoal(server);
        Assert.Equal(new Vector3(2500, 0, 0), bot.Goal);

        bot.Weapon = 11; // a better gun in hand: the rifle is only ammo, the near bullets win
        bot.PickGoal(server);
        Assert.Equal(new Vector3(0, 0, -600), bot.Goal);
    }

    [Fact]
    public void The_nearer_of_two_equal_items_wins_and_a_taken_one_waits_for_its_respawn()
    {
        var (server, bot, _) = Setup(World(), null);
        var far = server.Drop(Bullets, new Vector3(0, 0, -3000))!;
        var near = server.Drop(Bullets, new Vector3(0, 0, 1000))!;
        bot.PickGoal(server);
        Assert.Equal(near.Position, bot.Goal);

        // It took the near one (the server puts it back; the bot waits for the respawn time).
        bot.Took(near, server.Now);
        bot.PickGoal(server);
        Assert.Equal(far.Position, bot.Goal);
        server.Update(DmProtocol.PickupRespawn + 0.5);
        bot.PickGoal(server);
        Assert.Equal(near.Position, bot.Goal);
    }

    /// <summary>Defect on HEAD: any carried weapon item went in hand, a worse one too.</summary>
    [Fact]
    public void It_never_swaps_down()
    {
        var (_, bot, _) = Setup(World(), null);
        bot.Weapon = 7;
        bot.Took(Uzi);
        Assert.Equal(7, bot.Weapon);

        bot.Weapon = 1;
        bot.Took(LaserRifle);
        Assert.Equal(7, bot.Weapon);
    }

    [Fact]
    public void Two_bots_do_not_rush_the_same_vehicle()
    {
        var settings = new DmSettings { Items = System.Collections.Immutable.ImmutableDictionary<string, int>.Empty.Add("jeeps", 1) };
        var server = new DmServer(settings, new Random(1));
        server.SetArena(new DmArena { Map = 605, Spawns = [new DmSpawn(new Vector3(0, 1, 0), 0f)], ItemSpots = [], JeepSpots = [new Vector3(1500, 0, 0)] });
        var claims = new BotClaims();
        var brains = new List<BotBrain>();
        for (var i = 0; i < 2; i++)
        {
            var b = server.AddBot(DmClass.Human);
            var bot = new BotBrain(b.Id, 1, World(), WeaponTable.Shipped, new Random(7 + i), null, BotRides.None, null, claims);
            bot.Spawn(new Vector3(0, 1, i * 200f), 0f);
            b.Position = bot.Body.Position;
            brains.Add(bot);
        }

        foreach (var bot in brains)
        {
            bot.PickGoal(server);
        }

        Assert.Single(brains, b => b.Goal == server.Vehicles[1].Position);
    }

    // ---- aim ----

    [Fact]
    public void The_aim_error_settles_while_it_tracks()
    {
        var aim = new BotAim(settle: 2f, lag: 0.2f, turnRate: 5f, AimLead.None);
        aim.Acquire(new Random(3), 0.05f, 1f);
        var first = aim.Error.Length();
        Assert.True(first > 0.05f);
        for (var i = 0; i < 120; i++)
        {
            aim.Track(Vector3.Zero, new Vector3(0, 0, -1000), 0f, 0f, Dt);
        }

        Assert.True(aim.Error.Length() < first * 0.05f, $"{aim.Error.Length():0.000} of {first:0.000}");
    }

    [Fact]
    public void A_crossing_target_drags_the_aim_and_a_slow_shot_is_led()
    {
        float Yaw(AimLead lead, float speed)
        {
            var aim = new BotAim(settle: 3f, lag: 0.2f, turnRate: 5f, lead);
            aim.Acquire(new Random(3), 0f, 1f);
            var y = 0f;
            for (var i = 0; i < 120; i++)
            {
                (y, _) = aim.Track(Vector3.Zero, new Vector3(300f * i * Dt, 0, -1000), speed, 0f, Dt); // crossing to +X at 300/s
            }

            return Wrap(y - MathF.Atan2(-300f * 119 * Dt, 1000f));
        }

        Assert.True(Yaw(AimLead.None, 0f) > 0.02f, "the aim trails (to the left of the true bearing)");
        Assert.True(Yaw(AimLead.Slow, 1000f) < Yaw(AimLead.None, 0f) - 0.1f, "the lead puts it ahead");
    }

    [Fact]
    public void The_view_turns_no_faster_than_the_aim_allows()
    {
        var (server, bot, q) = Setup(World(), new Vector3(0, 0, 200)); // right behind, near enough to feel
        var before = bot.Body.Yaw;
        const int Ticks = 13; // one think, then a few ticks
        for (var i = 0; i < Ticks; i++)
        {
            Run(server, bot, q, Dt / 2f);
        }

        var turned = MathF.Abs(Wrap(bot.Body.Yaw - before));
        Assert.True(turned <= bot.Aim.TurnRate * Dt * Ticks + 1e-3f, $"turned {turned:0.000}");
        Assert.True(turned > 0f);
    }

    // ---- traits and seed ----

    [Fact]
    public void A_numbered_bot_has_its_names_traits_and_an_unknown_one_the_average()
    {
        Assert.Equal(BotTraits.For("T-800"), BotTraits.For("T-800-13"));
        Assert.NotEqual(BotTraits.Average, BotTraits.For("T-800"));
        Assert.Equal(BotTraits.Average, BotTraits.For("NOBODY"));
        Assert.All(DmServer.BotNames, n => Assert.NotEqual(BotTraits.Average, BotTraits.For(n)));
    }

    /// <summary>Defect on HEAD: without a seed the bots drew one from Random.Shared and kept it
    /// nowhere, so a match could not be played again. Now it is kept (and written into demos).</summary>
    [Fact]
    public void Unseeded_bots_keep_their_seed_and_replay_with_it()
    {
        var first = Match(null, out var seed);
        var again = Match(seed, out _);
        Assert.Equal(first, again);
    }

    private static List<Vector3> Match(int? seed, out int used)
    {
        var net = new LoopbackNetwork();
        var server = new DmServer(new DmSettings { Bots = 2 }, new Random(1));
        server.SetArena(new DmArena { Map = 605, Spawns = [new DmSpawn(new Vector3(0, 1, 0), 0f), new DmSpawn(new Vector3(2000, 1, -2000), 1f)], ItemSpots = [] });
        using var host = new DmHost(new NetEndpoint(net.Listen(0, DmProtocol.MaxPeers)!), server, "HOST", DmClass.Human);
        host.ReportLevelReady();
        server.Players[host.LocalId].Alive = false;
        var bots = DmBots.Start(host, World(wall: true), WeaponTable.Shipped, seed);
        for (var i = 0; i < 60 * 10; i++)
        {
            host.Update(Dt);
        }

        used = bots.Seed;
        return [.. bots.Brains.Values.Select(b => b.Body.Position)];
    }

    // ---- security review ----

    private static (DmHost Host, DmBots Bots) JeepMatch(int bots)
    {
        var net = new LoopbackNetwork();
        var settings = new DmSettings { Bots = bots, Items = System.Collections.Immutable.ImmutableDictionary<string, int>.Empty.Add("jeeps", 1) };
        var server = new DmServer(settings, new Random(1));
        server.SetArena(new DmArena
        {
            Map = 605, Spawns = [new DmSpawn(new Vector3(0, 1, 0), 0f), new DmSpawn(new Vector3(0, 1, 400), 0f)], ItemSpots = [],
            JeepSpots = [new Vector3(2500, 0, 0)],
        });
        var host = new DmHost(new NetEndpoint(net.Listen(0, DmProtocol.MaxPeers)!), server, "HOST", DmClass.Human);
        host.ReportLevelReady();
        server.Players[host.LocalId].Alive = false;
        return (host, DmBots.Start(host, World(), WeaponTable.Shipped, seed: 3));
    }

    /// <summary>A dead bot let go of the vehicle it headed for (it kept the claim until its respawn).</summary>
    [Fact]
    public void A_dead_bot_lets_go_of_its_vehicle()
    {
        var (host, bots) = JeepMatch(2);
        using var _ = host;
        var jeep = host.Server.Vehicles[1].Position;
        foreach (var b in bots.Brains.Values)
        {
            b.PickGoal(host.Server);
        }

        var claimer = bots.Brains.Values.Single(b => b.Goal == jeep);
        var other = bots.Brains.Values.Single(b => b != claimer);
        Assert.True(host.Server.Hit(claimer.Id, 5000f, host.LocalId, 1));
        other.PickGoal(host.Server);
        Assert.Equal(jeep, other.Goal);
    }

    /// <summary>The noises the host hands its bots allocate nothing (a sorted dictionary's walk did).</summary>
    [Fact]
    public void Handing_bots_a_noise_allocates_nothing()
    {
        var (host, bots) = JeepMatch(4);
        using var _ = host;
        bots.Noise(host.LocalId, Vector3.Zero, NoiseKind.Shot);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100; i++)
        {
            bots.Noise(host.LocalId, Vector3.Zero, NoiseKind.Shot);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    /// <summary>A new round draws a new seed (a dedicated server kept one for all its rounds).</summary>
    [Fact]
    public void A_new_seed_reseeds_every_brain()
    {
        var (host, bots) = JeepMatch(1);
        using var _ = host;
        bots.Reseed(1234);
        Assert.Equal(1234, bots.Seed);
        var a = bots.Brains.Values.Single();
        var (host2, bots2) = JeepMatch(1);
        using var __ = host2;
        bots2.Reseed(1234);
        for (var i = 0; i < 300; i++)
        {
            host.Update(Dt);
            host2.Update(Dt);
        }

        Assert.Equal(a.Body.Position, bots2.Brains.Values.Single().Body.Position);
    }
}