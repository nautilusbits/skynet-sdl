using System.Numerics;
using SkyNet.Game.DevTools;

namespace SkyNet.Game.Tests.DevTools;

/// <summary>The log's perf line: one per period, the frames' means and the soldier's walk.</summary>
public class PerfLogTests
{
    [Fact]
    public void OneLinePerPeriodWithTheMeans()
    {
        var perf = new PerfLog();
        var frame = new PerfFrame(FrameMs: 100, Ticks: 6, TickMs: 12, BuildMs: 40, DrawCalls: 200, Triangles: 50_000, GpuWaitMs: 30f);

        Assert.Null(perf.Frame(frame, 0, Vector3.Zero, collections: 10));
        string? line = null;
        for (var i = 1; i <= 50 && line == null; i++)
        {
            line = perf.Frame(frame, i * 0.1, new Vector3(i * 10, 0, 0), collections: 13);
        }

        Assert.NotNull(line);
        Assert.Equal("10.0 fps (mean 100 ms, worst 100 ms, 50 over 66 ms); 6.0 ticks a frame (2.0 ms each); build 40.0 ms; " +
            "200 draw calls, 50k triangles, GPU wait 30.0 ms; 3 GCs; at 500,0,0, moved 500 u", line);
        Assert.Null(perf.Frame(frame, 5.1, new Vector3(500, 0, 0)));
    }

    [Fact]
    public void ClearStartsOver()
    {
        var perf = new PerfLog();
        var frame = new PerfFrame(16, 1, 1, 5, 100, 1000, 0f);
        perf.Frame(frame, 0, Vector3.Zero);
        perf.Frame(frame, 4, Vector3.Zero);
        perf.Clear();

        Assert.Null(perf.Frame(frame, 6, Vector3.Zero));
        Assert.Null(perf.Frame(frame, 10, Vector3.Zero));
        Assert.NotNull(perf.Frame(frame, 11, Vector3.Zero));
    }
}
