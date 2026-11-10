using SkyNet.Game.Audio;

namespace SkyNet.Game.Tests.Audio;

/// <summary>The rain's hiss made in code: a clean loop, not a clipped one (owner, 2026-11-09: "skôr
/// distortion než padajúci dážď").</summary>
public class RainHissTests
{
    [Fact]
    public void TheHissNeverClips()
    {
        var samples = RainHiss.Make(1).Samples;

        var clipped = samples.Count(s => MathF.Abs(s) >= 0.999f);
        var peak = samples.Max(MathF.Abs);

        Assert.True(clipped == 0, $"{clipped} of {samples.Length} samples clipped, peak {peak}");
    }

    [Fact]
    public void TheHowlNeverClips()
    {
        Assert.True(WindHowl.Make(1).Samples.Max(MathF.Abs) < 0.999f);
    }

    [Fact]
    public void TheHissKeepsItsLevel()
    {
        var samples = RainHiss.Make(1).Samples;

        var rms = MathF.Sqrt(samples.Sum(s => s * s) / samples.Length);

        Assert.InRange(rms, 0.99f * RainHiss.HissRms, 1.01f * RainHiss.HissRms);
    }
}
