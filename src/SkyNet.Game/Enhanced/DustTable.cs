using System.Numerics;
using RetroEng.Json;

namespace SkyNet.Game.Enhanced;

/// <summary>
/// A mod's dust (a <see cref="Weather"/> file of kind <c>dust</c>, docs/modding.md): faint sheets of
/// dust drifting near the ground, and now and then a gust raising a dust cloud upwind that rolls past
/// the eye with the wind (<see cref="DustClouds"/>), fine grit streaking in it; over the outdoor maps
/// in the enhanced look, none under cover (<see cref="Dust"/>).
/// <code>
///   { "kind": "dust", "on": true, "amount": 0.2, "colour": [0.42, 0.36, 0.28], "height": 3, "streaks": 0.6,
///     "interval": 45, "duration": 24, "peak": 0.8, "size": 45, "rise": 10 }
/// </code>
/// Every key is optional (the defaults below); an unknown key or a value out of range is an error.
/// </summary>
/// <param name="Amount">How thick the faint sheets between the clouds are (0-1): their number and cover.</param>
/// <param name="Colour">The dust's colour (sRGB 0-1), lit by the map's light around.</param>
/// <param name="HeightMetres">How high over the ground the sheets drift (metres).</param>
/// <param name="Streaks">How much grit blows past in a dust cloud (0-1).</param>
public sealed record DustTable(bool On, float Amount, Vector3 Colour, float HeightMetres, float Streaks)
{
    /// <summary>The values a key left out takes.</summary>
    public static DustTable Defaults { get; } = new(true, 0.2f, new Vector3(0.42f, 0.36f, 0.28f), 3f, 0.6f);

    /// <summary>The mean seconds from one dust cloud to the next.</summary>
    public float Interval { get; init; } = 45f;

    /// <summary>A cloud's life (seconds): it rises upwind, passes the eye halfway and is gone.</summary>
    public float Duration { get; init; } = 24f;

    /// <summary>How thick a cloud's core is (0-1; 1: the view closes to a few metres).</summary>
    public float Peak { get; init; } = 0.8f;

    /// <summary>A cloud's radius along the wind (metres; across it 1.6 times).</summary>
    public float SizeMetres { get; init; } = 45f;

    /// <summary>How high a cloud climbs over its life (metres), spreading and thinning.</summary>
    public float RiseMetres { get; init; } = 10f;

    /// <summary>Reads a dust file's object. <see cref="JsonDataException"/> when it does not read.</summary>
    public static DustTable From(JsonItem o)
    {
        ShotLightTable.Only(o, "kind", "on", "amount", "colour", "height", "streaks", "interval", "duration", "peak", "size", "rise",
            "comment");
        var t = Defaults;
        if (o.Opt("on") is { } on)
        {
            t = t with { On = on.AsBool() };
        }

        return t with
        {
            Amount = Weather.Ranged(o, "amount", 0f, 1f) ?? t.Amount,
            Colour = Weather.Colour(o, "colour") ?? t.Colour,
            HeightMetres = Weather.Ranged(o, "height", 0.5f, 30f) ?? t.HeightMetres,
            Streaks = Weather.Ranged(o, "streaks", 0f, 1f) ?? t.Streaks,
            Interval = Weather.Ranged(o, "interval", 5f, 600f) ?? t.Interval,
            Duration = Weather.Ranged(o, "duration", 3f, 120f) ?? t.Duration,
            Peak = Weather.Ranged(o, "peak", 0f, 1f) ?? t.Peak,
            SizeMetres = Weather.Ranged(o, "size", 5f, 200f) ?? t.SizeMetres,
            RiseMetres = Weather.Ranged(o, "rise", 0f, 50f) ?? t.RiseMetres,
        };
    }
}
