using System.Numerics;
using RetroEng.Json;

namespace SkyNet.Game.Enhanced;

/// <summary>
/// A mod's cloud deck (a <see cref="Weather"/> file of kind <c>clouds</c>, docs/modding.md): a low,
/// dark layer over the outdoor maps in the enhanced look that hides the stars and the moon, drifting
/// with the wind, lit from below by the fires and the lightning (<see cref="WeatherSky"/>).
/// <code>
///   { "kind": "clouds", "on": true, "coverage": 0.85, "height": 400, "size": 250, "colour": [0.11, 0.1, 0.095], "fireGlow": 1, "drift": 2 }
/// </code>
/// Every key is optional (the defaults below); an unknown key or a value out of range is an error.
/// </summary>
/// <param name="Coverage">The share of the sky covered (0-1; 1 overcast).</param>
/// <param name="HeightMetres">The deck's height over the eye (metres).</param>
/// <param name="SizeMetres">A cloud's size (metres: the noise's cell).</param>
/// <param name="Colour">The deck's colour at night (sRGB 0-1), lit by the map's light around.</param>
/// <param name="FireGlow">How much the map's fires light it from below (0-4).</param>
/// <param name="Drift">Its drift as a multiple of the wind (0-10).</param>
public sealed record CloudTable(bool On, float Coverage, float HeightMetres, float SizeMetres, Vector3 Colour, float FireGlow, float Drift)
{
    /// <summary>The values a key left out takes.</summary>
    public static CloudTable Defaults { get; } = new(true, 0.85f, 400f, 250f, new Vector3(0.11f, 0.1f, 0.095f), 1f, 2f);

    /// <summary>The deck with rain: overcast, no star through it.</summary>
    public const float RainCoverage = 1f;

    /// <summary>Reads a clouds file's object. <see cref="JsonDataException"/> when it does not read.</summary>
    public static CloudTable From(JsonItem o)
    {
        ShotLightTable.Only(o, "kind", "on", "coverage", "height", "size", "colour", "fireGlow", "drift", "comment");
        var t = Defaults;
        if (o.Opt("on") is { } on)
        {
            t = t with { On = on.AsBool() };
        }

        return t with
        {
            Coverage = Weather.Ranged(o, "coverage", 0f, 1f) ?? t.Coverage,
            HeightMetres = Weather.Ranged(o, "height", 50f, 5000f) ?? t.HeightMetres,
            SizeMetres = Weather.Ranged(o, "size", 10f, 5000f) ?? t.SizeMetres,
            Colour = Weather.Colour(o, "colour") ?? t.Colour,
            FireGlow = Weather.Ranged(o, "fireGlow", 0f, 4f) ?? t.FireGlow,
            Drift = Weather.Ranged(o, "drift", 0f, 10f) ?? t.Drift,
        };
    }
}
