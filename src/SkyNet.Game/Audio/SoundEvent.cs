using System.Numerics;

namespace SkyNet.Game.Audio;

/// <summary>What a <see cref="SoundEvent"/> reports.</summary>
public enum SoundEventKind
{
    /// <summary>A one-shot without a place.</summary>
    Play2D,
    /// <summary>A one-shot at a place.</summary>
    Play3D,
    /// <summary>A looping sound at a place was set up (it plays while the listener is near).</summary>
    LoopStart,
    /// <summary>That loop was stopped for good.</summary>
    LoopStop,
    /// <summary>The ambient bed started.</summary>
    Ambient,
}

/// <summary>One request the <see cref="SoundBank"/> took, for a trace (<see cref="SoundBank.Trace"/>):
/// the sound's file, its DOS id (−1 when asked by name), where (null for 2D) and the gain asked for
/// before any distance fade. Reported whether or not a voice was free; <see cref="Played"/> says
/// whether one started.</summary>
public readonly record struct SoundEvent(SoundEventKind Kind, string Name, int Id, Vector3? Position, float Gain, bool Played)
{
    public override string ToString()
    {
        var at = Position is { } p ? FormattableString.Invariant($" at ({p.X:0},{p.Y:0},{p.Z:0})") : "";
        var id = Id >= 0 ? FormattableString.Invariant($" #{Id}") : "";
        return FormattableString.Invariant($"{Kind} {Name}{id}{at} gain {Gain:0.###}{(Played ? "" : " (not played)")}");
    }
}
