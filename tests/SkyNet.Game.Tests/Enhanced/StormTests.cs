using System.Numerics;
using System.Text;
using RetroEng.Audio;
using RetroEng.Render;
using SkyNet.Game.Audio;
using SkyNet.Game.Enhanced;

namespace SkyNet.Game.Tests.Enhanced;

public class StormTests
{
    private const float Dt = 1f / 60f;
    private static readonly object Scene = new();

    private static StormTable Parse(string json) => StormTable.Parse(Encoding.UTF8.GetBytes(json));

    [Fact]
    public void TheFileSetsEveryKeyAndDefaultsTheRest()
    {
        var t = Parse("""{ "interval": 7.5, "brightness": 2, "distance": [300, 900], "thunderVolume": 0.5, "comment": "x" }""");
        Assert.Equal(new StormTable(true, 7.5f, 2f, 300f, 900f, 0.5f), t);
        Assert.Equal(StormTable.Defaults, Parse("{}"));
        Assert.False(Parse("""{ "on": false }""").On);
    }

    [Theory]
    [InlineData("""{ "rain": true }""")]
    [InlineData("""{ "interval": 0.2 }""")]
    [InlineData("""{ "brightness": -1 }""")]
    [InlineData("""{ "thunderVolume": 2 }""")]
    [InlineData("""{ "distance": [500] }""")]
    [InlineData("""{ "distance": [900, 300] }""")]
    [InlineData("""{ "distance": [0, 300] }""")]
    [InlineData("""{ "on": "yes" }""")]
    public void ABadFileIsAnError(string json) =>
        Assert.ThrowsAny<Exception>(() => Parse(json));

    [Fact]
    public void NoTableOrIndoorsMeansNoStrikeEvenWhenAsked()
    {
        var storm = new Storm(1);
        storm.Strike();
        Assert.Null(storm.Tick(Dt, null, Scene, StormSky.Outdoor, Vector3.Zero).Strike);
        storm.Strike();
        Assert.Null(storm.Tick(Dt, StormTable.Defaults, Scene, StormSky.Indoor, Vector3.Zero).Strike);
        for (var i = 0; i < 60 * 120; i++)
        {
            Assert.Null(storm.Tick(Dt, StormTable.Defaults, Scene, StormSky.Indoor, Vector3.Zero).Strike);
        }

        Assert.Equal(0, storm.ThunderPending);
    }

    [Fact]
    public void WaitsAverageTheTablesInterval()
    {
        var storm = new Storm(7);
        const int Count = 20000;
        var sum = 0.0;
        for (var i = 0; i < Count; i++)
        {
            var w = storm.NextWait(10f);
            Assert.True(w >= Storm.MinGap);
            sum += w;
        }

        Assert.InRange(sum / Count, 9.6, 10.5);
    }

    [Fact]
    public void StrikesComeAtTheTablesRate()
    {
        var storm = new Storm(3);
        var table = StormTable.Defaults with { Interval = 5f };
        StormStrike? last = null;
        var strikes = 0;
        const float Seconds = 3000f;
        for (var t = 0f; t < Seconds; t += Dt)
        {
            var f = storm.Tick(Dt, table, Scene, StormSky.Outdoor, Vector3.Zero);
            if (f.Strike != null && !ReferenceEquals(f.Strike, last))
            {
                strikes++;
            }

            last = f.Strike;
        }

        Assert.InRange(strikes, 0.85f * Seconds / 5f, 1.15f * Seconds / 5f);
    }

    [Fact]
    public void AStrikeFlickersAndEnds()
    {
        var storm = new Storm(11);
        var table = StormTable.Defaults with { Interval = 1000f, Brightness = 1f };
        storm.Strike();
        var first = storm.Tick(0f, table, Scene, StormSky.Outdoor, Vector3.Zero);
        var strike = first.Strike!;
        Assert.InRange(strike.Pulses.Length, 2, 3);
        Assert.Equal(Storm.DistanceScale(strike.Metres), first.Flash, 1e-4f);
        Assert.InRange(strike.Metres, table.NearMetres, table.FarMetres);

        // Dark between the pulses, bright again at the second.
        var second = strike.Pulses[1].At;
        Assert.True(Storm.FlashAt(strike, second - 0.001f) < Storm.FlashAt(strike, second));
        Assert.True(Storm.BoltAlphaAt(strike, 0f) > 0.9f);

        var age = 0f;
        while (storm.Tick(Dt, table, Scene, StormSky.Outdoor, Vector3.Zero).Strike != null)
        {
            age += Dt;
            Assert.True(age < 1.5f, "a strike lasts under 1.5 s");
        }
    }

    [Fact]
    public void ThunderComesAfterTheSoundsTravelTime()
    {
        var storm = new Storm(5);
        var table = StormTable.Defaults with { Interval = 1000f, NearMetres = 1715f, FarMetres = 1715f, ThunderVolume = 0.5f };
        var heard = new List<(Sound Sound, float Gain, float At)>();
        var time = 0f;
        storm.Thunder = (sound, _, gain) => heard.Add((sound, gain, time));
        storm.Strike();
        storm.Tick(0f, table, Scene, StormSky.Outdoor, Vector3.Zero);
        Assert.Equal(1, storm.ThunderPending);

        // 1715 m at 343 m/s: 5 s; in world units 1715 × 40 / (343 × 40).
        Assert.Equal(5f, Storm.ThunderDelay(1715f * Storm.UnitsPerMetre), 1e-4f);
        while (time < 6f)
        {
            time += Dt;
            storm.Tick(Dt, table, Scene, StormSky.Outdoor, Vector3.Zero);
        }

        var (thunder, gain, at) = Assert.Single(heard);
        Assert.InRange(at, 5f - Dt, 5f + 2 * Dt);
        Assert.Equal(Storm.ThunderGain(0.5f, 1715f), gain, 1e-4f);
        Assert.Equal(Thunder.Rate, thunder.SampleRate);
    }

    [Fact]
    public void ANewSceneDropsTheStrikeAndItsThunder()
    {
        var storm = new Storm(9);
        var table = StormTable.Defaults with { Interval = 1000f };
        storm.Strike();
        Assert.NotNull(storm.Tick(0f, table, Scene, StormSky.Outdoor, Vector3.Zero).Strike);
        Assert.Null(storm.Tick(Dt, table, new object(), StormSky.Outdoor, Vector3.Zero).Strike);
        Assert.Equal(0, storm.ThunderPending);
    }

    [Fact]
    public void TheFlashLightsFromTheBoltsWay()
    {
        var lighting = LookLighting.For(LightPreset.Night, null, 4000f, 1f);
        Assert.Same(lighting, Storm.Lit(lighting, default));

        var strike = new StormStrike(1, 1000f, MathF.PI / 2f, [(0f, 1f)], []);
        var lit = Storm.Lit(lighting, new StormFrame(strike, 0f, 1f, 1f));
        // The bolt lies along +X: its light goes towards -X and down.
        Assert.True(lit.SunDirection.X < -0.5f && lit.SunDirection.Y < 0f);
        Assert.True(lit.Sky.X > lighting.Sky.X && lit.Sun.X > lighting.Sun.X);
        Assert.Equal(0f, lit.Stars!.Brightness);
        Assert.True(Storm.SkyOf(new Vector4(0.1f, 0.1f, 0.1f, 1f), new StormFrame(strike, 0f, 1f, 1f)).X > 0.1f);
    }

    [Fact]
    public void TheBoltRunsFromTheCloudToTheGroundWithBranches()
    {
        var bolt = Storm.Bolt.Make(new Random(4));
        var main = bolt[0].Points;
        Assert.Equal((1 << Storm.Bolt.MainDepth) + 1, main.Length);
        Assert.Equal(1f, main[0].Y);
        Assert.Equal(0f, main[^1].Y);
        Assert.InRange(bolt.Length, 4, 7);
        Assert.All(bolt.Skip(1), b => Assert.True(b.Brightness < 1f && b.Points[^1].Y < b.Points[0].Y));
    }

    [Fact]
    public void ThunderIsTheSameForASeedAndNearCracks()
    {
        var near = Thunder.Make(42, ThunderKind.Near);
        Assert.Equal(near.Samples, Thunder.Make(42, ThunderKind.Near).Samples);
        Assert.InRange(near.Samples.Max(MathF.Abs), 0.8f * Thunder.Peak, Thunder.Peak);
        var far = Thunder.Make(42, ThunderKind.Far);
        Assert.True(far.Frames > near.Frames);

        // The crack: the first 0.2 s of a near strike far louder than a far one's.
        var span = (int)(0.2f * Thunder.Rate);
        static float Energy(float[] s, int n) => s.Take(n).Sum(x => x * x);
        Assert.True(Energy(near.Samples, span) > 4f * Energy(far.Samples, span));
    }
}
