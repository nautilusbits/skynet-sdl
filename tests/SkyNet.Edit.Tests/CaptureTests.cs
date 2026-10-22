using System.Buffers.Binary;
using System.Diagnostics;

namespace SkyNet.Edit.Tests;

/// <summary>SkyNetEdit's --capture: the switches parsed, and a hidden run writing one window-sized BMP a frame.</summary>
public sealed class CaptureTests
{
    // EditorOptions.Parse (the exe's top-level record) by reflection: the options, or null and the error.
    private static (object? Options, string? Error) Parse(params string[] args)
    {
        var type = typeof(EditorPrefs).Assembly.GetType("EditorOptions")!;
        object?[] call = [args, null];
        var options = type.GetMethod("Parse")!.Invoke(null, call);
        return (options, (string?)call[1]);
    }

    private static T Field<T>(object options, string name) => (T)options.GetType().GetProperty(name)!.GetValue(options)!;

    [Fact]
    public void The_capture_switches_parse()
    {
        var (o, error) = Parse("--capture", "frames", "--capture-fps", "30", "--capture-frames", "5");
        Assert.Null(error);
        Assert.Equal("frames", Field<string>(o!, "Capture"));
        Assert.Equal(30, Field<int>(o!, "CaptureFps"));
        Assert.Equal(5, Field<int>(o!, "Frames"));

        Assert.Equal(60, Field<int>(Parse("--capture", "frames").Options!, "CaptureFps"));
        Assert.Null(Parse("--capture-fps", "5").Options);
        Assert.Null(Parse("--capture-frames", "0").Options);
    }

    /// <summary>Five frames, five files, each the whole window, and the log's count.</summary>
    [MapDataFact]
    public void A_hidden_run_captures_each_frame()
    {
        const int Width = 320, Height = 180, Frames = 5;
        var exe = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "SkyNetEdit.exe" : "SkyNetEdit");
        Assert.True(File.Exists(exe), exe + " is not built beside the tests");
        var prefs = Path.Combine(Path.GetTempPath(), $"SkyNetEdit-{Guid.NewGuid():N}.json");
        var dir = Path.ChangeExtension(prefs, null) + "-capture";
        var info = new ProcessStartInfo(exe) { RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var arg in new[] { "--mission", "1", "--prefs", prefs, "--data", EditDocumentTests.DataDir!, "--width", $"{Width}", "--height", $"{Height}",
            "--console-log", "--capture", dir, "--capture-frames", $"{Frames}" })
        {
            info.ArgumentList.Add(arg);
        }

        info.Environment["SKYNET_OFFSCREEN"] = "1";
        try
        {
            using var process = Process.Start(info)!;
            var output = process.StandardOutput.ReadToEndAsync();
            Assert.True(process.WaitForExit(80_000), "SkyNetEdit did not finish");
            Assert.Equal(0, process.ExitCode);
            Assert.Contains($"Capture: {Frames} frames at 60 fps", output.Result, StringComparison.Ordinal);

            var files = Directory.GetFiles(dir).Select(Path.GetFileName).Order().ToArray();
            Assert.Equal(Enumerable.Range(0, Frames).Select(i => $"frame_{i:D6}.bmp"), files);
            foreach (var file in files)
            {
                var bmp = File.ReadAllBytes(Path.Combine(dir, file!));
                Assert.Equal((Width, Height), (BinaryPrimitives.ReadInt32LittleEndian(bmp.AsSpan(18)), Math.Abs(BinaryPrimitives.ReadInt32LittleEndian(bmp.AsSpan(22)))));
            }
        }
        finally
        {
            File.Delete(prefs);
            File.Delete(Path.ChangeExtension(prefs, ".imgui.ini"));
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }
}
