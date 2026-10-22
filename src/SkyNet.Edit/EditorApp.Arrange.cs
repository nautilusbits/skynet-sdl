using System.Globalization;
using System.Numerics;
using Hexa.NET.ImGui;
using RetroEng.Commands;
using RetroEng.Diagnostics;
using RetroEng.Editor;
using SkyNet.Formats;
using SkyNet.Game.Behaviour;

namespace SkyNet.Edit;

/// <summary>Where a paste puts the copies: their anchor on the cursor's ground point, or where they were.</summary>
internal enum PasteAt
{
    Cursor,
    Same,
}

/// <summary>
/// The selection's edits as Unreal has them (kept simple): Duplicate (Ctrl+D, Alt+drag of the gizmo), Copy /
/// Cut / Paste (Ctrl+C / X / V, Ctrl+Shift+V in place) through <see cref="EntityClipboard"/> (kept across
/// maps and missions), Drop to floor (End), and snap / align / distribute (<see cref="EntityArrange"/>).
/// Each one undo step; the shown level is baked again from the layer.
/// </summary>
internal sealed partial class EditorApp
{
    private EntityClipboard? _clipboard;
    // A translate drag moving several entities (or Alt+drag's copies): their starts and the primary's.
    private (string Layer, Dictionary<int, (int X, int Y, int Z)> Starts, (int X, int Y, int Z) From)? _groupDrag;

    private void RegisterArrangeCommands()
    {
        _commands.Add("edit.copy", "edit.copy", _ => CopySelection() ? Inv($"Copied {_clipboard!.Items.Length}") : "Nothing selected",
            "copies the selected entities");
        _commands.Add("edit.cut", "edit.cut", _ => CutSelection() ? Inv($"Cut {_clipboard!.Items.Length}") : "Nothing selected",
            "copies the selected entities and deletes them (one undo step)");
        _commands.Add("edit.paste", "edit.paste [same|x,y]", a => PasteCmd(a.OptionalWord(0)),
            "pastes the copied entities on the ground under pixel x,y (default the view's centre), or where they were (same)");
        _commands.Add("edit.duplicate", "edit.duplicate", _ => Inv($"{DuplicateSelected()} copies"), "duplicates the selection beside it");
        _commands.Add("edit.drop", "edit.drop", _ => DropToFloor() ? "Dropped" : "Nothing moved", "puts the selection down onto the floor under it (End)");
        _commands.Add("edit.snap", "edit.snap", _ => Arranged(SelectionKeys(out var l) is { } k && EntityArrange.SnapToGrid(_doc!, l, k, _prefs.GridSize)),
            "snaps the selection's X and Z to the grid");
        _commands.Add("edit.align", "edit.align <x|y|z> <min|centre|max>", a => Arranged(SelectionKeys(out var l) is { } k
            && EntityArrange.Align(_doc!, l, k, Parse<ArrangeAxis>(a.Word(0)), Parse<AlignTo>(a.Word(1)))),
            "aligns the selection on a world axis to its lowest, middle or highest");
        _commands.Add("edit.distribute", "edit.distribute <x|y|z>", a => Arranged(SelectionKeys(out var l) is { } k
            && EntityArrange.Distribute(_doc!, l, k, Parse<ArrangeAxis>(a.Word(0)))),
            "spreads the selection evenly on a world axis between its outermost two");
    }

    private static T Parse<T>(string word) where T : struct, Enum =>
        Enum.TryParse<T>(word.Replace("center", "centre", StringComparison.OrdinalIgnoreCase), ignoreCase: true, out var v)
            ? v : throw new CommandUsageException($"{word}: one of {string.Join(", ", Enum.GetNames<T>()).ToLowerInvariant()}");

    private static string Arranged(bool moved) => moved ? "Arranged" : "Nothing moved";

    private string PasteCmd(string? where)
    {
        var at = where switch
        {
            null => ViewportArea.Size * 0.5f,
            "same" => (Vector2?)null,
            _ => where.Split(',') is [var x, var y] && float.TryParse(x, CultureInfo.InvariantCulture, out var px)
                && float.TryParse(y, CultureInfo.InvariantCulture, out var py) ? new Vector2(px, py) : throw new CommandUsageException("edit.paste [same|x,y]"),
        };
        var keys = Paste(at == null ? PasteAt.Same : PasteAt.Cursor, at ?? default);
        return keys.Count > 0 ? Inv($"Pasted as {string.Join(' ', keys.Select(k => "@" + k))}") : "Nothing pasted";
    }

    // ---- the selection ----

    /// <summary>The selected keys of the shown level (primary first); null: none.</summary>
    private List<int>? SelectionKeys(out string layer) =>
        SelectedEntities(out layer, out var list) ? [.. list.Select(e => e.FileOffset)] : null;

    // The keys selected, the first primary.
    private void SelectKeys(IReadOnlyList<int> keys)
    {
        if (keys.Count == 0 || _levelInstance == null)
        {
            return;
        }

        Select(new OutlinerItem(_levelInstance, keys[0]));
        foreach (var key in keys.Skip(1))
        {
            _extra.Add(new OutlinerItem(_levelInstance, key));
        }
    }

    // ---- duplicate, copy, cut, paste ----

    /// <summary>Copies of the selection <see cref="PlacePalette.DuplicateOffset"/> beside it, selected: how many.</summary>
    private int DuplicateSelected()
    {
        if (SelectionKeys(out var layer) is not { } keys)
        {
            return 0;
        }

        _doc!.EndDrag();
        IReadOnlyList<int> copies = [];
        Try(() => copies = EntityClipboard.Duplicate(_doc, layer, keys, (PlacePalette.DuplicateOffset, 0, 0)));
        SelectKeys(copies);
        Log.Info("Edit", $"Duplicated {string.Join(' ', keys.Select(k => "@" + k))} as {string.Join(' ', copies.Select(k => "@" + k))}");
        return copies.Count;
    }

    private bool CopySelection()
    {
        if (SelectionKeys(out var layer) is not { } keys || EntityClipboard.Copy(_doc!, layer, keys) is not { } clip)
        {
            return false;
        }

        _clipboard = clip;
        Log.Info("Edit", Inv($"Copied {clip.Items.Length} of {layer}"));
        return true;
    }

    private bool CutSelection()
    {
        if (SelectionKeys(out var layer) is not { } keys)
        {
            return false;
        }

        _doc!.EndDrag();
        EntityClipboard? clip = null;
        Try(() => clip = EntityClipboard.Cut(_doc, layer, keys));
        if (clip == null)
        {
            return false;
        }

        _clipboard = clip;
        Select(new OutlinerItem(_levelInstance!));
        Log.Info("Edit", Inv($"Cut {clip.Items.Length} of {layer}"));
        return true;
    }

    /// <summary>The clipboard added to the shown level, at the ground under pixel <paramref name="pixel"/>
    /// (none there: where they were) or where they were; the copies selected.</summary>
    private IReadOnlyList<int> Paste(PasteAt at, Vector2 pixel)
    {
        if (_clipboard is not { } clip || LayerOf(_levelInstance) is not { } layer)
        {
            return [];
        }

        var move = (0, 0, 0);
        if (at == PasteAt.Cursor && GroundUnder(pixel) is { } ground)
        {
            move = clip.MoveTo(ground);
        }

        _doc!.EndDrag();
        IReadOnlyList<int> keys = [];
        Try(() => keys = clip.Paste(_doc, layer, move));
        _doc.EndDrag();
        SelectKeys(keys);
        Log.Info("Edit", Inv($"Pasted {keys.Count} into {layer} moved {move}"));
        return keys;
    }

    // The first entity or ground a pixel's ray meets, on the grid when grid snap is on; null: nothing.
    private Vector3? GroundUnder(Vector2 pixel)
    {
        if (_picking == null || _level == null)
        {
            return null;
        }

        var ray = MouseRay(pixel.X, pixel.Y);
        var hit = Picker.Nearest(ray, _picking.Targets)?.Distance ?? float.MaxValue;
        if (_level.Outdoor && GroundField is { } ground)
        {
            hit = MathF.Min(hit, EntityPicking.GroundDistance(ray, ground));
        }

        if (hit == float.MaxValue)
        {
            return null;
        }

        var p = ray.At(hit);
        return _prefs.SnapGrid ? new Vector3(Snap.Grid(p.X, _prefs.GridSize), p.Y, Snap.Grid(p.Z, _prefs.GridSize)) : p;
    }

    // ---- drop, snap, align, distribute ----

    /// <summary>The selection put down onto the floor under each one's feet (one step); false: nothing moved.</summary>
    private bool DropToFloor()
    {
        if (SelectionKeys(out var layer) is not { } keys)
        {
            return false;
        }

        var moved = false;
        Try(() => moved = EntityArrange.Drop(_doc!, layer, keys, (e, feet) => FloorAt(feet, e.FileOffset), FootDepth));
        Log.Info("Edit", moved ? Inv($"Dropped {keys.Count} to the floor") : "Drop to floor: nothing under it or already down");
        return moved;
    }

    /// <summary>How far an entity's lowest point is below its position: a mesh's collision box as baked, else its lift.</summary>
    private float FootDepth(MapEntity e)
    {
        if (e.Variant == MapEntityVariant.Mesh && _picking != null && _picking.TryGet(e.FileOffset, out var t) && !t.Triangles.IsEmpty
            && _level != null && _level.Map.TryGetEntityAt(e.FileOffset, out var shown))
        {
            return BehaviourModel.Position(shown).Y - t.Min.Y;
        }

        return EntityArrange.Lift((int)e.Variant);
    }

    private void ArrangeSelection(Func<string, List<int>, bool> action)
    {
        if (SelectionKeys(out var layer) is { } keys)
        {
            Try(() => action(layer, keys));
        }
    }

    // ---- the menu, the keys, the gizmo ----

    /// <summary>The Edit menu's clipboard and Arrange items.</summary>
    private void ArrangeMenuItems(bool selected)
    {
        if (ImGui.MenuItem("Copy", "Ctrl+C", false, selected))
        {
            CopySelection();
        }

        if (ImGui.MenuItem("Cut", "Ctrl+X", false, selected))
        {
            CutSelection();
        }

        if (ImGui.MenuItem("Paste", "Ctrl+V", false, _clipboard != null))
        {
            Paste(PasteAt.Cursor, ViewportArea.Size * 0.5f);
        }

        if (ImGui.MenuItem("Paste in place", "Ctrl+Shift+V", false, _clipboard != null))
        {
            Paste(PasteAt.Same, default);
        }

        if (!ImGui.BeginMenu("Arrange", selected))
        {
            return;
        }

        if (ImGui.MenuItem("Drop to floor", "End"))
        {
            DropToFloor();
        }

        if (ImGui.MenuItem("Snap to grid"))
        {
            ArrangeSelection((l, k) => EntityArrange.SnapToGrid(_doc!, l, k, _prefs.GridSize));
        }

        foreach (var axis in Enum.GetValues<ArrangeAxis>())
        {
            ImGui.Separator();
            foreach (var to in Enum.GetValues<AlignTo>())
            {
                if (ImGui.MenuItem($"Align {axis} {to.ToString().ToLowerInvariant()}"))
                {
                    ArrangeSelection((l, k) => EntityArrange.Align(_doc!, l, k, axis, to));
                }
            }

            if (ImGui.MenuItem($"Distribute {axis}"))
            {
                ArrangeSelection((l, k) => EntityArrange.Distribute(_doc!, l, k, axis));
            }
        }

        ImGui.EndMenu();
    }

    // Ctrl+C / X / V, Ctrl+Shift+V, when ImGui does not hold the keyboard.
    private void ClipboardKeys(ImGuiIOPtr io)
    {
        if (!ViewKeys || !io.KeyCtrl)
        {
            return;
        }

        if (ImGui.IsKeyPressed(ImGuiKey.C, false))
        {
            CopySelection();
        }
        else if (ImGui.IsKeyPressed(ImGuiKey.X, false))
        {
            CutSelection();
        }
        else if (ImGui.IsKeyPressed(ImGuiKey.V, false))
        {
            Paste(io.KeyShift ? PasteAt.Same : PasteAt.Cursor, ViewMouse);
        }
    }

    /// <summary>A translate drag's group: Alt held duplicates the selection and drags the copies; a
    /// multi-selection moves together. Null: the primary alone (<see cref="EntityDrag"/>).</summary>
    private (string, Dictionary<int, (int X, int Y, int Z)>, (int X, int Y, int Z))? GroupDrag(MapEntity primary)
    {
        if (SelectionKeys(out var layer) is not { } keys || (!ImGui.GetIO().KeyAlt && keys.Count < 2))
        {
            return null;
        }

        IReadOnlyList<int> moved = keys;
        if (ImGui.GetIO().KeyAlt)
        {
            moved = EntityClipboard.Duplicate(_doc!, layer, keys, (0, 0, 0), DuplicateStep.Drag);
            SelectKeys(moved);
            Log.Info("Edit", $"Alt+drag: duplicated as {string.Join(' ', moved.Select(k => "@" + k))}");
        }

        var map = _doc!.Map(layer);
        var starts = new Dictionary<int, (int X, int Y, int Z)>();
        foreach (var key in moved)
        {
            if (map.TryGetEntityAt(key, out var e))
            {
                starts[key] = (e.X, e.Y, e.Z);
            }
        }

        return (layer, starts, (primary.X, primary.Y, primary.Z));
    }

    // The group moved by what the drag gives the primary (one step, merged frame by frame).
    private void DragGroup(PickRay ray)
    {
        var (layer, starts, from) = _groupDrag!.Value;
        var f = _drag!.FieldsFor(ray);
        var (dx, dy, dz) = (f["x"] - from.X, f["y"] - from.Y, f["z"] - from.Z);
        EntityArrange.Move(_doc!, layer, starts.ToDictionary(s => s.Key, s => (s.Value.X + dx, s.Value.Y + dy, s.Value.Z + dz)));
    }
}
