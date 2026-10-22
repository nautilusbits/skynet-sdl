using System.Diagnostics;
using SkyNet.Game.Menu;

namespace SkyNet.Edit.Tests;

/// <summary>The editor's preferences file (Settings window) and the File menu's mission switch.</summary>
public sealed class EditorMenuTests
{
    [Fact]
    public void PreferencesRoundTrip()
    {
        var path = Path.Combine(Path.GetTempPath(), $"SkyNetEdit-{Guid.NewGuid():N}.json");
        try
        {
            var p = new EditorPrefs
            {
                CameraSpeed = 2750f, MouseSensitivity = 1.5f, InvertY = true, FieldOfView = 85f, DefaultLook = Look.Enhanced,
                DataDir = @"D:\data", ModsDir = @"D:\mods", UiScale = 1.25f, FontSize = 16f, Mods = ["alpha", "beta"],
                ShowOutliner = false, ShowProperties = true, ShowLog = false,
            };
            p.Opened(3);
            p.Opened(1);
            p.Opened(3);
            Assert.True(p.Save(path));

            var q = EditorPrefs.Load(path);
            Assert.Equal(p.CameraSpeed, q.CameraSpeed);
            Assert.Equal(p.MouseSensitivity, q.MouseSensitivity);
            Assert.True(q.InvertY);
            Assert.Equal(85f, q.FieldOfView);
            Assert.Equal(Look.Enhanced, q.DefaultLook);
            Assert.Equal(@"D:\data", q.DataDir);
            Assert.Equal(@"D:\mods", q.ModsDir);
            Assert.Equal(1.25f, q.UiScale);
            Assert.Equal(16f, q.FontSize);
            Assert.Equal(["alpha", "beta"], q.Mods);
            Assert.Equal([3, 1], q.Recent);
            Assert.False(q.ShowOutliner);
            Assert.False(q.ShowLog);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void WindowStateRoundTrip()
    {
        var path = Path.Combine(Path.GetTempPath(), $"SkyNetEdit-{Guid.NewGuid():N}.json");
        try
        {
            var none = new EditorPrefs();
            Assert.True(none.Save(path));
            Assert.Null(EditorPrefs.Load(path).Window);

            var window = new RetroEng.Editor.WindowState(-1700, 120, 1440, 810, Maximized: true, Fullscreen: false, Display: "DELL U2720Q");
            Assert.True(new EditorPrefs { Window = window }.Save(path));
            Assert.Equal(window, EditorPrefs.Load(path).Window);

            var full = window with { Maximized = false, Fullscreen = true, Display = null };
            Assert.True(new EditorPrefs { Window = full }.Save(path));
            Assert.Equal(full, EditorPrefs.Load(path).Window);

            // A broken window object is dropped; the rest of the file still reads.
            File.WriteAllText(path, """{ "cameraSpeed": 900, "window": { "x": 10, "y": 10, "width": 0, "height": 600 } }""");
            var broken = EditorPrefs.Load(path);
            Assert.Null(broken.Window);
            Assert.Equal(900f, broken.CameraSpeed);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void FirstStartTakesTheSystemScale()
    {
        var path = Path.Combine(Path.GetTempPath(), $"SkyNetEdit-{Guid.NewGuid():N}.json");
        try
        {
            // No file, then a file without a uiScale: the first start, automatic.
            var p = EditorPrefs.Load(path);
            Assert.True(p.UiScaleAuto);
            Assert.True(p.FollowSystemScale(2f));
            Assert.Equal((2f, EditorPrefs.DefaultFontSize), (p.UiScale, p.FontSize));
            Assert.False(p.FollowSystemScale(2f));
            Assert.True(p.FollowSystemScale(1.5f));
            Assert.Equal(1.5f, p.UiScale);
            Assert.True(p.FollowSystemScale(9f));
            Assert.Equal(EditorPrefs.MaxUiScale, p.UiScale);
            File.WriteAllText(path, """{ "cameraSpeed": 900 }""");
            Assert.True(EditorPrefs.Load(path).UiScaleAuto);

            // Kept automatic: it still follows after a restart.
            Assert.True(p.FollowSystemScale(1.25f));
            Assert.True(p.Save(path));
            var q = EditorPrefs.Load(path);
            Assert.True(q.UiScaleAuto);
            Assert.Equal(1.25f, q.UiScale);
            Assert.True(q.FollowSystemScale(1.75f));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ManualScaleSurvivesUntilResetToSystem()
    {
        var path = Path.Combine(Path.GetTempPath(), $"SkyNetEdit-{Guid.NewGuid():N}.json");
        try
        {
            var p = new EditorPrefs { UiScale = 1.1f, FontSize = 18f, UiScaleAuto = false };
            Assert.True(p.Save(path));
            var q = EditorPrefs.Load(path);
            Assert.False(q.UiScaleAuto);
            Assert.False(q.FollowSystemScale(2f));
            Assert.Equal((1.1f, 18f), (q.UiScale, q.FontSize));

            // A file from before the flag with a scale kept: the user's.
            File.WriteAllText(path, """{ "uiScale": 1.4, "fontSize": 15 }""");
            var old = EditorPrefs.Load(path);
            Assert.False(old.UiScaleAuto);
            Assert.False(old.FollowSystemScale(2f));
            Assert.Equal(1.4f, old.UiScale);

            // Reset to system re-reads the scale and follows again.
            q.ResetToSystemScale(2f);
            Assert.True(q.UiScaleAuto);
            Assert.Equal((2f, EditorPrefs.DefaultFontSize), (q.UiScale, q.FontSize));
            Assert.True(q.FollowSystemScale(1f));
            Assert.Equal(1f, q.UiScale);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void MissingOrBrokenPreferencesGiveDefaults()
    {
        var path = Path.Combine(Path.GetTempPath(), $"SkyNetEdit-{Guid.NewGuid():N}.json");
        Assert.Equal(1500f, EditorPrefs.Load(path).CameraSpeed);
        try
        {
            File.WriteAllText(path, "{ \"cameraSpeed\": 1e9, \"fieldOfView\": \"wide\", \"recent\": [1, 1, 2] ");
            Assert.Equal(1500f, EditorPrefs.Load(path).CameraSpeed);
            File.WriteAllText(path, "{ \"cameraSpeed\": 1e9, \"fieldOfView\": \"wide\", \"recent\": [1, 1, 2] }");
            var p = EditorPrefs.Load(path);
            Assert.Equal(20000f, p.CameraSpeed);
            Assert.Equal(70f, p.FieldOfView);
            Assert.Equal([1, 2], p.Recent);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>The File menu's switch, twice over (1, 2, 1, 2) in a hidden window: no error logged and
    /// the same textures and meshes live each time a mission comes back.</summary>
    [MapDataFact]
    public void SwitchingMissionsLeaksNothing()
    {
        var exe = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "SkyNetEdit.exe" : "SkyNetEdit");
        Assert.True(File.Exists(exe), exe + " is not built beside the tests");
        var prefs = Path.Combine(Path.GetTempPath(), $"SkyNetEdit-{Guid.NewGuid():N}.json");
        var info = new ProcessStartInfo(exe)
        {
            ArgumentList = { "--cycle-missions", "1,2,1,2", "--prefs", prefs, "--data", EditDocumentTests.DataDir!, "--width", "640", "--height", "360" },
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        info.Environment["SKYNET_OFFSCREEN"] = "1";
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync();
        Assert.True(process.WaitForExit(80_000), "SkyNetEdit --cycle-missions did not finish");
        Assert.Equal(0, process.ExitCode);
        Assert.False(File.Exists(prefs), "a batch run wrote the preferences");
        _ = output;
    }
}
