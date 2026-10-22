using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Hexa.NET.ImGui;
using RetroEng.Commands;
using RetroEng.Diagnostics;
using RetroEng.Editor;
using SkyNet.Data;
using SkyNet.Formats;
using SkyNet.Game.Behaviour;
using SkyNet.Game.Enemies;
using SkyNet.Game.Mods;

namespace SkyNet.Edit;

/// <summary>
/// The Details panel (Unreal's, kept simple): the selected entities of the shown level as editable rows
/// (<see cref="EntityDetails"/>; a multi-selection's differing values as <see cref="EntityDetails.Mixed"/>, Ctrl+click adds or takes out),
/// a robot's type (<see cref="RobotTypeEdits"/>, the Tables grid's row) with "Make unique", and the entity's
/// model with "Replace model..." / "Remove replacement" (<see cref="EntityModel"/> over <see cref="ModelReplace"/>).
/// Each edit one undo step; the level is baked again from the layer, so the view shows it in either look.
/// </summary>
internal sealed partial class EditorApp
{
    private const string MixedText = EntityDetails.Mixed;
    // Ctrl+clicked entities outlined at most (more stay selected, unoutlined).
    private const int MaxExtraOutlines = 16;
    // A combo gets a filter box from this many choices.
    private const int FilterFrom = 12;
    private const float PositionSpeed = 4f, ValueSpeed = 1f;
    private static readonly Vector4 ExtraColour = new(1f, 0.55f, 0.1f, 1f);
    private static readonly string[] Angles = ["pitch", "yaw", "roll"];

    // The entities Ctrl+clicked besides _selected (of its instance).
    private readonly HashSet<OutlinerItem> _extra = [];
    private SelectionOutline[] _extraOutlines = [];
    // The number or text being dragged or typed (row key, value): a multi-selection's edit lands when let go.
    private (string Key, int Value)? _detailEdit;
    private (string Key, string Text)? _detailText;
    private string _detailFilter = "";

    // Before the level's mark: a re-bake releases what is made after it.
    private void MakeExtraOutlines() =>
        _extraOutlines = [.. Enumerable.Range(0, MaxExtraOutlines).Select(_ => new SelectionOutline(_renderer) { Colour = ExtraColour })];

    private void RegisterDetailCommands()
    {
        _commands.Add("entity.model", "entity.model <key> <file.glb>", a => EntityModelCmd(a.Word(0), a.Word(1)),
            "replaces the model shown entity key draws (prop: models/NAME.glb, robot: its type's NAME.robot.glb, sprite: a sprite_models row) in the save mod; one undo step");
        _commands.Add("entity.unmodel", "entity.unmodel <key>", a => EntityModelCmd(a.Word(0), null),
            "takes the save mod's replacement of shown entity key's model out (one undo step)");
        _commands.Add("entity.add", "entity.add <key>", a => AddSelected(a.Word(0)),
            "adds entity key of the shown level to the selection (Ctrl+click)");
    }

    // ---- the selection ----

    /// <summary>A Ctrl+click on an entity of the selection's instance adds it or takes it out; false: not such a click.</summary>
    private bool AddToSelection(OutlinerItem item)
    {
        if (!ImGui.GetIO().KeyCtrl || item.IsInstance || _selected is not { IsInstance: false } primary || primary.Instance != item.Instance)
        {
            return false;
        }

        ToggleSelected(primary, item);
        return true;
    }

    // The primary taken out: the next one is primary.
    private void ToggleSelected(OutlinerItem primary, OutlinerItem item)
    {
        if (item != primary)
        {
            if (!_extra.Remove(item))
            {
                _extra.Add(item);
            }

            return;
        }

        if (_extra.FirstOrDefault(e => e.Instance == primary.Instance) is { IsInstance: false } next)
        {
            _extra.Remove(next);
            _selected = next;
        }
    }

    private string AddSelected(string word)
    {
        if (!int.TryParse(word, NumberStyles.Integer, CultureInfo.InvariantCulture, out var key) || _levelInstance == null)
        {
            throw new CommandUsageException("entity.add <key>");
        }

        if (_selected is not { IsInstance: false } primary || primary.Instance != _levelInstance)
        {
            Select(new OutlinerItem(_levelInstance, key));
            return Inv($"Selected @{key}");
        }

        ToggleSelected(primary, new OutlinerItem(_levelInstance, key));
        return Inv($"{1 + _extra.Count} selected");
    }

    /// <summary>The selected entities of the shown level as the document has them now: the primary first.</summary>
    private bool SelectedEntities(out string layer, out List<MapEntity> entities)
    {
        entities = [];
        if (!SelectedEntity(out layer, out var first))
        {
            return false;
        }

        entities.Add(first);
        var map = _doc!.Map(layer);
        foreach (var item in _extra.Where(i => i.Instance == _selected!.Value.Instance))
        {
            if (map.TryGetEntityAt(item.Entity, out var e))
            {
                entities.Add(e);
            }
        }

        return true;
    }

    // The Ctrl+clicked entities' outlines, in a colour of their own.
    private void DrawExtraSelection()
    {
        if (_picking == null || _selected is not { IsInstance: false } primary || primary.Instance != _levelInstance)
        {
            return;
        }

        var drawn = 0;
        foreach (var item in _extra.Where(i => i.Instance == primary.Instance))
        {
            if (drawn >= _extraOutlines.Length || !_picking.TryGet(item.Entity, out var target))
            {
                continue;
            }

            _extraOutlines[drawn++].Draw(target.Min, target.Max, target.Triangles.Span);
        }
    }

    // ---- the panel ----

    /// <summary>The Details of the shown level's selected entities; false when none is (the read-only rows then).</summary>
    private bool DetailsPanel()
    {
        if (!SelectedEntities(out var layer, out var entities))
        {
            return false;
        }

        var map = _doc!.Map(layer);
        var robots = RobotGrid();
        var keys = entities.Select(e => e.FileOffset).ToList();
        DetailsHeader(map, entities);
        var choices = robots != null ? RobotTypeEdits.Choices(robots) : [];
        foreach (var section in EntityDetails.Of(map, entities, choices))
        {
            if (!ImGui.CollapsingHeader(section.Title, ImGuiTreeNodeFlags.DefaultOpen))
            {
                continue;
            }

            DetailRows(layer, keys, section);
            if (section.Title == nameof(EntityKind.Robot))
            {
                RobotTypeSection(layer, entities, robots);
            }

            if (section.Title == EntityDetails.Trigger && ImGui.Button("Open in Trigger graph"))
            {
                // GraphFrame focuses the selection's node once _graphFocused differs.
                (_showGraph, _graphFocused) = (true, null);
            }
        }

        ModelSection(map, entities, robots);
        return true;
    }

    private void DetailsHeader(MapFile map, List<MapEntity> entities)
    {
        if (entities.Count > 1)
        {
            ImGui.TextUnformatted(Inv($"{entities.Count} entities selected"));
            ImGui.TextDisabled("Ctrl+click adds or takes out; a value they differ in shows " + MixedText);
            return;
        }

        var e = entities[0];
        var p = BehaviourModel.Position(e);
        ImGui.TextUnformatted(EditorWorld.Label(map, e));
        ImGui.TextDisabled(Inv($"{EditorWorld.KindOf(e)}, key 0x{e.FileOffset:X}, cell {e.CellX},{e.CellZ}, world {p.X:0},{p.Y:0},{p.Z:0}"));
    }

    private void DetailRows(string layer, List<int> keys, DetailSection section)
    {
        if (!ImGui.BeginTable(section.Title, 2, ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.Resizable))
        {
            return;
        }

        foreach (var row in section.Rows)
        {
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            ImGui.TextUnformatted(row.Label);
            ImGui.TableSetColumnIndex(1);
            ImGui.SetNextItemWidth(-1f);
            ImGui.PushID(row.Key);
            DetailWidget(layer, keys, row);
            ImGui.PopID();
        }

        ImGui.EndTable();
    }

    private void DetailWidget(string layer, List<int> keys, DetailRow row)
    {
        switch (row.Kind)
        {
            case DetailKind.Text:
                ImGui.TextUnformatted(row.Text);
                return;
            case DetailKind.Bool:
                var on = row.Value == 1;
                ImGui.PushItemFlag((ImGuiItemFlags)ImGuiItemFlagsPrivate.MixedValue, row.Mixed);
                if (ImGui.Checkbox("##b", ref on))
                {
                    SetDetail(layer, keys, row, row.Mixed || on ? 1 : 0, DetailCommit.Step);
                }

                ImGui.PopItemFlag();
                return;
            case DetailKind.Choice:
                var preview = row.Mixed ? MixedText : row.Show(row.Value!.Value);
                if (Combo(preview, row.Choices ?? [], row.Value) is { } picked)
                {
                    SetDetail(layer, keys, row, picked, DetailCommit.Step);
                }

                return;
            default:
                DetailNumber(layer, keys, row);
                return;
        }
    }

    // A drag (or Ctrl+click typing): one entity follows it live (merged into one step), several get it when let go.
    private void DetailNumber(string layer, List<int> keys, DetailRow row)
    {
        var editing = _detailEdit is { } d && d.Key == row.Key;
        var value = editing ? _detailEdit!.Value.Value : row.Value ?? 0;
        var angle = Angles.Contains(row.Field);
        var position = row.Field is "x" or "y" or "z";

        // An angle wraps round instead of stopping (no clamp: min = max).
        var (min, max) = angle ? (0, 0) : (row.Min, row.Max);
        var changed = ImGui.DragInt("##n", ref value, position ? PositionSpeed : ValueSpeed, min, max, row.Mixed && !editing ? MixedText : "%d");
        if (ImGui.IsItemActivated())
        {
            _doc!.EndDrag();
        }

        if (changed)
        {
            value = angle ? Snap.WrapAngle(value, EntityDrag.AngleUnits) : value;
            _detailEdit = (row.Key, value);
            if (keys.Count == 1)
            {
                SetDetail(layer, keys, row, value, DetailCommit.Drag);
            }
        }

        if (!ImGui.IsItemDeactivated())
        {
            return;
        }

        if (ImGui.IsItemDeactivatedAfterEdit() && keys.Count > 1 && _detailEdit is { } done && done.Key == row.Key)
        {
            SetDetail(layer, keys, row, done.Value, DetailCommit.Step);
        }

        _detailEdit = null;
        _doc!.EndDrag();
    }

    private void SetDetail(string layer, List<int> keys, DetailRow row, int value, DetailCommit commit) =>
        Try(() => EntityDetails.Set(_doc!, layer, keys, row, value, commit));

    // A combo of choices (a filter box when there are many); the one picked, or null.
    private int? Combo(string preview, IReadOnlyList<DetailChoice> choices, int? current)
    {
        if (!ImGui.BeginCombo("##c", preview, ImGuiComboFlags.HeightLarge))
        {
            return null;
        }

        if (ImGui.IsWindowAppearing())
        {
            _detailFilter = "";
            if (choices.Count >= FilterFrom)
            {
                ImGui.SetKeyboardFocusHere();
            }
        }

        if (choices.Count >= FilterFrom)
        {
            ImGui.InputTextWithHint("##filter", "filter", ref _detailFilter, 64);
        }

        int? picked = null;
        foreach (var c in choices)
        {
            if (_detailFilter.Length > 0 && !c.Label.Contains(_detailFilter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (ImGui.Selectable(c.Label, c.Value == current) && c.Value != current)
            {
                picked = c.Value;
            }
        }

        ImGui.EndCombo();
        return picked;
    }

    // ---- the robot's type ----

    /// <summary>The Tables grid's robot types (mod robots included); null when they do not read.</summary>
    private TableGrid? RobotGrid()
    {
        AssetsFor(_data);
        return _tables?.Grid(RobotTableSource.TableName);
    }

    private void RobotTypeSection(string layer, List<MapEntity> entities, TableGrid? grid)
    {
        if (grid == null)
        {
            ImGui.TextDisabled("The robots table does not read (see the log).");
            return;
        }

        var types = entities.Select(e => e.EnemyType).Distinct().ToList();
        if (types.Count != 1)
        {
            ImGui.TextDisabled("The robots selected are of different types.");
            return;
        }

        var type = types[0];
        var name = RobotTypeEdits.Name(grid, type);
        if (!ImGui.CollapsingHeader($"Robot type - affects every {name}###robotType", ImGuiTreeNodeFlags.DefaultOpen))
        {
            return;
        }

        var map = _doc!.Map(layer);
        var users = map.Entities.Count(e => EditorWorld.KindOf(e) == EntityKind.Robot && e.EnemyType == type);
        ImGui.TextDisabled(Inv($"type {type}, {users} on this map; saved as robots/*.json in the save mod"));
        if (grid.Row(RobotTypeEdits.Key(type)) is not { } row)
        {
            ImGui.TextDisabled("The type is not in the table.");
            return;
        }

        if (ImGui.BeginTable("robotType", 2, ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.Resizable))
        {
            foreach (var key in RobotTypeEdits.Shown)
            {
                var c = RobotTypeEdits.Column(grid, key);
                if (c < 0)
                {
                    continue;
                }

                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                ImGui.TextUnformatted(grid.CellChanged(row, c) ? key + " *" : key);
                ImGui.TableSetColumnIndex(1);
                ImGui.SetNextItemWidth(-1f);
                ImGui.PushID(key);
                RobotCell(grid, type, grid.Columns[c], row[c]);
                ImGui.PopID();
            }

            ImGui.EndTable();
        }

        RobotTypeButtons(layer, entities, grid, type);
    }

    private void RobotTypeButtons(string layer, List<MapEntity> entities, TableGrid grid, int type)
    {
        if (entities.Count == 1 && ImGui.Button("Make unique"))
        {
            var key = entities[0].FileOffset;
            Try(() =>
            {
                var made = RobotTypeEdits.MakeUnique(_doc!, grid, layer, key);
                Log.Info("Edit", Inv($"@{key}: robot type {made}, a copy of {type}: its edits affect this robot only (File > Save writes it)"));
            });
        }

        if (entities.Count == 1 && ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("A new mod robot type copied from this one, for this robot only");
        }

        ImGui.SameLine();
        if (ImGui.Button("Open in Tables"))
        {
            _tables?.Show(RobotTableSource.TableName, RobotTypeEdits.Key(type));
        }
    }

    // One cell of the type's row, written through the grid (one undo step when let go).
    private void RobotCell(TableGrid grid, int type, GridColumn column, object? cell)
    {
        var id = "robot." + column.Key;
        switch (column.Kind)
        {
            case CellKind.Choice:
                var choices = (column.Choices ?? []).Select((g, i) => new DetailChoice(i, g.Label)).ToList();
                var at = (column.Choices ?? []).Select((g, i) => (g, i)).FirstOrDefault(p => TableGrid.Same(p.g.Value, cell));
                var current = at.g.Label != null ? at.i : (int?)null;
                if (Combo(column.Label(cell), choices, current) is { } i)
                {
                    Log.Info("Edit", RobotTypeEdits.Set(grid, EditHistory, type, column.Key, column.Choices![i].Value));
                }

                return;
            case CellKind.Text:
                var text = _detailText is { } t && t.Key == id ? t.Text : cell as string ?? "";
                if (ImGui.InputText("##t", ref text, 128))
                {
                    _detailText = (id, text);
                }

                if (ImGui.IsItemDeactivatedAfterEdit())
                {
                    _detailText = null;
                    Log.Info("Edit", RobotTypeEdits.Set(grid, EditHistory, type, column.Key, text));
                }

                return;
            default:
                var value = _detailEdit is { } d && d.Key == id ? d.Value : (int)Convert.ToInt64(cell ?? 0L, CultureInfo.InvariantCulture);
                if (ImGui.DragInt("##n", ref value, ValueSpeed))
                {
                    _detailEdit = (id, value);
                }

                if (ImGui.IsItemDeactivatedAfterEdit())
                {
                    _detailEdit = null;
                    Log.Info("Edit", RobotTypeEdits.Set(grid, EditHistory, type, column.Key, (long)value));
                }

                return;
        }
    }

    // ---- the model ----

    private void ModelSection(MapFile map, List<MapEntity> entities, TableGrid? robots)
    {
        var models = entities.Select(e => ModelOf(map, e, robots)).Distinct().ToList();
        if (models.Count != 1 || models[0].Slot == ModelSlot.None || !ImGui.CollapsingHeader("Model", ImGuiTreeNodeFlags.DefaultOpen))
        {
            return;
        }

        var m = models[0];
        ImGui.TextUnformatted(m.Slot switch
        {
            ModelSlot.Prop => $"{m.Name}: every {m.Name} in the game",
            ModelSlot.Robot => $"{m.Name}{RobotReplacement.Suffix}.glb: every robot built on {m.Name}",
            _ => $"sprite {m.Name}: everything drawn as it",
        });
        if (SaveFolder() is not { } folder)
        {
            ImGui.TextDisabled("File > Save As picks the mod a replacement goes to.");
            return;
        }

        if (ImGui.Button("Replace model..."))
        {
            _dialogs.OpenFile(_window, null, picked =>
            {
                if (picked != null)
                {
                    Log.Info("Edit", ReplaceModel(m, picked));
                }
            }, $"Replace {m.Name} with a 3D model (.glb)");
        }

        if (m.Slot == ModelSlot.Robot ? RobotReplaced(m.Name, folder) : m.Replaced(folder, SpriteGrid(m)))
        {
            ImGui.SameLine();
            if (ImGui.Button("Remove replacement"))
            {
                Log.Info("Edit", RemoveModel(m));
            }
        }

        if (_settings.Look != SkyNet.Game.Menu.Look.Enhanced)
        {
            ImGui.TextDisabled("A replacement shows in the ENHANCED look.");
        }
    }

    private EntityModel ModelOf(MapFile map, MapEntity e, TableGrid? robots) =>
        EntityModel.Of(map, e, t => robots != null ? RobotTypeEdits.ModelFile(robots, t) : null);

    private TableGrid? SpriteGrid(EntityModel m) => m.Slot == ModelSlot.Sprite ? _tables?.Grid(TableNames.SpriteModels) : null;

    private string ReplaceModel(EntityModel m, string glb)
    {
        if (m.Slot == ModelSlot.Robot)
        {
            return RobotReplaceCmd(m.Name, glb);
        }

        if (SaveFolder() is not { } folder)
        {
            return "No mod to write to (File > Save As picks one)";
        }

        var frames = m.Slot == ModelSlot.Prop ? new EnemyModels(_data).Get(m.Name + ".3D")?.Frames ?? 1 : 1;
        try
        {
            // A sprite's .glb is copied in before its row is set: mounted at once so the row's model reads.
            var said = m.Replace(EditHistory, folder, glb, SpriteGrid(m), frames, ModelFilesChanged);
            return said + "; Edit > Undo takes it back";
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return $"{m.Name}: not replaced ({e.Message})";
        }
    }

    private string RemoveModel(EntityModel m)
    {
        if (m.Slot == ModelSlot.Robot)
        {
            return RobotRevertCmd(m.Name);
        }

        if (SaveFolder() is not { } folder)
        {
            return "No mod to write to (File > Save As picks one)";
        }

        try
        {
            return m.Remove(EditHistory, folder, SpriteGrid(m), ModelFilesChanged);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return $"{m.Name}: not removed ({e.Message})";
        }
    }

    // Whether the save mod replaces the whole robot, unsaved edits included.
    private bool RobotReplaced(string model, string folder)
    {
        _robotModels.ModFolder = folder;
        return _robotModels.StateOf(model) is RobotModelState.Saved or RobotModelState.Unsaved;
    }

    // A replacement written or taken back (an undo too): the mod mounted again, the level baked again next frame.
    private void ModelFilesChanged()
    {
        ModelsChanged();
        _rebuildDue = Stopwatch.GetTimestamp();
    }

    // entity.model / entity.unmodel: the shown level's entity key's model (glb null: removed).
    private string EntityModelCmd(string word, string? glb)
    {
        if (!int.TryParse(word, NumberStyles.Integer, CultureInfo.InvariantCulture, out var key))
        {
            throw new CommandUsageException("entity.model <key> <file.glb>");
        }

        if (LayerOf(_levelInstance) is not { } layer || !_doc!.Map(layer).TryGetEntityAt(key, out var e))
        {
            return Inv($"The shown level has no entity {key}");
        }

        var m = ModelOf(_doc.Map(layer), e, RobotGrid());
        if (m.Slot == ModelSlot.None)
        {
            return Inv($"@{key} draws no model a mod replaces");
        }

        return glb != null ? ReplaceModel(m, glb) : RemoveModel(m);
    }

    private static string Inv(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
}
