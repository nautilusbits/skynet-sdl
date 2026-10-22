using System.Diagnostics;
using SkyNet.Game.DevTools;

namespace SkyNet.Game.Tests.DevTools;

/// <summary>The first seconds after a load: hitches counted, lazy builds charged to their frame, one report.</summary>
public class LoadHitchesTests
{
    private static long Ms(double ms) => (long)(ms * Stopwatch.Frequency / 1000.0);

    [Fact]
    public void Reports_hitches_and_their_builds_once_the_window_closes()
    {
        var hitches = new LoadHitches();
        LoadHitches.Note("left over from the load", 500);
        hitches.Start("MAP.210", 0);

        var t = 0L;
        Assert.Null(hitches.Frame(t += Ms(16)));
        LoadHitches.Note("test build", 120);
        Assert.Null(hitches.Frame(t += Ms(130)));
        string? report = null;
        while (report == null)
        {
            report = hitches.Frame(t += Ms(16));
        }

        Assert.Contains("MAP.210", report);
        Assert.Contains("1 over 33 ms", report);
        Assert.Contains("130 ms at 0.0 s (test build 120 ms)", report);
        Assert.Contains("built on first use: test build 1x 120 ms", report);
        Assert.DoesNotContain("left over", report);
        Assert.False(hitches.Watching);
        Assert.Null(hitches.Frame(t + Ms(16)));
    }

    [Fact]
    public void Smooth_frames_report_no_hitch()
    {
        var hitches = new LoadHitches();
        hitches.Start("MAP.211", 0);
        string? report = null;
        for (var t = Ms(16); report == null; t += Ms(16))
        {
            report = hitches.Frame(t);
        }

        Assert.Contains("0 over 33 ms", report);
        Assert.DoesNotContain("built on first use", report);
    }
}
