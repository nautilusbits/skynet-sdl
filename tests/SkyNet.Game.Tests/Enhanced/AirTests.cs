using System.Numerics;
using System.Text;
using RetroEng.Audio;
using RetroEng.Physics;
using RetroEng.Render;
using SkyNet.Game.Audio;
using SkyNet.Game.Enhanced;
using SkyNet.Game.Menu;
using SkyNet.Game.Tests.Audio;

namespace SkyNet.Game.Tests.Enhanced;

public class AirTests
{
    private const float Dt = 1f / 60f;
    private const int Rate = 44100;
    private static readonly object Scene = new();
    private static readonly Vector3 Eye = new(100f, 75f, 100f);
    /// <summary>The wasteland sample's kinds.</summary>
    private static readonly (string, string)[] Wasteland =
    [
        ("weather/clouds.json", """{ "coverage": 0.9, "height": 300, "size": 200, "colour": [0.1, 0.1, 0.1], "fireGlow": 2, "drift": 3 }"""),
        ("weather/haze.json", """{ "colour": [0.2, 0.15, 0.1], "visibility": 40, "density": 0.5, "horizon": 0.6 }"""),
        ("weather/dust.json", """{ "amount": 1, "colour": [0.4, 0.3, 0.2], "height": 4, "streaks": 1, "interval": 30, "duration": 15, "peak": 0.9, "size": 50, "rise": 12 }"""),
        ("weather/wind.json", """{ "direction": 90, "speed": 10, "gusts": 0.4, "volume": 0.7 }"""),
    ];

    private static Weather Parse(params (string Path, string Json)[] files) =>
        Weather.Parse(files.Select(f => (f.Path, Encoding.UTF8.GetBytes(f.Json))));

    [Fact]
    public void TheAirsKindsReadEveryKeyAndCombine()
    {
        var w = Parse(Wasteland);
        Assert.Equal(new CloudTable(true, 0.9f, 300f, 200f, new Vector3(0.1f), 2f, 3f), w.Clouds);
        Assert.Equal(new HazeAirTable(true, new Vector3(0.2f, 0.15f, 0.1f), 40f, 0.5f, 0.6f), w.Haze);
        Assert.Equal(new DustTable(true, 1f, new Vector3(0.4f, 0.3f, 0.2f), 4f, 1f)
        {
            Interval = 30f, Duration = 15f, Peak = 0.9f, SizeMetres = 50f, RiseMetres = 12f,
        }, w.Dust);
        Assert.Equal(new WindTable(true, 90f, 10f, 0.4f, 0.7f), w.Wind);
        Assert.Null(w.Rain);
        Assert.True(w.Any);

        // Left out: the defaults; a "kind" names the kind whatever the file's name.
        var d = Parse(("weather/a.json", """{ "kind": "clouds" }"""), ("weather/b.json", """{ "kind": "wind" }"""));
        Assert.Equal(CloudTable.Defaults, d.Clouds);
        Assert.Equal(WindTable.Defaults, d.Wind);
    }

    [Fact]
    public void RainBringsAnOvercastDeck()
    {
        Assert.Equal(CloudTable.RainCoverage, Parse(("weather/rain.json", "{}")).Clouds!.Coverage);
        var both = Parse(("weather/rain.json", "{}"), ("weather/clouds.json", """{ "coverage": 0.3, "height": 900 }"""));
        Assert.Equal(CloudTable.RainCoverage, both.Clouds!.Coverage);
        Assert.Equal(900f, both.Clouds.HeightMetres);
        Assert.Null(Parse(Wasteland[1]).Clouds);
    }

    [Theory]
    [InlineData("weather/clouds.json", """{ "coverage": 2 }""")]
    [InlineData("weather/clouds.json", """{ "colour": [1, 1] }""")]
    [InlineData("weather/haze.json", """{ "colour": [0.5, 0.5, 1.5] }""")]
    [InlineData("weather/dust.json", """{ "amount": -1 }""")]
    [InlineData("weather/dust.json", """{ "interval": 1 }""")]
    [InlineData("weather/wind.json", """{ "gusts": 3 }""")]
    [InlineData("weather/wind.json", """{ "speed": 8, "rain": 1 }""")]
    public void ABadAirFileIsLeftOut(string path, string json)
    {
        List<string> warnings = [];
        var w = Weather.Parse([(path, Encoding.UTF8.GetBytes(json))], warnings.Add);
        Assert.False(w.Any);
        Assert.Single(warnings);
    }

    [Fact]
    public void TheWindGustsAroundItsMeanAndItsDriftIsItsTravel()
    {
        var wind = new Wind();
        var table = WindTable.Defaults with { Direction = 90f, Speed = 10f, Gusts = 0.8f };
        WindState state = default;
        var gusts = new List<float>();
        var travelled = Vector3.Zero;
        for (var i = 0; i < 60 * 60; i++)
        {
            state = wind.Tick(Dt, table, Scene)!.Value;
            gusts.Add(state.Gust);
            travelled += state.Velocity * Dt;
        }

        // Blowing towards +X, gusting both ways round its mean, the drift what it carried.
        Assert.True(state.Velocity.X > 0f && MathF.Abs(state.Velocity.Z) < state.Velocity.X);
        Assert.InRange(gusts.Average(), 0.85f, 1.15f);
        Assert.True(gusts.Max() > 1.3f && gusts.Min() < 0.7f);
        Assert.True(Vector3.Distance(travelled, state.Drift) < 1f);
        Assert.All(gusts, g => Assert.InRange(g, Wind.MinGust, Wind.MaxGust));

        // Steady without gusts; none without a table.
        Assert.Equal(1f, Wind.GustAt(0f, 12.3f));
        Assert.Null(new Wind().Tick(Dt, null, Scene));
    }

    /// <summary>The streaks of a rain ticked a while in <paramref name="wind"/> (null: its own).</summary>
    private static Particle[] Rained(WindState? wind)
    {
        var rain = new Rain();
        var table = RainTable.Defaults with { Intensity = 1f, WindDirection = 0f, WindSpeed = 2f };
        for (var i = 0; i < 60; i++)
        {
            rain.Tick(Dt, table, Scene, StormSky.Outdoor, Eye, null, wind);
        }

        var view = new RainView();
        rain.Capture(view);
        var drops = Array.Empty<Particle>();
        var n = Rain.Drops(view, Eye, ParticleLevel.High, new Vector3(0.1f), ref drops);
        return drops[..n];
    }

    [Fact]
    public void TheWindDrivesTheRainsSlant()
    {
        static float Slant(Particle d) => new Vector2(d.Axis.X, d.Axis.Z).Length() / -d.Axis.Y;

        // The rain's own wind: 2 m/s towards +Z.
        var own = Rained(null);
        Assert.All(own, d => Assert.True(d.Axis.Z > 0f && MathF.Abs(d.Axis.X) < 1e-3f));

        // A 20 m/s wind towards +X: the streaks blown that way, far more slanted.
        var velocity = new Vector3(20f * Storm.UnitsPerMetre, 0f, 0f);
        var blown = Rained(new WindState(velocity, velocity, 1f));
        Assert.All(blown, d => Assert.True(d.Axis.X > 0f && MathF.Abs(d.Axis.Z) < 1e-3f));
        Assert.True(blown.Average(Slant) > 5f * own.Average(Slant));
    }

    /// <summary>A flat roof 200 units over the eye, <paramref name="half"/> units each way round it,
    /// over a ground 75 units under it.</summary>
    private static CollisionWorld Roofed(float half)
    {
        static Triangle[] Square(float x, float z, float half, float y)
        {
            var (a, b, c, d) = (new Vector3(x - half, y, z - half), new Vector3(x + half, y, z - half),
                new Vector3(x + half, y, z + half), new Vector3(x - half, y, z + half));
            return [new Triangle(a, b, c), new Triangle(a, c, d)];
        }

        var world = new CollisionWorld().Add(new TriangleGrid(Square(Eye.X, Eye.Z, 4000f, 0f), 256f));
        return half > 0f ? world.Add(new TriangleGrid(Square(Eye.X, Eye.Z, half, Eye.Y + 200f), 256f)) : world;
    }

    /// <summary>Whether a dust cloud is raised at the start.</summary>
    private enum Raise { None, Gust }

    /// <summary>The air of <paramref name="weather"/> ticked a while (2 s, or half a raised dust
    /// cloud's life: it is at the eye), captured.</summary>
    private static (AirView View, AirFrame Frame) Aired(Weather weather, CollisionWorld? world, StormSky sky = StormSky.Outdoor,
        Raise raise = Raise.None)
    {
        var air = new Air();
        if (raise == Raise.Gust)
        {
            air.Gust();
        }

        AirFrame frame = default;
        var ticks = raise == Raise.Gust ? (int)(weather.Dust!.Duration * 0.5f / Dt) : 120;
        for (var i = 0; i < ticks; i++)
        {
            frame = air.Tick(Dt, weather, Scene, sky, Eye, world);
        }

        var view = new AirView();
        air.Capture(view);
        return (view, frame);
    }

    private static int DustIn(AirView view, Func<Particle, bool> where, ParticleLevel level = ParticleLevel.High)
    {
        var sheets = Array.Empty<Particle>();
        var grit = Array.Empty<Particle>();
        var n = Dust.Sheets(view, Eye, level, new Vector3(0.1f), ref sheets);
        var g = Dust.Grit(view, Eye, level, new Vector3(0.1f), ref grit);
        return sheets.Take(n).Concat(grit.Take(g)).Count(where);
    }

    /// <summary>The dust settles on the cover read from the collision world: without one (the editor
    /// once passed none) nothing is drawn, so a view showing a mod's weather must pass its level's.</summary>
    [Fact]
    public void DustNeedsTheCollisionWorld()
    {
        var weather = Parse(Wasteland);
        Assert.Equal(0, DustIn(Aired(weather, null, raise: Raise.Gust).View, _ => true));
        Assert.True(DustIn(Aired(weather, Roofed(0f), raise: Raise.Gust).View, _ => true) > 0);
    }

    [Fact]
    public void DustDriftsOverOpenGroundNotIndoorsNorUnderARoof()
    {
        var weather = Parse(Wasteland);
        var open = Aired(weather, Roofed(0f), raise: Raise.Gust).View;
        Assert.True(DustIn(open, _ => true) > 100);
        Assert.True(DustIn(open, _ => true, ParticleLevel.Low) < DustIn(open, _ => true));
        Assert.Equal(0, DustIn(open, _ => true, ParticleLevel.Off));

        // Indoors: no air at all.
        var indoor = Aired(weather, Roofed(0f), StormSky.Indoor, Raise.Gust);
        Assert.Equal(0, DustIn(indoor.View, _ => true));
        Assert.Equal(0f, indoor.Frame.Howl);

        // Under a roof: none below it, while it blows round it and over it.
        const float Half = 400f;
        var roofed = Aired(weather, Roofed(Half), raise: Raise.Gust).View;
        var under = DustIn(roofed, d => MathF.Abs(d.Position.X - Eye.X) < Half - SkyCover.Cell
            && MathF.Abs(d.Position.Z - Eye.Z) < Half - SkyCover.Cell && d.Position.Y < Eye.Y + 200f);
        Assert.Equal(0, under);
        // (The grit's box lies on the roof; the sheets' reaches past it.)
        Assert.True(DustIn(roofed, d => MathF.Abs(d.Position.X - Eye.X) > Half && d.Position.Y < Eye.Y + 200f) > 5);
    }

    /// <summary>A dust table with clouds every <paramref name="interval"/> s, each <paramref name="duration"/> s.</summary>
    private static Weather Dusty(float interval, float duration) => Parse(Wasteland[3]) with
    {
        Dust = DustTable.Defaults with { Interval = interval, Duration = duration, Peak = 1f },
    };

    [Fact]
    public void DustCloudsComeAndGoWithTheGusts()
    {
        // Ten minutes of a cloud every 40 s on average, 20 s each: the dust at the eye and the wind's gust.
        var weather = Dusty(40f, 20f);
        var air = new Air();
        var view = new AirView();
        List<(float Dust, float Gust, float Squall)> frames = [];
        for (var i = 0; i < 600 * 60; i++)
        {
            var f = air.Tick(Dt, weather, Scene, StormSky.Outdoor, Eye, null);
            air.Capture(view);
            frames.Add((view.DustNear, f.Wind!.Value.Gust, view.Squall));
        }

        // Mostly clear, with clear stretches between clouds; a dozen or so dense episodes.
        Assert.InRange(frames.Count(f => f.Dust < 0.05f) / (float)frames.Count, 0.4f, 0.9f);
        var episodes = 0;
        var inside = false;
        foreach (var f in frames)
        {
            if (!inside && f.Dust > 0.5f)
            {
                (episodes, inside) = (episodes + 1, true);
            }
            else if (inside && f.Dust < 0.05f)
            {
                inside = false;
            }
        }

        Assert.InRange(episodes, 10, 20);

        // The thick dust comes with a squall: the wind blows harder then.
        var thick = frames.Where(f => f.Dust > 0.5f).ToList();
        var clear = frames.Where(f => f.Dust < 0.05f && f.Squall == 0f).ToList();
        Assert.True(thick.Average(f => f.Squall) > 0.7f);
        Assert.True(thick.Average(f => f.Gust) > clear.Average(f => f.Gust) + 0.5f);

        // The schedule: one event an interval on average, never two at once.
        var starts = Enumerable.Range(0, 100).Select(k => DustClouds.StartOf(weather.Dust!, k)).ToArray();
        Assert.InRange((starts[^1] - starts[0]) / 99f, 38f, 42f);
        Assert.All(starts.Zip(starts.Skip(1)), p => Assert.True(p.Second - p.First >= 0.4f * 40f - 1e-3f));
    }

    [Fact]
    public void ARaisedCloudRollsPastTheEyeAndTheAirClears()
    {
        // A cloud raised now (the schedule's first one minutes away): it reaches the eye halfway through
        // its life, thick, and is gone after it, the haze back to the cue alone.
        var weather = Dusty(600f, 20f);
        var air = new Air();
        var view = new AirView();
        var cue = new Fog(1500f, 10000f, 63, 0) { Beyond = FogBeyond.Clip, Colour = new Vector4(0.1f, 0.1f, 0.12f, 1f) };
        air.Gust();
        List<float> dust = [];
        for (var i = 0; i < 30 * 60; i++)
        {
            air.Tick(Dt, weather, Scene, StormSky.Outdoor, Eye, null);
            air.Capture(view);
            dust.Add(view.DustFog);
            if (i == 10 * 60)
            {
                // In the cloud: the view closes in and takes the dust's colour.
                var dusted = WeatherSky.Dusted(cue, view)!;
                Assert.True(dusted.End < 0.2f * cue.End);
                Assert.True(dusted.Colour.X > dusted.Colour.Z);
            }
        }

        var peak = dust.IndexOf(dust.Max());
        Assert.True(dust.Max() > 0.6f);
        Assert.InRange(peak * Dt, 6f, 14f);
        Assert.True(dust[60] < 0.1f);
        Assert.True(dust[^1] < 0.01f);
        Assert.Equal(0, view.DustCount);
        Assert.Same(cue, WeatherSky.Dusted(cue, view));
    }

    [Fact]
    public void ADustCloudDoesNotCloseInUnderARoofNorIndoors()
    {
        var weather = Dusty(600f, 20f);
        var open = Aired(weather, Roofed(0f), raise: Raise.Gust).View;
        var roofed = Aired(weather, Roofed(400f), raise: Raise.Gust).View;
        var indoor = Aired(weather, Roofed(0f), StormSky.Indoor, Raise.Gust).View;
        Assert.True(open.DustFog > 0.5f);
        Assert.True(roofed.DustNear > 0.5f);
        Assert.True(roofed.DustFog < 0.05f);
        Assert.Equal(0f, indoor.DustFog);
        Assert.Equal(0, indoor.DustCount);
    }

    [Fact]
    public void TheDeckHidesTheStarsAndDimsTheMoon()
    {
        var night = LookLighting.For(LightPreset.Night, null, 4000f, 1f);
        Assert.NotNull(night.Stars);
        var cue = new Fog(800f, 4000f, 63, 0) { Beyond = FogBeyond.Clip, Colour = new Vector4(0.1f, 0.1f, 0.12f, 1f) };
        Lighting Under(float coverage) => WeatherSky.Clouded(night, night,
            new AirView { Clouds = CloudTable.Defaults with { Coverage = coverage } }, cue, 0f, Vector3.Zero, 0f, ParticleLevel.High);

        var overcast = Under(1f);
        Assert.Null(overcast.Stars);
        Assert.Equal(1f, overcast.Clouds!.Coverage);
        Assert.True(overcast.Sun.Length() < 0.3f * night.Sun.Length());

        // Broken cloud: the stars stay for the gaps (the deck hides the rest per pixel).
        var broken = Under(0.5f);
        Assert.NotNull(broken.Stars);
        Assert.True(broken.Sun.Length() > overcast.Sun.Length());

        // No clouds or haze: the light as it was.
        Assert.Same(night, WeatherSky.Clouded(night, night, new AirView(), cue, 0f, Vector3.Zero, 0f, ParticleLevel.High));

        // A flash lights the deck from its way.
        var flash = WeatherSky.Clouded(night, night, new AirView { Clouds = CloudTable.Defaults }, cue, 1f, Vector3.UnitX, 0f, ParticleLevel.High);
        Assert.True(flash.Clouds!.Underlight.Length() > 1f);
        Assert.Equal(Vector3.UnitX, flash.Clouds.UnderlightWay);
    }

    [Fact]
    public void TheHazeThickensTheDepthCueAndColoursIt()
    {
        var cue = new Fog(1500f, 4000f, 63, 0) { Beyond = FogBeyond.Clip, Colour = new Vector4(0.1f, 0.1f, 0.12f, 1f) };
        var haze = HazeAirTable.Defaults with { VisibilityMetres = 50f, Colour = new Vector3(0.3f, 0.2f, 0.1f) };
        var hazed = WeatherSky.Hazed(cue, haze, 0f)!;
        Assert.Equal(50f * Storm.UnitsPerMetre, hazed.End);
        Assert.True(hazed.Start < cue.Start);
        Assert.Equal(FogBeyond.Hold, hazed.Beyond);
        Assert.Equal(0.3f, hazed.Colour.X, 2);
        Assert.True(hazed.Colour.X > hazed.Colour.Z);

        // Seen past the far clip, the far clip's; indoors (no cue) none.
        Assert.Equal(cue.End, WeatherSky.Hazed(cue, haze with { VisibilityMetres = 500f }, 0f)!.End);
        Assert.Equal(FogBeyond.Clip, WeatherSky.Hazed(cue, haze with { VisibilityMetres = 500f }, 0f)!.Beyond);
        Assert.Null(WeatherSky.Hazed(null, haze, 0f));
        Assert.Same(cue, WeatherSky.Hazed(cue, null, 0f));
    }

    /// <summary>A loop through the mixer for 2 s (after 1 s settling): its RMS (louder channel).</summary>
    private static float Heard(Func<Air, Rain, WeatherSound, float> tick)
    {
        var mixer = new Mixer(Rate);
        var sound = new WeatherSound(mixer);
        var air = new Air();
        var rain = new Rain(air.Cover);
        var block = new float[Rate / 60 * 2];
        double sum = 0;
        var frames = 0;
        for (var t = 0; t < 180; t++)
        {
            tick(air, rain, sound);
            mixer.Mix(block);
            if (t < 60)
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

    private static float Howl(Weather weather, CollisionWorld? world, StormSky sky = StormSky.Outdoor) => Heard((air, _, sound) =>
    {
        var f = air.Tick(Dt, weather, Scene, sky, Eye, world);
        sound.Wind(f.Sound, f.Howl, f.Pitch);
        return f.Howl;
    });

    private static float Hiss(RainTable table, CollisionWorld? world) => Heard((air, rain, sound) =>
    {
        var weather = Weather.None with { Rain = table };
        air.Tick(Dt, weather, Scene, StormSky.Outdoor, Eye, world);
        var f = rain.Tick(Dt, table, Scene, StormSky.Outdoor, Eye, world);
        sound.Rain(f.Sound, f.Hiss);
        return f.Hiss;
    });

    [Fact]
    public void TheHowlIsABedQuieterUnderCoverAndNoneIndoors()
    {
        var weather = Parse(Wasteland[3]);
        var open = Howl(weather, null);
        var covered = Howl(weather, Roofed(400f));
        Assert.InRange(open, 0.008f, 0.05f);
        Assert.InRange(covered / open, 0.3f, 0.6f);
        Assert.Equal(0f, Howl(weather, null, StormSky.Indoor));
        Assert.Equal(0f, Howl(Parse(Wasteland[0]), null));
    }

    [Fact]
    public void TheHowlLoopsWithoutAClick()
    {
        var howl = WindHowl.Make(3);
        Assert.Equal(2, howl.Channels);
        Assert.Equal(Looping.Forever, howl.Looping);
        var s = howl.Samples;
        var steps = Enumerable.Range(1, howl.Frames - 1).Select(i => MathF.Abs(s[i * 2] - s[(i - 1) * 2])).OrderBy(x => x).ToArray();
        var wrap = MathF.Abs(s[0] - s[(howl.Frames - 1) * 2]);
        Assert.True(wrap <= steps[(int)(steps.Length * 0.99)], $"the wrap {wrap}");
    }

    /// <summary>The loudest 50 ms of DOS's gunshot (SHOTS2.RAW, the rifles') through the mixer at its 0 dB (RMS).</summary>
    private static float Gunshot()
    {
        using var archive = AudioTestData.Open().OpenArchive(SoundBank.Archive);
        var shot = SoundBank.Decode("shots2.raw", archive.Read("shots2.raw")!)!;
        var mixer = new Mixer(Rate);
        mixer.Play(shot, SoundBank.Default2DDb == 0f ? 1f : 0f);
        var all = new float[Rate * 2];
        mixer.Mix(all);
        var window = Rate / 20 * 2;
        var loudest = 0.0;
        for (var at = 0; at + window <= all.Length; at += window / 4)
        {
            var sum = 0.0;
            for (var i = at; i < at + window; i++)
            {
                sum += all[i] * all[i];
            }

            loudest = Math.Max(loudest, Math.Sqrt(sum / window));
        }

        return (float)loudest;
    }

    private static float Db(float ratio) => 20f * MathF.Log10(ratio);

    [AudioDataFact]
    public void TheRainAndTheWindSitWellUnderAGunshot()
    {
        var gun = Gunshot();
        var rain = Hiss(RainTable.Defaults with { Intensity = 1f }, null);
        var wind = Howl(Parse(Wasteland[3]), null);
        Assert.InRange(Db(rain / gun), -26f, -18f);
        Assert.InRange(Db(wind / gun), -28f, -18f);
    }

    [Fact]
    public void TheHissIsABackgroundBed()
    {
        // Full rain at the default volume: a bed (DOS's ambient wind.raw plays at about 0.04).
        var full = Hiss(RainTable.Defaults with { Intensity = 1f }, null);
        Assert.InRange(full, 0.015f, 0.045f);
    }

    /// <summary>Renders the rain (intensity 1, open and under a roof) and the wind's howl as WAVs into
    /// the folder WEATHER_WAV names, to listen to; nothing without it.</summary>
    [Fact]
    public void RendersTheBedsToListenTo()
    {
        var folder = Environment.GetEnvironmentVariable("WEATHER_WAV");
        if (string.IsNullOrEmpty(folder))
        {
            return;
        }

        Directory.CreateDirectory(folder);
        var table = RainTable.Defaults with { Intensity = 1f };
        Render(Path.Combine(folder, "rain-open.wav"), (air, rain, sound, world) =>
        {
            air.Tick(Dt, Weather.None with { Rain = table }, Scene, StormSky.Outdoor, Eye, world);
            var f = rain.Tick(Dt, table, Scene, StormSky.Outdoor, Eye, world);
            sound.Rain(f.Sound, f.Hiss);
        }, null);
        Render(Path.Combine(folder, "rain-covered.wav"), (air, rain, sound, world) =>
        {
            air.Tick(Dt, Weather.None with { Rain = table }, Scene, StormSky.Outdoor, Eye, world);
            var f = rain.Tick(Dt, table, Scene, StormSky.Outdoor, Eye, world);
            sound.Rain(f.Sound, f.Hiss);
        }, Roofed(400f));
        var wind = Parse(Wasteland[3]);
        Render(Path.Combine(folder, "wind-open.wav"), (air, _, sound, world) =>
        {
            var f = air.Tick(Dt, wind, Scene, StormSky.Outdoor, Eye, world);
            sound.Wind(f.Sound, f.Howl, f.Pitch);
        }, null);
        if (AudioTestData.Dir != null)
        {
            // The gunshot at its own level, for the comparison.
            using var archive = AudioTestData.Open().OpenArchive(SoundBank.Archive);
            var shot = SoundBank.Decode("shots2.raw", archive.Read("shots2.raw")!)!;
            var mixer = new Mixer(Rate);
            mixer.Play(shot);
            var all = new float[Rate * 2 * 2];
            mixer.Mix(all);
            File.WriteAllBytes(Path.Combine(folder, "gunshot.wav"), WavWriter.Encode(all, Rate));
        }
    }

    private static void Render(string path, Action<Air, Rain, WeatherSound, CollisionWorld?> tick, CollisionWorld? world)
    {
        var mixer = new Mixer(Rate);
        var sound = new WeatherSound(mixer);
        var air = new Air();
        var rain = new Rain(air.Cover);
        var block = new float[Rate / 60 * 2];
        var all = new List<float>();
        for (var t = 0; t < 60 * 10; t++)
        {
            tick(air, rain, sound, world);
            mixer.Mix(block);
            all.AddRange(block);
        }

        File.WriteAllBytes(path, WavWriter.Encode(all.ToArray(), Rate));
    }
}
