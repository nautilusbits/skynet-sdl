using SkyNet.Formats;

namespace SkyNet.Game.Audio;

/// <summary>What a music synth does with the events <see cref="HmiSequencer"/> hands it.</summary>
internal interface IMidiSink
{
    /// <summary>One due event (note, controller, program, bend).</summary>
    void Dispatch(in HmiEvent e);

    /// <summary>The song reached its end: every held note is let go.</summary>
    void AllNotesOff();
}

/// <summary>
/// The song clock both music synths share (<see cref="MidiSynth"/>, <see cref="SoundFontSynth"/>):
/// events tick at the song's timer rate (HMI's 120 Hz, never PPQN × tempo); each step hands the
/// due ones to a sink. At the end every note is let go and the song starts again (or stops).
/// </summary>
internal sealed class HmiSequencer
{
    private const int DefaultRate = 120;

    private HmiEvent[] _events = [];
    private int _pos;
    private double _tick;
    private double _ticksPerSecond = DefaultRate;
    private int _length;

    /// <summary>Whether the song starts again at its end; off, it stops there.</summary>
    public bool Loop { get; set; } = true;
    /// <summary>A song is being sequenced.</summary>
    public bool Playing { get; private set; }
    /// <summary>The song position in ticks.</summary>
    public double Tick => _tick;

    /// <summary>Starts an event list (sorted by tick) of <paramref name="length"/> ticks; false
    /// when there is nothing to play.</summary>
    public bool Play(ReadOnlySpan<HmiEvent> events, int length, int ticksPerSecond)
    {
        _events = events.ToArray();
        _length = length;
        _ticksPerSecond = ticksPerSecond > 0 ? ticksPerSecond : DefaultRate;
        (_pos, _tick) = (0, 0);
        Playing = _events.Length > 0 && _length > 0;
        return Playing;
    }

    public void Stop() => Playing = false;

    /// <summary>Moves the song on by <paramref name="delta"/> seconds and dispatches what is due.</summary>
    public void Advance(double delta, IMidiSink sink)
    {
        if (!Playing)
        {
            return;
        }

        _tick += delta * _ticksPerSecond;
        while (_pos < _events.Length && _events[_pos].Tick <= _tick)
        {
            sink.Dispatch(_events[_pos]);
            _pos++;
        }

        if (_pos < _events.Length)
        {
            return;
        }

        sink.AllNotesOff();
        if (!Loop)
        {
            Playing = false;
            return;
        }

        _pos = 0;
        _tick -= _length;
        if (_tick < 0 || _tick > _ticksPerSecond)
        {
            _tick = 0;
        }
    }
}
