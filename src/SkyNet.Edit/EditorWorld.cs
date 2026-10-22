using System.Globalization;
using RetroEng.World;
using SkyNet.Formats;
using SkyNet.Game.Behaviour;
using SkyNet.Game.Campaign;

namespace SkyNet.Edit;

/// <summary>What the outliner groups a map's entities by.</summary>
public enum EntityKind { Prop, Actor, Light, Robot, Pickup, Sprite, Marker }

/// <summary>An outliner row: an instance of the mission (Entity -1) or one entity of an instance's
/// map, keyed by its MAP file offset (the key behaviour, saves and links use).</summary>
public readonly record struct OutlinerItem(string Instance, int Entity = -1)
{
    public bool IsInstance => Entity < 0;
}

/// <summary>The mission being edited, read-only for now: its world file (instances, links, start)
/// and each instance's map, parsed on first use, with the entities grouped by kind and the
/// properties panel's rows.</summary>
public sealed class EditorWorld
{
    private readonly Func<WorldInstance, MapFile?> _loadMap;
    private readonly Dictionary<string, (MapFile? Map, IReadOnlyDictionary<EntityKind, IReadOnlyList<MapEntity>> Kinds)> _maps = [];

    public EditorWorld(MissionFile mission, Func<int, MapFile?> loadMap)
        : this(mission, (WorldInstance i) => loadMap(MapOf(i)))
    {
    }

    /// <summary>The mission with each instance's map from <paramref name="loadMap"/> (an edited document's).</summary>
    public EditorWorld(MissionFile mission, Func<WorldInstance, MapFile?> loadMap)
    {
        Mission = mission;
        _loadMap = loadMap;
    }

    public MissionFile Mission { get; }
    public WorldDefinition World => Mission.World;

    public static readonly EntityKind[] Kinds = Enum.GetValues<EntityKind>();

    /// <summary>The map an instance shows (its layer's base map).</summary>
    public static int MapOf(WorldInstance instance) => MissionFile.LayerMap(instance);

    /// <summary>The instance's map, parsed once (null: it does not load).</summary>
    public MapFile? Map(string instance) => Entry(instance).Map;

    /// <summary>Forgets the parsed maps of the instances playing <paramref name="layer"/> (null: all): its edits changed.</summary>
    public void Invalidate(string? layer = null)
    {
        foreach (var id in _maps.Keys.Where(id => layer == null || World.Instance(id)?.Layer == layer).ToArray())
        {
            _maps.Remove(id);
        }
    }

    /// <summary>The instance's entities of one kind, in file order.</summary>
    public IReadOnlyList<MapEntity> Entities(string instance, EntityKind kind) =>
        Entry(instance).Kinds.TryGetValue(kind, out var list) ? list : [];

    private (MapFile? Map, IReadOnlyDictionary<EntityKind, IReadOnlyList<MapEntity>> Kinds) Entry(string instance)
    {
        if (_maps.TryGetValue(instance, out var entry))
        {
            return entry;
        }

        var map = World.Instance(instance) is { } i ? _loadMap(i) : null;
        var kinds = new Dictionary<EntityKind, IReadOnlyList<MapEntity>>();
        if (map != null)
        {
            foreach (var group in map.Entities.GroupBy(KindOf))
            {
                kinds[group.Key] = [.. group.OrderBy(e => e.FileOffset)];
            }
        }

        _maps[instance] = entry = (map, kinds);
        return entry;
    }

    /// <summary>Which outliner group an entity goes in.</summary>
    public static EntityKind KindOf(MapEntity e) => e.Variant switch
    {
        MapEntityVariant.Mesh => e.IsActor ? EntityKind.Actor : EntityKind.Prop,
        MapEntityVariant.Light => EntityKind.Light,
        MapEntityVariant.Sprite when e.IsMarker => e.MarkerType == 2 && e.EnemyType >= 0 ? EntityKind.Robot : EntityKind.Marker,
        MapEntityVariant.Sprite => e.MarkerType == -1 && PickupTable.Items.ContainsKey(e.SpriteIndex) ? EntityKind.Pickup : EntityKind.Sprite,
        _ => EntityKind.Marker,
    };

    /// <summary>An entity's row in the outliner: its kind's name for it and its key.</summary>
    public static string Label(MapFile map, MapEntity e) => KindOf(e) switch
    {
        EntityKind.Prop or EntityKind.Actor => Inv($"{map.EntityName(e)}  @{e.FileOffset}"),
        EntityKind.Light => Inv($"light {e.LightIntensity}  @{e.FileOffset}"),
        EntityKind.Robot => Inv($"robot type {e.EnemyType}{(e.Convoy ? " convoy" : "")}  @{e.FileOffset}"),
        EntityKind.Marker => Inv($"marker {e.MarkerType}  @{e.FileOffset}"),
        EntityKind.Pickup => Inv($"{PickupTable.Items[e.SpriteIndex].MessageKey}  @{e.FileOffset}"),
        _ => Inv($"sprite {e.SpriteBank}/{e.SpriteRecord}  @{e.FileOffset}"),
    };

    /// <summary>The properties panel's rows for an outliner item (read-only).</summary>
    public IReadOnlyList<(string Name, string Value)> Properties(OutlinerItem item)
    {
        var instance = World.Instance(item.Instance);
        if (instance == null)
        {
            return [("instance", item.Instance + " (not in the mission)")];
        }

        if (item.IsInstance)
        {
            List<(string, string)> rows =
            [
                ("instance", instance.Id),
                ("block", instance.Block),
                ("layer", instance.Layer),
                ("group", instance.Group ?? "-"),
                ("start", World.Start.Instance == instance.Id ? Inv($"yes (arrival {World.Start.Arrival})") : "no"),
            ];
            rows.AddRange(instance.Properties.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => ("property " + p.Key, p.Value)));
            rows.AddRange(World.Links.Where(l => l.From == instance.Id)
                .Select(l => ("link @" + l.Source.ToString(CultureInfo.InvariantCulture), Inv($"-> {l.To ?? "-"}{(l.Back ? " (back)" : "")}, arrival {l.Arrival}"))));
            if (Map(instance.Id) is { } map)
            {
                rows.Add(("entities", map.Entities.Length.ToString(CultureInfo.InvariantCulture)));
                rows.Add(("outdoor", map.IsOutdoor ? "yes" : "no"));
            }

            return rows;
        }

        if (Map(instance.Id) is not { } file || !file.TryGetEntityAt(item.Entity, out var e))
        {
            return [("entity", Inv($"@{item.Entity} (not in the map)"))];
        }

        var p = BehaviourModel.Position(e);
        List<(string, string)> list =
        [
            ("key", Inv($"{e.FileOffset} (0x{e.FileOffset:X})")),
            ("kind", KindOf(e).ToString()),
            ("variant", e.Variant.ToString()),
            ("cell", Inv($"{e.CellX}, {e.CellZ}")),
            ("DOS position", Inv($"{e.X}, {e.Y}, {e.Z}")),
            ("world position", Inv($"{p.X:0}, {p.Y:0}, {p.Z:0}")),
            ("flags", Inv($"0x{e.Flags:X2}")),
            ("state", e.StateByte.ToString(CultureInfo.InvariantCulture)),
            ("act", e.ActType.ToString(CultureInfo.InvariantCulture)),
            ("next", e.LinkNext.ToString(CultureInfo.InvariantCulture)),
        ];
        switch (e.Variant)
        {
            case MapEntityVariant.Mesh:
                list.Add(("name", file.EntityName(e)));
                list.Add(("actor", e.IsActor ? "yes" : "no"));
                list.Add(("angles (pitch, yaw, roll)", Inv($"{e.Pitch}, {e.Yaw}, {e.Roll}")));
                list.Add(("hit points", e.HitPoints.ToString(CultureInfo.InvariantCulture)));
                list.Add(("link", e.Link is { } l ? Inv($"@{l.Offset}: destroy {l.DestroyType}/{l.DestroyParam}, next {l.Next}, act {l.ActType}") : "-"));
                list.Add(("from defaults", e.UsesDefaults ? "yes" : "no"));
                break;
            case MapEntityVariant.Light:
                list.Add(("intensity", e.LightIntensity.ToString(CultureInfo.InvariantCulture)));
                list.Add(("enable", e.LightEnable.ToString(CultureInfo.InvariantCulture)));
                break;
            case MapEntityVariant.Sprite:
                list.Add(("sprite", Inv($"{e.SpriteIndex} (bank {e.SpriteBank}, record {e.SpriteRecord})")));
                if (e.IsMarker)
                {
                    list.Add(("marker type", e.MarkerType.ToString(CultureInfo.InvariantCulture)));
                    list.Add(("enemy type", e.EnemyType.ToString(CultureInfo.InvariantCulture)));
                    list.Add(("convoy", e.Convoy ? "yes" : "no"));
                }
                else if (PickupTable.Items.TryGetValue(e.SpriteIndex, out var pickup))
                {
                    list.Add(("pickup", pickup.ToString()));
                }

                list.Add(("param16", e.Param16.ToString(CultureInfo.InvariantCulture)));
                list.Add(("param8", e.Param8.ToString(CultureInfo.InvariantCulture)));
                break;
        }

        list.AddRange(World.Links.Where(l => l.From == instance.Id && l.Source == e.FileOffset)
            .Select(l => ("exit", Inv($"-> {l.To ?? "-"}{(l.Back ? " (back)" : "")}, arrival {l.Arrival}"))));
        return list;
    }

    private static string Inv(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
}
