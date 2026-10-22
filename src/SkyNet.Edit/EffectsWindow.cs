using System.Globalization;
using System.Numerics;
using Hexa.NET.ImGui;
using RetroEng.Commands;
using RetroEng.Diagnostics;
using RetroEng.Editor;
using RetroEng.Json;

namespace SkyNet.Edit;

/// <summary>What the Effects window shows its effect with (<see cref="EffectStage"/> in the editor, a fake in tests).</summary>
public interface IEffectStage
{
    /// <summary>Shows effect <paramref name="key"/> as <paramref name="row"/>'s cells (<paramref name="columns"/>) say; a row
    /// equal to the last one shown keeps the running effect.</summary>
    void Show(string key, IReadOnlyList<GridColumn> columns, object?[] row);

    /// <summary>Fires the shown effect once more (a one-shot effect: an explosion).</summary>
    void Trigger();

    /// <summary>Runs the shown effect <paramref name="seconds"/> on (paused: 0); a one-shot effect that ended starts again.</summary>
    void Advance(float seconds);
}

/// <summary>
/// View > Effect editor (editor-tools plan, T5): the ENHANCED effects (fire, smoke, explosions, sparks, ...) as the
/// <c>effects</c> table's rows. The selected one loops in the window's 3D area (the stage's own view target:
/// <see cref="EffectStage"/>); its fields are a property grid. Edits are cells of the Tables window's
/// grid: Edit > Undo steps, shown at once, amber where they differ from the base, Save writes the changed
/// rows into the save mod.
/// <code>
///   ┌ 3D preview (drag: turn, wheel: zoom) ─────────────────┐
///   │                                                       │
///   ├ effects ─┬ [Pause] [Trigger] [Save] [Reset to default]┤
///   │ fire   * │ size     | 1.5   | Reset                    │
///   │ smoke    │ speed    | 1     | Reset                    │
///   └──────────┴──────────┴───────┴──────────────────────────┘
/// </code>
/// </summary>
public sealed class EffectsWindow(Func<TableGrid?> grid, Func<UndoStack> history) : IPlayback
{
    /// <summary>The window's title.</summary>
    public const string Title = "Effects";
    public const string WindowId = "###Effects";

    private const float ListWidth = 150f, PreviewShare = 0.45f, ResetWidth = 52f, FieldWidth = 90f;
    private const float FloatDragShare = 0.005f, MinFloatDrag = 0.001f;
    private static readonly Vector4 Changed = new(1f, 0.7f, 0.25f, 1f), Panel = new(0.1f, 0.1f, 0.11f, 0.97f);

    private string? _selected;
    private bool _playing = true;
    private double _lastTime = -1.0;

    /// <summary>Whether the preview runs.</summary>
    public bool Playing => _playing;

    public void TogglePlay() => _playing = !_playing;

    /// <summary>The stage the preview runs on (null: no preview, the fields only).</summary>
    public IEffectStage? Stage { get; set; }

    /// <summary>The fields effect <c>key</c> reads (others are shown greyed); null: all.</summary>
    public Func<string, IReadOnlyList<string>>? Reads { get; set; }

    /// <summary>Raised after a save wrote files (the app mounts the mod again).</summary>
    public event Action? Saved;

    /// <summary>Raised when a command shows an effect (the app opens the window).</summary>
    public event Action? Shown;

    /// <summary>The effect selected (its key); the first row's when none was picked.</summary>
    public string? Selected => grid() is { } g ? SelectedRow(g) is { } r ? TableGrid.KeyOf(r) : null : null;

    /// <summary>Selects effect <paramref name="key"/>; false when there is none.</summary>
    public bool Select(string key)
    {
        if (grid()?.Row(key) == null)
        {
            return false;
        }

        _selected = key;
        Shown?.Invoke();
        return true;
    }

    /// <summary>Sets field <paramref name="field"/> of the selected effect as one undo step.</summary>
    public string Set(string field, object? value)
    {
        if (grid() is not { } g || SelectedRow(g) is not { } row)
        {
            return "Effects: no effect";
        }

        return g.Set(history(), TableGrid.KeyOf(row), ColumnOf(g, field), value);
    }

    /// <summary>Puts field <paramref name="field"/> of the selected effect back to its base value.</summary>
    public string Reset(string field) =>
        grid() is { } g && SelectedRow(g) is { } row ? g.RevertCell(history(), TableGrid.KeyOf(row), ColumnOf(g, field)) : "Effects: no effect";

    /// <summary>Puts the selected effect back to its base.</summary>
    public string ResetRow() =>
        grid() is { } g && SelectedRow(g) is { } row ? g.RevertRow(history(), TableGrid.KeyOf(row)) : "Effects: no effect";

    /// <summary>Writes the changed effects into the save mod.</summary>
    public string Save()
    {
        if (grid() is not { } g)
        {
            return "Effects: the table does not read";
        }

        if (g.Folder == null)
        {
            return "No mod to save to (File > Save As picks one)";
        }

        try
        {
            var written = g.Save();
            Saved?.Invoke();
            return $"Effects saved to {g.Folder}: {(written.Count == 0 ? "nothing to write" : string.Join(", ", written))}";
        }
        catch (Exception e) when (e is InvalidDataException or JsonDataException or IOException or UnauthorizedAccessException)
        {
            return $"Effects: not saved ({e.Message})";
        }
    }

    /// <summary>Hands the selected row to the stage and runs it <paramref name="seconds"/> on (the preview's frame step).</summary>
    public void Step(float seconds)
    {
        if (Stage == null || grid() is not { } g || SelectedRow(g) is not { } row)
        {
            return;
        }

        Stage.Show(TableGrid.KeyOf(row), g.Columns, row);
        Stage.Advance(_playing ? seconds : 0f);
    }

    // ---- commands ----

    /// <summary>Adds the effect.* commands to <paramref name="registry"/>.</summary>
    public void Register(CommandRegistry registry)
    {
        string[] Keys() => grid() is { } g ? [.. g.Rows.Select(TableGrid.KeyOf)] : [];
        registry.Add(new Command("effect.open", "effect.open <effect>", a => Select(a.Word(0)) ? $"Effects: {a.Word(0)}" : $"No effect {a.Word(0)}: {string.Join(", ", Keys())}")
        { Help = "shows an effect in the Effects window", Arguments = i => i == 0 ? Keys() : [] });
        registry.Add("effect.trigger", "effect.trigger", _ => Trigger(), "fires the shown effect once more (an explosion)");
        registry.Add("effect.save", "effect.save", _ => Save(), "writes the changed effects into the save mod");
    }

    private string Trigger()
    {
        if (Stage == null || Selected is not { } key)
        {
            return "Effects: no preview";
        }

        Stage.Trigger();
        return $"Effects: {key} triggered";
    }

    // ---- the window ----

    /// <summary>Draws the window; <paramref name="open"/> false when closed.</summary>
    public void Draw(ref bool open)
    {
        const ImGuiWindowFlags flags = ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;
        if (!ImGui.Begin(Title + WindowId, ref open, flags))
        {
            ImGui.End();
            return;
        }

        PlayKey.Poll(this);
        var now = ImGui.GetTime();
        Step(_lastTime < 0.0 ? 0f : (float)(now - _lastTime));
        _lastTime = now;

        var g = grid();
        ImGui.PushStyleColor(ImGuiCol.ChildBg, Panel);
        Preview();
        List(g);
        ImGui.SameLine();
        Fields(g);
        ImGui.PopStyleColor();
        ImGui.End();
    }

    // The effects, changed ones amber with a '*'.
    private void List(TableGrid? g)
    {
        if (!ImGui.BeginChild("##effects", new Vector2(ListWidth, 0f), ImGuiChildFlags.Borders))
        {
            ImGui.EndChild();
            return;
        }

        if (g == null)
        {
            ImGui.TextWrapped("The effects table does not read: see the log.");
        }

        var selected = g != null ? SelectedRow(g) : null;
        foreach (var row in g?.Rows ?? [])
        {
            var key = TableGrid.KeyOf(row);
            var changed = g!.RowChanged(row);
            if (changed)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, Changed);
            }

            if (ImGui.Selectable(changed ? key + " *" : key, ReferenceEquals(row, selected)))
            {
                _selected = key;
            }

            if (changed)
            {
                ImGui.PopStyleColor();
            }
        }

        ImGui.EndChild();
    }

    // The 3D area: the stage's picture (EffectStage.Render draws it).
    private void Preview()
    {
        var at = ImGui.GetCursorScreenPos();
        var avail = ImGui.GetContentRegionAvail();
        var size = Vector2.Max(new Vector2(avail.X, MathF.Round(avail.Y * PreviewShare)), new Vector2(32f));
        if (Stage is EffectStage picture)
        {
            picture.Picture(size);
        }
        else
        {
            ImGui.InvisibleButton("##effectView", size);
        }

        ImGui.GetWindowDrawList().AddText(at + new Vector2(6f, 4f), ImGui.GetColorU32(ImGuiCol.TextDisabled),
            Stage is EffectStage stage ? stage.Hint : "no preview");
    }

    // The toolbar and the selected effect's fields: name, value, Reset (amber: differs from the base).
    private void Fields(TableGrid? g)
    {
        if (!ImGui.BeginChild("##fields", Vector2.Zero, ImGuiChildFlags.Borders))
        {
            ImGui.EndChild();
            return;
        }

        var row = g != null ? SelectedRow(g) : null;
        Toolbar(g, row);
        if (g == null || row == null)
        {
            ImGui.EndChild();
            return;
        }

        const ImGuiTableFlags flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.ScrollY;
        if (ImGui.BeginTable("##props", 3, flags, ImGui.GetContentRegionAvail()))
        {
            ImGui.TableSetupColumn("field", ImGuiTableColumnFlags.WidthFixed, FieldWidth);
            ImGui.TableSetupColumn("value", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("reset", ImGuiTableColumnFlags.WidthFixed, ResetWidth);
            var key = TableGrid.KeyOf(row);
            var reads = Reads?.Invoke(key);
            for (var c = 1; c < g.Columns.Count; c++)
            {
                ImGui.PushID(c);
                Field(g, row, key, c, reads == null || reads.Contains(g.Columns[c].Key) ? FieldUse.Read : FieldUse.Unread);
                ImGui.PopID();
            }

            ImGui.EndTable();
        }

        ImGui.EndChild();
    }

    private void Toolbar(TableGrid? g, object?[]? row)
    {
        ImGui.BeginDisabled(Stage == null);
        if (ImGui.Button(_playing ? "Pause" : "Play"))
        {
            _playing = !_playing;
        }

        PlayKey.Tooltip();

        ImGui.SameLine();
        if (ImGui.Button("Trigger"))
        {
            Log.Info("Edit", Trigger());
        }

        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.BeginDisabled(g?.Folder == null || !g.Dirty);
        if (ImGui.Button("Save"))
        {
            Log.Info("Edit", Save());
        }

        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.BeginDisabled(row == null || !g!.RowChanged(row));
        if (ImGui.Button("Reset to default"))
        {
            Log.Info("Edit", ResetRow());
        }

        ImGui.EndDisabled();
        if (g == null)
        {
            return;
        }

        var state = $"{g.ChangedRows} changed{(g.Dirty ? ", unsaved" : "")} -> {(g.Folder != null ? Path.GetFileName(g.Folder) : "no mod (File > Save As)")}";
        ImGui.TextColored(g.Dirty ? Changed : ImGui.GetStyle().Colors[(int)ImGuiCol.Text], state);
    }

    private void Field(TableGrid g, object?[] row, string key, int c, FieldUse use)
    {
        var column = g.Columns[c];
        var changed = g.CellChanged(row, c);
        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        if (use == FieldUse.Unread)
        {
            ImGui.TextDisabled(column.Key);
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip($"{key} does not read {column.Key}");
            }
        }
        else if (changed)
        {
            ImGui.TextColored(Changed, column.Key);
        }
        else
        {
            ImGui.TextUnformatted(column.Key);
        }

        if (changed && ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(g.BaseOf(row) is { } b ? $"default: {column.Label(b[c])}" : "a row the base does not have");
        }

        ImGui.TableSetColumnIndex(1);
        ImGui.SetNextItemWidth(-1f);
        ImGui.BeginDisabled(column.ReadOnly || use == FieldUse.Unread);
        Edit(g, column, key, c, row[c]);
        ImGui.EndDisabled();

        ImGui.TableSetColumnIndex(2);
        ImGui.BeginDisabled(!changed || g.BaseOf(row) == null);
        if (ImGui.SmallButton("Reset"))
        {
            Log.Info("Edit", g.RevertCell(history(), key, c));
        }

        ImGui.EndDisabled();
    }

    // A field's editor: a drag for numbers (each drag one merged undo step), a combo of choices, else text.
    private void Edit(TableGrid g, GridColumn column, string key, int c, object? value)
    {
        switch (column.Kind)
        {
            case CellKind.Float when value is double or long:
                var f = (float)Convert.ToDouble(value, CultureInfo.InvariantCulture);
                var speed = MathF.Max(MathF.Abs(f) * FloatDragShare, MinFloatDrag);
                if (ImGui.DragFloat("##v", ref f, speed, 0f, 0f, "%.4g"))
                {
                    g.Set(history(), key, c, (double)f);
                }

                return;
            case CellKind.Int when value is long l and >= int.MinValue and <= int.MaxValue:
                var i = (int)l;
                if (ImGui.DragInt("##v", ref i, 0.2f))
                {
                    g.Set(history(), key, c, (long)i);
                }

                return;
            case CellKind.Choice when column.Choices != null:
                if (!ImGui.BeginCombo("##v", column.Label(value), ImGuiComboFlags.HeightLarge))
                {
                    return;
                }

                foreach (var choice in column.Choices)
                {
                    if (ImGui.Selectable(choice.Label, TableGrid.Same(choice.Value, value)) && !TableGrid.Same(choice.Value, value))
                    {
                        g.Set(history(), key, c, choice.Value);
                    }
                }

                ImGui.EndCombo();
                return;
        }

        var text = TableGrid.Text(value);
        var input = column.Kind == CellKind.Text ? ImGuiInputTextFlags.None : ImGuiInputTextFlags.CharsScientific;
        if (ImGui.InputText("##v", ref text, 256, input) && TableGrid.TryParse(column, text, out var parsed) && !TableGrid.Same(parsed, value))
        {
            g.Set(history(), key, c, parsed);
        }
    }

    // Whether the shown effect reads a field (one it does not is greyed: changing it changes nothing).
    private enum FieldUse { Read, Unread }

    // The picked row; the first when none was picked or it went.
    private object?[]? SelectedRow(TableGrid g) => (_selected != null ? g.Row(_selected) : null) ?? (g.Rows.Count > 0 ? g.Rows[0] : null);

    private static int ColumnOf(TableGrid g, string field)
    {
        for (var c = 0; c < g.Columns.Count; c++)
        {
            if (string.Equals(g.Columns[c].Key, field, StringComparison.OrdinalIgnoreCase))
            {
                return c;
            }
        }

        throw new CommandUsageException($"{g.Source.Name} has no field {field}");
    }
}
