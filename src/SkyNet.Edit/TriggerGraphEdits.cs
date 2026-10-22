using System.Globalization;
using SkyNet.Formats;
using SkyNet.Game.Behaviour;
using SkyNet.Game.Mods;

namespace SkyNet.Edit;

/// <summary>What happens to the links into a deleted node: they go on to its next (splice), or end (cut).</summary>
public enum Rewire { Splice, Cut }

/// <summary>
/// The trigger graph's edits (editor plan step T8): each one undo step on the document's history,
/// written into the layer (so File > Save puts it in the save mod's <c>maps/&lt;layer&gt;.layer.json</c>).
/// <see cref="MapLayerException"/> when the edit does not fit: no such node, a marker's act, a fixed or
/// out-of-range parameter, an act the table lacks.
/// </summary>
public static class GraphEdits
{
    /// <summary>The state an added trigger starts with: armed, as the DOS maps' gates and proximity triggers.</summary>
    private const int Armed = 1;

    /// <summary>Links <paramref name="from"/>'s out pin to <paramref name="to"/> (its one next; an old one is replaced).</summary>
    public static void Connect(EditDocument doc, string layer, int from, int to)
    {
        var map = doc.Map(layer);
        Entity(map, from);
        Entity(map, to);
        if (from == to)
        {
            throw new MapLayerException($"@{from}: a record cannot lead to itself");
        }

        doc.EndDrag();
        doc.EditLink(layer, from, next: to);
    }

    /// <summary>Removes <paramref name="from"/>'s link (its next ends the chain).</summary>
    public static void Disconnect(EditDocument doc, string layer, int from)
    {
        if (Entity(doc.Map(layer), from).LinkNext == 0)
        {
            return;
        }

        doc.EndDrag();
        doc.EditLink(layer, from, next: 0);
    }

    /// <summary>Gives node <paramref name="id"/> act <paramref name="act"/> (a mesh without a link record gets one).</summary>
    public static void SetAct(EditDocument doc, string layer, int id, int act)
    {
        var e = Entity(doc.Map(layer), id);
        if (e.MarkerType >= 0)
        {
            throw new MapLayerException($"@{id}: a marker's act byte is its robot type");
        }

        if (!ActCatalog.IsKnown(act))
        {
            throw new MapLayerException(string.Create(CultureInfo.InvariantCulture, $"act 0x{act:X2} is not in the act table"));
        }

        doc.EndDrag();
        doc.EditLink(layer, id, act: act);
    }

    /// <summary>Sets parameter <paramref name="name"/> of node <paramref name="id"/> (a Bool: 0 or 1).</summary>
    public static void SetParam(EditDocument doc, string layer, int id, string name, int value)
    {
        var map = doc.Map(layer);
        var e = Entity(map, id);
        var node = TriggerGraph.Build(doc, layer).Node(id) ?? throw new MapLayerException($"@{id} is not a node of the graph");
        var p = node.Param(name) ?? throw new MapLayerException($"@{id} has no parameter \"{name}\"");
        if (p.ReadOnly)
        {
            throw new MapLayerException($"\"{name}\" is fixed by the act; change the act");
        }

        if (value < p.Min || value > p.Max)
        {
            throw new MapLayerException(string.Create(CultureInfo.InvariantCulture, $"{name} {value} is not {p.Min}-{p.Max}"));
        }

        var field = p.Field!;
        var raw = p.Raw(e.StateByte, value);
        doc.EndDrag();
        doc.SetFields(layer, id, new Dictionary<string, int> { [field] = raw }, label: "Set " + name);
    }

    /// <summary>
    /// Adds a node with act <paramref name="act"/> at DOS position (<paramref name="x"/>, <paramref name="y"/>,
    /// <paramref name="z"/>): a logic sprite (the sprite the map's own trigger sprites use) or a mesh of model
    /// <paramref name="model"/>. A trigger (use gate, proximity, relay) starts armed. Its key.
    /// </summary>
    public static int AddNode(EditDocument doc, string layer, int act, NewNodeBody body, int x, int y, int z, string? model = null)
    {
        if (!ActCatalog.IsKnown(act))
        {
            throw new MapLayerException(string.Create(CultureInfo.InvariantCulture, $"act 0x{act:X2} is not in the act table"));
        }

        var kind = ActRules.For(act).Kind;
        var f = new Dictionary<string, int>
        {
            ["x"] = x, ["y"] = y, ["z"] = z, ["act"] = act,
            ["state"] = kind is ActKind.ProxGate or ActKind.ProxChain or ActKind.Relay ? Armed : 0,
        };
        if (body == NewNodeBody.Mesh)
        {
            if (string.IsNullOrWhiteSpace(model))
            {
                throw new MapLayerException("a mesh node needs a model name");
            }

            f["variant"] = (int)MapEntityVariant.Mesh;
            doc.EndDrag();
            return doc.AddEntity(layer, f, model.Trim().ToUpperInvariant());
        }

        f["variant"] = (int)MapEntityVariant.Sprite;
        f["sprite"] = LogicSprite(doc.Map(layer));
        doc.EndDrag();
        return doc.AddEntity(layer, f);
    }

    /// <summary>Deletes node <paramref name="id"/>; the links into it go on to its next or end
    /// (<paramref name="rewire"/>). One undo step.</summary>
    public static void DeleteNode(EditDocument doc, string layer, int id, Rewire rewire)
    {
        var map = doc.Map(layer);
        var e = Entity(map, id);
        var next = rewire == Rewire.Splice ? Math.Max(0, TriggerChains.NextOf(map, e)) : 0;
        doc.EndDrag();
        doc.Group("Delete node", () =>
        {
            foreach (var from in TriggerChains.Incoming(map, id))
            {
                doc.EditLink(layer, from, next: from == next ? 0 : next);
            }

            doc.DeleteEntity(layer, id);
        });
    }

    /// <summary>The sprite index the map's own logic sprites (variant 3, not a marker, with an act) use most; the
    /// shipped maps' trigger sprite when the map has none.</summary>
    private static int LogicSprite(MapFile map)
    {
        const int ShippedLogic = 26006;
        return map.Entities
            .Where(e => e.Variant == MapEntityVariant.Sprite && e.MarkerType < 0 && e.ActType > 0
                && ActRules.For(e.ActType).Kind is ActKind.ProxGate or ActKind.SoundCue or ActKind.Exit)
            .GroupBy(e => e.SpriteIndex).OrderByDescending(g => g.Count()).ThenBy(g => g.Key)
            .Select(g => g.Key).DefaultIfEmpty(ShippedLogic).First();
    }

    private static MapEntity Entity(MapFile map, int key) =>
        map.TryGetEntityAt(key, out var e) ? e : throw new MapLayerException($"@{key}: the map has no entity under that key");
}

/// <summary>The trigger graph's checks (<see cref="TriggerGraph.Issues"/>): acts the table lacks, links to
/// nothing or to the record itself, loops nothing starts, parameters out of range, light acts on records
/// that are not lights, links cut by an actor, acts nothing sets off, exits to maps the mission lacks.</summary>
internal static class GraphChecks
{
    public static IEnumerable<GraphIssue> Run(EditDocument doc, string? instance, MapFile map, IReadOnlyList<GraphNode> nodes)
    {
        foreach (var n in nodes)
        {
            map.TryGetEntityAt(n.Id, out var e);
            foreach (var issue in Node(doc, instance, map, n, e!))
            {
                yield return issue;
            }
        }

        foreach (var (head, count) in Validator.DeadLoops(map))
        {
            yield return new GraphIssue(ValidationLevel.Warning, TriggerGraph.DeadLoop, head, Inv($"a loop of {count} records that nothing leads into or starts"));
        }
    }

    private static IEnumerable<GraphIssue> Node(EditDocument doc, string? instance, MapFile map, GraphNode n, MapEntity e)
    {
        GraphIssue Issue(ValidationLevel level, string code, string text) => new(level, code, n.Id, text);

        if (e.LinkNext == e.FileOffset)
        {
            yield return Issue(ValidationLevel.Warning, TriggerGraph.SelfLink, "next leads to the record itself (ignored)");
        }
        else if (e.LinkNext > 0 && !map.TryGetEntityAt(e.LinkNext, out _))
        {
            yield return Issue(ValidationLevel.Error, TriggerGraph.Dangling, Inv($"next @{e.LinkNext} is no entity of the map"));
        }

        if (n.Body == NodeBody.Marker)
        {
            yield break;
        }

        if (n.Act > 0 && !ActCatalog.IsKnown(n.Act))
        {
            yield return Issue(ValidationLevel.Error, TriggerGraph.UnknownAct, Inv($"act 0x{n.Act:X2} is not in the act table"));
        }

        foreach (var p in n.Params.Where(p => !p.ReadOnly && (p.Value < p.Min || p.Value > p.Max)))
        {
            yield return Issue(ValidationLevel.Error, TriggerGraph.Range, Inv($"{p.Name} {p.Value} is not {p.Min}-{p.Max}"));
        }

        if (n.Info.Kind == ActKind.Light && n.Body != NodeBody.Light)
        {
            yield return Issue(ValidationLevel.Warning, TriggerGraph.LightAct, "a light act on a record that is no light does nothing");
        }

        // ObjFlipLink stops after an actor: its next is not reached by the walk.
        if (e.IsActor && n.Next >= 0)
        {
            yield return Issue(ValidationLevel.Warning, TriggerGraph.ActorCut, "the chain walk stops at this actor: its next is not reached");
        }

        if (n.Modes.Count == 0 && (e.StateByte & 1) == 0 && n.Info.Kind is not (ActKind.None or ActKind.Inert or ActKind.Undecoded))
        {
            yield return Issue(ValidationLevel.Warning, TriggerGraph.Unfired, "nothing sets it off: no link in, not armed, no use, shot or pad");
        }

        if (n.Exit is { MissionLink: false, Map: > 0 } x && doc.Mission is { } m && instance != null && m.InstanceOf(x.Map) == null)
        {
            yield return Issue(ValidationLevel.Error, TriggerGraph.ExitTarget, Inv($"exit to MAP.{x.Map:D3}, which the mission lacks (no exit link)"));
        }
    }

    private static string Inv(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
}
