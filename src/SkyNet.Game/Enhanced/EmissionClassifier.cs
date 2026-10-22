using System.Numerics;
using SkyNet.Formats;

namespace SkyNet.Game.Enhanced;

/// <summary>What <see cref="EmissionClassifier"/> found in one texture record.</summary>
/// <param name="Emissive">The record is a light source (lamp, strip light, lit screen).</param>
/// <param name="LitFraction">Lit texels over counted texels (0 when none are counted).</param>
/// <param name="MeanLitColour">The mean colour of the lit texels, 0–1 sRGB (zero when none is lit):
/// the lamp colour for the lights made from it later.</param>
/// <param name="LitTexels">Texels at or above the cut.</param>
/// <param name="CountedTexels">Texels counted (index 0 left out unless asked for).</param>
/// <param name="Mask">When asked for and the record is emissive: the record's palette indices with
/// every unlit texel set to 0 — an index image of only the light, for the palette pipeline.</param>
public readonly record struct EmissionResult(
    bool Emissive, float LitFraction, Vector3 MeanLitColour, int LitTexels, int CountedTexels, byte[]? Mask);

/// <summary>
/// Which DOS texture records are LIGHT: strip lights, lit panels, the glowing screens on a console,
/// the sodium heads of a street lamp. The DOS artists painted those bright; there is no light map,
/// the pixels are the lamp. Ported from the Godot port's ENHANCED mode (asset_cache.gd
/// <c>emission()</c>, removed with that mode):
/// <code>
///   texel lit  ⇔ max(luma, 0.85 · max channel) ≥ EMISSION_CUT     (integer math, as Godot did)
///   emissive   ⇔ EMISSION_MIN ≤ lit / counted ≤ EMISSION_MAX
/// </code>
/// Under the minimum it's a stray highlight; over the maximum it's a pale wall, and a glowing wall
/// would wash the room out.
/// Differences from the Godot scan: it read the ENHANCED image (upscaled 4×, then bilinear-resized
/// to at most <see cref="GodotScan"/> on the long side); this reads the record's own indices, 1:1.
/// Godot also counted index 0 as an opaque texel of the palette's colour 0 (the image was made
/// with transparent0 = false); here index 0 is transparent and left out of the count unless
/// <c>countIndexZero</c> is set (it's black in SKYNET.COL, so it's never lit either way).
/// </summary>
public static class EmissionClassifier
{
    /// <summary>asset_cache.gd EMISSION_CUT: the lit threshold, 0–1.</summary>
    public const float EmissionCut = 0.84f;
    /// <summary>asset_cache.gd EMISSION_MIN: below this lit fraction a record is not a lamp.</summary>
    public const float EmissionMin = 0.004f;
    /// <summary>asset_cache.gd EMISSION_MAX: above this lit fraction a record is a pale wall.</summary>
    public const float EmissionMax = 0.22f;
    /// <summary>asset_cache.gd EMISSION_SCAN: Godot's scan resolution (not used here, see above).</summary>
    public const int GodotScan = 96;
    /// <summary>The cut in 0–255 as Godot computed it: int(0.84 · 255) = 214.</summary>
    public static readonly int Cut8 = (int)(EmissionCut * 255.0);

    /// <summary>Whether an 8-bit colour is lit: Godot's integer luma (77, 151, 28) / 256, raised to
    /// 85 % of the brightest channel when that is more (a saturated warning strip counts as a lamp
    /// at a lower brightness than a white one), at or above <see cref="Cut8"/>.</summary>
    public static bool IsLit(byte r, byte g, byte b)
    {
        var l = (r * 77 + g * 151 + b * 28) >> 8;
        var top = Math.Max(r, Math.Max(g, b)) * 85 / 100;
        return Math.Max(l, top) >= Cut8;
    }

    /// <summary>The lit flag of every palette index (index 0 included; the caller skips it).</summary>
    public static bool[] LitTable(Palette palette)
    {
        var lit = new bool[Palette.Colors];
        for (var i = 0; i < Palette.Colors; i++)
        {
            var (r, g, b) = palette[i];
            lit[i] = IsLit(r, g, b);
        }

        return lit;
    }

    /// <summary>Classifies a record's palette-index pixels.</summary>
    public static EmissionResult Classify(ReadOnlySpan<byte> pixels, Palette palette, bool wantMask = false,
        bool countIndexZero = false) =>
        Classify(pixels, palette, LitTable(palette), wantMask, countIndexZero);

    /// <summary>Classifies a record with a <see cref="LitTable"/> made once for the palette.</summary>
    public static EmissionResult Classify(ReadOnlySpan<byte> pixels, Palette palette, bool[] litTable,
        bool wantMask = false, bool countIndexZero = false)
    {
        var counted = 0;
        var lit = 0;
        long sr = 0, sg = 0, sb = 0;
        foreach (var index in pixels)
        {
            if (index == 0 && !countIndexZero)
            {
                continue;
            }

            counted++;
            // Index 0 counts (when asked) but never lights: Godot's colour 0 was black.
            if (index == 0 || !litTable[index])
            {
                continue;
            }

            lit++;
            var (r, g, b) = palette[index];
            sr += r;
            sg += g;
            sb += b;
        }

        var fraction = counted > 0 ? lit / (float)counted : 0f;
        var emissive = counted > 0 && fraction >= EmissionMin && fraction <= EmissionMax;
        var mean = lit > 0 ? new Vector3(sr, sg, sb) / (lit * 255f) : Vector3.Zero;
        byte[]? mask = null;
        if (wantMask && emissive)
        {
            mask = new byte[pixels.Length];
            for (var i = 0; i < pixels.Length; i++)
            {
                var index = pixels[i];
                mask[i] = index != 0 && litTable[index] ? index : (byte)0;
            }
        }

        return new EmissionResult(emissive, fraction, mean, lit, counted, mask);
    }

    /// <summary>Classifies a TEXTURE.NNN record (its first frame); an empty record is not emissive.</summary>
    public static EmissionResult Classify(TextureRecord record, Palette palette, bool[] litTable, bool wantMask = false) =>
        record.IsEmpty
            ? default
            : Classify(record.Pixels.AsSpan()[..Math.Min(record.Pixels.Length, record.Width * record.Height)],
                palette, litTable, wantMask);
}
