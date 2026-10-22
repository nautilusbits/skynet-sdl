using System.Numerics;
using RetroEng.Audio;

namespace SkyNet.Game.Audio;

/// <summary>
/// The weather's sounds on the mixer's Effects bus (SOUND volume, paused with the game): a storm's
/// thunder towards its bolt, the rain's hiss and the wind's howl as loops round the ears. Called from
/// one thread (the simulation's).
/// </summary>
public sealed class WeatherSound(Mixer mixer)
{
    /// <summary>A thunder is heard at full level wherever the ears are (its distance is in its gain).</summary>
    public const float ThunderReach = 1e6f;
    /// <summary>Below every game sound when the voices run out.</summary>
    public const int ThunderPriority = -1;
    /// <summary>A loop under this gain is stopped.</summary>
    public const float Silent = 0.001f;

    private readonly Bed _rain = new(mixer), _wind = new(mixer);

    /// <summary>Plays a thunder at <paramref name="at"/> (world units) at <paramref name="gain"/> (0-1).</summary>
    public void Thunder(Sound sound, Vector3 at, float gain) =>
        mixer.Play(sound, new Emitter3D(at, ThunderReach, ThunderReach * 2f, Rolloff.Linear, gain, Looping.Once, 1f), ThunderPriority, Bus.Effects);

    /// <summary>The rain's hiss: <paramref name="sound"/> looped at <paramref name="gain"/> (0-1); a gain
    /// near 0 or no sound stops it. Started again when its voice was taken.</summary>
    public void Rain(Sound? sound, float gain) => _rain.Set(sound, gain, 1f);

    /// <summary>The wind's howl, as <see cref="Rain"/>, at <paramref name="pitch"/> (1: as made).</summary>
    public void Wind(Sound? sound, float gain, float pitch) => _wind.Set(sound, gain, pitch);

    /// <summary>Whether the rain's hiss plays.</summary>
    public bool Raining => _rain.Playing;

    /// <summary>Whether the wind's howl plays.</summary>
    public bool Howling => _wind.Playing;

    /// <summary>One looped bed round the ears.</summary>
    private sealed class Bed(Mixer mixer)
    {
        private VoiceHandle _voice = VoiceHandle.None;
        private Sound? _sound;

        public bool Playing => mixer.IsPlaying(_voice);

        public void Set(Sound? sound, float gain, float pitch)
        {
            if (sound == null || gain < Silent)
            {
                mixer.Stop(_voice);
                (_voice, _sound) = (VoiceHandle.None, null);
                return;
            }

            if (!ReferenceEquals(sound, _sound) || !mixer.IsPlaying(_voice))
            {
                mixer.Stop(_voice);
                var loop = sound.Looping == Looping.Forever ? sound : new Sound(sound.Samples, sound.Channels, sound.SampleRate, Looping.Forever);
                (_voice, _sound) = (mixer.Play(loop, gain, pitch: pitch, bus: Bus.Effects), sound);
                return;
            }

            mixer.SetVolume(_voice, gain);
            mixer.SetPitch(_voice, pitch);
        }
    }
}
