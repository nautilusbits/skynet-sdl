namespace SkyNet.Game;

/// <summary>
/// A long load's count, handed to <c>show</c> (a loading frame) at most once per <see cref="IntervalMs"/>:
/// a frame costs a present, and a load quicker than one interval shows nothing (no flash).
/// <code>
///   var p = new LoadProgress("PREPARING PLAYER MODELS", Draw);
///   p.Begin(1188);
///   foreach (...) { Build(...); p.Step(); }   // Draw(p) every 100 ms: "... 412/1188"
/// </code>
/// </summary>
public sealed class LoadProgress(string label, Action<LoadProgress> show, Func<long>? clockMs = null)
{
    /// <summary>The time between two frames shown (ms): 10 a second (a frame costs ~7 ms: 7 % of the load).</summary>
    public const int IntervalMs = 100;

    private readonly Func<long> _clock = clockMs ?? (() => Environment.TickCount64);
    private long _shownAt;

    /// <summary>The steps done.</summary>
    public int Done { get; private set; }

    /// <summary>The steps of the load; 0 before <see cref="Begin"/>.</summary>
    public int Total { get; private set; }

    /// <summary>Done of total, 0 to 1.</summary>
    public float Fraction => Total > 0 ? Math.Clamp((float)Done / Total, 0f, 1f) : 0f;

    /// <summary>The status line: "LABEL 412/1188".</summary>
    public string Text => Total > 0 ? $"{label} {Done}/{Total}" : label;

    /// <summary>The load starts with <paramref name="total"/> steps (the first frame one interval on).</summary>
    public void Begin(int total)
    {
        (Total, Done, _shownAt) = (total, 0, _clock());
    }

    /// <summary>One step done; shown when an interval passed since the last frame.</summary>
    public void Step()
    {
        Done++;
        var now = _clock();
        if (now - _shownAt < IntervalMs)
        {
            return;
        }

        _shownAt = now;
        show(this);
    }
}
