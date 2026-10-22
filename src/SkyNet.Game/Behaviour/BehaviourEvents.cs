using System.Globalization;
using System.Numerics;

namespace SkyNet.Game.Behaviour;

/// <summary>An 0xF0 doorway taken: the map to load (0 = back to the previous map) and the marker
/// set the player is placed at. The map switch itself is the caller's.</summary>
public readonly record struct ExitRequest(int Source, int TargetMap, int MarkerSet)
{
    public bool Back => TargetMap == 0;
}

/// <summary>A record's hit points ran out (or a chain broke it): the blast, the sound and the drop
/// of destruction type <see cref="DestroyType"/> (table 0x423d6) belong to the caller.</summary>
public readonly record struct DestroyEvent(int Source, Vector3 Position, int DestroyType, int DestroyParam, bool Removed);

/// <summary>
/// What the level behaviour does that someone else presents: sounds by id, messages by index,
/// mission progress, map exits, moving and breaking geometry. Every method has an empty default,
/// so a listener overrides only what it shows. Positions are in the level's axes.
/// </summary>
public interface IBehaviourListener
{
    /// <summary>A one-shot cue (0xDB-0xEB): sound id <paramref name="soundId"/> of the 0x4ff00 table.</summary>
    void Sound(int source, int soundId, Vector3 at) { }
    /// <summary>A VOICE.PRS line (0xED).</summary>
    void Voice(int source, int voiceId, Vector3 at) { }
    /// <summary>An ambient loop (0xEE) starts / stops.</summary>
    void LoopStart(int source, int soundId, Vector3 at) { }
    void LoopStop(int source) { }
    /// <summary>A hint [G<paramref name="index"/>+1] (0x1C-0x25) to show in the message box.</summary>
    void Hint(int source, int index, string text) { }
    /// <summary>An objective [M<paramref name="index"/>+1] fired (0x26-0x2A): the counter went down
    /// to <paramref name="left"/>; DOS plays sound 0x51 and prints <paramref name="text"/> when it is longer than 2.</summary>
    void Objective(int source, int index, int left, string text) { }
    /// <summary>The objective counter reached 0: the mission is won.</summary>
    void MissionComplete() { }
    /// <summary>Act 0x2B: the mission is lost at once.</summary>
    void MissionFailed(int source) { }
    void Exit(in ExitRequest request) { }
    /// <summary>A mover sets off: <paramref name="travel"/> units (or 11-bit angle units) along its
    /// axis, at <paramref name="speed"/> per second (0 = instant).</summary>
    void MoverStart(int source, MoverFamily family, int axis, float travel, float speed) { }
    void MoverStop(int source) { }
    /// <summary>A continuous rotator starts turning.</summary>
    void Spin(int source) { }
    /// <summary>A TRANSFRM.PRS wreck shows stage <paramref name="stage"/> of <paramref name="stages"/>.</summary>
    void Stage(int source, int stage, int stages) { }
    void Destroyed(in DestroyEvent e) { }
    /// <summary>Act 0x1B: the record is killed through the normal destruction path.</summary>
    void Demolish(int source, float hp) { }
    /// <summary>A variant-2 light changed: on/off and its intensity; <paramref name="announce"/> on
    /// the first step of a run (a flicker steps every 1/20 s while its bit is up).</summary>
    void Light(int source, string op, bool on, int intensity, bool announce) { }
    /// <summary>Water acts 0xD6-0xDA: the surface goes to <paramref name="target"/> (level Y),
    /// absolute or as a delta.</summary>
    void Water(int source, float target, bool absolute) { }
    /// <summary>0xF3: the robot built hidden at this sprite comes out.</summary>
    void Spawn(int source, int param) { }
    /// <summary>A chain reached a path vehicle's head marker: the vehicle drives.</summary>
    void PathStart(int source) { }
    /// <summary>A path vehicle (its actor marker) ran its path out and fired the record at its end.</summary>
    void PathEnd(int source) { }
    /// <summary>A walk-on pad was stood on.</summary>
    void Pad(int source) { }
}

/// <summary>
/// A listener that writes what happens in the Godot trigger graph's vocabulary
/// (<c>snd40</c>, <c>exit214/0</c>, <c>obj0</c>, <c>move@078f7:slideX-128</c>, …), for tests and
/// logs. Optionally forwards to another listener.
/// </summary>
public sealed class EffectLog(IBehaviourListener? next = null) : IBehaviourListener
{
    private readonly IBehaviourListener? _next = next;

    public List<string> Effects { get; } = [];

    /// <summary>Takes what was logged so far.</summary>
    public List<string> Drain()
    {
        var copy = new List<string>(Effects);
        Effects.Clear();
        return copy;
    }

    private static string Hex(int off) => off.ToString("x5", CultureInfo.InvariantCulture);

    public void Sound(int source, int soundId, Vector3 at) { Effects.Add($"snd{soundId}"); _next?.Sound(source, soundId, at); }
    public void Voice(int source, int voiceId, Vector3 at) { Effects.Add($"voice{voiceId}"); _next?.Voice(source, voiceId, at); }
    public void LoopStart(int source, int soundId, Vector3 at) { Effects.Add($"loop{soundId}"); _next?.LoopStart(source, soundId, at); }
    public void LoopStop(int source) => _next?.LoopStop(source);
    public void Hint(int source, int index, string text) { Effects.Add($"hint{index}"); _next?.Hint(source, index, text); }
    public void Objective(int source, int index, int left, string text) { Effects.Add($"obj{index}"); _next?.Objective(source, index, left, text); }
    public void MissionComplete() => _next?.MissionComplete();
    public void MissionFailed(int source) { Effects.Add("fail"); _next?.MissionFailed(source); }
    public void Exit(in ExitRequest r) { Effects.Add($"exit{(r.Back ? "back" : r.TargetMap.ToString(CultureInfo.InvariantCulture))}/{r.MarkerSet}"); _next?.Exit(r); }

    public void MoverStart(int source, MoverFamily family, int axis, float travel, float speed)
    {
        var fam = family switch
        {
            MoverFamily.Slide => "slide",
            MoverFamily.Swing => "swing",
            MoverFamily.Jump => "jump",
            MoverFamily.Slide5F => "slide5f",
            _ => "rot",
        };
        var t = MathF.Round(travel);
        Effects.Add($"move@{Hex(source)}:{fam}{"XYZ"[Math.Clamp(axis, 0, 2)]}{(t < 0 ? "-" : "+")}{Math.Abs(t).ToString("0", CultureInfo.InvariantCulture)}");
        _next?.MoverStart(source, family, axis, travel, speed);
    }

    public void MoverStop(int source) => _next?.MoverStop(source);
    public void Spin(int source) { Effects.Add($"spin@{Hex(source)}"); _next?.Spin(source); }
    public void Stage(int source, int stage, int stages) { Effects.Add($"break@{Hex(source)}:{stage}"); _next?.Stage(source, stage, stages); }
    public void Destroyed(in DestroyEvent e) { Effects.Add($"destroyed@{Hex(e.Source)}"); _next?.Destroyed(e); }
    public void Demolish(int source, float hp) { Effects.Add($"demolish@{Hex(source)}"); _next?.Demolish(source, hp); }
    public void Light(int source, string op, bool on, int intensity, bool announce)
    {
        if (announce)
        {
            Effects.Add($"light_{op}@{Hex(source)}");
        }

        _next?.Light(source, op, on, intensity, announce);
    }

    public void Water(int source, float target, bool absolute)
    {
        Effects.Add(absolute ? "waterabs" : $"water{(target <= 0 ? "+" : "-")}{Math.Abs(target).ToString("0", CultureInfo.InvariantCulture)}");
        _next?.Water(source, target, absolute);
    }

    public void Spawn(int source, int param) { Effects.Add($"spawn@{Hex(source)}"); _next?.Spawn(source, param); }
    public void PathStart(int source) { Effects.Add($"path@{Hex(source)}"); _next?.PathStart(source); }
    public void PathEnd(int source) { Effects.Add($"path@{Hex(source)}"); _next?.PathEnd(source); }
    public void Pad(int source) => _next?.Pad(source);
}
