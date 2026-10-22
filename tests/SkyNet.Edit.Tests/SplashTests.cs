using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using SkyNet.Formats;
using SkyNet.Game;
using SkyNet.Game.Menu;

namespace SkyNet.Edit.Tests;

/// <summary>The start-up splash (shown or skipped, its statuses, its close), the versions and build date,
/// and Help > About's text.</summary>
public sealed partial class SplashTests
{
    // EditorOptions (the exe's top-level record) by reflection, as CaptureTests reads it.
    private static object Parse(params string[] args)
    {
        var type = typeof(EditorPrefs).Assembly.GetType("EditorOptions")!;
        object?[] call = [args, null];
        return type.GetMethod("Parse")!.Invoke(null, call) ?? throw new InvalidOperationException((string?)call[1]);
    }

    private static T Get<T>(object options, string name) => (T)options.GetType().GetProperty(name)!.GetValue(options)!;

    [Fact]
    public void The_options_and_the_preference_decide_the_splash()
    {
        var on = new EditorPrefs();
        var off = new EditorPrefs { ShowSplash = false };
        Assert.True(on.ShowSplash);

        Assert.True(SplashScreen.Shows(SplashRequest.Normal, on));
        Assert.False(SplashScreen.Shows(SplashRequest.Normal, off));
        Assert.False(SplashScreen.Shows(SplashRequest.Automated, on));
        Assert.True(SplashScreen.Shows(SplashRequest.Forced, off));

        foreach (var automated in new[] { new[] { "--offscreen" }, ["--capture", "frames"], ["--script", "wait 1"], ["--check-missions"], ["--cycle-missions", "1,2"] })
        {
            Assert.Equal(SplashRequest.Automated, Get<SplashRequest>(Parse(automated), "SplashRequest"));
        }

        Assert.Equal(SplashRequest.Forced, Get<SplashRequest>(Parse("--offscreen", "--splash"), "SplashRequest"));
        var shot = Parse("--splash", "s.bmp", "--offscreen");
        Assert.Equal(SplashRequest.Forced, Get<SplashRequest>(shot, "SplashRequest"));
        Assert.Equal("s.bmp", Get<string>(shot, "SplashShot"));
        if (Environment.GetEnvironmentVariable("SKYNET_OFFSCREEN") != "1")
        {
            Assert.Equal(SplashRequest.Normal, Get<SplashRequest>(Parse("--mission", "2"), "SplashRequest"));
        }
    }

    [Fact]
    public void The_preference_is_kept()
    {
        var path = Path.Combine(Path.GetTempPath(), $"SkyNetEdit-{Guid.NewGuid():N}.json");
        try
        {
            Assert.True(EditorPrefs.Load(path).ShowSplash);
            new EditorPrefs { ShowSplash = false }.Save(path);
            Assert.False(EditorPrefs.Load(path).ShowSplash);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void The_status_advances_one_frame_a_stage_and_stops_once_closed()
    {
        var presented = new List<string>();
        var closed = 0;
        var splash = new SplashScreen(presented.Add, () => closed++);
        Assert.True(splash.IsOpen);
        Assert.Null(splash.Status);

        string[] stages = [SplashScreen.Data, SplashScreen.Mission(1, GameKind.SkyNet), SplashScreen.Map, SplashScreen.Shaders];
        foreach (var stage in stages)
        {
            splash.Step(stage);
            Assert.Equal(stage, splash.Status);
        }

        Assert.Equal(stages, presented);
        Assert.Equal(stages, splash.Shown);
        Assert.Equal("Opening mission 1...", stages[1]);
        Assert.Equal("Opening Future Shock mission 3...", SplashScreen.Mission(3, GameKind.FutureShock));
        Assert.Equal("Opening arena MAP.300...", SplashScreen.Arena(300));

        // Ready: later stages (a look change re-warming the shaders) draw nothing.
        splash.Close();
        splash.Close();
        Assert.False(splash.IsOpen);
        splash.Step(SplashScreen.Shaders);
        Assert.Equal(stages.Length, presented.Count);
        Assert.Equal(1, closed);
    }

    [Fact]
    public void The_versions_and_build_date_are_the_builds()
    {
        var props = File.ReadAllText(Path.Combine(RepoRoot(), "Directory.Build.props"));
        Assert.Equal(Property(props, "Version"), EditorVersions.Editor);
        Assert.Equal(Property(props, "RetroEngVersion"), EditorVersions.Engine);
        Assert.Equal(BuildInfo.Version, EditorVersions.Editor);
        Assert.Equal(MainMenuScreen.VersionLabel, "v" + BuildInfo.Version);

        foreach (var date in new[] { EditorVersions.BuildDate, BuildInfo.BuildDate })
        {
            Assert.True(DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _), date);
        }

        Assert.All(new[] { BuildInfo.Version, BuildInfo.BuildDate, BuildInfo.Engine }, s => Assert.False(string.IsNullOrWhiteSpace(s)));
        Assert.Equal($"SkyNetEdit {EditorVersions.Editor} ({EditorVersions.BuildDate}) · RetroEng {EditorVersions.Engine}", EditorVersions.Line);
        Assert.Equal($"SkyNET {BuildInfo.Version} ({BuildInfo.BuildDate}) - RetroEng {Property(props, "RetroEngVersion")}", BuildInfo.Banner);
    }

    [Fact]
    public void About_opens_closes_and_copies_both_versions()
    {
        var about = new AboutBox();
        Assert.False(about.IsOpen);
        about.Show();
        Assert.True(about.IsOpen);
        about.Keep(false);
        Assert.False(about.IsOpen);
        about.Show();
        about.Close();
        Assert.False(about.IsOpen);

        var info = AboutBox.VersionInfo("Test GPU", "Vulkan");
        Assert.Contains($"SkyNetEdit {EditorVersions.Editor} ({EditorVersions.BuildDate})", info, StringComparison.Ordinal);
        Assert.Contains($"RetroEng {EditorVersions.Engine}", info, StringComparison.Ordinal);
        Assert.Contains(".NET", info, StringComparison.Ordinal);
        Assert.Contains("Test GPU on Vulkan", info, StringComparison.Ordinal);
        Assert.DoesNotContain("http", AboutBox.Notice + AboutBox.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_splash_art_covers_640_by_360_with_its_middle_kept()
    {
        // 320 x 200, each row's pixels its row number: doubled to 640 x 400, 20 rows cut top and bottom.
        var rgba = new byte[320 * 200 * 4];
        for (var i = 0; i < rgba.Length; i++)
        {
            rgba[i] = (byte)(i / (320 * 4));
        }

        var (width, height, pixels) = TitleArt.Cover((320, 200, rgba), TitleArt.SplashWidth, TitleArt.SplashHeight);
        Assert.Equal((640, 360, 640 * 360 * 4), (width, height, pixels.Length));
        Assert.Equal(10, pixels[0]);
        Assert.Equal(10, pixels[640 * 4 + 4]);
        Assert.Equal(189, pixels[(359 * 640 + 639) * 4]);

        Assert.Equal($"v{EditorVersions.Editor} ({EditorVersions.BuildDate}) - RetroEng {EditorVersions.Engine}", SplashScreen.Version(GameKind.SkyNet));
        Assert.EndsWith(" - Future Shock", SplashScreen.Version(GameKind.FutureShock), StringComparison.Ordinal);
    }

    [MapDataFact]
    public void The_title_art_comes_from_the_data()
    {
        var art = TitleArt.Load(GameData.Open(EditDocumentTests.DataDir!)!);
        Assert.NotNull(art);
        Assert.Equal((320, 200), (art.Value.Width, art.Value.Height));
        Assert.Equal(320 * 200 * 4, art.Value.Rgba.Length);
        Assert.Contains(art.Value.Rgba.Where((_, i) => i % 4 != 3), b => b != 0);
    }

    /// <summary>A hidden run with --splash steps through the stages and closes before the editor's frames;
    /// without it a hidden run shows none.</summary>
    [MapDataFact]
    public void A_forced_splash_runs_its_stages_and_closes_when_ready()
    {
        var shot = Path.Combine(Path.GetTempPath(), $"SkyNetEdit-{Guid.NewGuid():N}-splash.bmp");
        try
        {
            var log = RunEditor("--splash", shot);
            var lines = SplashLine().Matches(log).Select(m => m.Groups[1].Value).ToList();
            Assert.Equal([SplashScreen.Data, SplashScreen.Renderer, "Opening mission 1...", SplashScreen.Map, SplashScreen.Shaders], lines.Take(5));
            Assert.StartsWith("closed after", lines[^1], StringComparison.Ordinal);
            Assert.True(File.Exists(shot), "no splash shot");

            Assert.DoesNotContain("Splash:", RunEditor(), StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(shot);
        }
    }

    [GeneratedRegex(@"Splash: (.+?)\r?$", RegexOptions.Multiline)]
    private static partial Regex SplashLine();

    private static string RunEditor(params string[] extra)
    {
        var exe = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "SkyNetEdit.exe" : "SkyNetEdit");
        Assert.True(File.Exists(exe), exe + " is not built beside the tests");
        var prefs = Path.Combine(Path.GetTempPath(), $"SkyNetEdit-{Guid.NewGuid():N}.json");
        var info = new ProcessStartInfo(exe) { RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var arg in new[] { "--mission", "1", "--prefs", prefs, "--data", EditDocumentTests.DataDir!, "--width", "640", "--height", "360",
            "--frames", "2", "--console-log", "--offscreen" }.Concat(extra))
        {
            info.ArgumentList.Add(arg);
        }

        try
        {
            using var process = Process.Start(info)!;
            var output = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(80_000))
            {
                process.Kill(entireProcessTree: true);
                Assert.Fail("SkyNetEdit did not finish");
            }

            Assert.Equal(0, process.ExitCode);
            return output.Result;
        }
        finally
        {
            File.Delete(prefs);
            File.Delete(Path.ChangeExtension(prefs, ".imgui.ini"));
        }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "SkyNet.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("no SkyNet.slnx above the tests");
    }

    private static string Property(string props, string name) =>
        Regex.Match(props, $"<{name}>([^<]+)</{name}>").Groups[1].Value;
}
