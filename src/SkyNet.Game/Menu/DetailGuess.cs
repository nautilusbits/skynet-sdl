using System.Text.RegularExpressions;

namespace SkyNet.Game.Menu;

/// <summary>
/// DETAIL on a first start (no saved setting), guessed from the GPU's name: SDL_GPU tells no video
/// memory, so a GPU known to share memory or to have little gets less (DETAIL also picks the HD
/// textures' size: <see cref="Mods.HdDetail"/>). The first rule that matches wins; none: HIGH.
/// <code>
///   phones, software        Adreno, Mali, PowerVR, Xclipse, Immortalis, llvmpipe   LOW
///   old Intel integrated    Intel HD Graphics                                     LOW
///   Intel discrete          Arc A770, Arc B580                                    HIGH
///   integrated              Intel UHD / Iris / Arc Graphics, Radeon Graphics,     MED
///                           Radeon Vega, Radeon 780M, Apple
///   2-4 GB cards            GeForce MX, GTX 1050, GTX 1650, GT 1030, RX 550/560/6400/6500   MED
/// </code>
/// </summary>
public static class DetailGuess
{
    private static readonly (Regex Gpu, Detail Detail)[] Rules =
    [
        (Rule(@"Adreno|Mali|PowerVR|Xclipse|Immortalis|Maleoon|llvmpipe|SwiftShader|Basic Render"), Detail.Low),
        (Rule(@"Intel.*\bHD Graphics"), Detail.Low),
        (Rule(@"Intel.*\bArc\b.*\b[AB]\d{3}"), Detail.High),
        (Rule(@"Intel|Apple"), Detail.Medium),
        (Rule(@"Radeon(\(TM\))?\s*(Graphics|Vega|\d{3}M\b)"), Detail.Medium),
        (Rule(@"\bMX\s?\d{3}|GTX 1050|GTX 1650|GT 1030|\bRX 5[56]0\b|RX 6[45]00"), Detail.Medium),
    ];

    /// <summary>The first DETAIL for a GPU named <paramref name="gpu"/> (GpuInfo.Device).</summary>
    public static Detail Pick(string gpu)
    {
        foreach (var (pattern, detail) in Rules)
        {
            if (pattern.IsMatch(gpu))
            {
                return detail;
            }
        }

        return Detail.High;
    }

    private static Regex Rule(string pattern) => new(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}
