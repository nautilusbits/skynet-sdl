using RetroEng.Diagnostics;
using RetroEng.Platform;
using SkyNet.Formats;
using SkyNet.Game;
using SkyNet.Game.Menu;

// The start-up splash: a small window of its own (the engine's SplashWindow: the title art, the name, the
// versions, one status line) while the game window loads hidden; the window is shown after its first frames
// and the splash fades out after it:
//
//   Run: splash ─► window (Deferred) ─► data, bake, level, shaders (Status) ─► loop: frames dropped ─► Show ─► present ─► EndSplash
//
// None offscreen (a hidden window) or on Android (a full-screen window from the start).
internal sealed partial class AppHost
{
    private SplashWindow? splash;
    private bool revealed;
    // Frames presented while hidden (dropped) before the window shows.
    private const int DroppedFrames = 8;
    private int hiddenFrames;

    /// <summary>Whether the window opens hidden behind a splash: a shown window off Android, not --capture (its frames count).</summary>
    private bool UsesSplash => !options.Offscreen && capture == null && !OperatingSystem.IsAndroid();

    /// <summary>The splash up (when <see cref="UsesSplash"/>) with its first status.</summary>
    private void StartSplash(GameData d, string status)
    {
        if (!UsesSplash)
        {
            return;
        }

        splash = new SplashWindow(TitleArt.Splash(d), "SkyNET", $"v{BuildInfo.Version} ({BuildInfo.BuildDate}) - RetroEng {BuildInfo.Engine}");
        SplashStatus(status);
    }

    /// <summary>A loading stage's line on the splash (nothing once it is gone).</summary>
    private void SplashStatus(string status)
    {
        if (splash == null)
        {
            return;
        }

        splash.Status = status;
        Log.Info("App", "Splash: " + status);
    }

    /// <summary>Main, after each present: the hidden window shown after <see cref="DroppedFrames"/> frames
    /// (dropped: showing it before the first, slow present left it blank white), the splash gone after the next.</summary>
    private void Presented()
    {
        if (!UsesSplash || splash == null && revealed)
        {
            return;
        }

        if (revealed)
        {
            EndSplash();
            return;
        }

        if (++hiddenFrames >= DroppedFrames)
        {
            revealed = true;
            window.Show();
        }
    }

    /// <summary>The splash fades out (and goes before a fatal message box).</summary>
    private void EndSplash()
    {
        if (splash == null)
        {
            return;
        }

        splash.Dispose();
        splash = null;
        Log.Info("App", "Splash: closed");
    }
}
