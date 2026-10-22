using System.Diagnostics;
using System.Runtime;

namespace SkyNet.Game;

/// <summary>
/// The GC's pace around a map's load. A load leaves much garbage (decoded textures, meshes, build
/// lists); collected during play it costs gen1 and gen2 pauses in the first seconds. So the load
/// ends with one full blocking collection, behind the loading screen, and play then runs in
/// <see cref="GCLatencyMode.SustainedLowLatency"/>: gen2 only in the background, never blocking.
/// <code>
///   Loading() ─► map built ─► Playing(): full GC, then no blocking gen2 until the next Loading()
/// </code>
/// </summary>
public static class GcPacing
{
    /// <summary>A map's load starts: blocking collections are allowed again.</summary>
    public static void Loading() => Loading(Apply);

    /// <summary><see cref="Loading()"/> with each GC setting changed through <paramref name="apply"/>.</summary>
    internal static void Loading(Action<Action> apply) => TryApply(apply, () => GCSettings.LatencyMode = GCLatencyMode.Interactive);

    /// <summary>The map is built: the load's garbage collected and compacted, the large object heap
    /// too (a load leaves holes among decoded textures, which stay committed otherwise), then play's
    /// latency mode. Returns the collection's time.</summary>
    public static TimeSpan Playing() => Playing(Apply);

    /// <summary><see cref="Playing()"/> with each GC setting changed through <paramref name="apply"/>.</summary>
    internal static TimeSpan Playing(Action<Action> apply)
    {
        var started = Stopwatch.GetTimestamp();
        TryApply(apply, () => GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce);
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        TryApply(apply, () => GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency);
        return Stopwatch.GetElapsedTime(started);
    }

    // Android's Mono has neither latency modes nor LOH compaction (PlatformNotSupportedException):
    // its GC runs as it is.
    private static void TryApply(Action<Action> apply, Action change)
    {
        try
        {
            apply(change);
        }
        catch (PlatformNotSupportedException)
        {
        }
    }

    private static void Apply(Action change) => change();
}
