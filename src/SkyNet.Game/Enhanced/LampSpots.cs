using System.Numerics;
using RetroEng.Content;
using RetroEng.Json;
using RetroEng.Render;
using RetroEng.Render.Lights;
using SkyNet.Game.Mods;

namespace SkyNet.Game.Enhanced;

/// <summary>What an override makes a light.</summary>
public enum LampKind { Omni, Spot }

/// <summary>One row of <c>lights/lamps.json</c>: which lights (every selector given must match;
/// none given: every lamp) and what it sets (null: as derived).</summary>
/// <param name="Map">The map number (MAP.NNN).</param>
/// <param name="Key">The placed record's MAP file offset (an art lamp's mesh, a map light's record).</param>
/// <param name="Model">The art lamp's model name (no ".3D", any case). A map light has none: a row naming
/// a model or a texture never matches one.</param>
/// <param name="Texture">The art lamp's main texture word (bank &lt;&lt; 7 | record).</param>
/// <param name="Srgb">The colour, sRGB 0-1.</param>
/// <param name="Intensity">The intensity at 150 u (<see cref="LightFalloff.EnergyAtRef"/>).</param>
/// <param name="Cone">A spot's outer half angle, degrees; <paramref name="Inner"/> where it is full.</param>
public sealed record LampOverride(int? Map = null, int? Key = null, string? Model = null, int? Texture = null, LampKind? Kind = null,
    Vector3? Direction = null, float? Cone = null, float? Inner = null, Vector3? Srgb = null, float? Intensity = null, float? Range = null)
{
    /// <summary>Whether it applies to a light of <paramref name="map"/>, record <paramref name="key"/>,
    /// <paramref name="model"/> (null: a map light) and <paramref name="texture"/>.</summary>
    public bool Matches(int map, int key, string? model, int texture) =>
        (Map is not { } m || m == map)
        && (Key is not { } k || k == key)
        && (Model is not { } name || (model != null && string.Equals(name, model, StringComparison.OrdinalIgnoreCase)))
        && (Texture is not { } t || (model != null && t == texture));
}

/// <summary>
/// Spot lights for the enhanced look's lamps. An art lamp (<see cref="EmissiveLamps"/>) whose lit
/// triangles face one way (<see cref="LampLight.Coherence"/> at least <see cref="MinCoherence"/>: a
/// ceiling strip, a wall fitting, a street lamp's head) becomes a shadowless spot along the way they
/// face, with a soft cone; a bulb or panel lit all round, a fire and a MAP light stay omni:
/// <code>
///   fire, no facing, coherence &lt; 0.8 ─────────────► omni
///   facing down (ceiling, a street lamp's head) ────► spot along it, full to 45°, dark at 88° (the walls lit, not its ceiling)
///   any other way (a wall fitting, lifted under the top) ► spot along it, full to 35°, dark at 85°
/// </code>
/// <c>lights/lamps.json</c> in a mod (docs/modding.md) overrides any of it:
/// <code>
///   { "lamps": [ { "map": 210, "key": 12345, "model": "LAMP01", "texture": 26626,
///                  "kind": "spot", "direction": [0, -1, 0], "cone": 60, "inner": 30,
///                  "colour": [1, 0.9, 0.7], "intensity": 0.12, "range": 900 } ] }
/// </code>
/// Every member is optional; the rows apply in order, a later one over an earlier one.
/// </summary>
public static class LampSpots
{
    public const string FileName = "lights/lamps.json";
    /// <summary>The least <see cref="LampLight.Coherence"/> a spot needs.</summary>
    public const float MinCoherence = 0.8f;
    /// <summary>A lamp facing down at least this much (−Y of its facing) is a ceiling lamp.</summary>
    public const float CeilingDown = 0.5f;
    public const float CeilingInner = 45f, CeilingOuter = 88f, WallInner = 35f, WallOuter = 85f;
    /// <summary>A forced spot without a direction known points down with this cone.</summary>
    public const float DefaultInner = 35f, DefaultOuter = 75f;

    /// <summary>Whether an art lamp is derived a spot (above).</summary>
    public static bool IsSpot(in LampLight lamp) =>
        !lamp.Flicker && lamp.Coherence >= MinCoherence && lamp.Facing.LengthSquared() > 0.25f;

    /// <summary>A lamp's light: <paramref name="colour"/> (linear) at its place, a spot when
    /// <see cref="IsSpot"/>.</summary>
    public static PointLight Derive(in LampLight lamp, Vector3 colour)
    {
        var light = new PointLight(lamp.Position, colour, lamp.Range, lamp.Energy);
        if (!IsSpot(lamp))
        {
            return light;
        }

        var ceiling = -lamp.Facing.Y >= CeilingDown;
        return ceiling ? light.AsSpot(lamp.Facing, CeilingInner, CeilingOuter) : light.AsSpot(lamp.Facing, WallInner, WallOuter);
    }

    /// <summary>The rows that apply to a light, in order.</summary>
    public static IEnumerable<LampOverride> For(IReadOnlyList<LampOverride> rows, int map, int key, string? model, int texture)
    {
        foreach (var r in rows)
        {
            if (r.Matches(map, key, model, texture))
            {
                yield return r;
            }
        }
    }

    /// <summary>A light with a row applied. <paramref name="facing"/>: the way its art faces (a forced
    /// spot with no direction takes it, else down).</summary>
    public static PointLight Apply(PointLight light, LampOverride row, Vector3 facing = default)
    {
        if (row.Srgb is { } srgb)
        {
            light = light with { Colour = LevelLights.Linear(srgb) };
        }

        if (row.Intensity is { } intensity)
        {
            light = light with { Energy = LightFalloff.EnergyAtRef(intensity) };
        }

        if (row.Range is { } range)
        {
            light = light with { Range = range };
        }

        var kind = row.Kind ?? (light.IsSpot || row.Direction != null || row.Cone != null ? LampKind.Spot : LampKind.Omni);
        if (kind == LampKind.Omni)
        {
            return light.AsSpot(Vector3.Zero, 0f, 0f);
        }

        var direction = row.Direction ?? (light.IsSpot ? light.SpotDirection : facing.LengthSquared() > 0.25f ? facing : -Vector3.UnitY);
        var outer = row.Cone ?? (light.IsSpot ? Degrees(light.SpotCosOuter) : DefaultOuter);
        var inner = row.Inner ?? (light.IsSpot && row.Cone == null ? Degrees(light.SpotCosInner) : MathF.Min(DefaultInner, outer * 0.5f));
        return light.AsSpot(direction, inner, outer);
    }

    private static float Degrees(float cos) => MathF.Acos(Math.Clamp(cos, -1f, 1f)) * 180f / MathF.PI;

    /// <summary>Reads a <c>lights/lamps.json</c>. <see cref="JsonDataException"/> when it does not read.</summary>
    public static List<LampOverride> Parse(ReadOnlySpan<byte> utf8)
    {
        var root = JsonItem.Parse(utf8).AsObject();
        ShotLightTable.Only(root, "lamps");
        var rows = new List<LampOverride>();
        foreach (var item in root.Opt("lamps")?.Items ?? [])
        {
            var o = item.AsObject();
            ShotLightTable.Only(o, "map", "key", "model", "texture", "kind", "direction", "cone", "inner", "colour", "intensity", "range");
            LampKind? kind = o.Opt("kind")?.AsString().ToLowerInvariant() switch
            {
                null => null,
                "spot" => LampKind.Spot,
                "omni" => LampKind.Omni,
                _ => throw o.Member("kind").Error("\"kind\" is \"spot\" or \"omni\""),
            };
            var direction = o.Opt("direction") is { } d ? Direction(d) : (Vector3?)null;
            rows.Add(new LampOverride(o.Opt("map")?.AsInt(), o.Opt("key")?.AsInt(), o.Opt("model")?.AsString().Replace(".3D", "", StringComparison.OrdinalIgnoreCase),
                o.Opt("texture") is { } t ? TextureWord(t) : null, kind, direction, ShotLightTable.Float(o, "cone"), ShotLightTable.Float(o, "inner"),
                ShotLightTable.Colour(o, "colour"), ShotLightTable.Float(o, "intensity"), ShotLightTable.Float(o, "range")));
        }

        return rows;
    }

    /// <summary>A texture as a word (bank &lt;&lt; 7 | record) or "BANK/RECORD".</summary>
    private static int TextureWord(JsonItem t)
    {
        if (t.Kind == JsonKind.Number)
        {
            return t.AsInt();
        }

        var parts = t.AsString().Split('/');
        return parts.Length == 2 && int.TryParse(parts[0], out var bank) && int.TryParse(parts[1], out var record) && record is >= 0 and < 128
            ? bank << 7 | record
            : throw t.Error("\"texture\" is a word or \"BANK/RECORD\"");
    }

    private static Vector3 Direction(JsonItem d)
    {
        var items = d.Items;
        if (items.Count != 3)
        {
            throw d.Error("\"direction\" is [x, y, z] (y up)");
        }

        var v = new Vector3((float)items[0].AsDouble(), (float)items[1].AsDouble(), (float)items[2].AsDouble());
        return v.LengthSquared() > 1e-12f && float.IsFinite(v.X + v.Y + v.Z) ? Vector3.Normalize(v) : throw d.Error("\"direction\" must not be zero");
    }

    // The mods read and what they gave, as one immutable reference: read from any thread (workers too).
    private sealed record Loaded(IReadOnlyList<LampOverride> Value, VirtualFileSystem? From);

    private static Loaded _box = new([], null);

    /// <summary>The rows of the mounted mods' file (the highest mod with it wins); none without one,
    /// or when it does not read (logged).</summary>
    public static IReadOnlyList<LampOverride> Current
    {
        get
        {
            var vfs = ModContent.Files;
            var cached = Volatile.Read(ref _box);
            if (!ReferenceEquals(vfs, cached.From))
            {
                cached = new Loaded(Load(vfs), vfs);
                Volatile.Write(ref _box, cached);
            }

            return cached.Value;
        }
    }

    private static IReadOnlyList<LampOverride> Load(VirtualFileSystem? vfs)
    {
        if (vfs?.TryReadAllBytes(FileName) is not { } bytes)
        {
            return [];
        }

        try
        {
            return Parse(bytes);
        }
        catch (Exception e) when (e is JsonDataException or System.Text.Json.JsonException)
        {
            RetroEng.Diagnostics.Log.Warn("Mods", $"{FileName}: {e.Message}; the lamps stay as derived");
            return [];
        }
    }
}
