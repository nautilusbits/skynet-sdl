using SkyNet.Formats;
using SkyNet.Game.Audio;

namespace SkyNet.Game.Tests.Audio;

/// <summary>MUSIC = SOUNDFONT (issue #70): the HMI events as MIDI messages, the synth over a
/// tiny .sf2, and the .sf2 files found in the soundfonts folder.</summary>
public class SoundFontSynthTests : IDisposable
{
    private const int Rate = 44100;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "skynet-sf2-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }
    }

    [Theory]
    [InlineData(0x90, 3, 60, 100, 0x90, 60, 100)]
    [InlineData(0x90, 9, 36, 120, 0x90, 36, 120)]
    [InlineData(0x80, 3, 60, 0, 0x80, 60, 0)]
    [InlineData(0xC0, 2, 40, 0, 0xC0, 40, 0)]
    [InlineData(0xB0, 1, 7, 90, 0xB0, 7, 90)]
    [InlineData(0xB0, 1, 11, 64, 0xB0, 11, 64)]
    [InlineData(0xB0, 1, 120, 0, 0xB0, 120, 0)]
    [InlineData(0xB0, 1, 121, 0, 0xB0, 121, 0)]
    [InlineData(0xB0, 1, 123, 0, 0xB0, 123, 0)]
    [InlineData(0xB0, 1, 10, 20, 0xB0, 10, 20)]
    [InlineData(0xB0, 1, 64, 127, 0xB0, 64, 127)]
    [InlineData(0xB0, 1, 91, 40, 0xB0, 91, 40)]
    [InlineData(0xB0, 1, 93, 10, 0xB0, 93, 10)]
    [InlineData(0xB0, 1, 1, 30, 0xB0, 1, 30)]
    public void EventsMapToMidi(int kind, int ch, int d1, int d2, int command, int m1, int m2)
    {
        var m = SoundFontSynth.ToMidi(new HmiEvent(0, kind, ch, d1, d2));
        Assert.Equal(new MidiMessage(ch, command, m1, m2), m);
    }

    [Fact]
    public void BendSplitsIntoSevenBitHalves()
    {
        // 12000 = 93 * 128 + 96: LSB 96, MSB 93.
        var m = SoundFontSynth.ToMidi(new HmiEvent(0, 0xE0, 4, 12000, 0));
        Assert.Equal(new MidiMessage(4, 0xE0, 96, 93), m);
    }

    [Theory]
    [InlineData(0xB0, 103)]
    [InlineData(0xB0, 105)]
    [InlineData(0xB0, 110)]
    [InlineData(0xB0, 0)]
    [InlineData(0x51, 500000)]
    public void UnhonouredEventsAreDropped(int kind, int d1) =>
        Assert.Null(SoundFontSynth.ToMidi(new HmiEvent(0, kind, 0, d1, 0)));

    private static HmiEvent[] Song() =>
    [
        new(0, 0xC0, 0, 0, 0),
        new(0, 0x90, 0, 69, 110),
        new(60, 0x80, 0, 69, 0),
    ];

    private static float[] Render(double seconds)
    {
        var synth = new SoundFontSynth(SoundFontSynth.Parse(TinySoundFont.Build()), Rate) { Loop = false };
        synth.Play(Song(), 120, 120);
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

    [Fact]
    public void NoteSoundsThenFades()
    {
        var buffer = Render(2);
        Assert.All(buffer, v => Assert.True(float.IsFinite(v)));

        // Sounding while the note is held (0-0.5 s), silent long after its note-off.
        var held = buffer.AsSpan(Rate / 10 * 2, Rate / 5 * 2);
        var late = buffer.AsSpan(Rate * 3 / 2 * 2, Rate / 5 * 2);
        Assert.True(Rms(held) > 0.01, $"silent: rms {Rms(held)}");
        Assert.True(Rms(late) < Rms(held) / 100, $"still sounding: rms {Rms(late)}");
    }

    [Fact]
    public void SameEventsGiveSameSamples() => Assert.Equal(Render(0.5), Render(0.5));

    [Theory]
    [InlineData("b.sf2", true)]
    [InlineData("Roland.SC-55.SF2", true)]
    [InlineData("x/../a.sf2", false)]
    [InlineData("../a.sf2", false)]
    [InlineData("..sf2", false)]
    [InlineData("a/b.sf2", false)]
    [InlineData("a\b.sf2", false)]
    [InlineData("/a.sf2", false)]
    [InlineData("C:a.sf2", false)]
    [InlineData(".sf2", false)]
    [InlineData("a.txt", false)]
    [InlineData("", false)]
    public void IdsAreChecked(string id, bool valid) => Assert.Equal(valid, SoundFonts.IsId(id));

    [Theory]
    [InlineData("GeneralUser-GS.sf2", "GeneralUser-GS.sf2")]
    [InlineData("skynet-soundfont/Roland.SC-55.sf2", "Roland.SC-55.sf2")]
    [InlineData("x/../a.sf2", "")]
    [InlineData("a/b/c.sf2", "")]
    [InlineData("/a.sf2", "")]
    [InlineData("a/b.txt", "")]
    [InlineData("", "")]
    public void OldModIdsMigrate(string stored, string id) => Assert.Equal(id, SoundFonts.FromSetting(stored));

    [Fact]
    public void MalformedIdReadsNull()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "x"));
        File.WriteAllBytes(Path.Combine(_dir, "a.sf2"), TinySoundFont.Build());
        File.WriteAllBytes(Path.Combine(_dir, "x", "a.sf2"), TinySoundFont.Build());
        Assert.Null(SoundFonts.Read(_dir, "x/a.sf2"));
        Assert.Null(SoundFonts.Read(_dir, "x/../a.sf2"));
        Assert.Null(SoundFonts.Read(_dir, Path.Combine(_dir, "a.sf2")));
        Assert.NotNull(SoundFonts.Read(_dir, "a.sf2"));
    }

    [Fact]
    public void OversizeFileIsRejected()
    {
        var bytes = TinySoundFont.Build();
        Directory.CreateDirectory(_dir);
        File.WriteAllBytes(Path.Combine(_dir, "a.sf2"), bytes);

        // A cap under the file's size reads null; at its size it reads.
        Assert.Null(SoundFonts.Read(_dir, "a.sf2", bytes.Length - 1));
        Assert.Equal(bytes, SoundFonts.Read(_dir, "a.sf2", bytes.Length));
        Assert.True(SoundFonts.MaxBytes >= 128L << 20);
    }

    [Fact]
    public void CorruptSf2DoesNotParse() =>
        Assert.ThrowsAny<Exception>(() => SoundFontSynth.Parse([.. TinySoundFont.Build().Take(100)]));

    [Fact]
    public void ScanSeesAFileAddedLater()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllBytes(Path.Combine(_dir, "a.sf2"), TinySoundFont.Build());
        Assert.Equal(["a.sf2"], SoundFonts.Scan(_dir));

        File.WriteAllBytes(Path.Combine(_dir, "b.sf2"), TinySoundFont.Build());
        Assert.Equal(["a.sf2", "b.sf2"], SoundFonts.Scan(_dir));
    }

    [Fact]
    public void NoFolderNoSoundFonts()
    {
        Assert.Empty(SoundFonts.Scan(_dir));
        Directory.CreateDirectory(_dir);
        Assert.Empty(SoundFonts.Scan(_dir));
    }

    [Fact]
    public void FindsFilesDirectlyInTheFolder()
    {
        var bytes = TinySoundFont.Build();
        Directory.CreateDirectory(Path.Combine(_dir, "deeper"));
        File.WriteAllBytes(Path.Combine(_dir, "Tiny.sf2"), bytes);
        File.WriteAllBytes(Path.Combine(_dir, "Roland.SC-55.sf2"), bytes);
        File.WriteAllBytes(Path.Combine(_dir, "deeper", "Nested.sf2"), bytes);
        File.WriteAllText(Path.Combine(_dir, "readme.txt"), "not a soundfont");

        var found = SoundFonts.Scan(_dir);
        Assert.Equal(["Roland.SC-55.sf2", "Tiny.sf2"], found);
        Assert.Equal(bytes, SoundFonts.Read(_dir, found[0]));
        Assert.Equal("ROLAND.SC-55", SoundFonts.Label(found[0]));
        Assert.Null(SoundFonts.Read(_dir, "Missing.sf2"));
    }
}
