using System.Collections.Immutable;
using System.Text;
using RetroEng.Content;
using RetroEng.Editor;
using RetroEng.Json;
using SkyNet.Data;
using SkyNet.Game.Mods;

namespace SkyNet.Edit;

/// <summary>
/// The moddable table <c>sprite_models</c> in the Tables grid (editor-tools plan, T3): a sprite drawn as a
/// mod's glTF model in the enhanced look (<see cref="SpriteModels"/>). No shipped rows: the base is the mods
/// under the save mod over an empty table; rows come from the sprite viewer's "Replace with 3D model...".
/// <code>
///   sprite ▲ | model      | scale | yaw | spin | bob | offsetY
///   214/007  | medkit.glb | 0     | 0   | 90   | 4   | 0          (scale 0: the sprite's height)
/// </code>
/// </summary>
public sealed class SpriteModelSource : ITableSource
{
    public const string Sprite = "sprite", Model = "model";

    private static readonly GridColumn[] Fixed =
    [
        new(Sprite, CellKind.Text, ReadOnly: true), new(Model, CellKind.Text), new("scale", CellKind.Float), new("yaw", CellKind.Float),
        new("spin", CellKind.Float), new("bob", CellKind.Float), new("offsetY", CellKind.Float),
    ];

    public string Name => TableNames.SpriteModels;

    public IReadOnlyList<GridColumn> Columns => Fixed;

    public bool CanAdd => false;

    /// <summary>The column of <paramref name="key"/>.</summary>
    public static int ColumnOf(string key) => Array.FindIndex(Fixed, c => c.Key == key);

    /// <summary>A new row: <paramref name="sprite"/> as <paramref name="model"/> at the sprite's height, unturned, still.</summary>
    public static object?[] NewRow(string sprite, string model) => Cells(new SpriteModelRow(sprite, model, 0, 0, 0, 0, 0));

    /// <summary>A grid row as the table's row.</summary>
    public static SpriteModelRow RowOf(object?[] r) =>
        new((string)r[0]!, (string)r[1]!, Num(r[2]), Num(r[3]), Num(r[4]), Num(r[5]), Num(r[6]));

    public (List<object?[]> Base, List<object?[]> Rows) Load(VirtualFileSystem? under, string? folder)
    {
        var table = GameTables.Layer(Name, TableSchemas.SpriteModels, SpriteModels.Base, under);
        var file = folder != null ? PathOf(folder) : null;
        var own = file != null && File.Exists(file) ? TableSchemas.SpriteModels.Apply(table, JsonItem.Parse(File.ReadAllBytes(file))) : table;
        return ([.. table.Rows.Select(Cells)], [.. own.Rows.Select(Cells)]);
    }

    public IReadOnlyList<string> Save(string folder, IReadOnlyList<object?[]> baseRows, IReadOnlyList<object?[]> rows)
    {
        var relative = GameTables.PathOf(Name);
        var path = PathOf(folder);
        var under = baseRows.ToDictionary(TableGrid.KeyOf);
        var changed = rows.Where(r => under.GetValueOrDefault(TableGrid.KeyOf(r)) is not { } b || !b.SequenceEqual(r, CellComparer.Instance)).ToList();
        if (changed.Count == 0)
        {
            if (!File.Exists(path))
            {
                return [];
            }

            File.Delete(path);
            return [relative + " (removed)"];
        }

        // One row a line, as the other table files; checked as the game reads it before anything is written.
        var lines = changed.Select(r => "    {" + string.Join(", ", Fixed.Select((c, i) => $"\"{c.Key}\": {TableGrid.JsonOf(r[i])}")) + "}");
        var bytes = Encoding.UTF8.GetBytes($"{{\n  \"table\": \"{Name}\",\n  \"rows\": [\n{string.Join(",\n", lines)}\n  ]\n}}\n");
        try
        {
            var read = TableSchemas.SpriteModels.Apply(SpriteModels.Base, JsonItem.Parse(bytes)).Rows;
            if (read.FirstOrDefault(r => !SpriteModels.TryKey(r.Sprite, out _)) is { } bad)
            {
                throw new InvalidDataException($"sprite \"{bad.Sprite}\" is not BANK/RECORD");
            }
        }
        catch (JsonDataException e)
        {
            throw new InvalidDataException($"Table {Name}: {e.Message}", e);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return [relative];
    }

    public object?[]? NewRow(object?[] from, IReadOnlyList<object?[]> rows) => null;

    private string PathOf(string folder) => Path.Combine(folder, GameTables.PathOf(Name));

    private static object?[] Cells(SpriteModelRow r) => [r.Sprite, r.Model, r.Scale, r.Yaw, r.Spin, r.Bob, r.OffsetY];

    private static double Num(object? cell) => cell is null ? 0.0 : Convert.ToDouble(cell, System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// "Replace with 3D model..." (T3): the .glb into the save mod's <c>models/sprites/</c> (copied when it lies elsewhere),
/// then the sprite's <c>sprite_models</c> row pointed at it in the Tables grid, one undo step (Save writes it).
/// <code>
///   pick X.glb ─► mods/mine/models/sprites/X.glb ─► grid row {sprite 214/007, model X.glb, ...} ─► Tables > Save
/// </code>
/// </summary>
public static class SpriteModelEdit
{
    /// <summary>The sprite models' folder in a mod (<see cref="SpriteModels.Folder"/>), apart from the DOS models' replacements.</summary>
    public static readonly string ModelsFolder = Path.Combine("models", "sprites");
    private const string GlbExtension = ".glb";

    /// <summary>Puts <paramref name="glb"/> in <paramref name="modFolder"/>'s models/sprites; returns its file name there.</summary>
    public static string CopyIn(string glb, string modFolder)
    {
        if (!string.Equals(Path.GetExtension(glb), GlbExtension, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"{Path.GetFileName(glb)} is not a .glb");
        }

        var name = Path.GetFileName(glb);
        var target = Path.Combine(modFolder, ModelsFolder, name);
        if (string.Equals(Path.GetFullPath(glb), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
        {
            return name;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(glb, target, overwrite: true);
        return name;
    }

    /// <summary>Points <paramref name="sprite"/>'s row at <paramref name="model"/>: its placement kept, a new row at defaults.</summary>
    public static string SetRow(TableGrid grid, UndoStack history, string sprite, string model)
    {
        var row = grid.Row(sprite) is { } old ? (object?[])old.Clone() : SpriteModelSource.NewRow(sprite, model);
        row[SpriteModelSource.ColumnOf(SpriteModelSource.Model)] = model;
        return grid.Put(history, row);
    }
}
