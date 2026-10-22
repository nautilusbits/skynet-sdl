using System.Collections.Immutable;
using System.Text;
using RetroEng.Content;
using RetroEng.Json;
using SkyNet.Data;

namespace SkyNet.Edit;

/// <summary>
/// A keyed game table (<see cref="TableSchema{T}"/>) in the grid: one column per row key of the file,
/// the base read through the layers under the save mod, the save mod's <c>tables/name.json</c> holding
/// only the rows that differ from it (docs/modding.md#tables).
/// <code>
///   { "table": "weapons", "rows": [ {"index": 1, "viewX": 156, "ammo": 0, ...} ] }
/// </code>
/// </summary>
public sealed class JsonTableSource<TRow> : ITableSource
{
    private readonly TableSchema<ImmutableArray<TRow>> _schema;
    private readonly Func<VirtualFileSystem?, TableFile<ImmutableArray<TRow>>> _load;

    /// <param name="name">The table (its file is <c>tables/name.json</c>).</param>
    /// <param name="schema">How its file reads and writes.</param>
    /// <param name="load">The table through some mods (the shipped or the game's own rows under them).</param>
    /// <param name="hints">Columns edited other than as their JSON value says (a combo, a decimal).</param>
    public JsonTableSource(string name, TableSchema<ImmutableArray<TRow>> schema, Func<VirtualFileSystem?, TableFile<ImmutableArray<TRow>>> load,
        IReadOnlyDictionary<string, GridColumn>? hints = null)
    {
        (Name, _schema, _load) = (name, schema, load);
        var first = Json(load(null)).Items[0];
        Columns = [.. first.Members.Select(m => hints?.GetValueOrDefault(m.Key) ?? new GridColumn(m.Key, KindOf(m.Value)))];
    }

    public string Name { get; }

    public IReadOnlyList<GridColumn> Columns { get; }

    public bool CanAdd => false;

    public string PathOf(string folder) => Path.Combine(folder, GameTables.PathOf(Name));

    public (List<object?[]> Base, List<object?[]> Rows) Load(VirtualFileSystem? under, string? folder)
    {
        var table = _load(under);
        var file = folder != null ? PathOf(folder) : null;
        var own = file != null && File.Exists(file) ? _schema.Apply(table, JsonItem.Parse(File.ReadAllBytes(file))) : table;
        return (Cells(table), Cells(own));
    }

    public IReadOnlyList<string> Save(string folder, IReadOnlyList<object?[]> baseRows, IReadOnlyList<object?[]> rows)
    {
        var path = PathOf(folder);
        var under = baseRows.ToDictionary(TableGrid.KeyOf);
        var changed = rows.Where(r => under.GetValueOrDefault(TableGrid.KeyOf(r)) is not { } b || !b.SequenceEqual(r, CellComparer.Instance)).ToList();
        var relative = GameTables.PathOf(Name);
        if (changed.Count == 0)
        {
            if (!File.Exists(path))
            {
                return [];
            }

            File.Delete(path);
            return [relative + " (removed)"];
        }

        var bytes = Encoding.UTF8.GetBytes(Write(changed));
        // What the game would read: the same rows over the base, or the error before any file changes.
        _schema.Apply(_load(null), JsonItem.Parse(bytes));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return [relative];
    }

    public object?[]? NewRow(object?[] from, IReadOnlyList<object?[]> rows) => null;

    // The file: a header line, then one row a line (as the shipped tables).
    private string Write(IEnumerable<object?[]> rows)
    {
        var lines = rows.Select(r => "    {" + string.Join(", ", Columns.Select((c, i) => $"\"{c.Key}\": {TableGrid.JsonOf(r[i])}")) + "}");
        return $"{{\n  \"table\": \"{Name}\",\n  \"rows\": [\n{string.Join(",\n", lines)}\n  ]\n}}\n";
    }

    private JsonItem Json(TableFile<ImmutableArray<TRow>> table) => _schema.Write(table).Member("rows");

    private List<object?[]> Cells(TableFile<ImmutableArray<TRow>> table) =>
        [.. Json(table).Items.Select(r => Columns.Select(c => TableGrid.CellOf(r.Member(c.Key), c.Kind)).ToArray())];

    private static CellKind KindOf(JsonItem v) => v.Kind switch
    {
        JsonKind.String => CellKind.Text,
        JsonKind.Number when !v.TryInt(out _) && !long.TryParse(v.NumberText, out _) => CellKind.Float,
        _ => CellKind.Int,
    };
}

/// <summary>Cells compared as <see cref="TableGrid.Same"/> does.</summary>
public sealed class CellComparer : IEqualityComparer<object?>
{
    public static readonly CellComparer Instance = new();

    public new bool Equals(object? x, object? y) => TableGrid.Same(x, y);

    public int GetHashCode(object? obj) => obj is long or double ? Convert.ToDouble(obj, System.Globalization.CultureInfo.InvariantCulture).GetHashCode() : obj?.GetHashCode() ?? 0;
}
