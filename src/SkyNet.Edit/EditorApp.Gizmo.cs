using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Hexa.NET.ImGui;
using RetroEng.Diagnostics;
using RetroEng.Editor;
using SDL;
using SkyNet.Formats;
using SkyNet.Game.Behaviour;
using SkyNet.Game.Mods;
using SkyNet.Game.Player;
using static SDL.SDL3;

namespace SkyNet.Edit;

/// <summary>The translate and rotate gizmo on the selected entity (W/E, the toolbar), its drags as
/// set-field commands on the document, the snapping, the editable position and angles, and the shown
/// level built again (debounced) once its layer's edits change.</summary>
internal sealed partial class EditorApp
{
    // The pause after the last edit before the shown level is baked again.
    private const double RebuildDelay = 0.3;

    private TransformGizmo? _transformGizmo;
    private GizmoMode _gizmoMode = GizmoMode.Translate;
    private EntityDrag? _drag;
    private int _gizmoHot = -1;
    private bool _leftWasDown, _dragConsumedClick;
    private long _rebuildDue;
    private readonly Dictionary<string, MapLayer> _openedLayers = new(StringComparer.Ordinal);
    private readonly List<Vector3> _previewTriangles = [];

    private GizmoSpace GizmoSpace => _prefs.GizmoLocal ? GizmoSpace.Local : GizmoSpace.World;

    private SnapSettings Snapping => new(_prefs.SnapGrid, _prefs.GridSize, _prefs.SnapAngle, _prefs.AngleStep, _prefs.SnapFloor);

    /// <summary>The document's layer of an instance (null: the document has none).</summary>
    private string? LayerOf(string? instance) =>
        instance != null && _world?.World.Instance(instance) is { } i && _doc != null && _doc.Layers.Contains(i.Layer) ? i.Layer : null;

    /// <summary>The map an instance plays now: the document's edits over its DOS map.</summary>
    private MapFile? EditedMap(RetroEng.World.WorldInstance instance) =>
        _doc != null && _doc.Layers.Contains(instance.Layer) ? _doc.Map(instance.Layer) : _data.LoadMap(EditorWorld.MapOf(instance));

    /// <summary>The document's map for the shown instance when its layer has edits since it was opened
    /// (the level then bakes from it); null: the data's block as it is.</summary>
    private MapFile? ShownEdits(RetroEng.World.WorldInstance instance) =>
        _doc != null && _doc.Layers.Contains(instance.Layer) && _openedLayers.TryGetValue(instance.Layer, out var opened)
            && !ReferenceEquals(opened, _doc.Layer(instance.Layer)) ? _doc.Map(instance.Layer) : null;

    private void DocumentOpened()
    {
        _openedLayers.Clear();
        _drag = null;
        _rebuildDue = 0;
        foreach (var layer in _doc!.Layers)
        {
            _openedLayers[layer] = _doc.Layer(layer);
        }

        _doc.MissionChanged += MissionEdited;
        _doc.Outliner.Changed += OutlinerChanged;
        _doc.LayerChanged += layer =>
        {
            _linksStale = true;
            _world?.Invalidate(layer);
            if (LayerOf(_levelInstance) == layer)
            {
                _rebuildDue = Stopwatch.GetTimestamp() + (long)(RebuildDelay * Stopwatch.Frequency);
            }
        };
    }

    // The shown level baked again from the edited layer, once the edits have paused and no drag is on.
    private void RebuildWhenDue()
    {
        if (_rebuildDue == 0 || _drag != null || Stopwatch.GetTimestamp() < _rebuildDue)
        {
            return;
        }

        _rebuildDue = 0;
        Reload();
    }

    /// <summary>The selected entity on the shown level, as the document has it now.</summary>
    private bool SelectedEntity(out string layer, out MapEntity entity)
    {
        (layer, entity) = ("", null!);
        if (_selected is not { IsInstance: false } item || item.Instance != _levelInstance || LayerOf(item.Instance) is not { } l)
        {
            return false;
        }

        layer = l;
        return _doc!.Map(l).TryGetEntityAt(item.Entity, out entity!);
    }

    // The ray through a point of the view (its own coordinates).
    private PickRay MouseRay(float x, float y)
    {
        var forward = PlayerController.Direction(_yaw, _pitch);
        var view = Matrix4x4.CreateLookAt(_eye, _eye + forward, Vector3.UnitY);
        return ViewportArea.Ray(view, FieldOfView, new Vector2(x, y));
    }

    // The left button on a handle starts a drag, held moves it, released ends it (one undo step).
    private unsafe void GizmoInput()
    {
        var buttons = SDL_GetMouseState(null, null);
        var left = (buttons & SDL_MouseButtonFlags.SDL_BUTTON_LMASK) != 0;
        var pressed = left && !_leftWasDown;
        _leftWasDown = left;
        var ray = MouseRay(ViewMouse.X, ViewMouse.Y);
        if (_drag != null)
        {
            if (left)
            {
                Try(() => { if (_groupDrag != null) { DragGroup(ray); } else { _drag.Update(ray); } });
            }
            else
            {
                _drag.End();
                (_drag, _groupDrag) = (null, null);
                _gizmoHot = -1;
            }

            return;
        }

        _gizmoHot = -1;
        if (!_viewHovered || !SelectedEntity(out var layer, out var e))
        {
            return;
        }

        var mode = EntityDrag.Turns(e) ? _gizmoMode : GizmoMode.Translate;
        var origin = BehaviourModel.Position(e);
        var axes = EntityDrag.Axes(mode, GizmoSpace, e);
        _gizmoHot = TransformGizmo.HitTest(mode, ray, origin, axes, TransformGizmo.SizeAt(_eye, origin, FieldOfView));
        if (pressed && _gizmoHot >= 0)
        {
            _drag = EntityDrag.Begin(_doc!, layer, e.FileOffset, mode, GizmoSpace, _gizmoHot, ray, Snapping, p => FloorAt(p, e.FileOffset));
            // After Begin (which closes the merge): Alt+drag's duplicate takes the copies' moves.
            _groupDrag = null;
            Try(() => _groupDrag = mode == GizmoMode.Translate ? GroupDrag(e) : null);
            _dragConsumedClick = true;
        }
    }

    // A command that does not fit the map (a value out of range) is logged, not thrown.
    private static void Try(Action edit)
    {
        try
        {
            edit();
        }
        catch (MapLayerException ex)
        {
            Log.Warn("Edit", ex.Message);
        }
    }

    private static void Try(Func<bool> edit) => Try(() => { _ = edit(); });

    /// <summary>The height of the collision (or the ground) under a world point, the entity itself left out.</summary>
    private float? FloorAt(Vector3 at, int exclude) =>
        _picking == null ? null
            : Snap.Floor(at, _picking.Targets, exclude, 32f,
                _level is { Outdoor: true } && GroundField is { } g ? r => EntityPicking.GroundDistance(r, g) is var d && d < float.MaxValue ? d : null : null);

    private void DrawTransformGizmo()
    {
        if (!SelectedEntity(out _, out var e))
        {
            return;
        }

        _transformGizmo ??= new TransformGizmo(_renderer);
        var mode = _drag?.Mode ?? (EntityDrag.Turns(e) ? _gizmoMode : GizmoMode.Translate);
        var origin = BehaviourModel.Position(e);
        var axes = EntityDrag.Axes(mode, _drag?.Space ?? GizmoSpace, e);
        _transformGizmo.Draw(mode, origin, axes, TransformGizmo.SizeAt(_eye, origin, FieldOfView), _drag?.Axis ?? _gizmoHot);
    }

    /// <summary>The outline's triangles moved from where the shown level has the entity to where the
    /// document has it (a drag or an edit not yet baked).</summary>
    private ReadOnlySpan<Vector3> Preview(int key, ReadOnlySpan<Vector3> triangles, ref Vector3 min, ref Vector3 max)
    {
        if (_level == null || !_level.Map.TryGetEntityAt(key, out var shown) || !SelectedEntity(out _, out var now) || now.FileOffset != key)
        {
            return triangles;
        }

        static Matrix4x4 Place(MapEntity e) =>
            BehaviourModel.Placement(EntityDrag.Turns(e) ? new Vector3(e.Pitch, e.Yaw, e.Roll) : Vector3.Zero, BehaviourModel.Position(e));
        Matrix4x4.Invert(Place(shown), out var back);
        var move = back * Place(now);
        if (move.IsIdentity)
        {
            return triangles;
        }

        _previewTriangles.Clear();
        var (lo, hi) = (new Vector3(float.MaxValue), new Vector3(float.MinValue));
        Span<Vector3> corners = stackalloc Vector3[8];
        for (var i = 0; i < 8; i++)
        {
            var c = Vector3.Transform(new Vector3((i & 1) != 0 ? max.X : min.X, (i & 2) != 0 ? max.Y : min.Y, (i & 4) != 0 ? max.Z : min.Z), move);
            (lo, hi) = (Vector3.Min(lo, c), Vector3.Max(hi, c));
        }

        foreach (var t in triangles)
        {
            _previewTriangles.Add(Vector3.Transform(t, move));
        }

        (min, max) = (lo, hi);
        return System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_previewTriangles);
    }

    // ---- the toolbar, the keys, the menu items ----

    private void GizmoToolbar()
    {
        ImGui.Separator();
        if (ImGui.RadioButton("Move (W)", _gizmoMode == GizmoMode.Translate))
        {
            _gizmoMode = GizmoMode.Translate;
        }

        if (ImGui.RadioButton("Rotate (E)", _gizmoMode == GizmoMode.Rotate))
        {
            _gizmoMode = GizmoMode.Rotate;
        }

        if (ImGui.SmallButton(_prefs.GizmoLocal ? "Local (L)" : "World (L)"))
        {
            ToggleSpace();
        }

        var (grid, angle, floor) = (_prefs.SnapGrid, _prefs.SnapAngle, _prefs.SnapFloor);
        if (ImGui.Checkbox("Grid", ref grid) | ImGui.Checkbox("Angle", ref angle) | ImGui.Checkbox("Floor", ref floor))
        {
            (_prefs.SnapGrid, _prefs.SnapAngle, _prefs.SnapFloor) = (grid, angle, floor);
            _prefsChanged = true;
        }
    }

    private void ToggleSpace()
    {
        _prefs.GizmoLocal = !_prefs.GizmoLocal;
        _prefsChanged = true;
    }

    // W/E/L/End when the keyboard is the view's and the camera is not flying (right mouse held).
    private unsafe void GizmoKeys()
    {
        var io = ImGui.GetIO();
        if (!ViewKeys || io.KeyCtrl || (SDL_GetMouseState(null, null) & SDL_MouseButtonFlags.SDL_BUTTON_RMASK) != 0)
        {
            return;
        }

        if (ImGui.IsKeyPressed(ImGuiKey.W, false))
        {
            _gizmoMode = GizmoMode.Translate;
        }
        else if (ImGui.IsKeyPressed(ImGuiKey.E, false))
        {
            _gizmoMode = GizmoMode.Rotate;
        }
        else if (ImGui.IsKeyPressed(ImGuiKey.L, false))
        {
            ToggleSpace();
        }
        else if (ImGui.IsKeyPressed(ImGuiKey.End, false))
        {
            DropToFloor();
        }
    }

    private void SnapMenu()
    {
        if (!ImGui.BeginMenu("Snapping"))
        {
            return;
        }

        var p = _prefs;
        var (grid, angle, floor, local) = (p.SnapGrid, p.SnapAngle, p.SnapFloor, p.GizmoLocal);
        var changed = ImGui.MenuItem("Snap to grid", "", ref grid);
        if (ImGui.BeginMenu("Grid size"))
        {
            foreach (var size in EditorPrefs.GridSizes)
            {
                if (ImGui.MenuItem(size.ToString(CultureInfo.InvariantCulture), "", p.GridSize == size))
                {
                    (p.GridSize, changed) = (size, true);
                }
            }

            ImGui.EndMenu();
        }

        changed |= ImGui.MenuItem("Snap angles", "", ref angle);
        if (ImGui.BeginMenu("Angle step"))
        {
            foreach (var step in EditorPrefs.AngleSteps)
            {
                if (ImGui.MenuItem(string.Create(CultureInfo.InvariantCulture, $"{step} ({step * 360.0 / EntityDrag.AngleUnits:0.##} deg)"), "", p.AngleStep == step))
                {
                    (p.AngleStep, changed) = (step, true);
                }
            }

            ImGui.EndMenu();
        }

        changed |= ImGui.MenuItem("Snap to floor while moving", "", ref floor);
        changed |= ImGui.MenuItem("Local axes", "L", ref local);
        if (ImGui.MenuItem("Drop selection to floor", "End"))
        {
            DropToFloor();
        }

        if (changed)
        {
            (p.SnapGrid, p.SnapAngle, p.SnapFloor, p.GizmoLocal) = (grid, angle, floor, local);
            _prefsChanged = true;
        }

        ImGui.EndMenu();
    }

    // ---- the properties panel's editable rows ----

    /// <summary>Draws a row's value; position and angles as fields that set them (a drag of a field is one step).</summary>
    private void PropertyValue(string name, string value)
    {
        if (name is not ("DOS position" or "angles (pitch, yaw, roll)") || !SelectedEntity(out var layer, out var e))
        {
            ImGui.TextUnformatted(value);
            return;
        }

        var angles = name != "DOS position";
        string[] fields = angles ? ["pitch", "yaw", "roll"] : ["x", "y", "z"];
        Span<int> v = [EntityDrag.Current(e, fields[0]), EntityDrag.Current(e, fields[1]), EntityDrag.Current(e, fields[2])];
        ImGui.SetNextItemWidth(-1f);
        bool edited;
        unsafe
        {
            fixed (int* p = v)
            {
                edited = ImGui.DragInt3("##" + name, p, angles ? 1f : 4f);
            }
        }

        if (ImGui.IsItemActivated())
        {
            _doc!.EndDrag();
        }

        if (edited)
        {
            var set = new Dictionary<string, int> { [fields[0]] = v[0], [fields[1]] = v[1], [fields[2]] = v[2] };
            if (angles)
            {
                foreach (var f in fields)
                {
                    set[f] = set[f] == EntityDrag.Current(e, f) ? set[f] : Snap.WrapAngle(set[f], EntityDrag.AngleUnits);
                }
            }

            Try(() => _doc!.SetFields(layer, e.FileOffset, set, label: angles ? "Rotate" : "Move"));
        }

        if (ImGui.IsItemDeactivated())
        {
            _doc!.EndDrag();
        }
    }
}
