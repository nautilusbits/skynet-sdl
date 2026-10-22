using System.Globalization;
using System.Numerics;
using Hexa.NET.ImGui;
using RetroEng.Diagnostics;
using RetroEng.Editor;
using SkyNet.Formats;
using SkyNet.Game.Mods;
using SkyNet.Game.Player;

namespace SkyNet.Edit;

/// <summary>The Place panel (editor plan step E7): palettes of props, robots, pickups, markers and
/// lights; an armed item is put down where the next click in the view meets the level (floor and grid
/// snap honoured), one add command each. Del deletes the selected entity, Ctrl+D duplicates it.</summary>
internal sealed partial class EditorApp
{
    private static readonly PlaceKind[] PlaceKinds = Enum.GetValues<PlaceKind>();
    private PlaceKind _placeKind = PlaceKind.Robot;
    private PlaceItem? _armed;
    // The tab follows _placeKind once when --place set it (the tab bar otherwise opens on its first tab).
    private bool _placeTabForced;
    private string _placeFilter = "";
    private (string Layer, MapLayer Edits, PlaceKind Kind)? _paletteOf;
    private IReadOnlyList<PlaceItem> _palette = [];

    /// <summary>The palette of the shown level's layer for the current tab (rebuilt when either changes).</summary>
    private IReadOnlyList<PlaceItem> Palette()
    {
        if (LayerOf(_levelInstance) is not { } layer)
        {
            return [];
        }

        var key = (layer, _doc!.Layer(layer), _placeKind);
        if (_paletteOf != key)
        {
            _paletteOf = key;
            _palette = PlacePalette.Items(_placeKind, _doc.Map(layer), _arena != null);
        }

        return _palette;
    }

    private void PlacePanel(Vector2 at, Vector2 size)
    {
        PanelAt(at, size);
        if (BeginPanel(EditorPanel.Place))
        {
            if (LayerOf(_levelInstance) == null)
            {
                ImGui.TextDisabled("Open a mission to place things on its level.");
            }
            else
            {
                PlaceTabs();
            }
        }

        ImGui.End();
    }

    private void PlaceTabs()
    {
        if (ImGui.BeginTabBar("placeKinds"))
        {
            foreach (var kind in PlaceKinds)
            {
                if (ImGui.BeginTabItem(kind + "s", _placeTabForced && kind == _placeKind ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None))
                {
                    if (_placeTabForced)
                    {
                        // Until the forced tab is the open one, another tab's selection changes nothing.
                        _placeTabForced = kind != _placeKind;
                    }
                    else if (_placeKind != kind)
                    {
                        (_placeKind, _armed) = (kind, null);
                    }

                    ImGui.EndTabItem();
                }
            }

            ImGui.EndTabBar();
        }

        ImGui.SetNextItemWidth(-1f);
        ImGui.InputTextWithHint("##placeFilter", "filter", ref _placeFilter, 64);
        var listHeight = MathF.Max(60f, ImGui.GetContentRegionAvail().Y - ImGui.GetFrameHeightWithSpacing() * 2.6f);
        if (ImGui.BeginChild("placeList", new Vector2(0f, listHeight), ImGuiChildFlags.Borders))
        {
            foreach (var item in Palette())
            {
                if (_placeFilter.Length > 0 && !item.Label.Contains(_placeFilter, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (ImGui.Selectable(item.Label, item == _armed))
                {
                    _armed = item == _armed ? null : item;
                }

                DragSource(() => item);
            }
        }

        ImGui.EndChild();
        if (_armed is { } armed)
        {
            ImGui.TextWrapped($"Click in the view to place {armed.Label.Trim()} (Esc stops).");
        }
        else
        {
            ImGui.TextDisabled("Pick an item, then click in the view; or drag it into the view.");
        }

        var selected = SelectedEntity(out _, out _);
        ImGui.BeginDisabled(!selected);
        if (ImGui.Button("Delete (Del)"))
        {
            DeleteSelected();
        }

        ImGui.SameLine();
        if (ImGui.Button("Duplicate (Ctrl+D)"))
        {
            DuplicateSelected();
        }

        ImGui.EndDisabled();
    }

    /// <summary>--place kind:text: the first item of that palette whose label holds the text, armed.</summary>
    private bool ArmPlace(string spec)
    {
        var (kindText, text) = spec.Split(':', 2) is [var k, var t] ? (k, t) : (spec, "");
        if (!Enum.TryParse<PlaceKind>(kindText, ignoreCase: true, out var kind))
        {
            Log.Error("Edit", $"--place {spec}: no palette {kindText} (prop, robot, pickup, marker, light, sprite)");
            return false;
        }

        (_placeKind, _placeTabForced) = (kind, true);
        _armed = Palette().FirstOrDefault(i => i.Label.Contains(text, StringComparison.OrdinalIgnoreCase));
        if (_armed == null)
        {
            Log.Error("Edit", $"--place {spec}: nothing in the {kind} palette matches");
            return false;
        }

        Log.Info("Edit", $"Armed {kind}: {_armed.Label.Trim()}");
        return true;
    }

    /// <summary>The armed item put down where a pixel's ray first meets an entity or the ground, onto the
    /// floor under that point when floor snap is on and on the grid when grid snap is; null: nothing there.</summary>
    private int? PlaceAt(float x, float y, float width, float height)
    {
        if (_armed is not { } item)
        {
            return null;
        }

        if (PlacePoint(x, y, width, height) is not { } p)
        {
            Log.Info("Edit", string.Create(CultureInfo.InvariantCulture, $"Place at {x:0}, {y:0}: nothing under the pointer"));
            return null;
        }

        return PutDown(item, p);
    }

    /// <summary>Where a pixel's ray first meets an entity or the ground, onto the floor under it when floor
    /// snap is on and on the grid when grid snap is (a placing click and a drop land there); null: nothing.</summary>
    private Vector3? PlacePoint(float x, float y, float width, float height)
    {
        if (_picking == null || _level == null)
        {
            return null;
        }

        var forward = PlayerController.Direction(_yaw, _pitch);
        var view = Matrix4x4.CreateLookAt(_eye, _eye + forward, Vector3.UnitY);
        var ray = PickRay.FromView(view, FieldOfView, width / MathF.Max(height, 1f), PickRay.Ndc(x, y, width, height));
        if (_picking.Surface(ray, _level.Outdoor ? GroundField : null) is not { } p)
        {
            return null;
        }

        if (_prefs.SnapGrid)
        {
            p = new Vector3(Snap.Grid(p.X, _prefs.GridSize), p.Y, Snap.Grid(p.Z, _prefs.GridSize));
        }

        if (_prefs.SnapFloor && FloorAt(p, -1) is { } floor)
        {
            p.Y = floor;
        }

        return p;
    }

    /// <summary><paramref name="item"/> added on the shown level at world point <paramref name="p"/> (one undo step) and selected: its key.</summary>
    private int? PutDown(PlaceItem item, Vector3 p)
    {
        if (LayerOf(_levelInstance) is not { } layer)
        {
            return null;
        }

        int? key = null;
        Try(() => key = PlacePalette.Place(_doc!, layer, item, p));
        if (key is { } k)
        {
            Select(new OutlinerItem(_levelInstance!, k));
            Log.Info("Edit", string.Create(CultureInfo.InvariantCulture, $"Placed {item.Label.Trim()} as {layer} @{k} at {p.X:0},{p.Y:0},{p.Z:0}"));
        }

        return key;
    }

    private void DeleteSelected()
    {
        if (SelectedEntity(out var layer, out var e))
        {
            _doc!.EndDrag();
            Try(() => _doc.DeleteEntity(layer, e.FileOffset));
            Select(new OutlinerItem(_levelInstance!));
            Log.Info("Edit", $"Deleted {layer} @{e.FileOffset}");
        }
    }

    // Del, Ctrl+D and Esc, when ImGui does not hold the keyboard.
    private void PlaceKeys(ImGuiIOPtr io)
    {
        if (!ViewKeys)
        {
            return;
        }

        if (ImGui.IsKeyPressed(ImGuiKey.Delete, false))
        {
            DeleteSelected();
        }
        else if (io.KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.D, false))
        {
            DuplicateSelected();
        }
        else if (ImGui.IsKeyPressed(ImGuiKey.Escape, false))
        {
            (_armed, _pickNext) = (null, null);
            _graphWindow?.CancelPick();
        }
    }
}
