using RetroEng.Audio;

namespace SkyNet.Game.Audio;

/// <summary>
/// Rain's hiss made in code (no recorded sound): a stereo loop of soft, low-mid noise with drops
/// pattering in it, its two channels apart so it fills the space round the ears. A background bed:
/// no white-noise top that cuts through the game's sounds.
/// <code>
///   noise ─► high-pass 250 Hz ─► low-pass 1.8 kHz (twice) ─► × slow swell ─┐
///   drops: short dull pats, a few hundred a second ─────────────────────────┼─► to HissRms ─► seamless loop
/// </code>
/// The loop's last <see cref="Overlap"/> seconds are faded into its start, so it repeats without a click.
/// </summary>
public static class RainHiss
{
    /// <summary>The synthesis rate.</summary>
    public const int Rate = 22050;
    /// <summary>The loop's length and its crossfade (seconds).</summary>
    public const float Seconds = 4f, Overlap = 0.5f;
    /// <summary>The loop's level (RMS).</summary>
    public const float HissRms = 0.3f;
    /// <summary>Drops heard a second (per channel).</summary>
    public const int DropsPerSecond = 220;

    private static readonly Lazy<Sound> Made = new(() => Make(1));

    /// <summary>The hiss (made once).</summary>
    public static Sound Sound => Made.Value;

    /// <summary>A hiss loop from <paramref name="seed"/>.</summary>
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

        Level(samples, HissRms);
        return new Sound(samples, 2, Rate, Looping.Forever);
    }

    /// <summary>One channel: the hiss with its slow swell and the drops.</summary>
    private static float[] Channel(int length, Random random)
    {
        var raw = new float[length];
        var high = OnePole(250f);
        var low = OnePole(1800f);
        var tick = OnePole(900f);
        float hp = 0f, lp = 0f, lp2 = 0f, swell = 1f, swellTo = 1f;
        for (var n = 0; n < length; n++)
        {
            // A new swell target every 0.25 s, followed slowly (gusts).
            if (n % (Rate / 4) == 0)
            {
                swellTo = 0.75f + 0.5f * (float)random.NextDouble();
            }

            swell += (swellTo - swell) * 0.0002f;
            var noise = (float)random.NextDouble() * 2f - 1f;
            hp += high * (noise - hp);
            lp += low * (noise - hp - lp);
            lp2 += low * (lp - lp2);
            raw[n] = lp2 * swell;
        }

        // The drops: a decaying burst of low-passed noise each (≈ 6 ms): a pat, not a click.
        var drops = (int)(DropsPerSecond * (float)length / Rate);
        var span = Rate / 160;
        for (var d = 0; d < drops; d++)
        {
            var at = random.Next(length);
            var size = 0.6f + 1.4f * (float)random.NextDouble();
            var f = 0f;
            for (var i = 0; i < span && at + i < length; i++)
            {
                var noise = (float)random.NextDouble() * 2f - 1f;
                f += tick * (noise - f);
                raw[at + i] += f * size * 2f * MathF.Exp(-i / (span * 0.3f));
            }
        }

        return raw;
    }

    internal static float OnePole(float hz) => 1f - MathF.Exp(-2f * MathF.PI * hz / Rate);

    /// <summary>Scales <paramref name="samples"/> to <paramref name="rms"/>, clamped to ±1.</summary>
    internal static void Level(float[] samples, float rms)
    {
        var sum = 0.0;
        foreach (var s in samples)
        {
            sum += s * s;
        }

        if (sum <= 0.0)
        {
            return;
        }

        var k = rms / (float)Math.Sqrt(sum / samples.Length);
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = Math.Clamp(samples[i] * k, -1f, 1f);
        }
    }
}
