using System.Numerics;
using SkyNet.Formats;
using SkyNet.Game.Campaign;

namespace SkyNet.Edit.Tests;

/// <summary>The canvas's view of the real trigger graph (editor-tools plan, T8) on mission 1's MAP.210:
/// it mirrors the model, and each canvas call is one undo step of the document.</summary>
[Collection(EditModsCollection.Name)]
public sealed class TriggerGraphViewTests
{
    private static (EditDocument Doc, TriggerGraphView View) Open()
    {
        var data = GameData.Open(EditDocumentTests.DataDir!)!;
        var doc = EditDocument.Load(data, MissionGenerator.Build(data, MissionTable.For(data.Game).ByNumber(1)!.Value), null);
        var layer = doc.Mission!.World.Instances.First(i => i.Id == "210").Layer;
        return (doc, new TriggerGraphView(doc, layer, () => Vector3.Zero));
    }

    [MapDataFact]
    public void The_view_shows_the_models_nodes_links_and_issues()
    {
        var (_, view) = Open();
        var graph = view.Graph;

        Assert.Equal(graph.Nodes.Select(n => n.Id), view.Nodes.Select(n => n.Id));
        Assert.Equal(graph.Edges.Select(e => (e.From, e.To)), view.Links.Select(l => (l.FromNode, l.ToNode)));
        Assert.Equal(graph.Issues.Count, view.Issues.Count);
        Assert.All(view.Nodes, n => Assert.Equal(["next"], n.Outputs));
    }

    [MapDataFact]
    public void Cutting_and_relinking_are_undo_steps_the_view_follows()
    {
        var (doc, view) = Open();
        var link = view.Links[0];

        view.Disconnect(link);
        Assert.DoesNotContain(link, view.Links);
        doc.History.Undo();
        Assert.Contains(link, view.Links);

        var other = view.Nodes.First(n => n.Id != link.FromNode && n.Id != link.ToNode).Id;
        view.Connect(new CanvasLink(link.FromNode, 0, other, 0));
        Assert.Contains(new CanvasLink(link.FromNode, 0, other, 0), view.Links);
        Assert.DoesNotContain(link, view.Links);
        doc.History.Undo();
        Assert.Contains(link, view.Links);
    }

    [MapDataFact]
    public void Picking_a_next_sets_it_in_one_undo_step_and_cancels_cleanly()
    {
        var (doc, view) = Open();
        var window = new TriggerGraphWindow(() => view, _ => { });
        var link = view.Links[0];
        CanvasParam Next() => view.Nodes.First(n => n.Id == link.FromNode).Params.Single(p => p.Key == TriggerGraphView.NextKey);
        Assert.Equal(ParamKind.Entity, Next().Kind);
        Assert.Equal(link.ToNode, Next().Value);
        var target = doc.Map(view.Layer).Entities.First(e => e.FileOffset != link.FromNode && e.FileOffset != link.ToNode).FileOffset;
        Assert.Contains(view.Targets(link.FromNode, TriggerGraphView.NextKey), c => c.Value == target);

        // Begun and cancelled (Esc): nothing changes, a later click is no pick.
        window.BeginPick(link.FromNode, TriggerGraphView.NextKey);
        Assert.True(window.Picking);
        window.CancelPick();
        Assert.False(window.Picking);
        Assert.Null(window.Pick(target));
        Assert.Equal(0, doc.History.UndoCount);

        // A click on an entity in the view: the field set, one undo step, the pick over.
        window.BeginPick(link.FromNode, TriggerGraphView.NextKey);
        Assert.NotNull(window.Pick(target));
        Assert.False(window.Picking);
        Assert.Equal(1, doc.History.UndoCount);
        Assert.Equal(target, Next().Value);
        doc.History.Undo();
        Assert.Contains(link, view.Links);

        // An Outliner row dropped on the field: the same edit; the record itself is refused.
        window.Drop(link.FromNode, TriggerGraphView.NextKey, target);
        Assert.Equal(target, Next().Value);
        Assert.Equal(1, doc.History.UndoCount);
        Assert.Contains("itself", window.Drop(link.FromNode, TriggerGraphView.NextKey, link.FromNode));
        Assert.Equal(1, doc.History.UndoCount);
    }

    [MapDataFact]
    public void A_move_is_one_undo_step_and_a_bad_edit_is_reported_not_thrown()
    {
        var (doc, view) = Open();
        var ids = view.Nodes.Take(2).Select(n => n.Id).ToList();
        var before = ids.Select(id => view.Nodes.First(n => n.Id == id).Position).ToList();

        view.Move(ids, new Vector2(40f, -20f));
        Assert.Equal(before.Select(p => p + new Vector2(40f, -20f)), ids.Select(id => view.Nodes.First(n => n.Id == id).Position));
        doc.History.Undo();
        Assert.Equal(before, ids.Select(id => view.Nodes.First(n => n.Id == id).Position));

        var line = view.Connect(new CanvasLink(ids[0], 0, ids[0], 0));
        Assert.Contains("itself", line);
    }

    [MapDataFact]
    public void Auto_layout_stacks_a_column_by_each_nodes_height()
    {
        var (_, view) = Open();

        // In every column each node starts below the one above it, a gap under its bottom.
        foreach (var column in view.Nodes.GroupBy(n => n.Position.X))
        {
            var stack = column.OrderBy(n => n.Position.Y).ToList();
            for (var i = 1; i < stack.Count; i++)
            {
                var above = stack[i - 1];
                Assert.True(stack[i].Position.Y >= above.Position.Y + GraphLayout.Size(above).Y + GraphLayouts.RowGap,
                    $"@{stack[i].Id} overlaps @{above.Id}");
            }
        }
    }

    [MapDataFact]
    public void An_added_node_is_one_undo_step()
    {
        var (doc, view) = Open();
        var steps = doc.History.UndoCount;

        var id = view.Add(view.Kinds[0].Id, new Vector2(-500f, -500f))!.Value;
        Assert.Contains(view.Nodes, n => n.Id == id && n.Position == new Vector2(-500f, -500f));
        Assert.Equal(steps + 1, doc.History.UndoCount);

        // One Ctrl+Z: the node and its position gone.
        Assert.True(doc.Undo());
        Assert.DoesNotContain(view.Nodes, n => n.Id == id);
        Assert.False(doc.Layouts.Stored(view.Layer).ContainsKey(id));
    }

    [MapDataFact]
    public void A_link_added_or_rewired_on_the_canvas_is_one_undo_step()
    {
        var (doc, view) = Open();
        var link = view.Links[0];
        var other = view.Nodes.First(n => n.Id != link.FromNode && n.Id != link.ToNode && view.Links.All(l => l.ToNode != n.Id)).Id;

        // The two in pins apart from every other node, so the drags hit them.
        view.Move([link.ToNode], new Vector2(-3000f, -3000f) - view.Nodes.First(n => n.Id == link.ToNode).Position);
        view.Move([other], new Vector2(-3000f, -2600f) - view.Nodes.First(n => n.Id == other).Position);
        var canvas = new GraphCanvas();
        var (origin, size) = (Vector2.Zero, new Vector2(1600f, 1000f));
        canvas.Update(view, new CanvasInput(origin), origin, size);
        canvas.Focus(view, link.ToNode);
        Vector2 InPin(int id) => canvas.Camera.ToScreen(GraphLayout.Pin(view.Nodes.First(n => n.Id == id), PinSide.In, 0));
        void Drag(Vector2 from, Vector2 to)
        {
            canvas.Update(view, new CanvasInput(from, LeftPressed: true, LeftDown: true), origin, size);
            canvas.Update(view, new CanvasInput(to, LeftDown: true), origin, size);
            canvas.Update(view, new CanvasInput(to, LeftReleased: true), origin, size);
        }

        // Rewired: the link's end dragged from its node's in pin to another's.
        var steps = doc.History.UndoCount;
        Drag(InPin(link.ToNode), InPin(other));
        var rewired = new CanvasLink(link.FromNode, 0, other, 0);
        Assert.Contains(rewired, view.Links);
        Assert.Equal(steps + 1, doc.History.UndoCount);
        Assert.True(doc.Undo());
        Assert.Contains(link, view.Links);
        Assert.DoesNotContain(rewired, view.Links);

        // Added: one step too.
        view.Connect(new CanvasLink(link.ToNode, 0, other, 0));
        Assert.Equal(steps + 1, doc.History.UndoCount);
    }
}
