using System.Buffers.Binary;
using System.Diagnostics;

namespace SkyNet.Edit.Tests;

/// <summary>The asset viewers' 3D (the Model Viewer, the Effects window) in view targets of their own: they dock,
/// float off the main window, and their picture shows the subject wherever the window is.</summary>
public sealed class ViewerTargetTests
{
    private const int Width = 1280, Height = 720;
    private const string OpenModel = "asset.open model 232LIFT";

    /// <summary>The Model Viewer docked at the dock space's left (the Outliner closed, so nothing floats over it):
    /// the dock holds, and the window's frame shows the model over the viewer's backdrop (before view targets the
    /// viewer could not dock, and its inset lay under any panel behind it).</summary>
    [MapDataFact]
    public void A_docked_model_viewer_shows_its_model()
    {
        var (log, shot) = RunEditor(OpenModel, "--look", "original", "--dock", "###ModelViewer");
        Assert.Contains("--dock: ###ModelViewer docked", log, StringComparison.Ordinal);
        Assert.True(ModelPixels(shot) > MinModelPixels, "no model drawn over the viewer's backdrop");
    }

    /// <summary>The Model Viewer floated into a window of its own (another monitor too).</summary>
    [MapDataFact]
    public void The_model_viewer_floats_off_the_main_window()
    {
        var (log, _) = RunEditor(OpenModel, "--look", "enhanced", "--float", "###ModelViewer");
        Assert.Contains("--float: ###ModelViewer in a window of its own", log, StringComparison.Ordinal);
    }

    /// <summary>The Effects window floated into a window of its own.</summary>
    [MapDataFact]
    public void The_effects_window_floats_off_the_main_window()
    {
        var (log, _) = RunEditor("effect.open fire", "--look", "enhanced", "--float", "###Effects");
        Assert.Contains("--float: ###Effects in a window of its own", log, StringComparison.Ordinal);
    }

    // The viewer's backdrop as the window shows it (ModelViewer.Backdrop, 0.16, 0.17, 0.19), and the least of its
    // pixels and of the model's over it: a lift's frame at a few hundred pixels across.
    private static readonly (int B, int G, int R) Backdrop = (48, 43, 41);
    private const int MinBackdropPixels = 20_000, MinModelPixels = 2000, Tolerance = 3, ModelContrast = 40;

    // Pixels inside the backdrop's box that differ from it clearly (none without a backdrop shown).
    private static int ModelPixels((int Width, int Height, byte[] Bgra) shot)
    {
        var (w, h, px) = shot;
        var (minX, minY, maxX, maxY, backdrop) = (w, h, -1, -1, 0);
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                if (Distance(px, (y * w + x) * 4) > Tolerance)
                {
                    continue;
                }

                (minX, minY, maxX, maxY) = (Math.Min(minX, x), Math.Min(minY, y), Math.Max(maxX, x), Math.Max(maxY, y));
                backdrop++;
            }
        }

        Assert.True(backdrop > MinBackdropPixels, $"the viewer's backdrop is not shown ({backdrop} pixels)");
        var count = 0;
        for (var y = minY; y <= maxY; y++)
        {
            for (var x = minX; x <= maxX; x++)
            {
                count += Distance(px, (y * w + x) * 4) > ModelContrast ? 1 : 0;
            }
        }

        return count;
    }

    private static int Distance(byte[] px, int i) =>
        Math.Abs(px[i] - Backdrop.B) + Math.Abs(px[i + 1] - Backdrop.G) + Math.Abs(px[i + 2] - Backdrop.R);

    // A hidden editor run of mission 1 running <paramref name="command"/>, the Outliner closed (it floats over the
    // dock space's left): its log and the window's last frame.
    private static (string Log, (int Width, int Height, byte[] Bgra) Shot) RunEditor(string command, params string[] extra)
    {
        var exe = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "SkyNetEdit.exe" : "SkyNetEdit");
        Assert.True(File.Exists(exe), exe + " is not built beside the tests");
        var prefs = Path.Combine(Path.GetTempPath(), $"SkyNetEdit-{Guid.NewGuid():N}.json");
        var shot = Path.ChangeExtension(prefs, ".bmp");
        File.WriteAllText(prefs, """{ "showOutliner": false }""");
        var info = new ProcessStartInfo(exe) { RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var arg in new[] { "--mission", "1", "--prefs", prefs, "--data", EditDocumentTests.DataDir!, "--width", $"{Width}", "--height", $"{Height}",
            "--frames", "14", "--console-log", "--command", command, "--window-screenshot", shot }.Concat(extra))
        {
            info.ArgumentList.Add(arg);
        }

        info.Environment["SKYNET_OFFSCREEN"] = "1";
        try
        {
            using var process = Process.Start(info)!;
            var output = process.StandardOutput.ReadToEndAsync();
            Assert.True(process.WaitForExit(80_000), "SkyNetEdit did not finish");
            var log = output.Result;
            Assert.True(process.ExitCode == 0, "SkyNetEdit exited " + process.ExitCode + ":\n" + log);
            var bmp = File.ReadAllBytes(shot);
            var (w, h) = (BinaryPrimitives.ReadInt32LittleEndian(bmp.AsSpan(18)), -BinaryPrimitives.ReadInt32LittleEndian(bmp.AsSpan(22)));
            var at = BinaryPrimitives.ReadInt32LittleEndian(bmp.AsSpan(10));
            return (log, (w, h, bmp[at..(at + w * h * 4)]));
        }
        finally
        {
            File.Delete(prefs);
            File.Delete(Path.ChangeExtension(prefs, ".imgui.ini"));
            File.Delete(shot);
        }
    }
}
