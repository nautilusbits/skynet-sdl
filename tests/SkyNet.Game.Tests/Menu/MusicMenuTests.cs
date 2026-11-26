using SkyNet.Game.Audio;
using SkyNet.Game.Menu;

namespace SkyNet.Game.Tests.Menu;

/// <summary>OPTIONS → DETAIL → GAMEPLAY: MUSIC (ORIGINAL / SOUNDFONT) and SOUNDFONT (which .sf2), issue #70.</summary>
public class MusicMenuTests : IDisposable
{
    private static readonly MenuFrame Frame = new(1280, 800);
    private const int MusicRow = 2;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "skynet-music-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }
    }

    private (MenuSystem Menu, SettingsRecorder Log, string Path) OpenGameplay(params string[] fonts)
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "settings.json");
        var log = new SettingsRecorder();
        var menu = new MenuSystem(log, new MenuSettings(), new MenuContext { SoundFonts = () => fonts }, path);
        menu.OpenTitle();
        foreach (var id in new[] { "options", "detail", "gameplay" })
        {
            menu.Current!.Select(id);
            menu.Update(new MenuInput { Accept = true }, Frame);
        }

        for (var i = 0; i < MusicRow; i++)
        {
            menu.Update(new MenuInput { Down = true }, Frame);
        }

        Assert.Equal("music", menu.Current!.Highlighted!.Id);
        return (menu, log, path);
    }

    [Fact]
    public void DefaultIsOriginal()
    {
        Assert.Equal(MusicSynth.Original, new MenuSettings().MusicSynth);
        Assert.Equal("", new MenuSettings().SoundFont);
    }

    [Fact]
    public void SettingRoundTrips()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "settings.json");
        var saved = new MenuSettings { MusicSynth = MusicSynth.SoundFont, SoundFont = "GeneralUser-GS.sf2" };
        Assert.Equal(SaveResult.Saved, SettingsStore.Save(saved, path));

        var loaded = SettingsStore.Load(path);
        Assert.Equal(MusicSynth.SoundFont, loaded.MusicSynth);
        Assert.Equal("GeneralUser-GS.sf2", loaded.SoundFont);
    }

    [Theory]
    [InlineData("x/../a.sf2")]
    [InlineData("a/b/c.sf2")]
    [InlineData("a/b.txt")]
    public void BadSoundFontIdLoadsEmpty(string id)
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "settings.json");
        Assert.Equal(SaveResult.Saved, SettingsStore.Save(new MenuSettings { MusicSynth = MusicSynth.SoundFont, SoundFont = id }, path));

        Assert.Equal("", SettingsStore.Load(path).SoundFont);
    }

    [Fact]
    public void OldModIdLoadsAsFileName()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "settings.json");
        Assert.Equal(SaveResult.Saved, SettingsStore.Save(new MenuSettings { MusicSynth = MusicSynth.SoundFont, SoundFont = "skynet-soundfont/Roland.SC-55.sf2" }, path));

        Assert.Equal("Roland.SC-55.sf2", SettingsStore.Load(path).SoundFont);
    }

    [Fact]
    public void SoundFontUnavailableWithoutSf2()
    {
        var (menu, log, _) = OpenGameplay();
        var screen = (GameplayScreen)menu.Current!;
        Assert.Equal("ORIGINAL", screen.Value("music"));
        Assert.Contains("SOUNDFONTS", screen.HintOf("music"));

        // Accept and right do not reach SOUNDFONT; the SOUNDFONT row says NONE.
        menu.Update(new MenuInput { Accept = true }, Frame);
        menu.Update(new MenuInput { Right = true }, Frame);
        Assert.Equal(MusicSynth.Original, menu.Settings.MusicSynth);
        Assert.DoesNotContain(log.Applied, s => s.MusicSynth == MusicSynth.SoundFont);
        Assert.Equal("NONE", screen.Value("soundfont"));
    }

    [Fact]
    public void PicksSoundFontAndSteps()
    {
        var (menu, log, path) = OpenGameplay("First.sf2", "Second.sf2");
        var screen = (GameplayScreen)menu.Current!;

        // Accept: ORIGINAL → SOUNDFONT with the first .sf2, applied and saved at once.
        menu.Update(new MenuInput { Accept = true }, Frame);
        Assert.Equal(MusicSynth.SoundFont, log.Applied[^1].MusicSynth);
        Assert.Equal("First.sf2", log.Applied[^1].SoundFont);
        Assert.Equal(MusicSynth.SoundFont, SettingsStore.Load(path).MusicSynth);
        Assert.Equal("SOUNDFONT", screen.Value("music"));

        // SOUNDFONT row: right steps to the next file, wrapping.
        menu.Update(new MenuInput { Down = true }, Frame);
        Assert.Equal("FIRST", screen.Value("soundfont"));
        menu.Update(new MenuInput { Right = true }, Frame);
        Assert.Equal("Second.sf2", menu.Settings.SoundFont);
        Assert.Equal("SECOND", screen.Value("soundfont"));
        menu.Update(new MenuInput { Right = true }, Frame);
        Assert.Equal("First.sf2", menu.Settings.SoundFont);
    }
}
