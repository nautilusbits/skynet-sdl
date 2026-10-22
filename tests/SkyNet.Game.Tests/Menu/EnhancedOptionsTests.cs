using System.Numerics;
using RetroEng.Platform;
using RetroEng.Render;
using SkyNet.Game.Enhanced;
using SkyNet.Game.Menu;

namespace SkyNet.Game.Tests.Menu;

/// <summary>Records every settings hand-over (SettingsChanged) as a copy.</summary>
internal sealed class SettingsRecorder : IMenuActions
{
    public List<MenuSettings> Applied { get; } = [];
    public void StartGame(SkyNet.Formats.GameKind game, int mission, SkyNet.Game.Campaign.Pilot pilot) { }
    public void BeginMission() { }
    public void Resume() { }
    public void RestartMission() { }
    public void QuitToTitle() { }
    public void Quit() { }
    public void LoadGame(int slot) { }
    public bool SaveGame(int slot) => true;
    public void SettingsChanged(MenuSettings settings) => Applied.Add(settings.Clone());
    public void Sound(string name) { }
}

/// <summary>ENHANCED OPTIONS (S19): the screen under DISPLAY, its rows applied live, the settings
/// file, and the light they make (no GPU).</summary>
public class EnhancedOptionsTests : IDisposable
{
    private static readonly MenuFrame Frame = new(1280, 800);
    private static readonly DisplayModes Modes = new(new Resolution(1920, 1080), new Resolution(1920, 1040), [new(1920, 1080)]);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "skynet-enhanced-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }
    }

    private static (MenuSystem Menu, SettingsRecorder Log) OpenEnhanced(MenuSettings settings, string? path = null)
    {
        var log = new SettingsRecorder();
        var menu = new MenuSystem(log, settings, new MenuContext { Display = () => Modes, EnhancedAvailable = true }, path);
        menu.OpenTitle();
        foreach (var id in new[] { "options", "display", "enhanced" })
        {
            menu.Current!.Select(id);
            menu.Update(new MenuInput { Accept = true }, Frame);
        }

        Assert.Equal("enhanced", menu.Current!.Name);
        return (menu, log);
    }

    private static void Step(MenuSystem menu, string row, int times = 1, bool left = false)
    {
        menu.Current!.Select(row);
        for (var i = 0; i < times; i++)
        {
            menu.Update(left ? new MenuInput { Left = true } : new MenuInput { Right = true }, Frame);
        }
    }

    [Fact]
    public void DefaultsAreTheEnhancedLookAsItWas()
    {
        var s = new MenuSettings();
        Assert.Equal((ShadowQuality.High, AntiAliasing.Fxaa, 256, 100, true, true),
            (s.Shadows, s.AntiAliasing, s.LightBudget, s.Ambient, s.AmbientOcclusion, s.Glow));
    }

    [Fact]
    public void DisplayKeepsItsRowsAndOpensEnhancedFromItsButton()
    {
        Assert.Equal(12, DisplayScreen.Rows.Length);
        var (menu, _) = OpenEnhanced(new MenuSettings { Look = Look.Enhanced });
        var screen = (EnhancedScreen)menu.Current!;
        Assert.False(screen.ForOriginal);
        Assert.Equal("shadows", screen.Highlighted!.Id);
        var seen = new List<string>();
        for (var i = 0; i < EnhancedScreen.Rows.Length + 1; i++)
        {
            seen.Add(menu.Current!.Highlighted!.Id);
            menu.Update(new MenuInput { Down = true }, Frame);
        }

        Assert.Equal(EnhancedScreen.Rows.Select(r => r.Id).Append("exit"), seen);
        menu.Update(new MenuInput { Back = true }, Frame);
        Assert.Equal("display", menu.Current!.Name);
        menu.Current.Select("enhanced");
        menu.Update(new MenuInput { Accept = true }, Frame);
        menu.Current!.Select("exit");
        menu.Update(new MenuInput { Accept = true }, Frame);
        Assert.Equal("display", menu.Current!.Name);
    }

    [Fact]
    public void RowsApplyAtOnce()
    {
        var (menu, log) = OpenEnhanced(new MenuSettings { Look = Look.Enhanced });
        var screen = (EnhancedScreen)menu.Current!;
        Step(menu, "shadows");
        Assert.Equal(ShadowQuality.Off, menu.Settings.Shadows);   // HIGH wraps to OFF
        Step(menu, "shadows", left: true);
        Assert.Equal(ShadowQuality.High, menu.Settings.Shadows);
        Step(menu, "aa", 2);
        Assert.Equal(AntiAliasing.Msaa4, menu.Settings.AntiAliasing);
        Assert.Equal("MSAA 4X", screen.Value("aa"));
        Step(menu, "lights");
        Assert.Equal(MenuSettings.LowLightBudget, menu.Settings.LightBudget);
        Assert.Equal("LOW (64)", screen.Value("lights"));
        Step(menu, "ambient", 5);
        Assert.Equal(150, menu.Settings.Ambient);
        Step(menu, "ambient", 20);
        Assert.Equal(MenuSettings.MaxAmbient, menu.Settings.Ambient);
        Step(menu, "ambient", 20, left: true);
        Assert.Equal(MenuSettings.MinAmbient, menu.Settings.Ambient);
        Assert.Equal("50%", screen.Value("ambient"));
        Step(menu, "occlusion");
        Assert.False(menu.Settings.AmbientOcclusion);
        Step(menu, "glow");
        Assert.False(menu.Settings.Glow);
        Assert.Equal("OFF", screen.Value("glow"));
        Assert.Equal(2 + 2 + 1 + 45 + 1 + 1, log.Applied.Count);
        Assert.False(log.Applied[^1].Glow);
    }

    [Fact]
    public void UnderOriginalTheRowsWaitForEnhanced()
    {
        var (menu, log) = OpenEnhanced(new MenuSettings { Look = Look.Original });
        Assert.True(((EnhancedScreen)menu.Current!).ForOriginal);
        Step(menu, "glow");
        Assert.False(menu.Settings.Glow);
        Assert.Equal(Look.Original, menu.Settings.Look);
        Assert.Single(log.Applied);
    }

    [Fact]
    public void ChoicesAreSavedAndReadBack()
    {
        var path = Path.Combine(_dir, "settings.json");
        var (menu, _) = OpenEnhanced(new MenuSettings { Look = Look.Enhanced }, path);
        Step(menu, "aa", 3);
        Step(menu, "ambient", 3, left: true);
        var back = SettingsStore.Load(path);
        Assert.Equal((AntiAliasing.Off, 70), (back.AntiAliasing, back.Ambient));
    }

    [Fact]
    public void EnhancedSettingsRoundTrip()
    {
        var s = new MenuSettings
        {
            Shadows = ShadowQuality.Low, AntiAliasing = AntiAliasing.Msaa2, LightBudget = MenuSettings.LowLightBudget,
            Ambient = 170, AmbientOcclusion = false, Glow = false, Relief = ReliefLevel.High, Particles = ParticleLevel.Low,
        };
        var path = Path.Combine(_dir, "round.json");
        Assert.Equal(SaveResult.Saved, SettingsStore.Save(s, path));
        var back = SettingsStore.Load(path);
        Assert.Equal(SettingsStore.Text(s), SettingsStore.Text(back));
        Assert.Equal(ReliefLevel.High, back.Relief);
        Assert.Equal(ParticleLevel.Low, back.Particles);
        Assert.Equal((ShadowQuality.Low, AntiAliasing.Msaa2, 64, 170, false, false),
            (back.Shadows, back.AntiAliasing, back.LightBudget, back.Ambient, back.AmbientOcclusion, back.Glow));
    }

    [Fact]
    public void BadEnhancedValuesFallBack()
    {
        var text = SettingsStore.Text(new MenuSettings())
            .Replace("\"shadows\": \"High\"", "\"shadows\": \"Ultra\"", StringComparison.Ordinal)
            .Replace("\"antiAliasing\": \"Fxaa\"", "\"antiAliasing\": 3", StringComparison.Ordinal)
            .Replace("\"lightBudget\": 256", "\"lightBudget\": 100", StringComparison.Ordinal)
            .Replace("\"ambient\": 100", "\"ambient\": 400", StringComparison.Ordinal);
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "bad.json");
        File.WriteAllText(path, text);
        var back = SettingsStore.Load(path);
        Assert.Equal((ShadowQuality.High, AntiAliasing.Fxaa, 256, 100), (back.Shadows, back.AntiAliasing, back.LightBudget, back.Ambient));

        // A file from before S19 has none of them: the defaults.
        File.WriteAllText(path, "{\"format\": \"skynet-csharp-settings\", \"version\": 1, \"look\": \"Enhanced\"}");
        back = SettingsStore.Load(path);
        Assert.Equal((Look.Enhanced, ShadowQuality.High, AntiAliasing.Fxaa, true, true),
            (back.Look, back.Shadows, back.AntiAliasing, back.AmbientOcclusion, back.Glow));
    }

    [Fact]
    public void ParticlesStepOffLowHigh()
    {
        var (menu, log) = OpenEnhanced(new MenuSettings { Look = Look.Enhanced });
        var screen = (EnhancedScreen)menu.Current!;
        Assert.Equal("HIGH", screen.Value("particles"));
        Step(menu, "particles");
        Assert.Equal(ParticleLevel.Off, menu.Settings.Particles);   // HIGH wraps to OFF
        Step(menu, "particles");
        Assert.Equal("LOW", screen.Value("particles"));
        Assert.Equal(ParticleLevel.Low, log.Applied[^1].Particles);
        // Eight rows and the note above EXIT.
        Assert.True(EnhancedScreen.NoteRect.Bottom <= EnhancedScreen.ExitRect.Y + EnhancedScreen.ExitRect.Height
            && EnhancedScreen.RowRect(EnhancedScreen.Rows.Length - 1).Bottom <= EnhancedScreen.NoteRect.Y + 1);
    }

    [Fact]
    public void NightHasStarsAwayFromTheMoon()
    {
        var night = LookLighting.For(LightPreset.Night, null, 2000f, 1f);
        Assert.NotNull(night.Stars);
        Assert.True(Vector3.Dot(night.Stars!.Avoid, Level.MoonDirection()) > 0.999f);
        Assert.Null(LookLighting.For(LightPreset.Dusk, null, 2000f, 1f).Stars);
        Assert.Equal(LookLighting.ExposureMood, night.Exposure);
    }

    [Fact]
    public void TheLightTakesTheOptions()
    {
        var on = LookLighting.For(LightPreset.Night, null, 2000f, 1f);
        var off = LookLighting.For(LightPreset.Night, null, 2000f, 1f, ShadowQuality.Off, 1.5f, occlusion: false, glow: false);
        Assert.Equal(ShadowQuality.Off, off.ShadowQuality);
        Assert.Equal(on.Sky * 1.5f, off.Sky);
        Assert.Equal(on.Ground * 1.5f, off.Ground);
        Assert.Equal(on.Sun, off.Sun);   // the moon is not the ambient
        Assert.True(on.OcclusionStrength > 0f && on.GlowIntensity > 0f && on.Glow > 0f);
        Assert.Equal((0f, 0f, 0f), (off.OcclusionStrength, off.GlowIntensity, off.Glow));

        var indoor = LookLighting.For(LightPreset.Interior, null, 2000f, 1f, ShadowQuality.High, 0.5f);
        Assert.Equal(ShadowQuality.Off, indoor.ShadowQuality);   // no sun indoors, whatever SHADOWS says
        Assert.Equal(LookLighting.IndoorAmbient * (0.5f * LookLighting.InteriorAmbientMood), indoor.Sky);
        Assert.Equal(1.5f, new MenuSettings { Ambient = 150 }.AmbientScale);
        Assert.Equal(Vector3.UnitY, indoor.Up);
    }
}
