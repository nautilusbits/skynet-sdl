using System.Numerics;
using SkyNet.Formats;

namespace SkyNet.Game.Water;

/// <summary>
/// How water looks in SKYNET.EXE v1.01. There is no surface polygon: after the 3D frame
/// FUN_00121148 runs FUN_0015859c (unless [0x38904] = 2, the torpedo ride), which walks the span
/// buffer row by row below the water plane's horizon and, for every span farther than the plane at
/// that row, remaps the pixels through one HAZE row (FUN_00120f85 copies HAZE.005 row 8 for marker
/// 103, HAZE.006 row 3 for 104, to [0x38914]), each read from x − 1, x or x + 1 (a ±4 random walk
/// per row and frame: a one-pixel shimmer). Under water ([0x30a50] bit 0x4000000) the palette is
/// SKYNTWTR.COL instead of SKYNET.COL ([0x53700] := [0x5001d], FUN_0012ea6b).
/// The original look draws from palette indices, so it applies the HAZE row itself
/// (<see cref="Remap"/>, through the renderer's plane remap, with the shimmer). The enhanced look's
/// frame is RGB: there both remaps are fitted per channel as c' = a·c + b over the palette and drawn
/// as an alpha blend: (1 − α)·c + α·C with α = 1 − a, C = b / α. The row is a light touch: 87 of
/// the 256 indices stay, the rest step to a neighbour a little bluer or lighter (darks and greys
/// +8..+20 blue, the saturated ramps one step darker).
/// </summary>
/// <param name="Remap">The HAZE row (256 indices: index → index); null when the file is missing.</param>
public readonly record struct WaterLook(Vector4 Surface, Vector4 Underwater, byte[]? Remap = null)
{
    /// <summary>HAZE.005 row 8 through SKYNET.COL, fitted: ≈ 0.9·c + (10, 12, 19).</summary>
    public static readonly Vector4 DefaultSurface = new(100f / 255f, 120f / 255f, 190f / 255f, 0.1f);
    /// <summary>SKYNTWTR.COL = 0.5·c + (35, 35, 126) of SKYNET.COL.</summary>
    public static readonly Vector4 DefaultUnderwater = new(70f / 255f, 70f / 255f, 252f / 255f, 0.5f);
    public const string UnderwaterPalette = "SKYNTWTR.COL";

    /// <summary>The look of <paramref name="water"/> from the data, each part falling back to its default.</summary>
    public static WaterLook Load(GameData data, Palette palette, WaterLevel water)
    {
        var surface = DefaultSurface;
        byte[]? remap = null;
        if (data.ReadLoose(water.HazeFile) is { } bytes && HazeTable.Parse(bytes) is { } haze)
        {
            var row = haze.Row(water.HazeRow).ToArray();
            surface = Fit(palette, i => palette[row[i]]) ?? surface;
            remap = row;
        }

        var underwater = DefaultUnderwater;
        if (data.ColBytes(UnderwaterPalette) is { } col && Palette.Parse(col) is { } wet)
        {
            underwater = Fit(palette, i => wet[i]) ?? underwater;
        }

        return new WaterLook(surface, underwater, remap);
    }

    /// <summary>The blend colour (rgb) and alpha that best turn each colour of <paramref name="palette"/>
    /// into <paramref name="target"/>'s: a least-squares c' = a·c + b per channel, α = 1 − mean a.
    /// Null when the remap does not darken towards a colour (α outside (0, 1]).</summary>
    public static Vector4? Fit(Palette palette, Func<int, (byte R, byte G, byte B)> target)
    {
        Span<float> a = stackalloc float[3];
        Span<float> b = stackalloc float[3];
        for (var ch = 0; ch < 3; ch++)
        {
            double sx = 0, sy = 0, sxx = 0, sxy = 0;
            for (var i = 0; i < Palette.Colors; i++)
            {
                var c = palette[i];
                var d = target(i);
                double x = ch == 0 ? c.R : ch == 1 ? c.G : c.B;
                double y = ch == 0 ? d.R : ch == 1 ? d.G : d.B;
                sx += x;
                sy += y;
                sxx += x * x;
                sxy += x * y;
            }

            const double n = Palette.Colors;
            var den = n * sxx - sx * sx;
            var slope = den == 0 ? 1.0 : (n * sxy - sx * sy) / den;
            a[ch] = (float)slope;
            b[ch] = (float)((sy - slope * sx) / n);
        }

        var alpha = 1f - (a[0] + a[1] + a[2]) / 3f;
        if (alpha <= 1e-3f || alpha > 1f)
        {
            return null;
        }

        return new Vector4(
            Math.Clamp(b[0] / alpha / 255f, 0f, 1f),
            Math.Clamp(b[1] / alpha / 255f, 0f, 1f),
            Math.Clamp(b[2] / alpha / 255f, 0f, 1f),
            alpha);
    }
}
