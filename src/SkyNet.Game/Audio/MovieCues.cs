using RetroEng.Audio;
using SkyNet.Formats;

namespace SkyNet.Game.Audio;

/// <summary>
/// Future Shock's BEGIN and END: VIDs without sound blocks. DOS (FUN_00143fba, from 0x11d4a9) plays
/// .RAW one-shots from MDMDSFXS.BSA as the frames come, by the {name, cue list} table at 0x3559e:
/// <code>
///   list  u32 → the names (NUL-separated, -1 ends), then 16-byte cues: i32 frame, i32 name index,
///         i32 volume (0-127), callback 0x1440ec (play once); frame -1 ends
/// </code>
/// A cue fires once its frame count has been decoded (frame 1: with the first picture). The port mixes
/// the cues into one track the movie's length, so the picture clock follows it as a VID's own sound.
/// </summary>
public static class MovieCues
{
    // A cue's volume at full scale (DOS scales it by the effects volume, the port by the bus).
    private const float MaxVolume = 127f;

    private readonly record struct Cue(int Frame, int Sound, int Volume);

    private sealed record Cues(string[] Names, Cue[] List);

    private static readonly Dictionary<string, Cues> Table = new(StringComparer.OrdinalIgnoreCase)
    {
        ["BEGIN"] = new(
            ["BAMSTRET.RAW", "BNUKE.RAW", "LFWGRV.RAW", "RFWGRV.RAW", "BHK.RAW", "LASER1.RAW", "EXPLO1.RAW",
             "TURRET.RAW", "BDTHCAMP.RAW", "GRUNT1.RAW", "CARCOLL1.RAW", "HVYFT5.RAW", "HYDRA2.RAW",
             "BAMFIRE.RAW", "BEXPLO2.RAW"],
            [
                new(1, 0, 127), new(66, 1, 127), new(102, 4, 100), new(102, 13, 127), new(185, 8, 127),
                new(309, 6, 127), new(334, 5, 100), new(336, 5, 100), new(336, 2, 127), new(339, 5, 100),
                new(339, 3, 120), new(343, 2, 110), new(347, 3, 100), new(348, 5, 100), new(351, 2, 90),
                new(352, 5, 100), new(353, 5, 100), new(355, 3, 80), new(357, 5, 100), new(358, 2, 80),
                new(361, 5, 100), new(362, 3, 90), new(366, 2, 100), new(367, 5, 100), new(369, 5, 100),
                new(370, 3, 110), new(374, 2, 120), new(375, 3, 32), new(375, 5, 100), new(377, 5, 100),
                new(379, 5, 100), new(379, 2, 32), new(380, 3, 90), new(380, 5, 100), new(384, 5, 100),
                new(384, 2, 80), new(386, 5, 100), new(388, 3, 70), new(388, 5, 100), new(392, 2, 60),
                new(393, 5, 100), new(397, 3, 100), new(407, 2, 100), new(417, 3, 100), new(427, 2, 100),
                new(435, 3, 100), new(443, 2, 100), new(454, 3, 100), new(456, 12, 127), new(457, 5, 100),
                new(461, 2, 100), new(461, 11, 127), new(461, 12, 100), new(462, 5, 100), new(466, 11, 100),
                new(467, 12, 127), new(470, 3, 100), new(470, 5, 100), new(472, 11, 127), new(473, 12, 100),
                new(478, 5, 100), new(478, 11, 100), new(479, 2, 100), new(479, 12, 127), new(483, 12, 100),
                new(484, 11, 127), new(487, 5, 100), new(488, 11, 100), new(489, 3, 50), new(490, 12, 127),
                new(495, 11, 127), new(495, 12, 100), new(496, 5, 100), new(499, 2, 50), new(500, 11, 100),
                new(501, 7, 100), new(507, 5, 100), new(511, 9, 100), new(513, 5, 100), new(525, 14, 127),
                new(533, 10, 100), new(536, 10, 100), new(539, 10, 100), new(544, 10, 100),
            ]),
        ["END"] = new(
            ["AMB_CORE.RAW", "DOORB.RAW", "LFWMET.RAW", "RFWMET.RAW", "TERMITOR.RAW", "LASER1.RAW", "EXPLO1.RAW",
             "EXPLO2.RAW", "GAUSS1.RAW", "GRUNT1.RAW", "PIPEHIT1.RAW", "BREATHIN.RAW", "SOFTTERM.RAW",
             "TURRET.RAW", "AMB_FIRE.RAW", "RUSHWIND.RAW", "AMB_WIND.RAW", "MOVGTERM.RAW", "GAUSS1.RAW",
             "AMB_CITY.RAW"],
            [
                new(1, 0, 80), new(27, 1, 127), new(36, 2, 32), new(42, 3, 36), new(46, 2, 40),
                new(51, 3, 44), new(56, 2, 48), new(61, 3, 44), new(66, 2, 40), new(68, 1, 48),
                new(71, 3, 36), new(75, 2, 32), new(76, 1, 127), new(84, 4, 100), new(93, 5, 100),
                new(96, 5, 100), new(98, 5, 100), new(101, 5, 100), new(104, 5, 100), new(107, 5, 100),
                new(109, 5, 100), new(111, 5, 100), new(111, 6, 127), new(116, 5, 100), new(117, 5, 100),
                new(121, 5, 100), new(126, 5, 100), new(127, 8, 127), new(127, 5, 100), new(132, 5, 100),
                new(136, 5, 100), new(137, 5, 100), new(137, 12, 48), new(140, 9, 64), new(142, 2, 64),
                new(147, 10, 64), new(148, 3, 64), new(153, 10, 64), new(153, 4, 16), new(161, 9, 64),
                new(179, 11, 96), new(179, 17, 64), new(208, 13, 64), new(223, 18, 127), new(234, 14, 90),
                new(257, 15, 127), new(301, 19, 127), new(330, 19, 80), new(349, 16, 100), new(399, 16, 100),
            ]),
    };

    /// <summary>The cues of <paramref name="movie"/> (BEGIN, END) mixed into one mono track at
    /// <see cref="SoundBank.RawRate"/>, the movie's length; clips past its end are cut, as DOS fades
    /// them when the pictures stop. <paramref name="sound"/> reads a clip by name (null: skipped).
    /// Null for a movie without cues.</summary>
    public static Sound? Soundtrack(string movie, VidFile vid, Func<string, Sound?> sound)
    {
        if (!Table.TryGetValue(movie, out var cues))
        {
            return null;
        }

        var track = new float[(int)(vid.Seconds * SoundBank.RawRate)];
        foreach (var cue in cues.List)
        {
            if (sound(cues.Names[cue.Sound]) is not { Channels: 1 } clip)
            {
                continue;
            }

            // Frame n fires once n frames are decoded: when frame n - 1 (from 0) shows.
            var start = (int)(vid.FrameTime(cue.Frame - 1) * SoundBank.RawRate);
            Mix(track, start, clip, cue.Volume / MaxVolume);
        }

        // Voices add up as in DOS's mixer, clipped at full scale.
        for (var i = 0; i < track.Length; i++)
        {
            track[i] = Math.Clamp(track[i], -1f, 1f);
        }

        return new Sound(track, 1, SoundBank.RawRate, Looping.Once);
    }

    // Adds a clip at start, resampled to the track's rate by the nearest sample (the clips are 11025 Hz).
    private static void Mix(float[] track, int start, Sound clip, float volume)
    {
        var step = clip.SampleRate / (double)SoundBank.RawRate;
        for (var i = start; i < track.Length; i++)
        {
            var at = (int)((i - start) * step);
            if (at >= clip.Frames)
            {
                return;
            }

            track[i] += clip.Samples[at] * volume;
        }
    }
}
