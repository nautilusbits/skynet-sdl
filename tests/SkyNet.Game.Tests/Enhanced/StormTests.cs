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
    [InlineData("""{ "interval": 3601 }""")]
    [InlineData("""{ "brightness": -1 }""")]
    [InlineData("""{ "thunderVolume": 2 }""")]
    [InlineData("""{ "distance": [500] }""")]
    [InlineData("""{ "distance": [900, 300] }""")]
    [InlineData("""{ "distance": [0, 300] }""")]
    [InlineData("""{ "on": "yes" }""")]
    public void ABadFileIsAnError(string json) =>
        Assert.ThrowsAny<Exception>(() => Parse(json));

    /// <summary>An engine strike <paramref name="metres"/> away along +X from the origin.</summary>
    private static Strike StrikeAt(float metres, int seed = 3) =>
        new(0f, new Vector3(metres * Storm.UnitsPerMetre, 0f, 0f), metres * Storm.UnitsPerMetre, 1f, seed);

    [Fact]
    public void NoTableOrIndoorsMeansNoThunder()
    {
        var storm = new Storm();
        var heard = 0;
        storm.Thunder = (_, _, _) => heard++;
        storm.Post(StrikeAt(1000f));
        storm.Tick(Dt, null, Scene, StormSky.Outdoor, Vector3.Zero);
        storm.Post(StrikeAt(1000f));
        for (var i = 0; i < 60 * 10; i++)
        {
            storm.Tick(Dt, StormTable.Defaults, Scene, StormSky.Indoor, Vector3.Zero);
        }

        Assert.Equal(0, storm.ThunderPending);
        Assert.Equal(0, heard);
    }

    [Fact]
    public void AStrikeAskedForIsTakenOnce()
    {
        var storm = new Storm();
        Assert.Null(storm.TakeStrike());
        storm.Strike(Vector3.UnitX);
        Assert.Equal(Vector3.UnitX, storm.TakeStrike());
        Assert.Null(storm.TakeStrike());
    }

    [Fact]
    public void ThunderComesAfterTheSoundsTravelTime()
    {
        var storm = new Storm();
        var table = StormTable.Defaults with { ThunderVolume = 0.5f };
        var heard = new List<(Sound Sound, Vector3 At, float Gain, float Time)>();
        var time = 0f;
        storm.Thunder = (sound, at, gain) => heard.Add((sound, at, gain, time));

        // 1715 m at 343 m/s: 5 s.
        storm.Post(StrikeAt(1715f));
        storm.Tick(0f, table, Scene, StormSky.Outdoor, Vector3.Zero);
        Assert.Equal(1, storm.ThunderPending);
        while (time < 6f)
        {
            time += Dt;
            storm.Tick(Dt, table, Scene, StormSky.Outdoor, Vector3.Zero);
        }

        var (thunder, at, gain, when) = Assert.Single(heard);
        Assert.InRange(when, 5f - Dt, 5f + 2 * Dt);
        Assert.Equal(Storm.ThunderGain(0.5f, 1715f), gain, 1e-4f);
        Assert.Equal(Thunder.Rate, thunder.SampleRate);
        // From the bolt's side (+X), high over the ears.
        Assert.True(at.X > 0f && at.Y > at.X);
    }

    [Fact]
    public void ANewSceneDropsTheThunder()
    {
        var storm = new Storm();
        storm.Post(StrikeAt(1000f));
        storm.Tick(0f, StormTable.Defaults, Scene, StormSky.Outdoor, Vector3.Zero);
        Assert.Equal(1, storm.ThunderPending);
        storm.Tick(Dt, StormTable.Defaults, new object(), StormSky.Outdoor, Vector3.Zero);
        Assert.Equal(0, storm.ThunderPending);
    }

    [Fact]
    public void AStrikePostedForTheOldSceneIsNotHeardOnTheNew()
    {
        var storm = new Storm();
        storm.Tick(0f, StormTable.Defaults, Scene, StormSky.Outdoor, Vector3.Zero);
        storm.Post(StrikeAt(1000f));
        storm.Tick(Dt, StormTable.Defaults, new object(), StormSky.Outdoor, Vector3.Zero);
        Assert.Equal(0, storm.ThunderPending);
    }

    [Fact]
    public void TheEngineStrikesAtTheTablesRate()
    {
        var table = StormTable.Defaults with { Interval = 5f };
        var system = new WeatherSystem(WeatherMap.Of(SkyNet.Game.Enhanced.Weather.None with { Storm = table }, 0f), Vector3.UnitY, 3);
        var strikes = 0;
        system.Struck += _ => strikes++;
        const float Seconds = 3000f;
        for (var t = 0f; t < Seconds; t += 0.1f)
        {
            system.Update(0.1f, Vector3.Zero);
        }

        Assert.InRange(strikes, 0.85f * Seconds / 5f, 1.15f * Seconds / 5f);
    }

    [Fact]
    public void TheEngineWorksInWorldUnitsAtTheTablesBrightness()
    {
        var night = LookLighting.For(LightPreset.Night, null, 4000f, 1f);
        var table = StormTable.Defaults with { Interval = 1f, Brightness = 2f };
        var mapped = WeatherMap.Of(SkyNet.Game.Enhanced.Weather.None with { Storm = table }, 0f);
        Assert.Equal(2f, mapped.LightningIntensity);

        var system = new WeatherSystem(mapped, Vector3.UnitY, 1) { MetresPerUnit = 1f / Storm.UnitsPerMetre };
        system.Strike(new Vector3(800f * Storm.UnitsPerMetre, 0f, 0f), table.Brightness);
        var frame = system.Apply(night).Weather!;
        Assert.Equal(1f / Storm.UnitsPerMetre, frame.Scale);
        Assert.True(frame.Reach > 10f * Storm.UnitsPerMetre);
        Assert.True(frame.Flash.Y > 0f && frame.Bolt!.Brightness > 1f);

        // The effects table's lightning row on the bolt; the stars washed out by a full flash.
        var lit = night with { Weather = frame };
        Assert.Equal(frame.Bolt.Width * (float)EffectTable.Current[EffectTable.Lightning].Size, WeatherSky.Bolted(lit).Weather!.Bolt!.Width);
        Assert.Same(night, WeatherSky.Bolted(night));
        Assert.Same(night, WeatherSky.Washed(night, 0f));
        Assert.Equal(0f, WeatherSky.Washed(night, 1f).Stars!.Brightness);
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
