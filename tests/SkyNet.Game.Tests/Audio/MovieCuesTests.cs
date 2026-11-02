using RetroEng.Audio;
using SkyNet.Formats;
using SkyNet.Game.Audio;
using SkyNet.Game.Tests.Behaviour;

namespace SkyNet.Game.Tests.Audio;

/// <summary>Future Shock's BEGIN and END carry no sound blocks; DOS (0x143fba) plays their RAW cues
/// from the table at 0x3559e as the frames come. Skipped without both games' data.</summary>
public class MovieCuesTests
{
    public static TheoryData<string> Movies => ["BEGIN", "END"];

    private const string NoData = "no Future Shock data beside SKYNET_DATA's SkyNET folder";

    private static bool HasData => BehaviourData.Dir is { } dir && GameDataLocator.OtherGameDir(dir) != null;

    private sealed class ShockDataFactAttribute : FactAttribute
    {
        public ShockDataFactAttribute()
        {
            if (!HasData)
            {
                Skip = NoData;
            }
        }
    }

    private sealed class ShockDataTheoryAttribute : TheoryAttribute
    {
        public ShockDataTheoryAttribute()
        {
            if (!HasData)
            {
                Skip = NoData;
            }
        }
    }

    private static GameData FutureShock() =>
        GameData.Open(GameDataLocator.OtherGameDir(BehaviourData.Dir!)!, BehaviourData.Dir)!;

    [ShockDataTheory]
    [MemberData(nameof(Movies))]
    public void Future_shock_movies_have_sound(string movie)
    {
        var data = FutureShock();
        using var archive = data.OpenArchive(SoundBank.Archive);
        using var vid = VidFile.Open(File.OpenRead(data.PathOf(movie + ".VID")))!;
        Assert.NotNull(vid);
        Assert.Equal(0, vid.AudioRate);

        var track = MovieCues.Soundtrack(movie, vid, n => SoundBank.Decode(n, archive.Read(n) ?? []));
        Assert.NotNull(track);
        Assert.Equal(vid.Seconds, track.Frames / (double)track.SampleRate, 1);
        Assert.Contains(track.Samples, v => MathF.Abs(v) > 0.1f);
    }

    // BEGIN's first cue (frame 1, BAMSTRET.RAW at full volume) starts with the movie; the others are missing here.
    [ShockDataFact]
    public void Cues_start_on_their_frames()
    {
        var data = FutureShock();
        using var archive = data.OpenArchive(SoundBank.Archive);
        using var vid = VidFile.Open(File.OpenRead(data.PathOf("BEGIN.VID")))!;
        var first = SoundBank.Decode("BAMSTRET.RAW", archive.Read("BAMSTRET.RAW")!)!;
        var track = MovieCues.Soundtrack("BEGIN", vid, n => n.Equals("BAMSTRET.RAW", StringComparison.OrdinalIgnoreCase) ? first : null)!;

        Assert.Equal(first.Samples[..first.Frames], track.Samples[..first.Frames]);
        Assert.All(track.Samples[first.Frames..], v => Assert.Equal(0f, v));
    }

    [ShockDataFact]
    public void Other_movies_have_no_cues()
    {
        var data = FutureShock();
        using var vid = VidFile.Open(File.OpenRead(data.PathOf("LOGO.VID")))!;
        Assert.Null(MovieCues.Soundtrack("LOGO", vid, _ => new Sound(new float[10], 1, SoundBank.RawRate, Looping.Once)));
    }
}
