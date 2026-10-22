using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using RetroEng.Diagnostics;
using SkyNet.Formats;

namespace SkyNet.Edit;

/// <summary>What a duplicate's step merges with: nothing, or the copies' moves after it (Alt+drag).</summary>
public enum DuplicateStep
{
    Own,
    Drag,
}

/// <summary>
/// Copied entities (Ctrl+C / X / V, Ctrl+D): each record's fields as <see cref="PlacePalette.FieldsOf"/> gives
/// them, its model by name and the values it takes from its map's per-name defaults. A paste adds them to any
/// layer (another map, another mission) as one undo step:
/// <code>
///   copied A ─next─► B ─next─► outside X
///   pasted A'─next─► B'          (X: kept on the same layer, else dropped, logged)
/// </code>
/// a name the target map lacks gets a slot of its own.
/// </summary>
public sealed class EntityClipboard
{
    /// <summary>One copied entity: its key there, its add fields, its model name (null: none), and the
    /// fields it took from the map's per-name defaults (written out when pasted onto another layer).</summary>
    public sealed record Item(int Key, ImmutableSortedDictionary<string, int> Fields, string? Name, ImmutableSortedDictionary<string, int> Own);

    private EntityClipboard(string layer, ImmutableArray<Item> items) => (Layer, Items) = (layer, items);

    /// <summary>The layer the entities were copied from.</summary>
    public string Layer { get; }

    /// <summary>The entities, the selection's primary first.</summary>
    public ImmutableArray<Item> Items { get; }

    /// <summary>The entities <paramref name="keys"/> of a layer as they are now; null: none of them is there.</summary>
    public static EntityClipboard? Copy(EditDocument doc, string layer, IEnumerable<int> keys)
    {
        var map = doc.Map(layer);
        var items = ImmutableArray.CreateBuilder<Item>();
        foreach (var key in keys.Distinct())
        {
            if (map.TryGetEntityAt(key, out var e))
            {
                items.Add(ItemOf(map, e));
            }
        }

        return items.Count > 0 ? new EntityClipboard(layer, items.ToImmutable()) : null;
    }

    /// <summary>Copies <paramref name="keys"/>, then deletes them (one step "Cut").</summary>
    public static EntityClipboard? Cut(EditDocument doc, string layer, IReadOnlyList<int> keys)
    {
        if (Copy(doc, layer, keys) is not { } clip)
        {
            return null;
        }

        doc.Group("Cut", () => clip.Items.ToList().ForEach(i => doc.DeleteEntity(layer, i.Key)));
        return clip;
    }

    /// <summary>Copies of <paramref name="keys"/> moved by <paramref name="move"/> (DOS units), one step
    /// "Duplicate"; <see cref="DuplicateStep.Drag"/>: <see cref="EntityArrange.Move"/> of the copies merges into it.</summary>
    public static IReadOnlyList<int> Duplicate(EditDocument doc, string layer, IReadOnlyList<int> keys, (int X, int Y, int Z) move,
        DuplicateStep step = DuplicateStep.Own)
    {
        if (Copy(doc, layer, keys) is not { } clip)
        {
            return [];
        }

        IReadOnlyList<int> copies = [];
        doc.Group("Duplicate", () => copies = clip.Paste(doc, layer, move),
            merge: step == DuplicateStep.Drag ? () => EntityArrange.MoveMerge(copies) : null);
        return copies;
    }

    private static Item ItemOf(MapFile map, MapEntity e)
    {
        var fields = PlacePalette.FieldsOf(e);
        string? name = null;
        if (e.Variant == MapEntityVariant.Mesh && e.NameIndex >= 0 && e.NameIndex < map.Names.Length && map.Names[e.NameIndex].Length > 0)
        {
            name = map.Names[e.NameIndex];
        }

        // A mesh on its map's per-name defaults: those values, for a map whose defaults differ.
        var own = ImmutableSortedDictionary.CreateBuilder<string, int>(StringComparer.Ordinal);
        if (e.Variant == MapEntityVariant.Mesh && e.UsesDefaults)
        {
            (own["hitPoints"], own["state"]) = (e.HitPoints, e.StateByte);
            if (e.Link is { } l)
            {
                (own["act"], own["next"], own["destroyType"], own["destroyParam"]) = (l.ActType, l.Next, l.DestroyType, l.DestroyParam);
            }
        }

        return new Item(e.FileOffset, fields.ToImmutableSortedDictionary(StringComparer.Ordinal), name, own.ToImmutable());
    }

    /// <summary>The point the copies are placed by (DOS units): the middle of their X and Z, the lowest feet.</summary>
    public (int X, int Y, int Z) Anchor
    {
        get
        {
            var xs = Items.Select(i => i.Fields["x"]).ToList();
            var zs = Items.Select(i => i.Fields["z"]).ToList();
            // DOS Y grows downward: the lowest feet are the largest y + lift.
            var feet = Items.Max(i => i.Fields["y"] + EntityArrange.Lift(i.Fields["variant"]));
            return ((xs.Min() + xs.Max()) / 2, feet, (zs.Min() + zs.Max()) / 2);
        }
    }

    /// <summary>The move that puts <see cref="Anchor"/> on the world point <paramref name="ground"/>.</summary>
    public (int X, int Y, int Z) MoveTo(Vector3 ground)
    {
        var a = Anchor;
        return ((int)MathF.Round(ground.X) - a.X, (int)MathF.Round(-ground.Y) - a.Y, (int)MathF.Round(-ground.Z) - a.Z);
    }

    /// <summary>
    /// The entities added to <paramref name="layer"/> moved by <paramref name="move"/> (one step "Paste"):
    /// their new keys, in <see cref="Items"/> order. Links between them point at the copies; a link outside
    /// them stays on the same layer when that entity is there, else it is dropped (logged).
    /// </summary>
    public IReadOnlyList<int> Paste(EditDocument doc, string layer, (int X, int Y, int Z) move)
    {
        var keys = new List<int>();
        doc.Group("Paste", () => PasteAll(doc, layer, move, keys));
        return keys;
    }

    private void PasteAll(EditDocument doc, string layer, (int X, int Y, int Z) move, List<int> keys)
    {
        var sameLayer = layer == Layer;
        var copied = Items.Select(i => i.Key).ToHashSet();

        // Pass 1: the adds; a link to a copied entity waits for its key.
        var links = new List<(int Key, int Next)>();
        foreach (var item in Items)
        {
            var f = new Dictionary<string, int>(item.Fields, StringComparer.Ordinal);
            // Another map's defaults differ; a default link to a copied entity is remapped, so written out too.
            if (!sameLayer || (item.Own.TryGetValue("next", out var own) && copied.Contains(own)))
            {
                foreach (var (k, v) in item.Own)
                {
                    f[k] = v;
                }
            }

            (f["x"], f["y"], f["z"]) = (f["x"] + move.X, f["y"] + move.Y, f["z"] + move.Z);
            var name = NameFor(doc.Map(layer), item, f);
            var next = f.GetValueOrDefault("next");
            if (next > 0 && copied.Contains(next))
            {
                f["next"] = 0;
            }
            else if (next > 0 && !(sameLayer && doc.Map(layer).TryGetEntityAt(next, out _)))
            {
                f["next"] = 0;
                Log.Info("Edit", string.Create(CultureInfo.InvariantCulture, $"Paste: @{item.Key}'s link to @{next} (not copied) dropped"));
            }

            var key = doc.AddEntity(layer, f, name);
            keys.Add(key);
            if (next > 0 && copied.Contains(next))
            {
                links.Add((key, next));
            }
        }

        // Pass 2: the links between copies remapped (A -> B becomes A' -> B').
        var newKey = Items.Select((item, i) => (item.Key, i)).ToDictionary(p => p.Key, p => keys[p.i]);
        foreach (var (key, next) in links)
        {
            doc.SetFields(layer, key, new Dictionary<string, int> { ["next"] = newKey[next] }, label: "Paste");
        }
    }

    // The model's name slot in the target map: the same index when it names the same, another slot that
    // does, else none (null nameIndex; the name returned adds a slot).
    private static string? NameFor(MapFile map, Item item, Dictionary<string, int> f)
    {
        if (item.Name is not { } name)
        {
            return null;
        }

        var index = f.TryGetValue("nameIndex", out var i) && i >= 0 && i < map.Names.Length && map.Names[i] == name ? i : map.Names.IndexOf(name);
        if (index >= 0)
        {
            f["nameIndex"] = index;
            return null;
        }

        f.Remove("nameIndex");
        Log.Info("Edit", $"Paste: name slot {name} added");
        return name;
    }
}
