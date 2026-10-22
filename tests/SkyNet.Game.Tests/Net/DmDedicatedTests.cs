using System.Collections.Immutable;
using System.Diagnostics;
using System.Net;
using System.Numerics;
using System.Text;
using RetroEng.Json;
using RetroEng.Net;
using SkyNet.Game.Net;
using SkyNet.Game.Tests.Behaviour;

namespace SkyNet.Game.Tests.Net;

/// <summary>The dedicated server: its config, the door rules (password, bans, idle), and a whole
/// round with two clients over 127.0.0.1 that ends in a map rotation.</summary>
public sealed class DmDedicatedTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"skynet-ded-{Environment.ProcessId}-{Guid.NewGuid():N}");

    public DmDedicatedTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        foreach (var c in _clients)
        {
            c.Dispose();
        }

        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static ServerConfig Parse(string json) => ServerConfig.Parse(Encoding.UTF8.GetBytes(json));

    // ---- config ----

    [Fact]
    public void A_full_config_reads()
    {
        var c = Parse("""
            {
              // comments and trailing commas are fine
              "name": "BIG SERVER", "port": 27020, "bind": "127.0.0.1", "max_players": 12, "max_per_address": 3,
              "password": "sesame", "cheats": true, "public": true, "master": "master.example:27950",
              "discovery": false, "motd": "Hi", "log_file": "x/server.log", "ban_file": "b.json",
              "idle_kick_seconds": 120, "timeout_seconds": 15, "intermission_seconds": 5,
              "bots": 3, "bot_skill": 2, "goal": "points", "goal_limit": 5000, "time": 15,
              "rotation": [
                "MAP.605",
                { "map": 601, "goal": "kills", "goal_limit": 10, "time": 0, "bots": 0, "replenish": false, "items": { "health": 2 } },
              ],
            }
            """);
        Assert.Equal(("BIG SERVER", 27020, IPAddress.Loopback, 12, 3, true, true, false), (c.Name, c.Port, c.Bind, c.MaxPlayers, c.MaxPerAddress, c.Cheats, c.Public, c.Discovery));
        Assert.Equal(DmProtocol.PasswordKey("sesame"), c.PasswordKey);
        Assert.Equal((120.0, 15.0, 5.0, 3, 2, DmGoal.Points, 5000, 15), (c.IdleKickSeconds, c.TimeoutSeconds, c.IntermissionSeconds, c.Bots, c.BotSkill, c.Goal!.Value, c.GoalLimit!.Value, c.TimeLimitMinutes));
        Assert.Equal([605, 601], c.Rotation.Select(r => r.Map));
        Assert.Equal(2, c.Rotation[1].Items!["health"]);
    }

    [Fact]
    public void An_empty_config_takes_the_defaults()
    {
        var c = Parse("{}");
        Assert.Equal((DmProtocol.DefaultPort, 8, 2, true, (byte[]?)null), (c.Port, c.MaxPlayers, c.MaxPerAddress, c.Discovery, c.PasswordKey));
        Assert.Equal(605, Assert.Single(c.Rotation).Map);
    }

    [Theory]
    [InlineData("{\n  \"port\": 70000\n}", "$.port (line 2, column 11)")]
    [InlineData("{\n  \"prot\": 1\n}", "$.prot (line 2, column 11): unknown key \"prot\"")]
    [InlineData("{ \"rotation\": [ { \"map\": \"MAP.9X\" } ] }", "$.rotation[0].map (line 1, column 26)")]
    [InlineData("{ \"rotation\": [ { \"map\": 605, \"goal\": \"frags\" } ] }", "expected points, kills, lives or time")]
    [InlineData("{ \"rotation\": [ { \"map\": 605, \"items\": { \"cake\": 1 } } ] }", "unknown item \"cake\"")]
    [InlineData("{ \"rotation\": [] }", "the rotation needs at least one map")]
    [InlineData("{ \"rotation\": [ { \"goal\": \"kills\" } ] }", "\"map\" is missing")]
    [InlineData("{ \"bind\": \"localhost\" }", "expected an IP address")]
    [InlineData("{ \"password\": \"a\", \"password_sha256\": \"00\" }", "not both")]
    [InlineData("{ \"password_sha256\": \"abcd\" }", "64 hex digits")]
    [InlineData("{ \"name\": 5 }", "$.name (line 1, column 11): expected a string")]
    [InlineData("{ \"port\": 1,, }", "line 1")]
    [InlineData("{ \"classes\": \"godot\" }", "$.classes (line 1, column 14): expected dos or balanced, found \"godot\"")]
    public void Config_errors_say_where(string json, string expected)
    {
        var e = Assert.Throws<JsonDataException>(() => Parse(json));
        Assert.Contains(expected, e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_config_file_error_names_the_file()
    {
        var path = Path.Combine(_dir, "server.json");
        File.WriteAllText(path, "{\n  \"bots\": \"many\"\n}\n");
        var e = Assert.ThrowsAny<Exception>(() => ServerConfig.Load(path));
        Assert.Equal($"{path}: $.bots (line 2, column 11): expected an integer, found \"many\"", ServerConfig.Describe(path, e));
        Assert.EndsWith("no such file", ServerConfig.Describe("nope.json", Assert.ThrowsAny<Exception>(() => ServerConfig.Load(Path.Combine(_dir, "nope.json")))));
    }

    [Fact]
    public void A_password_hash_stands_in_for_the_password()
    {
        var hex = Convert.ToHexString(DmProtocol.PasswordKey("sesame"));
        Assert.Equal(DmProtocol.PasswordKey("sesame"), Parse($"{{ \"password_sha256\": \"{hex.ToLowerInvariant()}\" }}").PasswordKey);
        Assert.Null(Parse("{ \"password\": \"\" }").PasswordKey);
    }

    [Fact]
    public void Classes_come_from_the_config_and_the_host_screen()
    {
        Assert.Equal(DmClassRules.Balanced, Parse("{}").Classes); // the default
        var c = Parse("{ \"classes\": \"DOS\", \"rotation\": [ \"MAP.605\", \"MAP.601\" ] }");
        Assert.Equal(DmClassRules.Dos, c.Classes);
        Assert.Equal(DmClassRules.Dos, c.OptionsFor(1).Classes);
        Assert.Equal(DmClassRules.Dos, c.SettingsFor(1, SkyNet.Formats.NetLevelList.Default(601)).Classes);

        // The menu's DEDICATED: the host screen's CLASSES and PASSWORD (only its key) go to the server.
        var menu = ServerConfig.FromOptions(new DmRunOptions { HostMap = 605, Classes = DmClassRules.Dos, Password = "sesame" }, _dir);
        Assert.Equal(DmClassRules.Dos, menu.Classes);
        Assert.Equal(DmProtocol.PasswordKey("sesame"), menu.PasswordKey);
        Assert.Equal(DmClassRules.Balanced, ServerConfig.FromOptions(new DmRunOptions { HostMap = 605 }, _dir).Classes);
        Assert.Null(ServerConfig.FromOptions(new DmRunOptions { HostMap = 605 }, _dir).PasswordKey);
    }

    [Fact]
    public void Classes_and_a_password_come_from_the_command_line()
    {
        var o = DmHeadless.Parse(["--host=MAP.605", "--classes=dos", "--password", "sesame"], out var error);
        Assert.True(o != null, error);
        Assert.Equal(DmClassRules.Dos, o.Classes);
        Assert.Equal(DmClassRules.Dos, o.SettingsFor(SkyNet.Formats.NetLevelList.Default(605)).Classes);
        Assert.Equal(DmProtocol.PasswordKey("sesame"), o.HostPolicy().PasswordKey);
        Assert.Equal(DmClassRules.Balanced, DmHeadless.Parse(["--host=MAP.605"], out _)!.Classes);
        Assert.Null(DmHeadless.Parse(["--host=MAP.605"], out _)!.HostPolicy().PasswordKey);
        Assert.Null(DmHeadless.Parse(["--host=MAP.605", "--classes=godot"], out error));
        Assert.Equal("--classes takes dos or balanced, not 'godot'", error);
        Assert.Contains("--classes", DmHeadless.Split(["--classes", "dos", "--map", "605"]).Net);
    }

    [Fact]
    public void Terminator_vision_comes_from_the_command_line_and_is_off_by_default()
    {
        Assert.False(DmHeadless.Parse(["--host=MAP.605", "--class=terminator"], out _)!.PointCloudVision);
        var o = DmHeadless.Parse(["--host=MAP.605", "--class=terminator", "--vision=pointcloud"], out var error);
        Assert.True(o != null, error);
        Assert.True(o.PointCloudVision);
        Assert.False(DmHeadless.Parse(["--join=127.0.0.1", "--vision", "normal"], out _)!.PointCloudVision);
        Assert.Null(DmHeadless.Parse(["--host=MAP.605", "--vision=hologram"], out error));
        Assert.Equal("--vision takes normal or pointcloud, not 'hologram'", error);
        Assert.Contains("--vision", DmHeadless.Split(["--vision", "pointcloud", "--map", "605"]).Net);
    }

    // ---- rotation ----

    [Fact]
    public void Rotation_entries_override_the_defaults_and_wrap()
    {
        var c = Parse("""
            { "bots": 2, "time": 7, "goal": "kills", "goal_limit": 9,
              "rotation": [ "MAP.605", { "map": "MAP.601", "bots": 0, "goal_limit": 3, "items": { "health": 1 } } ] }
            """);
        Assert.Equal((605, 2, 7, 9), (c.OptionsFor(0).HostMap!.Value, c.OptionsFor(0).Bots, c.OptionsFor(0).TimeLimitMinutes, c.OptionsFor(0).GoalLimit!.Value));
        Assert.Equal((601, 0, 7, 3), (c.OptionsFor(1).HostMap!.Value, c.OptionsFor(1).Bots, c.OptionsFor(1).TimeLimitMinutes, c.OptionsFor(1).GoalLimit!.Value));
        Assert.Equal(605, c.OptionsFor(2).HostMap);
        Assert.Equal(601, c.OptionsFor(-1).HostMap);

        // An entry's item counts go over the arena's own; the rest stay NETLEVEL's.
        var level = SkyNet.Formats.NetLevelList.Default(601) with { Items = ImmutableDictionary<string, int>.Empty.Add("health", 5).Add("armor", 4) };
        var s = c.SettingsFor(1, level);
        Assert.Equal((1, 4, "SKYNET DEDICATED", DmGoal.Kills, 3), (s.Items["health"], s.Items["armor"], s.ServerName, s.Goal, s.GoalLimit));
    }

    // ---- the door: password, idle ----

    private sealed class Rig
    {
        public readonly LoopbackNetwork Net = new();
        public readonly DmHost Host;

        public Rig(DmHostPolicy policy)
        {
            Host = new DmHost(new NetEndpoint(Net.Listen(0, DmProtocol.MaxPeers)!), new DmServer(new DmSettings(), new Random(3)) { LimitTravel = false }, null, DmClass.Human)
            {
                Policy = policy,
            };
            Host.Server.SetArena(new DmArena { Map = 605, Spawns = [new(Vector3.Zero, 0f), new(new Vector3(5000, 0, 0), 0f)], ItemSpots = [] });
        }

        public DmClient Join(string name, string? password = null) => new(new NetEndpoint(Net.Connect(Host.Port)), name, DmClass.Human, password);

        public void Pump(double dt = 0.01, params DmClient[] clients)
        {
            for (var i = 0; i < 4; i++)
            {
                Host.Update(dt);
                foreach (var c in clients)
                {
                    c.Update(dt);
                }
            }
        }
    }

    [Theory]
    [InlineData("sesame", null)]
    [InlineData("wrong", "Wrong password.")]
    [InlineData(null, "This server needs a password.")]
    public void A_passworded_server_checks_the_proof(string? given, string? refused)
    {
        var rig = new Rig(new DmHostPolicy { PasswordKey = DmProtocol.PasswordKey("sesame") });
        var c = rig.Join("ALICE", given);
        string? gone = null;
        c.Disconnected += r => gone = r;
        rig.Pump(0.01, c);
        Assert.Equal(refused, gone);
        Assert.Equal(refused == null, c.Active);
        Assert.Equal(refused == null ? 1 : 0, rig.Host.Server.Humans);
        Assert.False(rig.Host.Dedicated && rig.Host.Server.Players.ContainsKey(DmServer.HostId));
    }

    [Fact]
    public void A_dedicated_host_has_no_player_of_its_own()
    {
        var rig = new Rig(new DmHostPolicy());
        Assert.True(rig.Host.Dedicated);
        Assert.Equal(0, rig.Host.LocalId);
        Assert.Empty(rig.Host.Server.Players);
        var c = rig.Join("ALICE");
        rig.Pump(0.01, c);
        Assert.Equal([2], rig.Host.Server.Players.Keys);
        Assert.Equal([2], c.Players.Keys);
    }

    [Fact]
    public void An_idle_client_is_dropped_and_a_moving_one_kept()
    {
        var rig = new Rig(new DmHostPolicy { IdleSeconds = 5 });
        var idle = rig.Join("IDLE");
        var busy = rig.Join("BUSY");
        string? gone = null;
        idle.Disconnected += r => gone = r;
        rig.Pump(0.01, idle, busy);
        for (var t = 0; t < 40; t++)
        {
            // BUSY walks; IDLE sends the same pose over and over.
            busy.SendPose(new Vector3(t * 10, 0, 0), 0f, 0f, DmProtocol.FlagMoving, 1.0);
            idle.SendPose(Vector3.Zero, 0f, 0f, 0, 1.0);
            rig.Pump(0.05, idle, busy);
        }

        Assert.Equal("Idle for 5 seconds.", gone);
        Assert.True(busy.Active);
        Assert.Equal(["BUSY"], rig.Host.Server.Players.Values.Select(p => p.Name));
    }

    [Fact]
    public void A_kicked_player_is_told_why()
    {
        var rig = new Rig(new DmHostPolicy());
        var c = rig.Join("ALICE");
        string? gone = null;
        c.Disconnected += r => gone = r;
        rig.Pump(0.01, c);
        Assert.False(rig.Host.KickPlayer(-1, "bot"));
        Assert.True(rig.Host.KickPlayer(c.LocalId, "Kicked: spam"));
        rig.Pump(0.01, c);
        Assert.Equal("Kicked: spam", gone);
        Assert.Empty(rig.Host.Server.Players);
    }

    // ---- bans ----

    [Fact]
    public void The_ban_list_is_saved_and_read_back()
    {
        var path = Path.Combine(_dir, "sub", "bans.json");
        var bans = BanList.Load(path);
        Assert.Empty(bans.Entries);
        bans.Add(IPAddress.Parse("203.0.113.7"), "MALLORY", "aimbot");
        bans.Add(IPAddress.Parse("::ffff:198.51.100.2"), "", "");
        Assert.True(bans.IsBanned(IPAddress.Parse("::ffff:203.0.113.7")));
        var again = BanList.Load(path);
        Assert.Equal(["203.0.113.7", "198.51.100.2"], again.Entries.Select(b => b.Address));
        Assert.Equal(("MALLORY", "aimbot"), (again.Entries[0].Name, again.Entries[0].Reason));
        Assert.True(again.Remove(IPAddress.Parse("203.0.113.7")));
        Assert.False(again.Remove(IPAddress.Parse("203.0.113.7")));
        Assert.Single(BanList.Load(path).Entries);
        File.WriteAllText(path, "{ \"bans\": [ { \"address\": \"nope\" } ] }");
        Assert.Contains("$.bans[0].address", Assert.Throws<JsonDataException>(() => BanList.Load(path)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_screened_connection_is_refused_with_the_reason()
    {
        using var server = UdpTransport.Listen(0, "TEST", 4, IPAddress.Loopback)!;
        server.Screen = ep => IPAddress.IsLoopback(ep.Address) ? "You are banned from this server." : null;
        using var client = new NetEndpoint(UdpTransport.Connect("127.0.0.1", server.LocalPort, "TEST")!);
        var serverSide = new NetEndpoint(server);
        string? reason = null;
        DisconnectCause? cause = null;
        client.Disconnected += (_, c, r) => (cause, reason) = (c, r);
        var clock = Stopwatch.StartNew();
        while (reason == null && clock.Elapsed < TimeSpan.FromSeconds(5))
        {
            serverSide.Poll();
            client.Poll();
            Thread.Sleep(5);
        }

        Assert.Equal(DisconnectCause.Rejected, cause);
        Assert.Equal("You are banned from this server.", reason);
    }

    // ---- a whole round over 127.0.0.1 ----

    [BehaviourDataFact]
    public void Two_clients_play_a_round_and_the_server_rotates_the_map()
    {
        var config = Parse("""
            { "name": "TEST", "port": 0, "bind": "127.0.0.1", "discovery": false, "log_file": "", "ban_file": "",
              "intermission_seconds": 0.5, "idle_kick_seconds": 0, "motd": "welcome", "record_demos": true,
              "rotation": [ { "map": "MAP.605", "goal": "kills", "goal_limit": 1, "time": 0 },
                            { "map": "MAP.601", "goal": "kills", "goal_limit": 1, "time": 0, "bots": 1 } ] }
            """) with { BanFile = Path.Combine(_dir, "bans.json"), DemoFolder = Path.Combine(_dir, "demos") };
        using var server = DmDedicated.Start(BehaviourData.Data, config, out var error) ?? throw new InvalidOperationException(error);
        Assert.Equal(0, server.Host.LocalId);
        Assert.Contains("SDL3", DmDedicatedRunner.SdlState(), StringComparison.Ordinal);

        var a = Client("ALPHA");
        var b = Client("BRAVO");
        var maps = new Dictionary<DmClient, List<int>> { [a] = [], [b] = [] };
        var motd = new List<string>();
        foreach (var c in new[] { a, b })
        {
            var me = c;
            c.Welcomed += () =>
            {
                maps[me].Add(me.Settings!.Map);
                me.ReportLevelReady();
            };
            c.ChatReceived += (from, text) => motd.Add($"{from}:{text}");
        }

        var over = new List<string>();
        a.MatchOver += over.Add;
        Run(() => a.Active && b.Active && a.Players.TryGetValue(a.LocalId, out var pa) && pa.Alive && b.Players.TryGetValue(b.LocalId, out var pb) && pb.Alive);
        Assert.Equal(["0:welcome", "0:welcome"], motd);
        Assert.Equal((605, 2), (server.Server.Settings.Map, server.Server.Humans));

        // ALPHA shoots BRAVO until the first kill ends the round; the server moves both to MAP.601.
        var shot = 0.0;
        Run(() =>
        {
            if (a.MatchRunning && Clock() >= shot && a.Players.TryGetValue(b.LocalId, out var victim) && victim.Alive)
            {
                shot = Clock() + 0.2;
                a.SendPose(a.Players[a.LocalId].Position, 0f, 0f, 0, 1.0);
                a.SendHit(b.LocalId, 100f, 1);
            }

            return maps[a].Count >= 2 && maps[b].Count >= 2;
        });
        Assert.Equal(["ALPHA REACHED 1 KILLS"], over);
        Assert.Equal([605, 601], maps[a]);
        Assert.Equal([605, 601], maps[b]);
        Assert.Equal((601, 1, 1), (server.Server.Settings.Map, server.Rounds, server.Rotations));
        Assert.Equal((2, 3), (a.LocalId, b.LocalId));
        Assert.All(server.Server.Players.Values.Where(p => !p.Bot), p => Assert.Equal(0, p.Frags));
        Run(() => server.Server.BotCount == 1 && a.Players.Values.Any(p => p.Bot));

        // The console: kick BRAVO, then ban ALPHA's address; a new join from there is refused.
        string? bGone = null, aGone = null;
        b.Disconnected += r => bGone = r;
        a.Disconnected += r => aGone = r;
        Assert.StartsWith("kicked BRAVO", server.Execute("kick 3 too good"), StringComparison.Ordinal);
        Run(() => bGone != null);
        Assert.Equal("Kicked: too good", bGone);
        Assert.StartsWith("banned ALPHA (127.0.0.1)", server.Execute("ban 2 testing"), StringComparison.Ordinal);
        Run(() => aGone != null);
        Assert.Equal("Banned: testing", aGone);
        Assert.Single(BanList.Load(config.BanFile).Entries);
        var c3 = Client("CHARLIE");
        string? cGone = null;
        c3.Disconnected += r => cGone = r;
        Run(() => cGone != null);
        Assert.Equal("You are banned from this server: testing", cGone);
        Assert.Equal("unbanned 127.0.0.1", server.Execute("unban 127.0.0.1"));
        Assert.Contains("MAP.601", server.Execute("status"), StringComparison.Ordinal);
        Assert.Equal("stopping", server.Execute("quit"));
        Assert.True(server.Stopped);

        // record_demos: a network demo a round (written at the rotation and at the stop), each what a
        // client playing as nobody was sent; the first round's plays back to ALPHA's kill and the end.
        var demos = Directory.GetFiles(config.DemoFolder, "*.sndemo").Order().Select(RetroEng.Loop.NetDemoFile.Read).ToList();
        Assert.Equal(2, demos.Count);
        Assert.Equal(["605", "601"], demos.Select(d => d.Field(SkyNet.Game.Demos.NetDemo.KeyMap)));
        Assert.All(demos, d => Assert.Equal("dedicated", d.Field(SkyNet.Game.Demos.NetDemo.KeyRole)));
        var replaySession = new SkyNet.Game.Campaign.GameSession(BehaviourData.Data);
        var player = new SkyNet.Game.Demos.NetDemoPlayer(demos[0]);
        using (var replay = DmGame.StartReplay(replaySession, player))
        {
            Assert.Equal(DmGame.DemoCamera.Chase, replay.Camera);
            for (var i = 0; i < 100000 && !player.Done; i++)
            {
                replay.Frame(0.05f);
            }

            Assert.Equal("ALPHA REACHED 1 KILLS", replay.RoundOver);
            Assert.Equal(1, replay.Players.Values.Single(p => p.Name == "ALPHA").Frags);
            Assert.Equal(605, replaySession.MapNumber);
        }

        DmClient Client(string name)
        {
            var c = new DmClient(new NetEndpoint(UdpTransport.Connect("127.0.0.1", server.Port, DmProtocol.AppKey)!), name, DmClass.Human);
            _clients.Add(c);
            return c;
        }

        double Clock() => _clock.Elapsed.TotalSeconds;

        void Run(Func<bool> done)
        {
            var until = Clock() + 20;
            var last = Clock();
            while (!done())
            {
                Assert.True(Clock() < until, "timed out");
                var now = Clock();
                server.Update(now - last);
                last = now;
                foreach (var c in _clients)
                {
                    c.Update(0.01);
                }

                Thread.Sleep(5);
            }
        }
    }

    private readonly List<DmClient> _clients = [];
    private readonly Stopwatch _clock = Stopwatch.StartNew();
}
