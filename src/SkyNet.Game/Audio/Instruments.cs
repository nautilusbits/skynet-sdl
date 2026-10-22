namespace SkyNet.Game.Audio;

/// <summary>One wavetable instrument: a mono sample at <see cref="Instruments.Rate"/>, played back
/// pitched. A sustained sound loops [<see cref="LoopBegin"/>, <see cref="Samples"/>.Length); a
/// one-shot ends with its sample.</summary>
/// <param name="Samples">The sample, already through 16-bit PCM (as the Godot port stores it).</param>
/// <param name="LoopBegin">Where the loop starts, -1 for a one-shot.</param>
/// <param name="Hz">The pitch the sample sounds at; a note plays at note Hz / this.</param>
/// <param name="Fixed">Drums: always at the sample's own pitch.</param>
/// <param name="Release">Seconds the voice takes to fade after its note-off.</param>
/// <param name="GainDb">Added to the voice's volume.</param>
public sealed record Instrument(float[] Samples, int LoopBegin, double Hz, bool Fixed, double Release, double GainDb);

/// <summary>
/// The Godot port's General-MIDI instrument set (midi_synth.gd), ported value for value: one
/// additive wavetable per GM family (8 programs each) and a small drum kit. Samples are built at
/// 22050 Hz and quantised to 16 bits like the port's AudioStreamWAV. The only difference is the
/// noise generator (a PCG32 here, Godot's RandomNumberGenerator there): same seeds, other numbers.
/// </summary>
public static class Instruments
{
    public const int Rate = 22050;
    /// <summary>The loop segment of a sustained tone: 0.2 s.</summary>
    public const int LoopLength = 4410;
    /// <summary>Whole periods in the loop segment: the base pitch is about 260 Hz, close to C4.</summary>
    public const int LoopPeriods = 52;
    /// <summary>Warm-up items 0-127 are programs, DrumBase + note a drum note.</summary>
    public const int DrumBase = 128;

    /// <summary>The base pitch of every tone: a whole number of periods fits the loop.</summary>
    public static double LoopHz => (double)Rate * LoopPeriods / LoopLength;

    /// <summary>A GM family's tone: harmonics (number, amplitude), attack, decay, sustain level
    /// (0 = one-shot), release, gain and soft clip.</summary>
    public sealed record ToneSpec((int N, double Amp)[] Harmonics, double Attack, double Decay, double Sustain,
        double Release, double GainDb, double Clip = 0, bool Noise = false);

    public enum DrumKind { Kick, Hit, Snare, Clap, Tom, Hat, Cymbal, Bell, Cowbell }

    /// <summary>A GM drum note: its sample identity, kind and parameters.</summary>
    public sealed record DrumSpec(int Id, DrumKind Kind, double GainDb, double Length = 0.3, double Hz = 120);

    /// <summary>The family spec of a GM program (midi_synth.gd _family).</summary>
    public static ToneSpec Family(int program)
    {
        var family = program / 8;
        return family switch
        {
            0 => new([(1, 1.0), (2, 0.55), (3, 0.3), (4, 0.15), (5, 0.08), (6, 0.05)], 0.004, 1.4, 0, 0.25, 0),
            1 => new([(1, 1.0), (3, 0.35), (4, 0.3), (6, 0.12)], 0.002, 1.1, 0, 0.3, -2),
            2 => new([(1, 1.0), (2, 0.8), (3, 0.5), (4, 0.6), (6, 0.3), (8, 0.4)], 0.01, 0.2, 0.9, 0.08, -3),
            3 when program >= 29 => new([(1, 1.0), (2, 0.7), (3, 0.6), (4, 0.5), (5, 0.4), (6, 0.3), (7, 0.25)], 0.004, 1.2, 0.35, 0.15, -3, Clip: 2.5),
            3 => new([(1, 1.0), (2, 0.6), (3, 0.4), (4, 0.3), (5, 0.2), (6, 0.12)], 0.003, 0.9, 0, 0.2, -1),
            4 => new([(1, 1.0), (2, 0.5), (3, 0.3), (4, 0.12)], 0.004, 0.5, 0.45, 0.12, 1),
            5 or 6 => new([(1, 1.0), (2, 0.5), (3, 0.33), (4, 0.25), (5, 0.2), (6, 0.17), (7, 0.14), (8, 0.12)], 0.12, 0.3, 0.85, 0.4, -4),
            7 => new([(1, 1.0), (2, 0.6), (3, 0.5), (4, 0.4), (5, 0.35), (6, 0.3), (7, 0.25), (8, 0.2), (9, 0.15), (10, 0.12)], 0.04, 0.25, 0.8, 0.18, -3),
            8 => new([(1, 1.0), (3, 0.4), (5, 0.25), (7, 0.15), (9, 0.08)], 0.04, 0.2, 0.85, 0.15, -3),
            9 => new([(1, 1.0), (2, 0.15), (3, 0.06)], 0.05, 0.2, 0.85, 0.2, -1),
            10 when program is 80 or 84 or 87 => new([(1, 1.0), (3, 0.33), (5, 0.2), (7, 0.14), (9, 0.11), (11, 0.09)], 0.008, 0.2, 0.85, 0.1, -4),
            10 => new([(1, 1.0), (2, 0.5), (3, 0.33), (4, 0.25), (5, 0.2), (6, 0.17), (7, 0.14), (8, 0.12), (9, 0.11)], 0.008, 0.2, 0.85, 0.1, -4),
            11 => new([(1, 1.0), (2, 0.4), (3, 0.25), (4, 0.15), (5, 0.1), (6, 0.06)], 0.35, 0.4, 0.9, 0.7, -5),
            12 => new([(1, 1.0), (2, 0.3), (3, 0.2)], 0.2, 0.4, 0.8, 0.5, -5),
            13 => new([(1, 1.0), (2, 0.5), (3, 0.4), (5, 0.2)], 0.003, 0.8, 0, 0.2, -1),
            14 => new([(1, 1.0), (2, 0.4), (3, 0.3), (4, 0.2)], 0.002, 0.45, 0, 0.15, -1),
            _ => new([], 0.005, 0.6, 0, 0.3, -6, Noise: true),
        };
    }

    /// <summary>The GM percussion map (midi_synth.gd _drum_spec).</summary>
    public static DrumSpec Drum(int note) => note switch
    {
        35 or 36 => new(36, DrumKind.Kick, 2),
        37 => new(37, DrumKind.Hit, -6, Length: 0.04),
        38 or 40 => new(38, DrumKind.Snare, 0),
        39 => new(39, DrumKind.Clap, -3),
        41 or 43 => new(41, DrumKind.Tom, 0, Hz: 90),
        45 or 47 => new(45, DrumKind.Tom, 0, Hz: 130),
        48 or 50 => new(48, DrumKind.Tom, 0, Hz: 180),
        42 or 44 => new(42, DrumKind.Hat, -8, Length: 0.07),
        46 => new(46, DrumKind.Hat, -8, Length: 0.35),
        49 or 52 or 55 or 57 => new(49, DrumKind.Cymbal, -8, Length: 1.2),
        51 or 59 => new(51, DrumKind.Cymbal, -10, Length: 0.5),
        53 => new(53, DrumKind.Bell, -8),
        54 => new(54, DrumKind.Hat, -10, Length: 0.2),
        56 => new(56, DrumKind.Cowbell, -6),
        _ => new(60, DrumKind.Hit, -8, Length: 0.15),
    };

    /// <summary>A program's instrument.</summary>
    public static Instrument BuildProgram(int program)
    {
        var spec = Family(Math.Clamp(program, 0, 127));
        var (samples, loopBegin) = BuildTone(spec);
        return new Instrument(samples, loopBegin, LoopHz, false, spec.Release, spec.GainDb);
    }

    /// <summary>A drum note's instrument (fixed pitch, 0.05 s release).</summary>
    public static Instrument BuildDrum(int note)
    {
        var spec = Drum(note);
        return new Instrument(BuildDrumSamples(spec), -1, 1, true, 0.05, spec.GainDb);
    }

    /// <summary>Additive tone with an ADSR-shaped body; sustained sounds loop their last 0.2 s,
    /// one-shots decay to silence (midi_synth.gd _build_tone). Returns the samples and the loop start.</summary>
    public static (float[] Samples, int LoopBegin) BuildTone(ToneSpec spec)
    {
        var (a, d, sustain) = (spec.Attack, spec.Decay, spec.Sustain);
        var total = sustain > 0 ? a + d + 0.25 : a + d + 0.05;
        var n = (int)(total * Rate);
        if (sustain > 0)
        {
            n = Math.Max(n, (int)((a + d) * Rate) + LoopLength);
        }

        var hz = LoopHz;
        var buf = new float[n];
        var rng = new Pcg32(1234);
        var norm = 0.0;
        foreach (var h in spec.Harmonics)
        {
            norm += h.Amp;
        }

        if (norm <= 0)
        {
            norm = 1;
        }

        for (var i = 0; i < n; i++)
        {
            var t = (double)i / Rate;
            var s = 0.0;
            if (spec.Noise)
            {
                s = rng.NextRange(-1, 1);
            }
            else
            {
                foreach (var h in spec.Harmonics)
                {
                    s += h.Amp * Math.Sin(Math.Tau * hz * h.N * t);
                }

                s /= norm;
            }

            if (spec.Clip > 0)
            {
                s = Math.Tanh(s * spec.Clip) / Math.Tanh(spec.Clip);
            }

            double env;
            if (t < a)
            {
                env = t / a;
            }
            else if (sustain > 0)
            {
                env = sustain + (1 - sustain) * Math.Exp(-(t - a) / Math.Max(d * 0.35, 0.001));
            }
            else
            {
                env = Math.Exp(-(t - a) * 4 / Math.Max(d, 0.001));
                if (t > a + d)
                {
                    env *= Math.Max(0, 1 - (t - a - d) / 0.05);
                }
            }

            buf[i] = Pcm16(s * env * 0.8);
        }

        return (buf, sustain > 0 ? n - LoopLength : -1);
    }

    /// <summary>The drum kit (midi_synth.gd _build_drum).</summary>
    public static float[] BuildDrumSamples(DrumSpec spec)
    {
        var len = spec.Kind switch
        {
            DrumKind.Kick => 0.4,
            DrumKind.Snare => 0.25,
            DrumKind.Clap => 0.3,
            DrumKind.Tom => 0.45,
            DrumKind.Bell => 0.6,
            DrumKind.Cowbell => 0.3,
            _ => spec.Length,
        };
        var n = (int)(len * Rate);
        var buf = new float[n];
        var rng = new Pcg32(99);
        var prev = 0.0;
        for (var i = 0; i < n; i++)
        {
            var t = (double)i / Rate;
            var u = t / len;
            double s;
            switch (spec.Kind)
            {
                case DrumKind.Kick:
                    {
                        var f = 45 + 130 * Math.Exp(-t * 18);
                        s = Math.Tanh(Math.Sin(Math.Tau * f * t) * Math.Exp(-t * 9) * 1.8);
                        break;
                    }
                case DrumKind.Snare:
                    s = rng.NextRange(-1, 1) * Math.Exp(-t * 16) * 0.8 + Math.Sin(Math.Tau * 190 * t) * Math.Exp(-t * 30) * 0.5;
                    break;
                case DrumKind.Clap:
                    {
                        var nz = rng.NextRange(-1, 1);
                        var burst = 0.0;
                        for (var k = 0; k < 3; k++)
                        {
                            var tk = t - k * 0.011;
                            if (tk >= 0)
                            {
                                burst = Math.Max(burst, Math.Exp(-tk * 60));
                            }
                        }

                        s = nz * (burst * 0.7 + Math.Exp(-t * 12) * 0.5);
                        break;
                    }
                case DrumKind.Tom:
                    {
                        var f = spec.Hz * (1 + 0.8 * Math.Exp(-t * 20));
                        s = Math.Sin(Math.Tau * f * t) * Math.Exp(-t * 7);
                        break;
                    }
                case DrumKind.Hat:
                    {
                        var nz = rng.NextRange(-1, 1);
                        var hp = nz - prev;
                        prev = nz;
                        s = hp * Math.Exp(-t * (6 / len)) * 0.7;
                        break;
                    }
                case DrumKind.Cymbal:
                    {
                        var nz = rng.NextRange(-1, 1);
                        var hp = nz - prev * 0.6;
                        prev = nz;
                        s = hp * Math.Exp(-t * (4 / len)) * 0.6;
                        break;
                    }
                case DrumKind.Bell:
                    s = (Math.Sin(Math.Tau * 1180 * t) + 0.5 * Math.Sin(Math.Tau * 1760 * t) + 0.3 * Math.Sin(Math.Tau * 2600 * t)) / 1.8 * Math.Exp(-t * 5);
                    break;
                case DrumKind.Cowbell:
                    s = Math.Tanh((Math.Sin(Math.Tau * 560 * t) + Math.Sin(Math.Tau * 845 * t)) * 0.5 * Math.Exp(-t * 12) * 2);
                    break;
                default:
                    s = rng.NextRange(-1, 1) * Math.Exp(-t * (5 / len));
                    break;
            }

            if (u > 0.9)
            {
                s *= (1 - u) / 0.1;
            }

            buf[i] = Pcm16(Math.Clamp(s * 0.85, -1, 1));
        }

        return buf;
    }

    /// <summary>Through 16-bit PCM and back, as the Godot port's samples are stored.</summary>
    private static float Pcm16(double v) => (short)(int)(Math.Clamp(v, -1, 1) * 32767) / 32768f;

    /// <summary>PCG32 (XSH RR), the generator family Godot's RandomNumberGenerator uses.</summary>
    private struct Pcg32(ulong seed)
    {
        private const ulong Multiplier = 6364136223846793005UL;
        private const ulong Increment = 1442695040888963407UL;
        private ulong _state = (seed + Increment) * Multiplier + Increment;

        public uint Next()
        {
            var old = _state;
            _state = old * Multiplier + Increment;
            var xorShifted = (uint)(((old >> 18) ^ old) >> 27);
            var rot = (int)(old >> 59);
            return (xorShifted >> rot) | (xorShifted << (-rot & 31));
        }

        public double NextRange(double from, double to) => from + Next() / 4294967296.0 * (to - from);
    }
}
