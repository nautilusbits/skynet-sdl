namespace SkyNet.Upscale;

/// <summary>An 8-bit RGB image, rows top-down, no padding.</summary>
public readonly record struct RgbImage(int Width, int Height, byte[] Rgb);

/// <summary>Something that scales images by a whole factor, all of a batch in one go
/// (<see cref="RealEsrganUpscaler"/>; tests use a nearest one).</summary>
public interface IImageUpscaler
{
    int Factor { get; }

    /// <summary>Upscales every image; <paramref name="done"/> gets the count finished so far. The
    /// results are read one at a time from what is returned (it may keep them on disk).</summary>
    IUpscaledBatch Upscale(IReadOnlyList<RgbImage> images, Action<int>? done, CancellationToken cancel);
}

/// <summary>A batch's results, in the order of its inputs.</summary>
public interface IUpscaledBatch : IDisposable
{
    RgbImage this[int index] { get; }
}

/// <summary>
/// What a cut-out texture goes through on its way through an upscaler (docs/hires-textures.md,
/// tools/upscale/upscale.py), game-agnostic:
/// <code>
///   RGB + opaque mask ─► holes filled from their neighbours (growing inwards)
///                     ─► padded (wrapped for a repeating surface, edge-copied for a sprite)
///                     ─► the upscaler ─► padding cropped ─► alpha = the mask by nearest,
///                        hole RGB = a given colour
/// </code>
/// so no upscaler sees black around a cut-out and a tiling texture keeps its seams invisible.
/// </summary>
public static class TexturePipeline
{
    /// <summary>Source texels of padding around each frame.</summary>
    public const int Pad = 8;

    private static readonly (int Dy, int Dx)[] Neighbours = [(-1, 0), (1, 0), (0, -1), (0, 1), (-1, -1), (-1, 1), (1, -1), (1, 1)];

    /// <summary>Hole texels (mask false) take the mean of their opaque neighbours, growing inwards
    /// a ring at a time (<paramref name="wrap"/>: neighbours across the edges). In place on
    /// <paramref name="rgb"/> (3 floats a texel).</summary>
    public static void FillHoles(float[] rgb, ReadOnlySpan<bool> mask, int width, int height, bool wrap)
    {
        var known = mask.ToArray();
        if (Array.TrueForAll(known, k => k) || !Array.Exists(known, k => k))
        {
            return;
        }

        var n = width * height;
        var total = new float[n * 3];
        var count = new float[n];
        var grow = new bool[n];
        for (var pass = 0; pass < Math.Max(width, height); pass++)
        {
            if (Array.TrueForAll(known, k => k))
            {
                break;
            }

            Array.Clear(total);
            Array.Clear(count);
            foreach (var (dy, dx) in Neighbours)
            {
                for (var y = 0; y < height; y++)
                {
                    for (var x = 0; x < width; x++)
                    {
                        // The texel (dy, dx) away, as numpy's roll / a zero-padded shift reads it.
                        int sy = y - dy, sx = x - dx;
                        if (wrap)
                        {
                            sy = (sy + height) % height;
                            sx = (sx + width) % width;
                        }
                        else if (sy < 0 || sy >= height || sx < 0 || sx >= width)
                        {
                            continue;
                        }

                        var s = sy * width + sx;
                        var d = y * width + x;
                        var k = known[s] ? 1f : 0f;
                        count[d] += k;
                        total[d * 3] += rgb[s * 3] * k;
                        total[d * 3 + 1] += rgb[s * 3 + 1] * k;
                        total[d * 3 + 2] += rgb[s * 3 + 2] * k;
                    }
                }
            }

            for (var i = 0; i < n; i++)
            {
                grow[i] = !known[i] && count[i] > 0;
                if (grow[i])
                {
                    rgb[i * 3] = total[i * 3] / count[i];
                    rgb[i * 3 + 1] = total[i * 3 + 1] / count[i];
                    rgb[i * 3 + 2] = total[i * 3 + 2] / count[i];
                }
            }

            for (var i = 0; i < n; i++)
            {
                known[i] |= grow[i];
            }
        }
    }

    /// <summary>The frame rounded to bytes and padded by <paramref name="pad"/> texels on every
    /// side: wrapped (<paramref name="wrap"/>) or with the edge texels repeated.</summary>
    public static RgbImage Padded(float[] rgb, int width, int height, bool wrap, int pad = Pad)
    {
        int pw = width + 2 * pad, ph = height + 2 * pad;
        var output = new byte[pw * ph * 3];
        for (var y = 0; y < ph; y++)
        {
            var sy = wrap ? Mod(y - pad, height) : Math.Clamp(y - pad, 0, height - 1);
            for (var x = 0; x < pw; x++)
            {
                var sx = wrap ? Mod(x - pad, width) : Math.Clamp(x - pad, 0, width - 1);
                for (var c = 0; c < 3; c++)
                {
                    output[(y * pw + x) * 3 + c] = (byte)Math.Clamp(rgb[(sy * width + sx) * 3 + c] + 0.5f, 0f, 255f);
                }
            }
        }

        return new RgbImage(pw, ph, output);
    }

    /// <summary>An upscaled padded frame cropped back to the frame's box and written as straight
    /// RGBA into <paramref name="rgba"/> (width·factor × height·factor): alpha is
    /// <paramref name="mask"/> by nearest, a hole's RGB is <paramref name="hole"/>.</summary>
    public static void Finish(RgbImage scaled, int factor, ReadOnlySpan<bool> mask, int width, int height,
        (byte R, byte G, byte B) hole, Span<byte> rgba, int pad = Pad)
    {
        int ow = width * factor, oh = height * factor, crop = pad * factor;
        if (scaled.Width != ow + 2 * crop || scaled.Height != oh + 2 * crop)
        {
            throw new InvalidDataException($"upscaled frame is {scaled.Width}x{scaled.Height}, expected {ow + 2 * crop}x{oh + 2 * crop}");
        }

        for (var y = 0; y < oh; y++)
        {
            for (var x = 0; x < ow; x++)
            {
                var o = (y * ow + x) * 4;
                if (mask[y / factor * width + x / factor])
                {
                    var s = ((y + crop) * scaled.Width + x + crop) * 3;
                    rgba[o] = scaled.Rgb[s];
                    rgba[o + 1] = scaled.Rgb[s + 1];
                    rgba[o + 2] = scaled.Rgb[s + 2];
                    rgba[o + 3] = 255;
                }
                else
                {
                    (rgba[o], rgba[o + 1], rgba[o + 2], rgba[o + 3]) = (hole.R, hole.G, hole.B, 0);
                }
            }
        }
    }

    private static int Mod(int a, int m) => ((a % m) + m) % m;
}
