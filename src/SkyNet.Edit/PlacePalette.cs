using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using SkyNet.Formats;
using SkyNet.Game.Campaign;

namespace SkyNet.Edit;

/// <summary>The Place panel's palettes (editor plan step E7).</summary>
public enum PlaceKind
{
    Prop,
    Robot,
    Pickup,
    Marker,
    Light,
    Sprite,
}

/// <summary>One thing the Place panel puts down: the fields of the entity it adds (no position) and,
/// for a prop, nothing else: its hit points, state and link come from the map's per-name defaults
/// when the layer is applied, as DOS resolves them (<see cref="SkyNet.Game.Mods.MapLayer"/>). <paramref name="Name"/>:
/// a model the map's name slots lack, added to them with the entity.</summary>
public sealed record PlaceItem(PlaceKind Kind, string Label, ImmutableSortedDictionary<string, int> Fields, string? Name = null)
{
    /// <summary>The DOS Y above the floor its spawn sits (markers and sprites are lifted 0x10 by the game).</summary>
    public int Lift => Fields.TryGetValue("variant", out var v) ? v switch { 3 => 0x10, 2 => 64, _ => 0 } : 0;
}

/// <summary>
/// The palettes of a map (props by the map's model names, robots by the enemy type table, pickups by
/// the item table, markers with the arrival sets, lights) and the add / duplicate commands they give.
/// A sprite, marker or light copies the state, act and parameters of the map's first one of its kind
/// (its "defaults"); a robot marker is a bare start marker of its enemy type, as the DOS maps hold them.
/// </summary>
public static class PlacePalette
{
    public const int Marker = MapFile.MarkerBank << 7;
    /// <summary>How far a duplicate is put beside its original (DOS units, along X).</summary>
    public const int DuplicateOffset = 64;

    private static readonly (int Type, string Label)[] SpecialMarkers =
    [
        (0, "Player start (arrival set 0)"), (1, "Player start facing"), (3, "HK altitude ceiling"), (4, "Radiation source"),
        (6, "Map type / music"), (7, "Compass north"), (8, "Background radiation"),
        (30, "Border box 30"), (31, "Border box 31"), (32, "Border box 32"), (33, "Border box 33"),
        (100, "Deathmatch item spot"), (101, "Deathmatch jeep"), (102, "Deathmatch HK"),
        (103, "Water level"), (104, "Water level (second)"), (105, "Path loop"),
    ];

    private static ImmutableSortedDictionary<string, int> F(params (string Key, int Value)[] fields) =>
        fields.ToImmutableSortedDictionary(f => f.Key, f => f.Value, StringComparer.Ordinal);

    /// <summary>The palette of <paramref name="kind"/> for <paramref name="map"/>; <paramref name="deathmatch"/>
    /// (an arena): the markers are the deathmatch ones (<see cref="SkyNet.Game.Net.DmArena"/>).</summary>
    public static IReadOnlyList<PlaceItem> Items(PlaceKind kind, MapFile map, bool deathmatch = false) => kind switch
    {
        PlaceKind.Prop => Props(map),
        PlaceKind.Robot => Robots(),
        PlaceKind.Pickup => Pickups(map),
        PlaceKind.Marker => deathmatch ? DeathmatchMarkers(map) : Markers(map),
        PlaceKind.Sprite => Sprites(map),
        _ => Lights(map),
    };

    /// <summary>The item an Assets panel entry (<paramref name="kind"/>, <paramref name="id"/>) puts down on
    /// <paramref name="map"/>: a model ("FLENCER") as a prop, its name added when the map has none of it; a robot
    /// type ("037") as its start marker; a sprite record ("200/005") as a pickup or a sprite. Null: nothing to place.</summary>
    public static PlaceItem? FromAsset(string kind, string id, MapFile map) => kind switch
    {
        GameAssets.Models => Model(map, id),
        RobotAssets.Robots when int.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var type)
            => Robots().FirstOrDefault(r => r.Fields["enemy"] == type),
        GameAssets.Sprites when SpriteIndex(id) is { } sprite => Sprite(map, sprite),
        _ => null,
    };

    /// <summary>Model <paramref name="name"/> as a prop: under the map's name slot when it has one, else the name added.</summary>
    public static PlaceItem Model(MapFile map, string name)
    {
        var slot = map.Names.IndexOf(name, StringComparer.OrdinalIgnoreCase);
        return slot >= 0
            ? new PlaceItem(PlaceKind.Prop, map.Names[slot], F(("variant", 1), ("nameIndex", slot)))
            : new PlaceItem(PlaceKind.Prop, name + "  (new to the map)", F(("variant", 1)), name);
    }

    // "200/005": sprite index 200 << 7 | 5.
    private static int? SpriteIndex(string id) =>
        id.Split('/') is [var b, var r] && int.TryParse(b, NumberStyles.None, CultureInfo.InvariantCulture, out var bank)
            && int.TryParse(r, NumberStyles.None, CultureInfo.InvariantCulture, out var record) && record < 0x80
            ? bank << 7 | record : null;

    /// <summary>Sprite <paramref name="index"/> as a pickup when the item table has it, else as a plain sprite.</summary>
    public static PlaceItem Sprite(MapFile map, int index) =>
        PickupTable.Items.ContainsKey(index)
            ? Pickups(map).First(p => p.Fields["sprite"] == index)
            : new PlaceItem(PlaceKind.Sprite, string.Create(CultureInfo.InvariantCulture, $"sprite {index >> 7}/{index & 0x7F}"),
                Template(map, 3, e => !e.IsMarker && e.SpriteIndex == index, ("sprite", index)));

    // The sprites the map shows that are no pickup or marker.
    private static List<PlaceItem> Sprites(MapFile map) =>
        [.. map.Entities.Where(e => EditorWorld.KindOf(e) == EntityKind.Sprite && e.SpriteIndex >= 0).Select(e => e.SpriteIndex)
            .Distinct().Order().Select(i => Sprite(map, i))];

    private static List<PlaceItem> Props(MapFile map)
    {
        var used = map.Entities.Where(e => e.Variant == MapEntityVariant.Mesh && !e.IsActor && e.NameIndex >= 0).Select(e => e.NameIndex)
            .Concat(map.Defaults.Keys).Where(i => i >= 0 && i < map.Names.Length && map.Names[i].Length > 0).Distinct();
        return [.. used.Select(i => new PlaceItem(PlaceKind.Prop,
                map.Defaults.TryGetValue(i, out var d) && (d.HitPoints > 0 || d.Link?.ActType > 0)
                    ? string.Create(CultureInfo.InvariantCulture, $"{map.Names[i]}  (hp {d.HitPoints}, act {d.Link?.ActType ?? 0})")
                    : map.Names[i],
                F(("variant", 1), ("nameIndex", i))))
            .OrderBy(p => p.Label, StringComparer.Ordinal)];
    }

    private static List<PlaceItem> Robots() =>
        [.. SkyNet.Data.GameTables.Ais.Types.Where(t => t.Name.Length > 0 && t.Index <= SkyNet.Data.ModRobots.MaxType)
            .Select(t => new PlaceItem(PlaceKind.Robot,
                string.Create(CultureInfo.InvariantCulture, $"{t.Index,3} {t.Name}  (state {t.State}, hp {t.HitPoints}{(t.Fire != null ? ", armed" : "")})"),
                F(("variant", 3), ("sprite", Marker | 2), ("enemy", t.Index))))];

    /// <summary>The fields the map's first sprite of <paramref name="sprite"/> carries besides its place.</summary>
    private static ImmutableSortedDictionary<string, int> Template(MapFile map, int variant, Func<MapEntity, bool> match, params (string, int)[] fields)
    {
        var f = F(fields).Add("variant", variant);
        if (map.Entities.FirstOrDefault(e => (int)e.Variant == variant && match(e)) is { } t)
        {
            f = f.SetItem("state", t.StateByte);
            if (variant == 3)
            {
                f = f.SetItem("param16", t.Param16).SetItem("param8", t.Param8);
                if (!t.IsMarker)
                {
                    f = f.SetItem("act", t.ActType);
                }
            }
            else
            {
                f = f.SetItem("act", t.ActType).SetItem("light", t.LightIntensity).SetItem("lightEnable", t.LightEnable);
            }
        }

        return f;
    }

    private static List<PlaceItem> Pickups(MapFile map) =>
        [.. PickupTable.Items.OrderBy(i => i.Key).Select(i => new PlaceItem(PlaceKind.Pickup,
            string.Create(CultureInfo.InvariantCulture, $"{i.Value.MessageKey.Replace("Str_", "", StringComparison.Ordinal)}  ({i.Key >> 7}/{i.Key & 0x7F})"),
            Template(map, 3, e => e.SpriteIndex == i.Key, ("sprite", i.Key))))];

    /// <summary>An arena's markers: the spawn sets (0 and 10, 12 ... 28, each facing the nearest marker
    /// of the next type), the item spots (100) and the jeep / HK parking (101 / 102), then the rest.</summary>
    private static List<PlaceItem> DeathmatchMarkers(MapFile map)
    {
        var items = new List<(int Type, string Label)>();
        foreach (var set in SkyNet.Game.Net.DmArena.SpawnSets)
        {
            items.Add((set, string.Create(CultureInfo.InvariantCulture, $"Deathmatch spawn (set {set})")));
            items.Add((set + 1, string.Create(CultureInfo.InvariantCulture, $"Deathmatch spawn facing (set {set})")));
        }

        items.Add((SkyNet.Game.Net.DmArena.ItemMarker, "Deathmatch item spot"));
        items.Add((SkyNet.Game.Net.DmArena.JeepMarker, "Deathmatch jeep"));
        items.Add((SkyNet.Game.Net.DmArena.HkMarker, "Deathmatch HK"));
        items.AddRange(SpecialMarkers.Where(m => !items.Exists(i => i.Type == m.Type)));
        return MarkerItems(map, items);
    }

    private static List<PlaceItem> MarkerItems(MapFile map, List<(int Type, string Label)> items) =>
        [.. items.Select(m => new PlaceItem(PlaceKind.Marker, string.Create(CultureInfo.InvariantCulture, $"{m.Type,3} {m.Label}"),
            Template(map, 3, e => e.IsMarker && e.MarkerType == m.Type, ("sprite", Marker | m.Type))))];

    private static List<PlaceItem> Markers(MapFile map)
    {
        var items = SpecialMarkers.Select(m => (m.Type, m.Label)).ToList();
        // The arrival sets: position N, facing N + 1, for every set an exit sends the player to or a
        // marker holds, and the next free one.
        var sets = map.Entities.Where(e => e.Variant == MapEntityVariant.Sprite && !e.IsMarker && e.ActType == 0xF0).Select(e => e.Param8)
            .Concat(map.Entities.Where(e => e.IsMarker && e.MarkerType is >= 10 and < 30 or >= 40 and < 99).Select(e => e.MarkerType & ~1))
            .Where(n => n is >= 10 and < 99).ToHashSet();
        var free = Enumerable.Range(5, 45).Select(n => n * 2).FirstOrDefault(n => !sets.Contains(n) && n is < 30 or >= 40);
        if (free > 0)
        {
            sets.Add(free);
        }

        foreach (var n in sets.Order())
        {
            items.Add((n, string.Create(CultureInfo.InvariantCulture, $"Arrival set {n}{(n == free ? " (new)" : "")}")));
            items.Add((n + 1, string.Create(CultureInfo.InvariantCulture, $"Arrival set {n} facing")));
        }

        return MarkerItems(map, items);
    }

    private static List<PlaceItem> Lights(MapFile map)
    {
        var levels = map.Entities.Where(e => e.Variant == MapEntityVariant.Light).Select(e => e.LightIntensity).Distinct().Order().ToList();
        if (levels.Count == 0)
        {
            return [new PlaceItem(PlaceKind.Light, "Light 200", F(("variant", 2), ("light", 200), ("lightEnable", 1)))];
        }

        return [.. levels.Select(l => new PlaceItem(PlaceKind.Light, string.Create(CultureInfo.InvariantCulture, $"Light {l}"),
            Template(map, 2, e => e.LightIntensity == l, ("light", l))))];
    }

    /// <summary>The DOS position an item put on the world point <paramref name="feet"/> (a floor) takes.</summary>
    public static (int X, int Y, int Z) DosPosition(PlaceItem item, Vector3 feet) =>
        ((int)MathF.Round(feet.X), (int)MathF.Round(-feet.Y) - item.Lift, (int)MathF.Round(-feet.Z));

    /// <summary>Adds <paramref name="item"/> on the floor point <paramref name="feet"/> (one undo step): its key.</summary>
    public static int Place(EditDocument doc, string layer, PlaceItem item, Vector3 feet)
    {
        var (x, y, z) = DosPosition(item, feet);
        return doc.AddEntity(layer, item.Fields.SetItem("x", x).SetItem("y", y).SetItem("z", z), item.Name);
    }

    /// <summary>Entity <paramref name="key"/> added again <see cref="DuplicateOffset"/> beside it (one step): the new key.</summary>
    public static int Duplicate(EditDocument doc, string layer, int key)
    {
        if (!doc.Map(layer).TryGetEntityAt(key, out var e))
        {
            throw new SkyNet.Game.Mods.MapLayerException($"duplicate {key}: the map has no entity under that key");
        }

        var f = FieldsOf(e);
        f["x"] = e.X + DuplicateOffset;
        return doc.AddEntity(layer, f);
    }

    /// <summary>The fields that add an entity equal to <paramref name="e"/> (values from the map's
    /// per-name defaults are left out: the copy takes them as the original does).</summary>
    public static Dictionary<string, int> FieldsOf(MapEntity e)
    {
        var f = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["variant"] = (int)e.Variant, ["x"] = e.X, ["y"] = e.Y, ["z"] = e.Z,
        };
        switch (e.Variant)
        {
            case MapEntityVariant.Mesh:
                (f["actor"], f["pitch"], f["yaw"], f["roll"], f["nameIndex"]) = (e.IsActor ? 1 : 0, e.Pitch, e.Yaw, e.Roll, e.NameIndex);
                if (!e.UsesDefaults)
                {
                    (f["hitPoints"], f["state"]) = (e.HitPoints, e.StateByte);
                    if (e.Link is { } l)
                    {
                        (f["act"], f["next"], f["destroyType"], f["destroyParam"]) = (l.ActType, l.Next, l.DestroyType, l.DestroyParam);
                    }
                }

                break;
            case MapEntityVariant.Light:
                (f["light"], f["lightEnable"], f["state"], f["act"], f["next"]) = (e.LightIntensity, e.LightEnable, e.StateByte, e.ActType, e.LinkNext);
                break;
            default:
                (f["sprite"], f["state"], f["next"], f["param16"], f["param8"]) = (e.SpriteIndex, e.StateByte, e.LinkNext, e.Param16, e.Param8);
                if (e.IsMarker)
                {
                    (f["enemy"], f["convoy"]) = (Math.Max(e.EnemyType, 0), e.Convoy ? 1 : 0);
                }
                else
                {
                    f["act"] = e.ActType;
                }

                break;
        }

        return f;
    }
}
