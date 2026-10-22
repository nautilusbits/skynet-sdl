using System.Globalization;
using System.Numerics;
using Hexa.NET.ImGui;
using RetroEng.Editor;
using SkyNet.Formats;
using static SDL.SDL3;

namespace SkyNet.Edit;

/// <summary>
/// Drag and drop placing (Unreal's, kept simple): a model, robot type or sprite of the Assets panel, or any Place
/// panel item, dragged into the view. A ghost follows the pointer where its ray meets the level (the placing
/// click's point, snaps included); letting go there puts it down (one undo step) and selects it; Esc cancels.
/// <code>
///   Assets / Place row ──drag──► over the view: ghost at PlacePoint ──release──► PutDown ─► selected
///                                over a panel: no ghost            ──release──► nothing
/// </code>
/// </summary>
internal sealed partial class EditorApp
{
    private const string DragPayload = "SKYNET_PLACE";
    // The most model triangles a ghost outlines (SelectionOutline's cap).
    private const int GhostTriangles = SelectionOutline.MaxTriangles;
    private static readonly Vector4 GhostColour = new(0.3f, 1f, 0.45f, 1f);

    private readonly SelectionOutline _ghostOutline;
    private PlaceItem? _dragItem;
    // Esc let go of the drag: none begins again until the button is up.
    private bool _dragCancelled;
    // place.drag: the pointer held at this pixel (no mouse) until place.drop.
    private Vector2? _dragPixel;
    private Vector3? _ghostAt;
    // The last model ghost: its name and triangles about its origin.
    private (string Name, Vector3[] Triangles, Vector3 Min, Vector3 Max)? _ghostModel;
    private readonly List<Vector3> _ghostPlaced = [];

    /// <summary>A drag of the last item drawn begins or goes on: <paramref name="resolve"/> gives what it places.</summary>
    private void DragSource(Func<PlaceItem?> resolve)
    {
        if (_dragCancelled || LayerOf(_levelInstance) == null || !ImGui.BeginDragDropSource())
        {
            return;
        }

        var item = resolve();
        unsafe
        {
            ImGui.SetDragDropPayload(DragPayload, null, 0, ImGuiCond.None);
        }

        ImGui.TextUnformatted(item != null ? "Place " + item.Label.Trim() : "Nothing to place");
        ImGui.TextDisabled(_ghostAt != null ? "Release to place, Esc cancels" : "Over the view to place");
        ImGui.EndDragDropSource();
        _dragItem = item;
    }

    // An Assets panel row (its row the last item): models, robot types and sprites drag into the view.
    private void AssetDragSource(AssetEntry entry)
    {
        if (entry.Kind is not (GameAssets.Models or RobotAssets.Robots or GameAssets.Sprites))
        {
            return;
        }

        DragSource(() => LayerOf(_levelInstance) is { } layer ? PlacePalette.FromAsset(entry.Kind, entry.Id, _doc!.Map(layer)) : null);
    }

    /// <summary>The drag's frame, after every panel: the ghost's point under the pointer when it is over the view,
    /// the drop when the button is let go, Esc cancelling.</summary>
    private unsafe void DragFrame()
    {
        var down = _dragPixel != null || ImGui.IsMouseDown(ImGuiMouseButton.Left);
        if (!down)
        {
            _dragCancelled = false;
        }

        if (_dragItem is not { } item)
        {
            _ghostAt = null;
            return;
        }

        if (ImGui.IsKeyPressed(ImGuiKey.Escape, false))
        {
            CancelDrag();
            return;
        }

        // Over the view: the Viewport window's picture under the pointer (its coordinates).
        var (pointer, overView) = _dragPixel is { } pixel ? (pixel, true) : (ViewMouse, _viewHovered);
        var size = ViewportArea.Size;
        _ghostAt = overView ? PlacePoint(pointer.X, pointer.Y, size.X, size.Y) : null;
        if (down)
        {
            return;
        }

        Drop(item);
    }

    // The item put down at the ghost (nothing when there is none), the drag over.
    private int? Drop(PlaceItem item)
    {
        var key = _ghostAt is { } at ? PutDown(item, at) : null;
        (_dragItem, _ghostAt, _dragPixel) = (null, null, null);
        return key;
    }

    private void CancelDrag()
    {
        (_dragItem, _ghostAt, _dragPixel, _dragCancelled) = (null, null, null, true);
        ImGuiP.ClearDragDrop();
    }

    // ---- the ghost ----

    // The dragged item's outline where it would land: a model's triangles, else its pick box.
    private void DrawGhost()
    {
        if (_dragItem is not { } item || _ghostAt is not { } at || LayerOf(_levelInstance) is not { } layer)
        {
            return;
        }

        if (item.Kind == PlaceKind.Prop && GhostModel(item, _doc!.Map(layer)) is { } model)
        {
            _ghostPlaced.Clear();
            foreach (var t in model.Triangles)
            {
                _ghostPlaced.Add(t + at);
            }

            _ghostOutline.Draw(model.Min + at, model.Max + at, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_ghostPlaced));
            return;
        }

        var (centre, half) = item.Kind == PlaceKind.Robot
            ? (at + new Vector3(0f, EntityPicking.RobotHalf.Y, 0f), EntityPicking.RobotHalf)
            : (at + new Vector3(0f, item.Lift, 0f), EntityPicking.PointHalf);
        _ghostOutline.Draw(centre - half, centre + half);
    }

    // A prop's model as triangles about its origin (unturned, as placed), kept for the next frames.
    private (string Name, Vector3[] Triangles, Vector3 Min, Vector3 Max)? GhostModel(PlaceItem item, MapFile map)
    {
        var name = item.Name ?? (item.Fields.TryGetValue("nameIndex", out var i) && i >= 0 && i < map.Names.Length ? map.Names[i] : null);
        if (name == null)
        {
            return null;
        }

        if (_ghostModel is { } kept && kept.Name == name)
        {
            return kept;
        }

        if (_data.LoadModel(name + ".3D") is not { } model)
        {
            return null;
        }

        // Each face fanned into triangles, up to the outline's cap.
        var triangles = new List<Vector3>();
        var verts = model.Vertices;
        foreach (var face in model.Faces)
        {
            for (var k = 2; k < face.Indices.Length && triangles.Count < GhostTriangles * 3; k++)
            {
                int a = face.Indices[0], b = face.Indices[k - 1], c = face.Indices[k];
                if (a < verts.Length && b < verts.Length && c < verts.Length)
                {
                    triangles.AddRange([verts[a].ToYUp(), verts[b].ToYUp(), verts[c].ToYUp()]);
                }
            }
        }

        if (triangles.Count == 0)
        {
            return null;
        }

        var (min, max) = (triangles.Aggregate(Vector3.Min), triangles.Aggregate(Vector3.Max));
        _ghostModel = (name, [.. triangles], min, max);
        return _ghostModel;
    }

    // ---- the commands ----

    // place.drag kind:text x y: the palette item held over that pixel (a screenshot shows its ghost).
    private string DragCmd(string spec, float x, float y)
    {
        var (kindText, text) = spec.Split(':', 2) is [var k, var t] ? (k, t) : (spec, "");
        if (!Enum.TryParse<PlaceKind>(kindText, ignoreCase: true, out var kind) || LayerOf(_levelInstance) is not { } layer)
        {
            return $"No palette {kindText}, or no level shown";
        }

        var item = PlacePalette.Items(kind, _doc!.Map(layer), _arena != null).FirstOrDefault(i => i.Label.Contains(text, StringComparison.OrdinalIgnoreCase));
        if (item == null)
        {
            return $"Nothing in the {kind} palette matches {text}";
        }

        (_dragItem, _dragPixel) = (item, new Vector2(x, y));
        _ghostAt = PlacePoint(x, y, ViewportArea.Size.X, ViewportArea.Size.Y);
        return _ghostAt is { } at
            ? string.Create(CultureInfo.InvariantCulture, $"Dragging {item.Label.Trim()} over {at.X:0},{at.Y:0},{at.Z:0}")
            : $"Dragging {item.Label.Trim()}: nothing under the pointer";
    }

    private string DropCmd() =>
        _dragItem is { } item && Drop(item) is { } key ? Inv($"Dropped as @{key}") : "Nothing dropped";
}
