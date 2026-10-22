namespace SkyNet.Upscale;

/// <summary>
/// A surface's relief guessed from its upscaled colour (docs/hires-textures.md, the same as
/// tools/upscale/upscale.py's <c>relief_maps</c>), for the enhanced look's lit path, game-agnostic:
/// <code>
///   luminance ─► blurred (σ 0.6 source texel: the old art's dither stays flat) ─► Sobel ─► tangent-space normal
///   luminance, saturation ─► local mean and contrast (σ 1.5 source texels) ─► roughness
/// </code>
/// The normal: x along the image's columns (+u), y down its rows (+v), z out of the surface, each
/// stored as 128 + 127 n (flat: exactly 128, 128, 255). Roughness: 0-255 for 0-1; a busy or
/// contrasty texel is rough, a grey, fairly bright, even one (a metal panel) smoother. Each frame
/// of a stack on its own.
/// </summary>
public static class ReliefMaps
{
    /// <summary>The height's blur (σ, source texels).</summary>
    public const float HeightBlur = 0.6f;
    /// <summary>How deep luminance 0..1 is, in source texels.</summary>
    public const float Depth = 6f;
    /// <summary>The roughness's window (σ, source texels).</summary>
    public const float ContrastBlur = 1.5f;
    public const float BaseRoughness = 0.85f;
    public const float ContrastRoughness = 1.5f;
    public const float MetalSmoothing = 0.45f;
    public const float MinRoughness = 0.3f;

    /// <summary>The maps of straight RGBA8 <paramref name="width"/> x <paramref name="height"/>
    /// (<paramref name="frames"/> stacked downwards), upscaled <paramref name="scale"/> times from
    /// the art; <paramref name="wrap"/>: it tiles (neighbours across the edges). Returns RGB (the
    /// normal) and one byte a texel (the roughness).</summary>
    public static (byte[] Normal, byte[] Roughness) Make(ReadOnlySpan<byte> rgba, int width, int height, int frames, int scale, bool wrap)
    {
        if (frames < 1 || height % frames != 0 || rgba.Length < width * height * 4)
        {
            throw new ArgumentException($"{width}x{height} in {frames} frames: {rgba.Length} bytes");
        }

        var normal = new byte[width * height * 3];
        var roughness = new byte[width * height];
        var fh = height / frames;
        var n = width * fh;
        for (var f = 0; f < frames; f++)
        {
            Frame(rgba.Slice(f * n * 4, n * 4), width, fh, Math.Max(scale, 1), wrap, normal.AsSpan(f * n * 3, n * 3), roughness.AsSpan(f * n, n));
        }

        return (normal, roughness);
    }

    private static void Frame(ReadOnlySpan<byte> rgba, int w, int h, int scale, bool wrap, Span<byte> normal, Span<byte> roughness)
    {
        var n = w * h;
        var lum = new float[n];
        var square = new float[n];
        var sat = new float[n];
        for (var i = 0; i < n; i++)
        {
            float r = rgba[i * 4] / 255f, g = rgba[i * 4 + 1] / 255f, b = rgba[i * 4 + 2] / 255f;
            lum[i] = 0.2126f * r + 0.7152f * g + 0.0722f * b;
            square[i] = lum[i] * lum[i];
            float max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
            sat[i] = max > 0f ? (max - min) / max : 0f;
        }

        var heights = Blur(lum, w, h, HeightBlur * scale, wrap);
        var k = Depth * scale / 8f;
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                float At(int dx, int dy) => heights[Index(y + dy, h, wrap) * w + Index(x + dx, w, wrap)];
                var gx = At(1, -1) + 2f * At(1, 0) + At(1, 1) - At(-1, -1) - 2f * At(-1, 0) - At(-1, 1);
                var gy = At(-1, 1) + 2f * At(0, 1) + At(1, 1) - At(-1, -1) - 2f * At(0, -1) - At(1, -1);
                float nx = -gx * k, ny = -gy * k;
                var inv = 1f / MathF.Sqrt(nx * nx + ny * ny + 1f);
                var o = (y * w + x) * 3;
                normal[o] = Unit(nx * inv);
                normal[o + 1] = Unit(ny * inv);
                normal[o + 2] = Unit(inv);
            }
        }

        var sigma = ContrastBlur * scale;
        var mean = Blur(lum, w, h, sigma, wrap);
        var meanSquare = Blur(square, w, h, sigma, wrap);
        var meanSat = Blur(sat, w, h, sigma, wrap);
        for (var i = 0; i < n; i++)
        {
            var contrast = MathF.Sqrt(Math.Max(meanSquare[i] - mean[i] * mean[i], 0f));
            var metal = (1f - Smooth(0.12f, 0.35f, meanSat[i])) * Smooth(0.2f, 0.55f, mean[i]) * (1f - Smooth(0.04f, 0.12f, contrast));
            var rough = Math.Clamp(BaseRoughness + ContrastRoughness * contrast - MetalSmoothing * metal, MinRoughness, 1f);
            roughness[i] = (byte)MathF.Round(rough * 255f);
        }
    }

    /// <summary>A separable Gaussian blur (radius 3σ), wrapped or edge-clamped.</summary>
    private static float[] Blur(float[] source, int w, int h, float sigma, bool wrap)
    {
        var radius = (int)MathF.Ceiling(3f * sigma);
        var weights = new float[2 * radius + 1];
        var total = 0f;
        for (var i = -radius; i <= radius; i++)
        {
            weights[i + radius] = MathF.Exp(-i * i / (2f * sigma * sigma));
            total += weights[i + radius];
        }

        for (var i = 0; i < weights.Length; i++)
        {
            weights[i] /= total;
        }

        var across = new float[w * h];
        var result = new float[w * h];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var sum = 0f;
                for (var i = -radius; i <= radius; i++)
                {
                    sum += weights[i + radius] * source[y * w + Index(x + i, w, wrap)];
                }

                across[y * w + x] = sum;
            }
        }

        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var sum = 0f;
                for (var i = -radius; i <= radius; i++)
                {
                    sum += weights[i + radius] * across[Index(y + i, h, wrap) * w + x];
                }

                result[y * w + x] = sum;
            }
        }

        return result;
    }

    private static int Index(int i, int size, bool wrap) => wrap ? ((i % size) + size) % size : Math.Clamp(i, 0, size - 1);

    private static byte Unit(float v) => (byte)Math.Clamp(MathF.Round(128f + 127f * v), 1f, 255f);

    private static float Smooth(float edge0, float edge1, float x)
    {
        var t = Math.Clamp((x - edge0) / (edge1 - edge0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }
}
