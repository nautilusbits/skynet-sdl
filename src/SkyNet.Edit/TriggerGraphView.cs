using System.Globalization;
using System.Numerics;
using SkyNet.Game.Mods;

namespace SkyNet.Edit;

/// <summary>
/// The canvas's view (<see cref="IGraphView"/>) of one layer's <see cref="TriggerGraph"/>: nodes placed by
/// <see cref="GraphLayouts"/>, coloured by act family, an "act" combo and the node's parameters as rows;
/// edits through <see cref="GraphEdits"/> and the layouts, each an undo step of the document. Rebuilt when the
/// layer, the mission or the layout changes.
/// </summary>
public sealed class TriggerGraphView : IGraphView, IDisposable
{
    public const string ActKey = "act";

    /// <summary>The next link's row: an entity field (combo, Pick in the view, an Outliner row dropped on it).</summary>
    public const string NextKey = "next";
    private const string InPin = "in", NextPin = "next", DoesLabel = "does";

    private static readonly IReadOnlyList<ParamChoice> Acts =
        [.. ActCatalog.All.Select(a => new ParamChoice(a.Id, a.Name))];
    private static readonly IReadOnlyList<ParamChoice> OffOn = [new(0, "off"), new(1, "on")];
    private static readonly ParamChoice None = new(0, "none");
    private static readonly IReadOnlyList<CanvasNodeKind> NewKinds =
        [.. ActCatalog.All.Where(a => a.Id != 0).Select(a => new CanvasNodeKind(a.Id.ToString(CultureInfo.InvariantCulture), a.Name, a.Family.ToString()))
            .OrderBy(k => k.Category, StringComparer.Ordinal)];

    private readonly EditDocument _doc;
    private readonly string _layer;
    private readonly Func<Vector3> _newAt;
    private readonly Func<TriggerGraph, IReadOnlyList<CanvasChain>>? _verify;
    private TriggerGraph? _graph;
    private List<CanvasNode> _nodes = [];
    private List<CanvasLink> _links = [];
    private List<CanvasIssue> _issues = [];

    /// <param name="newAt">The world point a node added from the menu stands on (the level view's centre).</param>
    /// <param name="verify">Runs the game's verifier over the graph's chains (null: Verify lists nothing).</param>
    public TriggerGraphView(EditDocument doc, string layer, Func<Vector3> newAt, Func<TriggerGraph, IReadOnlyList<CanvasChain>>? verify = null)
    {
        (_doc, _layer, _newAt, _verify) = (doc, layer, newAt, verify);
        _doc.LayerChanged += OnLayer;
        _doc.MissionChanged += Stale;
        _doc.Layouts.Changed += OnLayer;
    }

    public string Layer => _layer;

    /// <summary>The model as the canvas shows it now.</summary>
    public TriggerGraph Graph => Build();

    public IReadOnlyList<CanvasNode> Nodes => Built(() => _nodes);

    public IReadOnlyList<CanvasLink> Links => Built(() => _links);

    public IReadOnlyList<CanvasIssue> Issues => Built(() => _issues);

    public IReadOnlyList<CanvasNodeKind> Kinds => NewKinds;

    public void Dispose()
    {
        _doc.LayerChanged -= OnLayer;
        _doc.MissionChanged -= Stale;
        _doc.Layouts.Changed -= OnLayer;
    }

    // ---- edits ----

    public string Connect(CanvasLink link) =>
        Run(() => GraphEdits.Connect(_doc, _layer, link.FromNode, link.ToNode), $"@{link.FromNode} next -> @{link.ToNode}");

    public string Disconnect(CanvasLink link) =>
        Run(() => GraphEdits.Disconnect(_doc, _layer, link.FromNode), $"@{link.FromNode} next cut");

    public string Relink(CanvasLink old, CanvasLink link) =>
        Run(() => _doc.Group("Relink", () =>
        {
            GraphEdits.Disconnect(_doc, _layer, old.FromNode);
            GraphEdits.Connect(_doc, _layer, link.FromNode, link.ToNode);
        }), $"@{link.FromNode} next -> @{link.ToNode}");

    public string Move(IReadOnlyList<int> nodes, Vector2 delta)
    {
        var at = _doc.Layouts.Resolve(Build());
        var moves = nodes.Where(at.ContainsKey).ToDictionary(id => id, id => at[id] + delta);
        _doc.Layouts.Move(_layer, moves);
        // Each drag its own undo step (the layouts merge moves of the same nodes until a drag ends).
        _doc.EndDrag();
        return $"Trigger graph: moved {moves.Count} nodes";
    }

    public string Delete(IReadOnlyList<int> nodes) =>
        Run(() => _doc.Group("Delete nodes", () =>
        {
            foreach (var id in nodes)
            {
                GraphEdits.DeleteNode(_doc, _layer, id, Rewire.Splice);
            }
        }), $"deleted {nodes.Count} nodes (links spliced)");

    public int? Add(string kind, Vector2 at)
    {
        if (!int.TryParse(kind, NumberStyles.Integer, CultureInfo.InvariantCulture, out var act))
        {
            return null;
        }

        try
        {
            // The entity and its position one undo step.
            var w = _newAt();
            var id = 0;
            _doc.GroupWith("Add node", () =>
            {
                id = GraphEdits.AddNode(_doc, _layer, act, NewNodeBody.Logic,
                    (int)MathF.Round(w.X), (int)MathF.Round(-w.Y), (int)MathF.Round(-w.Z));
                return _doc.Layouts.Place(_layer, new Dictionary<int, Vector2> { [id] = at });
            });
            _doc.EndDrag();
            return id;
        }
        catch (MapLayerException e)
        {
            RetroEng.Diagnostics.Log.Warn("Edit", "Trigger graph: " + e.Message);
            return null;
        }
    }

    public string SetParam(int node, string key, long value)
    {
        var v = (int)Math.Clamp(value, int.MinValue, int.MaxValue);
        return key switch
        {
            ActKey => Run(() => GraphEdits.SetAct(_doc, _layer, node, v), $"@{node} act {TriggerChains.ActName(v)}"),
            NextKey when v == 0 => Run(() => GraphEdits.Disconnect(_doc, _layer, node), $"@{node} next cut"),
            NextKey => Run(() => GraphEdits.Connect(_doc, _layer, node, v), $"@{node} next -> @{v}"),
            _ => Run(() => GraphEdits.SetParam(_doc, _layer, node, key, v), $"@{node} {key} {v}"),
        };
    }

    /// <summary>The next row's choices: none, then every other entity of the map by its label.</summary>
    public IReadOnlyList<ParamChoice> Targets(int node, string key)
    {
        if (key != NextKey)
        {
            return [];
        }

        var map = _doc.Map(_layer);
        return [None, .. map.Entities.Where(e => e.FileOffset != node).Select(e => new ParamChoice(e.FileOffset, EditorWorld.Label(map, e)))];
    }

    public IReadOnlyList<CanvasChain> Verify() => _verify?.Invoke(Build()) ?? [];

    private static string Run(Action edit, string done)
    {
        try
        {
            edit();
            return "Trigger graph: " + done;
        }
        catch (MapLayerException e)
        {
            return "Trigger graph: " + e.Message;
        }
    }

    // ---- the model as canvas nodes ----

    private void OnLayer(string layer)
    {
        if (layer == _layer)
        {
            Stale();
        }
    }

    private void Stale() => _graph = null;

    private T Built<T>(Func<T> part)
    {
        Build();
        return part();
    }

    private TriggerGraph Build()
    {
        if (_graph != null)
        {
            return _graph;
        }

        var graph = TriggerGraph.Build(_doc, _layer);
        var at = _doc.Layouts.Resolve(graph);
        _nodes = [.. graph.Nodes.Select(n => NodeOf(n, at[n.Id], graph.Node(n.Next)?.Title))];
        _links = [.. graph.Edges.Where(e => graph.Node(e.To) != null).Select(e => new CanvasLink(e.From, 0, e.To, 0))];
        _issues = [.. graph.Issues.Select(i => new CanvasIssue(i.Node, i.Level == ValidationLevel.Error ? IssueLevel.Error : IssueLevel.Warning, i.Text))];
        _graph = graph;
        return graph;
    }

    /// <summary>The height of <paramref name="n"/>'s box on the canvas (the auto layout stacks by it).</summary>
    public static float Height(GraphNode n) => GraphLayout.Size(NodeOf(n, Vector2.Zero)).Y;

    // nextTitle: the next node's title (a next always leads to a node: it has a link in).
    private static CanvasNode NodeOf(GraphNode n, Vector2 at, string? nextTitle = null)
    {
        var rows = new List<CanvasParam> { new(DoesLabel, DoesLabel, ParamKind.Label, 0, Text: n.Label) };
        if (n.Body != NodeBody.Marker)
        {
            rows.Add(new CanvasParam(ActKey, ActKey, ParamKind.Choice, n.Act, Acts, n.Info.Name));
        }

        rows.Add(new CanvasParam(NextKey, NextKey, ParamKind.Entity, Math.Max(n.Next, 0), Text: n.Next < 0 ? None.Label : nextTitle));

        rows.AddRange(n.Params.Select(ParamOf));
        return new CanvasNode(n.Id, n.Title, TriggerChains.Colour(n.Family), at, [InPin], [NextPin], rows);
    }

    private static CanvasParam ParamOf(GraphParam p)
    {
        if (p.ReadOnly)
        {
            var text = p.Type == ParamType.Text ? p.Text : p.Value.ToString(CultureInfo.InvariantCulture);
            return new CanvasParam(p.Name, p.Name, ParamKind.Label, p.Value, Text: text);
        }

        return p.Type == ParamType.Bool
            ? new CanvasParam(p.Name, p.Name, ParamKind.Choice, p.Value, OffOn)
            : new CanvasParam(p.Name, p.Name, ParamKind.Number, p.Value);
    }
}
