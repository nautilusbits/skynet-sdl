using System.Numerics;
using Hexa.NET.ImGui;
using RetroEng.Commands;
using RetroEng.Content;
using RetroEng.Diagnostics;
using RetroEng.Editor;
using SkyNet.Game.Mods;

namespace SkyNet.Edit;

/// <summary>
/// View > Tables (editor-tools plan, T6): one game table at a time as a grid, rows × columns, sortable,
/// each cell edited as its kind (a number, text, a combo of names). Cells that differ from the base (the
/// layers under the save mod) are amber; a right click reverts a cell or its row. Edits are steps of the
/// editor's undo stack; Save writes the changed rows into the save mod. The table.* commands do the same.
/// <code>
///   [weapons ▼] [Save] [Revert row] [Revert cell] [Add row]  filter   2 rows changed → mods/mine
///   index ▲ | viewX | ammo        | speed | ...
///   1 UZI   | 156   | 0 hitscan 10| 4500  |
/// </code>
/// </summary>
public sealed class TablesWindow(IReadOnlyList<ITableSource> sources, Func<UndoStack> history, Func<string?> folder)
{
    /// <summary>The window's title.</summary>
    public const string Title = "Tables";

    private static readonly Vector4 Changed = new(0.85f, 0.55f, 0.1f, 0.75f), ChangedRow = new(0.85f, 0.55f, 0.1f, 0.3f),
        Unsaved = new(1f, 0.8f, 0.3f, 1f);

    private readonly Dictionary<string, TableGrid> _grids = [];
    // The mods the grids' bases were read through (a remount reads them again).
    private VirtualFileSystem? _files;
    // The mods under the save mod when they were (UnderKey): the grids' bases.
    private string _underKey = "";
    private string _table = sources[0].Name;
    private string? _selectedKey;
    private int _selectedColumn = -1;
    private string _filter = "";
    private bool _changedOnly;
    private (int Column, bool Ascending) _sort = (0, true);
    // The row the next draw scrolls to (Show with a key).
    private string? _scrollTo;

    /// <summary>Raised after a save wrote files (the app mounts the mod again).</summary>
    public event Action? Saved;

    /// <summary>Raised when a grid's rows changed (an edit, its undo or redo) or were saved.</summary>
    public event Action<TableGrid>? Edited;

    /// <summary>Raised when a command shows a table (the app opens the window).</summary>
    public event Action? Shown;

    /// <summary>The table shown.</summary>
    public string Table => _table;

    /// <summary>Whether any grid holds an edit not saved.</summary>
    public bool Dirty => _grids.Values.Any(g => g.Dirty);

    /// <summary>The grid of table <paramref name="name"/>, read again when the save mod or the mounted mods
    /// changed (and it holds no unsaved edit); null for no such table or a file that does not read.</summary>
    public TableGrid? Grid(string name)
    {
        var target = folder();
        if (!ReferenceEquals(_files, ModContent.Files))
        {
            _files = ModContent.Files;

            // A remount that keeps the mods under the save mod (a save mounting it on top) keeps the grids and the
            // undo steps that point at them; another set of mods reads them again.
            var under = UnderKey(_files, target);
            if (under != _underKey)
            {
                foreach (var stale in _grids.Where(g => !g.Value.Dirty).Select(g => g.Key).ToList())
                {
                    _grids.Remove(stale);
                }
            }

            _underKey = under;
        }

        if (_grids.TryGetValue(name, out var grid) && (grid.Folder == target || grid.Dirty))
        {
            return grid;
        }

        if (sources.FirstOrDefault(s => s.Name == name) is not { } source)
        {
            return null;
        }

        try
        {
            grid = new TableGrid(source, Under(ModContent.Files, target), target);
        }
        catch (InvalidDataException e)
        {
            Log.Error("Edit", $"Table {name}: {e.Message}");
            return null;
        }

        _grids[name] = grid;
        grid.Changed += () => Edited?.Invoke(grid);
        return grid;
    }

    // The mounted mods without the save mod, as text ("name@priority|..."): what a grid's base is read through.
    private static string UnderKey(VirtualFileSystem? files, string? target)
    {
        var name = target != null ? Path.GetFileName(target) : null;
        return string.Join('|', (files?.Mounts ?? []).Where(m => !string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase))
            .Select(m => FormattableString.Invariant($"{m.Name}@{m.Priority}")));
    }

    // The mounted mods without the save mod (its own files are read from its folder). Shares their mounts: never disposed.
    private static VirtualFileSystem? Under(VirtualFileSystem? files, string? target)
    {
        if (files == null)
        {
            return null;
        }

        var name = target != null ? Path.GetFileName(target) : null;
        var vfs = new VirtualFileSystem();
        foreach (var m in files.Mounts.Where(m => !string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            vfs.Mount(m.Mount, m.Priority, m.Name);
        }

        return vfs;
    }

    /// <summary>Shows table <paramref name="name"/>; false when there is none.</summary>
    public bool Show(string name)
    {
        if (sources.All(s => s.Name != name))
        {
            return false;
        }

        _table = name;
        Shown?.Invoke();
        return true;
    }

    /// <summary>Shows table <paramref name="name"/> with row <paramref name="key"/> selected and scrolled to; false when there is none.</summary>
    public bool Show(string name, string key)
    {
        if (!Show(name))
        {
            return false;
        }

        (_selectedKey, _selectedColumn, _scrollTo, _filter, _changedOnly) = (key, 0, key, "", false);
        return true;
    }

    /// <summary>Saves every grid with unsaved edits into the save mod; returns what was done.</summary>
    public string Save()
    {
        if (folder() is not { } target)
        {
            return "No mod to save to (File > Save As picks one)";
        }

        var lines = new List<string>();
        foreach (var grid in _grids.Values.Where(g => g.Dirty).ToList())
        {
            if (grid.Folder != target)
            {
                lines.Add($"{grid.Source.Name}: read for another mod, not saved");
                continue;
            }

            try
            {
                var written = grid.Save();
                lines.Add($"{grid.Source.Name}: {(written.Count == 0 ? "nothing to write" : string.Join(", ", written))}");
            }
            catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                lines.Add($"{grid.Source.Name}: not saved ({e.Message})");
            }
        }

        if (lines.Count == 0)
        {
            return "Tables: nothing changed";
        }

        Saved?.Invoke();
        return $"Tables saved to {target}: " + string.Join("; ", lines);
    }

    // ---- commands ----

    /// <summary>Adds the table.* commands to <paramref name="registry"/>.</summary>
    public void Register(CommandRegistry registry)
    {
        var names = sources.Select(s => s.Name).ToArray();
        registry.Add(new Command("table.list", "table.list", _ =>
            string.Join("\n", sources.Select(s => Grid(s.Name) is { } g ? $"{s.Name}: {g.Rows.Count} rows, {g.ChangedRows} changed{(g.Dirty ? ", unsaved" : "")}" : $"{s.Name}: does not read")))
        { Help = "the tables the Tables window edits" });
        registry.Add(new Command("table.open", "table.open <table>", a => Show(a.Word(0)) ? $"Tables: {a.Word(0)}" : $"No table {a.Word(0)}: {string.Join(", ", names)}")
        { Help = "shows a table in the Tables window", Arguments = i => i == 0 ? names : [] });
        registry.Add(new Command("table.get", "table.get <table> <key> [column]", a => WithGrid(a, (g, row) =>
            a.OptionalWord(2) is { } column ? $"{a[1]} {column} = {Cell(g, row, column)}" : string.Join(", ", g.Columns.Select((c, i) => $"{c.Key} {TableGrid.Text(row[i])}"))))
        { Help = "a row's cells, or one", Arguments = i => i == 0 ? names : [] });
        registry.Add(new Command("table.set", "table.set <table> <key> <column> <value>", a => WithGrid(a, (g, row) =>
        {
            var c = ColumnOf(g, a.Word(2));
            return TableGrid.TryParse(g.Columns[c], a.Rest(3), out var value) ? g.Set(history(), a[1], c, value) : $"{a.Rest(3)} is not a {g.Columns[c].Kind} value";
        }))
        { Help = "sets a cell (an undo step; table.save writes it)", Arguments = i => i == 0 ? names : [] });
        registry.Add(new Command("table.revert", "table.revert <table> <key> [column]", a => WithGrid(a, (g, _) =>
            a.OptionalWord(2) is { } column ? g.RevertCell(history(), a[1], ColumnOf(g, column)) : g.RevertRow(history(), a[1])))
        { Help = "puts a row or a cell back to the base", Arguments = i => i == 0 ? names : [] });
        registry.Add(new Command("table.add", "table.add <table> <from key>", a => WithGrid(a, (g, _) =>
            g.AddRow(history(), a[1]) is { } key ? $"{g.Source.Name}: row {key} added" : $"{g.Source.Name} takes no new rows"))
        { Help = "adds a row copied from another (a robot type: a new type over the same base)", Arguments = i => i == 0 ? names : [] });
        registry.Add("table.save", "table.save", _ => Save(), "writes the changed rows of every table into the save mod");
    }

    private string WithGrid(CommandArgs a, Func<TableGrid, object?[], string> run)
    {
        if (Grid(a.Word(0)) is not { } grid)
        {
            return $"No table {a.Word(0)}";
        }

        return grid.Row(a.Word(1)) is { } row ? run(grid, row) : $"{a[0]}: no row {a[1]}";
    }

    private static int ColumnOf(TableGrid g, string key) =>
        g.Columns.Select((c, i) => (c, i)).FirstOrDefault(p => string.Equals(p.c.Key, key, StringComparison.OrdinalIgnoreCase), (null!, -1)).Item2 is var at and >= 0
            ? at : throw new CommandUsageException($"{g.Source.Name} has no column {key}");

    private static string Cell(TableGrid g, object?[] row, string column) => TableGrid.Text(row[ColumnOf(g, column)]);

    // ---- the window ----

    /// <summary>Draws the window; <paramref name="open"/> false when it was closed.</summary>
    public void Draw(ref bool open)
    {
        if (!ImGui.Begin(Title, ref open))
        {
            ImGui.End();
            return;
        }

        var grid = Grid(_table);
        Toolbar(grid);
        if (grid != null)
        {
            Sheet(grid);
        }

        ImGui.End();
    }

    private void Toolbar(TableGrid? grid)
    {
        ImGui.SetNextItemWidth(160f);
        if (ImGui.BeginCombo("##table", _table))
        {
            foreach (var s in sources)
            {
                var dirty = _grids.TryGetValue(s.Name, out var g) && g.Dirty;
                if (ImGui.Selectable(s.Name + (dirty ? " *" : ""), s.Name == _table))
                {
                    (_table, _selectedKey, _selectedColumn, _sort) = (s.Name, null, -1, (0, true));
                }
            }

            ImGui.EndCombo();
        }

        var target = folder();
        ImGui.SameLine();
        ImGui.BeginDisabled(target == null || !Dirty);
        if (ImGui.Button("Save"))
        {
            Log.Info("Edit", Save());
        }

        ImGui.EndDisabled();
        var row = _selectedKey != null ? grid?.Row(_selectedKey) : null;
        ImGui.SameLine();
        ImGui.BeginDisabled(row == null || !grid!.RowChanged(row));
        if (ImGui.Button("Revert row"))
        {
            Log.Info("Edit", grid!.RevertRow(history(), _selectedKey!));
        }

        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.BeginDisabled(row == null || _selectedColumn <= 0 || grid!.BaseOf(row) == null || !grid.CellChanged(row, _selectedColumn));
        if (ImGui.Button("Revert cell"))
        {
            Log.Info("Edit", grid!.RevertCell(history(), _selectedKey!, _selectedColumn));
        }

        ImGui.EndDisabled();
        if (grid?.Source.CanAdd == true)
        {
            ImGui.SameLine();
            ImGui.BeginDisabled(row == null);
            if (ImGui.Button("Add row"))
            {
                _selectedKey = grid.AddRow(history(), _selectedKey!) ?? _selectedKey;
            }

            ImGui.EndDisabled();
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(140f);
        ImGui.InputTextWithHint("##tableFilter", "filter", ref _filter, 64);
        ImGui.SameLine();
        ImGui.Checkbox("changed only", ref _changedOnly);
        if (grid == null)
        {
            ImGui.TextColored(Unsaved, "The table does not read: see the log.");
            return;
        }

        var state = $"{grid.ChangedRows} rows differ from the base{(grid.Dirty ? ", unsaved" : "")} -> {(target != null ? Path.GetFileName(target) : "no mod (File > Save As)")}";
        if (grid.Dirty)
        {
            ImGui.TextColored(Unsaved, state);
        }
        else
        {
            ImGui.TextUnformatted(state);
        }
    }

    private void Sheet(TableGrid grid)
    {
        var columns = grid.Columns;
        const ImGuiTableFlags flags = ImGuiTableFlags.Sortable | ImGuiTableFlags.Resizable | ImGuiTableFlags.ScrollX | ImGuiTableFlags.ScrollY
            | ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.BordersOuter | ImGuiTableFlags.SizingFixedFit;
        if (!ImGui.BeginTable("##grid", columns.Count, flags, ImGui.GetContentRegionAvail()))
        {
            return;
        }

        ImGui.TableSetupScrollFreeze(1, 1);
        for (var c = 0; c < columns.Count; c++)
        {
            var width = columns[c].Kind == CellKind.Choice || c == 0 ? 130f : columns[c].Kind == CellKind.Text ? 110f : 64f;
            ImGui.TableSetupColumn(columns[c].Key, c == 0 ? ImGuiTableColumnFlags.DefaultSort | ImGuiTableColumnFlags.NoHide : ImGuiTableColumnFlags.None, width);
        }

        ImGui.TableHeadersRow();
        var specs = ImGui.TableGetSortSpecs();
        if (!specs.IsNull && specs.SpecsCount > 0)
        {
            _sort = (specs.Specs.ColumnIndex, specs.Specs.SortDirection != ImGuiSortDirection.Descending);
        }

        foreach (var row in Ordered(grid))
        {
            var key = TableGrid.KeyOf(row);
            ImGui.TableNextRow();
            ImGui.PushID(key);
            for (var c = 0; c < columns.Count; c++)
            {
                ImGui.TableSetColumnIndex(c);
                ImGui.PushID(c);
                Cell(grid, row, key, c);
                ImGui.PopID();
            }

            ImGui.PopID();
            if (_scrollTo == key)
            {
                ImGui.SetScrollHereY(0.5f);
                _scrollTo = null;
            }
        }

        ImGui.EndTable();
    }

    // The rows shown: filtered, then sorted by the header's column.
    private IEnumerable<object?[]> Ordered(TableGrid grid)
    {
        var rows = grid.Rows.Where(r => (!_changedOnly || grid.RowChanged(r))
            && (_filter.Length == 0 || r.Select((v, i) => grid.Columns[i].Label(v)).Any(t => t.Contains(_filter, StringComparison.OrdinalIgnoreCase))));
        var (column, ascending) = _sort;
        if (column < 0 || column >= grid.Columns.Count)
        {
            return rows;
        }

        var sorted = rows.OrderBy(r => r[column], CellOrder.Instance);
        return ascending ? sorted : sorted.Reverse();
    }

    private void Cell(TableGrid grid, object?[] row, string key, int c)
    {
        var column = grid.Columns[c];
        // The changed cells amber, a changed row's key faintly so.
        var changed = grid.CellChanged(row, c);
        if (changed || (c == 0 && grid.RowChanged(row)))
        {
            ImGui.TableSetBgColor(ImGuiTableBgTarget.CellBg, ImGui.GetColorU32(changed ? Changed : ChangedRow));
        }

        var value = row[c];
        if (c == 0 || column.ReadOnly)
        {
            if (ImGui.Selectable(column.Label(value), _selectedKey == key))
            {
                (_selectedKey, _selectedColumn) = (key, c);
            }
        }
        else
        {
            ImGui.SetNextItemWidth(-1f);
            Edit(grid, column, key, c, value);
        }

        if (ImGui.IsItemActivated() || ImGui.IsItemClicked(ImGuiMouseButton.Right))
        {
            (_selectedKey, _selectedColumn) = (key, c);
        }

        if (changed && ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(grid.BaseOf(row) is { } b ? $"base: {column.Label(b[c])}" : "a row the base does not have");
        }

        if (ImGui.BeginPopupContextItem("##cell"))
        {
            if (ImGui.MenuItem("Revert cell", "", false, changed && c > 0 && grid.BaseOf(row) != null))
            {
                Log.Info("Edit", grid.RevertCell(history(), key, c));
            }

            if (ImGui.MenuItem("Revert row", "", false, grid.RowChanged(row)))
            {
                Log.Info("Edit", grid.RevertRow(history(), key));
            }

            ImGui.EndPopup();
        }
    }

    // A cell's editor: a combo of its choices, else text parsed as the column's kind (each change one merged undo step).
    private void Edit(TableGrid grid, GridColumn column, string key, int c, object? value)
    {
        if (column.Kind == CellKind.Choice && column.Choices != null)
        {
            if (!ImGui.BeginCombo("##v", column.Label(value), ImGuiComboFlags.HeightLarge))
            {
                return;
            }

            foreach (var choice in column.Choices)
            {
                if (ImGui.Selectable(choice.Label, TableGrid.Same(choice.Value, value)) && !TableGrid.Same(choice.Value, value))
                {
                    grid.Set(history(), key, c, choice.Value);
                }
            }

            ImGui.EndCombo();
            return;
        }

        var text = TableGrid.Text(value);
        var input = column.Kind == CellKind.Text ? ImGuiInputTextFlags.None : ImGuiInputTextFlags.CharsScientific;
        if (ImGui.InputText("##v", ref text, 256, input) && TableGrid.TryParse(column, text, out var parsed) && !TableGrid.Same(parsed, value))
        {
            grid.Set(history(), key, c, parsed);
        }
    }

    // Cells in sort order: numbers by value before text, null first.
    private sealed class CellOrder : IComparer<object?>
    {
        public static readonly CellOrder Instance = new();

        public int Compare(object? x, object? y) => (x, y) switch
        {
            (null, null) => 0,
            (null, _) => -1,
            (_, null) => 1,
            (string a, string b) => string.CompareOrdinal(a, b),
            (string, _) => 1,
            (_, string) => -1,
            _ => Convert.ToDouble(x, System.Globalization.CultureInfo.InvariantCulture).CompareTo(Convert.ToDouble(y, System.Globalization.CultureInfo.InvariantCulture)),
        };
    }
}
