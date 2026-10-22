using System.Collections.Immutable;
using System.Diagnostics;
using System.Net;
using RetroEng.Net;
using SkyNet.Formats;
using SkyNet.Game.Menu;
using SkyNet.Game.Net;

namespace SkyNet.Game.Tests.Menu;

/// <summary>A recorder that also takes the network screens' START.</summary>
internal sealed class NetRecorder : IMenuActions
{
    public List<string> Calls { get; } = [];
    public List<DmRunOptions> Started { get; } = [];
    public string? Refuse { get; set; }
    public bool Connecting { get; set; }
    public void StartGame(GameKind game, int mission, SkyNet.Game.Campaign.Pilot pilot) => Calls.Add("start");
    public void BeginMission() => Calls.Add("begin");
    public void Resume() => Calls.Add("resume");
    public void RestartMission() => Calls.Add("restart");
    public void QuitToTitle() => Calls.Add("title");
    public void Quit() => Calls.Add("quit");
    public void LoadGame(int slot) => Calls.Add($"load {slot}");
    public bool SaveGame(int slot) => false;
    public void SettingsChanged(MenuSettings settings) => Calls.Add("settings");
    public void Sound(string name) { }

    public string? StartNetGame(DmRunOptions options)
    {
        Started.Add(options);
        Calls.Add(options.HostMap != null ? $"host {options.HostMap}" : $"join {options.JoinHost}:{options.Port}");
        Connecting = Refuse == null && options.JoinHost != null;
        return Refuse;
    }

    public void StopNetGame()
    {
        Calls.Add("stop");
        Connecting = false;
    }
}

public sealed class NetMenuTests : IDisposable
{
    private static readonly MenuFrame Frame = new(1280, 800);
    private static readonly MenuInput Accept = new() { Accept = true };
    private static readonly MenuInput Back = new() { Back = true };
    private static readonly MenuInput Right = new() { Right = true };
    private static readonly MenuInput Left = new() { Left = true };

    private static readonly NetLevel[] Levels =
    [
        NetLevelList.Default(605) with { Name = "Highway/City", Items = NetLevelList.ItemKeys.ToImmutableDictionary(k => k, _ => 3) },
        NetLevelList.Default(603) with { Name = "Downtown L.A.", Replenish = false, ScoringKill = 700 },
    ];

    private readonly List<IDisposable> _owned = [];

    public void Dispose()
    {
        foreach (var d in _owned)
        {
            d.Dispose();
        }
    }

    private static (MenuSystem Menu, NetRecorder Log) Make(Func<DiscoveryBrowser?>? browser = null)
    {
        var log = new NetRecorder();
        var menu = new MenuSystem(log, new MenuSettings(), new MenuContext
        {
            NetLevels = () => Levels,
            LanBrowser = browser ?? (() => null),
            NetConnecting = () => log.Connecting,
        });
        return (menu, log);
    }

    private static void ToMultiplayer(MenuSystem menu)
    {
        menu.OpenTitle();
        menu.Update(Accept, Frame);
        menu.Current!.Select("multi");
        menu.Update(Accept, Frame);
        Assert.IsType<MultiplayerScreen>(menu.Current);
    }

    [Fact]
    public void MultiPlayerLeadsToTheHostAndJoinScreensAndExitGoesBack()
    {
        var (menu, _) = Make();
        ToMultiplayer(menu);
        Assert.Equal(["new", "join", "exit"], menu.Current!.Items.Select(i => i.Id));
        Assert.All(menu.Current.Items, i => Assert.Equal(MenuLayout.NetChoiceLit, i.Lit!.Value.Art));
        menu.Update(Accept, Frame);
        Assert.IsType<NetHostScreen>(menu.Current);
        menu.Update(Back, Frame);
        menu.Current!.Select("join");
        menu.Update(Accept, Frame);
        var join = Assert.IsType<NetJoinScreen>(menu.Current);
        Assert.Equal("LAN DISCOVERY UNAVAILABLE - TYPE AN ADDRESS", join.Status);
        menu.Current!.Select("exit");
        menu.Update(Accept, Frame);
        Assert.IsType<MultiplayerScreen>(menu.Current);
        menu.Current!.Select("exit");
        menu.Update(Accept, Frame);
        Assert.IsType<NewGameScreen>(menu.Current);
    }

    [Fact]
    public void TheHostScreenStartsWithItsFieldsAndRemembersThem()
    {
        var (menu, log) = Make();
        menu.Settings.CallSign = "Ace";
        ToMultiplayer(menu);
        menu.Update(Accept, Frame);
        var host = Assert.IsType<NetHostScreen>(menu.Current);
        Assert.Equal("start", host.Highlighted!.Id);
        Assert.Equal(605, host.Arena!.Map);

        // AREA cycles the arenas and takes their items and REPLENISH.
        host.Select("area");
        menu.Update(Right, Frame);
        Assert.Equal(603, host.Arena!.Map);
        Assert.False(host.Options().Replenish);
        Assert.Equal(4, host.Options().Items!["health"]);
        menu.Update(Accept, Frame);
        Assert.Equal(605, host.Arena!.Map);
        Assert.Equal(3, host.Options().Items!["health"]);

        // A number box: the first digit replaces it, left / right step it, START clamps it.
        host.Select("bots");
        menu.Update(new MenuInput { Typed = "2" }, Frame);
        Assert.Equal(2, host.Options().Bots);
        menu.Update(new MenuInput { Typed = "9" }, Frame);
        Assert.Equal(12, host.Options().Bots); // 29, at most 12
        menu.Update(new MenuInput { Erase = true }, Frame);
        menu.Update(Left, Frame);
        Assert.Equal(1, host.Options().Bots);
        host.Select("skill");
        menu.Update(Right, Frame);
        Assert.Equal(2, host.Options().BotSkill);
        host.Select("time");
        menu.Update(new MenuInput { Typed = "5" }, Frame);
        host.Select("port");
        menu.Update(new MenuInput { Typed = "47815" }, Frame);
        // The left box cycles DOS's thirteen avatars: Accept the next, left the one before (1 → 13,
        // the last AVFEMALE, a machine: the TERMINATOR).
        host.Select("class");
        menu.Update(Accept, Frame);
        Assert.Equal((2, DmClass.Human), (host.Avatar.Number, host.Options().Class));
        menu.Update(Left, Frame);
        menu.Update(Left, Frame);
        Assert.Equal((13, DmClass.Terminator, "WOMAN 6"), (host.Avatar.Number, host.Options().Class, host.Avatar.Name));

        // NAME: the call sign until typed over, upper case, spaces kept.
        host.Select("name");
        Assert.Equal("ACE", host.Options().Name);
        menu.Update(new MenuInput { Erase = true }, Frame);
        menu.Update(new MenuInput { Erase = true }, Frame);
        menu.Update(new MenuInput { Erase = true }, Frame);
        menu.Update(new MenuInput { Typed = "big ", Accept = true }, Frame);
        menu.Update(new MenuInput { Typed = "al" }, Frame);
        Assert.Equal("name", host.Highlighted!.Id); // the space typed was not Accept

        host.Select("start");
        menu.Update(Accept, Frame);
        Assert.Equal(["settings", "host 605"], log.Calls);
        Assert.False(menu.IsOpen);
        var o = log.Started.Single();
        Assert.Equal(("BIG AL", 47815, DmClass.Terminator, 1, 2, 5, 20, 8), (o.Name, o.Port, o.Class, o.Bots, o.BotSkill, o.TimeLimitMinutes, o.GoalLimit, o.MaxPlayers));

        // The settings keep it all, and the screen opens on it again.
        var net = menu.Settings.Net;
        Assert.Equal(("BIG AL", true, 13, 605, 1, 2, 5, 47815), (net.Name, net.Terminator, net.Avatar, net.Arena, net.Bots, net.BotSkill, net.TimeLimit, net.Port));
        menu.OpenNetwork(join: false);
        var again = Assert.IsType<NetHostScreen>(menu.Current);
        Assert.Equal(o with { Items = null }, again.Options() with { Items = null });
        Assert.Equal(o.Items!.OrderBy(p => p.Key), again.Options().Items!.OrderBy(p => p.Key));
        Assert.Equal(["title", "newgame", "multiplayer", "nethost"], menu.Stack.Select(s => s.Name));
    }

    [Fact]
    public void TheHostScreenSetsClassesAndAPasswordThatIsNeverSaved()
    {
        var (menu, log) = Make();
        ToMultiplayer(menu);
        menu.Update(Accept, Frame);
        var host = Assert.IsType<NetHostScreen>(menu.Current);
        Assert.Equal(DmClassRules.Balanced, host.Options().Classes); // the default
        Assert.Null(host.Options().Password);
        Assert.Equal("N", host.Locked);

        // CLASSES cycles DOS / BALANCED on Accept and left / right.
        host.Select("classes");
        menu.Update(Accept, Frame);
        Assert.Equal(DmClassRules.Dos, host.Options().Classes);
        menu.Update(Right, Frame);
        Assert.Equal(DmClassRules.Balanced, host.Options().Classes);
        menu.Update(Left, Frame);
        Assert.Equal(DmClassRules.Dos, host.Options().Classes);

        // PASSWORD takes any printable text as typed; LOCKED shows it is set.
        host.Select("password");
        menu.Update(new MenuInput { Typed = "Sec-ret!" }, Frame);
        Assert.Equal("Sec-ret!", host.Options().Password);
        Assert.Equal("Y", host.Locked);

        host.Select("start");
        menu.Update(Accept, Frame);
        var o = log.Started.Single();
        Assert.Equal((DmClassRules.Dos, "Sec-ret!"), (o.Classes, o.Password));
        Assert.Equal(DmClassRules.Dos, o.SettingsFor(Levels[0]).Classes);
        Assert.NotNull(o.HostPolicy().PasswordKey);

        // CLASSES is remembered; the password is not (neither its text nor its key).
        Assert.Equal(DmClassRules.Dos, menu.Settings.Net.Classes);
        var text = SettingsStore.Text(menu.Settings);
        Assert.DoesNotContain("Sec-ret", text, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToHexString(DmProtocol.PasswordKey("Sec-ret!")), text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"classes\": \"dos\"", text, StringComparison.Ordinal);
        menu.OpenNetwork(join: false);
        var again = Assert.IsType<NetHostScreen>(menu.Current);
        Assert.Equal(DmClassRules.Dos, again.Options().Classes);
        Assert.Null(again.Options().Password);
        Assert.Equal("N", again.Locked);
    }

    [Fact]
    public void TheClassBoxDescribesTheClassUnderTheRules()
    {
        Assert.Equal(["150 HEALTH", "MOTION DETECTOR", "FULL SPEED"], NetMenuScreen.ClassLines(DmClass.Human, DmClassRules.Balanced));
        Assert.Equal(["150 HEALTH", "MACHINE VISION", "NO DETECTOR", "0.9 SPEED"], NetMenuScreen.ClassLines(DmClass.Terminator, DmClassRules.Balanced));
        Assert.Equal(["150 HEALTH", "MACHINE VISION", "NO DETECTOR", "FULL SPEED"], NetMenuScreen.ClassLines(DmClass.Terminator, DmClassRules.Dos));
    }

    [Fact]
    public void TheHostScreensTerminatorVisionIsTheMachinesChoiceAndRemembered()
    {
        var (menu, log) = Make();
        ToMultiplayer(menu);
        menu.Update(Accept, Frame);
        var host = Assert.IsType<NetHostScreen>(menu.Current);
        var vision = host.Find("vision")!;
        // Off by default, and a HUMAN (avatar 1) cannot take it.
        Assert.Equal(("NORMAL", false, false), (host.Vision, vision.Enabled, host.Options().PointCloudVision));
        Assert.True(NetHostScreen.ClassArea.Contains(NetHostScreen.AvatarArea));
        Assert.False(NetHostScreen.ClassArea.IntersectsWith(NetHostScreen.VisionRow));
        Assert.True(MenuLayout.NetLeftBox.Contains(NetHostScreen.VisionRow));

        // Left from 1: avatar 13, a machine: the row comes alive under the class.
        host.Select("class");
        menu.Update(Left, Frame);
        Assert.True(host.Avatar.Machine);
        Assert.True(vision.Enabled);
        menu.Update(new MenuInput { Down = true }, Frame);
        Assert.Equal("vision", host.Highlighted!.Id);
        menu.Update(Accept, Frame);
        Assert.Equal("POINT CLOUD", host.Vision);
        menu.Update(Right, Frame);
        Assert.Equal("NORMAL", host.Vision);
        menu.Update(Left, Frame);
        Assert.Equal("POINT CLOUD", host.Vision);

        // Back to a HUMAN: the row goes dim and the choice off; a machine again takes it anew.
        host.Select("class");
        menu.Update(Right, Frame);
        Assert.Equal((false, "NORMAL"), (vision.Enabled, host.Vision));
        menu.Update(Left, Frame);
        Assert.Equal((true, "NORMAL"), (vision.Enabled, host.Vision));
        host.Select("vision");
        menu.Update(Accept, Frame);
        Assert.Equal("POINT CLOUD", host.Vision);

        host.Select("start");
        menu.Update(Accept, Frame);
        var o = log.Started.Single();
        Assert.Equal((DmClass.Terminator, true), (o.Class, o.PointCloudVision));
        Assert.True(menu.Settings.Net.TerminatorVision);
        Assert.Contains("\"terminatorVision\": true", SettingsStore.Text(menu.Settings), StringComparison.Ordinal);
        menu.OpenNetwork(join: false);
        var again = Assert.IsType<NetHostScreen>(menu.Current);
        Assert.Equal(("POINT CLOUD", true), (again.Vision, again.Find("vision")!.Enabled));
        Assert.True(again.Options().PointCloudVision);
    }

    [Fact]
    public void TheJoinScreensTerminatorVisionFollowsPlayAs()
    {
        var (menu, log) = Make();
        menu.Settings.Net.Address = "203.0.113.5";
        ToMultiplayer(menu);
        menu.Current!.Select("join");
        menu.Update(Accept, Frame);
        var join = Assert.IsType<NetJoinScreen>(menu.Current);
        var vision = join.Find("vision")!;
        Assert.Equal(("NORMAL", false), (join.Vision, vision.Enabled));
        var playAs = join.Find("class")!.Rect; // right under PLAY AS, as wide
        Assert.Equal((playAs.X, playAs.Width, playAs.Bottom + 5), (NetJoinScreen.JoinVision.X, NetJoinScreen.JoinVision.Width, NetJoinScreen.JoinVision.Y));

        join.Select("class");
        menu.Update(Left, Frame); // 13: a machine
        Assert.True(vision.Enabled);
        join.Select("vision");
        menu.Update(Accept, Frame);
        Assert.Equal("POINT CLOUD", join.Vision);
        // A HUMAN turns it off; back to the machine it stays off.
        join.Select("class");
        menu.Update(Right, Frame);
        Assert.Equal("NORMAL", join.Vision);
        menu.Update(Left, Frame);
        Assert.Equal("NORMAL", join.Vision);
        join.Select("vision");
        menu.Update(Accept, Frame);

        join.Select("start");
        menu.Update(Accept, Frame);
        var o = log.Started.Single();
        Assert.Equal((DmClass.Terminator, true), (o.Class, o.PointCloudVision));
        Assert.True(menu.Settings.Net.TerminatorVision);
    }

    [Fact]
    public void AHostThatCannotStartStaysOnTheScreen()
    {
        var (menu, log) = Make();
        log.Refuse = "cannot host on port 27015 (taken?)";
        ToMultiplayer(menu);
        menu.Update(Accept, Frame);
        menu.Update(Accept, Frame);
        Assert.IsType<NetHostScreen>(menu.Current);
        Assert.Equal("CANNOT HOST ON PORT 27015 (TAKEN?)", menu.Toast);
    }

    [Fact]
    public void TheNetSettingsRoundTrip()
    {
        var s = new MenuSettings();
        s.Net.Name = "BIG AL";
        s.Net.Terminator = true;
        s.Net.TerminatorVision = true;
        s.Net.Avatar = 7;
        s.Net.Arena = 603;
        s.Net.Bots = 5;
        s.Net.Port = 40000;
        s.Net.Replenish = false;
        s.Net.Items = new Dictionary<string, int> { ["health"] = 7, ["rockets"] = 0 };
        s.Net.Address = "198.51.100.20:27015";
        var path = Path.Combine(Path.GetTempPath(), $"skynet-net-settings-{Guid.NewGuid():N}.json");
        try
        {
            Assert.Equal(SaveResult.Saved, SettingsStore.Save(s, path));
            var back = SettingsStore.Load(path);
            Assert.Equal(SettingsStore.Text(s), SettingsStore.Text(back));
            Assert.Equal(7, back.Net.Items!["health"]);
            Assert.Equal("198.51.100.20:27015", back.Net.Address);
            Assert.True(back.Net.TerminatorVision);
            Assert.False(new NetMenuSettings().TerminatorVision); // off by default
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TheJoinScreenListsALanGameAndJoinsIt()
    {
        // A game answering discovery on 127.0.0.1, an ephemeral port.
        var settings = new DmSettings { ServerName = "AL'S GAME", Map = 603, MaxPlayers = 8 };
        var responder = DiscoveryResponder.Start(0, DmProtocol.DiscoveryMagic, IPAddress.Loopback)!;
        _owned.Add(responder);
        var (menu, log) = Make(() => DmLan.StartBrowser(responder.LocalPort, [IPAddress.Loopback], IPAddress.Loopback));
        ToMultiplayer(menu);
        menu.Current!.Select("join");
        menu.Update(Accept, Frame);
        var join = Assert.IsType<NetJoinScreen>(menu.Current);
        Assert.Equal("address", join.Highlighted!.Id);
        Assert.False(join.Find("game0")!.Enabled);

        var clock = Stopwatch.StartNew();
        while (join.Games.Count == 0 && clock.Elapsed.TotalSeconds < 5)
        {
            responder.Poll(clock.Elapsed.TotalSeconds, () => DmProtocol.DiscoveryInfo(settings, 2, 3, 41234));
            menu.Update(new MenuInput(), Frame, 0.05f);
            Thread.Sleep(10);
        }

        var game = Assert.Single(join.Games);
        Assert.Equal(("AL'S GAME", "MAP.603", 2, 3, 8, 41234), (game.Name, game.Map, game.Players, game.Bots, game.Max, game.Port));
        Assert.Equal("1 GAME FOUND", join.Status);
        Assert.True(join.Find("game0")!.Enabled);

        join.Select("game0");
        menu.Update(Accept, Frame);
        Assert.Equal(["settings", "join 127.0.0.1:41234"], log.Calls);
        Assert.Equal("CONNECTING TO 127.0.0.1:41234", join.Status);
        Assert.Equal("127.0.0.1:41234", menu.Settings.Net.Address);
        // The screen waits for the arena; Back while connecting gives the connection up.
        Assert.Same(join, menu.Current);
        menu.Update(Back, Frame);
        Assert.Equal("stop", log.Calls[^1]);
        Assert.Same(join, menu.Current);
        menu.Update(Back, Frame);
        Assert.IsType<MultiplayerScreen>(menu.Current);
    }

    [Fact]
    public void TheJoinScreensLockedBoxShowsTheHighlightedGamesPassword()
    {
        var settings = new DmSettings { ServerName = "LOCKED GAME", Map = 605, MaxPlayers = 8 };
        var responder = DiscoveryResponder.Start(0, DmProtocol.DiscoveryMagic, IPAddress.Loopback)!;
        _owned.Add(responder);
        var (menu, _) = Make(() => DmLan.StartBrowser(responder.LocalPort, [IPAddress.Loopback], IPAddress.Loopback));
        ToMultiplayer(menu);
        menu.Current!.Select("join");
        menu.Update(Accept, Frame);
        var join = Assert.IsType<NetJoinScreen>(menu.Current);
        Assert.Equal("", join.Locked); // no game chosen

        var clock = Stopwatch.StartNew();
        while (join.Games.Count == 0 && clock.Elapsed.TotalSeconds < 5)
        {
            responder.Poll(clock.Elapsed.TotalSeconds, () => DmProtocol.DiscoveryInfo(settings, 1, 0, 41235, password: true));
            menu.Update(new MenuInput(), Frame, 0.05f);
            Thread.Sleep(10);
        }

        Assert.True(Assert.Single(join.Games).Password);
        Assert.Equal("", join.Locked); // the address box is highlighted, empty
        join.Select("game0");
        Assert.Equal("Y", join.Locked);

        // The game drops its password: the next reply lists it open, and LOCKED says N.
        while (join.Games[0].Password && clock.Elapsed.TotalSeconds < 10)
        {
            responder.Poll(clock.Elapsed.TotalSeconds, () => DmProtocol.DiscoveryInfo(settings, 1, 0, 41235, password: false));
            menu.Update(new MenuInput(), Frame, 0.05f);
            Thread.Sleep(10);
        }

        Assert.Equal("N", join.Locked);

        // A typed address of a listed game is described as well.
        join.Select("address");
        menu.Update(new MenuInput { Typed = "127.0.0.1:41235" }, Frame);
        Assert.Equal("N", join.Locked);
        menu.Update(new MenuInput { Erase = true }, Frame);
        Assert.Equal("", join.Locked);
    }

    [Fact]
    public void TheAddressBoxJoinsATypedAddress()
    {
        var (menu, log) = Make();
        menu.Settings.Net.Address = "203.0.113.5";
        ToMultiplayer(menu);
        menu.Current!.Select("join");
        menu.Update(Accept, Frame);
        menu.Update(new MenuInput { Typed = ":abc" }, Frame);
        menu.Update(Accept, Frame);
        Assert.Equal("THE PORT MUST BE 1-65535", menu.Toast);
        Assert.Empty(log.Started);
        for (var i = 0; i < 3; i++)
        {
            menu.Update(new MenuInput { Erase = true }, Frame);
        }

        menu.Update(new MenuInput { Typed = "27999" }, Frame);
        menu.Current!.Select("start");
        menu.Update(Accept, Frame);
        var o = log.Started.Single();
        Assert.Equal(("203.0.113.5", 27999, DmClass.Human), (o.JoinHost, o.Port, o.Class));
    }
}
