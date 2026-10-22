using RetroEng.Audio;

namespace SkyNet.Game.Audio;

/// <summary>
/// The wind's howl made in code (no recorded sound): a stereo loop of noise rung through two narrow
/// band-passes whose pitch wanders (the whistle round corners), over a low rumble; its two channels
/// apart. <see cref="Enhanced.Air"/> raises its gain and pitch in a gust.
/// <code>
///   noise ─► band-pass 230-520 Hz, Q 9, wandering ─┐
///   noise ─► band-pass 420-760 Hz, Q 12, wandering ─┼─► × slow swell ─► to HowlRms ─► seamless loop
///   noise ─► low-pass 120 Hz (rumble) ──────────────┘
/// </code>
/// </summary>
public static class WindHowl
{
    /// <summary>The synthesis rate.</summary>
    public const int Rate = RainHiss.Rate;
    /// <summary>The loop's length and its crossfade (seconds).</summary>
    public const float Seconds = 8f, Overlap = 1f;
    /// <summary>The loop's level (RMS).</summary>
    public const float HowlRms = 0.3f;
    /// <summary>The howl's gain at a table's full volume in a <see cref="Enhanced.Air.HowlMetres"/> wind:
    /// a bed well under the game's sounds (about -20 dB under a gunshot).</summary>
    public const float Gain = 0.12f;
    /// <summary>The band-passes' pitch is set anew every this many samples.</summary>
    private const int Block = 64;

    private static readonly Lazy<Sound> Made = new(() => Make(1));

    /// <summary>The howl (made once).</summary>
    public static Sound Sound => Made.Value;

    /// <summary>A howl loop from <paramref name="seed"/>.</summary>
    public static Sound Make(int seed)
    {
        var random = new Random(seed);
        var frames = (int)(Seconds * Rate);
        var overlap = (int)(Overlap * Rate);
        var samples = new float[frames * 2];
        for (var channel = 0; channel < 2; channel++)
        {
            var raw = Channel(frames + overlap, random);

            // The tail past the loop fades into its start.
            for (var n = 0; n < frames; n++)
            {
                var s = raw[n];
                if (n < overlap)
                {
                    var k = (float)n / overlap;
                    s = s * k + raw[frames + n] * (1f - k);
                }

                samples[n * 2 + channel] = s;
            }
        }

        RainHiss.Level(samples, HowlRms);
        return new Sound(samples, 2, Rate, Looping.Forever);
    }

    /// <summary>One channel: two wandering whistles and the rumble, swelling.</summary>
    private static float[] Channel(int length, Random random)
    {
        var raw = new float[length];
        var low = new Resonator(230f, 520f, 9f, random);
        var high = new Resonator(420f, 760f, 12f, random);
        var rumbleK = RainHiss.OnePole(120f);
        float rumble = 0f, swell = 1f, swellTo = 1f;
        for (var n = 0; n < length; n++)
        {
            if (n % Block == 0)
            {
                low.Wander(random);
                high.Wander(random);
            }

            // A new swell target every half second, followed slowly.
            if (n % (Rate / 2) == 0)
            {
                swellTo = 0.6f + 0.8f * (float)random.NextDouble();
            }

            swell += (swellTo - swell) * 0.00015f;
            var noise = (float)random.NextDouble() * 2f - 1f;
            rumble += rumbleK * (noise - rumble);
            raw[n] = (low.Ring(noise) * 0.7f + high.Ring(noise) * 0.35f + rumble * 2.5f) * swell;
        }

        return raw;
    }

    /// <summary>A band-pass (RBJ, constant peak gain) whose centre wanders between two pitches.</summary>
    private sealed class Resonator(float from, float to, float q, Random random)
    {
        private float _at = (float)random.NextDouble(), _towards = (float)random.NextDouble();
        private float _b0, _a1, _a2, _x1, _x2, _y1, _y2;

        public void Wander(Random r)
        {
            if (MathF.Abs(_towards - _at) < 0.01f)
            {
                _towards = (float)r.NextDouble();
            }

            _at += (_towards - _at) * 0.004f;
            var w = 2f * MathF.PI * (from + (to - from) * _at) / Rate;
            var alpha = MathF.Sin(w) / (2f * q);
            var a0 = 1f + alpha;
            (_b0, _a1, _a2) = (alpha / a0, -2f * MathF.Cos(w) / a0, (1f - alpha) / a0);
        }

        public float Ring(float x)
        {
            var y = _b0 * x - _b0 * _x2 - _a1 * _y1 - _a2 * _y2;
            (_x2, _x1, _y2, _y1) = (_x1, x, _y1, y);
            return y;
        }
    }
}
