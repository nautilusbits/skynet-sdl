using System.Collections.Immutable;
using System.Text.Json;
using RetroEng.Content;
using RetroEng.Diagnostics;
using RetroEng.Json;
using SkyNet.Data;
using SkyNet.Formats;

namespace SkyNet.Game.Mods;

/// <summary>What a map layer's edit does to an entity.</summary>
public enum MapEditOp
{
    /// <summary>Changes fields of an entity the base map (or an earlier add) has.</summary>
    Set,
    /// <summary>A new entity under a key at or above <see cref="MapFile.FirstAddedKey"/>.</summary>
    Add,
    /// <summary>Removes an entity.</summary>
    Delete,
}

/// <summary>One edit of a map layer: the entity's key (its MAP file offset, or an added key), the
/// integer fields it sets (JSON names, <see cref="MapLayer.FieldNames"/>) and a name it gives.</summary>
public sealed record MapEdit(MapEditOp Op, int Key, ImmutableSortedDictionary<string, int> Fields, string? Name = null)
{
    public static MapEdit Delete(int key) => new(MapEditOp.Delete, key, ImmutableSortedDictionary<string, int>.Empty);
}

/// <summary>A map layer file that does not read or does not fit its map.</summary>
public sealed class MapLayerException(string message) : Exception(message);

/// <summary>
/// A map layer, <c>maps/MAP.NNN.layer.json</c> (or <c>maps/&lt;name&gt;.layer.json</c>, a second
/// edited copy of one map): a base MAP and the edits on it, applied to the parsed <see cref="MapFile"/>
/// (<see cref="Apply"/>), so behaviour, baking, collision and robots read an ordinary map
/// (docs/modding.md, "Map layers").
/// </summary>
public sealed class MapLayer
{
    public const string FormatName = "skynet-layer";
    public const int Version = 1;

    /// <summary>The integer fields an edit may set, as the JSON names them.</summary>
    public static readonly ImmutableArray<string> FieldNames =
    [
        "x", "y", "z", "pitch", "yaw", "roll", "variant", "flags", "actor", "nameIndex", "hitPoints",
        "state", "act", "next", "destroyType", "destroyParam", "light", "lightEnable", "sprite", "enemy",
        "convoy", "param16", "param8", "cellX", "cellZ",
    ];

    private static readonly ImmutableHashSet<string> Bools = ["actor", "convoy"];

    /// <summary>A DOS marker's robot type: the low 7 bits of its sub-record byte 10.</summary>
    private const int DosEnemyMask = 0x7F;

    public MapLayer(string baseMap, IEnumerable<MapEdit> edits, IEnumerable<string>? names = null)
    {
        Base = baseMap;
        Edits = [.. edits];
        Names = [.. names ?? []];
    }

    /// <summary>The base map, "MAP.NNN".</summary>
    public string Base { get; }

    /// <summary>The edits in the order they apply.</summary>
    public ImmutableArray<MapEdit> Edits { get; }

    /// <summary>Name slots added after the base map's own (an index of base count + i).</summary>
    public ImmutableArray<string> Names { get; }

    /// <summary>True when the layer changes nothing: the map plays as its DOS file and its baked block stays.</summary>
    public bool IsEmpty => Edits.IsEmpty && Names.IsEmpty;

    /// <summary>The layer file's path in a mod: <c>maps/&lt;layer&gt;.layer.json</c>.</summary>
    public static string PathOf(string layer) => $"maps/{layer}.layer.json";

    /// <summary>True when <paramref name="layer"/> can name a layer file (a plain file name).</summary>
    public static bool IsLayerName(string layer) =>
        layer.Length is > 0 and <= 64 && layer.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-') && !layer.Contains("..", StringComparison.Ordinal);

    /// <summary>Reads a layer file; <see cref="MapLayerException"/> when it is not a valid one.</summary>
    public static MapLayer Parse(ReadOnlySpan<byte> utf8)
    {
        try
        {
            var root = JsonItem.Parse(utf8).AsObject();
            if (root.Member("format").AsString() != FormatName)
            {
                throw root.Member("format").Error($"expected \"{FormatName}\"");
            }

            if (root.Member("version").AsInt() != Version)
            {
                throw root.Member("version").Error($"version {Version} is the one this build reads");
            }

            var baseMap = root.Member("base").AsString();
            if (MapOf(baseMap) < 0)
            {
                throw root.Member("base").Error("expected MAP.NNN");
            }

            var edits = root.Opt("edits")?.ToArray(ReadEdit) ?? [];
            var names = root.Opt("names")?.ToArray(n => n.AsString()) ?? [];
            foreach (var m in root.Members)
            {
                if (m.Key is not ("format" or "version" or "base" or "edits" or "names"))
                {
                    throw m.Value.Error($"\"{m.Key}\" is not a layer member");
                }
            }

            return new MapLayer(baseMap, edits, names);
        }
        catch (JsonDataException e)
        {
            throw new MapLayerException(e.Message);
        }
    }

    private static MapEdit ReadEdit(JsonItem item)
    {
        var o = item.AsObject();
        var op = o.Member("op").AsString() switch
        {
            "set" => MapEditOp.Set,
            "add" => MapEditOp.Add,
            "delete" => MapEditOp.Delete,
            _ => throw o.Member("op").Error("expected set, add or delete"),
        };
        var key = o.Member("key").AsInt(1, int.MaxValue);
        var fields = ImmutableSortedDictionary.CreateBuilder<string, int>(StringComparer.Ordinal);
        string? name = null;
        foreach (var (k, v) in o.Members)
        {
            if (k is "op" or "key")
            {
                continue;
            }

            if (op == MapEditOp.Delete)
            {
                throw v.Error("a delete takes only op and key");
            }

            if (k == "name")
            {
                name = v.AsString();
            }
            else if (!FieldNames.Contains(k))
            {
                throw v.Error($"\"{k}\" is not an entity field");
            }
            else
            {
                fields[k] = Bools.Contains(k) && v.Kind is JsonKind.True or JsonKind.False ? (v.AsBool() ? 1 : 0) : v.AsInt();
            }
        }

        if (op == MapEditOp.Add && !fields.ContainsKey("variant") && !fields.ContainsKey("flags"))
        {
            throw o.Error("an add needs a variant (1 mesh, 2 light, 3 sprite)");
        }

        return new MapEdit(op, key, fields.ToImmutable(), name);
    }

    /// <summary>The layer as its file (indented JSON).</summary>
    public byte[] ToBytes()
    {
        var root = JsonItem.NewObject()
            .Add("format", FormatName)
            .Add("version", Version)
            .Add("base", Base)
            .Add("edits", JsonItem.NewArray(Edits, e =>
            {
                var o = JsonItem.NewObject()
                    .Add("op", e.Op.ToString().ToLowerInvariant())
                    .Add("key", e.Key);
                foreach (var f in FieldNames.Where(e.Fields.ContainsKey))
                {
                    o.Add(f, Bools.Contains(f) ? JsonItem.Bool(e.Fields[f] != 0) : e.Fields[f]);
                }

                if (e.Name != null)
                {
                    o.Add("name", e.Name);
                }

                return o;
            }))
            .Add("names", JsonItem.NewArray(Names, n => n));
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            root.WriteTo(w);
        }

        stream.WriteByte((byte)'\n');
        return stream.ToArray();
    }

    /// <summary><paramref name="map"/> (MAP.<paramref name="number"/>) with this layer's edits;
    /// the same object when the layer <see cref="IsEmpty"/>. <see cref="MapLayerException"/> when
    /// the base is another map, a key is missing or taken, or a field is out of range.</summary>
    public MapFile Apply(int number, MapFile map)
    {
        if (MapOf(Base) != number)
        {
            throw new MapLayerException($"the layer's base is {Base}, not MAP.{number:D3}");
        }

        if (IsEmpty)
        {
            return map;
        }

        var names = map.Names.ToList();
        names.AddRange(Names);
        var order = map.Entities.Select(e => e.FileOffset).ToList();
        var byKey = map.Entities.ToDictionary(e => e.FileOffset);
        foreach (var edit in Edits)
        {
            var at = $"{edit.Op.ToString().ToLowerInvariant()} {edit.Key}";
            switch (edit.Op)
            {
                case MapEditOp.Delete:
                    if (!byKey.Remove(edit.Key))
                    {
                        throw new MapLayerException($"{at}: the map has no entity under that key");
                    }

                    order.Remove(edit.Key);
                    break;
                case MapEditOp.Set:
                    if (!byKey.TryGetValue(edit.Key, out var old))
                    {
                        throw new MapLayerException($"{at}: the map has no entity under that key");
                    }

                    byKey[edit.Key] = Edited(old, edit, names, at);
                    break;
                case MapEditOp.Add:
                    if (edit.Key < MapFile.FirstAddedKey)
                    {
                        throw new MapLayerException($"{at}: an added entity's key is {MapFile.FirstAddedKey} or more");
                    }

                    if (byKey.ContainsKey(edit.Key))
                    {
                        throw new MapLayerException($"{at}: the key is taken");
                    }

                    var fresh = new MapEntity { FileOffset = edit.Key, Flags = 0 };
                    byKey[edit.Key] = WithDefaults(map, Edited(fresh, edit, names, at), edit);
                    order.Add(edit.Key);
                    break;
            }
        }

        return map.With(order.Select(k => byKey[k]), names);
    }

    /// <summary>An added mesh resolved as DOS loads any entity block: one that is not an actor and sets no
    /// hit points, state or link record of its own takes them from the map's per-name defaults (0x233C),
    /// so a placed crate or car has the hit points, drops and action of the map's own.</summary>
    private static MapEntity WithDefaults(MapFile map, MapEntity e, MapEdit edit)
    {
        string[] own = ["hitPoints", "state", "act", "next", "destroyType", "destroyParam"];
        if (e.Variant != MapEntityVariant.Mesh || e.IsActor || own.Any(edit.Fields.ContainsKey)
            || !map.Defaults.TryGetValue(e.NameIndex, out var d))
        {
            return e;
        }

        return new MapEntity(e)
        {
            HitPoints = d.HitPoints,
            StateByte = d.StateByte,
            LinkOffset = d.LinkOffset,
            Link = d.Link,
            ActType = d.Link?.ActType ?? e.ActType,
            LinkNext = d.Link?.Next ?? e.LinkNext,
            UsesDefaults = true,
        };
    }

    private static MapEntity Edited(MapEntity e, MapEdit edit, List<string> names, string at)
    {
        var f = edit.Fields;
        int? Get(string k) => f.TryGetValue(k, out var v) ? v : null;
        int Byte(string k, int now) => Get(k) is { } v ? v is >= 0 and <= 255 ? v : throw new MapLayerException($"{at}: {k} {v} is not 0-255") : now;
        int U16(string k, int now) => Get(k) is { } v ? v is >= 0 and <= 0xFFFF ? v : throw new MapLayerException($"{at}: {k} {v} is not 0-65535") : now;

        var flags = Byte("flags", e.Flags);
        if (Get("variant") is { } variant)
        {
            flags = variant is >= 1 and <= 3 ? (flags & ~3) | variant : throw new MapLayerException($"{at}: variant {variant} is not 1-3");
        }

        if (Get("actor") is { } actor)
        {
            flags = actor != 0 ? flags | 0x40 : flags & ~0x40;
        }

        if ((flags & 0x08) != 0)
        {
            throw new MapLayerException($"{at}: flag 0x08 (a block DOS skips) is not an entity; delete it instead");
        }

        var x = Get("x") ?? e.X;
        var z = Get("z") ?? e.Z;
        var nameIndex = Get("nameIndex") ?? e.NameIndex;
        if (edit.Name != null)
        {
            nameIndex = names.IndexOf(edit.Name);
            if (nameIndex < 0)
            {
                names.Add(edit.Name);
                nameIndex = names.Count - 1;
            }
        }

        var sprite = Get("sprite") is { } s ? U16("sprite", s) : e.SpriteIndex;
        var marker = sprite >= 0 && sprite >> 7 == MapFile.MarkerBank;
        var enemy = Get("enemy") ?? e.EnemyType;
        if (enemy > ModRobots.MaxType)
        {
            throw new MapLayerException($"{at}: enemy {enemy} is not 0-{ModRobots.MaxType}");
        }

        // A mod robot's type (above DOS's 7-bit byte) is kept whole; the byte keeps what it had.
        var modRobot = enemy > DosEnemyMask;
        var convoy = Get("convoy") is { } c ? c != 0 : e.Convoy;
        var state = Byte("state", e.StateByte);
        var next = Get("next") ?? e.LinkNext;
        // A marker's sub-record byte 10 is both its robot type (and convoy bit) and its act.
        var act = Get("act") is { } a ? Byte("act", a)
            : marker && !modRobot && (Get("enemy") != null || Get("convoy") != null) ? (enemy & DosEnemyMask) | (convoy ? 0x80 : 0)
            : e.ActType;
        MapLinkRecord? link = e.Link;
        if ((flags & 3) == 1 && (Get("act") != null || Get("next") != null || Get("destroyType") != null || Get("destroyParam") != null))
        {
            var r = link ?? new MapLinkRecord(0, 0, 0, 0, 0, 0);
            link = r with
            {
                ActType = act,
                Next = next,
                DestroyType = Byte("destroyType", r.DestroyType),
                DestroyParam = Get("destroyParam") is { } dp ? dp is >= short.MinValue and <= short.MaxValue ? dp
                    : throw new MapLayerException($"{at}: destroyParam {dp} is not a 16-bit value") : r.DestroyParam,
            };
        }

        return new MapEntity(e)
        {
            Flags = (byte)flags,
            X = x,
            Y = Get("y") ?? e.Y,
            Z = z,
            CellX = Get("cellX") ?? (Get("x") != null || edit.Op == MapEditOp.Add ? CellOf(x) : e.CellX),
            CellZ = Get("cellZ") ?? (Get("z") != null || edit.Op == MapEditOp.Add ? CellOf(z) : e.CellZ),
            Pitch = Get("pitch") ?? e.Pitch,
            Yaw = Get("yaw") ?? e.Yaw,
            Roll = Get("roll") ?? e.Roll,
            NameIndex = nameIndex,
            HitPoints = U16("hitPoints", e.HitPoints),
            UsesDefaults = e.UsesDefaults && Get("hitPoints") == null && Get("state") == null && link == e.Link,
            Link = link,
            LinkOffset = link?.Offset ?? e.LinkOffset,
            LightIntensity = Get("light") is { } l ? U16("light", l) : e.LightIntensity,
            LightEnable = U16("lightEnable", e.LightEnable),
            SpriteIndex = sprite,
            MarkerType = marker ? sprite & 0x7F : -1,
            EnemyType = marker ? (modRobot ? enemy : enemy & DosEnemyMask) : -1,
            Convoy = marker && convoy,
            Param16 = U16("param16", e.Param16),
            Param8 = Byte("param8", e.Param8),
            StateByte = state,
            LinkNext = next,
            ActType = act,
        };
    }

    private static int CellOf(int dos) => (int)Math.Floor(dos / (double)Baking.MapBaker.CellSize);

    private static int MapOf(string name) => Campaign.MissionFile.MapOf(name);
}

/// <summary>
/// The map layers the mods hold, behind <see cref="GameData.LoadMap"/> (<see cref="GameData.LayerResolver"/>,
/// set when the mods mount): the highest mod that has <c>maps/&lt;layer&gt;.layer.json</c> wins.
/// </summary>
public static class MapLayers
{
    /// <summary>The layer <paramref name="layer"/> in <paramref name="vfs"/>; null when no mod has
    /// it. <see cref="MapLayerException"/> when its file does not read.</summary>
    public static MapLayer? Find(VirtualFileSystem? vfs, string layer) =>
        vfs != null && MapLayer.IsLayerName(layer) && vfs.TryReadAllBytes(MapLayer.PathOf(layer)) is { } b ? Parse(layer, b) : null;

    /// <summary>The layer file's bytes when it holds edits (what a block baked with it records); null otherwise.</summary>
    public static byte[]? EditBytes(VirtualFileSystem? vfs, string layer) =>
        vfs != null && MapLayer.IsLayerName(layer) && vfs.TryReadAllBytes(MapLayer.PathOf(layer)) is { } b
        && !Parse(layer, b).IsEmpty ? b : null;

    private static MapLayer Parse(string layer, byte[] bytes)
    {
        try
        {
            return MapLayer.Parse(bytes);
        }
        catch (MapLayerException e)
        {
            throw new MapLayerException($"{MapLayer.PathOf(layer)}: {e.Message}");
        }
    }

    /// <summary>MAP.<paramref name="number"/> played as <paramref name="layer"/>: the mods' layer
    /// applied, or the map itself when there is none or it does not fit (logged under [Mods]).</summary>
    public static MapFile Resolve(VirtualFileSystem? vfs, int number, string layer, MapFile map)
    {
        try
        {
            return Find(vfs, layer) is { } l ? l.Apply(number, map) : map;
        }
        catch (MapLayerException e)
        {
            Log.Warn("Mods", $"{MapLayer.PathOf(layer)}: {e.Message}; MAP.{number:D3} plays as its DOS file");
            return map;
        }
    }

    /// <summary>The map a layer name stands on: NNN for "MAP.NNN", else the base of the mods'
    /// <c>maps/&lt;name&gt;.layer.json</c>; −1 when it is neither.</summary>
    public static int BaseOf(string layer, VirtualFileSystem? vfs = null)
    {
        if (Campaign.MissionFile.MapOf(layer) is var n and >= 0)
        {
            return n;
        }

        try
        {
            return Find(vfs ?? ModContent.Files, layer) is { } l ? Campaign.MissionFile.MapOf(l.Base) : -1;
        }
        catch (MapLayerException)
        {
            return -1;
        }
    }

    /// <summary>Installs the resolver for <paramref name="vfs"/> (none when no mod has a layer).</summary>
    internal static void Install(VirtualFileSystem? vfs)
    {
        var any = vfs?.Enumerate("maps/").Any(p => p.EndsWith(".layer.json", StringComparison.OrdinalIgnoreCase)) == true;
        GameData.LayerResolver = any ? (n, layer, m) => Resolve(vfs, n, layer, m) : null;
    }
}
