using RetroEng.Platform;
using RetroEng.Render;
using SkyNet.Game.Menu;

namespace SkyNet.Game.Tests.Menu;

/// <summary>3D VIEW (VR plan V7): the screen under DISPLAY driven as a player would, its rows applied live,
/// and the settings file.</summary>
public class StereoMenuTests : IDisposable
{
    private static readonly MenuFrame Frame = new(1280, 800);
    private static readonly DisplayModes Modes = new(new Resolution(1920, 1080), new Resolution(1920, 1040), [new(1920, 1080)]);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "skynet-stereo-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }
    }

    private static (MenuSystem Menu, SettingsRecorder Log) Open3dView(MenuSettings settings, string? path = null)
    {
        var log = new SettingsRecorder();
        var menu = new MenuSystem(log, settings, new MenuContext { Display = () => Modes, EnhancedAvailable = true }, path);
        menu.OpenTitle();
        foreach (var id in new[] { "options", "display", "3dview" })
        {
            menu.Current!.Select(id);
            menu.Update(new MenuInput { Accept = true }, Frame);
        }

        Assert.Equal("3dview", menu.Current!.Name);
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
    public void MenuScriptWalksEveryRowAndBack()
    {
        var (menu, _) = Open3dView(new MenuSettings());
        var seen = new List<string>();
        for (var i = 0; i < StereoScreen.Rows.Length + 1; i++)
        {
            seen.Add(menu.Current!.Highlighted!.Id);
            menu.Update(new MenuInput { Down = true }, Frame);
        }

        Assert.Equal(StereoScreen.Rows.Select(r => r.Id).Append("exit"), seen);
        menu.Update(new MenuInput { Back = true }, Frame);
        Assert.Equal("display", menu.Current!.Name);
    }

    [Fact]
    public void RowsApplyLiveAndAreSaved()
    {
        var path = Path.Combine(_dir, "settings.json");
        var (menu, log) = Open3dView(new MenuSettings(), path);
        var screen = (StereoScreen)menu.Current!;
        // MODE: off → sbs → … → viewer (the last), and left from off wraps to it too.
        Step(menu, "mode", 1, left: true);
        Assert.Equal("viewer", menu.Settings.Stereo);
        Assert.Equal("PHONE VIEWER", screen.Value("mode"));
        Step(menu, "separation", 2);
        Step(menu, "depth");
        Step(menu, "canvas", 2);
        Step(menu, "viewer");
        Step(menu, "head");
        Step(menu, "aim");
        Assert.Equal((MenuSettings.DefaultStereoSeparation + 2, 300, 100, "cardboard1", HeadTracking.Gyro, AimCoupling.Decoupled),
            (menu.Settings.StereoSeparation, menu.Settings.StereoDepth, menu.Settings.CanvasDepth, menu.Settings.Viewer, menu.Settings.HeadTracking, menu.Settings.Aim));
        Assert.Equal(AimCoupling.Decoupled, log.Applied[^1].Aim);

        var back = SettingsStore.Load(path);
        Assert.Equal(("viewer", 300, 100, "cardboard1", HeadTracking.Gyro, AimCoupling.Decoupled),
            (back.Stereo, back.StereoDepth, back.CanvasDepth, back.Viewer, back.HeadTracking, back.Aim));
    }

    [Fact]
    public void StereoSettingsRoundTrip()
    {
        var s = new MenuSettings
        {
            Stereo = "sbsfull", StereoSeparation = 7, StereoDepth = 450, CanvasDepth = 120, Viewer = "custom",
            LensK1 = 25, LensK2 = 61, HeadTracking = HeadTracking.Gyro, Aim = AimCoupling.Decoupled,
        };
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "round.json");
        Assert.Equal(SaveResult.Saved, SettingsStore.Save(s, path));
        var back = SettingsStore.Load(path);
        Assert.Equal(SettingsStore.Text(s), SettingsStore.Text(back));
        Assert.Equal(new LensProfile(0.25f, 0.61f), back.Lens());

        // A file from before V7 keeps the defaults; bad values fall back.
        File.WriteAllText(path, "{\"format\": \"skynet-csharp-settings\", \"version\": 1, \"viewer\": \"oculus\", \"aim\": \"sideways\", \"lensK1\": 900}");
        back = SettingsStore.Load(path);
        Assert.Equal(("generic", AimCoupling.Coupled, HeadTracking.Off, 0), (back.Viewer, back.Aim, back.HeadTracking, back.CanvasDepth));
        Assert.Equal(LensProfile.Generic, back.Lens());
    }

    [Fact]
    public void MenusFitAViewerEye()
    {
        // A 2400 x 1080 phone in the viewer: an eye's canvas is 400 units wide (half the frame's aspect).
        // The screens drawn at the frame's full 2400 pixels spilled past it: each eye showed half of them.
        var eyeCanvas = Renderer.CanvasHeight * 1200f / 1080f;
        var frame = MenuFrame.ForCanvas(eyeCanvas, 1080);
        Assert.Equal((1200, 1080), (frame.Width, frame.Height));
        Assert.Equal(eyeCanvas, frame.Window.Width, 1e-3f);
    }
}
