using System.Globalization;
using System.Numerics;
using Hexa.NET.ImGui;
using RetroEng.Commands;

namespace SkyNet.Edit;

/// <summary>
/// View > Trigger graph (editor-tools plan, T8): the shown level's trigger chains as a node graph
/// (<see cref="GraphCanvas"/> over an <see cref="IGraphView"/>), Blueprint-style. Right-click: add a node;
/// drag pins to link; double-click a node: select its entity in the level view. The side panel lists the
/// validation issues and the Verify results per chain.
/// <code>
///   ┌ [Fit all] [Frame] [Verify]  100%  42 nodes ───────────┬ Issues (2) ────────┐
///   │  ┌──────────┐      ┌──────────┐                       │ ! @1234 no next    │
///   │  │ switch   ●─────►● door     │                       ├ Verify ────────────┤
///   │  └──────────┘      └──────────┘                       │ PASS chain @1234   │
///   └───────────────────────────────────────────────────────┴────────────────────┘
/// </code>
/// </summary>
public sealed class TriggerGraphWindow(Func<IGraphView?> view, Action<string> log)
{
    public const string Title = "Trigger graph";
    public const string WindowId = "###TriggerGraph";

    /// <summary>The drag and drop payload of an Outliner row of the shown level: the entity's key (an int).</summary>
    public const string EntityPayload = "SKYNET_ENTITY";

    private const string AddPopup = "##graphAdd";
    private const float SideWidth = 280f, GridStep = 32f, MinGridPixels = 12f;
    private const int GridMajor = 8, GridSpread = 4;

    // Node text is this tall (graph units); rows below MinWidgetZoom show text, below MinTextZoom only titles.
    private const float TextHeight = 14f, MinWidgetZoom = 0.55f, MinTextZoom = 0.3f, Rounding = 6f;
    private const float LabelShare = 0.42f, Inset = 8f, IssueThickness = 2.5f, SelectThickness = 2f, LinkThickness = 2.5f;

    private static readonly Vector4 Background = new(0.09f, 0.09f, 0.1f, 1f), GridMinor = new(1f, 1f, 1f, 0.04f),
        GridMajorColour = new(1f, 1f, 1f, 0.09f), Body = new(0.14f, 0.14f, 0.16f, 0.96f), Text = new(0.92f, 0.92f, 0.92f, 1f),
        Dim = new(0.65f, 0.65f, 0.68f, 1f), Selected = new(1f, 0.8f, 0.2f, 1f), Edge = new(0f, 0f, 0f, 0.7f),
        Error = new(1f, 0.25f, 0.25f, 1f), Warning = new(1f, 0.65f, 0.15f, 1f), Pass = new(0.35f, 0.9f, 0.4f, 1f),
        BoxFill = new(0.3f, 0.55f, 1f, 0.12f), BoxEdge = new(0.3f, 0.55f, 1f, 0.8f);

    private readonly GraphCanvas _canvas = new(log);
    private Vector2 _addAt;
    private string _addFilter = "", _pickFilter = "";
    private (int Node, string Key)? _editing;
    private int _editValue;
    // A field's Pick: the next entity clicked in the level view sets it.
    private (int Node, string Key)? _pick;

    /// <summary>The canvas (selection, camera).</summary>
    public GraphCanvas Canvas => _canvas;

    /// <summary>The last Verify's results; null before the first.</summary>
    public IReadOnlyList<CanvasChain>? Results { get; private set; }

    /// <summary>Raised on a node's double-click: the app selects its entity in the level view.</summary>
    public event Action<int>? Activated;

    /// <summary>Raised when a command shows the window.</summary>
    public event Action? Shown;

    /// <summary>Selects node <paramref name="node"/> and pans to it (the level view's selection).</summary>
    public void Focus(int node)
    {
        if (view() is { } v)
        {
            _canvas.Focus(v, node);
        }
    }

    /// <summary>True while a field's Pick waits for an entity clicked in the level view.</summary>
    public bool Picking => _pick != null;

    /// <summary>Starts a Pick of entity field <paramref name="key"/> of node <paramref name="node"/>.</summary>
    public void BeginPick(int node, string key) => _pick = (node, key);

    /// <summary>Ends a Pick without a change (Esc).</summary>
    public void CancelPick() => _pick = null;

    /// <summary>The level view's click on <paramref name="entity"/> during a Pick: the field set (one undo step), the
    /// Pick over; the log line, null when no Pick was on.</summary>
    public string? Pick(int entity)
    {
        if (_pick is not { } p)
        {
            return null;
        }

        _pick = null;
        return Drop(p.Node, p.Key, entity);
    }

    /// <summary>Sets entity field <paramref name="key"/> of <paramref name="node"/> to <paramref name="entity"/> (an
    /// Outliner row dropped on it): one undo step; the log line.</summary>
    public string Drop(int node, string key, int entity) =>
        view() is { } v ? v.SetParam(node, key, entity) : "Trigger graph: no level";

    /// <summary>Runs Verify; a summary line.</summary>
    public string Verify()
    {
        if (view() is not { } v)
        {
            return "Trigger graph: no level";
        }

        Results = v.Verify();
        var failed = Results.Count(r => !r.Passed);
        return $"Trigger graph: {Results.Count} chains verified, {failed} failed";
    }

    /// <summary>Adds the graph.* commands to <paramref name="registry"/>.</summary>
    public void Register(CommandRegistry registry)
    {
        registry.Add("graph.open", "graph.open", _ => Open(), "shows the Trigger graph window");
        registry.Add("graph.verify", "graph.verify", _ => Verify(), "verifies the shown level's trigger chains");
        registry.Add("graph.focus", "graph.focus [key]", a => FocusCommand(a.OptionalWord(0)),
            "selects a node (its entity key; default the first linked one) and zooms to it");
    }

    private string FocusCommand(string? word)
    {
        if (view() is not { } v)
        {
            return "Trigger graph: no level";
        }

        int? key = int.TryParse(word, NumberStyles.Integer, CultureInfo.InvariantCulture, out var k) ? k
            : v.Links.Count > 0 ? v.Links[0].FromNode : null;
        if (key is not { } id || !v.Nodes.Any(n => n.Id == id))
        {
            return $"Trigger graph: no node {word}";
        }

        Shown?.Invoke();
        _canvas.Focus(v, id);
        return $"Trigger graph: node {id}";
    }

    private string Open()
    {
        Shown?.Invoke();
        return view() is { } v ? $"Trigger graph: {v.Nodes.Count} nodes, {v.Links.Count} links" : "Trigger graph: no level";
    }

    /// <summary>Draws the window; <paramref name="open"/> false when closed.</summary>
    public void Draw(ref bool open)
    {
        if (!ImGui.Begin(Title + WindowId, ref open, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
            ImGui.End();
            return;
        }

        var v = view();
        if (v == null)
        {
            ImGui.TextDisabled("No level shown.");
            ImGui.End();
            return;
        }

        // Esc ends a Pick while the graph has the keyboard (the level view's Esc does the same there).
        if (_pick != null && ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows) && ImGui.IsKeyPressed(ImGuiKey.Escape, false))
        {
            CancelPick();
        }

        Toolbar(v);
        var avail = ImGui.GetContentRegionAvail();
        CanvasArea(v, new Vector2(MathF.Max(avail.X - SideWidth, 64f), avail.Y));
        ImGui.SameLine();
        Side(v);
        ImGui.End();
    }

    private void Toolbar(IGraphView v)
    {
        if (ImGui.Button("Fit all"))
        {
            _canvas.Frame(v, FrameScope.All);
        }

        ImGui.SameLine();
        if (ImGui.Button("Frame"))
        {
            _canvas.Frame(v, FrameScope.Selection);
        }

        ImGui.SameLine();
        var all = _canvas.Detail == NodeDetail.All;
        if (ImGui.Checkbox("All rows", ref all))
        {
            _canvas.Detail = all ? NodeDetail.All : NodeDetail.Selected;
        }

        ImGui.SameLine();
        if (ImGui.Button("Verify"))
        {
            log(Verify());
        }

        ImGui.SameLine();
        if (_pick is { } pick)
        {
            ImGui.TextColored(Selected, string.Create(CultureInfo.InvariantCulture, $"@{pick.Node} {pick.Key}: click an entity in the view (Esc cancels)"));
            return;
        }

        ImGui.TextDisabled(string.Create(CultureInfo.InvariantCulture,
            $"{_canvas.Camera.Zoom * 100f:0}%%  {v.Nodes.Count} nodes, {v.Links.Count} links  (Home fit, F frame, RMB add/pan, Alt+click cuts)"));
    }

    // ---- the canvas ----

    private void CanvasArea(IGraphView v, Vector2 size)
    {
        const ImGuiWindowFlags flags = ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoMove;
        if (!ImGui.BeginChild("##graphCanvas", size, ImGuiChildFlags.Borders, flags))
        {
            ImGui.EndChild();
            return;
        }

        var origin = ImGui.GetCursorScreenPos();
        var area = ImGui.GetContentRegionAvail();
        (_canvas.Camera.Origin, _canvas.Camera.Size) = (origin, area);
        var dl = ImGui.GetWindowDrawList();
        dl.AddRectFilled(origin, origin + area, Colour(Background));
        Grid(dl, origin, area);

        _canvas.Refresh(v);
        var nodes = _canvas.Nodes;
        var byId = GraphLayout.ById(nodes);
        var issues = v.Issues.ToLookup(i => i.Node);
        Links(dl, v, byId);
        foreach (var n in nodes.Where(n => !_canvas.Selection.Contains(n.Id)).Concat(nodes.Where(n => _canvas.Selection.Contains(n.Id))))
        {
            Node(dl, v, n, issues[n.Id].ToList(), origin, area);
        }

        Overlay(dl, v, byId);
        var request = _canvas.Update(v, Input(), origin, area);
        Request(v, request);
        AddMenu(v);
        ImGui.EndChild();
    }

    // This frame's pointer and keys; keys only while the canvas has focus (and then not the level view's: Home there flies home).
    private static CanvasInput Input()
    {
        var io = ImGui.GetIO();
        var hovered = ImGui.IsWindowHovered() && !ImGui.IsAnyItemHovered() && !ImGui.IsAnyItemActive();
        var keys = CanvasKeys.None;
        if (ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows) && !io.WantTextInput)
        {
            ImGui.SetNextFrameWantCaptureKeyboard(true);
            keys |= io.KeyShift ? CanvasKeys.Shift : 0;
            keys |= io.KeyCtrl ? CanvasKeys.Ctrl : 0;
            keys |= io.KeyAlt ? CanvasKeys.Alt : 0;
            keys |= ImGui.IsKeyPressed(ImGuiKey.Delete, false) ? CanvasKeys.Delete : 0;
            keys |= ImGui.IsKeyPressed(ImGuiKey.F, false) && !io.KeyCtrl ? CanvasKeys.Frame : 0;
            keys |= ImGui.IsKeyPressed(ImGuiKey.Home, false) ? CanvasKeys.FitAll : 0;
        }

        return new CanvasInput(io.MousePos, hovered,
            ImGui.IsMouseClicked(ImGuiMouseButton.Left), ImGui.IsMouseDown(ImGuiMouseButton.Left),
            ImGui.IsMouseReleased(ImGuiMouseButton.Left), ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left),
            ImGui.IsMouseClicked(ImGuiMouseButton.Right), ImGui.IsMouseDown(ImGuiMouseButton.Right),
            ImGui.IsMouseReleased(ImGuiMouseButton.Right),
            ImGui.IsMouseClicked(ImGuiMouseButton.Middle), ImGui.IsMouseDown(ImGuiMouseButton.Middle),
            hovered ? io.MouseWheel : 0f, keys);
    }

    private void Request(IGraphView v, CanvasRequest request)
    {
        switch (request.Kind)
        {
            case CanvasRequestKind.AddMenu:
                (_addAt, _addFilter) = (request.At, "");
                ImGui.OpenPopup(AddPopup);
                break;
            case CanvasRequestKind.Activate:
                Activated?.Invoke(request.Node);
                break;
        }
    }

    // Lines every GridStep units, every GridMajor-th brighter; spread out when zoomed far out.
    private void Grid(ImDrawListPtr dl, Vector2 origin, Vector2 size)
    {
        var step = GridStep * _canvas.Camera.Zoom;
        var major = GridMajor;
        while (step < MinGridPixels)
        {
            (step, major) = (step * GridSpread, Math.Max(major / GridSpread, 1));
        }

        var start = _canvas.Camera.ToGraph(origin) / (step / _canvas.Camera.Zoom);
        var (first, firstY) = ((int)MathF.Floor(start.X), (int)MathF.Floor(start.Y));
        for (var i = first; ; i++)
        {
            var x = _canvas.Camera.ToScreen(new Vector2(i * step / _canvas.Camera.Zoom, 0f)).X;
            if (x > origin.X + size.X)
            {
                break;
            }

            dl.AddLine(new Vector2(x, origin.Y), new Vector2(x, origin.Y + size.Y), Colour(i % major == 0 ? GridMajorColour : GridMinor));
        }

        for (var i = firstY; ; i++)
        {
            var y = _canvas.Camera.ToScreen(new Vector2(0f, i * step / _canvas.Camera.Zoom)).Y;
            if (y > origin.Y + size.Y)
            {
                break;
            }

            dl.AddLine(new Vector2(origin.X, y), new Vector2(origin.X + size.X, y), Colour(i % major == 0 ? GridMajorColour : GridMinor));
        }
    }

    // Each link as a curve in its source node's colour; the selected one white; the one being dragged off left out.
    private void Links(ImDrawListPtr dl, IGraphView v, Dictionary<int, CanvasNode> byId)
    {
        var zoom = _canvas.Camera.Zoom;
        foreach (var link in v.Links)
        {
            if (link == _canvas.Detached || !byId.TryGetValue(link.FromNode, out var from) || !byId.TryGetValue(link.ToNode, out var to))
            {
                continue;
            }

            var a = GraphLayout.Pin(from, PinSide.Out, link.FromPin, _canvas.Shift(from.Id));
            var b = GraphLayout.Pin(to, PinSide.In, link.ToPin, _canvas.Shift(to.Id));
            var picked = link == _canvas.SelectedLink;
            Curve(dl, a, b, picked ? Text : from.Colour, LinkThickness * zoom * (picked ? 1.6f : 1f));
        }
    }

    private void Curve(ImDrawListPtr dl, Vector2 a, Vector2 b, Vector4 colour, float thickness)
    {
        var c = GraphLayout.Curve(a, b);
        var cam = _canvas.Camera;
        dl.AddBezierCubic(cam.ToScreen(c.A), cam.ToScreen(c.B), cam.ToScreen(c.C), cam.ToScreen(c.D), Colour(colour), MathF.Max(thickness, 1f));
    }

    // The link being dragged and the selection box, over the nodes.
    private void Overlay(ImDrawListPtr dl, IGraphView v, Dictionary<int, CanvasNode> byId)
    {
        if (_canvas.LinkSource is { } pin && byId.TryGetValue(pin.Node, out var n))
        {
            var at = GraphLayout.Pin(n, pin.Side, pin.Index);
            var (a, b) = pin.Side == PinSide.Out ? (at, _canvas.Pointer) : (_canvas.Pointer, at);
            Curve(dl, a, b, Text, LinkThickness * _canvas.Camera.Zoom);
        }

        if (_canvas.Box is { } box)
        {
            var (min, max) = (Vector2.Min(box.A, box.B), Vector2.Max(box.A, box.B));
            dl.AddRectFilled(min, max, Colour(BoxFill));
            dl.AddRect(min, max, Colour(BoxEdge));
        }
    }

    // ---- a node ----

    private void Node(ImDrawListPtr dl, IGraphView v, CanvasNode n, List<CanvasIssue> issues, Vector2 origin, Vector2 area)
    {
        var cam = _canvas.Camera;
        var zoom = cam.Zoom;
        var shift = _canvas.Shift(n.Id);
        var min = cam.ToScreen(n.Position + shift);
        var max = min + GraphLayout.Size(n) * zoom;
        if (max.X < origin.X || max.Y < origin.Y || min.X > origin.X + area.X || min.Y > origin.Y + area.Y)
        {
            return;
        }

        var rounding = Rounding * zoom;
        dl.AddRectFilled(min, max, Colour(Body), rounding);
        dl.AddRectFilled(min, new Vector2(max.X, min.Y + GraphLayout.Header * zoom), Colour(n.Colour * new Vector4(0.6f, 0.6f, 0.6f, 1f)), rounding, ImDrawFlags.RoundCornersTop);
        Outline(dl, n, issues, min, max, rounding);

        ImGui.PushFont(ImGui.GetFont(), TextHeight * zoom / MathF.Max(ImGui.GetStyle().FontScaleMain, 0.01f));
        dl.AddText(min + new Vector2(Inset, (GraphLayout.Header - TextHeight) * 0.5f) * zoom, Colour(Text), n.Title);
        Pins(dl, v, n, shift);
        if (zoom >= MinTextZoom)
        {
            // Long values end at the node's edge.
            ImGui.PushClipRect(min, max, true);
            Params(dl, v, n, shift);
            ImGui.PopClipRect();
        }

        ImGui.PopFont();
        if (issues.Count > 0 && ImGui.IsWindowHovered() && ImGui.IsMouseHoveringRect(min, max))
        {
            Tooltip(string.Join("\n", issues.Select(i => (i.Level == IssueLevel.Error ? "error: " : "warning: ") + i.Text)));
        }
    }

    // Red (error) or amber (warning) where validation found something, else yellow when selected, else a dark edge.
    private void Outline(ImDrawListPtr dl, CanvasNode n, List<CanvasIssue> issues, Vector2 min, Vector2 max, float rounding)
    {
        var zoom = _canvas.Camera.Zoom;
        if (_canvas.Selection.Contains(n.Id))
        {
            var grow = new Vector2(IssueThickness * zoom + 1f);
            dl.AddRect(min - grow, max + grow, Colour(Selected), rounding, ImDrawFlags.None, SelectThickness * MathF.Max(zoom, 0.5f));
        }

        if (issues.Count == 0)
        {
            dl.AddRect(min, max, Colour(Edge), rounding);
            return;
        }

        var colour = issues.Any(i => i.Level == IssueLevel.Error) ? Error : Warning;
        dl.AddRect(min, max, Colour(colour), rounding, ImDrawFlags.None, IssueThickness * MathF.Max(zoom, 0.5f));
    }

    // Pins as dots on the edges (filled when linked), their labels inside.
    private void Pins(ImDrawListPtr dl, IGraphView v, CanvasNode n, Vector2 shift)
    {
        var cam = _canvas.Camera;
        var radius = GraphLayout.PinRadius * cam.Zoom;
        for (var side = 0; side < 2; side++)
        {
            var s = (PinSide)side;
            var labels = s == PinSide.In ? n.Inputs : n.Outputs;
            for (var k = 0; k < labels.Count; k++)
            {
                var at = cam.ToScreen(GraphLayout.Pin(n, s, k, shift));
                var pin = new CanvasPin(n.Id, s, k);
                var linked = v.Links.Any(l => l.From == pin || l.To == pin);
                if (linked)
                {
                    dl.AddCircleFilled(at, radius, Colour(n.Colour));
                }
                else
                {
                    dl.AddCircle(at, radius, Colour(n.Colour), 0, MathF.Max(1.5f * cam.Zoom, 1f));
                }

                if (cam.Zoom < MinTextZoom)
                {
                    continue;
                }

                var width = ImGui.CalcTextSize(labels[k]).X;
                var x = s == PinSide.In ? at.X + radius + Inset * cam.Zoom : at.X - radius - Inset * cam.Zoom - width;
                dl.AddText(new Vector2(x, at.Y - TextHeight * cam.Zoom * 0.5f), Colour(Dim), labels[k]);
            }
        }
    }

    // The parameter rows: label left, value right (a widget when near enough to use one).
    private void Params(ImDrawListPtr dl, IGraphView v, CanvasNode n, Vector2 shift)
    {
        var cam = _canvas.Camera;
        var zoom = cam.Zoom;
        var labelWidth = GraphLayout.Width * LabelShare * zoom;
        var fieldWidth = (GraphLayout.Width - Inset * 2f) * zoom - labelWidth;
        for (var i = 0; i < n.Params.Count; i++)
        {
            var p = n.Params[i];
            var row = cam.ToScreen(GraphLayout.ParamRow(n, i, shift));
            var textY = row.Y + (GraphLayout.Row - TextHeight) * 0.5f * zoom;
            dl.AddText(new Vector2(row.X + Inset * zoom, textY), Colour(Dim), p.Label);
            var field = new Vector2(row.X + Inset * zoom + labelWidth, row.Y);
            if (zoom < MinWidgetZoom || p.Kind == ParamKind.Label)
            {
                dl.AddText(new Vector2(field.X, textY), Colour(Text), ValueText(p));
                continue;
            }

            ImGui.PushID(n.Id);
            ImGui.PushID(i);
            ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(4f, (GraphLayout.Row - TextHeight) * 0.5f - 1f) * zoom);
            ImGui.SetCursorScreenPos(field);
            ImGui.SetNextItemWidth(fieldWidth);
            Field(v, n, p);
            ImGui.PopStyleVar();
            ImGui.PopID();
            ImGui.PopID();
        }
    }

    private static string ValueText(CanvasParam p) =>
        p.Text ?? p.Choices?.FirstOrDefault(c => c.Value == p.Value).Label ?? p.Value.ToString(CultureInfo.InvariantCulture);

    private void Field(IGraphView v, CanvasNode n, CanvasParam p)
    {
        switch (p.Kind)
        {
            case ParamKind.Number:
                Number(v, n, p);
                return;
            case ParamKind.Choice:
                Pick(v, n, p, p.Choices ?? []);
                return;
            case ParamKind.Entity:
                Entity(v, n, p);
                return;
        }
    }

    // An entity field: its combo (an Outliner row of the shown level dropped on it sets it) and a Pick button.
    private unsafe void Entity(IGraphView v, CanvasNode n, CanvasParam p)
    {
        var picking = _pick == (n.Id, p.Key);
        var button = picking ? "Esc" : "Pick";
        var style = ImGui.GetStyle();
        ImGui.SetNextItemWidth(MathF.Max(ImGui.CalcItemWidth() - ImGui.CalcTextSize(button).X - style.FramePadding.X * 2f - style.ItemSpacing.X, 16f));
        Pick(v, n, p, null);
        if (ImGui.BeginDragDropTarget())
        {
            var payload = ImGui.AcceptDragDropPayload(EntityPayload);
            if (!payload.IsNull && payload.DataSize == sizeof(int))
            {
                log(Drop(n.Id, p.Key, *(int*)payload.Data));
            }

            ImGui.EndDragDropTarget();
        }

        ImGui.SameLine();
        if (ImGui.Button(button))
        {
            _pick = picking ? null : (n.Id, p.Key);
        }

        if (ImGui.IsItemHovered())
        {
            Tooltip(picking ? "Click an entity in the view; Esc cancels" : "Pick in view: click an entity; or drop an Outliner row on the field");
        }
    }

    // A drag (or Ctrl+click typing) that becomes one SetParam when let go: one undo step.
    private void Number(IGraphView v, CanvasNode n, CanvasParam p)
    {
        var id = (n.Id, p.Key);
        var value = _editing == id ? _editValue : (int)Math.Clamp(p.Value, int.MinValue, int.MaxValue);
        if (ImGui.DragInt("##n", ref value, 0.2f))
        {
            (_editing, _editValue) = (id, value);
        }

        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            _editing = null;
            log(v.SetParam(n.Id, p.Key, value));
        }
    }

    // A combo of the parameter's choices, or (entity) of the view's targets with a filter; its list at the normal font size.
    private void Pick(IGraphView v, CanvasNode n, CanvasParam p, IReadOnlyList<ParamChoice>? choices)
    {
        if (!ImGui.BeginCombo("##c", ValueText(p), ImGuiComboFlags.HeightLarge))
        {
            return;
        }

        ImGui.PushFont(ImGui.GetFont(), ImGui.GetStyle().FontSizeBase);
        if (choices == null)
        {
            if (ImGui.IsWindowAppearing())
            {
                _pickFilter = "";
                ImGui.SetKeyboardFocusHere();
            }

            ImGui.InputTextWithHint("##pick", "filter", ref _pickFilter, 64);
            choices = v.Targets(n.Id, p.Key);
        }

        foreach (var c in choices)
        {
            if (_pickFilter.Length > 0 && p.Kind == ParamKind.Entity && !c.Label.Contains(_pickFilter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (ImGui.Selectable(c.Label, c.Value == p.Value) && c.Value != p.Value)
            {
                log(v.SetParam(n.Id, p.Key, c.Value));
            }
        }

        ImGui.PopFont();
        ImGui.EndCombo();
    }

    // ---- the add-node menu ----

    // Right-click on empty canvas: the node kinds by category, a search box first.
    private void AddMenu(IGraphView v)
    {
        if (!ImGui.BeginPopup(AddPopup))
        {
            return;
        }

        if (ImGui.IsWindowAppearing())
        {
            ImGui.SetKeyboardFocusHere();
        }

        ImGui.InputTextWithHint("##addFilter", "search", ref _addFilter, 64);
        string? category = null;
        foreach (var kind in v.Kinds)
        {
            if (_addFilter.Length > 0 && !kind.Label.Contains(_addFilter, StringComparison.OrdinalIgnoreCase)
                && !kind.Category.Contains(_addFilter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (kind.Category != category)
            {
                category = kind.Category;
                ImGui.SeparatorText(category);
            }

            if (ImGui.Selectable(kind.Label))
            {
                Add(v, kind);
            }
        }

        ImGui.EndPopup();
    }

    private void Add(IGraphView v, CanvasNodeKind kind)
    {
        if (v.Add(kind.Id, _addAt) is { } id)
        {
            _canvas.Select(id);
            log($"Trigger graph: added {kind.Label}");
            return;
        }

        log($"Trigger graph: {kind.Label} not added");
    }

    // ---- the side panel ----

    private void Side(IGraphView v)
    {
        if (!ImGui.BeginChild("##graphSide", Vector2.Zero, ImGuiChildFlags.Borders))
        {
            ImGui.EndChild();
            return;
        }

        Issues(v);
        Verified(v);
        ImGui.EndChild();
    }

    // Each issue: click to select and pan to its node.
    private void Issues(IGraphView v)
    {
        var issues = v.Issues;
        if (!ImGui.CollapsingHeader($"Issues ({issues.Count})###issues", ImGuiTreeNodeFlags.DefaultOpen))
        {
            return;
        }

        var titles = GraphLayout.ById(v.Nodes);
        for (var i = 0; i < issues.Count; i++)
        {
            var issue = issues[i];
            var title = titles.TryGetValue(issue.Node, out var n) ? n.Title : issue.Node.ToString(CultureInfo.InvariantCulture);
            ImGui.PushStyleColor(ImGuiCol.Text, issue.Level == IssueLevel.Error ? Error : Warning);
            if (ImGui.Selectable($"{title}: {issue.Text}##issue{i}"))
            {
                _canvas.Focus(v, issue.Node);
            }

            ImGui.PopStyleColor();
        }
    }

    // The last Verify per chain: green PASS, red FAIL; click to select the chain's nodes.
    private void Verified(IGraphView v)
    {
        if (!ImGui.CollapsingHeader("Verify###verify", ImGuiTreeNodeFlags.DefaultOpen))
        {
            return;
        }

        if (Results == null)
        {
            ImGui.TextDisabled("Not run (Verify).");
            return;
        }

        for (var i = 0; i < Results.Count; i++)
        {
            var r = Results[i];
            ImGui.PushStyleColor(ImGuiCol.Text, r.Passed ? Pass : Error);
            if (ImGui.Selectable($"{(r.Passed ? "PASS" : "FAIL")} {r.Name}##chain{i}"))
            {
                _canvas.Show(v, r.Nodes);
            }

            ImGui.PopStyleColor();
            if (ImGui.IsItemHovered() && r.Text.Length > 0)
            {
                Tooltip(r.Text);
            }

            Problems(v, r, i);
        }
    }

    // A failed chain's problems, one row per node: click to select and pan to it.
    private void Problems(IGraphView v, CanvasChain r, int chain)
    {
        if (r.Problems is not { Count: > 0 } problems)
        {
            return;
        }

        var titles = GraphLayout.ById(v.Nodes);
        ImGui.Indent();
        for (var i = 0; i < problems.Count; i++)
        {
            var p = problems[i];
            var title = titles.TryGetValue(p.Node, out var n) ? n.Title : p.Node.ToString(CultureInfo.InvariantCulture);
            ImGui.PushStyleColor(ImGuiCol.Text, p.Level == IssueLevel.Error ? Error : Warning);
            if (ImGui.Selectable($"{title}: {p.Text}##problem{chain}.{i}"))
            {
                _canvas.Focus(v, p.Node);
            }

            ImGui.PopStyleColor();
        }

        ImGui.Unindent();
    }

    private static uint Colour(Vector4 c) => ImGui.ColorConvertFloat4ToU32(c);

    // SetTooltip formats printf-style: a '%' in the text is escaped.
    private static void Tooltip(string text) => ImGui.SetTooltip(text.Replace("%", "%%", StringComparison.Ordinal));
}
