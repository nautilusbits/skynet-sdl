using RetroEng.Platform;
using RetroEng.Render;
using SkyNet.Game.Menu;

namespace SkyNet.Game.Tests.Menu;

/// <summary>3D VIEW's BACKLIGHT: the level by setting and layout, the menu row, the settings file, the hook.</summary>
public class ScreenBrightnessTests : IDisposable
{
    private static readonly MenuFrame Frame = new(1280, 800);
    private static readonly DisplayModes Modes = new(new Resolution(1920, 1080), new Resolution(1920, 1040), [new(1920, 1080)]);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "skynet-backlight-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }
    }

    [Theory]
    [InlineData(ScreenBrightness.Auto, StereoLayout.Viewer, 1f)]
    [InlineData(ScreenBrightness.Auto, StereoLayout.Mono, ScreenBrightnessLevels.System)]
    [InlineData(ScreenBrightness.System, StereoLayout.Viewer, ScreenBrightnessLevels.System)]
    [InlineData(ScreenBrightness.Percent50, StereoLayout.Mono, 0.5f)]
    [InlineData(ScreenBrightness.Percent75, StereoLayout.Viewer, 0.75f)]
    [InlineData(ScreenBrightness.Max, StereoLayout.Mono, 1f)]
    public void LevelBySettingAndLayout(ScreenBrightness setting, StereoLayout layout, float level) =>
        Assert.Equal(level, ScreenBrightnessLevels.Level(setting, layout));

    [Fact]
    public void DefaultIsAuto() => Assert.Equal(ScreenBrightness.Auto, new MenuSettings().ScreenBrightness);

    [Fact]
    public void RowStepsAndIsSaved()
    {
        var path = Path.Combine(_dir, "settings.json");
        var menu = new MenuSystem(new SettingsRecorder(), new MenuSettings(), new MenuContext { Display = () => Modes, EnhancedAvailable = true }, path);
        menu.OpenTitle();
        foreach (var id in new[] { "options", "display", "3dview" })
        {
            menu.Current!.Select(id);
            menu.Update(new MenuInput { Accept = true }, Frame);
        }

        // AUTO → SYSTEM → 50% → 75% → MAX → AUTO; left from AUTO wraps to MAX.
        var screen = (StereoScreen)menu.Current!;
        screen.Select("brightness");
        var names = new List<string>();
        for (var i = 0; i < 5; i++)
        {
            names.Add(screen.Value("brightness"));
            menu.Update(new MenuInput { Right = true }, Frame);
        }

        Assert.Equal(["AUTO", "SYSTEM", "50%", "75%", "MAX"], names);
        Assert.Equal("AUTO", screen.Value("brightness"));
        menu.Update(new MenuInput { Left = true }, Frame);
        Assert.Equal(ScreenBrightness.Max, menu.Settings.ScreenBrightness);
        Assert.Equal(ScreenBrightness.Max, SettingsStore.Load(path).ScreenBrightness);

        // A file without it, or with a bad value, keeps AUTO.
        File.WriteAllText(path, "{\"format\": \"skynet-csharp-settings\", \"version\": 1, \"screenBrightness\": \"dazzling\"}");
        Assert.Equal(ScreenBrightness.Auto, SettingsStore.Load(path).ScreenBrightness);
    }

    [Fact]
    public void HookGetsEachChangeOnce()
    {
        var calls = new List<float>();
        var window = new WindowBrightness(calls.Add);
        window.Update(ScreenBrightness.Auto, StereoLayout.Mono);
        window.Update(ScreenBrightness.Auto, StereoLayout.Mono);
        window.Update(ScreenBrightness.Auto, StereoLayout.Viewer);
        window.Update(ScreenBrightness.Auto, StereoLayout.Viewer);
        window.Update(ScreenBrightness.Auto, StereoLayout.SideBySide);
        window.Update(ScreenBrightness.Percent50, StereoLayout.Viewer);
        window.Update(ScreenBrightness.System, StereoLayout.Viewer);

        // Leaving the viewer and SYSTEM hand back the system's brightness.
        Assert.Equal([ScreenBrightnessLevels.System, 1f, ScreenBrightnessLevels.System, 0.5f, ScreenBrightnessLevels.System], calls);
    }

    [Fact]
    public void DesktopWithoutHookIsNoOp()
    {
        var window = new WindowBrightness(null);
        window.Update(ScreenBrightness.Max, StereoLayout.Viewer);
        Assert.Null(window.Applied);
    }
}
