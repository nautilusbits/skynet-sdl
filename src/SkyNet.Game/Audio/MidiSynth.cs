using SkyNet.Formats;

namespace SkyNet.Game.Audio;

/// <summary>
/// The HMI sequencer and wavetable synth of the Godot port (midi_synth.gd), rendering samples
/// itself instead of driving Godot players: 40 voices, each one instrument sample played at
/// note Hz / sample Hz (linear interpolation), mono to both channels at
/// velocity dB + channel dB + <see cref="GainDb"/>. Events tick at the song's timer rate (HMI's
/// 120 Hz, never PPQN × tempo); controllers 7, 11, 120/123 and 121, programs and pitch bend
/// (±2 semitones) are honoured, tempo metas ignored. A note-off lets the voice fade at
/// max(90, 48 / release) dB per second until it is 48 dB down. Songs loop.
/// <para>Single-threaded and deterministic: the same events give the same samples.</para>
/// </summary>
public sealed class MidiSynth
{
    public const int Voices = 40;
    private const double ReleaseDbPerSecond = 90;
    private const double MinDb = -48;
    /// <summary>Frames between control steps (release fades, event dispatch): 1.5 ms at 44.1 kHz.</summary>
    public const int ControlFrames = 64;

    private sealed class Voice
    {
        public bool Playing;
        public int Channel = -1;
        public int Note = -1;
        public bool On;
        public int Age;
        public double Release = 0.2;
        public double VelocityDb;
        public double ChannelDb;
        public double VolumeDb;
        public Instrument? Instrument;
        public double Position;
        public double Step;
    }

    private struct Channel
    {
        public int Program;
        public int Volume;
        public int Expression;
        public double Bend;
    }

    private readonly Voice[] _voices = new Voice[Voices];
    private readonly Channel[] _channels = new Channel[16];
    private readonly Instrument?[] _programs = new Instrument?[128];
    private readonly Instrument?[] _drums = new Instrument?[128];
    private HmiEvent[] _events = [];
    private int _pos;
    private double _tick;
    private double _ticksPerSecond = 120;
    private int _length;
    private int _age;
    private int _controlLeft;

    public MidiSynth(int sampleRate = 44100)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        SampleRate = sampleRate;
        for (var i = 0; i < Voices; i++)
        {
            _voices[i] = new Voice();
        }

        ResetChannels();
    }

    /// <summary>Output frames per second.</summary>
    public int SampleRate { get; }
    /// <summary>The synth's overall level (the port's gain_db).</summary>
    public double GainDb { get; set; } = -4;
    /// <summary>Whether the song starts again at its end; off, it stops there.</summary>
    public bool Loop { get; set; } = true;
    /// <summary>A song is being sequenced (voices may still ring after it stops).</summary>
    public bool Playing { get; private set; }
    /// <summary>The song position in ticks.</summary>
    public double Tick => _tick;
    /// <summary>Voices sounding now.</summary>
    public int ActiveVoices => _voices.Count(v => v.Playing);

    /// <summary>Starts a song from its beginning, with every instrument it uses built first.</summary>
    public void Play(HmiFile song) => Play(song.Events.AsSpan(), song.Length, song.Rate);

    /// <summary>Starts an event list (sorted by tick) of <paramref name="length"/> ticks at
    /// <paramref name="ticksPerSecond"/>.</summary>
    public void Play(ReadOnlySpan<HmiEvent> events, int length, int ticksPerSecond)
    {
        Stop();
        _events = events.ToArray();
        _length = length;
        _ticksPerSecond = ticksPerSecond > 0 ? ticksPerSecond : 120;
        (_pos, _tick, _controlLeft) = (0, 0, 0);
        ResetChannels();
        Playing = _events.Length > 0 && _length > 0;
        if (Playing)
        {
            WarmUp(_events);
        }
    }

    /// <summary>Silences everything at once.</summary>
    public void Stop()
    {
        Playing = false;
        foreach (var v in _voices)
        {
            (v.Playing, v.On, v.Channel, v.Instrument) = (false, false, -1, null);
        }
    }

    /// <summary>Renders interleaved stereo frames (overwritten).</summary>
    public void Render(Span<float> stereo)
    {
        var frames = stereo.Length / 2;
        var done = 0;
        while (done < frames)
        {
            if (_controlLeft == 0)
            {
                Control((double)ControlFrames / SampleRate);
                _controlLeft = ControlFrames;
            }

            var run = Math.Min(_controlLeft, frames - done);
            Mix(stereo.Slice(done * 2, run * 2));
            done += run;
            _controlLeft -= run;
        }
    }

    /// <summary>One control step: fade released voices, then move the song on and dispatch
    /// (the order of the port's _process).</summary>
    private void Control(double delta)
    {
        ReleaseVoices(delta);
        if (!Playing)
        {
            return;
        }

        _tick += delta * _ticksPerSecond;
        while (_pos < _events.Length && _events[_pos].Tick <= _tick)
        {
            Dispatch(_events[_pos]);
            _pos++;
        }

        if (_pos >= _events.Length)
        {
            AllNotesOff();
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

    private void Mix(Span<float> stereo)
    {
        stereo.Clear();
        var frames = stereo.Length / 2;
        foreach (var v in _voices)
        {
            if (!v.Playing)
            {
                continue;
            }

            var inst = v.Instrument!;
            var samples = inst.Samples;
            var gain = (float)Math.Pow(10, v.VolumeDb / 20);
            for (var f = 0; f < frames; f++)
            {
                if (v.Position >= samples.Length)
                {
                    if (inst.LoopBegin < 0)
                    {
                        (v.Playing, v.Channel) = (false, -1);
                        break;
                    }

                    v.Position -= samples.Length - inst.LoopBegin;
                }

                var i = (int)v.Position;
                var t = (float)(v.Position - i);
                var next = i + 1 < samples.Length ? i + 1 : inst.LoopBegin >= 0 ? inst.LoopBegin : i;
                var s = (samples[i] + (samples[next] - samples[i]) * t) * gain;
                stereo[f * 2] += s;
                stereo[f * 2 + 1] += s;
                v.Position += v.Step;
            }
        }
    }

    private void ResetChannels()
    {
        for (var i = 0; i < _channels.Length; i++)
        {
            _channels[i] = new Channel { Program = 0, Volume = 100, Expression = 127, Bend = 1 };
        }
    }

    private void Dispatch(in HmiEvent e)
    {
        var ch = e.Channel & 0x0F;
        switch (e.Kind)
        {
            case 0x90:
                NoteOn(ch, e.Data1, e.Data2);
                break;
            case 0x80:
                NoteOff(ch, e.Data1);
                break;
            case 0xB0:
                switch (e.Data1)
                {
                    case 7:
                        _channels[ch].Volume = e.Data2;
                        UpdateChannelVolume(ch);
                        break;
                    case 11:
                        _channels[ch].Expression = e.Data2;
                        UpdateChannelVolume(ch);
                        break;
                    case 120 or 123:
                        ChannelNotesOff(ch);
                        break;
                    case 121:
                        (_channels[ch].Volume, _channels[ch].Expression, _channels[ch].Bend) = (100, 127, 1);
                        break;
                }

                break;
            case 0xC0:
                _channels[ch].Program = e.Data1;
                break;
            case 0xE0:
                _channels[ch].Bend = Math.Pow(2, (e.Data1 - 8192) / 8192.0 * 2 / 12);
                foreach (var v in _voices)
                {
                    if (v.Channel == ch && v.On)
                    {
                        ApplyPitch(v);
                    }
                }

                break;
        }
    }

    private static double LinearToDb(double linear) => 20 * Math.Log10(linear);

    private double ChannelDb(int ch)
    {
        var c = _channels[ch];
        return LinearToDb(Math.Max(c.Volume / 127.0 * (c.Expression / 127.0), 0.0005));
    }

    private void UpdateChannelVolume(int ch)
    {
        foreach (var v in _voices)
        {
            if (v.Channel == ch && v.On)
            {
                v.ChannelDb = ChannelDb(ch);
                v.VolumeDb = v.VelocityDb + v.ChannelDb + GainDb;
            }
        }
    }

    private static double NoteHz(int note) => 440 * Math.Pow(2, (note - 69) / 12.0);

    private void ApplyPitch(Voice v)
    {
        var inst = v.Instrument;
        if (inst == null)
        {
            return;
        }

        var pitch = inst.Fixed ? 1 : Math.Clamp(NoteHz(v.Note) * _channels[v.Channel].Bend / inst.Hz, 0.05, 20);
        v.Step = pitch * Instruments.Rate / SampleRate;
    }

    private Instrument ProgramInstrument(int program)
    {
        program = Math.Clamp(program, 0, 127);
        return _programs[program] ??= Instruments.BuildProgram(program);
    }

    private Instrument DrumInstrument(int note)
    {
        note = Math.Clamp(note, 0, 127);
        return _drums[note] ??= Instruments.BuildDrum(note);
    }

    private void NoteOn(int ch, int note, int velocity)
    {
        var inst = ch == 9 ? DrumInstrument(note) : ProgramInstrument(_channels[ch].Program);
        // Retrigger the same note, else a free voice, else the oldest.
        var slot = Array.FindIndex(_voices, v => v.Channel == ch && v.Note == note && v.On);
        if (slot < 0)
        {
            slot = Array.FindIndex(_voices, v => !v.Playing);
        }

        if (slot < 0)
        {
            var oldest = -1;
            for (var i = 0; i < _voices.Length; i++)
            {
                if (oldest < 0 || _voices[i].Age < oldest)
                {
                    (oldest, slot) = (_voices[i].Age, i);
                }
            }
        }

        _age++;
        var pick = _voices[slot];
        (pick.Channel, pick.Note, pick.On, pick.Age, pick.Release) = (ch, note, true, _age, inst.Release);
        pick.VelocityDb = LinearToDb(Math.Max(velocity / 127.0, 0.01)) + inst.GainDb;
        pick.ChannelDb = ChannelDb(ch);
        pick.Instrument = inst;
        ApplyPitch(pick);
        pick.VolumeDb = pick.VelocityDb + pick.ChannelDb + GainDb;
        (pick.Playing, pick.Position) = (true, 0);
    }

    private void NoteOff(int ch, int note)
    {
        foreach (var v in _voices)
        {
            if (v.Channel == ch && v.Note == note && v.On)
            {
                v.On = false;
            }
        }
    }

    private void ChannelNotesOff(int ch)
    {
        foreach (var v in _voices)
        {
            if (v.Channel == ch && v.On)
            {
                v.On = false;
            }
        }
    }

    private void AllNotesOff()
    {
        foreach (var v in _voices)
        {
            v.On = false;
        }
    }

    private void ReleaseVoices(double delta)
    {
        foreach (var v in _voices)
        {
            if (v.On || !v.Playing)
            {
                continue;
            }

            var release = Math.Max(v.Release, 0.02);
            v.VolumeDb -= delta * Math.Max(ReleaseDbPerSecond, 48 / release);
            if (v.VolumeDb < MinDb + GainDb)
            {
                (v.Playing, v.Channel) = (false, -1);
            }
        }
    }

    /// <summary>Builds every instrument the events can ask for (the port's _song_items): each
    /// melodic note's program, the drum notes, and every program set on a channel that plays
    /// notes (a looping song carries a channel's last program into its next pass).</summary>
    private void WarmUp(ReadOnlySpan<HmiEvent> events)
    {
        Span<int> program = stackalloc int[16];
        var noted = 0;
        foreach (var e in events)
        {
            var ch = e.Channel & 0x0F;
            if (e.Kind == 0x90)
            {
                if (ch == 9)
                {
                    DrumInstrument(e.Data1);
                }
                else
                {
                    ProgramInstrument(program[ch]);
                    noted |= 1 << ch;
                }
            }
            else if (e.Kind == 0xC0 && ch != 9)
            {
                program[ch] = e.Data1;
            }
        }

        foreach (var e in events)
        {
            var ch = e.Channel & 0x0F;
            if (e.Kind == 0xC0 && ch != 9 && ((noted >> ch) & 1) != 0)
            {
                ProgramInstrument(e.Data1);
            }
        }
    }
}
