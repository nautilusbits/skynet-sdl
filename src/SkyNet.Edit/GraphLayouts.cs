using System.Collections.Immutable;
using System.Numerics;
using System.Text.Json;
using RetroEng.Content;
using RetroEng.Diagnostics;
using RetroEng.Editor;
using RetroEng.Json;
using SkyNet.Game.Mods;

namespace SkyNet.Edit;

/// <summary>Which layouts a save writes: the changed ones, or every non-empty one (a save into another mod).</summary>
public enum LayoutSave { Changed, NonEmpty }

/// <summary>
/// The trigger graphs' node positions per layer: an editor-only file of the save mod,
/// <c>editor/&lt;layer&gt;.graph.json</c> (the game does not read it), written with the layers. A node with
/// no position stands where <see cref="Auto"/> puts it. Moves and resets are undo steps of the document;
/// a drag's moves merge into one until <see cref="EditDocument.EndDrag"/>.
/// </summary>
public sealed class GraphLayouts(UndoStack history)
{
    public const string FormatName = "skynet-graph-layout";
    public const int Version = 1;

    /// <summary>Auto layout: a column per chain step; in a column, nodes stacked by their height plus a gap (canvas units).</summary>
    public const float ColumnWidth = 280f, RowGap = 24f;

    private sealed class Entry(ImmutableDictionary<int, Vector2> positions)
    {
        public ImmutableDictionary<int, Vector2> Now { get; set; } = positions;
        public ImmutableDictionary<int, Vector2> Saved { get; set; } = positions;
    }

    private static readonly ImmutableDictionary<int, Vector2> None = ImmutableDictionary<int, Vector2>.Empty;
    private readonly Dictionary<string, Entry> _layers = new(StringComparer.Ordinal);

    /// <summary>Raised with the layer's name when its positions change (a move, a reset, an undo).</summary>
    public event Action<string>? Changed;

    /// <summary>The file's path in a mod.</summary>
    public static string PathOf(string layer) => $"editor/{layer}.graph.json";

    /// <summary>The positions stored for <paramref name="layer"/> (empty: all automatic).</summary>
    public IReadOnlyDictionary<int, Vector2> Stored(string layer) => Get(layer).Now;

    /// <summary>Every node of <paramref name="graph"/> placed: its stored position, else the automatic one.</summary>
    public IReadOnlyDictionary<int, Vector2> Resolve(TriggerGraph graph)
    {
        var stored = Get(graph.Layer).Now;
        var auto = Auto(graph);
        return graph.Nodes.ToDictionary(n => n.Id, n => stored.TryGetValue(n.Id, out var p) ? p : auto[n.Id]);
    }

    /// <summary>Moves nodes to new positions, one undo step (merged while a drag goes on).</summary>
    public void Move(string layer, IReadOnlyDictionary<int, Vector2> moves)
    {
        var before = Get(layer).Now;
        var after = before.SetItems(moves);
        var merge = "move " + layer + " " + string.Join(',', moves.Keys.Order());
        history.Execute(new LayoutCommand(this, layer, "Move nodes", before, after, merge));
    }

    /// <summary>Places nodes now, outside the history: the done edit, for the step that added them
    /// (<see cref="EditDocument.GroupWith"/>).</summary>
    public IUndoCommand Place(string layer, IReadOnlyDictionary<int, Vector2> moves)
    {
        var before = Get(layer).Now;
        var place = new LayoutCommand(this, layer, "Place nodes", before, before.SetItems(moves), null);
        place.Do();
        return place;
    }

    /// <summary>Forgets <paramref name="layer"/>'s positions (auto layout again), one undo step.</summary>
    public void Reset(string layer)
    {
        var before = Get(layer).Now;
        if (before.IsEmpty)
        {
            return;
        }

        history.Execute(new LayoutCommand(this, layer, "Auto layout", before, None, null));
    }

    /// <summary>
    /// The automatic layout, layered left to right by chain order: a node's column is one past the
    /// furthest node leading into it (a loop's back link ignored), so a chain reads as a row
    /// (gate → door → sound → exit); each connected group of nodes is a band under the last.
    /// </summary>
    public static Dictionary<int, Vector2> Auto(TriggerGraph graph)
    {
        var column = Columns(graph);
        var result = new Dictionary<int, Vector2>();
        var top = 0f;
        foreach (var group in Groups(graph))
        {
            // Each column's next free y: a node goes there, the next one a gap under its bottom.
            var free = new Dictionary<int, float>();
            foreach (var id in group.OrderBy(id => column[id]).ThenBy(id => id))
            {
                var y = free.GetValueOrDefault(column[id]);
                free[column[id]] = y + TriggerGraphView.Height(graph.Node(id)!) + RowGap;
                result[id] = new Vector2(column[id] * ColumnWidth, top + y);
            }

            top += free.Values.Max();
        }

        return result;
    }

    // The longest path to each node from a chain's start. The links that close a loop (found by walking
    // from the starts, then from any node left) are dropped first, so the rest is acyclic.
    private static Dictionary<int, int> Columns(TriggerGraph graph)
    {
        var back = new HashSet<int>();
        var seen = new HashSet<int>();
        var order = graph.Nodes.Where(n => n.In.Count == 0).Concat(graph.Nodes).Select(n => n.Id);
        foreach (var start in order)
        {
            var path = new HashSet<int>();
            for (var id = start; seen.Add(id) && path.Add(id);)
            {
                var next = graph.Node(id)!.Next;
                if (graph.Node(next) == null)
                {
                    break;
                }

                if (path.Contains(next))
                {
                    back.Add(id);
                    break;
                }

                id = next;
            }
        }

        // Kahn's order over the remaining links: a node's column is one past its furthest predecessor.
        var column = graph.Nodes.ToDictionary(n => n.Id, _ => 0);
        var waiting = graph.Nodes.ToDictionary(n => n.Id, n => n.In.Count(i => graph.Node(i) != null && !back.Contains(i)));
        var ready = new Queue<int>(waiting.Where(w => w.Value == 0).Select(w => w.Key).Order());
        while (ready.TryDequeue(out var id))
        {
            var next = graph.Node(id)!.Next;
            if (back.Contains(id) || graph.Node(next) == null)
            {
                continue;
            }

            column[next] = Math.Max(column[next], column[id] + 1);
            if (--waiting[next] == 0)
            {
                ready.Enqueue(next);
            }
        }

        return column;
    }

    // The connected groups (links either way), each in key order of its lowest node.
    private static List<List<int>> Groups(TriggerGraph graph)
    {
        var seen = new HashSet<int>();
        var groups = new List<List<int>>();
        foreach (var n in graph.Nodes)
        {
            if (!seen.Add(n.Id))
            {
                continue;
            }

            var group = new List<int>();
            var stack = new Stack<int>([n.Id]);
            while (stack.TryPop(out var id))
            {
                group.Add(id);
                var node = graph.Node(id)!;
                foreach (var other in node.In.Append(node.Next).Where(o => graph.Node(o) != null && seen.Add(o)))
                {
                    stack.Push(other);
                }
            }

            groups.Add(group);
        }

        return groups;
    }

    /// <summary>Reads <paramref name="layer"/>'s file from the mods (none, or one that does not read: empty, logged).</summary>
    public void Load(VirtualFileSystem? vfs, string layer)
    {
        if (vfs == null || !MapLayer.IsLayerName(layer) || vfs.TryReadAllBytes(PathOf(layer)) is not { } bytes)
        {
            return;
        }

        try
        {
            _layers[layer] = new Entry(Parse(bytes));
        }
        catch (JsonDataException e)
        {
            Log.Warn("Edit", $"{PathOf(layer)}: {e.Message}; laying the graph out afresh");
        }
    }

    /// <summary>Writes the layouts into mod folder <paramref name="modFolder"/>; the paths written.</summary>
    public IReadOnlyList<string> Save(string modFolder, LayoutSave which)
    {
        var written = new List<string>();
        foreach (var (layer, e) in _layers)
        {
            var changed = !ReferenceEquals(e.Now, e.Saved);
            if (!(changed || (which == LayoutSave.NonEmpty && !e.Now.IsEmpty)))
            {
                continue;
            }

            var path = Path.Combine(modFolder, PathOf(layer));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path + ".tmp", ToBytes(layer, e.Now));
            File.Move(path + ".tmp", path, overwrite: true);
            written.Add(PathOf(layer));
        }

        return written;
    }

    /// <summary>The layouts were written as they are now.</summary>
    public void MarkSaved()
    {
        foreach (var e in _layers.Values)
        {
            e.Saved = e.Now;
        }
    }

    private static ImmutableDictionary<int, Vector2> Parse(byte[] bytes)
    {
        var root = JsonItem.Parse(bytes).AsObject();
        if (root.Member("format").AsString() != FormatName)
        {
            throw root.Member("format").Error($"expected \"{FormatName}\"");
        }

        if (root.Member("version").AsInt() != Version)
        {
            throw root.Member("version").Error($"version {Version} is the one this build reads");
        }

        var b = ImmutableDictionary.CreateBuilder<int, Vector2>();
        foreach (var n in root.Member("nodes").Items)
        {
            b[n.Member("key").AsInt(1, int.MaxValue)] = new Vector2(n.Member("x").AsInt(), n.Member("y").AsInt());
        }

        return b.ToImmutable();
    }

    private static byte[] ToBytes(string layer, ImmutableDictionary<int, Vector2> positions)
    {
        var root = JsonItem.NewObject()
            .Add("format", FormatName)
            .Add("version", Version)
            .Add("layer", layer)
            .Add("nodes", JsonItem.NewArray(positions.OrderBy(p => p.Key), p => JsonItem.NewObject()
                .Add("key", p.Key).Add("x", (int)MathF.Round(p.Value.X)).Add("y", (int)MathF.Round(p.Value.Y))));
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            root.WriteTo(w);
        }

        stream.WriteByte((byte)'\n');
        return stream.ToArray();
    }

    private Entry Get(string layer) => _layers.TryGetValue(layer, out var e) ? e : _layers[layer] = new Entry(None);

    private void Put(string layer, ImmutableDictionary<int, Vector2> value)
    {
        Get(layer).Now = value;
        Changed?.Invoke(layer);
    }

    private sealed class LayoutCommand(GraphLayouts owner, string layer, string label,
        ImmutableDictionary<int, Vector2> before, ImmutableDictionary<int, Vector2> after, string? merge) : IUndoCommand
    {
        private ImmutableDictionary<int, Vector2> _after = after;

        public string Label { get; } = label;
        private string? Merge { get; } = merge;
        private string Layer { get; } = layer;

        public void Do() => owner.Put(Layer, _after);

        public void Undo() => owner.Put(Layer, before);

        public bool TryMerge(IUndoCommand next)
        {
            if (Merge == null || next is not LayoutCommand c || c.Merge != Merge || c.Layer != Layer)
            {
                return false;
            }

            _after = c._after;
            return true;
        }
    }
}
