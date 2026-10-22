using RetroEng.Audio;
using RetroEng.Json;

namespace SkyNet.Game.Enhanced;

/// <summary>
/// A mod's rain (a <see cref="Weather"/> file of kind <c>rain</c>, docs/modding.md): streaks falling
/// round the eye and a hiss over the outdoor maps in the enhanced look (<see cref="Rain"/>).
/// <code>
///   { "kind": "rain", "on": true, "intensity": 0.6, "windDirection": 30, "windSpeed": 4, "volume": 0.6 }
/// </code>
/// Every key is optional (the defaults below); an unknown key or a value out of range is an error.
/// </summary>
/// <param name="Intensity">How hard it rains (0-1): the drops and the hiss.</param>
/// <param name="WindDirection">The way the drops are blown, degrees round the up axis (0 towards +Z, 90 towards +X).</param>
/// <param name="WindSpeed">The wind in metres a second: how slanted the drops fall.</param>
/// <param name="Volume">The hiss's gain at full intensity (0-1), on the Effects bus.</param>
public sealed record RainTable(bool On, float Intensity, float WindDirection, float WindSpeed, float Volume)
{
    /// <summary>The values a key left out takes.</summary>
    public static RainTable Defaults { get; } = new(true, 0.6f, 30f, 4f, 0.6f);

    /// <summary>The strongest wind a file may ask for (metres a second).</summary>
    public const float MaxWind = 30f;

    /// <summary>The mod's own hiss (<see cref="Weather.RainFile"/>, looped); null: made in code
    /// (<see cref="Audio.RainHiss"/>).</summary>
    public Sound? Recorded { get; init; }

    /// <summary>Reads a rain file's object. <see cref="JsonDataException"/> when it does not read.</summary>
    public static RainTable From(JsonItem o)
    {
        ShotLightTable.Only(o, "kind", "on", "intensity", "windDirection", "windSpeed", "volume", "comment");
        var t = Defaults;
        if (o.Opt("on") is { } on)
        {
            t = t with { On = on.AsBool() };
        }

        return t with
        {
            Intensity = Weather.Ranged(o, "intensity", 0f, 1f) ?? t.Intensity,
            WindDirection = Weather.Ranged(o, "windDirection", -360f, 360f) ?? t.WindDirection,
            WindSpeed = Weather.Ranged(o, "windSpeed", 0f, MaxWind) ?? t.WindSpeed,
            Volume = Weather.Ranged(o, "volume", 0f, 1f) ?? t.Volume,
        };
    }
}
