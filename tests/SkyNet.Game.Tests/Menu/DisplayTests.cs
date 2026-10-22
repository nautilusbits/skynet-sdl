using RetroEng.Platform;
using RetroEng.Render;
using SkyNet.Formats;
using SkyNet.Game.Menu;

namespace SkyNet.Game.Tests.Menu;

/// <summary>Records each window the menus hand the game (SettingsChanged), as the app applies it.</summary>
internal sealed class DisplayRecorder : IMenuActions
{
    public List<DisplayChoice> Applied { get; } = [];
    public void StartGame(GameKind game, int mission, SkyNet.Game.Campaign.Pilot pilot) { }
    public void BeginMission() { }
    public void Resume() { }
    public void RestartMission() { }
    public void QuitToTitle() { }
    public void Quit() { }
    public void LoadGame(int slot) { }
    public bool SaveGame(int slot) => true;
    public void SettingsChanged(MenuSettings settings) => Applied.Add(DisplayChoice.From(settings));
    public void Sound(string name) { }
}

public class DisplayOptionsTests
{
    private static readonly DisplayModes Modes = new(new Resolution(1920, 1080), new Resolution(1920, 1040),
        [new(1920, 1080), new(1280, 720), new(1920, 1080), new(800, 600), new(2560, 1440)]);

    [Fact]
    public void WindowedSizesFitTheUsableAreaAndKeepTheWindowsOwn()
    {
        var sizes = DisplayOptions.Sizes(WindowMode.Windowed, Modes, new Resolution(1000, 700));
        Assert.Contains(new Resolution(1000, 700), sizes);
        Assert.Contains(new Resolution(1280, 800), sizes);   // 16:10
        Assert.Contains(new Resolution(1366, 768), sizes);   // 16:9
        Assert.Contains(new Resolution(1024, 768), sizes);   // 4:3
        Assert.DoesNotContain(new Resolution(1920, 1080), sizes);   // taller than the usable area
        Assert.All(sizes, s => Assert.True(s.Width <= 1920 && s.Height <= 1040 || s == new Resolution(1000, 700)));
        Assert.Equal(sizes.OrderBy(s => s.Area).ThenBy(s => s.Width), sizes);
    }

    [Fact]
    public void FullscreenSizesAreTheDisplaysModesOnceEach()
    {
        var sizes = DisplayOptions.Sizes(WindowMode.Fullscreen, Modes, new Resolution(1280, 720));
        Assert.Equal([new(800, 600), new(1280, 720), new(1920, 1080), new(2560, 1440)], sizes);
        Assert.Equal([new Resolution(1920, 1080)], DisplayOptions.Sizes(WindowMode.Fullscreen, Modes with { Fullscreen = [] }, default));
        Assert.Equal([new Resolution(1920, 1080)], DisplayOptions.Sizes(WindowMode.Borderless, Modes, new Resolution(1280, 720)));
    }

    [Fact]
    public void StepsWrapAndStayInTheirMode()
    {
        var c = new DisplayChoice(WindowMode.Windowed, new Resolution(1280, 720), null);
        Assert.Equal(WindowMode.Borderless, DisplayOptions.StepMode(c, 1).Mode);
        Assert.Equal(WindowMode.Fullscreen, DisplayOptions.StepMode(c, -1).Mode);
        Assert.Equal(new Resolution(1280, 800), DisplayOptions.StepSize(c, Modes, 1).Window);

        var full = c with { Mode = WindowMode.Fullscreen };
        Assert.Equal(new Resolution(1920, 1080), full.Shown(Modes));   // none chosen: the desktop's
        var next = DisplayOptions.StepSize(full, Modes, 1);
        Assert.Equal(new Resolution(2560, 1440), next.Fullscreen);
        Assert.Equal(new Resolution(1280, 720), next.Window);   // the windowed size stays
        Assert.Equal(new Resolution(800, 600), DisplayOptions.StepSize(next, Modes, 1).Fullscreen);

        var borderless = c with { Mode = WindowMode.Borderless };
        Assert.Equal(borderless, DisplayOptions.StepSize(borderless, Modes, 1));

        Assert.Equal(VSync.On, DisplayOptions.StepVSync(VSync.Off, 1));
        Assert.Equal(VSync.Off, DisplayOptions.StepVSync(VSync.Adaptive, 1));
        Assert.Equal(Renderer.AutoScale, DisplayOptions.StepScale(50, -1));
        Assert.Equal(150, DisplayOptions.StepScale(100, 1));
        Assert.Equal(MenuSettings.MaxBrightness, DisplayOptions.StepBrightness(MenuSettings.MaxBrightness, 1));
        Assert.Equal(90, DisplayOptions.StepBrightness(100, -1));
    }

    [Fact]
    public void ScaleCyclesFromAuto()
    {
        var seen = new List<int>();
        var scale = Renderer.AutoScale;
        for (var i = 0; i < 5; i++)
        {
            seen.Add(scale);
            scale = DisplayOptions.StepScale(scale, 1);
        }

        Assert.Equal([Renderer.AutoScale, 50, 100, 150, 200], seen);
        Assert.Equal(Renderer.AutoScale, scale);
        Assert.Equal(200, DisplayOptions.StepScale(Renderer.AutoScale, -1));
        Assert.Equal(Renderer.FullScale, DisplayOptions.StepScale(77, 1));
        Assert.Equal("AUTO 63%", DisplayOptions.ScaleName(Renderer.AutoScale, 63));
        Assert.Equal("AUTO", DisplayOptions.ScaleName(Renderer.AutoScale, null));
        Assert.Equal("150%", DisplayOptions.ScaleName(150, 63));
    }
}

public class DisplayScreenTests
{
    private static readonly MenuFrame Frame = new(1280, 800);
    private static readonly DisplayModes Modes = new(new Resolution(1920, 1080), new Resolution(1920, 1040), [new(1280, 720), new(1920, 1080)]);

    private static (MenuSystem Menu, DisplayRecorder Log) OpenDisplay(MenuSettings? settings = null, MenuContext? context = null)
    {
        var log = new DisplayRecorder();
        var menu = new MenuSystem(log, settings ?? new MenuSettings(), context ?? new MenuContext { Display = () => Modes });
        menu.OpenTitle();
        menu.Current!.Select("options");
        menu.Update(new MenuInput { Accept = true }, Frame);
        menu.Current!.Select("display");
        menu.Update(new MenuInput { Accept = true }, Frame);
        Assert.Equal("display", menu.Current!.Name);
        return (menu, log);
    }

    private static void Step(MenuSystem menu, string row, int times = 1)
    {
        menu.Current!.Select(row);
        for (var i = 0; i < times; i++)
        {
            menu.Update(new MenuInput { Right = true }, Frame);
        }
    }

    private static void Press(MenuSystem menu, string id)
    {
        menu.Current!.Select(id);
        menu.Update(new MenuInput { Accept = true }, Frame);
    }

    [Fact]
    public void OptionsReachesDisplayAndRowsGoDown()
    {
        var (menu, _) = OpenDisplay();
        Assert.Equal("mode", menu.Current!.Highlighted!.Id);
        var seen = new List<string>();
        for (var i = 0; i < DisplayScreen.Rows.Length + 1; i++)
        {
            seen.Add(menu.Current.Highlighted!.Id);
            menu.Update(new MenuInput { Down = true }, Frame);
        }

        // Below the rows: ENHANCED... (centred, between APPLY and EXIT).
        Assert.Equal(DisplayScreen.Rows.Select(r => r.Id).Append("enhanced"), seen);
        menu.Update(new MenuInput { Back = true }, Frame);
        Assert.Equal("options", menu.Current!.Name);
    }

    [Fact]
    public void ModeAndSizeWaitForApply()
    {
        var (menu, log) = OpenDisplay();
        Step(menu, "size");
        var screen = (DisplayScreen)menu.Current!;
        Assert.True(screen.Dirty);
        Assert.Equal(new Resolution(1280, 720), menu.Settings.WindowSize);
        Assert.Empty(log.Applied);
        Assert.Equal("1280 X 800", screen.Value("size"));

        // Esc drops it.
        menu.Update(new MenuInput { Back = true }, Frame);
        Press(menu, "display");
        Assert.False(((DisplayScreen)menu.Current!).Dirty);
    }

    [Fact]
    public void ApplyTriesTheModeAndYesKeepsIt()
    {
        var (menu, log) = OpenDisplay();
        Step(menu, "mode", 2);
        Step(menu, "size");
        Press(menu, "apply");
        Assert.Equal("keep", menu.Current!.Name);
        Assert.Equal(new DisplayChoice(WindowMode.Fullscreen, new Resolution(1280, 720), new Resolution(1280, 720)), log.Applied.Single());
        Assert.Equal(WindowMode.Fullscreen, menu.Settings.WindowMode);

        Press(menu, "yes");
        Assert.Equal("display", menu.Current!.Name);
        Assert.False(((DisplayScreen)menu.Current).Dirty);
        Assert.Equal(WindowMode.Fullscreen, menu.Settings.WindowMode);
        Assert.Equal(new Resolution(1280, 720), menu.Settings.FullscreenSize);
    }

    [Theory]
    [InlineData("no")]
    [InlineData("esc")]
    [InlineData("timeout")]
    public void UnconfirmedModeReverts(string how)
    {
        var (menu, log) = OpenDisplay();
        Step(menu, "mode");
        Press(menu, "apply");
        var keep = (KeepBox)menu.Current!;
        Assert.Equal(WindowMode.Borderless, menu.Settings.WindowMode);
        switch (how)
        {
            case "no": Press(menu, "no"); break;
            case "esc": menu.Update(new MenuInput { Back = true }, Frame); break;
            default:
                menu.Update(new MenuInput(), Frame, KeepBox.Seconds - 1f);
                Assert.Same(keep, menu.Current);
                Assert.Equal(1f, keep.Left, 3);
                menu.Update(new MenuInput(), Frame, 1.5f);
                break;
        }

        Assert.Equal("display", menu.Current!.Name);
        Assert.Equal(WindowMode.Windowed, menu.Settings.WindowMode);
        Assert.Equal([WindowMode.Borderless, WindowMode.Windowed], log.Applied.Select(a => a.Mode));
        Assert.False(((DisplayScreen)menu.Current).Dirty);
        Assert.Equal("DISPLAY SETTINGS REVERTED", menu.Toast);
    }

    [Fact]
    public void LiveRowsApplyAtOnce()
    {
        var (menu, log) = OpenDisplay();
        Step(menu, "vsync");
        Assert.Equal(VSync.Adaptive, menu.Settings.VSync);
        Step(menu, "scale");
        Assert.Equal(150, menu.Settings.RenderScale);
        Step(menu, "brightness");
        Assert.Equal(110, menu.Settings.Brightness);
        Assert.Equal(1.1f, menu.Settings.Gamma, 3);
        Step(menu, "fps");
        Assert.True(menu.Settings.ShowFps);
        Assert.Equal(4, log.Applied.Count);
    }

    [Fact]
    public void EnhancedIsComingSoon()
    {
        var (menu, log) = OpenDisplay();
        Step(menu, "look");
        Assert.Equal(Look.Original, menu.Settings.Look);
        Assert.Equal("ENHANCED: COMING SOON", menu.Toast);
        Assert.Empty(log.Applied);
    }

    [Fact]
    public void FixedFullscreenHidesWindowRowsAndMovesTheRestUp()
    {
        var context = new MenuContext { Display = () => Modes, Window = WindowKind.FixedFullscreen, RenderScaleInUse = () => 63 };
        var (menu, _) = OpenDisplay(new MenuSettings { RenderScale = Renderer.AutoScale }, context);
        var screen = (DisplayScreen)menu.Current!;
        Assert.Null(screen.Find("mode"));
        Assert.Null(screen.Find("size"));
        Assert.Equal("vsync", screen.Highlighted!.Id);

        // Each shown row takes the next slot from the top: no gap where WINDOW and RESOLUTION were.
        var shown = DisplayScreen.Rows.Skip(2).ToArray();
        for (var i = 0; i < shown.Length; i++)
        {
            Assert.Equal(MenuLayout.At(screen.Origin, DisplayScreen.RowRect(i)), screen.Find(shown[i].Id)!.Rect);
        }

        Assert.Equal("AUTO 63%", screen.Value("scale"));
        Step(menu, "scale");
        Assert.Equal(50, menu.Settings.RenderScale);
    }

    [Fact]
    public void DesktopKeepsWindowRowsInPlace()
    {
        var (menu, _) = OpenDisplay();
        var screen = (DisplayScreen)menu.Current!;
        for (var i = 0; i < DisplayScreen.Rows.Length; i++)
        {
            Assert.Equal(MenuLayout.At(screen.Origin, DisplayScreen.RowRect(i)), screen.Find(DisplayScreen.Rows[i].Id)!.Rect);
        }
    }

    [Fact]
    public void HudStyleStepsAtOnce()
    {
        var (menu, _) = OpenDisplay();
        var screen = (DisplayScreen)menu.Current!;
        Assert.Equal("CLASSIC", screen.Value("hud"));
        Step(menu, "hud");
        Assert.Equal(SkyNet.Game.Hud.HudStyle.Modern, menu.Settings.HudStyle);
        Assert.Equal("MODERN", screen.Value("hud"));
        Step(menu, "hud");
        Assert.Equal(SkyNet.Game.Hud.HudStyle.Classic, menu.Settings.HudStyle);
    }

    [Fact]
    public void UiScaleStepsAtOnce()
    {
        var (menu, log) = OpenDisplay();
        var screen = (DisplayScreen)menu.Current!;
        Assert.Equal("AUTO", screen.Value("uiscale"));
        Step(menu, "uiscale");
        Assert.Equal(SkyNet.Game.Hud.UiScale.X1, menu.Settings.UiScale);
        Assert.Equal("1X", screen.Value("uiscale"));
        Step(menu, "uiscale", 3);
        Assert.Equal(SkyNet.Game.Hud.UiScale.Auto, menu.Settings.UiScale);
        Assert.Equal(4, log.Applied.Count);
    }

    [Fact]
    public void NothingToApplyStaysOnTheScreen()
    {
        var (menu, log) = OpenDisplay();
        Press(menu, "apply");
        Assert.Equal("display", menu.Current!.Name);
        Assert.Empty(log.Applied);
    }
}

public class DisplaySettingsStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "skynet-display-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }
    }

    [Fact]
    public void DisplaySettingsRoundTrip()
    {
        var s = new MenuSettings
        {
            WindowMode = WindowMode.Fullscreen, FullscreenSize = new Resolution(2560, 1440), VSync = VSync.Adaptive,
            RenderScale = 50, Brightness = 130, Look = Look.Enhanced, ShowFps = true,
        };
        var path = Path.Combine(_dir, "settings.json");
        Assert.Equal(SaveResult.Saved, SettingsStore.Save(s, path));
        var back = SettingsStore.Load(path);
        Assert.Equal(SettingsStore.Text(s), SettingsStore.Text(back));
        Assert.Equal(new Resolution(2560, 1440), back.FullscreenSize);
        Assert.Equal((VSync.Adaptive, 50, 130, Look.Enhanced), (back.VSync, back.RenderScale, back.Brightness, back.Look));
    }

    [Fact]
    public void HudStyleRoundTripsAndDefaultsToClassic()
    {
        Assert.Equal(SkyNet.Game.Hud.HudStyle.Classic, new MenuSettings().HudStyle);
        var path = Path.Combine(_dir, "hud.json");
        Assert.Equal(SaveResult.Saved, SettingsStore.Save(new MenuSettings { HudStyle = SkyNet.Game.Hud.HudStyle.Modern }, path));
        Assert.Equal(SkyNet.Game.Hud.HudStyle.Modern, SettingsStore.Load(path).HudStyle);
    }

    [Fact]
    public void UiScaleRoundTripsAndDefaultsToAuto()
    {
        Assert.Equal(SkyNet.Game.Hud.UiScale.Auto, new MenuSettings().UiScale);
        var path = Path.Combine(_dir, "ui.json");
        Assert.Equal(SaveResult.Saved, SettingsStore.Save(new MenuSettings { UiScale = SkyNet.Game.Hud.UiScale.X2 }, path));
        Assert.Equal(SkyNet.Game.Hud.UiScale.X2, SettingsStore.Load(path).UiScale);
    }

    [Fact]
    public void AutoScaleRoundTrips()
    {
        var path = Path.Combine(_dir, "auto.json");
        Assert.Equal(SaveResult.Saved, SettingsStore.Save(new MenuSettings { RenderScale = Renderer.AutoScale }, path));
        Assert.Equal(Renderer.AutoScale, SettingsStore.Load(path).RenderScale);
    }

    [Fact]
    public void BadDisplayValuesFallBack()
    {
        var d = new MenuSettings();
        Assert.Null(d.FullscreenSize);
        var text = SettingsStore.Text(d)
            .Replace("\"renderScale\": 100", "\"renderScale\": 77", StringComparison.Ordinal)
            .Replace("\"brightness\": 100", "\"brightness\": 900", StringComparison.Ordinal)
            .Replace("\"vsync\": \"On\"", "\"vsync\": \"Sometimes\"", StringComparison.Ordinal)
            .Replace("\"fullscreenWidth\": 0", "\"fullscreenWidth\": 1920", StringComparison.Ordinal);
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "bad.json");
        File.WriteAllText(path, text);
        var back = SettingsStore.Load(path);
        Assert.Equal((Renderer.FullScale, MenuSettings.DefaultBrightness, VSync.On), (back.RenderScale, back.Brightness, back.VSync));
        Assert.Null(back.FullscreenSize);   // one side alone is no size
    }
}
