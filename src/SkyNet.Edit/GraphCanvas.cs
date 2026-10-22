using System.Numerics;

namespace SkyNet.Edit;

/// <summary>Modifier and command keys the canvas reads in a frame (the window maps keyboard keys to them).</summary>
[Flags]
public enum CanvasKeys
{
    None = 0,
    Shift = 1,
    Ctrl = 2,
    Alt = 4,
    Delete = 8,
    /// <summary>F: frames the selection.</summary>
    Frame = 16,
    /// <summary>Home: fits every node.</summary>
    FitAll = 32,
}

/// <summary>One frame's pointer and keys, screen pixels. <see cref="Hovered"/>: the pointer is over the
/// canvas and not over one of its widgets (a press elsewhere starts nothing).</summary>
public readonly record struct CanvasInput(Vector2 Mouse, bool Hovered = true,
    bool LeftPressed = false, bool LeftDown = false, bool LeftReleased = false, bool LeftDouble = false,
    bool RightPressed = false, bool RightDown = false, bool RightReleased = false,
    bool MiddlePressed = false, bool MiddleDown = false,
    float Wheel = 0f, CanvasKeys Keys = CanvasKeys.None)
{
    public bool Has(CanvasKeys key) => (Keys & key) != 0;
}

/// <summary>Which nodes show all their parameter rows: the selected ones (the rest show the first, so the
/// automatic layout's rows do not overlap), or all.</summary>
public enum NodeDetail { Selected, All }

/// <summary>What the window does after a frame: nothing, open the add-node menu at a graph point, or activate a node.</summary>
public enum CanvasRequestKind { None, AddMenu, Activate }

public readonly record struct CanvasRequest(CanvasRequestKind Kind, Vector2 At = default, int Node = 0);

/// <summary>
/// The node canvas's behaviour, without ImGui (the window draws it, tests drive it with <see cref="CanvasInput"/>):
/// pan (middle or right drag), zoom (wheel, around the pointer), select (click, Shift adds, Ctrl toggles, box drag),
/// move the selection, link pins by dragging from one to another, drag a link's input end off to remove it,
/// Delete, F frames the selection, Home fits all. Each finished gesture is one <see cref="IGraphView"/> call.
/// <code>
///   press on:  out pin ─► Linking ──release on an in pin──► Connect
///              in pin with a link ─► Linking (that link's end) ──release elsewhere──► Disconnect (on a pin: Relink)
///              node ─► Moving ──release──► Move(selection, delta)
///              link ─► link selected (Alt: Disconnect)
///              empty ─► Boxing ──release──► selection = nodes in the box
/// </code>
/// </summary>
public sealed class GraphCanvas(Action<string>? log = null)
{
    // A press moves this many pixels before it counts as a drag (a right click under it opens the menu).
    private const float DragPixels = 4f;
    private const float WheelStep = 1.15f;

    private enum Mode { Idle, Moving, Linking, Boxing }

    private readonly HashSet<int> _selection = [];
    private Mode _mode;
    private Vector2 _pressScreen, _pressGraph, _lastMouse;
    private bool _panning, _panMoved;
    private CanvasPin _linkFrom;
    private CanvasLink? _detached;
    private bool _fitted;
    private IReadOnlyList<CanvasNode> _shown = [];
    private int? _pendingFocus;

    // A node not shown in full keeps this many parameter rows (its "does" line).
    private const int CompactRows = 1;

    // Focus zooms in at least this far, so the node reads.
    private const float FocusZoom = 0.8f;

    public GraphCamera Camera { get; } = new();

    public NodeDetail Detail { get; set; }

    /// <summary>The nodes as drawn and hit: the view's, those not in full cut to their first rows (see <see cref="Refresh"/>).</summary>
    public IReadOnlyList<CanvasNode> Nodes => _shown;

    /// <summary>Takes the view's nodes as they are now (before drawing).</summary>
    public void Refresh(IGraphView view)
    {
        var nodes = view.Nodes;
        _shown = Detail == NodeDetail.All ? nodes
            : [.. nodes.Select(n => n.Params.Count <= CompactRows || _selection.Contains(n.Id) ? n : n with { Params = [.. n.Params.Take(CompactRows)] })];
    }

    /// <summary>The selected nodes.</summary>
    public IReadOnlySet<int> Selection => _selection;

    /// <summary>The selected link (Delete removes it), if any.</summary>
    public CanvasLink? SelectedLink { get; private set; }

    /// <summary>While moving: how far the selection is dragged (graph units, not yet sent to the view).</summary>
    public Vector2 MoveDelta { get; private set; }

    /// <summary>While linking: the pin dragged from (the link drawn from it to the pointer).</summary>
    public CanvasPin? LinkSource => _mode == Mode.Linking ? _linkFrom : null;

    /// <summary>While linking from an existing link's end: that link (drawn as the dragged one, not in place).</summary>
    public CanvasLink? Detached => _mode == Mode.Linking ? _detached : null;

    /// <summary>While box selecting: the box, screen pixels.</summary>
    public (Vector2 A, Vector2 B)? Box => _mode == Mode.Boxing ? (_pressScreen, _lastMouse) : null;

    /// <summary>The pointer, graph units (last frame).</summary>
    public Vector2 Pointer => Camera.ToGraph(_lastMouse);

    /// <summary>Where node <paramref name="id"/> is drawn: shifted by the drag while it is being moved.</summary>
    public Vector2 Shift(int id) => _mode == Mode.Moving && _selection.Contains(id) ? MoveDelta : Vector2.Zero;

    /// <summary>Selects node <paramref name="id"/> alone and pans (and zooms, when far out) to it (a selection made in the level view).</summary>
    public void Focus(IGraphView view, int id)
    {
        if (!view.Nodes.Any(n => n.Id == id))
        {
            return;
        }

        Select(id);
        Refresh(view);
        _fitted = true;
        if (Camera.Size.X <= 0f)
        {
            // Not drawn yet: centred on the first frame that knows the canvas's size.
            _pendingFocus = id;
            return;
        }

        var n = GraphLayout.ById(_shown)[id];
        if (Camera.Zoom < FocusZoom)
        {
            Camera.ZoomAt(Camera.Origin + Camera.Size * 0.5f, FocusZoom / Camera.Zoom);
        }

        Camera.CentreOn(n.Position + GraphLayout.Size(n) * 0.5f);
    }

    /// <summary>Selects exactly <paramref name="ids"/> and frames them.</summary>
    public void Show(IGraphView view, IEnumerable<int> ids)
    {
        _selection.Clear();
        _selection.UnionWith(ids);
        SelectedLink = null;
        Frame(view, FrameScope.Selection);
    }

    /// <summary>Fits every node (Home) or the selection (F; everything when nothing is selected).</summary>
    public void Frame(IGraphView view, FrameScope scope)
    {
        Refresh(view);
        var ids = scope == FrameScope.Selection && _selection.Count > 0 ? _selection : null;
        if (GraphLayout.Bounds(_shown, ids) is { } box)
        {
            _fitted = true;
            Camera.Fit(box.Min, box.Max);
        }
    }

    /// <summary>Runs one frame of input over the canvas at <paramref name="origin"/>, <paramref name="size"/> pixels.</summary>
    public CanvasRequest Update(IGraphView view, CanvasInput input, Vector2 origin, Vector2 size)
    {
        (Camera.Origin, Camera.Size) = (origin, size);
        Prune(view);
        Refresh(view);
        if (_pendingFocus is { } focus && size.X > 0f)
        {
            _pendingFocus = null;
            Focus(view, focus);
        }

        if (!_fitted && size.X > 0f && view.Nodes.Count > 0)
        {
            _fitted = true;
            Frame(view, FrameScope.All);
        }

        var request = Pan(input);
        if (input.Hovered && input.Wheel != 0f)
        {
            Camera.ZoomAt(input.Mouse, MathF.Pow(WheelStep, input.Wheel));
        }

        if (input.LeftPressed && input.Hovered && _mode == Mode.Idle)
        {
            Press(view, input);
        }

        if (_mode == Mode.Moving && Dragged(input.Mouse))
        {
            MoveDelta = Camera.ToGraph(input.Mouse) - _pressGraph;
        }

        if (input.LeftReleased || (!input.LeftDown && _mode != Mode.Idle))
        {
            Release(view, input);
        }

        if (input.LeftDouble && input.Hovered && GraphLayout.HitNode(_shown, Camera.ToGraph(input.Mouse)) is { } hit)
        {
            request = new CanvasRequest(CanvasRequestKind.Activate, Node: hit);
        }

        Keys(view, input.Keys);
        _lastMouse = input.Mouse;
        Refresh(view);
        return request;
    }

    // Middle or right drag pans; a right click that did not move asks for the add-node menu.
    private CanvasRequest Pan(CanvasInput input)
    {
        if ((input.RightPressed || input.MiddlePressed) && input.Hovered)
        {
            // Panning counts from the press point (the pointer may have jumped since the last frame).
            (_panning, _panMoved, _pressScreen, _lastMouse) = (true, false, input.Mouse, input.Mouse);
        }

        if (_panning && (input.RightDown || input.MiddleDown))
        {
            Camera.Pan(input.Mouse - _lastMouse);
            _panMoved |= Dragged(input.Mouse);
        }

        if (!_panning || input.RightDown || input.MiddleDown)
        {
            return default;
        }

        _panning = false;
        return input.RightReleased && !_panMoved
            ? new CanvasRequest(CanvasRequestKind.AddMenu, Camera.ToGraph(_pressScreen))
            : default;
    }

    private void Press(IGraphView view, CanvasInput input)
    {
        (_pressScreen, _pressGraph) = (input.Mouse, Camera.ToGraph(input.Mouse));
        MoveDelta = Vector2.Zero;
        var nodes = _shown;

        // A pin: drag a new link from it, or an input pin's link off it.
        if (GraphLayout.HitPin(nodes, _pressGraph, Camera.Zoom) is { } pin)
        {
            (_mode, _linkFrom, _detached) = (Mode.Linking, pin, null);
            if (pin.Side == PinSide.In && LastInto(view, pin) is { } link)
            {
                (_linkFrom, _detached) = (link.From, link);
            }

            return;
        }

        if (GraphLayout.HitNode(nodes, _pressGraph) is { } id)
        {
            PressNode(id, input.Keys);
            return;
        }

        if (GraphLayout.HitLink(nodes, view.Links, _pressGraph, Camera.Zoom) is { } hit)
        {
            PressLink(view, hit, input.Keys);
            return;
        }

        _mode = Mode.Boxing;
    }

    // Ctrl toggles the node; Shift adds it; a plain press on an unselected node selects it alone. Then the selection moves.
    private void PressNode(int id, CanvasKeys keys)
    {
        SelectedLink = null;
        if ((keys & CanvasKeys.Ctrl) != 0)
        {
            if (!_selection.Remove(id))
            {
                _selection.Add(id);
            }

            return;
        }

        if (!_selection.Contains(id))
        {
            if ((keys & CanvasKeys.Shift) == 0)
            {
                _selection.Clear();
            }

            _selection.Add(id);
        }

        _mode = Mode.Moving;
    }

    // A link selected; Alt+click cuts it at once (Unreal's convention).
    private void PressLink(IGraphView view, CanvasLink link, CanvasKeys keys)
    {
        _selection.Clear();
        if ((keys & CanvasKeys.Alt) != 0)
        {
            Log(view.Disconnect(link));
            SelectedLink = null;
            return;
        }

        SelectedLink = link;
    }

    private void Release(IGraphView view, CanvasInput input)
    {
        var mode = _mode;
        _mode = Mode.Idle;
        switch (mode)
        {
            case Mode.Moving:
                EndMove(view);
                break;
            case Mode.Linking:
                EndLink(view, input.Mouse);
                break;
            case Mode.Boxing:
                EndBox(view, input);
                break;
        }
    }

    private void EndMove(IGraphView view)
    {
        var delta = MoveDelta;
        MoveDelta = Vector2.Zero;
        if (delta == Vector2.Zero || _selection.Count == 0)
        {
            return;
        }

        Log(view.Move([.. Ordered(view)], delta));
    }

    // Released on a pin of the other side: link to it. A dragged-off link end is removed unless dropped where it was.
    private void EndLink(IGraphView view, Vector2 mouse)
    {
        var target = GraphLayout.HitPin(_shown, Camera.ToGraph(mouse), Camera.Zoom);
        var valid = target is { } t && t.Side != _linkFrom.Side && t.Node != _linkFrom.Node;
        var link = valid ? CanvasLink.Between(_linkFrom, target!.Value) : (CanvasLink?)null;
        if (_detached is { } old)
        {
            _detached = null;
            if (link == old)
            {
                return;
            }

            // Dropped on another pin: moved in one edit; elsewhere: removed.
            Log(link is { } to && !view.Links.Contains(to) ? view.Relink(old, to) : view.Disconnect(old));
            return;
        }

        if (link is { } l && !view.Links.Contains(l))
        {
            Log(view.Connect(l));
        }
    }

    // A box selects the nodes it meets (Shift adds them); a click on empty canvas clears the selection.
    private void EndBox(IGraphView view, CanvasInput input)
    {
        var additive = input.Has(CanvasKeys.Shift);
        SelectedLink = null;
        if (!additive)
        {
            _selection.Clear();
        }

        if (!Dragged(input.Mouse))
        {
            return;
        }

        _selection.UnionWith(GraphLayout.InBox(_shown, _pressGraph, Camera.ToGraph(input.Mouse)));
    }

    private void Keys(IGraphView view, CanvasKeys keys)
    {
        if ((keys & CanvasKeys.Delete) != 0 && _mode == Mode.Idle)
        {
            DeleteSelected(view);
        }

        if ((keys & CanvasKeys.Frame) != 0)
        {
            Frame(view, FrameScope.Selection);
        }

        if ((keys & CanvasKeys.FitAll) != 0)
        {
            Frame(view, FrameScope.All);
        }
    }

    private void DeleteSelected(IGraphView view)
    {
        if (_selection.Count > 0)
        {
            Log(view.Delete([.. Ordered(view)]));
            _selection.Clear();
            return;
        }

        if (SelectedLink is { } link)
        {
            Log(view.Disconnect(link));
            SelectedLink = null;
        }
    }

    // Selected ids in the view's node order (calls are deterministic, whatever the set's order).
    private IEnumerable<int> Ordered(IGraphView view) => view.Nodes.Select(n => n.Id).Where(_selection.Contains);

    /// <summary>Selects node <paramref name="id"/> alone (a node just added).</summary>
    public void Select(int id)
    {
        _selection.Clear();
        _selection.Add(id);
        SelectedLink = null;
    }

    // Drops what the view no longer has (an undo took a node or link away).
    private void Prune(IGraphView view)
    {
        if (_selection.Count > 0)
        {
            _selection.IntersectWith(view.Nodes.Select(n => n.Id));
        }

        if (SelectedLink is { } link && !view.Links.Contains(link))
        {
            SelectedLink = null;
        }
    }

    private static CanvasLink? LastInto(IGraphView view, CanvasPin pin)
    {
        CanvasLink? last = null;
        foreach (var link in view.Links)
        {
            if (link.To == pin)
            {
                last = link;
            }
        }

        return last;
    }

    private bool Dragged(Vector2 mouse) => Vector2.Distance(mouse, _pressScreen) >= DragPixels;

    private void Log(string line) => log?.Invoke(line);
}

/// <summary>What Frame fits: the selection (all when none) or every node.</summary>
public enum FrameScope { Selection, All }
