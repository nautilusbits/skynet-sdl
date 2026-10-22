using SkyNet.Formats;
using SkyNet.Game.Enemies;
using SkyNet.Game.Hud;
using SkyNet.Game.Menu;

namespace SkyNet.Game.Tests.Menu;

/// <summary>Bindings without SDL: action names with lists of control names.</summary>
internal sealed class FakeBindings : IBindings
{
    private readonly List<(string Name, List<string> Controls, string[] Defaults)> _actions = [];

    public FakeBindings Add(string name, params string[] controls)
    {
        _actions.Add((name, [.. controls], controls));
        return this;
    }

    public int Find(string action) => _actions.FindIndex(a => a.Name == action);
    public string Name(int action) => _actions[action].Name;
    public int Count => _actions.Count;
    public IReadOnlyList<string> Bindings(int action) => _actions[action].Controls;

    public bool Bind(int action, string control)
    {
        if (control == "Bogus")
        {
            return false;
        }

        if (!_actions[action].Controls.Contains(control))
        {
            _actions[action].Controls.Add(control);
        }

        return true;
    }

    public void Unbind(int action, string? control = null)
    {
        if (control == null)
        {
            _actions[action].Controls.Clear();
        }
        else
        {
            _actions[action].Controls.RemoveAll(c => string.Equals(c, control, StringComparison.OrdinalIgnoreCase));
        }
    }

    public void ResetBindings()
    {
        foreach (var a in _actions)
        {
            a.Controls.Clear();
            a.Controls.AddRange(a.Defaults);
        }
    }

    // The gamepad's buttons: none here (GamepadBindingTests use the engine's Input).
    public IReadOnlyList<string> PadBindings(int action) => [];
    public bool BindPad(int action, string control) => false;
    public void UnbindPad(int action, string? control = null) { }
    public void ResetPadBindings() { }
}

internal sealed class Recorder : IMenuActions
{
    public List<string> Calls { get; } = [];
    public void StartGame(GameKind game, int mission, SkyNet.Game.Campaign.Pilot pilot) => Calls.Add($"start {game} {mission} {pilot.Name}|{pilot.CallSign}");
    public void BeginMission() => Calls.Add("begin");
    public void Resume() => Calls.Add("resume");
    public void RestartMission() => Calls.Add("restart");
    public void QuitToTitle() => Calls.Add("title");
    public void Quit() => Calls.Add("quit");
    public void LoadGame(int slot) => Calls.Add($"load {slot}");
    public bool SaveResult { get; set; } = true;
    public bool SaveGame(int slot)
    {
        Calls.Add($"save {slot}");
        return SaveResult;
    }
    public void SettingsChanged(MenuSettings settings) => Calls.Add("settings");
    public void Sound(string name) { }
}

public class MenuNavigationTests
{
    private static readonly MenuFrame Frame = new(1280, 800);
    private static readonly MenuInput Accept = new() { Accept = true };
    private static readonly MenuInput Back = new() { Back = true };
    private static readonly MenuInput Right = new() { Right = true };
    private static readonly MenuInput Left = new() { Left = true };
    private static readonly MenuInput Down = new() { Down = true };

    private static (MenuSystem Menu, Recorder Log, FakeBindings Binds) Make(bool futureShock = false)
    {
        var binds = new FakeBindings().Add("Forward", "W").Add("Back", "S").Add("Fire", "Left mouse").Add("Use", "E");
        var log = new Recorder();
        var menu = new MenuSystem(log, new MenuSettings(), new MenuContext
        {
            FutureShockAvailable = futureShock,
            Bindings = binds,
            SlotName = i => i == 2 ? "SAVED" : null,
        });
        return (menu, log, binds);
    }

    /// <summary>A click on the item where the open screen draws it (a dialog has its own scaled frame).</summary>
    private static MenuInput ClickOn(MenuSystem menu, MenuItem item) =>
        new() { Pointer = menu.Current!.FrameIn(Frame).CanvasPoint(item.Centre.X, item.Centre.Y), Click = true };

    [Fact]
    public void NewGameOnePlayerStartsSkyNetsFirstMission()
    {
        var (menu, log, _) = Make();
        menu.OpenTitle();
        Assert.Equal("new", menu.Current!.Highlighted!.Id);
        Assert.False(menu.PausesGame);
        menu.Update(Accept, Frame);
        Assert.Equal("newgame", menu.Current!.Name);
        Assert.Equal("one", menu.Current.Highlighted!.Id);
        menu.Update(Accept, Frame);
        // The name and call sign dialog comes first.
        var pilot = Assert.IsType<PilotScreen>(menu.Current);
        Assert.True(menu.TakesText);
        Assert.Equal("name", pilot.Highlighted!.Id);
        Assert.Equal("", pilot.Text(0)); // DOS starts empty; the port pre-fills only once a game was named
        // Letters only, shown upper case: the space (also a menu Accept key) and the digits are dropped.
        menu.Update(new MenuInput { Typed = "Test" }, Frame);
        menu.Update(new MenuInput { Typed = " ", Accept = true }, Frame);
        menu.Update(new MenuInput { Typed = "pilot-9" }, Frame);
        Assert.Equal("TESTPILOT", pilot.Text(0));
        Assert.Equal("name", pilot.Highlighted!.Id); // the space typed was not Accept
        menu.Update(Accept, Frame);
        Assert.Equal("callsign", pilot.Highlighted!.Id);
        menu.Update(new MenuInput { Typed = "ACEX" }, Frame);
        menu.Update(new MenuInput { Erase = true }, Frame);
        Assert.Equal("ACE", pilot.Text(1));
        Assert.Empty(log.Calls);
        menu.Update(Accept, Frame);
        // Stored capitalised, as DOS's 0x135596 does.
        Assert.Equal(["settings", "start SkyNet 1 Testpilot|Ace"], log.Calls);
        Assert.False(menu.IsOpen);
        // The last-used pair is the dialog's default next time.
        Assert.Equal(("Testpilot", "Ace"), (menu.Settings.PlayerName, menu.Settings.CallSign));
        menu.OpenTitle();
        menu.Update(Accept, Frame);
        menu.Update(Accept, Frame);
        Assert.Equal("TESTPILOT", ((PilotScreen)menu.Current!).Text(0));
    }

    [Fact]
    public void EmptyFieldsTakeTheDefaultsAndTheFieldsStopAtSeventeenLetters()
    {
        var (menu, log, _) = Make();
        menu.OpenTitle();
        menu.Update(Accept, Frame);
        menu.Update(Accept, Frame);
        var pilot = (PilotScreen)menu.Current!;
        menu.Update(new MenuInput { Typed = new string('Q', 30) }, Frame);
        Assert.Equal(17, pilot.Text(0).Length);
        menu.Update(new MenuInput { Erase = true }, Frame);
        Assert.Equal(16, pilot.Text(0).Length);
        for (var i = 0; i < 20; i++)
        {
            menu.Update(new MenuInput { Erase = true }, Frame);
        }

        Assert.Equal("", pilot.Text(0));
        // A gamepad's A twice: both fields empty, the STRINGS.PRS defaults.
        menu.Update(Accept, Frame);
        menu.Update(Accept, Frame);
        Assert.Equal(["settings", "start SkyNet 1 Noname|Ice"], log.Calls);
    }

    [Fact]
    public void EscOnTheNameDialogGoesBackToNewGame()
    {
        var (menu, log, _) = Make();
        menu.OpenTitle();
        menu.Update(Accept, Frame);
        menu.Update(Accept, Frame);
        Assert.Equal("pilot", menu.Current!.Name);
        menu.Update(Back, Frame);
        Assert.Equal("newgame", menu.Current!.Name);
        Assert.False(menu.TakesText);
        Assert.Empty(log.Calls);
    }

    [Fact]
    public void AClickPutsTheCursorInAFieldWithoutStarting()
    {
        var (menu, log, _) = Make();
        menu.OpenTitle();
        menu.Update(Accept, Frame);
        menu.Update(Accept, Frame);
        var pilot = (PilotScreen)menu.Current!;
        menu.Update(ClickOn(menu, pilot.Find("callsign")!), Frame);
        Assert.Equal("callsign", pilot.Highlighted!.Id);
        menu.Update(ClickOn(menu, pilot.Find("name")!), Frame);
        Assert.Equal("name", pilot.Highlighted!.Id);
        Assert.Empty(log.Calls);
    }

    [Fact]
    public void FutureShockNeedsItsData()
    {
        var (menu, log, _) = Make(futureShock: false);
        menu.OpenTitle();
        menu.Update(Accept, Frame);
        menu.Current!.Select("futureshock");
        menu.Update(Accept, Frame);
        Assert.Empty(log.Calls);
        Assert.NotNull(menu.Toast);

        (menu, log, _) = Make(futureShock: true);
        menu.OpenTitle();
        menu.Update(Accept, Frame);
        menu.Current!.Select("futureshock");
        menu.Update(Accept, Frame);
        // Future Shock asks too (0x11cb4a has no check for it).
        Assert.Equal("pilot", menu.Current!.Name);
        menu.Update(Accept, Frame);
        menu.Update(Accept, Frame);
        Assert.Equal(["settings", "start FutureShock 1 Noname|Ice"], log.Calls);
    }

    [Fact]
    public void TheBarWalksLeftAndRightAndWraps()
    {
        var (menu, _, _) = Make();
        menu.OpenPause();
        Assert.Equal("return", menu.Current!.Highlighted!.Id);
        menu.Update(Right, Frame);
        Assert.Equal("load", menu.Current.Highlighted!.Id);
        menu.Update(Left, Frame);
        menu.Update(Left, Frame);
        Assert.Equal("quit", menu.Current.Highlighted!.Id);
        menu.Update(Right, Frame);
        Assert.Equal("return", menu.Current.Highlighted!.Id);
    }

    [Fact]
    public void SaveIsOnlyInGame()
    {
        var (menu, _, _) = Make();
        menu.OpenTitle();
        Assert.False(menu.Current!.Find("save")!.Enabled);
        menu.OpenPause();
        Assert.True(menu.Current!.Find("save")!.Enabled);
        Assert.True(menu.PausesGame);
    }

    [Fact]
    public void EscResumesFromThePauseMenuAndStepsBackFromItsPages()
    {
        var (menu, log, _) = Make();
        menu.OpenPause();
        menu.Current!.Select("options");
        menu.Update(Accept, Frame);
        Assert.Equal("options", menu.Current!.Name);
        menu.Update(Back, Frame);
        Assert.Equal("pause", menu.Current!.Name);
        Assert.Empty(log.Calls);
        menu.Update(Back, Frame);
        Assert.False(menu.IsOpen);
        Assert.Equal(["resume"], log.Calls);
    }

    [Fact]
    public void ThePointerHighlightsAndClicks()
    {
        var (menu, _, _) = Make();
        menu.OpenTitle();
        var options = menu.Current!.Find("options")!;
        menu.Update(new MenuInput { Pointer = Frame.CanvasPoint(options.Centre.X, options.Centre.Y) }, Frame);
        Assert.Equal("options", menu.Current.Highlighted!.Id);
        Assert.Equal("title", menu.Current.Name);
        menu.Update(ClickOn(menu, options), Frame);
        Assert.Equal("options", menu.Current!.Name);
    }

    [Fact]
    public void OptionsChangeTheSettingsAndTellTheGame()
    {
        var (menu, log, _) = Make();
        menu.OpenTitle();
        menu.Current!.Select("options");
        menu.Update(Accept, Frame);
        var screen = menu.Current!;
        Assert.Equal("sound", screen.Highlighted!.Id);
        menu.Update(Left, Frame);
        Assert.Equal(MenuSettings.VolumeSteps - 1, menu.Settings.SoundVolume);
        menu.Update(ClickOn(menu, screen.Find("difficulty0")!), Frame);
        Assert.Equal(DifficultyLevel.Low, menu.Settings.Difficulty);
        menu.Update(ClickOn(menu, screen.Find("stereo")!), Frame);
        Assert.True(menu.Settings.ReverseStereo);
        // A click on the music bar sets it by where it lands.
        var music = screen.Find("music")!;
        menu.Update(new MenuInput { Pointer = menu.Current.FrameIn(Frame).CanvasPoint(music.Rect.X + 0.5f, music.Centre.Y), Click = true }, Frame);
        Assert.Equal(0, menu.Settings.MusicVolume);
        Assert.Equal(4, log.Calls.Count(c => c == "settings"));

        menu.Update(ClickOn(menu, screen.Find("detail")!), Frame);
        Assert.Equal("detail", menu.Current!.Name);
        menu.Update(ClickOn(menu, menu.Current.Find("detail0")!), Frame);
        Assert.Equal(Detail.Low, menu.Settings.Detail);
        // DOS's RESOLUTION pair is GAMEPLAY's entry now.
        Assert.Null(menu.Current.Find("res640"));
        menu.Update(ClickOn(menu, menu.Current.Find("gameplay")!), Frame);
        Assert.Equal("gameplay", menu.Current!.Name);
    }

    [Fact]
    public void RestartBoxYesRestartsNoGoesToTheTitle()
    {
        var (menu, log, _) = Make();
        menu.OpenRestart();
        Assert.Equal("restart", menu.Current!.Name);
        Assert.Equal("yes", menu.Current.Highlighted!.Id);
        menu.Update(Accept, Frame);
        Assert.Equal(["restart"], log.Calls);
        Assert.False(menu.IsOpen);

        menu.OpenRestart();
        menu.Update(Right, Frame);
        Assert.Equal("no", menu.Current!.Highlighted!.Id);
        menu.Update(Accept, Frame);
        menu.OpenRestart();
        menu.Update(Back, Frame);
        Assert.Equal(["restart", "title", "title"], log.Calls);
    }

    [Fact]
    public void QuitAsksFirst()
    {
        var (menu, log, _) = Make();
        menu.OpenTitle();
        menu.Update(Back, Frame);
        Assert.Equal("quit", menu.Current!.Name);
        menu.Update(Right, Frame);
        menu.Update(Accept, Frame);
        Assert.Equal("title", menu.Current!.Name);
        Assert.Empty(log.Calls);
        menu.Current.Select("quit");
        menu.Update(Accept, Frame);
        menu.Update(Accept, Frame);
        Assert.Equal(["quit"], log.Calls);

        menu.OpenPause();
        menu.Current!.Select("quit");
        menu.Update(Accept, Frame);
        Assert.Equal("quitmain", menu.Current!.Name);
        menu.Update(Accept, Frame);
        Assert.Equal(["quit", "title"], log.Calls);
        Assert.False(menu.IsOpen);
    }

    [Fact]
    public void LoadOffersOnlyUsedSlotsSaveEvery()
    {
        var (menu, log, _) = Make();
        menu.OpenTitle();
        menu.Current!.Select("load");
        menu.Update(Accept, Frame);
        Assert.Equal("slot2", menu.Current!.Highlighted!.Id);
        Assert.False(menu.Current.Find("slot0")!.Enabled);
        menu.Update(Accept, Frame);
        Assert.Equal(["load 2"], log.Calls);
        Assert.False(menu.IsOpen);

        menu.OpenPause();
        menu.Current!.Select("save");
        menu.Update(Accept, Frame);
        Assert.Equal("slot0", menu.Current!.Highlighted!.Id);
        menu.Update(Down, Frame);
        menu.Update(Accept, Frame);
        Assert.Equal(["load 2", "save 1"], log.Calls);
        Assert.Equal("pause", menu.Current!.Name);
        Assert.Equal("GAME SAVED", menu.Toast);
    }

    [Fact]
    public void ARefusedSaveSaysNothingAndStaysOnTheSlots()
    {
        var (menu, log, _) = Make();
        log.SaveResult = false;
        menu.OpenPause();
        menu.Current!.Select("save");
        menu.Update(Accept, Frame);
        menu.Update(Accept, Frame);
        Assert.Equal(["save 0"], log.Calls);
        Assert.Equal("save", menu.Current!.Name);
        Assert.Null(menu.Toast);
    }

    [Fact]
    public void ControlsRebindTakesTheNextKeyAndFreesItElsewhere()
    {
        var (menu, _, binds) = Make();
        menu.OpenTitle();
        menu.Current!.Select("options");
        menu.Update(Accept, Frame);
        menu.Current!.Select("controls");
        menu.Update(Accept, Frame);
        var screen = Assert.IsType<ControlsScreen>(menu.Current);
        Assert.False(screen.Find("turn_left")!.Enabled); // no such action in the game
        screen.Select("forward");
        menu.Update(Accept with { Control = "Return" }, Frame);
        Assert.Equal(0, screen.Capturing);
        // The frame that opened the box carries its own press: skipped.
        menu.Update(new MenuInput { Control = "Return" }, Frame);
        Assert.Equal(0, screen.Capturing);
        menu.Update(new MenuInput { Control = "S" }, Frame);
        Assert.Equal(-1, screen.Capturing);
        Assert.Equal(["S"], binds.Bindings(binds.Find("Forward")));
        Assert.Empty(binds.Bindings(binds.Find("Back")));
        Assert.Equal(["S"], menu.Settings.Bindings["Forward"]);
        Assert.Empty(menu.Settings.Bindings["Back"]);

        // Esc cancels a box without leaving the screen.
        screen.Select("fire");
        menu.Update(Accept, Frame);
        menu.Update(new MenuInput(), Frame);
        menu.Update(Back with { Control = "Escape" }, Frame);
        Assert.Equal("controls", menu.Current!.Name);
        Assert.Equal(["Left mouse"], binds.Bindings(binds.Find("Fire")));

        menu.Update(ClickOn(menu, screen.Find("default")!), Frame);
        Assert.Equal(["W"], binds.Bindings(binds.Find("Forward")));
        // The settings keep every action's keys, the defaults too.
        Assert.Equal(Rebind.Snapshot(binds), menu.Settings.Bindings);
    }

    [Fact]
    public void ARefusedControlKeepsTheOldBinding()
    {
        var binds = new FakeBindings().Add("Fire", "Left mouse");
        Assert.False(Rebind.Apply(binds, 0, "Bogus", [0]));
        Assert.Equal(["Left mouse"], binds.Bindings(0));
    }

    [Fact]
    public void TheMissionScreenPagesAndBegins()
    {
        var brief = Briefing.Parse(System.Text.Encoding.ASCII.GetBytes(
            "[EN]\nScout the complex.\n%%%%\n[BR]\n[004]\nFirst line.\n####\n[005]\nSecond line.\n####\n%%%%\n[TA]\ntachkftr tacscout\n%%%%\n"));
        var (menu, log, _) = Make();
        var screen = menu.OpenMission(new MissionBriefing(GameKind.SkyNet, 1, 210, brief));
        Assert.Equal(3, screen.Pages.Count);
        Assert.Equal("BRIEF210.IMG", screen.Pages[0].Picture);
        Assert.Equal("BRIEF004.IMG", screen.Pages[1].Picture);
        Assert.Equal(["tachkftr", "tacscout"], screen.Dossiers);
        Assert.Equal(MissionTab.Briefing, screen.Tab);
        menu.Update(Down, Frame);
        menu.Update(Down, Frame);
        Assert.Equal(2, screen.Page);
        menu.Update(Down, Frame);
        Assert.Equal(2, screen.Page);
        menu.Update(new MenuInput { Up = true }, Frame);
        Assert.Equal(1, screen.Page);

        menu.Update(Right, Frame);
        menu.Update(Right, Frame);
        Assert.Equal("tactical", screen.Highlighted!.Id);
        menu.Update(Accept, Frame);
        Assert.Equal(MissionTab.Tactical, screen.Tab);
        menu.Update(Down, Frame);
        Assert.Equal(1, screen.Dossier);
        menu.Update(Down, Frame);
        Assert.Equal(0, screen.Dossier);

        menu.Update(Right, Frame);
        menu.Update(Right, Frame);
        Assert.Equal("begin", screen.Highlighted!.Id);
        menu.Update(Accept, Frame);
        Assert.Equal(["begin"], log.Calls);
        Assert.False(menu.IsOpen);
    }

    [Fact]
    public void FutureShockBriefingUsesItsPictureTag()
    {
        var brief = Briefing.Parse(System.Text.Encoding.ASCII.GetBytes("[EN]\n[009]\nGo.\n%%%%\n"));
        var pages = MissionScreen.BuildPages(new MissionBriefing(GameKind.FutureShock, 1, 10, brief));
        Assert.Equal("BRIEF009.IMG", pages[0].Picture);
        Assert.Equal(MenuLayout.BriefPalette, pages[0].Palette);
    }

    [Fact]
    public void FrameMapsDosPixelsBothWays()
    {
        // The screen stretches over the whole window.
        var f = new MenuFrame(1269, 700);
        Assert.Equal(0f, f.ScreenX(0));
        Assert.Equal(1269f, f.ScreenX(MenuFrame.ScreenWidth));
        Assert.Equal(700f, f.ScreenY(MenuFrame.ScreenHeight));
        Assert.Equal(4, f.TextScale);
        var p = f.CanvasPoint(100, 50);
        var back = f.ToDos(p);
        Assert.InRange(back.X, 99.8f, 100.2f); // edges snap to whole screen pixels
        Assert.InRange(back.Y, 49.8f, 50.2f);

        // A dialog scales uniformly by the Godot port's factor on its 1280 x 720 canvas, centred.
        var panel = new MenuFrame(1920, 1080).Panel(new System.Drawing.Rectangle(61, 34, 198, 132), 4f);
        Assert.Equal(6f, panel.ScaleX);
        Assert.Equal(6f, panel.ScaleY);
        Assert.Equal((1920 - 198 * 6) / 2f, panel.ScreenX(61));
        Assert.Equal((1080 - 132 * 6) / 2f, panel.ScreenY(34));
        var q = panel.ToDos(panel.CanvasPoint(150, 100));
        Assert.Equal(150f, q.X, 3);
        Assert.Equal(100f, q.Y, 3);
    }

    [Fact]
    public void ShortNamesFitTheBoxes()
    {
        Assert.Equal("MOUSE L", ControlsScreen.ShortName("Left mouse"));
        Assert.Equal("L SHIFT", ControlsScreen.ShortName("Left Shift"));
        Assert.Equal("PG DN", ControlsScreen.ShortName("PageDown"));
        Assert.Equal("W", ControlsScreen.ShortName("W"));
    }
}

public class SettingsStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "skynet-menu-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }
    }

    private string PathOf(string name) => Path.Combine(_dir, name);

    [Fact]
    public void RoundTrips()
    {
        var s = new MenuSettings
        {
            Detail = Detail.Medium, SoundVolume = 3, MusicVolume = 11, ReverseStereo = true, HudArt = HudArtSet.HiRes,
            Difficulty = DifficultyLevel.High, WindowWidth = 1920, WindowHeight = 1080, WindowMode = WindowMode.Borderless,
            PlayerName = "Testpilot", CallSign = "Ace", ShowFps = true, DebugOverlay = true,
        };
        s.Bindings["Forward"] = ["W", "Up"];
        s.Bindings["Fire"] = [];
        var path = PathOf("sub/settings.json");
        Assert.Equal(SaveResult.Saved, SettingsStore.Save(s, path));
        var back = SettingsStore.Load(path);
        Assert.Equal(SettingsStore.Text(s), SettingsStore.Text(back));
        Assert.Equal(["W", "Up"], back.Bindings["Forward"]);
        Assert.Empty(back.Bindings["Fire"]);
        Assert.Equal(("Testpilot", "Ace"), (back.PlayerName, back.CallSign));
        Assert.True(back.ShowFps && back.DebugOverlay);
        // An older file without the pair (or a hand-edited one) loads empty or cleaned.
        File.WriteAllText(path, SettingsStore.Text(s).Replace("\"Testpilot\"", "\"test pilot!\"", StringComparison.Ordinal)
            .Replace("\"callSign\": \"Ace\",", "", StringComparison.Ordinal));
        Assert.Equal(("Testpilot", ""), (SettingsStore.Load(path).PlayerName, SettingsStore.Load(path).CallSign));
        // Saving again over our own file works.
        back.SoundVolume = 0;
        Assert.Equal(SaveResult.Saved, SettingsStore.Save(back, path));
        Assert.Equal(0, SettingsStore.Load(path).SoundVolume);
    }

    [Fact]
    public void TheWindowDefaultsTo1280x720WindowedAndRejectsOddSizes()
    {
        var d = new MenuSettings();
        Assert.Equal((1280, 720, WindowMode.Windowed), (d.WindowWidth, d.WindowHeight, d.WindowMode));
        var path = PathOf("window.json");
        Assert.Contains("\"windowWidth\": 1280", SettingsStore.Text(d), StringComparison.Ordinal);
        var text = SettingsStore.Text(d).Replace("\"windowWidth\": 1280", "\"windowWidth\": 5", StringComparison.Ordinal);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        var back = SettingsStore.Load(path);
        Assert.Equal((1280, 720), (back.WindowWidth, back.WindowHeight));
    }

    [Fact]
    public void AForeignFileIsNeitherReadNorOverwritten()
    {
        Directory.CreateDirectory(_dir);
        var path = PathOf("settings.json");
        const string foreign = "{\"detail\":\"Low\",\"volume\":3}";
        File.WriteAllText(path, foreign);
        Assert.False(SettingsStore.IsOwnOrAbsent(path));
        Assert.Equal(Detail.High, SettingsStore.Load(path).Detail);
        Assert.Equal(SaveResult.ForeignFile, SettingsStore.Save(new MenuSettings(), path));
        Assert.Equal(foreign, File.ReadAllText(path));

        var junk = PathOf("junk.json");
        File.WriteAllBytes(junk, [0xFF, 0x00, 0x13]);
        Assert.Equal(SaveResult.ForeignFile, SettingsStore.Save(new MenuSettings(), junk));
        Assert.Equal([0xFF, 0x00, 0x13], File.ReadAllBytes(junk));
    }

    [Fact]
    public void BadValuesFallBackToDefaults()
    {
        Directory.CreateDirectory(_dir);
        var path = PathOf("settings.json");
        File.WriteAllText(path, $"{{\"format\":\"{SettingsStore.Format}\",\"detail\":\"Ultra\",\"soundVolume\":99,\"musicVolume\":2,\"difficulty\":\"low\"}}");
        var s = SettingsStore.Load(path);
        Assert.Equal(Detail.High, s.Detail);
        Assert.Equal(MenuSettings.VolumeSteps, s.SoundVolume);
        Assert.Equal(2, s.MusicVolume);
        Assert.Equal(DifficultyLevel.Low, s.Difficulty);
    }

    [Fact]
    public void TheDefaultPathIsTheCSharpPortsOwn()
    {
        var p = SettingsStore.DefaultPath;
        Assert.EndsWith(SettingsStore.FileName, p);
        Assert.DoesNotContain("godot", p, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AMissingFileIsWrittenWithEverySetting()
    {
        var path = PathOf("first/settings.json");
        var controls = SkyNet.Game.Campaign.CampaignControls.Create();
        var binds = new InputBindings(new RetroEng.Platform.Input(controls.Actions));
        Assert.True(SettingsStore.CreateIfMissing(path, binds));
        var text = File.ReadAllText(path);
        Assert.Equal(SettingsStore.Text(new MenuSettings { Bindings = Rebind.Snapshot(binds), PadBindings = PadRebind.Snapshot(binds) }), text);
        Assert.Contains("\"Forward\": [", text, StringComparison.Ordinal);
        Assert.Contains("\"Noclip\": []", text, StringComparison.Ordinal);
        Assert.Contains("\"commandBinds\": {}", text, StringComparison.Ordinal);
        Assert.Equal(["W", "Up"], SettingsStore.Load(path).Bindings["Forward"]);

        // An existing file stays as it is.
        File.WriteAllText(path, text.Replace("\"soundVolume\": 16", "\"soundVolume\": 3", StringComparison.Ordinal));
        Assert.False(SettingsStore.CreateIfMissing(path, binds));
        Assert.Equal(3, SettingsStore.Load(path).SoundVolume);
    }

    [Fact]
    public void AnOldFileWithOnlyChangedBindingsKeepsTheOtherDefaults()
    {
        Directory.CreateDirectory(_dir);
        var path = PathOf("old.json");
        File.WriteAllText(path, $"{{\"format\":\"{SettingsStore.Format}\",\"bindings\":{{\"Forward\":[\"I\"]}}}}");
        var s = SettingsStore.Load(path);
        Assert.Empty(s.CommandBinds);
        var controls = SkyNet.Game.Campaign.CampaignControls.Create();
        var input = new RetroEng.Platform.Input(controls.Actions);
        var binds = new InputBindings(input);
        Rebind.Restore(binds, s.Bindings);
        Assert.Equal(["I"], input.Bindings(controls.Forward));
        Assert.Equal(["S", "Down"], input.Bindings(controls.Back)); // absent from the file: the default
    }

    [Fact]
    public void EachStartShowsTheSameKeys()
    {
        // Three starts on one settings file: a box keeps its key (REVERSE stays S, not DOWN every other run).
        var controls = SkyNet.Game.Campaign.CampaignControls.Create();
        var saved = Rebind.Snapshot(new InputBindings(new RetroEng.Platform.Input(controls.Actions)));
        for (var run = 0; run < 3; run++)
        {
            var input = new RetroEng.Platform.Input(controls.Actions);
            var binds = new InputBindings(input);
            Rebind.Restore(binds, saved);
            Assert.Equal(["S", "Down"], input.Bindings(controls.Back));
            saved = Rebind.Snapshot(binds);
        }
    }

    [Fact]
    public void CommandBindsRoundTrip()
    {
        var s = new MenuSettings();
        s.CommandBinds["F"] = "noclip";
        s.CommandBinds["F9"] = "screenshot shot.bmp";
        var path = PathOf("binds.json");
        Assert.Equal(SaveResult.Saved, SettingsStore.Save(s, path));
        Assert.Contains("\"commandBinds\": {", SettingsStore.Text(s), StringComparison.Ordinal);
        var back = SettingsStore.Load(path);
        Assert.Equal("noclip", back.CommandBinds["f"]);
        Assert.Equal("screenshot shot.bmp", back.CommandBinds["F9"]);
        Assert.Equal(SettingsStore.Text(s), SettingsStore.Text(back));
        Assert.Equal("noclip", s.Clone().CommandBinds["F"]);
    }

    [Fact]
    public void ChangesAreSavedThroughTheMenu()
    {
        var path = PathOf("menu.json");
        var menu = new MenuSystem(new Recorder(), new MenuSettings(), null, path);
        menu.OpenTitle();
        menu.Current!.Select("options");
        menu.Update(new MenuInput { Accept = true }, new MenuFrame(640, 400));
        menu.Current!.Select("difficulty2");
        menu.Update(new MenuInput { Accept = true }, new MenuFrame(640, 400));
        Assert.Equal(SaveResult.Saved, menu.LastSave);
        Assert.Equal(DifficultyLevel.High, SettingsStore.Load(path).Difficulty);
    }
}
