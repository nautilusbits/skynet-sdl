using RetroEng.Audio;

namespace SkyNet.Game.Audio;

/// <summary>A near strike cracks before it rumbles; a far one only rumbles.</summary>
public enum ThunderKind { Near, Far }

/// <summary>
/// Thunder made in code (no recorded sound): filtered noise rolling in a few swells, and for a
/// near strike a sharp crack ahead of it. One seed gives one sound.
/// <code>
///   near:  |crack|~~~ROLL~~~roll~~~~...      high-passed burst (about 0.3 s), then the rumble
///   far:   ~~~~ROLL~~roll~~~~~...            the rumble only, darker and slower to swell
///   rumble = noise ─► low-pass (body, 380-700 Hz) + low-pass (boom, 90 Hz) ─► × swells ─► to RumbleRms
///   out    = soft clip(rumble + crack) × Peak
/// </code>
/// The rumble keeps a body in the band small speakers play (above 300 Hz) and is levelled by its own
/// loudness, so the crack's peak does not push it down.
/// </summary>
public static class Thunder
{
    /// <summary>The synthesis rate (mono).</summary>
    public const int Rate = 22050;
    /// <summary>The loudest sample, so the mixer's limiter has little to do.</summary>
    public const float Peak = 0.9f;
    /// <summary>A rumble's length in seconds: near, far.</summary>
    public const float NearSeconds = 4.5f, FarSeconds = 7f;
    /// <summary>The rumble's level (RMS of its loudest second) before the soft clip.</summary>
    public const float RumbleRms = 0.5f;
    /// <summary>The crack's peak, relative to the rumble's level.</summary>
    public const float CrackPeak = 2.5f;

    /// <summary>A strike's thunder: a near one has the crack and a brighter rumble.</summary>
    public static Sound Make(int seed, ThunderKind kind)
    {
        var near = kind == ThunderKind.Near;
        var random = new Random(seed);
        var seconds = near ? NearSeconds : FarSeconds;
        var samples = new float[(int)(seconds * Rate)];

        // The rumble's tone: a body (a far strike's darker, it lost its highs over the distance) and a boom.
        var body = OnePole(near ? 700f : 380f);
        var boom = OnePole(90f);

        // Swells: echoes of the channel's parts arriving one after another (e.g. 0.1 s, 0.9 s, 2.1 s).
        var swells = 4 + random.Next(4);
        var at = new float[swells];
        var size = new float[swells];
        var decay = new float[swells];
        for (var i = 0; i < swells; i++)
        {
            at[i] = i == 0 ? (near ? 0.05f : 0.3f) : (float)random.NextDouble() * seconds * 0.6f;
            size[i] = i == 0 ? 1f : 0.35f + 0.6f * (float)random.NextDouble();
            decay[i] = 0.6f + 1.4f * (float)random.NextDouble();
        }

        float b1 = 0f, b2 = 0f, d1 = 0f, d2 = 0f;
        for (var n = 0; n < samples.Length; n++)
        {
            var t = (float)n / Rate;
            var noise = (float)random.NextDouble() * 2f - 1f;
            b1 += body * (noise - b1);
            b2 += body * (b1 - b2);
            d1 += boom * (noise - d1);
            d2 += boom * (d1 - d2);
            samples[n] = (b2 + BoomMix * d2) * Envelope(t, at, size, decay, near) * TailFade(t, seconds);
        }

        Level(samples, RumbleRms);
        if (near)
        {
            AddCrack(samples, random);
        }

        for (var n = 0; n < samples.Length; n++)
        {
            samples[n] = MathF.Tanh(samples[n]) * Peak;
        }

        return new Sound(samples, 1, Rate, Looping.Once);
    }

    /// <summary>The boom's share against the body (the boom's noise is weaker: it passes less).</summary>
    private const float BoomMix = 2.5f;

    /// <summary>The swells' sum at <paramref name="t"/>: each rises over 0.12 s (0.4 s far) and decays.</summary>
    private static float Envelope(float t, float[] at, float[] size, float[] decay, bool near)
    {
        var rise = near ? 0.12f : 0.4f;
        var sum = 0f;
        for (var i = 0; i < at.Length; i++)
        {
            var dt = t - at[i];
            if (dt <= 0f)
            {
                continue;
            }

            sum += size[i] * MathF.Min(1f, dt / rise) * MathF.Exp(-dt / decay[i]);
        }

        return sum;
    }

    /// <summary>The last half second fades to silence (no click at the end).</summary>
    private static float TailFade(float t, float seconds) => Math.Clamp((seconds - t) / 0.5f, 0f, 1f);

    /// <summary>The crack: high-passed noise, very loud and short, a few sharp spikes in it.</summary>
    private static void AddCrack(float[] samples, Random random)
    {
        var length = (int)(0.35f * Rate);
        var lowPass = 0f;
        var a = OnePole(900f);
        for (var n = 0; n < length && n < samples.Length; n++)
        {
            var t = (float)n / Rate;
            var noise = (float)random.NextDouble() * 2f - 1f;
            lowPass += a * (noise - lowPass);
            var high = noise - lowPass;
            var spike = random.NextDouble() < 0.002 ? 1.5f : 1f;
            samples[n] += CrackPeak * high * spike * MathF.Exp(-t / 0.07f);
        }
    }

    /// <summary>A one-pole low-pass's coefficient for <paramref name="hz"/>.</summary>
    private static float OnePole(float hz) => 1f - MathF.Exp(-2f * MathF.PI * hz / Rate);

    /// <summary>Scales <paramref name="samples"/> so their loudest second's RMS is <paramref name="rms"/>.</summary>
    private static void Level(float[] samples, float rms)
    {
        var loudest = 0.0;
        for (var start = 0; start < samples.Length; start += Rate / 4)
        {
            var end = Math.Min(samples.Length, start + Rate);
            var sum = 0.0;
            for (var i = start; i < end; i++)
            {
                sum += samples[i] * samples[i];
            }

            loudest = Math.Max(loudest, sum / Rate);
        }

        if (loudest <= 0.0)
        {
            return;
        }

        var k = rms / (float)Math.Sqrt(loudest);
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] *= k;
        }
    }
}
