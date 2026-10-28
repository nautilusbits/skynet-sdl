using System.Globalization;
using System.Numerics;

namespace SkyNet.Game.DevTools;

/// <summary>One rendered frame for <see cref="PerfLog"/>: its length, the simulation ticks run before
/// it and their time, the frame build's time and the renderer's counts.</summary>
public readonly record struct PerfFrame(double FrameMs, int Ticks, double TickMs, double BuildMs, int DrawCalls, int Triangles, float GpuWaitMs);

/// <summary>
/// A line for the log every <see cref="Period"/> seconds of play: the FPS, the slow frames, the ticks a
/// frame (a frame that falls behind runs more ticks, which makes it slower still), where the time
/// went and where the soldier was. A phone's slowdown can be read from its log afterwards.
/// <code>
///   perf.Frame(new PerfFrame(41.2, 2, 3.1, 30.5, 210, 52000, 12f), seconds, feet)   // null, then a line
///   "24.3 fps (mean 41 ms, worst 120 ms, 6 over 66 ms); 2.4 ticks a frame (3.1 ms each); build 30.5 ms;
///    210 draw calls, 52k triangles, GPU wait 12.0 ms; 3 GCs; at 20316,752,-38618, moved 1200 u"
/// </code>
/// </summary>
public sealed class PerfLog
{
    /// <summary>Seconds between the lines.</summary>
    public const double Period = 5.0;

    /// <summary>A frame longer than this is counted as slow (under 15 FPS).</summary>
    public const double SlowMs = 66.0;

    private double _start = double.NaN;
    private int _frames, _slow, _ticks;
    private double _frameMs, _worst, _tickMs, _buildMs, _gpuWait;
    private long _draws, _triangles;
    private float _moved;
    private Vector3 _last;
    private int _gcAt;

    /// <summary>Starts over (a map was entered): the next line covers frames from here on.</summary>
    public void Clear() => _start = double.NaN;

    /// <summary>A frame ended at <paramref name="seconds"/> with the soldier's feet at
    /// <paramref name="feet"/>; the line when a period is over, else null.</summary>
    public string? Frame(in PerfFrame frame, double seconds, Vector3 feet) =>
        Frame(frame, seconds, feet, GC.CollectionCount(0));

    /// <summary><see cref="Frame(in PerfFrame, double, Vector3)"/> with the gen0 collections made so far.</summary>
    internal string? Frame(in PerfFrame frame, double seconds, Vector3 feet, int collections)
    {
        if (double.IsNaN(_start))
        {
            Reset(seconds, feet, collections);
            return null;
        }

        _frames++;
        _frameMs += frame.FrameMs;
        _worst = Math.Max(_worst, frame.FrameMs);
        _slow += frame.FrameMs > SlowMs ? 1 : 0;
        _ticks += frame.Ticks;
        _tickMs += frame.TickMs;
        _buildMs += frame.BuildMs;
        _gpuWait += frame.GpuWaitMs;
        _draws += frame.DrawCalls;
        _triangles += frame.Triangles;
        _moved += Vector3.Distance(_last, feet);
        _last = feet;
        if (seconds - _start < Period)
        {
            return null;
        }

        var line = Line(collections - _gcAt);
        Reset(seconds, feet, collections);
        return line;
    }

    private string Line(int collections)
    {
        var c = CultureInfo.InvariantCulture;
        var n = Math.Max(_frames, 1);
        var mean = _frameMs / n;
        var fps = mean > 0 ? 1000.0 / mean : 0;
        var perTick = _ticks > 0 ? _tickMs / _ticks : 0;
        return string.Create(c, $"{fps:0.0} fps (mean {mean:0} ms, worst {_worst:0} ms, {_slow} over {SlowMs:0} ms); ") +
            string.Create(c, $"{_ticks / (double)n:0.0} ticks a frame ({perTick:0.0} ms each); build {_buildMs / n:0.0} ms; ") +
            string.Create(c, $"{_draws / n} draw calls, {_triangles / n / 1000}k triangles, GPU wait {_gpuWait / n:0.0} ms; ") +
            string.Create(c, $"{collections} GCs; at {_last.X:0},{_last.Y:0},{_last.Z:0}, moved {_moved:0} u");
    }

    private void Reset(double seconds, Vector3 feet, int collections)
    {
        _start = seconds;
        _gcAt = collections;
        (_frames, _slow, _ticks) = (0, 0, 0);
        (_frameMs, _worst, _tickMs, _buildMs, _gpuWait) = (0, 0, 0, 0, 0);
        (_draws, _triangles) = (0, 0);
        (_last, _moved) = (feet, 0f);
    }
}
