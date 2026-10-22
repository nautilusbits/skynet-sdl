using System.Diagnostics;
using System.Numerics;
using RetroEng.Editor;
using RetroEng.Render;
using SkyNet.Formats;
using SkyNet.Game.Menu;

namespace SkyNet.Edit.Tests;

/// <summary>Every user-facing setting kept in the preferences file: round trip, old files, the pref
/// command's setter, Reset settings, and the command line's overrides kept out of the file.</summary>
public sealed class EditorPrefsTests
{
    private static string TempPrefs() => Path.Combine(Path.GetTempPath(), $"SkyNetEdit-{Guid.NewGuid():N}.json");

    /// <summary>Every field away from its default.</summary>
    private static EditorPrefs Changed() => new()
    {
        CameraSpeed = 2750f, MouseSensitivity = 1.5f, InvertY = true, FieldOfView = 85f, DrawDistance = 12000f,
        DefaultLook = Look.Enhanced, DataDir = @"D:\data", ModsDir = @"D:\mods", UiScale = 1.25f, FontSize = 16f, UiScaleAuto = false,
        Mods = ["alpha", "beta"], Recent = [3, 1],
        ShowOutliner = false, ShowProperties = false, ShowLog = false, ShowPlace = false, ShowTriggers = false,
        ShowMission = true, ShowValidation = true, ShowAssets = true,
        SnapGrid = true, GridSize = 256, SnapAngle = true, AngleStep = 64, SnapFloor = true, GizmoLocal = true,
        Window = new WindowState(10, 20, 800, 600, Maximized: true, Fullscreen: false, Display: "D1"),
        ShowLinks = false, ShowMarkers = false, ShowLights = false, ShowSprites = false, ShowRobots = false, ShowEffects = false,
        Gizmo = GizmoMode.Rotate, PlaceTab = PlaceKind.Light,
        AssetKind = "Sounds", AssetOrigin = AssetOrigin.Mod, AssetMapOnly = true, AssetSearch = "door open",
        SaveMod = "mymod",
        Shadows = ShadowQuality.Low, AntiAliasing = AntiAliasing.Msaa4, LightBudget = MenuSettings.LowLightBudget, Ambient = 150,
        AmbientOcclusion = false, Glow = false, Relief = ReliefLevel.High, Particles = ParticleLevel.Low,
        Place = new EditorPlace(GameKind.FutureShock, 4, Arena: false, "M2", new Vector3(100f, 200.5f, -300f), 1.25f, -0.5f),
    };

    [Fact]
    public void EveryPreferenceRoundTrips()
    {
        var path = TempPrefs();
        try
        {
            var p = Changed();
            Assert.True(p.Save(path));
            var q = EditorPrefs.Load(path);

            // The whole file the same, then each new field by name.
            Assert.Equal(p.ToJson(), q.ToJson());
            Assert.Equal((false, false, false, false, false, false), (q.ShowLinks, q.ShowMarkers, q.ShowLights, q.ShowSprites, q.ShowRobots, q.ShowEffects));
            Assert.Equal(GizmoMode.Rotate, q.Gizmo);
            Assert.Equal(PlaceKind.Light, q.PlaceTab);
            Assert.Equal(("Sounds", AssetOrigin.Mod, true, "door open"), (q.AssetKind, q.AssetOrigin, q.AssetMapOnly, q.AssetSearch));
            Assert.Equal("mymod", q.SaveMod);
            Assert.Equal(p.Place, q.Place);
            Assert.Equal(12000f, q.DrawDistance);
            Assert.Equal(Look.Enhanced, q.DefaultLook);
            Assert.Equal((true, 256, true, 64, true, true), (q.SnapGrid, q.GridSize, q.SnapAngle, q.AngleStep, q.SnapFloor, q.GizmoLocal));
            Assert.Equal((ShadowQuality.Low, AntiAliasing.Msaa4, MenuSettings.LowLightBudget, 150), (q.Shadows, q.AntiAliasing, q.LightBudget, q.Ambient));
            Assert.Equal((false, false, ReliefLevel.High, ParticleLevel.Low), (q.AmbientOcclusion, q.Glow, q.Relief, q.Particles));

            // An arena without a camera.
            p.Place = new EditorPlace(GameKind.SkyNet, 300, Arena: true, "A");
            Assert.True(p.Save(path));
            Assert.Equal(p.Place, EditorPrefs.Load(path).Place);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void OldFileLoadsWithDefaults()
    {
        var path = TempPrefs();
        try
        {
            // A file from before these settings, with a key no version knows and broken new values.
            File.WriteAllText(path, """
                { "cameraSpeed": 900, "defaultLook": "enhanced", "snapGrid": true, "showLog": false, "someday": 1,
                  "recent": [5], "gizmo": "sideways", "placeTab": 7, "place": { "game": "skynet" } }
                """);
            var p = EditorPrefs.Load(path);
            var d = new EditorPrefs();
            Assert.Equal(900f, p.CameraSpeed);
            Assert.Equal(Look.Enhanced, p.DefaultLook);
            Assert.True(p.SnapGrid);
            Assert.False(p.ShowLog);
            Assert.Equal([5], p.Recent);
            Assert.Equal((d.ShowLinks, d.ShowMarkers, d.ShowLights, d.ShowSprites, d.ShowRobots, d.ShowEffects),
                (p.ShowLinks, p.ShowMarkers, p.ShowLights, p.ShowSprites, p.ShowRobots, p.ShowEffects));
            Assert.Equal(GizmoMode.Translate, p.Gizmo);
            Assert.Equal(PlaceKind.Robot, p.PlaceTab);
            Assert.Equal((null, null, false, ""), (p.AssetKind, p.AssetOrigin, p.AssetMapOnly, p.AssetSearch));
            Assert.Null(p.SaveMod);
            Assert.Null(p.Place);

            // ENHANCED OPTIONS: the game's defaults.
            var g = new MenuSettings();
            Assert.Equal((g.Shadows, g.AntiAliasing, g.LightBudget, g.Ambient, g.AmbientOcclusion, g.Glow, g.Relief, g.Particles),
                (p.Shadows, p.AntiAliasing, p.LightBudget, p.Ambient, p.AmbientOcclusion, p.Glow, p.Relief, p.Particles));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void WithSetsOnePreferenceByItsFileName()
    {
        var p = new EditorPrefs();
        Assert.False(p.With("showRobots", "false", out _)!.ShowRobots);
        Assert.Equal(Look.Enhanced, p.With("defaultLook", "enhanced", out _)!.DefaultLook);
        Assert.Equal(GizmoMode.Rotate, p.With("gizmo", "rotate", out _)!.Gizmo);
        Assert.Equal("a b", p.With("assetSearch", "a b", out _)!.AssetSearch);
        Assert.Equal(20000f, p.With("cameraSpeed", "1e9", out _)!.CameraSpeed);
        Assert.Equal("false", p.With("showRobots", "false", out _)!.Get("showRobots"));
        Assert.Null(p.With("noSuchThing", "1", out var error));
        Assert.Contains("noSuchThing", error);
        Assert.Null(p.Get("noSuchThing"));
        Assert.Equal(ParticleLevel.Off, p.With("particles", "off", out _)!.Particles);
        Assert.Equal(MenuSettings.MaxAmbient, p.With("ambient", "999", out _)!.Ambient);
        Assert.Equal(p.LightBudget, p.With("lightBudget", "100", out _)!.LightBudget);
    }

    [Fact]
    public void EnhancedOptionsReachTheLook()
    {
        var p = Changed();
        var s = new MenuSettings();
        p.ApplyTo(s);
        Assert.Equal((p.Shadows, p.AntiAliasing, p.LightBudget, p.Ambient, p.AmbientOcclusion, p.Glow, p.Relief, p.Particles),
            (s.Shadows, s.AntiAliasing, s.LightBudget, s.Ambient, s.AmbientOcclusion, s.Glow, s.Relief, s.Particles));
    }

    [Fact]
    public void ResetKeepsLayoutHistoryAndFolders()
    {
        var p = Changed();
        var r = p.Reset();
        var d = new EditorPrefs();
        Assert.Equal((p.Window, p.Place, p.DataDir, p.ModsDir), (r.Window, r.Place, r.DataDir, r.ModsDir));
        Assert.Equal(p.Mods, r.Mods);
        Assert.Equal(p.Recent, r.Recent);
        Assert.Equal((p.ShowOutliner, p.ShowAssets, p.ShowMission), (r.ShowOutliner, r.ShowAssets, r.ShowMission));
        Assert.Equal((d.CameraSpeed, d.DefaultLook, d.ShowRobots, d.Gizmo, d.SaveMod, d.AssetSearch, d.SnapGrid, d.UiScaleAuto),
            (r.CameraSpeed, r.DefaultLook, r.ShowRobots, r.Gizmo, r.SaveMod, r.AssetSearch, r.SnapGrid, r.UiScaleAuto));
        Assert.Equal((d.Shadows, d.AntiAliasing, d.Ambient, d.Particles), (r.Shadows, r.AntiAliasing, r.Ambient, r.Particles));
    }

    /// <summary>Hidden editor runs on one preferences file: the command line's look, mission and camera stay out
    /// of it; settings changed by a command are written and come back on the next start.</summary>
    [MapDataFact]
    public void CommandLineOverridesAreNotWrittenBack()
    {
        var path = TempPrefs();
        try
        {
            Assert.True(new EditorPrefs { ShowRobots = false }.Save(path));
            Run(path, "--look", "enhanced", "--mission", "2", "--pos", "10,20,30");
            var after = EditorPrefs.Load(path);
            Assert.Equal(Look.Original, after.DefaultLook);
            Assert.Null(after.Place);
            Assert.False(after.ShowRobots);

            // No overrides: a command's changes and where the editor was are kept.
            Run(path, "--command", "pref showRobots true;pref gizmo rotate;pref defaultLook enhanced");
            var kept = EditorPrefs.Load(path);
            Assert.True(kept.ShowRobots);
            Assert.Equal(GizmoMode.Rotate, kept.Gizmo);
            Assert.Equal(Look.Enhanced, kept.DefaultLook);
            Assert.Equal((GameKind.SkyNet, 1, false), (kept.Place!.Game, kept.Place.Number, kept.Place.Arena));
            Assert.NotNull(kept.Place.Eye);
        }
        finally
        {
            File.Delete(path);
            File.Delete(Path.ChangeExtension(path, ".imgui.ini"));
        }
    }

    private static void Run(string prefs, params string[] args)
    {
        var exe = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "SkyNetEdit.exe" : "SkyNetEdit");
        Assert.True(File.Exists(exe), exe + " is not built beside the tests");
        var info = new ProcessStartInfo(exe)
        {
            ArgumentList = { "--offscreen", "--prefs", prefs, "--data", EditDocumentTests.DataDir!, "--width", "640", "--height", "360", "--frames", "4" },
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        foreach (var a in args)
        {
            info.ArgumentList.Add(a);
        }

        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync();
        Assert.True(process.WaitForExit(80_000), "SkyNetEdit did not finish");
        Assert.Equal(0, process.ExitCode);
        _ = output;
    }

    [Fact]
    public void ViewDistanceStaysInItsRange()
    {
        // 0 or less: the map's own; else within 500..100000 world units.
        Assert.Equal(0f, EditorPrefs.ViewDistance(0f));
        Assert.Equal(0f, EditorPrefs.ViewDistance(-5f));
        Assert.Equal(EditorPrefs.MinDrawDistance, EditorPrefs.ViewDistance(10f));
        Assert.Equal(2500f, EditorPrefs.ViewDistance(2500f));
        Assert.Equal(EditorPrefs.MaxDrawDistance, EditorPrefs.ViewDistance(1e9f));

        // The default draws as before (the map's own); the file and the pref command keep the range.
        var p = new EditorPrefs();
        Assert.Equal(0f, p.DrawDistance);
        Assert.Equal(EditorPrefs.MinDrawDistance, p.With("drawDistance", "100", out _)!.DrawDistance);
        Assert.Equal(EditorPrefs.MaxDrawDistance, p.With("drawDistance", "5000000", out _)!.DrawDistance);
        Assert.Equal(1500f, p.With("drawDistance", "1500", out _)!.DrawDistance);
    }
}
