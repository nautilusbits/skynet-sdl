using System.Globalization;
using SkyNet.Formats;

namespace SkyNet.Edit;

/// <summary>A world axis of the view (X east, Y up, Z south; DOS has y and z the other way).</summary>
public enum ArrangeAxis
{
    X,
    Y,
    Z,
}

/// <summary>Where Align puts the selection on an axis: its lowest, middle or highest value.</summary>
public enum AlignTo
{
    Min,
    Centre,
    Max,
}

/// <summary>
/// The selection arranged (Unreal's snap, align, distribute and End): each action sets the entities'
/// DOS x, y, z as one undo step, nothing when nothing changes. Values are taken on the world axis, so
/// "Align Y min" lines the entities up on the lowest of them.
/// </summary>
public static class EntityArrange
{
    private const string MoveLabel = "Move";

    /// <summary>How far above its feet an entity's DOS position sits (sprites and markers 0x10, lights 64).</summary>
    public static int Lift(int variant) => variant switch { 3 => 0x10, 2 => 64, _ => 0 };

    /// <summary>The merge key of a move of exactly <paramref name="keys"/> (one per gizmo drag).</summary>
    public static string MoveMerge(IEnumerable<int> keys) =>
        "move " + string.Join(',', keys.Select(k => k.ToString(CultureInfo.InvariantCulture)));

    /// <summary>Sets the DOS positions <paramref name="to"/> (key to x, y, z) as one step that later moves of
    /// the same keys merge into (until EndDrag); false when none changes.</summary>
    public static bool Move(EditDocument doc, string layer, IReadOnlyDictionary<int, (int X, int Y, int Z)> to, string label = MoveLabel)
    {
        var map = doc.Map(layer);
        var changed = to.Where(p => map.TryGetEntityAt(p.Key, out var e) && (e.X, e.Y, e.Z) != p.Value).ToList();
        if (changed.Count == 0)
        {
            return false;
        }

        doc.Group(label, () =>
        {
            foreach (var (key, (x, y, z)) in changed)
            {
                doc.SetFields(layer, key, new Dictionary<string, int> { ["x"] = x, ["y"] = y, ["z"] = z }, label: label);
            }
        }, merge: () => MoveMerge(to.Keys));
        return true;
    }

    /// <summary>X and Z of each entity to the nearest multiple of <paramref name="grid"/> (heights stay on their floors).</summary>
    public static bool SnapToGrid(EditDocument doc, string layer, IReadOnlyList<int> keys, int grid)
    {
        var to = Entities(doc, layer, keys).ToDictionary(e => e.FileOffset,
            e => (RetroEng.Editor.Snap.Grid(e.X, grid), e.Y, RetroEng.Editor.Snap.Grid(e.Z, grid)));
        return Arrange(doc, layer, to, "Snap to grid");
    }

    /// <summary>Every entity's <paramref name="axis"/> value to the selection's min, centre or max.</summary>
    public static bool Align(EditDocument doc, string layer, IReadOnlyList<int> keys, ArrangeAxis axis, AlignTo to)
    {
        var list = Entities(doc, layer, keys);
        var values = Aligned([.. list.Select(e => World(e, axis))], to);
        return Arrange(doc, layer, With(list, axis, values), $"Align {axis} {to}");
    }

    /// <summary>The entities spread evenly on <paramref name="axis"/> between its two outermost (3 or more).</summary>
    public static bool Distribute(EditDocument doc, string layer, IReadOnlyList<int> keys, ArrangeAxis axis)
    {
        var list = Entities(doc, layer, keys);
        var values = Distributed([.. list.Select(e => World(e, axis))]);
        return Arrange(doc, layer, With(list, axis, values), $"Distribute {axis}");
    }

    /// <summary>
    /// Each entity put down onto the floor under it (End): <paramref name="floor"/> gives the world height of
    /// the first surface under a world point (the entity itself left out; null: nothing), <paramref name="foot"/>
    /// how far its lowest point is below its position. The floor is looked for under its feet.
    /// </summary>
    public static bool Drop(EditDocument doc, string layer, IReadOnlyList<int> keys, Func<MapEntity, System.Numerics.Vector3, float?> floor,
        Func<MapEntity, float> foot)
    {
        var to = new Dictionary<int, (int X, int Y, int Z)>();
        foreach (var e in Entities(doc, layer, keys))
        {
            var depth = foot(e);
            var feet = new System.Numerics.Vector3(e.X, -e.Y - depth, -e.Z);
            if (floor(e, feet) is { } y)
            {
                to[e.FileOffset] = (e.X, -(int)MathF.Round(y + depth), e.Z);
            }
        }

        return Arrange(doc, layer, to, "Drop to floor");
    }

    /// <summary>Every value set to the list's min, centre (rounded) or max.</summary>
    public static int[] Aligned(IReadOnlyList<int> values, AlignTo to)
    {
        if (values.Count == 0)
        {
            return [];
        }

        var (min, max) = (values.Min(), values.Max());
        var v = to switch { AlignTo.Min => min, AlignTo.Max => max, _ => (int)Math.Round((min + (double)max) / 2, MidpointRounding.AwayFromZero) };
        return [.. values.Select(_ => v)];
    }

    /// <summary>The values spaced evenly (rounded) between their min and max, in their order on the axis
    /// (ties by list order): [0, 10, 90, 100] gives [0, 33, 67, 100]; fewer than 3 stay.</summary>
    public static int[] Distributed(IReadOnlyList<int> values)
    {
        var result = values.ToArray();
        if (values.Count < 3)
        {
            return result;
        }

        var order = Enumerable.Range(0, values.Count).OrderBy(i => values[i]).ThenBy(i => i).ToList();
        var (first, last) = (values[order[0]], values[order[^1]]);
        for (var n = 0; n < order.Count; n++)
        {
            result[order[n]] = (int)Math.Round(first + (last - first) * (double)n / (order.Count - 1), MidpointRounding.AwayFromZero);
        }

        return result;
    }

    private static List<MapEntity> Entities(EditDocument doc, string layer, IReadOnlyList<int> keys)
    {
        var map = doc.Map(layer);
        var list = new List<MapEntity>();
        foreach (var key in keys.Distinct())
        {
            if (map.TryGetEntityAt(key, out var e))
            {
                list.Add(e);
            }
        }

        return list;
    }

    // The world value of an axis: DOS y and z point the other way.
    private static int World(MapEntity e, ArrangeAxis axis) => axis switch { ArrangeAxis.X => e.X, ArrangeAxis.Y => -e.Y, _ => -e.Z };

    private static Dictionary<int, (int X, int Y, int Z)> With(List<MapEntity> list, ArrangeAxis axis, int[] values) =>
        list.Select((e, i) => (e, v: values[i])).ToDictionary(p => p.e.FileOffset, p => axis switch
        {
            ArrangeAxis.X => (p.v, p.e.Y, p.e.Z),
            ArrangeAxis.Y => (p.e.X, -p.v, p.e.Z),
            _ => (p.e.X, p.e.Y, -p.v),
        });

    // One step of its own (never merged into a drag before it).
    private static bool Arrange(EditDocument doc, string layer, Dictionary<int, (int X, int Y, int Z)> to, string label)
    {
        doc.EndDrag();
        var moved = Move(doc, layer, to, label);
        doc.EndDrag();
        return moved;
    }
}
