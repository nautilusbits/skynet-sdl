using System.Numerics;

namespace SkyNet.Edit.Tests;

/// <summary>The node canvas (editor-tools plan, T8) on a fake graph: pan/zoom math, hit tests, and that each
/// gesture ends in the right single edit call.</summary>
public sealed class GraphCanvasTests
{
    private static readonly Vector2 Origin = new(100f, 50f), Size = new(1200f, 800f);
    private static readonly Vector4 White = Vector4.One;

    private readonly FakeGraph _graph = new();
    private readonly GraphCanvas _canvas = new();

    public GraphCanvasTests()
    {
        _graph.Nodes.Add(Node(1, new Vector2(0f, 0f)));
        _graph.Nodes.Add(Node(2, new Vector2(400f, 0f)));
        _graph.Nodes.Add(Node(3, new Vector2(400f, 300f)));
        _graph.Links.Add(new CanvasLink(1, 0, 2, 0));
        Frame(new CanvasInput(Origin));
    }

    private static CanvasNode Node(int id, Vector2 at) =>
        new(id, $"node {id}", White, at, ["in"], ["next"], [new CanvasParam("p", "p", ParamKind.Number, 0)]);

    private CanvasNode N(int id) => _graph.Nodes.Single(n => n.Id == id);

    private Vector2 Screen(Vector2 graph) => _canvas.Camera.ToScreen(graph);

    private Vector2 PinAt(int id, PinSide side) => Screen(GraphLayout.Pin(N(id), side, 0));

    private Vector2 Middle(int id) => Screen(N(id).Position + GraphLayout.Size(N(id)) * 0.5f);

    private CanvasRequest Frame(CanvasInput input) => _canvas.Update(_graph, input, Origin, Size);

    // A left drag in three frames: press, move, release.
    private void Drag(Vector2 from, Vector2 to, CanvasKeys keys = CanvasKeys.None)
    {
        Frame(new CanvasInput(from, LeftPressed: true, LeftDown: true, Keys: keys));
        Frame(new CanvasInput(to, LeftDown: true, Keys: keys));
        Frame(new CanvasInput(to, LeftReleased: true, Keys: keys));
    }

    private void Click(Vector2 at, CanvasKeys keys = CanvasKeys.None) => Drag(at, at, keys);

    // ---- math ----

    [Fact]
    public void Screen_and_graph_points_round_trip_at_any_pan_and_zoom()
    {
        var cam = new GraphCamera { Origin = Origin, Size = Size, Offset = new Vector2(37f, -12f) };
        cam.ZoomAt(new Vector2(300f, 200f), 1.7f);
        var p = new Vector2(123f, -45f);

        Assert.Equal(p.X, cam.ToGraph(cam.ToScreen(p)).X, 3);
        Assert.Equal(p.Y, cam.ToGraph(cam.ToScreen(p)).Y, 3);
    }

    [Fact]
    public void Zoom_keeps_the_point_under_the_cursor_and_stays_in_its_limits()
    {
        var cam = new GraphCamera { Origin = Origin, Size = Size };
        var cursor = new Vector2(640f, 333f);
        var before = cam.ToGraph(cursor);
        cam.ZoomAt(cursor, 2f);

        Assert.Equal(2f, cam.Zoom);
        Assert.True(Vector2.Distance(before, cam.ToGraph(cursor)) < 1e-3f);

        cam.ZoomAt(cursor, 100f);
        Assert.Equal(GraphCamera.MaxZoom, cam.Zoom);
        cam.ZoomAt(cursor, 1e-4f);
        Assert.Equal(GraphCamera.MinZoom, cam.Zoom);
    }

    [Fact]
    public void Fit_centres_the_box_and_shows_all_of_it()
    {
        var cam = new GraphCamera { Origin = Origin, Size = Size };
        cam.Fit(new Vector2(-500f, 0f), new Vector2(1500f, 400f));

        var centre = cam.ToScreen(new Vector2(500f, 200f));
        Assert.Equal(Origin.X + Size.X / 2f, centre.X, 2);
        Assert.Equal(Origin.Y + Size.Y / 2f, centre.Y, 2);
        Assert.True(cam.ToScreen(new Vector2(-500f, 0f)).X >= Origin.X);
        Assert.True(cam.ToScreen(new Vector2(1500f, 400f)).X <= Origin.X + Size.X);
    }

    [Fact]
    public void Pins_sit_on_the_node_edges_and_are_hit_near_their_centre()
    {
        var n = N(2);
        Assert.Equal(new Vector2(400f, GraphLayout.Header + GraphLayout.Row / 2f), GraphLayout.Pin(n, PinSide.In, 0));
        Assert.Equal(400f + GraphLayout.Width, GraphLayout.Pin(n, PinSide.Out, 0).X);

        var pin = GraphLayout.Pin(n, PinSide.Out, 0);
        Assert.Equal(new CanvasPin(2, PinSide.Out, 0), GraphLayout.HitPin(_graph.Nodes, pin + new Vector2(3f, 2f), 1f));
        Assert.Null(GraphLayout.HitPin(_graph.Nodes, pin + new Vector2(30f, 0f), 1f));
    }

    [Fact]
    public void Node_hit_takes_the_topmost_and_link_hit_follows_the_curve()
    {
        _graph.Nodes.Add(Node(4, new Vector2(10f, 10f)));
        Assert.Equal(4, GraphLayout.HitNode(_graph.Nodes, new Vector2(20f, 20f)));
        Assert.Null(GraphLayout.HitNode(_graph.Nodes, new Vector2(-20f, 20f)));

        var curve = GraphLayout.Curve(GraphLayout.Pin(N(1), PinSide.Out, 0), GraphLayout.Pin(N(2), PinSide.In, 0));
        var mid = GraphLayout.At(curve, 0.5f);
        Assert.Equal(new CanvasLink(1, 0, 2, 0), GraphLayout.HitLink(_graph.Nodes, _graph.Links, mid + new Vector2(0f, 3f), 1f));
        Assert.Null(GraphLayout.HitLink(_graph.Nodes, _graph.Links, mid + new Vector2(0f, 40f), 1f));
    }

    [Fact]
    public void A_box_takes_the_nodes_it_meets_whichever_way_it_was_dragged()
    {
        Assert.Equal([2, 3], GraphLayout.InBox(_graph.Nodes, new Vector2(700f, 600f), new Vector2(390f, 10f)));
        Assert.Empty(GraphLayout.InBox(_graph.Nodes, new Vector2(-100f, 500f), new Vector2(-50f, 600f)));
    }

    // ---- gestures ----

    [Fact]
    public void Dragging_from_an_out_pin_to_an_in_pin_connects_them()
    {
        Drag(PinAt(2, PinSide.Out), PinAt(3, PinSide.In));

        Assert.Equal(["connect 2.0->3.0"], _graph.Calls);
    }

    [Fact]
    public void Dragging_from_an_in_pin_to_an_out_pin_connects_them_the_right_way_round()
    {
        Drag(PinAt(3, PinSide.In), PinAt(2, PinSide.Out));

        Assert.Equal(["connect 2.0->3.0"], _graph.Calls);
    }

    [Fact]
    public void A_link_onto_its_own_node_or_the_same_side_is_not_made()
    {
        Drag(PinAt(2, PinSide.Out), PinAt(2, PinSide.In));
        Drag(PinAt(1, PinSide.Out), PinAt(2, PinSide.Out));

        Assert.Empty(_graph.Calls);
    }

    [Fact]
    public void Dragging_a_link_end_off_its_pin_removes_it()
    {
        Drag(PinAt(2, PinSide.In), Screen(new Vector2(200f, 600f)));

        Assert.Equal(["disconnect 1.0->2.0"], _graph.Calls);
    }

    [Fact]
    public void Dragging_a_link_end_to_another_pin_rewires_it()
    {
        Drag(PinAt(2, PinSide.In), PinAt(3, PinSide.In));

        Assert.Equal(["relink 1.0->2.0 to 1.0->3.0"], _graph.Calls);
    }

    [Fact]
    public void Dropping_a_link_end_back_where_it_was_changes_nothing()
    {
        Drag(PinAt(2, PinSide.In), PinAt(2, PinSide.In));

        Assert.Empty(_graph.Calls);
    }

    [Fact]
    public void A_node_drag_is_one_move_of_the_selection_on_release()
    {
        Click(Middle(2));
        Click(Middle(3), CanvasKeys.Shift);
        var from = Middle(3);
        var to = from + new Vector2(50f, 20f) * _canvas.Camera.Zoom;

        Frame(new CanvasInput(from, LeftPressed: true, LeftDown: true));
        Frame(new CanvasInput(to, LeftDown: true));
        Assert.Empty(_graph.Calls);
        Assert.Equal(new Vector2(50f, 20f), Round(_canvas.Shift(2)));
        Frame(new CanvasInput(to, LeftReleased: true));

        Assert.Equal(["move 2,3 by 50,20"], _graph.Calls);
        Assert.Equal(Vector2.Zero, _canvas.Shift(2));
    }

    [Fact]
    public void A_click_selects_one_node_shift_adds_ctrl_toggles_and_empty_clears()
    {
        Click(Middle(1));
        Assert.Equal([1], _canvas.Selection);
        Click(Middle(2), CanvasKeys.Shift);
        Assert.Equal([1, 2], _canvas.Selection.Order());
        Click(Middle(1), CanvasKeys.Ctrl);
        Assert.Equal([2], _canvas.Selection);
        Click(Screen(new Vector2(-300f, 700f)));

        Assert.Empty(_canvas.Selection);
        Assert.Empty(_graph.Calls);
    }

    [Fact]
    public void A_box_drag_on_empty_canvas_selects_what_it_meets()
    {
        Drag(Screen(new Vector2(380f, -40f)), Screen(new Vector2(700f, 700f)));

        Assert.Equal([2, 3], _canvas.Selection.Order());
        Assert.Empty(_graph.Calls);
    }

    [Fact]
    public void Delete_removes_the_selected_nodes_else_the_selected_link()
    {
        Drag(Screen(new Vector2(380f, -40f)), Screen(new Vector2(700f, 700f)));
        Frame(new CanvasInput(Origin, Keys: CanvasKeys.Delete));
        Assert.Equal(["delete 2,3"], _graph.Calls);

        _graph.Calls.Clear();
        var curve = GraphLayout.Curve(GraphLayout.Pin(N(1), PinSide.Out, 0), GraphLayout.Pin(N(2), PinSide.In, 0));
        Click(Screen(GraphLayout.At(curve, 0.5f)));
        Assert.Equal(new CanvasLink(1, 0, 2, 0), _canvas.SelectedLink);
        Frame(new CanvasInput(Origin, Keys: CanvasKeys.Delete));

        Assert.Equal(["disconnect 1.0->2.0"], _graph.Calls);
    }

    [Fact]
    public void Alt_click_on_a_link_cuts_it()
    {
        var curve = GraphLayout.Curve(GraphLayout.Pin(N(1), PinSide.Out, 0), GraphLayout.Pin(N(2), PinSide.In, 0));
        Click(Screen(GraphLayout.At(curve, 0.5f)), CanvasKeys.Alt);

        Assert.Equal(["disconnect 1.0->2.0"], _graph.Calls);
    }

    [Fact]
    public void A_right_click_asks_for_the_add_menu_and_a_right_drag_pans()
    {
        var at = Screen(new Vector2(900f, 500f));
        Frame(new CanvasInput(at, RightPressed: true, RightDown: true));
        var menu = Frame(new CanvasInput(at, RightReleased: true));
        Assert.Equal(CanvasRequestKind.AddMenu, menu.Kind);
        Assert.True(Vector2.Distance(new Vector2(900f, 500f), menu.At) < 0.01f);

        var offset = _canvas.Camera.Offset;
        Frame(new CanvasInput(at, RightPressed: true, RightDown: true));
        Frame(new CanvasInput(at + new Vector2(80f, -30f), RightDown: true));
        var none = Frame(new CanvasInput(at + new Vector2(80f, -30f), RightReleased: true));

        Assert.Equal(CanvasRequestKind.None, none.Kind);
        Assert.Equal(offset + new Vector2(80f, -30f), _canvas.Camera.Offset);
    }

    [Fact]
    public void Middle_drag_pans_and_the_wheel_zooms_around_the_pointer()
    {
        var at = new Vector2(500f, 400f);
        var offset = _canvas.Camera.Offset;
        Frame(new CanvasInput(at, MiddlePressed: true, MiddleDown: true));
        Frame(new CanvasInput(at + new Vector2(-40f, 25f), MiddleDown: true));
        Frame(new CanvasInput(at + new Vector2(-40f, 25f)));
        Assert.Equal(offset + new Vector2(-40f, 25f), _canvas.Camera.Offset);

        var under = _canvas.Camera.ToGraph(at);
        var zoom = _canvas.Camera.Zoom;
        Frame(new CanvasInput(at, Wheel: 2f));

        Assert.True(_canvas.Camera.Zoom > zoom);
        Assert.True(Vector2.Distance(under, _canvas.Camera.ToGraph(at)) < 1e-2f);
    }

    [Fact]
    public void Double_click_activates_the_node_under_the_pointer()
    {
        var request = Frame(new CanvasInput(Middle(3), LeftPressed: true, LeftDown: true, LeftDouble: true));

        Assert.Equal(new CanvasRequest(CanvasRequestKind.Activate, Node: 3), request);
    }

    [Fact]
    public void Focus_selects_the_node_and_centres_it()
    {
        _canvas.Focus(_graph, 3);

        Assert.Equal([3], _canvas.Selection);
        Assert.True(Vector2.Distance(Middle(3), Origin + Size * 0.5f) < 0.01f);
    }

    [Fact]
    public void Frame_fits_the_selection_and_home_fits_everything()
    {
        Click(Middle(3));
        Frame(new CanvasInput(Origin, Keys: CanvasKeys.Frame));
        Assert.True(Vector2.Distance(Middle(3), Origin + Size * 0.5f) < 0.01f);

        Frame(new CanvasInput(Origin, Keys: CanvasKeys.FitAll));
        var all = GraphLayout.Bounds(_graph.Nodes)!.Value;
        Assert.True(Vector2.Distance(Screen((all.Min + all.Max) * 0.5f), Origin + Size * 0.5f) < 0.01f);
    }

    [Fact]
    public void A_node_the_view_no_longer_has_leaves_the_selection()
    {
        Click(Middle(3));
        _graph.Nodes.RemoveAll(n => n.Id == 3);
        Frame(new CanvasInput(Origin));

        Assert.Empty(_canvas.Selection);
    }

    [Fact]
    public void Presses_off_the_canvas_start_nothing()
    {
        Frame(new CanvasInput(PinAt(2, PinSide.Out), Hovered: false, LeftPressed: true, LeftDown: true));
        Frame(new CanvasInput(PinAt(3, PinSide.In), Hovered: false, LeftReleased: true));

        Assert.Empty(_graph.Calls);
        Assert.Null(_canvas.LinkSource);
    }

    private static Vector2 Round(Vector2 v) => new(MathF.Round(v.X, 2), MathF.Round(v.Y, 2));

    // A graph that records the calls the canvas makes.
    private sealed class FakeGraph : IGraphView
    {
        public List<CanvasNode> Nodes { get; } = [];

        public List<CanvasLink> Links { get; } = [];

        public List<string> Calls { get; } = [];

        IReadOnlyList<CanvasNode> IGraphView.Nodes => Nodes;

        IReadOnlyList<CanvasLink> IGraphView.Links => Links;

        public IReadOnlyList<CanvasIssue> Issues => [];

        public IReadOnlyList<CanvasNodeKind> Kinds => [new("relay", "relay", "logic")];

        public string Connect(CanvasLink link) => Log($"connect {Text(link)}");

        public string Disconnect(CanvasLink link) => Log($"disconnect {Text(link)}");

        public string Relink(CanvasLink old, CanvasLink link) => Log($"relink {Text(old)} to {Text(link)}");

        public string Move(IReadOnlyList<int> nodes, Vector2 delta) =>
            Log($"move {string.Join(",", nodes)} by {MathF.Round(delta.X, 2)},{MathF.Round(delta.Y, 2)}");

        public string Delete(IReadOnlyList<int> nodes) => Log($"delete {string.Join(",", nodes)}");

        public int? Add(string kind, Vector2 at)
        {
            Log($"add {kind}");
            return 99;
        }

        public string SetParam(int node, string key, long value) => Log($"set {node}.{key}={value}");

        public IReadOnlyList<ParamChoice> Targets(int node, string key) => [];

        public IReadOnlyList<CanvasChain> Verify() => [];

        private static string Text(CanvasLink l) => $"{l.FromNode}.{l.FromPin}->{l.ToNode}.{l.ToPin}";

        private string Log(string call)
        {
            Calls.Add(call);
            return call;
        }
    }
}
