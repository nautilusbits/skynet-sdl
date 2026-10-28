using System.Numerics;
using RetroEng.Net;
using RetroEng.Physics;
using SkyNet.Game.Campaign;
using SkyNet.Game.Net;
using SkyNet.Game.Tests.Behaviour;
using SkyNet.Game.Weapons;
using Xunit.Abstractions;

namespace SkyNet.Game.Tests.Net;

/// <summary>Step 7, the bots: their decisions on a hand-driven clock (target choice, line of
/// sight, distance keeping, reaction, bursts, pickups), and a host with three bots on MAP.605
/// playing a minute of game time.</summary>
public class BotBrainTests(ITestOutputHelper output)
{
    private const float Dt = 1f / 60f;

    private sealed class Recorder : IBotOutput
    {
        public readonly List<(float At, int Weapon, Vector3 From, Vector3 Dir)> Shots = [];
        public readonly List<(int Victim, float Points)> Hits = [];
        public readonly List<int> Pickups = [];
        public float Now;
        public DmServer? Server;

        public void Pose(int id, Vector3 feet, float yaw, float pitch, int flags) => Server?.Pose(id, feet, yaw, pitch, flags);
        public void Fire(int id, int weapon, Vector3 from, Vector3 dir) => Shots.Add((Now, weapon, from, dir));
        public void Hit(int id, int victim, float points, int weapon) => Hits.Add((victim, points));
        public void SelfHit(int id, float points, int weapon) { }
        public void Pickup(int id, int key) => Pickups.Add(key);
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

    private static CollisionWorld World(bool wall) =>
        new CollisionWorld().Add(new TriangleGrid(wall ? Floor().Concat(Wall()) : Floor()));

    /// <summary>A server with a bot (−1) at the origin facing −Z and humans at the given feet.</summary>
    private static (DmServer Server, BotBrain Bot, Recorder Out) Setup(CollisionWorld world, int skill, params Vector3[] humans)
    {
        var server = new DmServer(new DmSettings(), new Random(1));
        for (var i = 0; i < humans.Length; i++)
        {
            var h = server.AddHuman(2 + i, $"H{i}", DmClass.Human)!;
            (h.Alive, h.Position) = (true, humans[i]);
        }

        var b = server.AddBot(DmClass.Human);
        b.Alive = true;
        var bot = new BotBrain(b.Id, skill, world, WeaponTable.Shipped, new Random(7));
        bot.Spawn(new Vector3(0, 1, 0), 0f);
        return (server, bot, new Recorder { Server = server });
    }

    private static void Run(DmServer server, BotBrain bot, Recorder rec, float seconds)
    {
        for (var t = 0f; t < seconds; t += Dt)
        {
            rec.Now += Dt;
            server.Update(Dt);
            bot.Step(Dt, server, rec);
            bot.StepShots(Dt, server, rec);
        }
    }

    [Fact]
    public void It_hunts_the_nearest_player_it_can_see()
    {
        // Player 2 is nearer but behind the wall; player 3 stands in the open.
        var (server, bot, rec) = Setup(World(wall: true), 1, new Vector3(0, 0, -900), new Vector3(1500, 0, 0));
        bot.Think(server, rec);
        Assert.Equal(3, bot.Target);
        Assert.True(bot.TargetVisible);

        // Without the wall the nearer one is the target.
        (server, bot, rec) = Setup(World(wall: false), 1, new Vector3(0, 0, -900), new Vector3(1500, 0, 0));
        bot.Think(server, rec);
        Assert.Equal(2, bot.Target);

        // Nobody beyond the see range, nobody dead.
        (server, bot, rec) = Setup(World(wall: false), 1, new Vector3(0, 0, -(BotBrain.SeeRange + 100)), new Vector3(300, 0, 0));
        server.Players[3].Alive = false;
        bot.Think(server, rec);
        Assert.Null(bot.Target);
    }

    [Fact]
    public void A_wall_between_them_means_no_target_and_no_shot()
    {
        var (server, bot, rec) = Setup(World(wall: true), 2, new Vector3(0, 0, -900));
        Run(server, bot, rec, 3f);
        Assert.Null(bot.Target);
        Assert.Empty(rec.Shots);
        Assert.False(bot.CanSee(BotBrain.EyeOf(new Vector3(0, 0, 0)), BotBrain.EyeOf(new Vector3(0, 0, -900))));
        Assert.True(bot.CanSee(BotBrain.EyeOf(new Vector3(0, 0, 0)), BotBrain.EyeOf(new Vector3(1000, 0, -900))));
    }

    [Fact]
    public void Its_poses_carry_the_weapon_in_hand()
    {
        var (server, bot, rec) = Setup(World(wall: false), 1);
        bot.Weapon = 7;
        Run(server, bot, rec, 1f);
        Assert.Equal(7, server.Players[bot.Id].Weapon);
    }

    [Fact]
    public void It_keeps_its_weapons_distance_while_strafing()
    {
        var (_, bot, _) = Setup(World(wall: false), 1);
        bot.Weapon = 1; // UZI: 700
        var ahead = -Vector3.UnitZ;
        Assert.True(Vector3.Dot(bot.KeepDistance(ahead * 2000f, Dt), ahead) > 0.9f, "too far: closes in");
        Assert.True(Vector3.Dot(bot.KeepDistance(ahead * 200f, Dt), ahead) < -0.9f, "too near: backs off");
        var within = bot.KeepDistance(ahead * 700f, Dt);
        Assert.Equal(0f, Vector3.Dot(within, ahead), 3);
        Assert.Equal(1f, within.Length(), 3);
        bot.Weapon = 7; // LASER RIFLE: 1400, so 700 is too near
        Assert.True(Vector3.Dot(bot.KeepDistance(ahead * 700f, Dt), ahead) < 0f);
    }

    [Fact]
    public void It_reacts_then_fires_bursts_that_hit_for_the_tables_points()
    {
        var (server, bot, rec) = Setup(World(wall: false), 0, new Vector3(0, 0, -700));
        bot.Weapon = 1;
        Run(server, bot, rec, 6f);
        Assert.Equal(2, bot.Target);
        Assert.NotEmpty(rec.Shots);
        // Skill 0 waits its 0.9 s reaction (plus the first think and the turn).
        Assert.True(rec.Shots[0].At >= BotBrain.Skills[0].Reaction, $"first shot at {rec.Shots[0].At}");
        // Never faster than the UZI's DOS rate (5 a second).
        for (var i = 1; i < rec.Shots.Count; i++)
        {
            Assert.True(rec.Shots[i].At - rec.Shots[i - 1].At >= 1f / 5f - Dt * 1.5f);
        }

        // Bursts of 3-6 shots, then a pause: some gap is longer than the cadence.
        Assert.Contains(rec.Shots.Zip(rec.Shots.Skip(1)), p => p.Second.At - p.First.At > 0.5f);
        // The hits are the UZI's 10 points times the skill's damage scale, on player 2.
        Assert.NotEmpty(rec.Hits);
        Assert.All(rec.Hits, h => Assert.Equal((2, 10f), h)); // the UZI ammo's 10 points, as a human's UZI deals them
        Assert.All(rec.Shots, s => Assert.Equal(1, s.Weapon));
    }

    [Fact]
    public void A_sharp_bot_lands_more_than_a_poor_one()
    {
        int Landed(int skill)
        {
            var (server, bot, rec) = Setup(World(wall: false), skill, new Vector3(0, 0, -1400));
            bot.Weapon = 7;
            Run(server, bot, rec, 20f);
            return rec.Hits.Count * 100 / Math.Max(rec.Shots.Count, 1);
        }

        Assert.True(Landed(2) > Landed(0));
    }

    [Fact]
    public void A_target_out_of_sight_is_chased_then_given_up()
    {
        var (server, bot, rec) = Setup(World(wall: true), 1, new Vector3(-1000, 0, -900));
        bot.Think(server, rec);
        Assert.Equal(2, bot.Target);
        // It steps behind the wall: chased to where it was seen, then dropped.
        server.Players[2].Position = new Vector3(0, 0, -1000);
        bot.Think(server, rec);
        Assert.Equal(2, bot.Target);
        Assert.False(bot.TargetVisible);
        Assert.Equal(new Vector3(-1000, 0, -900), bot.LastSeen);
        for (var i = 0; i < BotBrain.LoseAfter / BotBrain.ThinkInterval + 1; i++)
        {
            bot.Think(server, rec);
        }

        Assert.Null(bot.Target);

        // A dead target is dropped at once.
        (server, bot, rec) = Setup(World(wall: false), 1, new Vector3(0, 0, -900));
        bot.Think(server, rec);
        server.Players[2].Alive = false;
        bot.Think(server, rec);
        Assert.Null(bot.Target);
    }

    [Fact]
    public void It_roams_to_items_takes_what_it_walks_over_and_jumps_a_kerb()
    {
        var kerb = Quad(new(-400, 0, -300), new(400, 0, -300), new(400, 40, -300), new(-400, 40, -300))
            .Concat(Quad(new(-400, 40, -300), new(400, 40, -300), new(400, 40, -3000), new(-400, 40, -3000)));
        var world = new CollisionWorld().Add(new TriangleGrid(Floor().Concat(kerb)));
        var (server, bot, rec) = Setup(world, 1);
        server.SetArena(new DmArena
        {
            Map = 605, Spawns = [new DmSpawn(new Vector3(0, 1, 0), 0f)], ItemSpots = [new Vector3(0, 40, -1200)],
        });
        server.Drop(25601, new Vector3(0, 40, -1200));
        var key = server.Pickups.Keys.Single();
        bot.PickGoal(server);
        Assert.Equal(new Vector3(0, 40, -1200), bot.Goal);
        var jumped = false;
        for (var t = 0f; t < 8f && rec.Pickups.Count == 0; t += Dt)
        {
            bot.Step(Dt, server, rec);
            jumped |= bot.Jumped;
        }

        Assert.True(jumped, "never jumped the 40-unit kerb");
        Assert.Contains(key, rec.Pickups);
        Assert.True(bot.Body.Position.Y > 30f, "on top of the kerb");
        Assert.True(bot.Walked > 1000f);
    }

    [BehaviourDataFact]
    public void Three_bots_on_map_605_move_fire_and_score_in_a_minute()
    {
        var session = new GameSession(BehaviourData.Data);
        Assert.True(session.StartDeathmatch(605));
        var arena = DmBots.Cleared(DmArena.Load(BehaviourData.Data, 605)!, session);
        var net = new LoopbackNetwork();
        var settings = new DmSettings { Map = 605, Bots = 3, BotSkill = 1, Items = NetLevelDefaults(605) };
        using var host = new DmHost(new NetEndpoint(net.Listen(0, DmProtocol.MaxPeers)!), new DmServer(settings, new Random(11)), "HOST", DmClass.Human);
        host.Server.SetArena(arena);
        host.ReportLevelReady();
        var bots = DmBots.Start(host, session.World, session.Weapons.Table, seed: 42);
        Assert.Equal(3, bots.Brains.Count);
        Assert.All(host.Server.Players.Values.Where(p => p.Bot), p => Assert.True(p.Alive));
        var start = bots.Brains.ToDictionary(b => b.Key, b => b.Value.Body.Position);
        var fired = new HashSet<int>();
        host.Fired += (id, _, _, _) => fired.Add(id);
        var poses = new HashSet<int>();
        host.PoseReceived += (id, _, _, _, _, _) => poses.Add(id);
        var deaths = 0;
        host.Server.Died += (_, killer, _) => deaths += killer < 0 ? 1 : 0;

        for (var i = 0; i < 60 * 60; i++)
        {
            host.Update(Dt);
        }

        output.WriteLine($"{deaths} deaths by a bot's hand");
        foreach (var p in host.Server.Scoreboard())
        {
            var b = bots.Brains.GetValueOrDefault(p.Id);
            output.WriteLine($"{p.Id} {p.Name}: {p.Frags} frags, {p.Deaths} deaths, {p.Points} points, health {p.Health:0}" +
                (b != null ? $", walked {b.Walked:0}, {b.Shots} shots, {b.Hits} hits, weapon {b.Weapon}" : ""));
        }

        foreach (var (id, b) in bots.Brains)
        {
            Assert.True(b.Walked > 500f, $"bot {id} walked only {b.Walked:0}");
            Assert.True(float.IsFinite(b.Body.Position.Y) && b.Body.Position.Y > -5000f, $"bot {id} fell out at {b.Body.Position}");
        }

        Assert.Equal(3, poses.Count);
        Assert.True(fired.Count >= 2, $"only {fired.Count} bots fired");
        Assert.True(bots.Brains.Values.Sum(b => b.Hits) > 0, "no bot hit anybody");
        var scored = host.Server.Players.Values.Where(p => p.Bot).Sum(p => p.Points);
        Assert.True(scored > 0, $"the bots scored {scored} points");
        Assert.NotEqual(start.Values, bots.Brains.Values.Select(b => b.Body.Position));
    }

    private static System.Collections.Immutable.ImmutableDictionary<string, int> NetLevelDefaults(int map) =>
        SkyNet.Formats.NetLevelList.For(SkyNet.Formats.NetLevelList.Load(BehaviourData.Data), map).Items;
}
