namespace SkyNet.Game.DevTools;

/// <summary>The frames of about the last second: their count (the FPS), the mean and the longest
/// frame time.</summary>
public readonly record struct FrameSummary(float Fps, float AverageMilliseconds, float MaxMilliseconds, int Frames);

/// <summary>
/// The rendered frames' times over a sliding window (<see cref="Window"/> seconds), for the FPS
/// readout and the overlay: a fixed ring, so adding a frame allocates nothing.
/// <code>
///   stats.Add(frameMs);        // each rendered frame
///   stats.Summary()            // FPS 60, 16.7 ms mean, 18.2 ms max over the last second
/// </code></summary>
public sealed class FrameStats
{
    public const float Window = 1f;
    private const int Capacity = 2048;

    private readonly float[] _ms = new float[Capacity];
    private int _next;
    private int _count;

    public void Add(float milliseconds)
    {
        _ms[_next] = milliseconds;
        _next = (_next + 1) % Capacity;
        _count = Math.Min(_count + 1, Capacity);
    }

    public void Clear() => (_next, _count) = (0, 0);

    /// <summary>The newest frames that fit in <see cref="Window"/> (at least one).</summary>
    public FrameSummary Summary()
    {
        var sum = 0f;
        var max = 0f;
        var frames = 0;
        for (var i = 0; i < _count; i++)
        {
            var ms = _ms[(_next - 1 - i + Capacity) % Capacity];
            if (frames > 0 && sum + ms > Window * 1000f)
            {
                break;
            }

            sum += ms;
            max = MathF.Max(max, ms);
            frames++;
        }

        if (frames == 0 || sum <= 0f)
        {
            return default;
        }

        var average = sum / frames;
        return new FrameSummary(1000f / average, average, max, frames);
    }
}
