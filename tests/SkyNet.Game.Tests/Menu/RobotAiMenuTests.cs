using SkyNet.Game.Enemies;
using SkyNet.Game.Menu;

namespace SkyNet.Game.Tests.Menu;

/// <summary>OPTIONS → DETAIL → GAMEPLAY: ROBOT AI's row, the settings file, applied and saved at once.</summary>
public class RobotAiMenuTests : IDisposable
{
    private static readonly MenuFrame Frame = new(1280, 800);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "skynet-robotai-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }
    }

    private (MenuSystem Menu, SettingsRecorder Log, string Path) OpenGameplay()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "settings.json");
        var log = new SettingsRecorder();
        var menu = new MenuSystem(log, new MenuSettings(), new MenuContext(), path);
        menu.OpenTitle();
        foreach (var id in new[] { "options", "detail", "gameplay" })
        {
            menu.Current!.Select(id);
            menu.Update(new MenuInput { Accept = true }, Frame);
        }

        Assert.Equal("gameplay", menu.Current!.Name);
        return (menu, log, path);
    }

    [Theory]
    [InlineData(RobotAi.Classic)]
    [InlineData(RobotAi.Improved)]
    public void SettingRoundTrips(RobotAi ai)
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "settings.json");
        Assert.Equal(SaveResult.Saved, SettingsStore.Save(new MenuSettings { RobotAi = ai }, path));

        Assert.Equal(ai, SettingsStore.Load(path).RobotAi);
    }

    [Fact]
    public void DefaultIsImproved() => Assert.Equal(RobotAi.Improved, new MenuSettings().RobotAi);

    [Fact]
    public void RowStepsAppliesAndSaves()
    {
        var (menu, log, path) = OpenGameplay();
        var screen = (GameplayScreen)menu.Current!;
        Assert.Equal("robotai", screen.Highlighted!.Id);
        Assert.Equal("IMPROVED", screen.Value("robotai"));

        // Accept: IMPROVED → CLASSIC, handed to the game and written to the file at once.
        menu.Update(new MenuInput { Accept = true }, Frame);
        Assert.Equal(RobotAi.Classic, menu.Settings.RobotAi);
        Assert.Equal(RobotAi.Classic, log.Applied[^1].RobotAi);
        Assert.Equal(RobotAi.Classic, SettingsStore.Load(path).RobotAi);
        Assert.Equal("CLASSIC", screen.Value("robotai"));
        Assert.Contains("DOS", screen.HintOf("robotai"));

        // Left and right step it too.
        menu.Update(new MenuInput { Right = true }, Frame);
        Assert.Equal(RobotAi.Improved, menu.Settings.RobotAi);
        menu.Update(new MenuInput { Left = true }, Frame);
        Assert.Equal(RobotAi.Classic, menu.Settings.RobotAi);
    }

    [Fact]
    public void ExitAndEscReturnToDetail()
    {
        var (menu, _, _) = OpenGameplay();
        menu.Update(new MenuInput { Back = true }, Frame);
        Assert.Equal("detail", menu.Current!.Name);
    }
}
