using System.Numerics;
using RetroEng.Json;

namespace SkyNet.Game.Mods;

/// <summary>
/// A mod model's levels of detail (docs/modding.md#levels-of-detail): <c>NAME.lod1.glb</c>,
/// <c>NAME.lod2.glb</c>... beside <c>NAME.glb</c>, each coarser, drawn once the model's largest side
/// covers less of the screen's height than its threshold.
/// <code>
///   screen  1.0 ─── level 0 (NAME.glb) ─── 0.25 ─── level 1 (NAME.lod1.glb) ─── 0.08 ─── level 2 ...
///   hysteresis 0.1: a crossed threshold holds until 0.25·1.1 going back, the next is crossed at 0.08·0.9
/// </code>
/// </summary>
/// <param name="Screen">Per level from 1, the screen-height fraction below which it is drawn
/// (descending, each in (0, 1]); empty: no levels.</param>
/// <param name="Hysteresis">The band around a threshold a switch back must clear (0.1: ±10 %).</param>
internal sealed record LodSettings(float[] Screen, float Hysteresis)
{
    /// <summary>LOD1 at 25 % of the screen's height, LOD2 at 8 %, ±10 %.</summary>
    public static readonly LodSettings Default = new([0.25f, 0.08f], 0.1f);

    /// <summary>The most levels a model may have beside its own.</summary>
    public const int MaxLevels = 3;

    private const float MaxHysteresis = 0.5f;

    /// <summary>A level's file name without the extension: ("HUMMER", 1) → "HUMMER.lod1".</summary>
    public static string FileName(string name, int level) => $"{name}.lod{level}";

    /// <summary>A sidecar's <c>"lod"</c>: <c>{"screen": [0.25, 0.08], "hysteresis": 0.1}</c> (each key
    /// optional) or <c>"off"</c>; a bad value throws <see cref="FormatException"/>.</summary>
    public static LodSettings Parse(JsonItem e)
    {
        if (e.TryString(out var word))
        {
            return word == "off" ? new LodSettings([], 0f) : throw new FormatException($"lod '{word}' (an object or \"off\")");
        }

        var s = Default;
        foreach (var m in e.AsObject().Members)
        {
            s = m.Key switch
            {
                "screen" => s with { Screen = m.Value.ToArray(x => (float)x.AsDouble()) },
                "hysteresis" => s with { Hysteresis = (float)m.Value.AsDouble() },
                "comment" => s,
                _ => throw new FormatException($"lod: unknown key '{m.Key}'"),
            };
        }

        if (s.Screen.Length > MaxLevels)
        {
            throw new FormatException($"lod: at most {MaxLevels} screen thresholds");
        }

        for (var i = 0; i < s.Screen.Length; i++)
        {
            if (!(s.Screen[i] > 0f && s.Screen[i] <= 1f) || (i > 0 && s.Screen[i] >= s.Screen[i - 1]))
            {
                throw new FormatException("lod: screen thresholds descending, each in (0, 1]");
            }
        }

        if (!(s.Hysteresis >= 0f && s.Hysteresis < MaxHysteresis))
        {
            throw new FormatException($"lod: hysteresis in [0, {MaxHysteresis})");
        }

        return s;
    }

    /// <summary>A sidecar holding only <c>"lod"</c> (and <c>"comment"</c>): a robot template's
    /// <c>NAME.robot.json</c>; none: <see cref="Default"/>.</summary>
    public static LodSettings ParseFile(byte[] json)
    {
        var s = Default;
        foreach (var p in JsonItem.Parse(json, lenient: true).AsObject().Members)
        {
            s = p.Key switch
            {
                "lod" => Parse(p.Value),
                "comment" => s,
                _ => throw new FormatException($"unknown key '{p.Key}'"),
            };
        }

        return s;
    }

    /// <summary>
    /// The level to draw at <paramref name="fraction"/> of the screen's height, now drawing
    /// <paramref name="current"/> (−1: none yet, no hysteresis). A threshold crossed (k &lt; current)
    /// holds until the model grows past it by the band; the next is crossed only below it by the band.
    /// </summary>
    public int Pick(int current, float fraction)
    {
        var level = 0;
        for (var k = 0; k < Screen.Length; k++)
        {
            var band = current < 0 ? 1f : k < current ? 1f + Hysteresis : 1f - Hysteresis;
            if (fraction >= Screen[k] * band)
            {
                break;
            }

            level = k + 1;
        }

        return level;
    }
}

/// <summary>Where the frame is seen from, for picking levels of detail: the eye and the screen
/// heights one world unit spans at distance 1 (1 / (2 tan(fovY / 2))); default: none (level 0).</summary>
internal readonly record struct LodView(Vector3 Eye, float Focus)
{
    /// <summary>The view of an eye with a vertical field of view of <paramref name="fovY"/> radians.</summary>
    public static LodView Of(Vector3 eye, float fovY) => new(eye, fovY > 0f ? 0.5f / MathF.Tan(fovY / 2f) : 0f);

    /// <summary>The screen-height fraction a thing <paramref name="size"/> across at <paramref name="centre"/>
    /// covers; no view: +∞ (the finest level).</summary>
    public float Fraction(Vector3 centre, float size)
    {
        if (Focus <= 0f)
        {
            return float.PositiveInfinity;
        }

        var distance = MathF.Max(Vector3.Distance(Eye, centre), 1f);
        return size * Focus / distance;
    }
}
