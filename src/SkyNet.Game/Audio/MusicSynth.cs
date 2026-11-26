using SkyNet.Formats;

namespace SkyNet.Game.Audio;

/// <summary>GAMEPLAY's MUSIC (issue #70): the port's own synth (<see cref="MidiSynth"/>) or a
/// user .sf2 (<see cref="SoundFontSynth"/>).</summary>
public enum MusicSynth { Original, SoundFont }

/// <summary>A synth <see cref="MusicPlayer"/> renders songs with.</summary>
public interface IMusicSynth
{
    /// <summary>Whether the song starts again at its end.</summary>
    bool Loop { get; set; }

    /// <summary>Starts a song from its beginning.</summary>
    void Play(HmiFile song);

    /// <summary>Renders interleaved stereo frames (overwritten).</summary>
    void Render(Span<float> stereo);
}
