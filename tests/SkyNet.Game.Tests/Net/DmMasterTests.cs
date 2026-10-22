using System.Diagnostics;
using System.Net;
using RetroEng.Net;
using RetroEng.Net.Master;
using SkyNet.Game.Menu;
using SkyNet.Game.Net;
using SkyNet.Game.Tests.Behaviour;
using SkyNet.Game.Tests.Menu;

namespace SkyNet.Game.Tests.Net;

/// <summary>The master server with the game (docs/multiplayer.md "Master server"), all on 127.0.0.1
/// with ephemeral ports: a public dedicated server is listed and joined through the introduction, the
/// join screen's INTERNET tab and PASSWORD box, the host screen's PUBLIC, SERVER STATUS's YES / NO.</summary>
public sealed class DmMasterTests : IDisposable
{
    private static readonly MenuFrame Frame = new(1280, 800);
    private readonly List<IDisposable> _owned = [];
    private readonly List<Action<double>> _pollers = [];
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private double Now => _clock.Elapsed.TotalSeconds;

    public void Dispose()
    {
        foreach (var d in Enumerable.Reverse(_owned))
        {
            d.Dispose();
        }
    }

    private MasterServer Master()
    {
        var m = MasterServer.Start(new MasterConfig { Bind = IPAddress.Loopback, Port = 0 }, out var error) ?? throw new InvalidOperationException(error);
        _owned.Add(m);
        _pollers.Add(m.Poll);
        return m;
    }

    private void Run(Func<bool> done, double seconds = 15)
    {
        var until = Now + seconds;
        while (!done())
        {
            Assert.True(Now < until, "timed out");
            var now = Now;
            foreach (var p in _pollers.ToList())
            {
                p(now);
            }

            Thread.Sleep(5);
        }
    }

    [BehaviourDataFact]
    public void A_public_dedicated_server_is_found_in_the_internet_list_and_joined_through_the_master()
    {
        var master = Master();
        var config = ServerConfig.Parse(System.Text.Encoding.UTF8.GetBytes($$"""
            { "name": "PUBLIC TEST", "port": 0, "bind": "127.0.0.1", "discovery": false, "log_file": "", "ban_file": "",
              "public": true, "master": "127.0.0.1:{{master.Port}}", "password": "sesame", "idle_kick_seconds": 0,
              "rotation": [ { "map": "MAP.605", "time": 0 } ] }
            """));
        var server = DmDedicated.Start(BehaviourData.Data, config, out var error) ?? throw new InvalidOperationException(error);
        var last = Now;
        _pollers.Add(now =>
        {
            server.Update(now - last);
            last = now;
        });
        _owned.Add(server);
        Run(() => server.Host.Master?.State == MasterEntryState.Listed);
        Assert.Contains(server.Lines, l => l.Contains("listed as 127.0.0.1:", StringComparison.Ordinal));

        // The INTERNET list of this game and build: the server, its ping, its lock.
        var browser = MasterBrowser.Start($"127.0.0.1:{master.Port}", DmProtocol.GameId(BehaviourData.Data.Game), DmProtocol.Version)!;
        _owned.Add(browser);
        _pollers.Add(now => browser.Poll(now));
        Run(() => browser.Servers.Count == 1 && browser.Servers[0].PingMs != null);
        var entry = browser.Servers[0];
        Assert.Equal(("PUBLIC TEST", "MAP.605", true, server.Port), (entry.Info.Name, entry.Info.Map, entry.Info.Password, entry.Address.Port));

        // Joined through the introduction, with the password.
        var o = new DmRunOptions
        {
            JoinHost = entry.Address.Address.ToString(), Port = entry.Address.Port, Name = "ALPHA", Password = "sesame",
            Master = $"127.0.0.1:{master.Port}", Introduce = true,
        };
        var transport = DmHeadless.ClientTransport(o)!;
        var client = new DmClient(new NetEndpoint(transport), "ALPHA", DmClass.Human, o.Password);
        _owned.Add(client);
        client.Welcomed += client.ReportLevelReady;
        _pollers.Add(_ => client.Update(0.01));
        Run(() => client.Active && server.Server.Humans == 1);
        Assert.Equal(1, master.Introductions);
        Assert.Equal(0, transport.DirectFallbacks);

        // The ping answer has the player now; the master hears the next heartbeat.
        browser.Refresh();
        Run(() => browser.Servers.Count == 1 && browser.Servers[0].Info.Players == 1);

        // The stop says goodbye: the list is empty at once.
        server.Stop("test over");
        server.Dispose();
        _pollers.RemoveAt(1);
        Run(() => master.Listed == 0);
    }

    [BehaviourDataFact]
    public void Server_status_asks_before_Back_stops_the_server()
    {
        var config = ServerConfig.Parse("""
            { "name": "STATUS", "port": 0, "bind": "127.0.0.1", "discovery": false, "log_file": "", "ban_file": "", "rotation": [ "MAP.605" ] }
            """u8);
        var server = DmDedicated.Start(BehaviourData.Data, config, out var error) ?? throw new InvalidOperationException(error);
        _owned.Add(server);
        var log = new NetRecorder();
        var menu = new MenuSystem(log, new MenuSettings(), new MenuContext());
        menu.OpenTitle();
        var status = new ServerStatusScreen(server);
        menu.Push(status);
        menu.Update(new MenuInput { Back = true }, Frame);
        var box = Assert.IsType<ConfirmBox>(menu.Current);
        Assert.Equal("STOP THE SERVER?", box.Caption);
        var before = server.Uptime;
        menu.Update(new MenuInput(), Frame, 0.1f);
        Assert.True(server.Uptime > before, "the server runs on under the box");
        box.Select("no");
        menu.Update(new MenuInput { Accept = true }, Frame);
        Assert.Same(status, menu.Current);
        Assert.False(server.Stopped);

        menu.Update(new MenuInput { Back = true }, Frame);
        menu.Current!.Select("yes");
        menu.Update(new MenuInput { Accept = true }, Frame);
        Assert.True(server.Stopped);
        Assert.IsNotType<ServerStatusScreen>(menu.Current);
        Assert.IsNotType<ConfirmBox>(menu.Current);
    }

    // ---- the menus (no game data needed) ----

    private (MenuSystem Menu, NetRecorder Log) Menu(string? master)
    {
        var log = new NetRecorder();
        var menu = new MenuSystem(log, new MenuSettings(), new MenuContext
        {
            NetLevels = () => [SkyNet.Formats.NetLevelList.Default(605)],
            LanBrowser = () => null,
            NetConnecting = () => log.Connecting,
            MasterAddress = () => master,
        });
        menu.OpenTitle();
        menu.Update(new MenuInput { Accept = true }, Frame);
        menu.Current!.Select("multi");
        menu.Update(new MenuInput { Accept = true }, Frame);
        return (menu, log);
    }

    /// <summary>A game server on 127.0.0.1 announcing <paramref name="info"/> to <paramref name="master"/>.</summary>
    private UdpTransport Announce(MasterServer master, ServerInfo info)
    {
        var t = UdpTransport.Listen(0, DmProtocol.AppKey, 4, IPAddress.Loopback)!;
        var ep = new NetEndpoint(t);
        _owned.Add(ep);
        var announcer = new MasterAnnouncer(t, "127.0.0.1", master.Port, () => info, IPAddress.Loopback);
        var responder = new InfoResponder(t, () => info);
        t.Unconnected = (from, data) =>
        {
            if (!announcer.Handle(from, data))
            {
                responder.Handle(from, data, Now);
            }
        };
        _pollers.Add(now =>
        {
            ep.Poll();
            announcer.Poll(now);
        });
        Run(() => announcer.State == MasterEntryState.Listed);
        return t;
    }

    [Fact]
    public void The_internet_tab_lists_the_master_s_games_and_joins_one_through_it()
    {
        var master = Master();
        var open = Announce(master, new ServerInfo { GameId = "skynet", Protocol = DmProtocol.Version, Name = "OPEN GAME", Map = "MAP.605", Players = 3, MaxPlayers = 8, Bots = 1 });
        var locked = Announce(master, new ServerInfo { GameId = "skynet", Protocol = DmProtocol.Version, Name = "LOCKED GAME", Map = "MAP.603", Players = 1, MaxPlayers = 8, Password = true });
        Announce(master, new ServerInfo { GameId = "skynet", Protocol = DmProtocol.Version - 1, Name = "OLD BUILD", Map = "MAP.605" });

        var (menu, log) = Menu($"127.0.0.1:{master.Port}");
        menu.Current!.Select("join");
        menu.Update(new MenuInput { Accept = true }, Frame);
        var join = Assert.IsType<NetJoinScreen>(menu.Current);
        Assert.False(join.OnInternet);
        join.Select("tabinternet");
        menu.Update(new MenuInput { Accept = true }, Frame);
        Assert.True(join.OnInternet);
        _pollers.Add(_ => menu.Update(new MenuInput(), Frame, 0.02f));
        Run(() => join.Games.Count == 2 && join.Games.All(g => g.PingMs != null));
        Assert.Equal(["OPEN GAME", "LOCKED GAME"], join.Games.Select(g => g.Name)); // most players first
        Assert.Equal((false, true), (join.Games[0].Password, join.Games[1].Password));
        Assert.All(join.Games, g => Assert.True(g.ViaMaster));
        Assert.Equal("2 GAMES ON THE MASTER SERVER", join.Status);

        // A passworded game wants the PASSWORD box first.
        join.Select("game1");
        menu.Update(new MenuInput { Accept = true }, Frame);
        Assert.Equal("THIS GAME NEEDS A PASSWORD", menu.Toast);
        Assert.Equal("password", join.Highlighted!.Id);
        menu.Update(new MenuInput { Typed = "Se Same" }, Frame);
        join.Select("game1");
        menu.Update(new MenuInput { Accept = true }, Frame);
        var o = log.Started.Single();
        Assert.Equal(("127.0.0.1", locked.LocalPort, "Se Same", true, $"127.0.0.1:{master.Port}"), (o.JoinHost, o.Port, o.Password, o.Introduce, o.Master));
        Assert.StartsWith("CONNECTING TO 127.0.0.1:", join.Status, StringComparison.Ordinal);
        Assert.NotEqual(open.LocalPort, o.Port);
    }

    [Fact]
    public void Without_a_master_address_the_internet_tab_says_so_and_contacts_nobody()
    {
        var (menu, _) = Menu(null);
        Assert.Equal("", menu.Settings.Net.Master);
        Assert.False(NetMenuSettings.MasterSet(""));
        menu.Current!.Select("join");
        menu.Update(new MenuInput { Accept = true }, Frame);
        var join = Assert.IsType<NetJoinScreen>(menu.Current);
        join.ShowTab(true);
        Assert.Equal("NO MASTER SERVER SET", join.Status);
        Assert.Equal("SET NET.MASTER IN THE SETTINGS OR A MASTERS.TXT BESIDE THE GAME", menu.Toast);
        Assert.Empty(join.Games);
    }

    [Fact]
    public void Public_and_dedicated_are_remembered_and_public_needs_a_master()
    {
        var (menu, log) = Menu(null);
        menu.Update(new MenuInput { Accept = true }, Frame);
        var host = Assert.IsType<NetHostScreen>(menu.Current);
        host.Select("public");
        menu.Update(new MenuInput { Accept = true }, Frame);
        Assert.True(host.Options().Public);
        host.Select("start");
        menu.Update(new MenuInput { Accept = true }, Frame);
        Assert.Equal("PUBLIC: NO MASTER SERVER SET (SETTINGS OR MASTERS.TXT)", menu.Toast);
        Assert.Empty(log.Started);
        Assert.True(menu.Settings.Net.Public);

        menu.Settings.Net.Master = "master.example.org:27900";
        menu.Settings.Net.Dedicated = true;
        var path = Path.Combine(Path.GetTempPath(), $"skynet-master-settings-{Guid.NewGuid():N}.json");
        try
        {
            Assert.Equal(SaveResult.Saved, SettingsStore.Save(menu.Settings, path));
            var back = SettingsStore.Load(path);
            Assert.Equal(("master.example.org:27900", true, true), (back.Net.Master, back.Net.Public, back.Net.Dedicated));
        }
        finally
        {
            File.Delete(path);
        }

        menu.Update(new MenuInput { Back = true }, Frame);
        menu.Current!.Select("new");
        menu.Update(new MenuInput { Accept = true }, Frame);
        var again = Assert.IsType<NetHostScreen>(menu.Current);
        Assert.True(again.Options().Dedicated);
        Assert.Equal("master.example.org:27900", again.Options().Master);
    }

    [Fact]
    public void The_master_address_comes_from_the_command_line_the_settings_then_masters_txt()
    {
        Assert.Equal(["master.example.org", "backup.example.org:27901"], MasterAddresses.Parse(
            "# shipped masters\r\nmaster.example.org   # the main one\n\n// a spare\nbackup.example.org:27901\nnot a host:0\nmaster.example.org\n"));
        var dir = Path.Combine(Path.GetTempPath(), $"skynet-masters-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            Assert.Null(MasterAddresses.Pick(null, "", dir));
            File.WriteAllText(Path.Combine(dir, MasterAddresses.FileName), "master.example.org:27900\n");
            Assert.Equal("master.example.org:27900", MasterAddresses.Pick(null, "", dir));
            Assert.Equal("settings.example.org", MasterAddresses.Pick(null, "settings.example.org", dir));
            Assert.Equal("cli.example.org:1", MasterAddresses.Pick("cli.example.org:1", "settings.example.org", dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void The_lan_reply_carries_the_password_flag_and_old_replies_still_read()
    {
        var s = new DmSettings { ServerName = "X", Map = 605, MaxPlayers = 8 };
        Assert.True(DmProtocol.ReadDiscoveryInfo(DmProtocol.DiscoveryInfo(s, 1, 0, 27015, password: true))!.Value.Password);
        var old = DmProtocol.DiscoveryInfo(s, 1, 0, 27015);
        Assert.False(DmProtocol.ReadDiscoveryInfo(old.AsSpan(0, old.Length - 1))!.Value.Password);
    }
}
