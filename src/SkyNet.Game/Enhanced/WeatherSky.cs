using System.Numerics;
using RetroEng.Render;
using SkyNet.Game.Menu;

namespace SkyNet.Game.Enhanced;

/// <summary>
/// The weather's sky and air on the renderer, on the frame build's thread: the level's own light and
/// depth cue (whatever was last set from outside) with a storm's flash, a cloud deck and a haze on them.
/// <code>
///   Lighting ─► Storm.Lit (flash) ─► + CloudLayer (cover, drift, fires and flash under it, the
///                                      horizon's haze), the moon dimmed, no stars when overcast
///   Fog      ─► the haze's colour (lit by a flash), ending at its visibility, held past it
///            ─► in a dust cloud: closing towards a few metres, tinted the dust's colour
/// </code>
/// </summary>
public sealed class WeatherSky
{
    /// <summary>The deck's noise octaves per PARTICLES level (OFF, LOW, HIGH).</summary>
    public static readonly int[] CloudOctaves = [2, 3, 5];
    /// <summary>The share of the moon's (or the dusk sun's) light a full cover takes away.</summary>
    public const float CoveredLight = 0.8f;
    /// <summary>The map's own deck's drift (units a second: about 3 m/s).</summary>
    private static readonly Vector3 OwnDrift = new(72f, 0f, 96f);
    /// <summary>A cover from which no star is drawn at all.</summary>
    public const float Overcast = 0.99f;
    /// <summary>The fires' light under the deck (linear, all fires near), and the distance (units) a
    /// fire counts fully within.</summary>
    public static readonly Vector3 FireUnderlight = new(0.035f, 0.014f, 0.005f);
    public const float FireReach = 2500f, FiresForFull = 4f;
    /// <summary>A flash's light under the deck and on the whole of it.</summary>
    public const float FlashUnder = 1.2f, FlashDeck = 0.12f, FlashHaze = 0.2f;
    /// <summary>Without a haze the deck fades into the depth cue's colour at the horizon this much.</summary>
    public const float CueHorizon = 0.5f;
    /// <summary>The elevation (its sine: about 10 degrees) the horizon's haze fades out at.</summary>
    public const float HorizonBand = 0.18f;
    /// <summary>The haze is total at its visibility; it starts this share of the way there, thickening
    /// early (the curve's exponent).</summary>
    public const float HazeStart = 0.04f, HazeCurve = 0.75f;
    /// <summary>What a dust cloud's full core leaves seen (metres), and its colour's brightness against
    /// the haze's (it catches the light around).</summary>
    public const float DustCoreMetres = 6f, DustGlow = 1.4f;
    /// <summary>A dust cloud's colour without a coloured cue (linear, times the dust's colour).</summary>
    public const float DustUncued = 0.1f;
    /// <summary>Thinner dust at the eye than this leaves the cue as it is.</summary>
    public const float DustFrom = 0.005f;

    /// <summary>The night's light all around the deck's colour is set for.</summary>
    private static readonly float NightAround =
        ((LookLighting.NightSky + LookLighting.NightGround) * (0.5f * LookLighting.NightAmbientMood)).Length();

    private Lighting? _base, _lit;
    private Fog? _baseFog, _litFog;
    private object? _firesOf;
    private Vector3[] _fires = [];

    /// <summary>Sets the renderer's light and depth cue for the frame seen from <paramref name="eye"/>:
    /// the level's own with <paramref name="storm"/>'s flash and <paramref name="air"/>'s clouds and
    /// haze on them; <paramref name="level"/>'s fires light the deck.</summary>
    public void Apply(Renderer renderer, in StormFrame storm, AirView air, Level? level, ParticleLevel particles)
    {
        if (renderer.Lighting is not { } now)
        {
            (_base, _lit, _baseFog, _litFog) = (null, null, null, null);
            return;
        }

        // What was set from outside (a look change, a new map) is the new base.
        if (!ReferenceEquals(now, _lit))
        {
            _base = now;
        }

        if (!ReferenceEquals(renderer.Fog, _litFog))
        {
            _baseFog = renderer.Fog;
        }

        var flash = storm.Flash > Storm.MinFlash ? storm.Flash : 0f;
        _lit = Clouded(Storm.Lit(_base!, storm), _base!, air, _baseFog, flash, storm.Strike?.Way ?? Vector3.Zero,
            FireAmount(level, air), particles);
        _litFog = Dusted(Hazed(_baseFog, air.Haze, flash), air);

        // The map's own deck (no mod's clouds) drifts slowly on the effects' clock.
        if (air.Clouds == null && _lit.Clouds is { } own)
        {
            _lit = _lit with { Clouds = own with { Drift = OwnDrift * renderer.EffectTime } };
        }

        renderer.Lighting = _lit;
        renderer.Fog = _litFog;
    }

    /// <summary><paramref name="lit"/> under <paramref name="air"/>'s deck and haze: the deck's colour
    /// from <paramref name="around"/>'s light, the fires (<paramref name="fires"/> 0-1) and a flash
    /// (<paramref name="flash"/> from <paramref name="way"/>) under it; the moon dimmed by the cover,
    /// no stars when overcast. As it is without clouds or haze.</summary>
    public static Lighting Clouded(Lighting lit, Lighting around, AirView air, Fog? cue, float flash, Vector3 way, float fires,
        ParticleLevel particles)
    {
        if (air.Clouds == null && air.Haze == null)
        {
            return lit;
        }

        var cover = air.Clouds?.Coverage ?? 0f;
        var horizon = air.Haze is { } haze ? new Vector4(Linear(haze.Colour) + Storm.FlashSky * (flash * FlashHaze), haze.Horizon)
            : cue is { Colour.W: > 0f } c ? new Vector4(Linear(new Vector3(c.Colour.X, c.Colour.Y, c.Colour.Z)), CueHorizon) : Vector4.Zero;
        // A haze alone keeps the map's own deck (a dusk sky's) under its horizon.
        var own = air.Clouds == null ? lit.Clouds : null;
        var layer = (own ?? new CloudLayer(cover, Vector3.Zero, 1f, 1f)) with { Horizon = horizon, HorizonBand = HorizonBand, Octaves = CloudOctaves[(int)particles] };
        if (air.Clouds is { } clouds)
        {
            var light = ((around.Sky + around.Ground) * 0.5f).Length() / NightAround;
            layer = layer with
            {
                Colour = Linear(clouds.Colour) * Math.Clamp(light, 0.3f, 4f) + Storm.FlashSky * (flash * FlashDeck),
                Height = clouds.HeightMetres * Storm.UnitsPerMetre,
                Scale = clouds.SizeMetres * Storm.UnitsPerMetre,
                Drift = air.CloudDrift,
                Underlight = FireUnderlight * (fires * clouds.FireGlow) + Storm.FlashLight * (flash * FlashUnder),
                UnderlightWay = flash > 0f ? way : Vector3.Zero,
            };
        }

        return lit with
        {
            Sun = lit.Sun * (1f - CoveredLight * cover),
            Stars = cover >= Overcast ? null : lit.Stars,
            Clouds = layer,
        };
    }

    /// <summary>The depth cue <paramref name="cue"/> in <paramref name="haze"/>: its colour mixed towards
    /// the haze's (lit by <paramref name="flash"/>), total at the visibility (the far clip at most) and
    /// held past it. As it is without a haze or a cue (indoors).</summary>
    public static Fog? Hazed(Fog? cue, HazeAirTable? haze, float flash)
    {
        if (haze == null || cue == null)
        {
            return cue;
        }

        var end = MathF.Min(haze.VisibilityMetres * Storm.UnitsPerMetre, cue.End);
        var colour = Linear(haze.Colour) + Storm.FlashSky * (flash * FlashHaze);
        var from = Linear(new Vector3(cue.Colour.X, cue.Colour.Y, cue.Colour.Z));
        var mixed = Vector3.Lerp(cue.Colour.W > 0f ? from : colour, colour, haze.Density);
        return cue with
        {
            Start = MathF.Min(cue.Start, end * HazeStart),
            End = end,
            Exponent = HazeCurve,
            Beyond = end < cue.End ? FogBeyond.Hold : cue.Beyond,
            Colour = new Vector4(Srgb.FromLinearValue(mixed.X), Srgb.FromLinearValue(mixed.Y), Srgb.FromLinearValue(mixed.Z), 1f),
        };
    }

    /// <summary>The depth cue <paramref name="cue"/> inside <paramref name="air"/>'s dust clouds (their
    /// thickness at the eye, <see cref="AirView.DustFog"/>): ending nearer, towards
    /// <see cref="DustCoreMetres"/> at a full core, and tinted the dust's colour at the cue's brightness.
    /// As it is out of the clouds, without dust or a cue (indoors).</summary>
    public static Fog? Dusted(Fog? cue, AirView air)
    {
        if (cue == null || air.Dust is not { } dust || air.DustFog < DustFrom)
        {
            return cue;
        }

        // Thickness 0.5 halves the way (in log) from the cue's end to the core's.
        var d = Math.Clamp(air.DustFog, 0f, 1f);
        var core = DustCoreMetres * Storm.UnitsPerMetre;
        var end = cue.End > core ? cue.End * MathF.Pow(core / cue.End, d) : cue.End;

        var tint = Linear(dust.Colour);
        var from = Linear(new Vector3(cue.Colour.X, cue.Colour.Y, cue.Colour.Z));
        var lit = cue.Colour.W > 0f ? tint * (DustGlow * Luma(from) / MathF.Max(Luma(tint), 1e-4f)) : tint * DustUncued;
        var mixed = Vector3.Lerp(cue.Colour.W > 0f ? from : lit, lit, MathF.Min(1f, d * 1.5f));
        return cue with
        {
            Start = cue.Start * end / cue.End,
            End = end,
            Beyond = end < cue.End ? FogBeyond.Hold : cue.Beyond,
            Colour = new Vector4(Srgb.FromLinearValue(mixed.X), Srgb.FromLinearValue(mixed.Y), Srgb.FromLinearValue(mixed.Z), 1f),
        };
    }

    private static float Luma(Vector3 c) => 0.2126f * c.X + 0.7152f * c.Y + 0.0722f * c.Z;

    /// <summary>How much the map's fires near the eye light the deck (0-1): each fire counts by its
    /// distance, <see cref="FiresForFull"/> near ones in all.</summary>
    private float FireAmount(Level? level, AirView air)
    {
        if (level == null || air.Clouds == null)
        {
            return 0f;
        }

        if (!ReferenceEquals(level, _firesOf))
        {
            _firesOf = level;
            _fires = [.. level.Block.Lamps.Where(l => l.Flicker).Select(l => l.Position)];
        }

        var sum = 0f;
        var eye = new Vector3(air.OriginX + SkyCover.Cells / 2, 0f, air.OriginZ + SkyCover.Cells / 2) * SkyCover.Cell;
        foreach (var f in _fires)
        {
            var d = new Vector2(f.X - eye.X, f.Z - eye.Z).Length() / FireReach;
            sum += 1f / (1f + d * d);
        }

        return MathF.Min(1f, sum / FiresForFull);
    }

    private static Vector3 Linear(Vector3 c) => new(Srgb.ToLinear(c.X), Srgb.ToLinear(c.Y), Srgb.ToLinear(c.Z));
}
