using System.Numerics;
using RetroEng.Json;

namespace SkyNet.Game.Enhanced;

/// <summary>
/// A mod's haze (a <see cref="Weather"/> file of kind <c>haze</c>, docs/modding.md): thicker,
/// coloured air over the outdoor maps in the enhanced look. The level's depth cue (its
/// <see cref="RetroEng.Render.Fog"/>) takes the haze's colour and ends at its visibility, and the
/// sky over the horizon fades into it (<see cref="WeatherSky"/>).
/// <code>
///   { "kind": "haze", "on": true, "colour": [0.17, 0.15, 0.125], "visibility": 60, "density": 1, "horizon": 0.75 }
/// </code>
/// Every key is optional (the defaults below); an unknown key or a value out of range is an error.
/// </summary>
/// <param name="Colour">The haze's colour at night (sRGB 0-1); a flash lights it.</param>
/// <param name="VisibilityMetres">Where it hides everything (metres; past the far clip: the far clip).</param>
/// <param name="Density">How much it replaces the palette's haze colour (0-1).</param>
/// <param name="Horizon">How thickly it covers the sky at the horizon (0-1).</param>
public sealed record HazeAirTable(bool On, Vector3 Colour, float VisibilityMetres, float Density, float Horizon)
{
    /// <summary>The values a key left out takes.</summary>
    public static HazeAirTable Defaults { get; } = new(true, new Vector3(0.17f, 0.15f, 0.125f), 60f, 1f, 0.75f);

    /// <summary>Reads a haze file's object. <see cref="JsonDataException"/> when it does not read.</summary>
    public static HazeAirTable From(JsonItem o)
    {
        ShotLightTable.Only(o, "kind", "on", "colour", "visibility", "density", "horizon", "comment");
        var t = Defaults;
        if (o.Opt("on") is { } on)
        {
            t = t with { On = on.AsBool() };
        }

        return t with
        {
            Colour = Weather.Colour(o, "colour") ?? t.Colour,
            VisibilityMetres = Weather.Ranged(o, "visibility", 5f, 10000f) ?? t.VisibilityMetres,
            Density = Weather.Ranged(o, "density", 0f, 1f) ?? t.Density,
            Horizon = Weather.Ranged(o, "horizon", 0f, 1f) ?? t.Horizon,
        };
    }
}
