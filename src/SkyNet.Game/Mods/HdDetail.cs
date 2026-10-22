using System.Numerics;
using RetroEng.Render;

namespace SkyNet.Game.Mods;

/// <summary>What a texture's texels hold, which says how a box of them is averaged.</summary>
internal enum Texels
{
    /// <summary>sRGB colour, straight alpha: averaged in linear light, weighted by alpha.</summary>
    Colour,
    /// <summary>sRGB colour whose alpha is ignored (drawn opaque): averaged in linear light, alpha 255.</summary>
    Opaque,
    /// <summary>Plain values (occlusion, roughness, metal): the bytes averaged.</summary>
    Data,
    /// <summary>A tangent-space normal (127.5 + 127.5 n): averaged, made unit again; alpha as data.</summary>
    Normal,
}

/// <summary>
/// DETAIL's texture quality for HD and mod textures (DOS textures stay as they are): HIGH full
/// size, MED half each side (a quarter of the memory), LOW a quarter (a sixteenth). Shrunk on the
/// CPU with a box filter as the images are decoded, so the GPU gets the smaller texture.
/// </summary>
internal static class HdDetail
{
    /// <summary>Each side divided by: HIGH 1, MED 2, LOW 4.</summary>
    internal static int Divisor(Detail detail) => detail switch
    {
        Detail.Low => 4,
        Detail.Medium => 2,
        _ => 1,
    };

    /// <summary>The divisor for a record's HD texture <paramref name="scale"/> times the record's
    /// size: the largest power of two up to <see cref="Divisor"/> that divides the scale, so the
    /// shrunk texture is still a whole multiple of the record (x4 at LOW: 4; x6 at LOW: 2; x3: 1).</summary>
    internal static int Factor(int scale, Detail detail)
    {
        var f = Divisor(detail);
        while (f > 1 && (scale < f || scale % f != 0))
        {
            f /= 2;
        }

        return f;
    }

    /// <summary>An image with each side divided by <paramref name="divisor"/> (at least 1 texel);
    /// divisor 1: the image itself.</summary>
    internal static (int W, int H, byte[] Rgba) Shrink((int W, int H, byte[] Rgba) image, int divisor, Texels kind)
    {
        if (divisor <= 1)
        {
            return image;
        }

        var (w, h) = (Math.Max(1, image.W / divisor), Math.Max(1, image.H / divisor));
        if ((w, h) == (image.W, image.H))
        {
            return image;
        }

        return (w, h, Shrink(image.Rgba, image.W, image.H, w, h, kind));
    }

    /// <summary>RGBA8 <paramref name="w"/> x <paramref name="h"/> boxed down to <paramref name="nw"/>
    /// x <paramref name="nh"/>: each target texel the mean of the source texels it covers (an uneven
    /// side spreads them: 3 to 1 takes all three).</summary>
    internal static byte[] Shrink(byte[] rgba, int w, int h, int nw, int nh, Texels kind)
    {
        var target = new byte[nw * nh * 4];
        for (var y = 0; y < nh; y++)
        {
            var (y0, y1) = (y * h / nh, Math.Max(y * h / nh + 1, (y + 1) * h / nh));
            for (var x = 0; x < nw; x++)
            {
                var (x0, x1) = (x * w / nw, Math.Max(x * w / nw + 1, (x + 1) * w / nw));
                Box(rgba, w, x0, x1, y0, y1, kind, target.AsSpan((y * nw + x) * 4, 4));
            }
        }

        return target;
    }

    /// <summary>One target texel: the source box [x0, x1) x [y0, y1) averaged as its kind says.</summary>
    private static void Box(byte[] rgba, int w, int x0, int x1, int y0, int y1, Texels kind, Span<byte> into)
    {
        var lin = Srgb.ToLinearTable;
        var sum = Vector4.Zero;
        var weighted = Vector3.Zero;
        for (var y = y0; y < y1; y++)
        {
            for (var x = x0; x < x1; x++)
            {
                var i = (y * w + x) * 4;
                var (r, g, b, a) = (rgba[i], rgba[i + 1], rgba[i + 2], rgba[i + 3]);
                if (kind is Texels.Colour or Texels.Opaque)
                {
                    var c = new Vector3(lin[r], lin[g], lin[b]);
                    sum += new Vector4(c, a);
                    weighted += c * a;
                    continue;
                }

                sum += kind == Texels.Normal
                    ? new Vector4(new Vector3(r, g, b) / Half - Vector3.One, a)
                    : new Vector4(r, g, b, a);
            }
        }

        var n = (x1 - x0) * (y1 - y0);
        switch (kind)
        {
            case Texels.Colour:
                // Clear texels add no colour (no dark fringe); all clear: the plain mean.
                var colour = sum.W > 0f ? weighted / sum.W : new Vector3(sum.X, sum.Y, sum.Z) / n;
                Store(into, colour, sum.W / n);
                break;
            case Texels.Opaque:
                Store(into, new Vector3(sum.X, sum.Y, sum.Z) / n, Opaque);
                break;
            case Texels.Normal:
                var v = new Vector3(sum.X, sum.Y, sum.Z);
                v = v.LengthSquared() > 1e-12f ? Vector3.Normalize(v) : Vector3.UnitZ;
                var e = (v + Vector3.One) * Half;
                into[0] = Byte(e.X);
                into[1] = Byte(e.Y);
                into[2] = Byte(e.Z);
                into[3] = Byte(sum.W / n);
                break;
            default:
                var m = sum / n;
                (into[0], into[1], into[2], into[3]) = (Byte(m.X), Byte(m.Y), Byte(m.Z), Byte(m.W));
                break;
        }
    }

    /// <summary>A normal byte's midpoint: n = byte / 127.5 - 1.</summary>
    private const float Half = 127.5f;

    private const float Opaque = 255f;

    private static void Store(Span<byte> into, Vector3 linear, float alpha)
    {
        into[0] = ToSrgb(linear.X);
        into[1] = ToSrgb(linear.Y);
        into[2] = ToSrgb(linear.Z);
        into[3] = Byte(alpha);
    }

    private static byte Byte(float v) => (byte)Math.Clamp((int)(v + 0.5f), 0, 255);

    /// <summary>Linear 0-1 to sRGB bytes in 4096 steps (a pow per texel is slow).</summary>
    private static readonly byte[] ToSrgbTable = [.. Enumerable.Range(0, 4096).Select(i => Srgb.FromLinear(i / 4095f))];

    private static byte ToSrgb(float linear) => ToSrgbTable[Math.Clamp((int)(linear * 4095f + 0.5f), 0, 4095)];
}
