using System.Numerics;
using RetroEng.Render;
using SkyNet.Game.Menu;

namespace SkyNet.Game.Enhanced;

/// <summary>
/// A mod's dust over an outdoor map in the enhanced look (<see cref="DustTable"/>): faint lumpy sheets
/// drifting over the ground round the eye, the dust clouds' puffs rolling past (<see cref="DustClouds"/>)
/// and fine grit streaking past in them, lit by the scene's light (a flash too). Nothing is kept:
/// every particle is hashed and moved by the wind's drift (or a calm air's) and the air's clock (a
/// sheet or a streak wrapped into a box round the eye, a puff round its cloud's core) and set on the
/// ground (or roof) of its cell of the cover; none where that cell is not known.
/// <code>
///   sheets: 150-420 u wide, 0..height over the cell's top, drifting with the wind, faded at the box's sides
///   puffs:  220-540 u wide, spreading as their cloud rises; faded near the eye (the haze takes over there)
///   grit:   streaks 0..1.5 m up, blown at 1.6 × the wind, as long as 0.07 s of their flight; only in a cloud
/// </code>
/// </summary>
public sealed class Dust
{
    /// <summary>Sheets and grit streaks at full amount per PARTICLES level (OFF, LOW, HIGH).</summary>
    public static readonly int[] SheetCount = [0, 16, 44], GritCount = [0, 140, 420];
    /// <summary>Puffs of a dust cloud per PARTICLES level (OFF, LOW, HIGH).</summary>
    public static readonly int[] PuffCount = [0, 14, 40];
    /// <summary>A puff's opacity at a full cloud's core.</summary>
    public const float PuffAlpha = 0.75f;
    /// <summary>A puff fades out this near the eye (times its size), fully faded at a third of it.</summary>
    public const float PuffNear = 1.2f;
    /// <summary>Grit blows where the dust at the eye (or the squall's share) is at least this; less: none.</summary>
    public const float GritFrom = 0.03f, GritSquall = 0.3f;
    /// <summary>The sheets' and the grit's boxes round the eye (units across; inside the cover's grid).</summary>
    public const float SheetBox = 1400f, GritBox = 700f;
    /// <summary>The grit's height over the ground (units) and its speed against the wind's.</summary>
    public const float GritHeight = 60f, GritSpeed = 1.6f;
    /// <summary>A grit streak's length in seconds of its flight, and its width (units).</summary>
    public const float GritSeconds = 0.035f, GritWidth = 0.35f;
    /// <summary>The air without a wind (units a second).</summary>
    public static readonly Vector3 CalmWind = new(16f, 0f, 7f);

    private Particle[] _sheets = [];
    private Particle[] _grit = [];
    private float[] _keys = [];

    /// <summary>Queues the dust of <paramref name="view"/> seen from <paramref name="eye"/>, lit by the
    /// renderer's light: none at PARTICLES OFF, in the original look or without dust.</summary>
    public void Draw(Renderer renderer, AirView view, Vector3 eye, ParticleLevel level)
    {
        if (renderer.Lighting is not { } lighting || view.Dust == null)
        {
            return;
        }

        var ambient = (lighting.Sky + lighting.Ground) * 0.5f + lighting.Sun * 0.25f;
        var n = Sheets(view, eye, level, ambient, ref _sheets);
        n = Puffs(view, eye, level, ambient, ref _sheets, n);
        if (_keys.Length < n)
        {
            _keys = new float[_sheets.Length];
        }

        // Back to front: the farthest first.
        for (var i = 0; i < n; i++)
        {
            _keys[i] = -Vector3.DistanceSquared(_sheets[i].Position, eye);
        }

        Array.Sort(_keys, _sheets, 0, n);
        renderer.DrawParticles(_sheets.AsSpan(0, n), ParticleBlend.Alpha, 60f);
        var g = Grit(view, eye, level, ambient, ref _grit);
        renderer.DrawParticles(_grit.AsSpan(0, g), ParticleBlend.Alpha, 4f);
    }

    /// <summary>The wind the dust moves with now and its drift.</summary>
    private static (Vector3 Wind, Vector3 Drift) Blow(AirView view) =>
        view.Wind is { } w ? (w.Velocity, w.Drift) : (CalmWind, CalmWind * view.Clock);

    /// <summary>The sheets of <paramref name="view"/> round <paramref name="eye"/> into
    /// <paramref name="into"/> (grown as needed); returns how many. A sheet over a cell not open to the
    /// sky (a roof over it, not known yet) is left out.</summary>
    public static int Sheets(AirView view, Vector3 eye, ParticleLevel level, Vector3 ambient, ref Particle[] into)
    {
        if (view.Dust is not { } table)
        {
            return 0;
        }

        var count = (int)(SheetCount[(int)level] * table.Amount);
        if (into.Length < count)
        {
            into = new Particle[count];
        }

        var (_, drift) = Blow(view);
        var colour = Srgb(table.Colour) * ambient * 9f;
        var height = table.HeightMetres * Storm.UnitsPerMetre;
        var n = 0;
        for (var i = 0; i < count; i++)
        {
            var h = Atmosphere.Hash3(i, 61);
            var g = Atmosphere.Hash3(i, 62);
            var speed = 0.6f + 0.5f * g.X;
            var wobble = new Vector3(MathF.Sin(view.Clock * 0.13f + h.Z * 30f), 0f, MathF.Cos(view.Clock * 0.11f + h.X * 30f)) * 40f;
            if (!Place(view, eye, new Vector3(h.X, 0f, h.Z) * SheetBox + drift * speed + wobble, SheetBox, out var at, out var fade))
            {
                continue;
            }

            var size = 150f + 270f * g.Y;
            at.Y += size * 0.25f + g.Z * height;

            var alpha = (0.1f + 0.16f * table.Amount) * (0.6f + 0.4f * h.Y) * fade;
            into[n++] = new Particle(at, size, new Vector4(colour, alpha), Spin: g.Z * 6.2f + view.Clock * 0.03f * (h.Y - 0.5f), Seed: 1f + i);
        }

        return n;
    }

    /// <summary>The puffs of <paramref name="view"/>'s dust clouds added to <paramref name="into"/> after
    /// its first <paramref name="n"/> (grown as needed); returns how many in all. Each rolls round its
    /// cloud's core, on its cell's top (the nearest known one past the grid).</summary>
    public static int Puffs(AirView view, Vector3 eye, ParticleLevel level, Vector3 ambient, ref Particle[] into, int n)
    {
        if (view.Dust is not { } table || view.DustCount == 0)
        {
            return n;
        }

        var per = PuffCount[(int)level];
        if (into.Length < n + per * view.DustCount)
        {
            Array.Resize(ref into, n + per * view.DustCount);
        }

        var colour = Srgb(table.Colour) * ambient * 9f;
        var height = table.HeightMetres * Storm.UnitsPerMetre;
        for (var c = 0; c < view.DustCount; c++)
        {
            var cloud = view.DustClouds[c];
            var (radius, phase, strength) = (cloud.RadiusNow, cloud.Phase, cloud.Strength);
            var across = new Vector3(cloud.Way.Z, 0f, -cloud.Way.X);
            var rise = table.RiseMetres * Storm.UnitsPerMetre * phase;
            for (var i = 0; i < per; i++)
            {
                // Bunched round the core (two hashes summed), rolling slowly round it.
                var h = Atmosphere.Hash3(i, 900 + cloud.Seed);
                var g = Atmosphere.Hash3(i, 1900 + cloud.Seed);
                var (u, v) = (h.X + g.X - 1f, h.Z + g.Y - 1f);
                var roll = new Vector3(MathF.Sin(view.Clock * 0.2f + h.Y * 30f), 0f, MathF.Cos(view.Clock * 0.17f + g.Z * 30f)) * (0.08f * radius);
                var at = cloud.Centre + cloud.Way * (u * radius) + across * (v * radius * DustClouds.Across) + roll;
                var top = ClampedTopAt(view, at.X, at.Z);
                if (!float.IsFinite(top))
                {
                    continue;
                }

                var size = (220f + 320f * g.Y) * (1f + DustClouds.Spread * phase);
                at.Y = top + size * 0.3f + h.Y * (height + rise);
                var near = SmoothStep(PuffNear * size / 3f, PuffNear * size, Vector3.Distance(at, eye));
                var core = MathF.Exp(-DustClouds.Falloff * (u * u + v * v));
                var alpha = PuffAlpha * cloud.Peak * strength * core * near / (1f + DustClouds.Spread * phase);
                if (alpha < 0.005f)
                {
                    continue;
                }

                into[n++] = new Particle(at, size, new Vector4(colour, alpha), Spin: g.Z * 6.2f + view.Clock * 0.08f * (h.Y - 0.5f),
                    Seed: 100f + cloud.Seed * 64 + i);
            }
        }

        return n;
    }

    /// <summary>The grit streaks of <paramref name="view"/> round <paramref name="eye"/>, as <see cref="Sheets"/>:
    /// only while a dust cloud is at the eye (or a squall blows), as many as it is thick.</summary>
    public static int Grit(AirView view, Vector3 eye, ParticleLevel level, Vector3 ambient, ref Particle[] into)
    {
        if (view.Dust is not { } table)
        {
            return 0;
        }

        var gust = MathF.Max(view.DustNear, GritSquall * view.Squall);
        var count = gust < GritFrom ? 0 : (int)(GritCount[(int)level] * table.Streaks * MathF.Min(1f, gust * 2f));
        if (into.Length < count)
        {
            into = new Particle[count];
        }

        var (wind, drift) = Blow(view);
        var colour = Srgb(table.Colour) * ambient * 5f;
        var n = 0;
        for (var i = 0; i < count; i++)
        {
            var h = Atmosphere.Hash3(i, 63);
            var g = Atmosphere.Hash3(i, 64);
            var speed = GritSpeed * (0.7f + 0.6f * g.X);
            var lift = new Vector3(0f, (MathF.Sin(view.Clock * (1.5f + 2f * g.Y) + h.Y * 40f) + 1f) * 6f, 0f);
            if (!Place(view, eye, new Vector3(h.X, 0f, h.Z) * GritBox + drift * speed, GritBox, out var at, out var fade))
            {
                continue;
            }

            at.Y += 4f + g.Z * GritHeight + lift.Y;
            var velocity = wind * speed + Vector3.UnitY * (lift.Y * 0.5f);
            if (Vector3.DistanceSquared(at, eye) < Rain.NearCut * Rain.NearCut || velocity.LengthSquared() < 1f)
            {
                continue;
            }

            into[n++] = new Particle(at, GritWidth, new Vector4(colour, 0.35f * (0.4f + 0.6f * h.Y) * fade), Shape: ParticleShape.Streak,
                Axis: velocity * GritSeconds);
        }

        return n;
    }

    /// <summary>A point wrapped into the box round the eye, set on its cell's top (the ground or a roof
    /// under open sky); false where the cell is unknown or nothing is under it. <paramref name="fade"/>
    /// thins it out towards the box's sides.</summary>
    private static bool Place(AirView view, Vector3 eye, Vector3 p, float box, out Vector3 at, out float fade)
    {
        var (left, back) = (eye.X - box / 2f, eye.Z - box / 2f);
        at = new Vector3(left + Wrap(p.X - left, box), 0f, back + Wrap(p.Z - back, box));
        var edge = MathF.Max(MathF.Abs(at.X - eye.X), MathF.Abs(at.Z - eye.Z)) / box;
        fade = 1f - SmoothStep(0.34f, 0.5f, edge);
        var top = view.TopAt(at.X, at.Z);
        if (fade <= 0f || !float.IsFinite(top))
        {
            return false;
        }

        at.Y = top;
        return true;
    }

    /// <summary>The cover's top over (<paramref name="x"/>, <paramref name="z"/>), past the grid the
    /// nearest edge cell's (a far puff only needs about where the ground is).</summary>
    private static float ClampedTopAt(AirView view, float x, float z)
    {
        var (low, high) = (new Vector2(view.OriginX, view.OriginZ) * SkyCover.Cell, new Vector2(view.OriginX + SkyCover.Cells, view.OriginZ + SkyCover.Cells) * SkyCover.Cell);
        return view.TopAt(Math.Clamp(x, low.X, high.X - 1f), Math.Clamp(z, low.Y, high.Y - 1f));
    }

    private static Vector3 Srgb(Vector3 c) =>
        new(RetroEng.Render.Srgb.ToLinear(c.X), RetroEng.Render.Srgb.ToLinear(c.Y), RetroEng.Render.Srgb.ToLinear(c.Z));

    private static float Wrap(float v, float size) => v - MathF.Floor(v / size) * size;

    private static float SmoothStep(float a, float b, float x)
    {
        var t = Math.Clamp((x - a) / (b - a), 0f, 1f);
        return t * t * (3f - 2f * t);
    }
}
