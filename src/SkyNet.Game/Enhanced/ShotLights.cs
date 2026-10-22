using System.Globalization;
using System.Numerics;
using RetroEng.Content;
using RetroEng.Json;
using RetroEng.Render.Lights;
using SkyNet.Game.Mods;
using SkyNet.Game.Weapons;

namespace SkyNet.Game.Enhanced;

/// <summary>How strongly a shot lights the world in the enhanced look: a hand weapon's bolt a
/// little, a heavy cannon's (a robot's, the biggest hand laser) more, a vehicle's (the HK's and the
/// jeep's blasting lasers) most.</summary>
public enum ShotClass { Hand, Heavy, Vehicle }

/// <summary>A class's light: the bolt's in flight (its reach in world units and its intensity at
/// 150 u, <see cref="LightFalloff.EnergyAtRef"/>) and its impact flash (reach, intensity, seconds,
/// fading linearly).</summary>
public readonly record struct ShotClassLight(float Radius, float Intensity, float ImpactRadius, float ImpactIntensity, float ImpactLife);

/// <summary>One row of the table: which shots (an ammo record's index, or its model's name
/// prefix, e.g. "LASER2"; both given, both must match) and what it sets (null: the default).</summary>
public sealed record ShotLightEntry(int? Ammo, string? Model, Vector3? Srgb = null, ShotClass? Class = null, float? Radius = null,
    float? Intensity = null, float? ImpactRadius = null, float? ImpactIntensity = null, float? ImpactLife = null)
{
    public bool Matches(AmmoRecord ammo) =>
        (Ammo is not { } i || i == ammo.Index)
        && (Model is not { } m || ammo.Model.StartsWith(m, StringComparison.OrdinalIgnoreCase));
}

/// <summary>A shot's light, resolved: linear colour, the bolt's reach and energy, the impact's.</summary>
public readonly record struct ShotLight(Vector3 Colour, float Range, float Energy, float ImpactRange, float ImpactEnergy, float ImpactLife);

/// <summary>
/// The laser and plasma shots' own light in the enhanced look (<see cref="GameLights"/>): a moving
/// coloured point light on the bolt and a short flash where it strikes, by ammo record. The defaults
/// are made from the ammo table itself, so SkyNET's and Future Shock's rows (and a mod's table)
/// light alike:
/// <code>
///   a bolt whose model is LASER*        → a laser: LASER1 red, LASER2 plasma blue, LASER3 red-orange
///   class: blasts (the HK's, the jeep's) → Vehicle; |damage| ≥ 100 → Heavy; else Hand
///          a robot's shot at least <see cref="RobotClass"/> (the robots' laser cannons)
///   other bolts (rockets): none here (GameLights' old bolt light)
/// </code>
/// A mod overrides it with <c>lights/shots.json</c> (docs/modding.md):
/// <code>
///   { "classes": { "hand": { "radius": 500, "intensity": 0.7, "impactRadius": 450, "impactIntensity": 1.6, "impactLife": 0.12 }, ... },
///     "robotClass": "heavy",
///     "shots": [ { "ammo": 6, "model": "LASER1", "colour": [1, 0.3, 0.2], "class": "hand", "radius": 600, "intensity": 0.9,
///                  "impactRadius": 500, "impactIntensity": 2, "impactLife": 0.15 } ] }
/// </code>
/// Every member is optional; a later shot row wins over an earlier one and over the defaults, field by
/// field. A row may name a non-laser bolt (a rocket) to give it this light.
/// </summary>
public sealed class ShotLightTable
{
    /// <summary>The file in a mod.</summary>
    public const string FileName = "lights/shots.json";

    /// <summary>The colours (sRGB) of the laser models.</summary>
    public static readonly Vector3 Laser1Srgb = GameLights.LaserSrgb, Laser2Srgb = GameLights.PlasmaSrgb, Laser3Srgb = GameLights.RobotBoltSrgb;

    public static readonly IReadOnlyDictionary<ShotClass, ShotClassLight> DefaultClasses = new Dictionary<ShotClass, ShotClassLight>
    {
        [ShotClass.Hand] = new(500f, 0.7f, 450f, 1.6f, 0.12f),
        [ShotClass.Heavy] = new(850f, 1.5f, 750f, 2.6f, 0.18f),
        [ShotClass.Vehicle] = new(1300f, 2.6f, 1100f, 3.6f, 0.25f),
    };

    /// <summary>The robots' shots light at least as this class.</summary>
    public const ShotClass DefaultRobotClass = ShotClass.Heavy;

    /// <summary>|damage| from which a non-blasting laser is heavy.</summary>
    public const float HeavyDamage = 100f;

    public static ShotLightTable Default { get; } = new(DefaultClasses, DefaultRobotClass, []);

    public IReadOnlyDictionary<ShotClass, ShotClassLight> Classes { get; }
    public ShotClass RobotClass { get; }
    public IReadOnlyList<ShotLightEntry> Entries { get; }

    public ShotLightTable(IReadOnlyDictionary<ShotClass, ShotClassLight> classes, ShotClass robotClass, IReadOnlyList<ShotLightEntry> entries)
    {
        Classes = classes;
        RobotClass = robotClass;
        Entries = entries;
    }

    /// <summary>The default class of an ammo record (above).</summary>
    public static ShotClass ClassOf(AmmoRecord ammo) =>
        ammo.Blasts ? ShotClass.Vehicle : ammo.Strength >= HeavyDamage ? ShotClass.Heavy : ShotClass.Hand;

    /// <summary>The default colour (sRGB) of a laser model; null for a model that is not a laser.</summary>
    public static Vector3? LaserSrgb(string model) =>
        model.StartsWith("LASER1", StringComparison.OrdinalIgnoreCase) ? Laser1Srgb
        : model.StartsWith("LASER2", StringComparison.OrdinalIgnoreCase) ? Laser2Srgb
        : model.StartsWith("LASER", StringComparison.OrdinalIgnoreCase) ? Laser3Srgb
        : null;

    /// <summary>The light of a shot of <paramref name="ammo"/> (a robot's: <paramref name="robot"/>),
    /// or null when it has none here (not a bolt, not a laser and no row names it).</summary>
    public ShotLight? For(AmmoRecord ammo, bool robot = false)
    {
        if (ammo.Family != AmmoFamily.Bolt)
        {
            return null;
        }

        var srgb = LaserSrgb(ammo.Model);
        var cls = ClassOf(ammo);
        float? radius = null, intensity = null, impactRadius = null, impactIntensity = null, impactLife = null;
        var named = false;
        foreach (var e in Entries)
        {
            if (!e.Matches(ammo))
            {
                continue;
            }

            named = true;
            srgb = e.Srgb ?? srgb;
            cls = e.Class ?? cls;
            radius = e.Radius ?? radius;
            intensity = e.Intensity ?? intensity;
            impactRadius = e.ImpactRadius ?? impactRadius;
            impactIntensity = e.ImpactIntensity ?? impactIntensity;
            impactLife = e.ImpactLife ?? impactLife;
        }

        if (srgb is not { } colour)
        {
            if (!named)
            {
                return null;
            }

            colour = GameLights.RocketSrgb;
        }

        if (robot && cls < RobotClass)
        {
            cls = RobotClass;
        }

        var c = Classes.TryGetValue(cls, out var found) ? found : DefaultClasses[cls];
        return new ShotLight(LevelLights.Linear(colour), radius ?? c.Radius, LightFalloff.EnergyAtRef(intensity ?? c.Intensity),
            impactRadius ?? c.ImpactRadius, LightFalloff.EnergyAtRef(impactIntensity ?? c.ImpactIntensity), MathF.Max(impactLife ?? c.ImpactLife, 0f));
    }

    /// <summary>Reads a <c>lights/shots.json</c> over <paramref name="over"/> (default: the defaults).
    /// <see cref="JsonDataException"/> when it does not read.</summary>
    public static ShotLightTable Parse(ReadOnlySpan<byte> utf8, ShotLightTable? over = null)
    {
        over ??= Default;
        var root = JsonItem.Parse(utf8).AsObject();
        var classes = new Dictionary<ShotClass, ShotClassLight>(over.Classes);
        var robotClass = over.RobotClass;
        var entries = new List<ShotLightEntry>(over.Entries);
        foreach (var m in root.Members)
        {
            switch (m.Key)
            {
                case "classes":
                    foreach (var k in m.Value.Members)
                    {
                        var cls = ClassNamed(k.Value, k.Key);
                        var c = classes[cls];
                        var o = k.Value.AsObject();
                        classes[cls] = new ShotClassLight(
                            Float(o, "radius") ?? c.Radius, Float(o, "intensity") ?? c.Intensity, Float(o, "impactRadius") ?? c.ImpactRadius,
                            Float(o, "impactIntensity") ?? c.ImpactIntensity, Float(o, "impactLife") ?? c.ImpactLife);
                        Only(o, "radius", "intensity", "impactRadius", "impactIntensity", "impactLife");
                    }

                    break;
                case "robotClass":
                    robotClass = ClassNamed(m.Value, m.Value.AsString());
                    break;
                case "shots":
                    foreach (var item in m.Value.Items)
                    {
                        var o = item.AsObject();
                        Only(o, "ammo", "model", "colour", "class", "radius", "intensity", "impactRadius", "impactIntensity", "impactLife");
                        var ammo = o.Opt("ammo")?.AsInt();
                        var model = o.Opt("model")?.AsString();
                        if (ammo == null && string.IsNullOrEmpty(model))
                        {
                            throw item.Error("a shot names an \"ammo\" index or a \"model\"");
                        }

                        entries.Add(new ShotLightEntry(ammo, model, Colour(o, "colour"),
                            o.Opt("class") is { } cl ? ClassNamed(cl, cl.AsString()) : null,
                            Float(o, "radius"), Float(o, "intensity"), Float(o, "impactRadius"), Float(o, "impactIntensity"), Float(o, "impactLife")));
                    }

                    break;
                default:
                    throw m.Value.Error($"\"{m.Key}\" is not a shots member (classes, robotClass, shots)");
            }
        }

        return new ShotLightTable(classes, robotClass, entries);
    }

    private static ShotClass ClassNamed(JsonItem at, string name) => name.ToLowerInvariant() switch
    {
        "hand" => ShotClass.Hand,
        "heavy" => ShotClass.Heavy,
        "vehicle" => ShotClass.Vehicle,
        _ => throw at.Error($"\"{name}\" is not a class (hand, heavy, vehicle)"),
    };

    internal static float? Float(JsonItem o, string name)
    {
        if (o.Opt(name) is not { } v)
        {
            return null;
        }

        var f = (float)v.AsDouble();
        return float.IsFinite(f) && f >= 0f ? f : throw v.Error($"\"{name}\" must be a number 0 or more");
    }

    internal static Vector3? Colour(JsonItem o, string name)
    {
        if (o.Opt(name) is not { } v)
        {
            return null;
        }

        var items = v.Items;
        if (items.Count != 3)
        {
            throw v.Error($"\"{name}\" is [r, g, b], 0-1 each");
        }

        return new Vector3(Math.Clamp((float)items[0].AsDouble(), 0f, 4f), Math.Clamp((float)items[1].AsDouble(), 0f, 4f),
            Math.Clamp((float)items[2].AsDouble(), 0f, 4f));
    }

    internal static void Only(JsonItem o, params string[] names)
    {
        foreach (var m in o.Members)
        {
            if (Array.IndexOf(names, m.Key) < 0)
            {
                throw m.Value.Error(string.Create(CultureInfo.InvariantCulture, $"\"{m.Key}\" is not one of {string.Join(", ", names)}"));
            }
        }
    }

    // The mods read and what they gave, as one immutable reference: read from any thread (workers too).
    private sealed record Loaded(ShotLightTable Value, VirtualFileSystem? From);

    private static Loaded _box = new(Default, null);

    /// <summary>The table of the mounted mods (<see cref="ModContent.Files"/>: the highest mod with
    /// the file wins), else the defaults; a file that does not read is logged and the defaults kept.</summary>
    public static ShotLightTable Current
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

    private static ShotLightTable Load(VirtualFileSystem? vfs)
    {
        if (vfs?.TryReadAllBytes(FileName) is not { } bytes)
        {
            return Default;
        }

        try
        {
            return Parse(bytes);
        }
        catch (Exception e) when (e is JsonDataException or System.Text.Json.JsonException)
        {
            RetroEng.Diagnostics.Log.Warn("Mods", $"{FileName}: {e.Message}; the default shot lights stay");
            return Default;
        }
    }
}
