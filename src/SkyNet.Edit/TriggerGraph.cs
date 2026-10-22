using System.Globalization;
using SkyNet.Formats;
using SkyNet.Game.Behaviour;
using SkyNet.Verify;

namespace SkyNet.Edit;

/// <summary>Where an act's handler comes from: SKYNET.EXE's table, or the port alone.</summary>
public enum ActOrigin { Dos, Port }

/// <summary>A node parameter's value type: a number, one state bit, a map number, or text (fixed only).</summary>
public enum ParamType { Int, Bool, Map, Text }

/// <summary>What record carries a node: a mesh (variant 1), a light (2), a sprite (3), a placement marker.</summary>
public enum NodeBody { Mesh, Light, Sprite, Marker }

/// <summary>What a new node is made of: an invisible logic sprite, or a mesh with a model name.</summary>
public enum NewNodeBody { Logic, Mesh }

/// <summary>
/// One parameter of a node: its name, type and value, the range it may take, and the map layer field
/// it writes (<see cref="Mask"/>: the state bit a Bool stands for). No field: the act id fixes it
/// (a mover's axis and travel, an objective's number): change the act to change it.
/// </summary>
public sealed record GraphParam(string Name, ParamType Type, int Value, int Min, int Max, string? Field, int Mask = 0, string Text = "")
{
    public bool ReadOnly => Field == null;

    /// <summary>The field's raw value for <paramref name="value"/>: a Bool sets or clears its bit in <paramref name="state"/>.</summary>
    public int Raw(int state, int value) => Type == ParamType.Bool ? (value != 0 ? state | Mask : state & ~Mask) : value;
}

/// <summary>One act of the catalogue: id, name ("0x3F slide X +64"), handler family, origin and
/// the parameters its id fixes.</summary>
public sealed record ActInfo(int Id, string Name, ActKind Kind, LinkFamily Family, ActOrigin Origin, IReadOnlyList<GraphParam> Fixed);

/// <summary>Where an exit node leads: the mission's link (instance, or back) or the record's own map and set.</summary>
public sealed record GraphExit(int Map, int Arrival, string? Instance, bool Back, bool MissionLink);

/// <summary>
/// One node of the trigger graph: a record that fires or is fired. In pin: <see cref="In"/> (the records
/// whose next is this one); out pin: <see cref="Next"/> (−1: none; DOS gives a record one next).
/// <see cref="Modes"/> are how the player sets it off (the verifier's: use, prox, walk, touch, shot,
/// death, counter, chain).
/// </summary>
public sealed record GraphNode(int Id, string Title, string Label, NodeBody Body, int Act, ActInfo Info,
    IReadOnlyList<string> Modes, IReadOnlyList<GraphParam> Params, int Next, IReadOnlyList<int> In, GraphExit? Exit)
{
    public LinkFamily Family => Body == NodeBody.Marker ? LinkFamily.Other : Info.Family;

    /// <summary>True when the player sets it off itself (any mode but "chain"): a chain's start.</summary>
    public bool IsSource => Modes.Any(m => m != "chain");

    public GraphParam? Param(string name) => Params.FirstOrDefault(p => p.Name == name);
}

/// <summary>A link of the graph: <see cref="From"/>'s next is <see cref="To"/>; coloured by From's act.</summary>
public sealed record GraphEdge(int From, int To, LinkFamily Family);

/// <summary>One problem of the graph: level, code (<see cref="TriggerGraph"/>'s constants), node and text.</summary>
public sealed record GraphIssue(ValidationLevel Level, string Code, int Node, string Text);

/// <summary>
/// The SkyNET act catalogue for the graph's act picker: every act the handler table decodes
/// (<see cref="ActRules"/>), with the parameters the id fixes. The game defines no port-only act
/// (every handler is SKYNET.EXE's); <see cref="ActOrigin.Port"/> marks one when the runtime adds it.
/// </summary>
public static class ActCatalog
{
    /// <summary>Every known act, in id order.</summary>
    public static IReadOnlyList<ActInfo> All { get; } = [.. TriggerChains.KnownActs.Select(Make)];

    private static readonly Dictionary<int, ActInfo> ById = All.ToDictionary(a => a.Id);

    /// <summary>The act <paramref name="id"/>, an undecoded one included.</summary>
    public static ActInfo Info(int id) => ById.TryGetValue(id, out var a) ? a : Make(id);

    /// <summary>True when the handler table decodes <paramref name="id"/>.</summary>
    public static bool IsKnown(int id) => ById.ContainsKey(id);

    private static ActInfo Make(int id) =>
        new(id, TriggerChains.ActName(id), ActRules.For(id).Kind, TriggerChains.Family(id), ActOrigin.Dos, Fixed(id));

    // The parameters the act id fixes: the slot's config words as the handler reads them.
    private static List<GraphParam> Fixed(int id)
    {
        var r = ActRules.For(id);
        switch (r.Kind)
        {
            case ActKind.Mover:
                var m = ActRules.Mover(id);
                return
                [
                    Text("motion", m.Family.ToString().ToLowerInvariant()),
                    Text("axis", "XYZ"[m.Axis].ToString()),
                    Fixed("travel", (int)m.Span),
                    Fixed("direction", (int)m.Sign),
                    Fixed("speed", (int)m.Speed),
                ];
            case ActKind.Light:
                return [Text("op", ActRules.LightOp(id))];
            case ActKind.Destructible:
                return [Text("stages", id == ActRules.DestructRamp ? "ramp while armed" : "one per enable")];
            case ActKind.Hint:
                return [Fixed("hint", r.P6 + 1)];
            case ActKind.Objective:
                return [Fixed("objective", r.P6 + 1)];
            case ActKind.Relay:
                return [Fixed("at count", r.P4)];
            case ActKind.Water:
                return [Fixed("delta", r.P4), Fixed("partner act", r.P6)];
            case ActKind.SoundCue:
                return [Fixed("sound", r.P4)];
            case ActKind.ProxGate:
                return [Fixed("reach", r.P6)];
            case ActKind.ProxChain:
                return [Fixed("radius", r.P4)];
            default:
                return [];
        }
    }

    private static GraphParam Fixed(string name, int value) => new(name, ParamType.Int, value, value, value, null);

    private static GraphParam Text(string name, string text) => new(name, ParamType.Text, 0, 0, 0, null, Text: text);
}

/// <summary>
/// The trigger graph of one layer of the edited mission (editor plan step T8): a node per record that
/// fires or is fired (an act the table decodes, or a link in or out), an edge per next link, each node's
/// act, parameters and readable label, and the graph's problems (<see cref="Issues"/>). Built from the
/// layer's edited map; rebuild it after a change (<see cref="EditDocument.LayerChanged"/>).
/// </summary>
public sealed class TriggerGraph
{
    public const string UnknownAct = "act", Dangling = "next", SelfLink = "self", DeadLoop = "loop", Range = "range",
        LightAct = "light", ActorCut = "actor", Unfired = "unfired", ExitTarget = "exit";

    private readonly Dictionary<int, GraphNode> _byId;

    private TriggerGraph(string layer, int map, List<GraphNode> nodes, List<GraphEdge> edges, List<GraphIssue> issues)
    {
        Layer = layer;
        MapNumber = map;
        Nodes = nodes;
        Edges = edges;
        Issues = issues;
        _byId = nodes.ToDictionary(n => n.Id);
    }

    public string Layer { get; }

    public int MapNumber { get; }

    /// <summary>The nodes in key order.</summary>
    public IReadOnlyList<GraphNode> Nodes { get; }

    /// <summary>The links between nodes, in key order of their source.</summary>
    public IReadOnlyList<GraphEdge> Edges { get; }

    /// <summary>The problems: errors first, then warnings and info, each in node order. One the DOS map
    /// already has is a warning, not an error.</summary>
    public IReadOnlyList<GraphIssue> Issues { get; }

    public GraphNode? Node(int id) => _byId.GetValueOrDefault(id);

    /// <summary>The graph of <paramref name="layer"/> as it is now.</summary>
    public static TriggerGraph Build(EditDocument doc, string layer)
    {
        var map = doc.Map(layer);
        var instance = doc.Mission?.World.Instances.FirstOrDefault(i => i.Layer == layer)?.Id;
        var nodes = NodesOf(doc, instance, map);
        var edges = nodes.Where(n => n.Next >= 0).Select(n => new GraphEdge(n.Id, n.Next, n.Family)).ToList();
        var issues = GraphChecks.Run(doc, instance, map, nodes).ToList();
        var inDos = GraphChecks.Run(doc, instance, doc.BaseMap(layer), NodesOf(doc, instance, doc.BaseMap(layer)))
            .Select(i => (i.Code, i.Node)).ToHashSet();
        issues = [.. issues
            .Select(i => i.Level == ValidationLevel.Error && inDos.Contains((i.Code, i.Node))
                ? i with { Level = ValidationLevel.Warning, Text = i.Text + " (as in the DOS map)" } : i)
            .OrderBy(i => i.Level).ThenBy(i => i.Node)];
        return new TriggerGraph(layer, doc.MapNumber(layer), nodes, edges, issues);
    }

    /// <summary>True when record <paramref name="e"/> belongs in the graph: it links or is linked, or
    /// carries an act that does something on its own or when reached (not a pickup, loop, light
    /// act on a non-light, inert or empty act unless linked).</summary>
    private static bool Wanted(MapEntity e, HashSet<int> incoming)
    {
        if (e.LinkNext > 0 || incoming.Contains(e.FileOffset))
        {
            return true;
        }

        if (e.MarkerType >= 0 || e.ActType == 0)
        {
            return false;
        }

        return ActRules.For(e.ActType, (int)e.Variant).Kind is not (ActKind.Pickup or ActKind.SoundLoop or ActKind.Inert or ActKind.None);
    }

    private static List<GraphNode> NodesOf(EditDocument doc, string? instance, MapFile map)
    {
        var incoming = new Dictionary<int, List<int>>();
        foreach (var e in map.Entities)
        {
            if (TriggerChains.NextOf(map, e) is var n and >= 0)
            {
                (incoming.TryGetValue(n, out var list) ? list : incoming[n] = []).Add(e.FileOffset);
            }
        }

        var linked = incoming.Keys.ToHashSet();
        var nodes = new List<GraphNode>();
        foreach (var e in map.Entities.OrderBy(e => e.FileOffset))
        {
            if (!Wanted(e, linked))
            {
                continue;
            }

            var body = GraphParams.BodyOf(e);
            var act = body == NodeBody.Marker ? 0 : e.ActType;
            var info = ActCatalog.Info(act);
            var modes = TriggerVerifier.Modes(e, TriggerVerifier.KindName(e), linked.Contains(e.FileOffset));
            var exit = info.Kind == ActKind.Exit && body != NodeBody.Marker ? ExitOf(doc, instance, e) : null;
            nodes.Add(new GraphNode(e.FileOffset, EditorWorld.Label(map, e), LabelOf(e, body, info, exit), body, act, info, modes,
                GraphParams.Of(e, body, info), TriggerChains.NextOf(map, e), incoming.GetValueOrDefault(e.FileOffset) ?? [], exit));
        }

        return nodes;
    }

    private static GraphExit ExitOf(EditDocument doc, string? instance, MapEntity e)
    {
        var link = instance == null ? null : doc.Mission?.World.Link(instance, e.FileOffset);
        return link == null
            ? new GraphExit(e.Param16, e.Param8, null, e.Param16 == 0, MissionLink: false)
            : new GraphExit(e.Param16, link.Arrival, link.To, link.Back, MissionLink: true);
    }

    // A node's readable line: what its act does with this record ("exit to MAP.212 set 0", "voice 21029").
    private static string LabelOf(MapEntity e, NodeBody body, ActInfo info, GraphExit? exit)
    {
        if (body == NodeBody.Marker)
        {
            return e.MarkerType == 2 ? Inv($"robot type {e.EnemyType} (path)") : Inv($"marker {e.MarkerType}");
        }

        return info.Kind switch
        {
            ActKind.Exit when exit is { MissionLink: true } x => x.Back ? "exit back" : x.Instance == null ? "exit (closed)" : $"exit to {x.Instance} set {x.Arrival}",
            ActKind.Exit => e.Param16 == 0 ? Inv($"exit back set {e.Param8}") : Inv($"exit to MAP.{e.Param16:D3} set {e.Param8}"),
            ActKind.Voice => Inv($"voice {e.Param16}"),
            ActKind.SoundLoop => Inv($"sound loop {e.Param16}"),
            ActKind.Spawn => Inv($"spawn type {e.Param16}"),
            ActKind.Light when e.Variant != MapEntityVariant.Light => TriggerChains.ActText(info.Id) + " (no light: inert)",
            _ => TriggerChains.ActText(info.Id),
        };
    }

    private static string Inv(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
}

/// <summary>The parameters of a record as a node: those its act id fixes, then the record's own.</summary>
internal static class GraphParams
{
    private const int StateMax = 255, U16Max = 0xFFFF, MapMax = 999;

    /// <summary>What record carries <paramref name="e"/> as a node.</summary>
    public static NodeBody BodyOf(MapEntity e) => e.Variant switch
    {
        MapEntityVariant.Mesh => NodeBody.Mesh,
        MapEntityVariant.Light => NodeBody.Light,
        _ => e.MarkerType >= 0 ? NodeBody.Marker : NodeBody.Sprite,
    };

    /// <summary>The parameters of any record (a node or not) under its own act.</summary>
    public static IReadOnlyList<GraphParam> Of(MapEntity e) =>
        BodyOf(e) is var body && body == NodeBody.Marker ? [] : Of(e, body, ActCatalog.Info(e.ActType));

    public static IReadOnlyList<GraphParam> Of(MapEntity e, NodeBody body, ActInfo info)
    {
        if (body == NodeBody.Marker)
        {
            return [];
        }

        var list = new List<GraphParam>(info.Fixed);
        list.Add(Bit("armed", e, 1));
        list.Add(Bit("on every hit", e, 2));
        list.Add(Bit("on death", e, 4));
        switch (body)
        {
            case NodeBody.Mesh:
                list.Add(Bit("wall button", e, ActRules.WallButtonBit));
                list.Add(Bit("walk-on pad", e, ActRules.PadBit));
                list.Add(new GraphParam("hit points", ParamType.Int, e.HitPoints, 0, U16Max, "hitPoints"));
                if (e.Link is { } link)
                {
                    list.Add(new GraphParam("destroy type", ParamType.Int, link.DestroyType, 0, StateMax, "destroyType"));
                    list.Add(new GraphParam("destroy param", ParamType.Int, link.DestroyParam, short.MinValue, short.MaxValue, "destroyParam"));
                }

                break;
            case NodeBody.Light:
                list.Add(new GraphParam("intensity", ParamType.Int, e.LightIntensity, 0, U16Max, "light"));
                list.Add(new GraphParam("enable", ParamType.Int, e.LightEnable, 0, U16Max, "lightEnable"));
                break;
            case NodeBody.Sprite:
                list.AddRange(SpriteParams(e, info.Kind));
                break;
        }

        return list;
    }

    // A sprite's per-use words (sub+2, sub+4) as its act reads them.
    private static IEnumerable<GraphParam> SpriteParams(MapEntity e, ActKind kind) => kind switch
    {
        ActKind.Exit =>
        [
            new GraphParam("map", ParamType.Map, e.Param16, 0, MapMax, "param16"),
            new GraphParam("arrival set", ParamType.Int, e.Param8, 0, StateMax, "param8"),
        ],
        ActKind.Voice => [new GraphParam("voice line", ParamType.Int, e.Param16, 0, U16Max, "param16")],
        ActKind.SoundLoop => [new GraphParam("sound", ParamType.Int, e.Param16, 0, U16Max, "param16")],
        ActKind.Spawn => [new GraphParam("robot type", ParamType.Int, e.Param16, 0, U16Max, "param16")],
        _ => [],
    };

    private static GraphParam Bit(string name, MapEntity e, int mask) =>
        new(name, ParamType.Bool, (e.StateByte & mask) != 0 ? 1 : 0, 0, 1, "state", mask);
}
