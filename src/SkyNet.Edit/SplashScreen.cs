using System.Diagnostics;
using System.Globalization;
using RetroEng.Diagnostics;
using SkyNet.Formats;

namespace SkyNet.Edit;

/// <summary>How the run asked for the splash: a plain start, an automated run (--offscreen, --capture,
/// --script, the batch checks) that skips it, or --splash forcing it.</summary>
public enum SplashRequest
{
    Normal,
    Automated,
    Forced,
}

/// <summary>
/// The start-up splash's stages: a small window of its own (the engine's SplashWindow: title art, name,
/// versions, one status line) while the main window loads hidden. Each stage redraws the status (no threads);
/// Close comes once the main window is shown with its first frame:
/// <code>
///   Program ─Step(Data)─► ctor ─Step(Renderer)─► Open ─Step(Mission)─► Show ─Step(Map)─► ApplyLook
///     ─Step(Shaders)─► first frame: window shown + presented ─► Close (the splash fades out)
/// </code>
/// </summary>
public sealed class SplashScreen
{
    public const string Data = "Loading data...";
    public const string Renderer = "Starting the renderer...";
    public const string Map = "Building map...";
    public const string Shaders = "Compiling shaders...";

    private readonly Action<string> _present;
    private readonly Action? _close;
    private readonly List<string> _shown = [];
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    /// <summary><paramref name="present"/> shows a status; <paramref name="close"/> takes the splash down.</summary>
    public SplashScreen(Action<string> present, Action? close = null) => (_present, _close) = (present, close);

    /// <summary>Whether the splash shows: forced always, automated never, else as the preference says.</summary>
    public static bool Shows(SplashRequest request, EditorPrefs prefs) => request switch
    {
        SplashRequest.Forced => true,
        SplashRequest.Automated => false,
        _ => prefs.ShowSplash,
    };

    /// <summary>The splash's name line.</summary>
    public const string Title = "SkyNetEdit";

    /// <summary>The splash's version line: "v0.38.0 (2026-10-19) - RetroEng 0.29.0", Future Shock's data named.</summary>
    public static string Version(GameKind game) =>
        $"v{EditorVersions.Editor} ({EditorVersions.BuildDate}) - RetroEng {EditorVersions.Engine}{(game == GameKind.FutureShock ? " - Future Shock" : "")}";

    /// <summary>--splash file.bmp: the picture the splash window gets (its text is the engine's, drawn on top).</summary>
    public static void SaveShot(string path, (int Width, int Height, byte[] Rgba)? art)
    {
        if (art is not { } picture)
        {
            Log.Warn("Edit", "Splash: no title art in the data, no picture saved");
            return;
        }

        ViewShot.WriteBmp(path, picture.Width, picture.Height, picture.Rgba);
        Log.Info("Edit", $"Splash: picture saved to {path}");
    }

    /// <summary>"Opening mission 3..." (Future Shock's named).</summary>
    public static string Mission(int number, GameKind game) =>
        string.Create(CultureInfo.InvariantCulture, $"Opening {(game == GameKind.FutureShock ? "Future Shock " : "")}mission {number}...");

    /// <summary>"Opening arena MAP.300...".</summary>
    public static string Arena(int map) => string.Create(CultureInfo.InvariantCulture, $"Opening arena MAP.{map:D3}...");

    /// <summary>The status line now; null before the first step.</summary>
    public string? Status { get; private set; }

    /// <summary>The statuses shown, in order.</summary>
    public IReadOnlyList<string> Shown => _shown;

    /// <summary>False once the editor is ready.</summary>
    public bool IsOpen { get; private set; } = true;

    /// <summary>A stage begins: its status presented (nothing once closed).</summary>
    public void Step(string status)
    {
        if (!IsOpen)
        {
            return;
        }

        Status = status;
        _shown.Add(status);
        Log.Info("Edit", "Splash: " + status);
        _present(status);
    }

    /// <summary>The editor's first frame is on screen: the splash goes.</summary>
    public void Close()
    {
        if (!IsOpen)
        {
            return;
        }

        IsOpen = false;
        _close?.Invoke();
        Log.Info("Edit", string.Create(CultureInfo.InvariantCulture, $"Splash: closed after {_clock.Elapsed.TotalMilliseconds:0} ms"));
    }
}
