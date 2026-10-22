using System.Diagnostics;
using System.Globalization;

namespace SkyNet.Game.DevTools;

/// <summary>--capture: a video's frames made by the program itself. Each frame is one fixed step of
/// 1/<see cref="Fps"/> s whatever the wall clock says (<see cref="Timestamp"/> drives the loop), and
/// the presented window is saved as the next numbered BMP (frame_000000.bmp, ...): ffmpeg -framerate
/// 60 -i frame_%06d.bmp makes the video. Without a folder (--fixed-step) only the clock is kept:
/// a scripted run (a shot mid-fire after `hold Fire; wait 3`) is the same every time, nothing saved.</summary>
public sealed class FrameCapture
{
    public const int DefaultFps = 60;
    public const int MinFps = 10;
    public const int MaxFps = 240;

    /// <param name="dir">The frames' folder; null: none saved, the fixed step only.</param>
    public FrameCapture(string? dir, int fps = DefaultFps)
    {
        Fps = fps is >= MinFps and <= MaxFps ? fps : throw new ArgumentOutOfRangeException(nameof(fps));
        if (dir != null)
        {
            Dir = Path.GetFullPath(dir);
            Directory.CreateDirectory(Dir);
        }
    }

    /// <summary>The frames' folder; null: none saved.</summary>
    public string? Dir { get; }

    public int Fps { get; }

    /// <summary>The frames taken so far: the next frame's number.</summary>
    public int Taken { get; private set; }

    /// <summary>One frame's length (seconds).</summary>
    public double Step => 1.0 / Fps;

    /// <summary>The driven clock: the frame being made, in <see cref="Stopwatch"/> ticks.</summary>
    public long Timestamp => Taken * Stopwatch.Frequency / Fps;

    /// <summary>The driven clock in milliseconds (the animation clock).</summary>
    public long Milliseconds => Taken * 1000L / Fps;

    /// <summary>The driven clock in seconds.</summary>
    public float Seconds => (float)(Taken * Step);

    /// <summary>The file frame <paramref name="frame"/> is saved to.</summary>
    public string PathOf(int frame) => Path.Combine(Dir ?? "", string.Create(CultureInfo.InvariantCulture, $"frame_{frame:D6}.bmp"));

    /// <summary>The file this frame goes to (null: none saved); the clock moves on one step.</summary>
    public string? Take()
    {
        var frame = Taken++;
        return Dir != null ? PathOf(frame) : null;
    }

    /// <summary>A frame that also had a screenshot of its own was saved once, there: the capture gets a copy.</summary>
    public static void Mirror(string? shot, string? captured)
    {
        if (shot == null || captured == null || !File.Exists(shot))
        {
            return;
        }

        File.Copy(shot, captured, overwrite: true);
    }

    /// <summary>--capture-fps's value.</summary>
    public static int ParseFps(string? value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var fps) && fps is >= MinFps and <= MaxFps
            ? fps
            : throw new FormatException($"--capture-fps takes {MinFps} to {MaxFps}");

    /// <summary>--capture-frames's value.</summary>
    public static int ParseFrames(string? value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var frames) && frames > 0
            ? frames
            : throw new FormatException("--capture-frames takes a positive number");
}
