using RetroEng.Audio;
using RetroEng.Json;

namespace SkyNet.Game.Enhanced;

/// <summary>
/// A mod's wind (a <see cref="Weather"/> file of kind <c>wind</c>, docs/modding.md): one wind over the
/// outdoor maps in the enhanced look that slants the rain, blows the dust, drifts the clouds, leans
/// the smoke and howls (<see cref="Wind"/>).
/// <code>
///   { "kind": "wind", "on": true, "direction": 60, "speed": 8, "gusts": 0.5, "volume": 0.5 }
/// </code>
/// Every key is optional (the defaults below); an unknown key or a value out of range is an error.
/// </summary>
/// <param name="Direction">The way it blows, degrees round the up axis (0 towards +Z, 90 towards +X).</param>
/// <param name="Speed">Its mean speed (metres a second).</param>
/// <param name="Gusts">How gusty (0 steady, 1 wild).</param>
/// <param name="Volume">The howl's gain (0-1) in a 12 m/s wind, on the Effects bus.</param>
public sealed record WindTable(bool On, float Direction, float Speed, float Gusts, float Volume)
{
    /// <summary>The values a key left out takes.</summary>
    public static WindTable Defaults { get; } = new(true, 60f, 8f, 0.5f, 0.5f);

    /// <summary>The mod's own howl (<see cref="Weather.WindFile"/>, looped); null: made in code
    /// (<see cref="Audio.WindHowl"/>).</summary>
    public Sound? Recorded { get; init; }

    /// <summary>Reads a wind file's object. <see cref="JsonDataException"/> when it does not read.</summary>
    public static WindTable From(JsonItem o)
    {
        ShotLightTable.Only(o, "kind", "on", "direction", "speed", "gusts", "volume", "comment");
        var t = Defaults;
        if (o.Opt("on") is { } on)
        {
            t = t with { On = on.AsBool() };
        }

        return t with
        {
            Direction = Weather.Ranged(o, "direction", -360f, 360f) ?? t.Direction,
            Speed = Weather.Ranged(o, "speed", 0f, RainTable.MaxWind) ?? t.Speed,
            Gusts = Weather.Ranged(o, "gusts", 0f, 1f) ?? t.Gusts,
            Volume = Weather.Ranged(o, "volume", 0f, 1f) ?? t.Volume,
        };
    }
}
