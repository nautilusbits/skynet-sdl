using RetroEng.Audio;
using SkyNet.Formats;
using SkyNet.Game.Audio;

namespace SkyNet.Game.Tests.Audio;

public class MusicTests
{
    private const int Rate = 44100;

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
    public void MapTypeTablePicksTheSong()
    {
        Assert.Equal("T200.HMI", MusicTracks.ForMapType(0));
        Assert.Equal("T205.HMI", MusicTracks.ForMapType(1));
        Assert.Equal("T305.HMI", MusicTracks.ForMapType(11));
        Assert.Equal("T200.HMI", MusicTracks.ForMapType(12));
        Assert.Equal("T200.HMI", MusicTracks.ForMapType(-1));
    }

    [AudioDataFact]
    public void FirstSongRendersTwoSecondsOfSound()
    {
        var data = AudioTestData.Open();
        var song = HmiFile.Parse(data.ReadLoose(MusicTracks.ByMapType[0])!)!;
        var synth = new MidiSynth(Rate);
        synth.Play(song);
        var buffer = new float[Rate * 2 * 2];
        synth.Render(buffer);
        Assert.All(buffer, v => Assert.True(float.IsFinite(v)));
        Assert.True(Rms(buffer) > 0.005, $"silent: rms {Rms(buffer)}");
    }

    [AudioDataFact]
    public void EveryTrackParses()
    {
        var data = AudioTestData.Open();
        foreach (var name in MusicTracks.ByMapType.Append(MusicTracks.Title))
        {
            Assert.NotNull(HmiFile.Parse(data.ReadLoose(name)!));
        }
    }

    [AudioDataFact]
    public void EverySoundIdLoads()
    {
        var data = AudioTestData.Open();
        using var archive = data.OpenArchive(SoundBank.Archive);
        using var bank = SoundBank.Open(new Mixer(), data)!;
        // pings.wav is named by the table but not shipped in the archive.
        var present = Enumerable.Range(0, SoundIds.Names.Count).Where(id => archive.Contains(SoundIds.NameOf(id)!)).ToList();
        Assert.True(present.Count >= SoundIds.Names.Count - 1, $"{SoundIds.Names.Count - present.Count} sounds missing");
        Assert.All(present, id => Assert.NotNull(bank.Get(id)));
    }

    [AudioDataFact]
    public void PlayerFeedsTheMusicBusWithoutUnderruns()
    {
        var data = AudioTestData.Open();
        var mixer = new Mixer(Rate);
        using var music = new MusicPlayer(mixer, data);
        music.PlayForMapType(0);
        Assert.Equal("T200.HMI", music.Current);

        // Pull 3 s at real time, as the audio thread would.
        var block = new float[Mixer.BlockFrames * 2];
        var start = DateTime.UtcNow;
        var deadline = start.AddSeconds(15);
        double sum = 0;
        var mixed = 0;
        while (mixed < Rate * 3 && DateTime.UtcNow < deadline)
        {
            var due = (DateTime.UtcNow - start).TotalSeconds * Rate;
            if (mixed > due)
            {
                Thread.Sleep(1);
                continue;
            }

            // A device pulls one block at a time: when this thread was held up, the time it lost is
            // not pulled in one burst (that would drain the ring faster than real time).
            if (due - mixed > Mixer.BlockFrames * 2)
            {
                start = DateTime.UtcNow.AddSeconds(-(double)mixed / Rate);
            }

            mixer.Mix(block);
            mixed += Mixer.BlockFrames;
            foreach (var v in block)
            {
                sum += v * v;
            }
        }

        Assert.True(sum > 0, "no music reached the mixer");
        Assert.Equal(0, music.Underruns);
        music.Stop();
        Assert.Null(music.Current);
    }
}
