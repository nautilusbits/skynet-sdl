using System.Numerics;
using RetroEng.Render;
using SkyNet.Game.Menu;

namespace SkyNet.Game.Enhanced;

/// <summary>What the frame build needs of the weather: the mods' weather (<see cref="Weather.None"/>
/// indoors, in the original look), the seconds it moves on (0: held), the eye and a strike asked for
/// ahead (its way; null: none).</summary>
public readonly record struct SkyFrame(Weather Weather, float Seconds, Vector3 Eye, Vector3? Strike);

/// <summary>
/// The weather's sky and air on the renderer, on the frame build's thread: the level's own light and
/// depth cue (whatever was last set from outside) under the engine's <see cref="WeatherSystem"/> (rain,
/// wet ground, overcast, lightning) with our cloud deck, haze and dust on them.
/// <code>
///   Lighting ─► + CloudLayer (colour, height, fires under it, the horizon's haze), no stars when overcast
///            ─► WeatherSystem.Apply (cover, drift, rain, flash, bolt; 40 u a metre) ─► the bolt's effects row
///   Fog      ─► the haze's colour (lit by a flash), ending at its visibility, held past it
///            ─► in a dust cloud: closing towards a few metres, tinted the dust's colour
///   Struck   ─► the caller (Storm.Post: the thunder on the simulation's thread)
/// </code>
/// </summary>
public sealed class WeatherSky
{
    /// <summary>The deck's noise octaves per PARTICLES level (OFF, LOW, HIGH).</summary>
    public static readonly int[] CloudOctaves = [2, 3, 5];
    /// <summary>The engine's drops at full intensity per PARTICLES level (OFF, LOW, HIGH), and a phone's most.</summary>
    public static readonly int[] DropCount = [0, 1500, 6000];
    public const int PhoneDrops = 3000;
    /// <summary>The volumetric deck on the desktop, the flat one on phones.</summary>
    public static readonly CloudQuality DeckQuality = OperatingSystem.IsAndroid() ? CloudQuality.Flat : CloudQuality.Volumetric;
    /// <summary>The volumetric deck's depth (metres) and its weather cell per flat noise cell (the
    /// engine's 8000 over its own flat deck's 400).</summary>
    public const float DeckMetres = 500f, VolumeCells = 20f;
    /// <summary>The default deck colour's albedo in the volume: a dark storm deck under the moon.</summary>
    public const float VolumeAlbedo = 0.4f;
    /// <summary>A changed weather (another mod) blends in over this (seconds).</summary>
    public const float BlendSeconds = 5f;
    /// <summary>The map's own deck's drift (units a second: about 3 m/s).</summary>
    private static readonly Vector3 OwnDrift = new(72f, 0f, 96f);
    /// <summary>A cover from which no star is drawn at all.</summary>
    public const float Overcast = 0.99f;
    /// <summary>The fires' light under the deck (linear, all fires near), and the distance (units) a
    /// fire counts fully within.</summary>
    public static readonly Vector3 FireUnderlight = new(0.035f, 0.014f, 0.005f);
    public const float FireReach = 2500f, FiresForFull = 4f;
    /// <summary>The engine's flash at a strike of intensity 1 (its green): a full flash.</summary>
    public const float FullFlash = 2.35f;
    /// <summary>A flash weaker than this (of a full one) leaves the stars as they are.</summary>
    public const float MinFlash = 0.01f;
    /// <summary>A full flash's light on the haze (linear).</summary>
    public static readonly Vector3 FlashSky = new(0.55f, 0.6f, 0.8f);
    /// <summary>A flash's share on the haze.</summary>
    public const float FlashHaze = 0.2f;
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
    private WeatherSystem? _system;
    private object? _systemOf;
    private Weather? _weatherOf;
    private float _flash;

    /// <summary>A lightning strike (on this thread): its thunder is the caller's (<see cref="Storm.Post"/>).</summary>
    public event Action<Strike>? Struck;

    /// <summary>Sets the renderer's light and depth cue for <paramref name="frame"/>: the level's own
    /// under the engine's weather with <paramref name="air"/>'s clouds, haze and dust on them;
    /// <paramref name="level"/>'s fires light the deck.</summary>
    public void Apply(Renderer renderer, in SkyFrame frame, AirView air, Level? level, ParticleLevel particles)
    {
        if (renderer.Lighting is not { } now)
        {
            (_base, _lit, _baseFog, _litFog, _system, _systemOf, _weatherOf, _flash) = (null, null, null, null, null, null, null, 0f);
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

        var system = SystemFor(frame, level, particles);
        system?.Update(frame.Seconds, frame.Eye);
        if (system != null && frame.Strike is { } ahead && frame.Weather.Storm is { } storm)
        {
            StrikeAhead(system, storm, frame.Eye, ahead);
        }

        // Our deck drifts by the wind times its drift; the engine adds the wind's travel once.
        var drift = system != null && air.Clouds is { } clouds ? system.Drift * (clouds.Drift - 1f) : Vector3.Zero;
        _lit = Clouded(_base!, _base!, air, _baseFog, FireAmount(level, air), particles, drift);

        // The map's own deck (no mod's clouds) drifts slowly on the effects' clock.
        if (air.Clouds == null && _lit.Clouds is { } own)
        {
            _lit = _lit with { Clouds = own with { Drift = OwnDrift * renderer.EffectTime } };
        }

        _flash = system == null ? 0f : system.Flash.Y / FullFlash;
        if (system != null)
        {
            // The game's unweathered light every frame (Apply would compound on its own).
            _lit = Washed(Bolted(system.Apply(_lit)), _flash);
        }

        _litFog = Dusted(Hazed(_baseFog, air.Haze, _flash), air);
        renderer.Lighting = _lit;
        renderer.Fog = _litFog;
    }

    /// <summary>The engine's weather for <paramref name="frame"/>: made for a new level, blended to a
    /// changed weather; none without rain, clouds or a storm.</summary>
    private WeatherSystem? SystemFor(in SkyFrame frame, Level? level, ParticleLevel particles)
    {
        var w = frame.Weather;
        if (w.Storm == null && w.Rain == null && w.Clouds == null)
        {
            (_system, _systemOf, _weatherOf) = (null, null, null);
            return null;
        }

        var target = WeatherMap.Of(w, _base!.Clouds?.Coverage ?? 0f);
        if (_system == null || !ReferenceEquals(level, _systemOf))
        {
            _system = new WeatherSystem(target, Vector3.UnitY, level?.Number ?? 0) { MetresPerUnit = 1f / Storm.UnitsPerMetre };
            _system.Struck += OnStruck;
            (_systemOf, _weatherOf) = (level, w);
        }
        else if (!ReferenceEquals(w, _weatherOf))
        {
            _system.Change(target, BlendSeconds);
            _weatherOf = w;
        }

        if (w.Storm is { } storm)
        {
            _system.LightningNear = storm.NearMetres * Storm.UnitsPerMetre;
            _system.LightningFar = storm.FarMetres * Storm.UnitsPerMetre;
        }

        // The flat deck's bolts reach up to it (the volume's to its base on their own).
        if (w.Clouds is { } clouds)
        {
            _system.BoltHeight = clouds.HeightMetres * Storm.UnitsPerMetre;
        }

        var drops = DropCount[(int)particles];
        _system.Drops = OperatingSystem.IsAndroid() ? Math.Min(drops, PhoneDrops) : drops;
        return _system;
    }

    private void OnStruck(Strike strike) => Struck?.Invoke(strike);

    /// <summary>A strike along <paramref name="ahead"/> (level), at the storm's nearest and brightness.</summary>
    private static void StrikeAhead(WeatherSystem system, StormTable storm, Vector3 eye, Vector3 ahead)
    {
        var level = new Vector3(ahead.X, 0f, ahead.Z);
        var way = level.LengthSquared() > 0f ? Vector3.Normalize(level) : Vector3.UnitZ;
        system.Strike(eye + way * (storm.NearMetres * Storm.UnitsPerMetre), storm.Brightness);
    }

    /// <summary><paramref name="lit"/>'s bolt as <see cref="EffectTable.Lightning"/>'s row draws it.</summary>
    public static Lighting Bolted(Lighting lit)
    {
        if (lit.Weather is not { Bolt: { } bolt } w)
        {
            return lit;
        }

        var row = EffectTable.Current[EffectTable.Lightning];
        bolt = bolt with { Colour = bolt.Colour * EffectTable.Light(row), Width = bolt.Width * (float)row.Size };
        return lit with { Weather = w with { Bolt = bolt } };
    }

    /// <summary><paramref name="lit"/>'s stars washed out by a flash (<paramref name="flash"/> of a full one).</summary>
    public static Lighting Washed(Lighting lit, float flash)
    {
        if (flash <= MinFlash || lit.Stars is not { } stars)
        {
            return lit;
        }

        return lit with { Stars = stars with { Brightness = stars.Brightness * MathF.Max(0f, 1f - flash) } };
    }

    /// <summary><paramref name="lit"/> under <paramref name="air"/>'s deck and haze: the deck's colour
    /// from <paramref name="around"/>'s light and the fires (<paramref name="fires"/> 0-1) under it,
    /// drifted by <paramref name="drift"/>; no stars when overcast. As it is without clouds or haze.</summary>
    public static Lighting Clouded(Lighting lit, Lighting around, AirView air, Fog? cue, float fires, ParticleLevel particles, Vector3 drift)
    {
        if (air.Clouds == null && air.Haze == null)
        {
            return lit;
        }

        var cover = air.Clouds?.Coverage ?? 0f;
        var horizon = air.Haze is { } haze ? new Vector4(Linear(haze.Colour), haze.Horizon)
            : cue is { Colour.W: > 0f } c ? new Vector4(Linear(new Vector3(c.Colour.X, c.Colour.Y, c.Colour.Z)), CueHorizon) : Vector4.Zero;
        // A haze alone keeps the map's own deck (a dusk sky's) under its horizon.
        var own = air.Clouds == null ? lit.Clouds : null;
        var layer = (own ?? new CloudLayer(cover, Vector3.Zero, 1f, 1f)) with { Horizon = horizon, HorizonBand = HorizonBand, Octaves = CloudOctaves[(int)particles] };
        if (air.Clouds is { } clouds)
        {
            var light = ((around.Sky + around.Ground) * 0.5f).Length() / NightAround;
            var colour = Linear(clouds.Colour) * Math.Clamp(light, 0.3f, 4f);
            layer = layer with
            {
                Colour = DeckQuality == CloudQuality.Flat ? colour : Albedo(clouds.Colour),
                Height = clouds.HeightMetres * Storm.UnitsPerMetre,
                Scale = clouds.SizeMetres * Storm.UnitsPerMetre,
                Drift = drift,
                Underlight = FireUnderlight * (fires * clouds.FireGlow),
                Quality = DeckQuality,
                Volume = VolumeOf(clouds),
            };
        }

        return lit with
        {
            Stars = cover >= Overcast ? null : lit.Stars,
            Clouds = layer,
        };
    }

    /// <summary>The volume's albedo of a deck's colour <paramref name="srgb"/> (the flat deck shows its
    /// colour, the volume is lit): the default colour's at <see cref="VolumeAlbedo"/>, the rest as bright.</summary>
    public static Vector3 Albedo(Vector3 srgb) =>
        Vector3.Min(Linear(srgb) * (VolumeAlbedo / Luma(Linear(CloudTable.Defaults.Colour))), Vector3.One);

    /// <summary>The volumetric deck of <paramref name="clouds"/>: the engine's in world units, its base
    /// at the deck's height, its cells by the deck's size.</summary>
    public static CloudVolume VolumeOf(CloudTable clouds)
    {
        const float U = Storm.UnitsPerMetre;
        var d = new CloudVolume();
        var cell = clouds.SizeMetres * U * VolumeCells;
        return d with
        {
            Base = clouds.HeightMetres * U,
            Thickness = DeckMetres * U,
            Density = d.Density / U,
            WeatherScale = cell,
            ShapeScale = cell * (d.ShapeScale / d.WeatherScale),
            DetailScale = cell * (d.DetailScale / d.WeatherScale),
            Reach = d.Reach * U,
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
        var colour = Linear(haze.Colour) + FlashSky * (flash * FlashHaze);
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
