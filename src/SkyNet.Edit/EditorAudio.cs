using RetroEng.Audio;
using SkyNet.Formats;
using SkyNet.Game.Audio;

namespace SkyNet.Edit;

/// <summary>
/// The editor's sound output (editor-tools plan, T4): the engine's device and mixer, one sample voice
/// at a time and the game's <see cref="MusicPlayer"/> (HMI through its MIDI synth). Made on first use;
/// offscreen the device is muted (mixed and dropped, so voices still end on time).
/// <code>
///   SoundViewer ─► Play(sound) ─┐
///   MusicViewer ─► Song(name) ──┴─► Mixer ─► AudioDevice (speakers, or muted offscreen)
/// </code>
/// </summary>
public sealed class EditorAudio(Output output, Func<string, byte[]?> readSong) : IDisposable
{
    private AudioDevice? _device;
    private MusicPlayer? _music;
    private VoiceHandle _voice = VoiceHandle.None;
    private Sound? _playing;
    private double _started, _songStarted;
    private double _now;

    /// <summary>Whether a device plays (false: muted).</summary>
    public bool Audible => Device.IsOpen;

    private AudioDevice Device => _device ??= new AudioDevice(output);

    private MusicPlayer Music => _music ??= new MusicPlayer(Device.Mixer, readSong);

    /// <summary>Once a frame: the clock, and a muted device mixes the time that passed.</summary>
    public void Tick(double now, float delta)
    {
        _now = now;
        _device?.Update(delta);
    }

    /// <summary>Plays <paramref name="sound"/> alone (the last one stops).</summary>
    public void Play(Sound sound)
    {
        Stop();
        (_voice, _playing, _started) = (Device.Mixer.Play(sound, bus: Bus.Interface), sound, _now);
    }

    public void Stop()
    {
        if (_device != null && !_voice.IsNone)
        {
            _device.Mixer.Stop(_voice);
        }

        (_voice, _playing) = (VoiceHandle.None, null);
    }

    /// <summary>The sample playing and how far in (0-1); null when none.</summary>
    public (Sound Sound, float Done)? Sample()
    {
        if (_playing is not { } sound || _device == null || !_device.Mixer.IsPlaying(_voice))
        {
            return null;
        }

        var seconds = (double)sound.Frames / sound.SampleRate;
        return (sound, seconds > 0 ? (float)Math.Clamp((_now - _started) / seconds, 0, 1) : 1f);
    }

    /// <summary>A song by file name, parsed; null when it is missing or does not parse.</summary>
    public HmiFile? Song(string name) => Music.Song(name);

    /// <summary>Plays <paramref name="song"/> looping (the one playing stops).</summary>
    public void PlaySong(string song)
    {
        Music.Play(song);
        _songStarted = _now;
    }

    public void StopSong() => _music?.Stop();

    /// <summary>The song playing and the seconds since it started (looped onto its length); null when none.</summary>
    public (string Name, double Seconds)? SongAt()
    {
        if (_music?.Current is not { } name || Song(name) is not { Rate: > 0 } song)
        {
            return null;
        }

        var length = (double)song.Length / song.Rate;
        var elapsed = _now - _songStarted;
        return (name, length > 0 ? elapsed % length : elapsed);
    }

    public void Dispose()
    {
        _music?.Dispose();
        _device?.Dispose();
    }
}
