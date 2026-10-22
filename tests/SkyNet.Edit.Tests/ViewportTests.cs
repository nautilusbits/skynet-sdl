using System.Buffers.Binary;
using System.Diagnostics;
using System.Numerics;
using System.Text.RegularExpressions;
using RetroEng.Editor;

namespace SkyNet.Edit.Tests;

/// <summary>The 3D view as the "Viewport" window: a point of its picture mapped to the view's own coordinates
/// and rays, its rectangle cut from a captured frame, and the editor itself docking it by default.</summary>
public sealed partial class ViewportTests
{
    private const float Fov = 1.2f;

    // A view docked right of a 300-point panel and below a 40-point menu, on a 2x display.
    private static readonly ViewArea Docked = new(new Vector2(300f, 40f), new Vector2(800f, 400f), 2f);

    [Fact]
    public void A_point_of_the_picture_is_the_views_own()
    {
        Assert.Equal(Vector2.Zero, Docked.Local(new Vector2(300f, 40f)));
        Assert.Equal(new Vector2(400f, 200f), Docked.Local(new Vector2(700f, 240f)));
        Assert.True(Docked.Contains(new Vector2(300f, 40f)));
        Assert.False(Docked.Contains(new Vector2(299f, 100f)));
        Assert.False(Docked.Contains(new Vector2(1100f, 100f)));
        Assert.Equal(2f, Docked.Aspect);
    }

    [Fact]
    public void The_target_has_the_areas_pixels()
    {
        var pixels = Docked.Pixels;
        Assert.Equal((1600, 800), (pixels.Width, pixels.Height));

        // A collapsed window still makes a one-pixel target.
        var none = new ViewArea(Vector2.Zero, Vector2.Zero).Pixels;
        Assert.Equal((1, 1), (none.Width, none.Height));
    }

    /// <summary>The ray through the view's middle is the camera's forward whatever the window around it; the
    /// view's corner is the frustum's corner at the view's own aspect (not the window's).</summary>
    [Fact]
    public void Rays_go_through_the_view_not_the_window()
    {
        var view = Matrix4x4.CreateLookAt(Vector3.Zero, -Vector3.UnitZ, Vector3.UnitY);
        var middle = Docked.Ray(view, Fov, Docked.Size * 0.5f);
        Assert.True(Vector3.Distance(-Vector3.UnitZ, Vector3.Normalize(middle.Direction)) < 1e-4f);

        var corner = Vector3.Normalize(Docked.Ray(view, Fov, Vector2.Zero).Direction);
        var expected = Vector3.Normalize(PickRay.FromView(view, Fov, 2f, new Vector2(-1f, 1f)).Direction);
        Assert.True(Vector3.Distance(expected, corner) < 1e-4f);
        Assert.True(corner.X < 0f && corner.Y > 0f);
    }

    [Fact]
    public void A_shot_is_opaque_and_written_as_a_bmp()
    {
        // A 2 x 1 picture with no alpha.
        var cut = ViewShot.Opaque([1, 1, 7, 0, 2, 1, 7, 0]);
        Assert.Equal(new byte[] { 1, 1, 7, 255, 2, 1, 7, 255 }, cut);

        var path = Path.Combine(Path.GetTempPath(), $"viewshot-{Guid.NewGuid():N}.bmp");
        try
        {
            ViewShot.WriteBmp(path, 2, 1, cut);
            var bmp = File.ReadAllBytes(path);
            Assert.Equal((byte)'B', bmp[0]);
            Assert.Equal(bmp.Length, BinaryPrimitives.ReadInt32LittleEndian(bmp.AsSpan(2)));
            Assert.Equal(2, BinaryPrimitives.ReadInt32LittleEndian(bmp.AsSpan(18)));
            Assert.Equal(-1, BinaryPrimitives.ReadInt32LittleEndian(bmp.AsSpan(22)));
            Assert.Equal(new byte[] { 7, 1, 1, 255 }, bmp[54..58]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>The editor itself, hidden: the Viewport docked in the dock space's middle (the main window's
    /// width, under the menu), floated into a window of its own, then docked back by View > Reset layout; the
    /// screenshot is the view's picture only.</summary>
    [MapDataFact]
    public void The_editor_docks_floats_and_resets_the_viewport()
    {
        var docked = RunEditor(8);
        Assert.Equal(Width, docked.Width);
        Assert.InRange(docked.Height, Height / 2, Height - 1);

        var floated = RunEditor(14, "--float", "Viewport", "--reset-layout");
        Assert.Contains("--float: Viewport in a window of its own", floated.Log, StringComparison.Ordinal);
        Assert.Contains("Layout reset", floated.Log, StringComparison.Ordinal);
        Assert.Equal((docked.Width, docked.Height), (floated.Width, floated.Height));
    }

    /// <summary>A Viewport floated into a window of its own still gives its screenshot: the view target's picture.</summary>
    [MapDataFact]
    public void A_floated_viewport_gives_its_screenshot()
    {
        var floated = RunEditor(10, "--float", "Viewport");
        Assert.Contains("--float: Viewport in a window of its own", floated.Log, StringComparison.Ordinal);
        Assert.True(floated.Width > 1 && floated.Height > 1);
    }

    private const int Width = 640, Height = 360;

    // A hidden editor run with a screenshot on its last frame: the shot's size (from the log and the BMP) and the log.
    private static (int Width, int Height, string Log) RunEditor(int frames, params string[] extra)
    {
        var exe = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "SkyNetEdit.exe" : "SkyNetEdit");
        Assert.True(File.Exists(exe), exe + " is not built beside the tests");
        var prefs = Path.Combine(Path.GetTempPath(), $"SkyNetEdit-{Guid.NewGuid():N}.json");
        var shot = Path.ChangeExtension(prefs, ".bmp");
        var info = new ProcessStartInfo(exe) { RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var arg in new[] { "--mission", "1", "--prefs", prefs, "--data", EditDocumentTests.DataDir!, "--width", $"{Width}", "--height", $"{Height}",
            "--frames", $"{frames}", "--console-log", "--screenshot", shot }.Concat(extra))
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
            var log = output.Result;
            var said = ShotLine().Match(log);
            Assert.True(said.Success, "no view screenshot logged");
            var bmp = File.ReadAllBytes(shot);
            var (w, h) = (BinaryPrimitives.ReadInt32LittleEndian(bmp.AsSpan(18)), -BinaryPrimitives.ReadInt32LittleEndian(bmp.AsSpan(22)));
            Assert.Equal((int.Parse(said.Groups[1].Value), int.Parse(said.Groups[2].Value)), (w, h));
            return (w, h, log);
        }
        finally
        {
            File.Delete(prefs);
            File.Delete(Path.ChangeExtension(prefs, ".imgui.ini"));
            File.Delete(shot);
        }
    }

    [GeneratedRegex(@"\(the view, (\d+) x (\d+)\)")]
    private static partial Regex ShotLine();
}
