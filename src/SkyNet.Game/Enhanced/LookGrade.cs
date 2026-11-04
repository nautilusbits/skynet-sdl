using System.Numerics;
using RetroEng.Render;

namespace SkyNet.Game.Enhanced;

/// <summary>
/// The enhanced look's colour grade per preset (<see cref="Lighting.Grade"/>): a subtle film look
/// built in code as a LUT strip, one cached <see cref="ColourGrade"/> per preset (the renderer
/// uploads a grade when its instance changes).
/// <code>
///   interior: slightly cool mids and shadows, neutral highlights
///   night:    cool blue shadows, neutral highlights
///   dusk:     warm mids, a touch of warmth in the shadows
///   all:      a gentle S-curve (AgX flattens the darks a little)
/// </code>
/// </summary>
public static class LookGrade
{
    /// <summary>Entries a side: a strip of <see cref="Size"/> squares, each Size × Size.</summary>
    public const int Size = 32;

    /// <summary>The S-curve's strength: the share of a smoothstep blended in (0: none).</summary>
    public const float Contrast = 0.12f;

    /// <summary>The tints (sRGB offsets at full weight): shadows weighted by (1 - luma)², mids by
    /// 4·luma·(1 - luma); highlights get none, so white stays white.</summary>
    public static readonly Vector3 InteriorShadows = new(-0.008f, 0f, 0.010f);
    public static readonly Vector3 InteriorMids = new(-0.005f, 0f, 0.006f);
    public static readonly Vector3 NightShadows = new(-0.012f, -0.002f, 0.026f);
    public static readonly Vector3 NightMids = new(-0.006f, 0f, 0.010f);
    public static readonly Vector3 DuskShadows = new(0.012f, 0f, -0.010f);
    public static readonly Vector3 DuskMids = new(0.030f, 0.010f, -0.028f);

    private static readonly ColourGrade Interior = Build(InteriorShadows, InteriorMids);
    private static readonly ColourGrade Night = Build(NightShadows, NightMids);
    private static readonly ColourGrade Dusk = Build(DuskShadows, DuskMids);

    /// <summary>The preset's grade: the same instance every call.</summary>
    public static ColourGrade Of(LightPreset preset) => preset switch
    {
        LightPreset.Night => Night,
        LightPreset.Dusk => Dusk,
        _ => Interior,
    };

    /// <summary>The grade of one sRGB colour: the S-curve, then the shadow and mid tints.</summary>
    public static Vector3 Apply(Vector3 colour, Vector3 shadows, Vector3 mids)
    {
        var curved = Vector3.Lerp(colour, colour * colour * (new Vector3(3f) - 2f * colour), Contrast);
        var luma = Vector3.Dot(curved, new Vector3(0.2126f, 0.7152f, 0.0722f));
        var dark = (1f - luma) * (1f - luma);
        var mid = 4f * luma * (1f - luma);
        return Vector3.Clamp(curved + shadows * dark + mids * mid, Vector3.Zero, Vector3.One);
    }

    /// <summary>A strip (RGBA, 8 bits a channel, rows from the top) of <paramref name="grade"/>:
    /// <paramref name="size"/> squares side by side, one per blue step; in a square red runs
    /// across, green down. Width size², height size.
    /// <code>
    ///   ┌──────┬──────┬─────┬────────┐
    ///   │ b=0  │ b=1  │ ... │ b=n-1  │   pixel (b·n + r, g) = grade(r, g, b) / (n - 1)
    ///   └──────┴──────┴─────┴────────┘
    /// </code></summary>
    public static byte[] Strip(int size, Func<Vector3, Vector3> grade)
    {
        var width = size * size;
        var pixels = new byte[width * size * 4];
        var step = 1f / (size - 1);
        for (var g = 0; g < size; g++)
        {
            for (var b = 0; b < size; b++)
            {
                for (var r = 0; r < size; r++)
                {
                    var c = grade(new Vector3(r * step, g * step, b * step));
                    var at = (g * width + b * size + r) * 4;
                    pixels[at] = ToByte(c.X);
                    pixels[at + 1] = ToByte(c.Y);
                    pixels[at + 2] = ToByte(c.Z);
                    pixels[at + 3] = byte.MaxValue;
                }
            }
        }

        return pixels;
    }

    private static ColourGrade Build(Vector3 shadows, Vector3 mids) =>
        ColourGrade.FromStrip(Strip(Size, c => Apply(c, shadows, mids)), Size);

    private static byte ToByte(float value) => (byte)MathF.Round(Math.Clamp(value, 0f, 1f) * byte.MaxValue);
}
