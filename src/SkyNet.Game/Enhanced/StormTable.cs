using System.Globalization;
using RetroEng.Audio;
using RetroEng.Json;

namespace SkyNet.Game.Enhanced;

/// <summary>
/// A mod's storm (a <see cref="Weather"/> file of kind <c>storm</c>, docs/modding.md): lightning and
/// thunder over the outdoor maps in the enhanced look (<see cref="Storm"/>).
/// <code>
///   { "kind": "storm", "on": true, "interval": 20, "brightness": 1, "distance": [800, 6000], "thunderVolume": 0.8 }
/// </code>
/// Every key is optional (the defaults below); an unknown key or a value out of range is an error.
/// </summary>
public sealed record StormTable(bool On, float Interval, float Brightness, float NearMetres, float FarMetres, float ThunderVolume)
{
    /// <summary>The values a key left out takes.</summary>
    public static StormTable Defaults { get; } = new(true, 20f, 1f, 800f, 6000f, 0.8f);

    /// <summary>The shortest mean interval and the farthest bolt (metres) a file may ask for.</summary>
    public const float MinInterval = 1f, MaxMetres = 50000f, MaxBrightness = 4f;

    /// <summary>The mod's own thunder (<see cref="Weather.ThunderFile"/>) played for every strike;
    /// null: made in code (<see cref="Audio.Thunder"/>).</summary>
    public Sound? Recorded { get; init; }

    /// <summary>Reads a storm file. <see cref="JsonDataException"/> when it does not read.</summary>
    public static StormTable Parse(ReadOnlySpan<byte> utf8) => From(JsonItem.Parse(utf8).AsObject());

    /// <summary>Reads a storm file's object.</summary>
    public static StormTable From(JsonItem o)
    {
        ShotLightTable.Only(o, "kind", "on", "interval", "brightness", "distance", "thunderVolume", "comment");
        var t = Defaults;
        if (o.Opt("on") is { } on)
        {
            t = t with { On = on.AsBool() };
        }

        t = t with
        {
            Interval = Weather.Ranged(o, "interval", MinInterval, float.MaxValue) ?? t.Interval,
            Brightness = Weather.Ranged(o, "brightness", 0f, MaxBrightness) ?? t.Brightness,
            ThunderVolume = Weather.Ranged(o, "thunderVolume", 0f, 1f) ?? t.ThunderVolume,
        };
        if (o.Opt("distance") is not { } d)
        {
            return t;
        }

        // [near, far] metres: the bolts strike between them.
        var items = d.Items;
        if (items.Count != 2)
        {
            throw d.Error("\"distance\" is [near, far] in metres");
        }

        float near = (float)items[0].AsDouble(), far = (float)items[1].AsDouble();
        if (!(near > 0f && far >= near && far <= MaxMetres))
        {
            throw d.Error(string.Create(CultureInfo.InvariantCulture, $"\"distance\" needs 0 < near <= far <= {MaxMetres}"));
        }

        return t with { NearMetres = near, FarMetres = far };
    }
}
