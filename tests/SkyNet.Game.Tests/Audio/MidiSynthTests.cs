using System.Runtime.InteropServices;
using System.Security.Cryptography;
using SkyNet.Formats;
using SkyNet.Game.Audio;

namespace SkyNet.Game.Tests.Audio;

public class MidiSynthTests
{
    private const int Rate = 44100;

    /// <summary>Two seconds at 120 ticks/s: a piano note over a pad chord, drums, a volume
    /// change, a bend and a program change.</summary>
    private static HmiEvent[] Song() =>
    [
        new(0, 0xC0, 1, 88, 0),
        new(0, 0xB0, 0, 7, 110),
        new(0, 0x90, 0, 60, 100),
        new(0, 0x90, 1, 48, 80),
        new(0, 0x90, 1, 55, 80),
        new(0, 0x90, 9, 36, 120),
        new(30, 0x90, 9, 42, 90),
        new(60, 0x80, 0, 60, 0),
        new(60, 0x90, 9, 38, 110),
        new(60, 0xE0, 1, 12000, 0),
        new(90, 0x90, 9, 42, 90),
        new(120, 0xC0, 0, 40, 0),
        new(120, 0x90, 0, 64, 90),
        new(180, 0x80, 1, 48, 0),
        new(180, 0x80, 1, 55, 0),
        new(200, 0x80, 0, 64, 0),
    ];

    private static float[] Render(double seconds, bool loop = false)
    {
        var synth = new MidiSynth(Rate) { Loop = loop };
        synth.Play(Song(), 240, 120);
        var buffer = new float[(int)(seconds * Rate) * 2];
        synth.Render(buffer);
        return buffer;
    }

    private static double Rms(ReadOnlySpan<float> s)
    {
        double sum = 0;
        foreach (var v in s)
        {
            sum += v * v;
        }

        return Math.Sqrt(sum / Math.Max(1, s.Length));
    }

    private static string Hash(float[] s) => Convert.ToHexStringLower(SHA256.HashData(MemoryMarshal.AsBytes(s.AsSpan())));

    [Fact]
    public void RendersTheSameSamplesEveryTime() => Assert.Equal(Hash(Render(2)), Hash(Render(2)));

    [Fact]
    public void ChunkSizeDoesNotChangeTheOutput()
    {
        var whole = Render(1);
        var synth = new MidiSynth(Rate) { Loop = false };
        synth.Play(Song(), 240, 120);
        var pieces = new float[whole.Length];
        for (var at = 0; at < pieces.Length; at += 2 * 333)
        {
            synth.Render(pieces.AsSpan(at, Math.Min(2 * 333, pieces.Length - at)));
        }

        Assert.Equal(Hash(whole), Hash(pieces));
    }

    [Fact]
    public void SoundsWhileNotesPlayAndFadesAfter()
    {
        var s = Render(3);
        Assert.All(s, v => Assert.True(float.IsFinite(v)));
        Assert.InRange(Rms(s.AsSpan(0, Rate)), 0.02, 1.0);
        // Everything is off at tick 200 (1.67 s); the slowest release is 0.7 s.
        var tail = Rms(s.AsSpan((int)(2.6 * Rate) * 2));
        Assert.True(tail < 1e-6, $"tail rms {tail}");
        // Mono into both channels.
        for (var i = 0; i < s.Length; i += 2)
        {
            Assert.Equal(s[i], s[i + 1]);
        }
    }

    [Fact]
    public void LoopingSongStartsAgain()
    {
        var s = Render(5, loop: true);
        // The second pass starts at 2 s with the piano and the kick.
        Assert.True(Rms(s.AsSpan(2 * Rate * 2, Rate / 2)) > 0.02);
        Assert.True(Rms(s.AsSpan(4 * Rate * 2, Rate / 2)) > 0.02);
    }

    [Fact]
    public void ToneLoopsAWholeNumberOfPeriods()
    {
        var (samples, loopBegin) = Instruments.BuildTone(Instruments.Family(48));
        Assert.Equal(Instruments.LoopLength, samples.Length - loopBegin);
        Assert.Equal(260.0, Instruments.LoopHz, 9);
        // The seam is smooth: the step across it is a normal step of the wave.
        Assert.InRange(Math.Abs(samples[loopBegin] - samples[^1]), 0, 0.2);
    }

    [Fact]
    public void InstrumentBuildsAreDeterministicAndInRange()
    {
        for (var p = 0; p < 128; p += 8)
        {
            Assert.Equal(Instruments.BuildProgram(p).Samples, Instruments.BuildProgram(p).Samples);
        }

        foreach (var note in new[] { 35, 37, 38, 39, 41, 42, 46, 49, 51, 53, 54, 56, 60 })
        {
            var drum = Instruments.BuildDrum(note);
            Assert.Equal(drum.Samples, Instruments.BuildDrum(note).Samples);
            Assert.All(drum.Samples, v => Assert.InRange(v, -1f, 1f));
        }
    }
}
