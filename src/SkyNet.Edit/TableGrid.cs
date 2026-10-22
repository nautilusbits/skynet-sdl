using System.Globalization;
using RetroEng.Content;
using RetroEng.Editor;
using RetroEng.Json;
using SkyNet.Data;

namespace SkyNet.Edit;

/// <summary>How a grid cell is edited: a whole number, a decimal, free text, or one of a list.</summary>
public enum CellKind { Int, Float, Text, Choice }

/// <summary>One value a <see cref="CellKind.Choice"/> cell may take, as the combo shows it.</summary>
public readonly record struct GridChoice(object Value, string Label);

/// <summary>A grid column: the file's key, how its cells are edited, the choices of a combo.</summary>
public sealed record GridColumn(string Key, CellKind Kind, IReadOnlyList<GridChoice>? Choices = null, bool ReadOnly = false)
{
    /// <summary>The choice's label for <paramref name="value"/>; the plain value when it is none of them.</summary>
    public string Label(object? value)
    {
        foreach (var c in Choices ?? [])
        {
            if (TableGrid.Same(c.Value, value))
            {
                return c.Label;
            }
        }

        return TableGrid.Text(value);
    }
}

/// <summary>
/// One table the grid edits (editor-tools plan, T6): its columns, its rows as the layers under the save mod
/// give them (the base) and as the save mod's own file changes them, and how the changed rows go back to that mod.
/// Cells are <c>long</c>, <c>double</c>, <c>string</c> or null; column 0 is the row's key.
/// </summary>
public interface ITableSource
{
    /// <summary>The table's name ("weapons"); the file it writes is the source's own.</summary>
    string Name { get; }

    IReadOnlyList<GridColumn> Columns { get; }

    /// <summary>Whether the grid may add rows (a robot type).</summary>
    bool CanAdd { get; }

    /// <summary>The rows under the save mod (<paramref name="under"/>: every mounted mod but it) and the
    /// rows with the save mod's files in <paramref name="folder"/> over them (null folder: the base).</summary>
    (List<object?[]> Base, List<object?[]> Rows) Load(VirtualFileSystem? under, string? folder);

    /// <summary>Writes the rows that differ from <paramref name="baseRows"/> into the mod <paramref name="folder"/>
    /// and removes what the mod no longer needs; returns the files written or removed. A file the game would
    /// not read throws <see cref="InvalidDataException"/> before anything is written.</summary>
    IReadOnlyList<string> Save(string folder, IReadOnlyList<object?[]> baseRows, IReadOnlyList<object?[]> rows);

    /// <summary>A new row copied from <paramref name="from"/> under a free key; null when the table takes none.</summary>
    object?[]? NewRow(object?[] from, IReadOnlyList<object?[]> rows);
}

/// <summary>
/// A table in the grid: the source's base and current rows by key, each edit a step of an
/// <see cref="UndoStack"/> (Edit > Undo), the cells that differ from the base, and Save into the mod.
/// <code>
///   layers under the save mod ──► Base ─┐ diff (amber cells)
///   save mod's file over them ──► Rows ─┘──► Save ──► only the changed rows into the mod
/// </code>
/// </summary>
public sealed class TableGrid
{
    private readonly List<object?[]> _rows;
    private readonly Dictionary<string, object?[]> _base;
    // The rows as last loaded or saved (Dirty compares with them).
    private List<object?[]> _saved;

    public TableGrid(ITableSource source, VirtualFileSystem? under, string? folder)
    {
        Source = source;
        Folder = folder;
        var (baseRows, rows) = source.Load(under, folder);
        _base = baseRows.ToDictionary(KeyOf);
        _rows = rows;
        _saved = Copy(rows);
    }

    public ITableSource Source { get; }

    /// <summary>Raised after the rows changed (an edit, its undo or redo) or were saved.</summary>
    public event Action? Changed;

    /// <summary>The save mod's folder the rows were read from and go to; null: none chosen.</summary>
    public string? Folder { get; }

    public IReadOnlyList<GridColumn> Columns => Source.Columns;

    public IReadOnlyList<object?[]> Rows => _rows;

    /// <summary>Whether the rows differ from the mod's files (an edit not saved).</summary>
    public bool Dirty => _rows.Count != _saved.Count || _rows.Zip(_saved).Any(p => !SameRow(p.First, p.Second));

    /// <summary>Rows that differ from the base (the ones a save writes).</summary>
    public int ChangedRows => _rows.Count(RowChanged);

    /// <summary>The row's key, as text.</summary>
    public static string KeyOf(object?[] row) => Text(row[0]);

    /// <summary>The row of <paramref name="key"/>; null when none.</summary>
    public object?[]? Row(string key) => _rows.Find(r => KeyOf(r) == key);

    /// <summary>The base row of <paramref name="row"/>'s key; null for a row the base does not have.</summary>
    public object?[]? BaseOf(object?[] row) => _base.GetValueOrDefault(KeyOf(row));

    public bool CellChanged(object?[] row, int column) => BaseOf(row) is not { } b || !Same(b[column], row[column]);

    public bool RowChanged(object?[] row) => BaseOf(row) is not { } b || !SameRow(b, row);

    /// <summary>Sets a cell as one undo step (a run of edits to one cell merges into one).</summary>
    public string Set(UndoStack history, string key, int column, object? value)
    {
        if (Row(key) is not { } row)
        {
            return $"{Source.Name}: no row {key}";
        }

        if (Columns[column].ReadOnly || (column == 0))
        {
            return $"{Source.Name}: {Columns[column].Key} is not edited";
        }

        if (Same(row[column], value))
        {
            return $"{Source.Name} {key} {Columns[column].Key} is {Text(value)} already";
        }

        history.Execute(new CellEdit(this, key, column, row[column], value));
        return $"{Source.Name} {key} {Columns[column].Key} = {Text(value)}";
    }

    /// <summary>Puts a cell back to its base value.</summary>
    public string RevertCell(UndoStack history, string key, int column) =>
        Row(key) is { } row && BaseOf(row) is { } b ? Set(history, key, column, b[column]) : $"{Source.Name}: {key} has no base";

    /// <summary>Puts a row back to its base; a row the base does not have goes.</summary>
    public string RevertRow(UndoStack history, string key)
    {
        if (Row(key) is not { } row)
        {
            return $"{Source.Name}: no row {key}";
        }

        if (!RowChanged(row))
        {
            return $"{Source.Name} {key} is the base already";
        }

        history.Execute(new RowEdit(this, key, row, BaseOf(row) is { } b ? (object?[])b.Clone() : null, "Revert"));
        return $"{Source.Name} {key} reverted";
    }

    /// <summary>Adds a row copied from <paramref name="fromKey"/>'s under a free key; returns its key, or null.</summary>
    public string? AddRow(UndoStack history, string fromKey)
    {
        if (!Source.CanAdd || Row(fromKey) is not { } from || Source.NewRow(from, _rows) is not { } row)
        {
            return null;
        }

        var key = KeyOf(row);
        history.Execute(new RowEdit(this, key, null, row, "Add"));
        return key;
    }

    /// <summary>Adds <paramref name="row"/>, or replaces the row of its key, as one undo step.</summary>
    public string Put(UndoStack history, object?[] row)
    {
        var key = KeyOf(row);
        var old = Row(key);
        if (old != null && SameRow(old, row))
        {
            return $"{Source.Name} {key} is so already";
        }

        history.Execute(new RowEdit(this, key, old, row, old == null ? "Add" : "Set"));
        return $"{Source.Name} {key} {(old == null ? "added" : "set")}";
    }

    /// <summary>Writes the changed rows into <see cref="Folder"/>; returns the files written or removed.</summary>
    public IReadOnlyList<string> Save()
    {
        if (Folder == null)
        {
            throw new InvalidOperationException("No mod to save to");
        }

        var written = Source.Save(Folder, [.. _base.Values], _rows);
        _saved = Copy(_rows);
        Changed?.Invoke();
        return written;
    }

    // ---- cells ----

    /// <summary>Whether two cell values are the same (numbers by value, text exactly).</summary>
    public static bool Same(object? a, object? b) => (a, b) switch
    {
        (null, null) => true,
        (null, _) or (_, null) => false,
        (long x, double y) => x == y,
        (double x, long y) => x == y,
        _ => a.Equals(b),
    };

    private static bool SameRow(object?[] a, object?[] b) => a.Length == b.Length && a.Zip(b).All(p => Same(p.First, p.Second));

    /// <summary>A cell as the grid writes it: numbers invariant, null as "-".</summary>
    public static string Text(object? value) => value switch
    {
        null => "-",
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };

    /// <summary><paramref name="text"/> as a value of <paramref name="column"/>; null when it is not one.</summary>
    public static bool TryParse(GridColumn column, string text, out object? value)
    {
        value = null;
        if (column.Choices != null && column.Choices.FirstOrDefault(c => string.Equals(c.Label, text, StringComparison.OrdinalIgnoreCase)
            || string.Equals(Text(c.Value), text, StringComparison.OrdinalIgnoreCase)) is { Value: { } picked })
        {
            value = picked;
            return true;
        }

        switch (column.Kind)
        {
            case CellKind.Text:
                value = text;
                return true;
            case CellKind.Float when double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d):
                value = d;
                return true;
            case CellKind.Int or CellKind.Choice when long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l):
                value = l;
                return true;
            case CellKind.Choice when column.Choices?.FirstOrDefault().Value is string:
                value = text;
                return true;
            default:
                return text == "-" && column.Kind != CellKind.Text;
        }
    }

    /// <summary>A JSON value as a cell: text, a whole number (long), a decimal (double), or null.</summary>
    public static object? CellOf(JsonItem v, CellKind kind) => v.Kind switch
    {
        JsonKind.String => v.AsString(),
        JsonKind.Number when kind == CellKind.Float => v.AsDouble(),
        JsonKind.Number => v.AsLong(),
        _ => null,
    };

    /// <summary>A cell as a JSON value.</summary>
    public static JsonItem JsonOf(object? cell) => cell switch
    {
        long l => l,
        int i => i,
        double d => TableSchemas.Number(d),
        string s => s,
        _ => JsonItem.Null(),
    };

    private static List<object?[]> Copy(List<object?[]> rows) => [.. rows.Select(r => (object?[])r.Clone())];

    private void Put(string key, int column, object? value)
    {
        if (Row(key) is { } row)
        {
            row[column] = value;
        }

        Changed?.Invoke();
    }

    // Replaces the row of key (null: removes it; a row not there is added in key order).
    private void Put(string key, object?[]? row)
    {
        var at = _rows.FindIndex(r => KeyOf(r) == key);
        if (at >= 0)
        {
            _rows.RemoveAt(at);
        }

        if (row != null)
        {
            _rows.Insert(at >= 0 ? at : _rows.Count, (object?[])row.Clone());
        }

        Changed?.Invoke();
    }

    // One cell's edit; the next edit of the same cell merges into it (a typed number, a drag).
    private sealed class CellEdit(TableGrid grid, string key, int column, object? before, object? after) : IUndoCommand
    {
        private object? _after = after;

        public string Label => $"{grid.Source.Name} {key} {grid.Columns[column].Key}";

        public void Do() => grid.Put(key, column, _after);

        public void Undo() => grid.Put(key, column, before);

        public bool TryMerge(IUndoCommand next)
        {
            if (next is not CellEdit e || e.Grid != grid || e.Key != key || e.Column != column)
            {
                return false;
            }

            _after = e._after;
            return true;
        }

        private TableGrid Grid => grid;
        private string Key => key;
        private int Column => column;
    }

    // A whole row replaced, added (before null) or removed (after null).
    private sealed class RowEdit(TableGrid grid, string key, object?[]? before, object?[]? after, string verb) : IUndoCommand
    {
        private readonly object?[]? _before = before != null ? (object?[])before.Clone() : null;

        public string Label => $"{verb} {grid.Source.Name} {key}";

        public void Do() => grid.Put(key, after);

        public void Undo() => grid.Put(key, _before);

        public bool TryMerge(IUndoCommand next) => false;
    }
}
