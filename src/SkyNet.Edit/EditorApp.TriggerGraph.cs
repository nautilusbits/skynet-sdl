using System.Globalization;
using System.Numerics;
using Hexa.NET.ImGui;
using RetroEng.Diagnostics;
using SkyNet.Formats;
using SkyNet.Game.Behaviour;
using SkyNet.Game.Player;

namespace SkyNet.Edit;

/// <summary>View > Trigger graph (editor-tools plan, T8): the shown level's trigger graph as nodes
/// (<see cref="TriggerGraphWindow"/> over <see cref="TriggerGraphView"/>). A node's double-click selects its
/// entity and flies there; selecting an entity elsewhere focuses its node.</summary>
internal sealed partial class EditorApp
{
    // A node added from the menu stands this far before the camera (world units).
    private const float NewNodeDistance = 256f;
    private const float GraphWidth = 1100f;

    private TriggerGraphWindow? _graphWindow;
    private TriggerGraphView? _graphView;
    private EditDocument? _graphDoc;
    private OutlinerItem? _graphFocused;
    private bool _showGraph;

    private void RegisterGraphCommands()
    {
        _graphWindow = new TriggerGraphWindow(GraphView, line => Log.Info("Edit", line));
        _graphWindow.Shown += () => _showGraph = true;
        _graphWindow.Activated += key =>
        {
            if (_levelInstance == null)
            {
                return;
            }

            var item = new OutlinerItem(_levelInstance, key);
            _graphFocused = item;
            Select(item);
            FlyTo(item);
        };
        _graphWindow.Register(_commands);
    }

    // The shown level's graph; a new view when the document or the shown layer changed.
    private TriggerGraphView? GraphView()
    {
        var layer = LayerOf(_levelInstance);
        if (_doc == null || layer == null)
        {
            _graphView?.Dispose();
            (_graphView, _graphDoc) = (null, null);
            return null;
        }

        if (_graphView != null && ReferenceEquals(_graphDoc, _doc) && _graphView.Layer == layer)
        {
            return _graphView;
        }

        _graphView?.Dispose();
        var doc = _doc;
        _graphView = new TriggerGraphView(doc, layer, () => _eye + PlayerController.Direction(_yaw, _pitch) * NewNodeDistance,
            _ => [.. GraphVerify.All(doc, layer, _transfrm ??= BehaviourModel.LoadTransfrm(_data)).Select(Verdict).OrderBy(c => c.Passed)]);
        _graphDoc = _doc;
        return _graphView;
    }

    // A chain's verdict as a Verify row (failed ones first): it fails when a start does nothing, fails the mission or
    // misses a record; each problem a row on its node.
    private static CanvasChain Verdict(ChainVerdict v)
    {
        var text = v.Runs.Count == 0 ? v.What : string.Join('\n', v.Runs.Select(r => r.Summary));
        var problems = v.Problems.Select(p => new CanvasIssue(p.Node, IssueLevel.Error, p.Text)).ToList();
        return new CanvasChain(string.Create(CultureInfo.InvariantCulture, $"{v.What} @{v.Head:x5}"), v.Passed, text, v.Keys, problems);
    }

    // A field's Pick (Trigger graph) takes the level view's click: the field set, the selection stays.
    private bool GraphPick(int entity)
    {
        if (_graphWindow is not { Picking: true } window)
        {
            return false;
        }

        Log.Info("Edit", window.Pick(entity) ?? "");
        return true;
    }

    // An Outliner row of the shown level drags onto a Trigger graph entity field (its key the payload).
    private unsafe void EntityDragSource(string instance, MapFile map, MapEntity e)
    {
        if (instance != _levelInstance || !_showGraph || !ImGui.BeginDragDropSource())
        {
            return;
        }

        var key = e.FileOffset;
        ImGui.SetDragDropPayload(TriggerGraphWindow.EntityPayload, &key, sizeof(int), ImGuiCond.None);
        ImGui.TextUnformatted(EditorWorld.Label(map, e));
        ImGui.TextDisabled("Drop on a Trigger graph field");
        ImGui.EndDragDropSource();
    }

    private void GraphFrame(Vector2 at, float width, float height)
    {
        if (!_showGraph || _graphWindow == null)
        {
            return;
        }

        // The level view's selection focuses its node (once per change).
        if (_selected is { IsInstance: false } item && item != _graphFocused && item.Instance == _levelInstance)
        {
            _graphFocused = item;
            _graphWindow.Focus(item.Entity);
        }

        // First shown over the view's free part, widened to GraphWidth (leftwards when the screen ends).
        var main = ImGui.GetMainViewport();
        var w = MathF.Min(MathF.Max(width, GraphWidth), main.Size.X);
        var x = MathF.Max(MathF.Min(at.X, main.Size.X - w), 0f);
        ImGui.SetNextWindowPos(main.Pos + new Vector2(x, at.Y), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new Vector2(w, height), ImGuiCond.FirstUseEver);
        _graphWindow.Draw(ref _showGraph);
    }
}
