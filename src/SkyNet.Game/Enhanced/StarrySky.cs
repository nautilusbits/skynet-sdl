using System.Numerics;
using RetroEng.Render;

namespace SkyNet.Game.Enhanced;

/// <summary>
/// A night map's sky in the enhanced look: how many stars (a multiple of the default count), how
/// bright, the Milky Way's strength, the night clouds' cover and colour (sRGB 0-1), the moon's halo.
/// </summary>
internal sealed record NightLook(float Stars, float StarBrightness, float MilkyWay, float Cover, Vector3 Clouds, float MoonGlow)
{
    /// <summary>The DOS night kept: a black sky, a few thin dark clouds.</summary>
    internal static NightLook Defaults { get; } = new(1f, 1f, 1f, 0.22f, new Vector3(0.02f, 0.022f, 0.03f), 1f);
}

/// <summary>A star of the enhanced night: its way, its linear colour, its size (pixels across).</summary>
internal readonly record struct NightStar(Vector3 Way, Vector3 Colour, float Pixels);

/// <summary>
/// The enhanced look's night sky round the DOS one (black fill, the DOS stars, the moon where DOS has it):
/// <code>
///   stars:     the DOS 256 (their palette tints) + many faint hashed ones, soft dots of 1-3 px,
///              a few bright; more crowd the Milky Way; none under the horizon
///   milky way: a faint band (a strip of the sky sphere) along a tilted great circle, mottled
///   clouds:    the engine's deck, thin and dark, lit faintly on the moon's side (it hides the stars)
///   moon:      (drawn added over the black) a disc of the LRO near side (NASA, public domain: data/sky), shaded as a ball (limb
///              darkening, light from the DOS moon light's side), a soft halo round it
/// </code>
/// </summary>
internal static class StarrySky
{
    /// <summary>The faint stars added to the DOS ones at <see cref="NightLook.Stars"/> 1.</summary>
    internal const int FaintStars = 2200;
    /// <summary>The share of the faint stars crowded into the Milky Way.</summary>
    private const float BandShare = 0.35f;
    /// <summary>The Milky Way's pole (its great circle lies across it) and half width (degrees).</summary>
    private static readonly Vector3 BandPole = Vector3.Normalize(new Vector3(0.35f, 0.42f, 0.84f));
    private const float BandWidth = 7f;
    /// <summary>The band's strip: segments along it and rows across it, its brightness.</summary>
    private const int BandSegments = 256, BandRows = 13;
    private const float BandLight = 0.014f;
    /// <summary>The stars and the band fade in over the horizon up to this (sine of the elevation).</summary>
    private const float HorizonFade = 0.12f;
    /// <summary>A star's rim points (its soft dot).</summary>
    private const int StarRim = 6;

    /// <summary>The moon disc's rings and segments, and the halo's rings out to <see cref="HaloReach"/> radii.</summary>
    internal const int MoonRings = 48, MoonSegments = 160, HaloRings = 10;
    internal const float HaloReach = 2.1f;
    /// <summary>The halo's strength at the rim and its colour (linear).</summary>
    private const float HaloAlpha = 0.2f;
    private static readonly Vector3 HaloColour = new(0.55f, 0.6f, 0.72f);
    /// <summary>The moon's colour at full albedo (linear) and its albedo's lift (the maria not black).</summary>
    private static readonly Vector3 MoonTint = new(0.9f, 0.88f, 0.84f);
    private const float AlbedoLift = 0.12f, AlbedoGain = 1.9f;
    /// <summary>The way the moon's light falls on its face (screen: up-left, as the DOS art), and how much.</summary>
    private static readonly Vector3 FaceLight = Vector3.Normalize(new Vector3(-0.45f, 0.5f, 0.75f));
    private const float FaceShade = 0.35f;

    /// <summary>The night's stars: <paramref name="dos"/> (ways, linear tints) brighter and larger,
    /// then the faint hashed ones, under the horizon none.</summary>
    internal static List<NightStar> Stars(NightLook look, IReadOnlyList<(Vector3 Way, Vector3 Tint)> dos)
    {
        var stars = new List<NightStar>();
        for (var i = 0; i < dos.Count; i++)
        {
            var h = Atmosphere.Hash3(i, 71);
            var bright = 0.55f + 0.9f * MathF.Pow(h.X, 3f);
            stars.Add(new NightStar(dos[i].Way, dos[i].Tint * (bright * look.StarBrightness), 1.6f + 1.6f * MathF.Pow(h.X, 3f)));
        }

        var count = (int)(FaintStars * look.Stars);
        for (var i = 0; i < count; i++)
        {
            var h = Atmosphere.Hash3(i, 72);
            var g = Atmosphere.Hash3(i, 73);
            var way = i < count * BandShare ? InBand(h, g.Z) : OnSphere(h.X, h.Y);
            if (way.Y < 0f)
            {
                continue;
            }

            // Many faint, a few bright: brightness by a steep power of a hash.
            var b = 0.07f + 0.55f * MathF.Pow(g.X, 7f);
            stars.Add(new NightStar(way, Temperature(g.Y) * (b * look.StarBrightness), 1.1f + 1.4f * MathF.Pow(g.X, 6f)));
        }

        return stars;
    }

    /// <summary>The stars as soft dots <paramref name="pixel"/> units across a pixel at the sky's
    /// distance: a centre and <see cref="StarRim"/> dark rim points each (drawn added).</summary>
    internal static (Vertex[] Vertices, ushort[] Indices) StarMesh(IReadOnlyList<NightStar> stars, float distance, float pixel)
    {
        var vertices = new Vertex[stars.Count * (StarRim + 1)];
        var indices = new ushort[stars.Count * StarRim * 3];
        var (v, n) = (0, 0);
        foreach (var s in stars)
        {
            var u = Vector3.Normalize(Vector3.Cross(MathF.Abs(s.Way.Y) < 0.99f ? Vector3.UnitY : Vector3.UnitX, s.Way));
            var w = Vector3.Cross(s.Way, u);
            var c = s.Way * distance;
            var r = pixel * s.Pixels;
            var centre = v;
            vertices[v++] = new Vertex(c, Vector2.Zero, Packed(s.Colour * Fade(s.Way.Y), 1f));
            for (var k = 0; k < StarRim; k++)
            {
                var a = k * MathF.Tau / StarRim;
                vertices[v++] = new Vertex(c + (u * MathF.Cos(a) + w * MathF.Sin(a)) * r, Vector2.Zero, Packed(Vector3.Zero, 0f));
                indices[n++] = (ushort)centre;
                indices[n++] = (ushort)(centre + 1 + k);
                indices[n++] = (ushort)(centre + 1 + (k + 1) % StarRim);
            }
        }

        return (vertices, indices);
    }

    /// <summary>The Milky Way: a strip <see cref="BandRows"/> rows across its great circle, its light
    /// falling off across it and mottled along it (drawn added), dark under the horizon.</summary>
    internal static (Vertex[] Vertices, ushort[] Indices) BandMesh(NightLook look, float distance)
    {
        var (u, w) = Basis(BandPole);
        var vertices = new Vertex[BandSegments * BandRows];
        for (var s = 0; s < BandSegments; s++)
        {
            var a = s * MathF.Tau / BandSegments;
            var along = u * MathF.Cos(a) + w * MathF.Sin(a);
            for (var r = 0; r < BandRows; r++)
            {
                var off = (r / (float)(BandRows - 1) * 2f - 1f) * BandWidth * 2.2f;
                var e = off * MathF.PI / 180f;
                var way = along * MathF.Cos(e) + BandPole * MathF.Sin(e);
                var across = off / BandWidth;
                var mottle = 0.45f + 0.55f * Noise(s * 0.11f, r * 0.7f) * (0.6f + 0.4f * Noise(s * 0.37f + 17f, r * 1.3f));
                var light = BandLight * look.MilkyWay * MathF.Exp(-across * across) * mottle * Fade(way.Y);
                vertices[s * BandRows + r] = new Vertex(way * distance, Vector2.Zero, Packed(new Vector3(0.85f, 0.9f, 1f) * light, 1f));
            }
        }

        var indices = new List<ushort>(BandSegments * (BandRows - 1) * 6);
        for (var s = 0; s < BandSegments; s++)
        {
            var t = (s + 1) % BandSegments;
            for (var r = 0; r < BandRows - 1; r++)
            {
                ushort a = (ushort)(s * BandRows + r), b = (ushort)(t * BandRows + r);
                indices.AddRange([a, b, (ushort)(b + 1), a, (ushort)(b + 1), (ushort)(a + 1)]);
            }
        }

        return (vertices, [.. indices]);
    }

    /// <summary>The thin night deck, lit a little from under on the moon's side; null without cover.</summary>
    internal static CloudLayer? CloudsOf(NightLook look, Vector3 moon)
    {
        if (look.Cover <= 0f)
        {
            return null;
        }

        var level = new Vector3(moon.X, 0f, moon.Z);
        return new CloudLayer(look.Cover, Linear(look.Clouds), CloudHeight, CloudSize)
        {
            Underlight = MoonUnderlight,
            UnderlightWay = level.LengthSquared() > 1e-12f ? Vector3.Normalize(level) : Vector3.Zero,
            Octaves = CloudOctaves,
        };
    }

    /// <summary>The deck's height and cell (units), octaves and the moon's light under it (linear).</summary>
    private const float CloudHeight = 600f * Storm.UnitsPerMetre, CloudSize = 260f * Storm.UnitsPerMetre;
    private const int CloudOctaves = 4;
    private static readonly Vector3 MoonUnderlight = new(0.006f, 0.007f, 0.009f);

    /// <summary>The moon's disc and halo in its own plane: each point's place (x right, y up, 1 the
    /// disc's radius) and colour. <paramref name="albedo"/>: the near side's grey, <paramref name="side"/>
    /// texels square (null: an even grey).</summary>
    internal static (Vector2[] Places, uint[] Colours, ushort[] Indices) MoonMesh(NightLook look, byte[]? albedo, int side)
    {
        var rings = MoonRings + HaloRings;
        var places = new Vector2[1 + rings * MoonSegments];
        var colours = new uint[places.Length];
        places[0] = Vector2.Zero;
        colours[0] = MoonColour(albedo, side, Vector2.Zero);
        for (var r = 1; r <= rings; r++)
        {
            // The disc's rings even out to its rim, the halo's spread out past it.
            var radius = r <= MoonRings ? r / (float)MoonRings : 1f + (HaloReach - 1f) * MathF.Pow((r - MoonRings) / (float)HaloRings, 1.5f);
            for (var s = 0; s < MoonSegments; s++)
            {
                var a = s * MathF.Tau / MoonSegments;
                var p = new Vector2(MathF.Cos(a), MathF.Sin(a)) * radius;
                var i = 1 + (r - 1) * MoonSegments + s;
                places[i] = p;
                colours[i] = r < MoonRings ? MoonColour(albedo, side, p) : Halo(look, radius);
            }
        }

        var indices = new List<ushort>(MoonSegments * 6 * rings);
        for (var s = 0; s < MoonSegments; s++)
        {
            var t = (s + 1) % MoonSegments;
            indices.AddRange([0, (ushort)(1 + s), (ushort)(1 + t)]);
            for (var r = 1; r < rings; r++)
            {
                ushort a = (ushort)(1 + (r - 1) * MoonSegments + s), b = (ushort)(1 + (r - 1) * MoonSegments + t);
                indices.AddRange([a, (ushort)(a + MoonSegments), (ushort)(b + MoonSegments), a, (ushort)(b + MoonSegments), b]);
            }
        }

        return (places, colours, [.. indices]);
    }

    /// <summary>The moon's linear colour at <paramref name="p"/> on its disc (unit radius): the albedo
    /// lifted and gained, darkened to the limb, a little brighter on the lit side.</summary>
    internal static Vector3 MoonLight(byte[]? albedo, int side, Vector2 p)
    {
        var z = MathF.Sqrt(MathF.Max(0f, 1f - p.LengthSquared()));
        var grey = albedo == null ? 0.5f : Sample(albedo, side, p) / 255f;
        var a = AlbedoLift + AlbedoGain * Srgb.ToLinear(grey);
        var normal = new Vector3(p.X, p.Y, z);
        var face = 1f - FaceShade + FaceShade * MathF.Max(0f, Vector3.Dot(normal, FaceLight));
        var limb = 0.55f + 0.45f * MathF.Pow(z, 0.6f);
        return MoonTint * (a * face * limb);
    }

    private static uint MoonColour(byte[]? albedo, int side, Vector2 p) => Packed(MoonLight(albedo, side, p), 1f);

    /// <summary>The halo past the rim: its colour, fading out to <see cref="HaloReach"/>.</summary>
    private static uint Halo(NightLook look, float radius)
    {
        var t = Math.Clamp((radius - 1f) / (HaloReach - 1f), 0f, 1f);
        // Drawn added: the halo's light is its colour times its strength.
        var alpha = HaloAlpha * look.MoonGlow * MathF.Pow(1f - t, 2.2f);
        return Packed(HaloColour * Math.Clamp(alpha, 0f, 1f), 1f);
    }

    /// <summary>The near side's grey at <paramref name="p"/> (bilinear; y up).</summary>
    private static float Sample(byte[] albedo, int side, Vector2 p)
    {
        var x = (p.X + 1f) * 0.5f * side - 0.5f;
        var y = (1f - p.Y) * 0.5f * side - 0.5f;
        int x0 = (int)MathF.Floor(x), y0 = (int)MathF.Floor(y);
        float fx = x - x0, fy = y - y0;
        float At(int i, int j) => albedo[Math.Clamp(j, 0, side - 1) * side + Math.Clamp(i, 0, side - 1)];
        return (At(x0, y0) * (1f - fx) + At(x0 + 1, y0) * fx) * (1f - fy) + (At(x0, y0 + 1) * (1f - fx) + At(x0 + 1, y0 + 1) * fx) * fy;
    }

    /// <summary>A star's colour by a hash: blue-white through white to warm.</summary>
    private static Vector3 Temperature(float t) => t < 0.5f
        ? Vector3.Lerp(new Vector3(0.7f, 0.8f, 1f), Vector3.One, t * 2f)
        : Vector3.Lerp(Vector3.One, new Vector3(1f, 0.82f, 0.6f), (t - 0.5f) * 2f);

    /// <summary>A way in the Milky Way: along its circle, spread across it (gaussian-ish).</summary>
    private static Vector3 InBand(Vector3 h, float spread)
    {
        var (u, w) = Basis(BandPole);
        var a = h.X * MathF.Tau;
        var off = (h.Y + h.Z + spread - 1.5f) * BandWidth * 1.4f * MathF.PI / 180f;
        return Vector3.Normalize((u * MathF.Cos(a) + w * MathF.Sin(a)) * MathF.Cos(off) + BandPole * MathF.Sin(off));
    }

    private static Vector3 OnSphere(float a, float b)
    {
        var y = a * 2f - 1f;
        var r = MathF.Sqrt(1f - y * y);
        var phi = b * MathF.Tau;
        return new Vector3(r * MathF.Cos(phi), y, r * MathF.Sin(phi));
    }

    private static (Vector3 U, Vector3 W) Basis(Vector3 pole)
    {
        var u = Vector3.Normalize(Vector3.Cross(MathF.Abs(pole.Y) < 0.99f ? Vector3.UnitY : Vector3.UnitX, pole));
        return (u, Vector3.Cross(pole, u));
    }

    /// <summary>The sky's light fading in over the horizon (by the sine of the elevation).</summary>
    private static float Fade(float y) => Math.Clamp(y / HorizonFade, 0f, 1f);

    /// <summary>Smooth value noise in [0, 1].</summary>
    private static float Noise(float x, float y)
    {
        int ix = (int)MathF.Floor(x), iy = (int)MathF.Floor(y);
        float fx = x - ix, fy = y - iy;
        fx = fx * fx * (3f - 2f * fx);
        fy = fy * fy * (3f - 2f * fy);
        float H(int i, int j) => Atmosphere.Hash3(i * 7919 + j * 104729, 74).X;
        return (H(ix, iy) * (1f - fx) + H(ix + 1, iy) * fx) * (1f - fy) + (H(ix, iy + 1) * (1f - fx) + H(ix + 1, iy + 1) * fx) * fy;
    }

    private static uint Packed(Vector3 linear, float alpha) =>
        Vertex.Rgba(Byte(linear.X), Byte(linear.Y), Byte(linear.Z), (byte)MathF.Round(Math.Clamp(alpha, 0f, 1f) * 255f));

    private static byte Byte(float linear) => (byte)MathF.Round(Srgb.FromLinearValue(Math.Clamp(linear, 0f, 1f)) * 255f);

    private static Vector3 Linear(Vector3 c) => new(Srgb.ToLinear(c.X), Srgb.ToLinear(c.Y), Srgb.ToLinear(c.Z));
}
