using System.Diagnostics;
using System.Drawing;
using System.Numerics;
using RetroEng.Diagnostics;
using RetroEng.Render;
using SkyNet.Game;
using SkyNet.Game.Menu;

// Loading frames: a load that blocks the loop's thread draws, between its steps, the title art with a
// status line and a bar in the game window (the splash takes the line while it is up). The load's
// thread presents them itself (split loop: the frame build is drained, main shows the packet).
//
//   load ─► step ─► LoadProgress.Step ─► (100 ms on) DrawLoading ─► Present ─► step ...
internal sealed partial class AppHost
{
    /// <summary>A level load slower than this so far shows its next stages (a quick one never flashes).</summary>
    private const double LongLoadMs = 250.0;

    // The band, the line and the bar on the DOS 320 x 200 screen.
    private static readonly Rectangle LoadingBand = new(0, 166, 320, 34);
    private static readonly Rectangle LoadingLine = new(8, 168, 304, 14);
    private static readonly Rectangle LoadingBar = new(16, 186, 288, 6);
    private static readonly Vector4 LoadingShade = new(0f, 0f, 0f, 0.75f);

    // The loading frames drawn since the last log line and their cost.
    private int loadingFrames;
    private long loadingTicks;

    /// <summary>A long load's count shown as loading frames.</summary>
    private LoadProgress Loading(string label) => new(label, p => DrawLoading(p.Text, p.Fraction));

    /// <summary>A level load's stage: on the splash, or a loading frame once the load is slow.</summary>
    private void LoadingStage(string status, float fraction, double elapsedMs)
    {
        if (splash != null)
        {
            SplashStatus(status);
            return;
        }

        if (elapsedMs < LongLoadMs)
        {
            return;
        }

        DrawLoading(status.ToUpperInvariant(), fraction);
    }

    /// <summary>One loading frame: the title, <paramref name="text"/> and the bar at <paramref name="fraction"/>.</summary>
    private void DrawLoading(string text, float fraction)
    {
        if (splash != null)
        {
            splash.Status = text;
            return;
        }

        var started = Stopwatch.GetTimestamp();
        // One thread: nobody else answers the window meanwhile.
        if (frameBuild == null)
        {
            window.PumpEvents(input);
        }

        menuPainter.Use(null);
        menuPainter.FillWindow(MenuPainter.Black);
        menuPainter.Image(MenuLayout.TitleImage, MenuLayout.TitlePalette, 0, 0);
        menuPainter.Fill(LoadingBand, LoadingShade);
        menuPainter.TextIn(MenuLayout.NetFont, text, LoadingLine, MenuPainter.TextGreen);
        menuPainter.Outline(LoadingBar, MenuPainter.TextGreen);
        menuPainter.Fill(LoadingBar with { Width = (int)(LoadingBar.Width * Math.Clamp(fraction, 0f, 1f)) }, MenuPainter.TextGreen);
        renderer.Present(new View(Matrix4x4.Identity, Matrix4x4.Identity, Vector3.Zero), new Vector4(0f, 0f, 0f, 1f));
        loadingFrames++;
        loadingTicks += Stopwatch.GetTimestamp() - started;
    }

    /// <summary>The loading frames of <paramref name="load"/> and their cost into the log.</summary>
    private void LogLoading(string load)
    {
        if (loadingFrames == 0)
        {
            return;
        }

        Log.Info("App", $"{load}: {loadingFrames} loading frames, {Stopwatch.GetElapsedTime(0, loadingTicks).TotalMilliseconds:0} ms");
        (loadingFrames, loadingTicks) = (0, 0);
    }
}
