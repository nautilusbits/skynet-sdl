using System.Numerics;
using RetroEng.Render;
using SkyNet.Game.Enhanced;

namespace SkyNet.Game.Tests.Enhanced;

/// <summary>A map's bake off the load's thread: its estimated progress, its result, and a failure
/// (out of memory included) that ends it without a volume and without throwing.</summary>
public sealed class IrradianceJobTests
{
    private static readonly TimeSpan Long = TimeSpan.FromSeconds(30);

    private static IrradianceInput Input(int rays = 8) =>
        new(231, LightPreset.Interior, [], [], new IrradianceBakeSettings(Vector3.Zero, new Vector3(100, 100, 100), 50f) { Rays = rays, Bounces = 1 }, 0);

    [Fact]
    public void EstimateFollowsTheGuessThenEasesBelowTheCeiling()
    {
        Assert.Equal(0f, IrradianceJob.Estimate(0.0, 2.0));
        Assert.Equal(0.5f, IrradianceJob.Estimate(1.0, 2.0), 1e-5f);
        Assert.Equal(IrradianceJob.Steady, IrradianceJob.Estimate(1.8, 2.0), 1e-5f);

        // A slower device: past the guess the bar still moves, never to the end.
        var over = IrradianceJob.Estimate(2.0, 2.0);
        var later = IrradianceJob.Estimate(20.0, 2.0);
        Assert.InRange(over, IrradianceJob.Steady, IrradianceJob.Ceiling);
        Assert.True(later > over);
        Assert.True(later <= IrradianceJob.Ceiling);
    }

    [Fact]
    public void WorkCountsEveryProbesRaysForEachPass()
    {
        // 100 u every 50 u: 3 probes an axis; 8 rays; the direct pass and one bounce.
        Assert.Equal(3.0 * 3 * 3 * 8 * 2, IrradianceJob.Work(Input().Settings));
    }

    [Fact]
    public void ProgressRisesWhileTheBakeRunsAndEndsDone()
    {
        using var release = new ManualResetEventSlim();
        var job = IrradianceJob.Start(() => Input(), i =>
        {
            release.Wait(Long);
            return IrradianceBake.Bake(i.Meshes, i.Lamps, i.Settings);
        });

        // Waiting on the bake: the input's share is done, the bar below the end.
        SpinWait.SpinUntil(() => job.Input != null, Long);
        Assert.False(job.Wait(TimeSpan.FromMilliseconds(20)));
        Assert.InRange(job.Progress, IrradianceJob.InputShare, IrradianceJob.Ceiling);

        release.Set();
        Assert.True(job.Wait(Long));
        Assert.Equal(1f, job.Progress);
        Assert.NotNull(job.Result);
        Assert.Null(job.Failure);
        Assert.Equal(IrradianceJob.Threads, job.Input!.Settings.MaxDegreeOfParallelism);
    }

    [Fact]
    public void OutOfMemoryEndsWithoutAVolume()
    {
        var job = IrradianceJob.Start(() => Input(), _ => throw new OutOfMemoryException());

        Assert.True(job.Wait(Long));
        Assert.Null(job.Result);
        Assert.Equal(nameof(OutOfMemoryException), job.Failure);
    }

    [Fact]
    public void NoInputOrABrokenOneEndsWithoutAVolume()
    {
        var none = IrradianceJob.Start(() => null);
        var broken = IrradianceJob.Start(() => throw new InvalidDataException("no palette"));

        Assert.True(none.Wait(Long) && broken.Wait(Long));
        Assert.Null(none.Result);
        Assert.Equal("no input", none.Failure);
        Assert.Null(broken.Result);
        Assert.Equal(nameof(InvalidDataException), broken.Failure);
    }

    [Fact]
    public void MeasuredRateIsKeptAcrossRuns()
    {
        var dir = Directory.CreateTempSubdirectory("skynet-rate-").FullName;
        try
        {
            var file = Path.Combine(dir, "lighting", "bake-rate.txt");
            var before = IrradianceJob.Rate;

            // No file, or one that is not a rate: the default stays.
            IrradianceJob.LoadRate(file);
            Assert.Equal(before, IrradianceJob.Rate);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, "fast");
            IrradianceJob.LoadRate(file);
            Assert.Equal(before, IrradianceJob.Rate);

            // A phone's rate saved, then read at the next start.
            File.Delete(file);
            IrradianceJob.SaveRate(file, 2.5e5);
            IrradianceJob.LoadRate(file);
            Assert.Equal(2.5e5, IrradianceJob.Rate);
            IrradianceJob.SaveRate(file, before);
            IrradianceJob.LoadRate(file);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ThreadsLeaveACoreFree()
    {
        Assert.Equal(Math.Max(1, Environment.ProcessorCount - 1), IrradianceJob.Threads);
    }
}
