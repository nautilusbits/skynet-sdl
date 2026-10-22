using System.Numerics;
using RetroEng.Audio;
using SkyNet.Game.Audio;
using SkyNet.Game.Enhanced;

namespace SkyNet.Game.Tests.Enhanced;

public class ThunderHeardTests
{
    private const int Rate = 44100;
    private const int TicksPerSecond = 60;
    /// <summary>Small speakers give little below this: the level is measured above it.</summary>
    private const float SpeakerLowHz = 300f;
    /// <summary>The game's own sounds at full level, their loudest second above <see cref="SpeakerLowHz"/>
    /// (MDMDSFXS.BSA at the DOS volume): explo1.raw 0.11, shots5.raw 0.05.</summary>
    private const float ExplosionRms = 0.1f, GunshotRms = 0.04f;

    /// <summary>The loudest second's RMS above <see cref="SpeakerLowHz"/> of a strike at
    /// <paramref name="metres"/>, played as the game plays it: the storm's queue, the weather's voice,
    /// the mixer with the ears at the eye; the ear towards the bolt and the other.</summary>
    private static (float Near, float Far) Heard(float metres, int seed)
    {
        var mixer = new Mixer(Rate);
        var eye = new Vector3(1000f, 75f, 2000f);
        mixer.SetListener(eye, -Vector3.UnitZ, Vector3.UnitY);
        var sound = new WeatherSound(mixer);
        var storm = new Storm(seed) { Thunder = sound.Thunder };
        var table = StormTable.Defaults with { Interval = 1000f, NearMetres = metres, FarMetres = metres };
        // To the right: the far ear hears it too.
        storm.Strike(Vector3.UnitX);

        var seconds = Storm.ThunderDelay(metres * Storm.UnitsPerMetre) + 8f;
        var block = new float[Rate / TicksPerSecond * 2];
        List<float> left = [], right = [];
        for (var t = 0; t < seconds * TicksPerSecond; t++)
        {
            storm.Tick(1f / TicksPerSecond, table, mixer, StormSky.Outdoor, eye);
            mixer.Mix(block);
            for (var i = 0; i < block.Length; i += 2)
            {
                left.Add(block[i]);
                right.Add(block[i + 1]);
            }
        }

        // The ear towards the bolt, the other.
        var (l, r) = (LoudestSecond([.. left], Rate), LoudestSecond([.. right], Rate));
        return (MathF.Max(l, r), MathF.Min(l, r));
    }

    private static float LoudestSecond(float[] s, int rate)
    {
        var a = 1f - MathF.Exp(-2f * MathF.PI * SpeakerLowHz / rate);
        var low = 0f;
        var squares = new double[s.Length];
        for (var i = 0; i < s.Length; i++)
        {
            low += a * (s[i] - low);
            var high = s[i] - low;
            squares[i] = high * high;
        }

        var best = 0.0;
        for (var start = 0; start + rate <= s.Length; start += rate / 4)
        {
            var sum = 0.0;
            for (var i = start; i < start + rate; i++)
            {
                sum += squares[i];
            }

            best = Math.Max(best, sum / rate);
        }

        return (float)Math.Sqrt(best);
    }

    /// <summary>At the sample mod's thunderVolume (0.8) a near strike is as loud as an explosion, the
    /// farthest one as a gunshot, in the band small speakers play.</summary>
    [Theory]
    [InlineData(800f, 1, ExplosionRms)]
    [InlineData(800f, 2, ExplosionRms)]
    [InlineData(1500f, 6, ExplosionRms)]
    [InlineData(3000f, 3, GunshotRms)]
    [InlineData(6000f, 4, GunshotRms)]
    [InlineData(6000f, 5, GunshotRms)]
    public void AStrikesThunderIsHeard(float metres, int seed, float least)
    {
        var (near, far) = Heard(metres, seed);
        Assert.True(near >= least, $"{metres} m: {near:F4} RMS above {SpeakerLowHz} Hz, under {least}");
        Assert.True(far >= least / 4f, $"{metres} m: the far ear {far:F4}");
    }
}
