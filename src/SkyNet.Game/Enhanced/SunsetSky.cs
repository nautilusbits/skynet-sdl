using System.Numerics;
using RetroEng.Render;

namespace SkyNet.Game.Enhanced;

/// <summary>
/// A dusk map's sky in the enhanced look (sRGB colours 0-1, angles in degrees): the gradient from
/// the horizon up, the sunset's glow, and a thin cloud deck lit from the sun's side.
/// </summary>
/// <param name="Horizon">The sky at the horizon and below it: the haze the terrain fades into.</param>
/// <param name="Low">The deep red a little over the horizon (<see cref="SunsetSky.LowElevation"/>).</param>
/// <param name="Zenith">The sky straight up.</param>
/// <param name="Glow">The sunset's glow round the sun and along the horizon under it.</param>
/// <param name="SunElevation">How high the glow's centre stands (degrees; the light keeps its own height).</param>
/// <param name="GlowWidth">The glow's half width along the horizon (degrees).</param>
/// <param name="Cover">The cloud deck's cover (0: none).</param>
/// <param name="Clouds">The deck's colour.</param>
internal sealed record SunsetLook(Vector3 Horizon, Vector3 Low, Vector3 Zenith, Vector3 Glow, float SunElevation, float GlowWidth,
    float Cover, Vector3 Clouds)
{
    /// <summary>The look kept from the DOS dome SKY_SKY.3D: its band's red at the horizon (palette
    /// 18 / band ≈ (125, 16, 10) below, (80, 9, 8) at eye height), its top cap (palette 26, (59, 8, 7))
    /// darkened at the zenith, the glow where its bright texels are.</summary>
    internal static SunsetLook Defaults { get; } = new(
        Horizon: new Vector3(0.44f, 0.1f, 0.06f),
        Low: new Vector3(0.36f, 0.05f, 0.035f),
        Zenith: new Vector3(0.09f, 0.018f, 0.03f),
        Glow: new Vector3(1f, 0.52f, 0.2f),
        SunElevation: 3f,
        GlowWidth: 38f,
        Cover: 0.3f,
        Clouds: new Vector3(0.06f, 0.02f, 0.02f));
}

/// <summary>
/// The enhanced look's dusk sky (the dusk maps, <see cref="Level.IsDusk"/>), in place of the DOS dome:
/// a sphere round the eye coloured per vertex, its pole at the sun so the glow is finely cut.
/// <code>
///   zenith ──── dark
///      │  smooth
///   low (≈12°) ─ deep red                 ┌─ glow: core round the sun + a wide band along
///      │                                  │  the horizon under it, nothing below the horizon
///   horizon ─── Horizon ═ the fog's colour ┘  (the far terrain fades into the same colour: no band)
///   below ───── Horizon
/// </code>
/// </summary>
internal static class SunsetSky
{
    /// <summary>Where <see cref="SunsetLook.Low"/> is reached, and where the zenith's colour is (degrees).</summary>
    internal const float LowElevation = 12f, ZenithElevation = 80f;
    /// <summary>The sphere's distance from the eye (units; inside the far clip, drawn without depth).</summary>
    internal const float Radius = 1000f;
    /// <summary>The sphere's rings out from the sun and segments round it.</summary>
    internal const int Rings = 64, Segments = 96;
    /// <summary>The rings crowd towards the sun by this power (a fine core).</summary>
    private const float RingCrowding = 1.6f;
    /// <summary>The glow's core: its brightness and width (the cosine's power).</summary>
    private const float CoreStrength = 1.4f, CorePower = 900f;
    /// <summary>The halo round the sun: its brightness and width.</summary>
    private const float HaloStrength = 0.55f, HaloPower = 24f;
    /// <summary>The band along the horizon: its brightness and how fast it thins upward (degrees).</summary>
    private const float BandStrength = 0.7f, BandHeight = 9f;
    /// <summary>The glow fades in across the horizon (degrees under to over it).</summary>
    private const float GlowFloor = -1.5f, GlowFull = 1f;

    /// <summary>The way to the sun the glow centres on: the light's heading (<paramref name="light"/>,
    /// the way it goes; null: <paramref name="fallback"/>'s), at <paramref name="elevation"/> degrees.</summary>
    internal static Vector3 SunOf(Vector3? light, Vector3 fallback, float elevation)
    {
        var to = -(light ?? fallback);
        var flat = new Vector2(to.X, to.Z);
        var heading = flat.LengthSquared() > 1e-12f ? Vector2.Normalize(flat) : Vector2.UnitX;
        var e = elevation * MathF.PI / 180f;
        return new Vector3(heading.X * MathF.Cos(e), MathF.Sin(e), heading.Y * MathF.Cos(e));
    }

    /// <summary>The sky's linear colour seen along <paramref name="way"/> (unit) with the sun along <paramref name="sun"/>.</summary>
    internal static Vector3 ColourAt(SunsetLook look, Vector3 way, Vector3 sun)
    {
        var e = Degrees(way.Y);
        var horizon = Linear(look.Horizon);

        // The gradient: horizon → deep red → zenith, flat below the horizon.
        var low = Vector3.Lerp(horizon, Linear(look.Low), SmoothStep(0f, LowElevation, e));
        var sky = Vector3.Lerp(low, Linear(look.Zenith), MathF.Pow(SmoothStep(LowElevation * 0.5f, ZenithElevation, e), 0.8f));

        // The glow: a core and halo round the sun, a band along the horizon under it (by heading).
        var c = MathF.Max(0f, Vector3.Dot(way, sun));
        var round = CoreStrength * MathF.Pow(c, CorePower) + HaloStrength * MathF.Pow(c, HaloPower);
        var off = HeadingApart(way, sun) / look.GlowWidth;
        var band = BandStrength * MathF.Exp(-off * off) * MathF.Exp(-MathF.Max(e, 0f) / BandHeight);
        var glow = Linear(look.Glow) * ((round + band) * SmoothStep(GlowFloor, GlowFull, e));
        return sky + glow;
    }

    /// <summary>The sky sphere: <see cref="Rings"/> rings out from the sun (its pole) to the far
    /// pole, <see cref="Segments"/> round, each vertex coloured by <see cref="ColourAt"/>.</summary>
    internal static (Vertex[] Vertices, ushort[] Indices) Mesh(SunsetLook look, Vector3 sun)
    {
        var u = Vector3.Normalize(Vector3.Cross(MathF.Abs(sun.Y) < 0.99f ? Vector3.UnitY : Vector3.UnitX, sun));
        var v = Vector3.Cross(sun, u);
        var vertices = new Vertex[2 + (Rings - 1) * Segments];
        vertices[0] = Corner(look, sun, sun);
        vertices[^1] = Corner(look, -sun, sun);
        for (var r = 1; r < Rings; r++)
        {
            var theta = MathF.PI * MathF.Pow(r / (float)Rings, RingCrowding);
            for (var s = 0; s < Segments; s++)
            {
                var phi = s * MathF.Tau / Segments;
                var way = sun * MathF.Cos(theta) + (u * MathF.Cos(phi) + v * MathF.Sin(phi)) * MathF.Sin(theta);
                vertices[1 + (r - 1) * Segments + s] = Corner(look, way, sun);
            }
        }

        // Fans at both poles, quads between the rings.
        var indices = new List<ushort>(Segments * 6 * Rings);
        var last = (ushort)(vertices.Length - 1);
        for (var s = 0; s < Segments; s++)
        {
            var n = (s + 1) % Segments;
            indices.AddRange([0, Ring(1, s), Ring(1, n)]);
            indices.AddRange([last, Ring(Rings - 1, n), Ring(Rings - 1, s)]);
            for (var r = 1; r < Rings - 1; r++)
            {
                indices.AddRange([Ring(r, s), Ring(r + 1, s), Ring(r + 1, n), Ring(r, s), Ring(r + 1, n), Ring(r, n)]);
            }
        }

        return (vertices, [.. indices]);

        static ushort Ring(int r, int s) => (ushort)(1 + (r - 1) * Segments + s);
    }

    /// <summary>The fog's colour (sRGB, as <see cref="Fog.Colour"/> takes it): the sky's at the horizon.</summary>
    internal static Vector4 HazeOf(SunsetLook look) => new(look.Horizon, 1f);

    /// <summary>The thin deck over the dusk sky: its colour from the look, lit from under on the
    /// sun's side by the glow; null without cover.</summary>
    internal static CloudLayer? CloudsOf(SunsetLook look, Vector3 sun)
    {
        if (look.Cover <= 0f)
        {
            return null;
        }

        var level = new Vector3(sun.X, 0f, sun.Z);
        return new CloudLayer(look.Cover, Linear(look.Clouds), CloudHeight, CloudSize)
        {
            Underlight = Linear(look.Glow) * CloudGlow,
            UnderlightWay = level.LengthSquared() > 1e-12f ? Vector3.Normalize(level) : Vector3.Zero,
            Octaves = CloudOctaves,
        };
    }

    /// <summary>The deck's height and cell (units), its noise octaves, and the glow's light under it.</summary>
    private const float CloudHeight = 500f * Storm.UnitsPerMetre, CloudSize = 220f * Storm.UnitsPerMetre, CloudGlow = 0.05f;
    private const int CloudOctaves = 4;

    private static Vertex Corner(SunsetLook look, Vector3 way, Vector3 sun)
    {
        var c = ColourAt(look, way, sun);
        return new Vertex(way * Radius, Vector2.Zero, Vertex.Rgba(Byte(c.X), Byte(c.Y), Byte(c.Z), 255));
    }

    /// <summary>The angle (degrees) between two ways' headings.</summary>
    private static float HeadingApart(Vector3 a, Vector3 b)
    {
        var d = MathF.Atan2(a.X, a.Z) - MathF.Atan2(b.X, b.Z);
        d = MathF.IEEERemainder(d, MathF.Tau);
        return MathF.Abs(d) * 180f / MathF.PI;
    }

    private static float Degrees(float sine) => MathF.Asin(Math.Clamp(sine, -1f, 1f)) * 180f / MathF.PI;

    private static byte Byte(float linear) => (byte)MathF.Round(Srgb.FromLinearValue(Math.Clamp(linear, 0f, 1f)) * 255f);

    private static Vector3 Linear(Vector3 c) => new(Srgb.ToLinear(c.X), Srgb.ToLinear(c.Y), Srgb.ToLinear(c.Z));

    private static float SmoothStep(float a, float b, float x)
    {
        var t = Math.Clamp((x - a) / (b - a), 0f, 1f);
        return t * t * (3f - 2f * t);
    }
}
