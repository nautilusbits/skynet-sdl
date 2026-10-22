namespace SkyNet.Edit;

/// <summary>
/// Seeded 2D value noise and its fractal sum, for the terrain generator and the noise brush. Integer
/// hashing only (no <see cref="Random"/>), so a seed gives the same field on every machine and runtime.
/// </summary>
public static class TerrainNoise
{
    /// <summary>Smooth value noise at (x, y) in [−1, 1]; lattice points every 1.0.</summary>
    public static double Value(int seed, double x, double y)
    {
        var (x0, y0) = ((int)Math.Floor(x), (int)Math.Floor(y));
        var (u, v) = (Fade(x - x0), Fade(y - y0));
        var a = Lerp(Lattice(seed, x0, y0), Lattice(seed, x0 + 1, y0), u);
        var b = Lerp(Lattice(seed, x0, y0 + 1), Lattice(seed, x0 + 1, y0 + 1), u);
        return Lerp(a, b, v);
    }

    /// <summary>
    /// Fractal sum of <paramref name="octaves"/> layers of <see cref="Value"/>, each <paramref name="lacunarity"/>
    /// times finer and <paramref name="persistence"/> times weaker, scaled back into [−1, 1].
    /// </summary>
    public static double Fractal(int seed, double x, double y, int octaves, double persistence = 0.5, double lacunarity = 2.0)
    {
        double sum = 0, amplitude = 1, total = 0, f = 1;
        for (var o = 0; o < Math.Max(octaves, 1); o++)
        {
            sum += Value(seed + o * 1013, x * f, y * f) * amplitude;
            total += amplitude;
            amplitude *= persistence;
            f *= lacunarity;
        }

        return sum / total;
    }

    /// <summary>A lattice point's value in [−1, 1].</summary>
    private static double Lattice(int seed, int x, int y) => Hash(seed, x, y) / (double)uint.MaxValue * 2.0 - 1.0;

    /// <summary>A 32-bit avalanche hash of (seed, x, y) (murmur3's finaliser).</summary>
    private static uint Hash(int seed, int x, int y)
    {
        var h = (uint)seed * 0x9E3779B1u ^ (uint)x * 0x85EBCA77u ^ (uint)y * 0xC2B2AE3Du;
        h ^= h >> 16;
        h *= 0x85EBCA6Bu;
        h ^= h >> 13;
        h *= 0xC2B2AE35u;
        h ^= h >> 16;
        return h;
    }

    private static double Fade(double t) => t * t * (3 - 2 * t);

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;
}
