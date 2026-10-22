using System.Numerics;
using System.Text;
using RetroEng.Audio;
using RetroEng.Physics;
using RetroEng.Render;
using SkyNet.Game.Audio;
using SkyNet.Game.Enhanced;
using SkyNet.Game.Menu;

namespace SkyNet.Game.Tests.Enhanced;

public class WeatherTests
{
    private const float Dt = 1f / 60f;
    private const int Rate = 44100;
    private static readonly object Scene = new();
    private static readonly Vector3 Eye = new(100f, 75f, 100f);

    private static Weather Parse(params (string Path, string Json)[] files) =>
        Weather.Parse(files.Select(f => (f.Path, Encoding.UTF8.GetBytes(f.Json))));

    [Fact]
    public void ARainFileSetsEveryKeyAndDefaultsTheRest()
    {
        var w = Parse(("weather/wet.json", """{ "kind": "rain", "intensity": 0.9, "windDirection": 90, "windSpeed": 12, "volume": 0.4, "comment": "x" }"""));
        Assert.Equal(new RainTable(true, 0.9f, 90f, 12f, 0.4f), w.Rain);
        Assert.Null(w.Storm);
        Assert.Equal(RainTable.Defaults, Parse(("weather/rain.json", "{}")).Rain);
    }

    [Fact]
    public void KindsCombineAndTheKindDefaultsToTheFilesName()
    {
        var w = Parse(("weather/rain.json", """{ "intensity": 1 }"""), ("weather/storm.json", """{ "interval": 9 }"""));
        Assert.Equal(1f, w.Rain!.Intensity);
        Assert.Equal(9f, w.Storm!.Interval);
        Assert.True(w.Any);
    }

    [Fact]
    public void ASecondFileOfAKindOrABadOneIsLeftOut()
    {
        List<string> warnings = [];
        var w = Weather.Parse(
        [
            ("weather/a.json", Encoding.UTF8.GetBytes("""{ "kind": "rain", "intensity": 0.2 }""")),
            ("weather/b.json", Encoding.UTF8.GetBytes("""{ "kind": "rain", "intensity": 0.8 }""")),
            ("weather/c.json", Encoding.UTF8.GetBytes("""{ "kind": "snow" }""")),
            ("weather/d.json", Encoding.UTF8.GetBytes("""{ "intensity": 0.5 }""")),
            ("weather/storm.json", Encoding.UTF8.GetBytes("""{ "kind": "storm", "rain": true }""")),
        ], warnings.Add);
        Assert.Equal(0.2f, w.Rain!.Intensity);
        Assert.Null(w.Storm);
        Assert.Equal(4, warnings.Count);
    }

    [Theory]
    [InlineData("""{ "intensity": 1.5 }""")]
    [InlineData("""{ "windSpeed": -1 }""")]
    [InlineData("""{ "volume": 2 }""")]
    [InlineData("""{ "interval": 3 }""")]
    public void ABadRainFileIsAnError(string json)
    {
        List<string> warnings = [];
        var w = Weather.Parse([("weather/rain.json", Encoding.UTF8.GetBytes(json))], warnings.Add);
        Assert.Null(w.Rain);
        Assert.Single(warnings);
    }

    [Fact]
    public void RainTurnedOffIsNone() => Assert.False(Parse(("weather/rain.json", """{ "on": false }""")).Any);

    /// <summary>The streaks of a rain ticked a while over <paramref name="world"/>.</summary>
    private static (Particle[] Drops, int Count, RainFrame Frame) Rained(Rain rain, RainTable table, CollisionWorld? world,
        StormSky sky = StormSky.Outdoor, ParticleLevel level = ParticleLevel.High)
    {
        RainFrame frame = default;
        for (var i = 0; i < 120; i++)
        {
            frame = rain.Tick(Dt, table, Scene, sky, Eye, world);
        }

        var view = new RainView();
        rain.Capture(view);
        var drops = Array.Empty<Particle>();
        var n = Rain.Drops(view, Eye, level, new Vector3(0.1f), ref drops);
        return (drops, n, frame);
    }

    [Fact]
    public void ItRainsOutdoorsAndNotIndoorsOrWithParticlesOff()
    {
        var table = RainTable.Defaults with { Intensity = 1f };
        var open = Rained(new Rain(), table, null);
        Assert.InRange(open.Count, Rain.DropCount[(int)ParticleLevel.High] / 2, Rain.DropCount[(int)ParticleLevel.High]);
        Assert.True(open.Frame.Hiss > 0f);
        Assert.All(open.Drops.Take(open.Count), d => Assert.True(d.Shape == ParticleShape.Streak && d.Axis.Y < 0f));

        var indoor = Rained(new Rain(), table, null, StormSky.Indoor);
        Assert.Equal(0, indoor.Count);
        Assert.Equal(0f, indoor.Frame.Hiss);

        Assert.Equal(0, Rained(new Rain(), table, null, level: ParticleLevel.Off).Count);
        Assert.True(Rained(new Rain(), table, null, level: ParticleLevel.Low).Count < open.Count);
    }

    /// <summary>A flat roof 200 units over the eye, <paramref name="half"/> units each way round it.</summary>
    private static CollisionWorld Roof(float half)
    {
        var y = Eye.Y + 200f;
        var (a, b, c, d) = (new Vector3(Eye.X - half, y, Eye.Z - half), new Vector3(Eye.X + half, y, Eye.Z - half),
            new Vector3(Eye.X + half, y, Eye.Z + half), new Vector3(Eye.X - half, y, Eye.Z + half));
        return new CollisionWorld().Add(new TriangleGrid([new Triangle(a, b, c), new Triangle(a, c, d)], 256f));
    }

    [Fact]
    public void NoDropFallsUnderARoofAndTheHissIsQuieterThere()
    {
        var table = RainTable.Defaults with { Intensity = 1f };
        const float Half = 300f;
        var (drops, n, frame) = Rained(new Rain(), table, Roof(Half));
        var under = drops.Take(n).Count(d => MathF.Abs(d.Position.X - Eye.X) < Half - Rain.CoverCell
            && MathF.Abs(d.Position.Z - Eye.Z) < Half - Rain.CoverCell && d.Position.Y < Eye.Y + 200f);
        Assert.Equal(0, under);
        // Above the roof and round it, it rains.
        Assert.True(n > 500);
        Assert.Contains(drops.Take(n), d => d.Position.Y > Eye.Y + 200f);

        var open = Rained(new Rain(), table, null).Frame;
        Assert.InRange(frame.Hiss, 0.9f * Rain.ShelteredHiss * open.Hiss, 1.1f * Rain.ShelteredHiss * open.Hiss);
    }

    [Fact]
    public void ANewSceneOrNoTableStopsTheRain()
    {
        var rain = new Rain();
        var table = RainTable.Defaults;
        for (var i = 0; i < 60; i++)
        {
            rain.Tick(Dt, table, Scene, StormSky.Outdoor, Eye, null);
        }

        Assert.Null(rain.Tick(Dt, null, Scene, StormSky.Outdoor, Eye, null).Table);
        var view = new RainView();
        rain.Capture(view);
        Assert.Null(view.Table);

        // A new scene: the hiss rises again from silence.
        var first = rain.Tick(Dt, table, new object(), StormSky.Outdoor, Eye, null);
        Assert.True(first.Hiss < 0.1f * table.Volume);
    }

    /// <summary>The hiss through the mixer for a second at <paramref name="table"/>: its RMS (louder channel).</summary>
    private static float HissHeard(RainTable table, CollisionWorld? world)
    {
        var mixer = new Mixer(Rate);
        var sound = new WeatherSound(mixer);
        var rain = new Rain();
        var block = new float[Rate / 60 * 2];
        double sum = 0;
        var frames = 0;
        for (var t = 0; t < 180; t++)
        {
            var frame = rain.Tick(Dt, table, Scene, StormSky.Outdoor, Eye, world);
            sound.Rain(frame.Sound, frame.Hiss);
            mixer.Mix(block);
            if (t < 120)
            {
                continue;
            }

            foreach (var s in block)
            {
                sum += s * s;
            }

            frames += block.Length;
        }

        return (float)Math.Sqrt(sum / frames);
    }

    [Fact]
    public void TheHissIsHeardByTheIntensityAndUnderCoverLess()
    {
        var full = HissHeard(RainTable.Defaults with { Intensity = 1f, Volume = 1f }, null);
        var light = HissHeard(RainTable.Defaults with { Intensity = 0.25f, Volume = 1f }, null);
        var covered = HissHeard(RainTable.Defaults with { Intensity = 1f, Volume = 1f }, Roof(300f));

        // A background bed at full rain and volume (about the game's ambient wind.raw: 0.04 at -13 dB), a light rain about half.
        Assert.InRange(full, 0.03f, 0.07f);
        Assert.InRange(light / full, 0.4f, 0.6f);
        Assert.InRange(covered / full, 0.25f, 0.45f);
        Assert.Equal(0f, HissHeard(RainTable.Defaults with { Volume = 0f }, null));
    }

    [Fact]
    public void TheHissLoopsWithoutAClick()
    {
        var hiss = RainHiss.Make(3);
        Assert.Equal(2, hiss.Channels);
        Assert.Equal(Looping.Forever, hiss.Looping);
        var s = hiss.Samples;
        // The jump from the last frame to the first is no bigger than a step inside the loop.
        var steps = Enumerable.Range(1, hiss.Frames - 1).Select(i => MathF.Abs(s[i * 2] - s[(i - 1) * 2])).OrderBy(x => x).ToArray();
        var wrap = MathF.Abs(s[0] - s[(hiss.Frames - 1) * 2]);
        Assert.True(wrap <= steps[(int)(steps.Length * 0.99)], $"the wrap {wrap}");
    }

    [Fact]
    public void AModsSoundsReplaceTheMadeOnes()
    {
        var recorded = new Sound(new float[100], 1, 11025, Looping.Forever);
        var rain = new Rain();
        var frame = rain.Tick(Dt, RainTable.Defaults with { Recorded = recorded }, Scene, StormSky.Outdoor, Eye, null);
        Assert.Same(recorded, frame.Sound);
        Assert.Same(RainHiss.Sound, rain.Tick(Dt, RainTable.Defaults, Scene, StormSky.Outdoor, Eye, null).Sound);

        var storm = new Storm(2);
        var thunder = new Sound(new float[100], 1, 11025, Looping.Once);
        Sound? heard = null;
        storm.Thunder = (sound, _, _) => heard = sound;
        storm.Strike();
        var table = StormTable.Defaults with { Interval = 1000f, NearMetres = 100f, FarMetres = 100f, Recorded = thunder };
        for (var i = 0; i < 60; i++)
        {
            storm.Tick(Dt, table, Scene, StormSky.Outdoor, Eye);
        }

        Assert.Same(thunder, heard);
    }
}
