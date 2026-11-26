using MeltySynth;
using SkyNet.Formats;

namespace SkyNet.Game.Audio;

/// <summary>One channel message as MeltySynth takes it (status high nibble, two data bytes).</summary>
internal readonly record struct MidiMessage(int Channel, int Command, int Data1, int Data2);

/// <summary>
/// MUSIC = SOUNDFONT (issue #70): the songs' events through MeltySynth and a user .sf2, on the
/// same clock as <see cref="MidiSynth"/> (<see cref="HmiSequencer"/>, a step every
/// <see cref="MidiSynth.ControlFrames"/> frames). The songs are General MIDI (SC-55 era): channel 10
/// (index 9) is drums, MeltySynth's percussion bank; its programs pick GS kits (25: TR-808, 48:
/// orchestra). What goes through: notes, programs, pitch bend, the controllers
/// <see cref="MidiSynth"/> honours (7, 11, 120, 121, 123) and the GM ones it cannot (1 modulation,
/// 10 pan, 64 sustain, 91 reverb, 93 chorus), which the songs use. HMI's private controllers
/// (103-119), bank select and tempo metas are dropped.
/// <code>
///   HmiSequencer ──► ToMidi ──► Synthesizer (MeltySynth, .sf2) ──► left / right ──► interleaved
/// </code>
/// <para>Single-threaded and deterministic: the same events give the same samples.</para>
/// </summary>
public sealed class SoundFontSynth : IMusicSynth, IMidiSink
{
    private const int NoteOn = 0x90;
    private const int NoteOff = 0x80;
    private const int Controller = 0xB0;
    private const int Program = 0xC0;
    private const int PitchBend = 0xE0;
    private const int SevenBits = 0x7F;
    private const int CcModulation = 1;
    private const int CcVolume = 7;
    private const int CcPan = 10;
    private const int CcSustain = 64;
    private const int CcReverb = 91;
    private const int CcChorus = 93;
    private const int CcExpression = 11;
    private const int CcSoundOff = 120;
    private const int CcResetControllers = 121;
    private const int CcNotesOff = 123;
    /// <summary>MeltySynth's level (its default 0.5, +8 dB) so a song sounds about as loud as
    /// <see cref="MidiSynth"/>'s: GeneralUser GS over T200.HMI, RMS -21 dBFS both.</summary>
    private const float MasterVolume = 1.25f;

    private readonly Synthesizer _synth;
    private readonly HmiSequencer _sequencer = new();
    private readonly float[] _left = new float[MidiSynth.ControlFrames];
    private readonly float[] _right = new float[MidiSynth.ControlFrames];
    private int _controlLeft;

    public SoundFontSynth(SoundFont font, int sampleRate = 44100)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        SampleRate = sampleRate;
        _synth = new Synthesizer(font, sampleRate) { MasterVolume = MasterVolume };
    }

    /// <summary>Output frames per second.</summary>
    public int SampleRate { get; }

    public bool Loop { get => _sequencer.Loop; set => _sequencer.Loop = value; }

    /// <summary>Reads a .sf2 (SoundFont 2; MeltySynth does not take SF3); throws when it does not read.</summary>
    public static SoundFont Parse(byte[] sf2)
    {
        using var stream = new MemoryStream(sf2, writable: false);
        return new SoundFont(stream);
    }

    public void Play(HmiFile song) => Play(song.Events.AsSpan(), song.Length, song.Rate);

    /// <summary>Starts an event list (sorted by tick) of <paramref name="length"/> ticks at
    /// <paramref name="ticksPerSecond"/>.</summary>
    public void Play(ReadOnlySpan<HmiEvent> events, int length, int ticksPerSecond)
    {
        _synth.Reset();
        _controlLeft = 0;
        _sequencer.Play(events, length, ticksPerSecond);
    }

    public void Render(Span<float> stereo)
    {
        var frames = stereo.Length / 2;
        var done = 0;
        while (done < frames)
        {
            if (_controlLeft == 0)
            {
                _sequencer.Advance((double)MidiSynth.ControlFrames / SampleRate, this);
                _controlLeft = MidiSynth.ControlFrames;
            }

            var run = Math.Min(_controlLeft, frames - done);
            _synth.Render(_left.AsSpan(0, run), _right.AsSpan(0, run));
            for (var f = 0; f < run; f++)
            {
                stereo[(done + f) * 2] = _left[f];
                stereo[(done + f) * 2 + 1] = _right[f];
            }

            done += run;
            _controlLeft -= run;
        }
    }

    /// <summary>The MIDI message of an HMI event; null for what is dropped.
    /// A bend's 14-bit value splits into its low and high 7 bits (12000 → 96, 93).</summary>
    internal static MidiMessage? ToMidi(in HmiEvent e)
    {
        var ch = e.Channel & 0x0F;
        return e.Kind switch
        {
            NoteOn or NoteOff => new MidiMessage(ch, e.Kind, e.Data1, e.Data2),
            Program => new MidiMessage(ch, Program, e.Data1, 0),
            PitchBend => new MidiMessage(ch, PitchBend, e.Data1 & SevenBits, (e.Data1 >> 7) & SevenBits),
            Controller when e.Data1 is CcModulation or CcVolume or CcExpression or CcPan or CcSustain or CcReverb or CcChorus
                or CcSoundOff or CcResetControllers or CcNotesOff =>
                new MidiMessage(ch, Controller, e.Data1, e.Data2),
            _ => null,
        };
    }

    void IMidiSink.Dispatch(in HmiEvent e)
    {
        if (ToMidi(e) is not { } m)
        {
            return;
        }

        _synth.ProcessMidiMessage(m.Channel, m.Command, m.Data1, m.Data2);
    }

    /// <summary>The song's end: notes released (they ring out), as <see cref="MidiSynth"/> does.</summary>
    void IMidiSink.AllNotesOff() => _synth.NoteOffAll(immediate: false);
}
