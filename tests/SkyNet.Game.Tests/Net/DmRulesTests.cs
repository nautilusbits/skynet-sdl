using System.Collections.Immutable;
using System.Numerics;
using RetroEng.Net;
using RetroEng.Physics;
using SkyNet.Formats;
using SkyNet.Game.Behaviour;
using SkyNet.Game.Campaign;
using SkyNet.Game.Net;
using SkyNet.Game.Player;
using SkyNet.Game.Tests.Behaviour;
using SkyNet.Game.Weapons;

namespace SkyNet.Game.Tests.Net;

/// <summary>
/// The owner's deathmatch rules (docs/multiplayer.md "Rules"): DOS's NETLEVEL scoring and goal,
/// NEW ROUND resetting the map, level blasts owned by the server and credited to the breaker, the
/// travel guard on client poses, the bots' DOS weapons, pickups, bodies and falls, the cheats behind
/// the host's flag, the network events on the status lines.
/// </summary>
public class DmRulesTests
{
    private const float Dt = 1f / 60f;

    private static readonly DmArena Arena = new()
    {
        Map = 605,
        Spawns = [new(new Vector3(0, 0, 0), 0f), new(new Vector3(10000, 0, 0), 1f), new(new Vector3(0, 0, 10000), 2f)],
        ItemSpots = [],
    };

    private static DmServer Server(DmSettings settings, int humans = 3)
    {
        var s = new DmServer(settings, new Random(1));
        s.SetArena(Arena);
        for (var id = 1; id <= humans; id++)
        {
            s.AddHuman(id, $"P{id}", DmClass.Human);
            s.LevelReady(id);
        }

        return s;
    }

    // ---- 1. scoring and the goal ----

    [Fact]
    public void Netlevel_goal_and_dos_default_limits_make_the_settings()
    {
        var level = NetLevelList.Default(605) with { Goal = "points" };
        var s = new DmRunOptions().SettingsFor(level);
        Assert.Equal((DmGoal.Points, 10000), (s.Goal, s.GoalLimit));
        s = new DmRunOptions { Goal = DmGoal.Lives, GoalLimit = 3, TimeLimitMinutes = 7 }.SettingsFor(level);
        Assert.Equal((DmGoal.Lives, 3, 7 * 60.0), (s.Goal, s.GoalLimit, s.RoundSeconds));
        Assert.Equal(DmGoal.Kills, DmSettings.ParseGoal("KILLS"));
        Assert.Equal(DmGoal.Time, DmSettings.ParseGoal("time"));
        // A TIME goal's minutes and the TIME box: the shorter.
        Assert.Equal(5 * 60.0, new DmSettings { Goal = DmGoal.Time, GoalLimit = 5, TimeLimitMinutes = 20 }.RoundSeconds);
        Assert.Equal(DmSettings.DefaultLimit(DmGoal.Time) * 60.0, new DmRunOptions { Goal = DmGoal.Time }.SettingsFor(level).RoundSeconds);

        var o = DmHeadless.Parse(["--host=605", "--goal=points", "--goal-limit=1500", "--cheats"], out var error);
        Assert.Null(error);
        Assert.Equal((DmGoal.Points, 1500, true), (o!.Goal, o.GoalLimit, o.Cheats));
        Assert.Null(DmHeadless.Parse(["--host=605", "--goal=frags"], out error));
        Assert.NotNull(error);
    }

    [Fact]
    public void The_settings_carry_goal_limit_and_cheats_on_the_wire()
    {
        var w = new NetWriter().Clear();
        DmProtocol.WriteSettings(w, new DmSettings { Goal = DmGoal.Lives, GoalLimit = 4, Cheats = true, TimeLimitMinutes = 9 });
        var r = new NetReader(w.ToArray());
        var back = DmProtocol.ReadSettings(ref r);
        Assert.Equal((DmGoal.Lives, 4, true, 9), (back.Goal, back.GoalLimit, back.Cheats, back.TimeLimitMinutes));
    }

    [Fact]
    public void Dos_points_a_hit_a_killing_blow_and_a_death()
    {
        var s = Server(new DmSettings { GoalLimit = 0 });
        s.Hit(2, 10f, 1, 1);                         // a hit: +100
        Assert.Equal(100, s.Players[1].Points);
        s.Hit(2, 5000f, 1, 1);                       // the killing blow: +500 and a kill, no hit
        Assert.Equal((600, 1), (s.Players[1].Points, s.Players[1].Frags));
        Assert.Equal((-400, 1, 0), (s.Players[2].Points, s.Players[2].Deaths, s.Players[2].Frags));
        s.Hit(3, 5000f, 3, 6);                       // his own rocket: only the death
        Assert.Equal((-400, 1, 0), (s.Players[3].Points, s.Players[3].Deaths, s.Players[3].Frags));
    }

    [Fact]
    public void A_points_game_ends_at_the_leaders_points_and_ranks_by_points()
    {
        var s = Server(new DmSettings { Goal = DmGoal.Points, GoalLimit = 700 });
        string? over = null;
        s.MatchOver += r => over = r;
        s.Hit(2, 5000f, 3, 1);       // P3 500
        s.Hit(3, 10f, 1, 1);         // P1 100
        Assert.Equal([3, 1, 2], s.Scoreboard().Select(p => p.Id));
        Assert.Null(over);
        s.Update(DmProtocol.RespawnDelay);
        s.Hit(1, 10f, 3, 1);         // P3 600
        s.Hit(1, 10f, 3, 1);         // P3 700: the goal
        Assert.Equal("P3 REACHED 700 POINTS", over);
        Assert.False(s.MatchRunning);
    }

    [Fact]
    public void A_lives_game_ends_when_somebody_has_died_the_limit_and_ranks_by_fewest_deaths()
    {
        var s = Server(new DmSettings { Goal = DmGoal.Lives, GoalLimit = 2 });
        string? over = null;
        s.MatchOver += r => over = r;
        s.Hit(2, 5000f, 1, 1);
        s.Update(DmProtocol.RespawnDelay);
        Assert.Equal(2, s.Scoreboard()[^1].Id);
        s.Hit(2, 5000f, 3, 1);
        Assert.Equal("P2 LOST 2 LIVES", over);
        Assert.Equal([1, 3, 2], s.Scoreboard().Select(p => p.Id)); // 0 deaths (P1, P3 tie: kills 1 each, points), then P2
    }

    [Fact]
    public void A_time_goal_runs_its_minutes()
    {
        var s = Server(new DmSettings { Goal = DmGoal.Time, GoalLimit = 1 });
        Assert.Equal(60.0, s.TimeLeft);
        string? over = null;
        s.MatchOver += r => over = r;
        s.Update(59.0);
        Assert.Null(over);
        s.Update(1.5);
        Assert.Equal("TIME LIMIT REACHED", over);
    }

    // ---- 4. the travel guard ----

    [Fact]
    public void A_forged_pose_is_clamped_to_what_the_body_could_run()
    {
        var s = Server(new DmSettings());
        var p = s.Players[2];
        var at = p.Position;
        s.Update(DmServer.RespawnFresh + 0.5);
        // Running: 20 poses a second at the class's top speed are all taken.
        var top = s.TopSpeed(2);
        Assert.Equal(MathF.Sqrt(400f * 400f + 310f * 310f) * DmProtocol.SpeedFactor(DmClass.Human, DmClassRules.Balanced), top, 1);
        var pos = at;
        for (var i = 0; i < 20; i++)
        {
            s.Update(0.05);
            pos += new Vector3(top * 0.05f, 0f, 0f);
            Assert.True(s.ClientPose(2, pos, 0, 0, 0, float.NaN, out var accepted));
            Assert.Equal(pos, accepted);
        }

        // A jump of 5000 units next to an item: clamped to the budget, and logged.
        var log = new List<string>();
        s.Warned += log.Add;
        s.Update(0.05);
        var far = pos + new Vector3(0f, 0f, 5000f);
        Assert.True(s.ClientPose(2, far, 0, 0, 0, float.NaN, out var clamped));
        Assert.True(Vector3.Distance(clamped, pos) <= top * DmServer.TravelSlack * DmServer.TravelBurst + DmServer.TravelMargin + 1f);
        Assert.Equal(clamped, s.Players[2].Position);
        Assert.Equal(1, s.PosesClamped);
        Assert.Single(log);
        // A fall is not a run: 1000 straight down in a second is fine.
        s.Update(1.0);
        Assert.True(s.ClientPose(2, clamped - new Vector3(0f, 1000f, 0f), 0, 0, 0, float.NaN, out var fell));
        Assert.Equal(clamped - new Vector3(0f, 1000f, 0f), fell);
    }

    [Fact]
    public void A_stale_pose_after_a_respawn_is_dropped_and_the_dead_do_not_move()
    {
        var s = Server(new DmSettings());
        s.Update(DmServer.RespawnFresh + 0.5);
        var died = s.Players[2].Position;
        s.Hit(2, 5000f, 1, 1);
        Assert.True(s.ClientPose(2, died + new Vector3(3000f, 0f, 0f), 0, 0, 0, float.NaN, out var lying));
        Assert.Equal(died, lying); // the body lies where it fell
        s.Update(DmProtocol.RespawnDelay);
        var spawn = s.Players[2].Position;
        Assert.False(s.ClientPose(2, spawn + new Vector3(4000f, 0f, 0f), 0, 0, 0)); // sent before the respawn reached the client
        Assert.Equal(spawn, s.Players[2].Position);
        Assert.Equal(1, s.PosesDropped);
        Assert.True(s.ClientPose(2, spawn + new Vector3(5f, 0f, 0f), 0, 0, 0)); // a step from the spawn
    }

    [Fact]
    public void A_vehicle_has_its_own_top_speed()
    {
        var s = Server(new DmSettings { Items = ImmutableDictionary<string, int>.Empty.Add("jeeps", 1).Add("hks", 1) });
        s.PlaceVehicles([s.Players[2].Position], [s.Players[3].Position]);
        Assert.True(s.EnterVehicle(2, 1));
        Assert.True(s.EnterVehicle(3, 2));
        Assert.Equal(SkyNet.Game.Vehicles.VehicleController.JeepMaxSpeed, s.TopSpeed(2));
        Assert.True(s.TopSpeed(3) > SkyNet.Game.Vehicles.VehicleController.HkSpeed);
        Assert.True(s.TopSpeed(1) < s.TopSpeed(2));
    }

    // ---- 2 and 3. the map's own state: NEW ROUND and level blasts ----

    private static (DmHost Host, DmLevelServer Level, LoopbackNetwork Net) LevelHost(int arena)
    {
        var map = BehaviourData.Data.LoadMap(arena)!;
        var b = new LevelBehaviour(map, BehaviourData.Transfrm, new MissionProgress(0));
        var away = new Vector3(-1e7f);
        b.Start(away, away);
        var net = new LoopbackNetwork();
        var host = new DmHost(new NetEndpoint(net.Listen(0, DmProtocol.MaxPeers)!),
            new DmServer(new DmSettings { Map = arena, Items = ImmutableDictionary<string, int>.Empty }, new Random(5)) { LimitTravel = false },
            "HOST", DmClass.Human);
        host.Server.SetArena(new DmArena { Map = arena, Spawns = [new(Vector3.Zero, 0f)], ItemSpots = [] });
        host.ReportLevelReady();
        host.Level = new DmLevelServer(b);
        return (host, host.Level, net);
    }

    [BehaviourDataFact]
    public void New_round_puts_the_map_back_on_the_server_and_on_every_client()
    {
        var (host, level, net) = LevelHost(605);
        var server = level.Behaviour;
        var map = BehaviourData.Data.LoadMap(605)!;
        var mirror = new LevelBehaviour(map, BehaviourData.Transfrm, new MissionProgress(0));
        mirror.Start(new Vector3(-1e7f), new Vector3(-1e7f));
        var c = new DmClient(new NetEndpoint(net.Connect(host.Port)), "ALICE", DmClass.Human);
        var client = new DmLevelClient(mirror, i => c.SendLevelIntent(i));
        c.Welcomed += c.ReportLevelReady;
        c.LevelState += u => Assert.True(client.Apply(u));
        void Pump()
        {
            for (var i = 0; i < 4; i++)
            {
                host.Update(Dt);
                c.Update(Dt);
                server.Tick(Dt, new PlayerProbe(new Vector3(-1e7f), new Vector3(-1e7f)));
                mirror.Tick(Dt, new PlayerProbe(new Vector3(-1e7f), new Vector3(-1e7f)));
            }
        }

        Pump();
        var start = server.Records.ToDictionary(o => o, server.Capture);
        var crates = server.Records.Where(o => server.IsDamageable(o) && server.HasHp(o)).Take(3).ToList();
        Assert.NotEmpty(crates);
        foreach (var crate in crates)
        {
            server.Hit(crate, 1e6f);
        }

        Pump();
        Assert.All(crates, o => Assert.True(mirror.Spent(o), "the client sees it broken"));

        host.Server.Restart();
        Pump();
        Assert.Equal(1, client.Resets);
        foreach (var off in server.Records)
        {
            Assert.Equal(start[off], server.Capture(off));
            Assert.Equal(start[off], mirror.Capture(off));
        }

        Assert.All(crates, o => Assert.True(server.IsDamageable(o), "whole again"));
        Assert.Null(level.Delta()); // nothing left to send
    }

    [BehaviourDataFact]
    public void A_level_blast_kills_on_the_breakers_account()
    {
        var (host, level, _) = LevelHost(605);
        var s = host.Server;
        s.AddHuman(2, "BREAKER", DmClass.Human);
        s.AddHuman(3, "VICTIM", DmClass.Human);
        s.LevelReady(2);
        s.LevelReady(3);
        var b = level.Behaviour;
        var barrel = b.Records.First(o => b.IsDamageable(o) && b.HasHp(o));
        Assert.True(b.TryPositionOf(barrel, out var at));
        s.Players[2].Position = at + new Vector3(600f, 0f, 0f);
        s.Players[3].Position = at - new Vector3(0f, DmAvatar.Height / 2f, 0f); // right in it
        Assert.True(level.ClientHit(s, 2, barrel, 1e6f));
        Assert.Equal(2, b.BreakerOf(barrel));
        var died = new List<(int Victim, int Killer, int Weapon)>();
        s.Died += (v, k, w) => died.Add((v, k, w));
        var struck = level.Blast(s, barrel, at, 400f);
        Assert.Contains(struck, h => h.Victim == 3);
        Assert.DoesNotContain(struck, h => h.Victim == 2); // 600 away is out of a 400 blast's reach
        Assert.Equal([(3, 2, DmProtocol.LevelBlastWeapon)], died);
        Assert.Equal(1, s.Players[2].Frags);   // the breaker's kill, not the victim's suicide
        Assert.Equal(500, s.Players[2].Points);

        // A record nobody struck (the world's): a death in its blast is nobody's kill.
        s.Update(DmProtocol.RespawnDelay);
        s.Players[3].Position = at - new Vector3(0f, DmAvatar.Height / 2f, 0f);
        var other = b.Records.First(o => o != barrel && b.IsDamageable(o));
        Assert.Equal(0, b.BreakerOf(other));
        level.Blast(s, other, at, 400f);
        Assert.Equal((0, 2), (s.Players[3].Frags, s.Players[3].Deaths));
        Assert.Equal(1, s.Players[2].Frags);
    }

    [BehaviourDataFact]
    public void The_hosts_session_hands_a_broken_records_blast_to_the_server()
    {
        var session = new GameSession(BehaviourData.Data);
        Assert.True(session.StartDeathmatch(605));
        var b = session.Behaviour!;
        var level = new DmLevelServer(b);
        var s = new DmServer(new DmSettings { Items = ImmutableDictionary<string, int>.Empty }, new Random(2)) { LimitTravel = false };
        s.SetArena(Arena);
        s.AddHuman(1, "HOST", DmClass.Human);
        s.AddHuman(2, "CLIENT", DmClass.Human);
        s.LevelReady(1);
        s.LevelReady(2);
        DmGame.OwnLevelBlasts(session, level, s);
        // A record whose destruction has its own radial blast (the link record's i16).
        var map = session.Map!;
        var barrel = b.Records.FirstOrDefault(o => b.IsDamageable(o) && map.TryGetEntityAt(o, out var e) && e.Link is { DestroyParam: not 0 });
        Assert.NotEqual(0, barrel);
        Assert.True(b.TryPositionOf(barrel, out var at));
        s.Players[1].Position = at + new Vector3(0f, 0f, 30f);  // the host's own player stands at it
        session.Player.Spawn(at + new Vector3(0f, 0f, 30f), 0f);
        var hostHealth = session.Soldier.Health;
        Assert.True(level.ClientHit(s, 2, barrel, 1e6f));
        Assert.True(b.Spent(barrel));
        Assert.True(s.Players[1].Health < DmProtocol.DosHealth || !s.Players[1].Alive, "the server's blast struck the host");
        Assert.Equal(hostHealth, session.Soldier.Health); // not twice: the local blast spared him
        if (!s.Players[1].Alive)
        {
            Assert.Equal(1, s.Players[2].Frags);
        }
    }

    // ---- 5. bots ----

    private sealed class Recorder : IBotOutput
    {
        public readonly List<(int Victim, float Points, int Weapon)> Hits = [];
        public readonly List<float> SelfHits = [];
        public int Fired;
        public DmServer? Server;

        public void Pose(int id, Vector3 feet, float yaw, float pitch, int flags) => Server?.Pose(id, feet, yaw, pitch, flags);
        public void Fire(int id, int weapon, Vector3 from, Vector3 dir) => Fired++;
        public void Hit(int id, int victim, float points, int weapon) => Hits.Add((victim, points, weapon));
        public void SelfHit(int id, float points, int weapon) => SelfHits.Add(points);
        public void Pickup(int id, int key) => Server?.Pickup(key, id);
    }

    private static CollisionWorld Flat()
    {
        IEnumerable<Triangle> Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d) => [new Triangle(a, b, c), new Triangle(a, c, d)];
        return new CollisionWorld().Add(new TriangleGrid(Quad(new(-20000, 0, -20000), new(20000, 0, -20000), new(20000, 0, 20000), new(-20000, 0, 20000))));
    }

    private static (DmServer Server, BotBrain Bot, Recorder Out) BotSetup(int weapon, params Vector3[] humans)
    {
        var server = new DmServer(new DmSettings(), new Random(1));
        for (var i = 0; i < humans.Length; i++)
        {
            var h = server.AddHuman(2 + i, $"H{i}", DmClass.Human)!;
            (h.Alive, h.Position) = (true, humans[i]);
        }

        var p = server.AddBot(DmClass.Human);
        p.Alive = true;
        var bot = new BotBrain(p.Id, 2, Flat(), WeaponTable.Shipped, new Random(3));
        bot.Spawn(new Vector3(0, 1, 0), 0f);
        bot.Weapon = weapon;
        return (server, bot, new Recorder { Server = server });
    }

    private static void Run(DmServer server, BotBrain bot, Recorder rec, float seconds)
    {
        for (var t = 0f; t < seconds; t += Dt)
        {
            server.Update(Dt);
            if (server.Players[bot.Id].Alive)
            {
                bot.Step(Dt, server, rec);
                server.Players[bot.Id].Position = bot.Body.Position;
            }

            bot.StepShots(Dt, server, rec);
        }
    }

    [Fact]
    public void A_bots_bullet_deals_the_dos_ammo_points()
    {
        var (server, bot, rec) = BotSetup(2, new Vector3(0, 0, -1100));
        Run(server, bot, rec, 4f);
        Assert.NotEmpty(rec.Hits);
        var ammo = WeaponTable.Shipped.AmmoOf(WeaponTable.Shipped.Weapons[2])!;
        Assert.All(rec.Hits, h => Assert.Equal((2, ammo.Strength, 2), h));
    }

    [Fact]
    public void A_bots_laser_rifle_is_a_bolt_that_arrives_later()
    {
        var (server, bot, rec) = BotSetup(7, new Vector3(0, 0, -1400));
        var w = WeaponTable.Shipped.Weapons[7];
        var ammo = WeaponTable.Shipped.AmmoOf(w)!;
        Assert.Equal(AmmoFamily.Bolt, ammo.Family);
        var firstShotAt = -1f;
        for (var t = 0f; t < 4f && rec.Hits.Count == 0; t += Dt)
        {
            server.Update(Dt);
            bot.Step(Dt, server, rec);
            if (rec.Fired > 0 && firstShotAt < 0f)
            {
                firstShotAt = t;
                Assert.True(bot.InFlight > 0, "the shot flies");
                Assert.Empty(rec.Hits);
            }

            bot.StepShots(Dt, server, rec);
        }

        Assert.NotEmpty(rec.Hits);
        Assert.Equal((2, ammo.Strength), (rec.Hits[0].Victim, rec.Hits[0].Points));
    }

    [Fact]
    public void A_bots_shotgun_blasts_whoever_stands_by_the_one_it_hits()
    {
        var (server, bot, rec) = BotSetup(4, new Vector3(0, 0, -350), new Vector3(20, 0, -380));
        Run(server, bot, rec, 4f);
        var ammo = WeaponTable.Shipped.AmmoOf(WeaponTable.Shipped.Weapons[4])!;
        Assert.True(ammo.Blasts);
        Assert.Contains(rec.Hits, h => h.Points == ammo.Strength);              // a direct hit
        Assert.Contains(rec.Hits, h => h.Points < ammo.Strength && h.Points > 0); // the blast's falloff on the other
        Assert.Empty(rec.SelfHits); // 350 away: out of its own blast
    }

    [Fact]
    public void A_bot_takes_a_weapon_item_into_its_hand()
    {
        var (_, bot, _) = BotSetup(1);
        var rocket = PickupTable.Items.First(kv => kv.Value.Weapon == 6).Key;
        Assert.True(bot.Took(rocket));
        Assert.Equal(6, bot.Weapon);
        Assert.False(bot.Took(PickupTable.Items.First(kv => kv.Value.Weapon < 0).Key)); // ammo only: no change
        Assert.Equal(6, bot.Weapon);
        Assert.Equal(1, bot.WeaponsTaken);
    }

    [BehaviourDataFact]
    public void The_hosts_bots_put_the_weapons_they_pick_up_in_hand()
    {
        var session = new GameSession(BehaviourData.Data);
        Assert.True(session.StartDeathmatch(605));
        var net = new LoopbackNetwork();
        using var host = new DmHost(new NetEndpoint(net.Listen(0, DmProtocol.MaxPeers)!),
            new DmServer(new DmSettings { Map = 605, Bots = 1, Items = ImmutableDictionary<string, int>.Empty }, new Random(4)), "HOST", DmClass.Human);
        host.Server.SetArena(DmBots.Cleared(DmArena.Load(BehaviourData.Data, 605)!, session));
        host.ReportLevelReady();
        var bots = DmBots.Start(host, session.World, session.Weapons.Table, seed: 1);
        var (id, brain) = bots.Brains.First();
        var launcher = PickupTable.Items.First(kv => kv.Value.Weapon == 5).Key;
        var item = host.Server.Drop(launcher, host.Server.Players[id].Position)!;
        Assert.True(host.Server.Pickup(item.Key, id));
        Assert.Equal(5, brain.Weapon);
    }

    [Fact]
    public void A_bot_falling_from_a_height_hurts_itself()
    {
        var (server, bot, rec) = BotSetup(1);
        bot.Spawn(new Vector3(5000, 2000, 5000), 0f);
        Run(server, bot, rec, 6f);
        Assert.NotEmpty(rec.SelfHits);
        Assert.True(rec.SelfHits.Sum() > 0f);
    }

    [Fact]
    public void Bodies_block_each_other()
    {
        var world = Flat();
        var body = new PlayerController(world).Body;
        var other = (new Vector3(100, 0, 0), BodyBlock.Radius);
        // Walking into a body stops at the touching distance.
        var to = BodyBlock.Separate(new Vector3(40, 0, 0), new Vector3(90, 0, 0), [other], world, body);
        Assert.Equal(100f - 2 * BodyBlock.Radius, to.X, 2);
        // Above its head: no contact.
        Assert.Equal(new Vector3(100, 200, 0), BodyBlock.Separate(new Vector3(40, 200, 0), new Vector3(100, 200, 0), [other], world, body));

        // A bot whose step ends inside a player's body is put beside it.
        var (server, bot, rec) = BotSetup(1, new Vector3(0, 0, -10));
        for (var t = 0f; t < 1f; t += Dt)
        {
            server.Update(Dt);
            bot.Step(Dt, server, rec);
            var d = new Vector2(bot.Body.Position.X, bot.Body.Position.Z + 10f).Length();
            Assert.True(d >= 2 * BodyBlock.Radius - 0.5f, $"inside the player: {d:0.0} at {t:0.00} s");
        }
    }
}
