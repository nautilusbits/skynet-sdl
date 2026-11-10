using System.Numerics;
using RetroEng.Render;
using Sky = RetroEng.Render.Weather;

namespace SkyNet.Game.Enhanced;

/// <summary>
/// The mods' weather as the engine's (<see cref="WeatherSystem"/>): lengths in world units
/// (<see cref="Storm.UnitsPerMetre"/>), haze and dust left to <see cref="WeatherSky"/>.
/// <code>
///   rain   ─► Precipitation.Rain, Intensity, Wetness; its own steady wind without a wind file
///   storm  ─► LightningRate (60 / interval), LightningIntensity (brightness), towering dense clouds
///   clouds ─► Cover (rain: 1)
///   wind   ─► Wind (way × speed, gusts); none: the rain's, else a calm 1.5 m/s
/// </code>
/// </summary>
public static class WeatherMap
{
    /// <summary>The air's drift without a wind (metres a second).</summary>
    public const float CalmMetres = 1.5f;
    /// <summary>The way the calm air drifts (level, unit).</summary>
    private static readonly Vector3 CalmWay = new(0.6f, 0f, 0.8f);
    /// <summary>The engine's gusts' size (metres; <see cref="RetroEng.Render.Wind.GustScale"/>).</summary>
    private const float GustMetres = 60f;
    private const float SecondsPerMinute = 60f;

    /// <summary>The engine's weather of <paramref name="w"/>; <paramref name="ownCover"/> the map's
    /// own deck's coverage, kept without a clouds file (the engine sets the deck's coverage).</summary>
    public static Sky Of(Weather w, float ownCover)
    {
        // A storm's clouds tower, a rain's or a deck's lie low.
        var look = w.Storm != null ? Sky.Storm : Sky.Rain;
        var sky = new Sky
        {
            Wind = WindOf(w),
            Cover = w.Clouds?.Coverage ?? ownCover,
            CloudType = look.CloudType,
            CloudDensity = look.CloudDensity,
        };

        if (w.Rain is { } rain)
        {
            sky = sky with { Precipitation = Precipitation.Rain, Intensity = rain.Intensity, Wetness = rain.Intensity };
        }

        if (w.Storm is { } storm)
        {
            sky = sky with { LightningRate = SecondsPerMinute / storm.Interval, LightningIntensity = storm.Brightness };
        }

        return sky;
    }

    /// <summary>The wind of <paramref name="w"/>: its wind file's, else the rain's own (steady), else a calm air.</summary>
    public static RetroEng.Render.Wind WindOf(Weather w)
    {
        if (w.Wind is { } table)
        {
            return WindOf(table);
        }

        var velocity = w.Rain is { } rain ? Level(rain.WindDirection) * (rain.WindSpeed * Storm.UnitsPerMetre)
            : CalmWay * (CalmMetres * Storm.UnitsPerMetre);
        return new RetroEng.Render.Wind(velocity) { GustScale = GustMetres * Storm.UnitsPerMetre };
    }

    /// <summary>A wind file's wind (units a second).</summary>
    public static RetroEng.Render.Wind WindOf(WindTable table) =>
        new(Level(table.Direction) * (table.Speed * Storm.UnitsPerMetre), table.Gusts) { GustScale = GustMetres * Storm.UnitsPerMetre };

    /// <summary>The level way <paramref name="degrees"/> round up (0 towards +Z, 90 towards +X).</summary>
    private static Vector3 Level(float degrees)
    {
        var angle = degrees * MathF.PI / 180f;
        return new Vector3(MathF.Sin(angle), 0f, MathF.Cos(angle));
    }
}
