using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace SkyNet.Game.DevTools;

/// <summary>
/// The frames of the first seconds after a map is entered (<see cref="Window"/>): how many took over
/// <see cref="HitchMs"/> and what was built in them, for the log. What a frame builds on first use
/// (a mod's sprite models, a robot's mesh, a replacement's GPU mesh) is reported through
/// <see cref="Note"/> from any thread and charged to the frame that ends next.
/// <code>
///   hitches.Start("MAP.210", Stopwatch.GetTimestamp());   // the load is done
///   hitches.Frame(Stopwatch.GetTimestamp())                // each frame: null, then the report once
///   LoadHitches.Note("robot mesh", 12.5)                   // a lazy build, wherever it runs
/// </code>
/// </summary>
public sealed class LoadHitches
{
    /// <summary>Seconds after the load that are watched.</summary>
    public const double Window = 10.0;

    /// <summary>A frame longer than this is a hitch (two 60 Hz frames).</summary>
    public const double HitchMs = 33.0;

    /// <summary>The hitches listed in the report (the longest).</summary>
    public const int Listed = 5;

    private static readonly ConcurrentQueue<(string What, double Ms)> Notes = new();

    /// <summary>Whether a window is open anywhere (no notes pile up without one).</summary>
    private static volatile bool _open;

    private string? _label;
    private long _start, _last;
    private int _frames;
    private double _worst;
    private readonly List<(double At, double Ms, string Causes)> _hitches = [];
    private readonly Dictionary<string, (int Count, double Ms)> _built = [];
    private readonly Dictionary<string, (int Count, double Ms)> _frame = [];
    private readonly StringBuilder _causes = new();

    /// <summary>Whether a window is being watched.</summary>
    public bool Watching => _label != null;

    /// <summary>A lazy build of <paramref name="ms"/> milliseconds (any thread).</summary>
    public static void Note(string what, double ms)
    {
        if (_open)
        {
            Notes.Enqueue((what, ms));
        }
    }

    /// <summary><see cref="Note"/> of the time since <paramref name="started"/> (<see cref="Stopwatch"/> ticks).</summary>
    public static void Since(string what, long started) =>
        Note(what, Stopwatch.GetElapsedTime(started).TotalMilliseconds);

    /// <summary>Watches the frames from <paramref name="timestamp"/> (<see cref="Stopwatch"/> ticks)
    /// on; what was noted before is dropped (the load's own builds).</summary>
    public void Start(string label, long timestamp)
    {
        Notes.Clear();
        _open = true;
        (_label, _start, _last, _frames, _worst) = (label, timestamp, timestamp, 0, 0.0);
        _hitches.Clear();
        _built.Clear();
    }

    /// <summary>A frame ended at <paramref name="timestamp"/>; the report when this frame closes
    /// the window, else null.</summary>
    public string? Frame(long timestamp)
    {
        if (_label == null)
        {
            return null;
        }

        var ms = (timestamp - _last) * 1000.0 / Stopwatch.Frequency;
        var at = (_last - _start) / (double)Stopwatch.Frequency;
        _last = timestamp;
        _frames++;
        _worst = Math.Max(_worst, ms);
        _frame.Clear();
        while (Notes.TryDequeue(out var note))
        {
            var (count, total) = _built.GetValueOrDefault(note.What);
            _built[note.What] = (count + 1, total + note.Ms);
            var (n, sum) = _frame.GetValueOrDefault(note.What);
            _frame[note.What] = (n + 1, sum + note.Ms);
        }

        // This frame's builds by kind: "sprite-model mesh 48x 62 ms".
        _causes.Clear();
        foreach (var (what, (n, sum)) in _frame.Where(f => f.Value.Ms >= 1.0).OrderByDescending(f => f.Value.Ms))
        {
            _causes.Append(_causes.Length > 0 ? ", " : "").Append(what).Append(' ').Append(n > 1 ? $"{n}x " : "").Append(Ms(sum));
        }

        if (ms > HitchMs)
        {
            _hitches.Add((at, ms, _causes.ToString()));
        }

        return (timestamp - _start) / (double)Stopwatch.Frequency >= Window ? Report() : null;
    }

    private string Report()
    {
        var c = CultureInfo.InvariantCulture;
        var text = new StringBuilder();
        text.Append(c, $"{_label}, the first {Window:0} s: {_frames} frames, {_hitches.Count} over {HitchMs:0} ms (worst {Ms(_worst)})");
        foreach (var (at, ms, causes) in _hitches.OrderByDescending(h => h.Ms).Take(Listed))
        {
            text.Append(c, $"; {Ms(ms)} at {at:0.0} s").Append(causes.Length > 0 ? $" ({causes})" : "");
        }

        if (_built.Count > 0)
        {
            text.Append("; built on first use: ")
                .AppendJoin(", ", _built.OrderByDescending(b => b.Value.Ms).Select(b => $"{b.Key} {b.Value.Count}x {Ms(b.Value.Ms)}"));
        }

        (_label, _open) = (null, false);
        Notes.Clear();
        return text.ToString();
    }

    private static string Ms(double ms) => ms.ToString("0", CultureInfo.InvariantCulture) + " ms";
}
