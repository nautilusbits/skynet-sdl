namespace SkyNet.Game.Tests;

/// <summary>A long load's count is shown throttled: nothing for a quick load, then at most one frame
/// per <see cref="LoadProgress.IntervalMs"/>.</summary>
public class LoadProgressTests
{
    [Fact]
    public void QuickLoadShowsNothing()
    {
        long now = 0;
        var shown = 0;
        var p = new LoadProgress("MODELS", _ => shown++, () => now);
        // Done within one interval: no frame.
        var steps = LoadProgress.IntervalMs - 1;
        p.Begin(steps);
        for (var i = 0; i < steps; i++)
        {
            now++;
            p.Step();
        }

        Assert.Equal(0, shown);
        Assert.Equal(steps, p.Done);
    }

    [Fact]
    public void ShowsOncePerInterval()
    {
        long now = 0;
        List<int> shown = [];
        var p = new LoadProgress("MODELS", q => shown.Add(q.Done), () => now);
        p.Begin(1000);
        // 10 ms a step: 1000 steps over 10 s.
        for (var i = 0; i < 1000; i++)
        {
            now += 10;
            p.Step();
        }

        // A frame every `every` steps: the first interval's end rounded up to a step.
        var every = (LoadProgress.IntervalMs + 9) / 10;
        Assert.Equal(1000 / every, shown.Count);
        Assert.Equal(every, shown[0]);
        Assert.Equal(2 * every, shown[1]);
    }

    [Fact]
    public void TextAndFraction()
    {
        long now = 0;
        LoadProgress? seen = null;
        var p = new LoadProgress("PREPARING THE PLAYERS' MODELS", q => seen = q, () => now);
        Assert.Equal(0f, p.Fraction);
        p.Begin(4);
        now = LoadProgress.IntervalMs;
        p.Step();

        Assert.Same(p, seen);
        Assert.Equal("PREPARING THE PLAYERS' MODELS 1/4", p.Text);
        Assert.Equal(0.25f, p.Fraction);
    }
}
