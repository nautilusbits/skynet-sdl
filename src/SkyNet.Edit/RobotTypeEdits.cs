using System.Collections.Immutable;
using System.Globalization;
using RetroEng.Editor;
using SkyNet.Data;
using SkyNet.Game.Mods;

namespace SkyNet.Edit;

/// <summary>
/// A robot's type in the Details panel: the type picker's choices and the type's parameters, read and set on the
/// Tables grid's <c>robots</c> rows (so Tables > Save or File > Save writes the type's <c>robots/*.json</c>), and
/// "Make unique": a new mod robot type copied from the instance's, given to that instance only.
/// <code>
///   marker @key ── enemy ──► robots row (type) ◄── every marker of that type
///   Make unique:  robots row N+1 = copy of row type;  marker @key enemy = N+1   (one undo step)
/// </code>
/// </summary>
public static class RobotTypeEdits
{
    /// <summary>The grid keys the Details panel shows, in order.</summary>
    public static readonly ImmutableArray<string> Shown =
    [
        "model", "look", "state", "hitPoints", "speed", "near", "turn", "range", "altitude", "forwardSpeed",
        "fireAmmo", "fireRate", "fireRange", "fireSpeed", "engineSound",
    ];

    private const string Model = "model", Comment = "comment";

    /// <summary>The grid's key of type <paramref name="type"/>.</summary>
    public static string Key(int type) => type.ToString(CultureInfo.InvariantCulture);

    /// <summary>The column of grid key <paramref name="key"/>; -1 when none.</summary>
    public static int Column(TableGrid grid, string key) => grid.Columns.Select(c => c.Key).ToList().IndexOf(key);

    /// <summary>The types a robot may take: every row with a model ("12 flencer").</summary>
    public static IReadOnlyList<DetailChoice> Choices(TableGrid grid) =>
        [.. grid.Rows.Select(r => (Type: (int)(long)r[0]!, Name: NameOf(r[Column(grid, Model)])))
            .Where(t => t.Name.Length > 0)
            .Select(t => new DetailChoice(t.Type, Inv($"{t.Type} {t.Name}")))];

    /// <summary>Type <paramref name="type"/>'s name: its model's ("flencer"); "" when the grid has no such row.</summary>
    public static string Name(TableGrid grid, int type) => grid.Row(Key(type)) is { } row ? NameOf(row[Column(grid, Model)]) : "";

    /// <summary>Type <paramref name="type"/>'s model file ("FLENCER.3D"); null when none.</summary>
    public static string? ModelFile(TableGrid grid, int type) =>
        grid.Row(Key(type)) is { } row && row[Column(grid, Model)] is string m && m.Length > 0 ? m : null;

    /// <summary>Sets key <paramref name="key"/> of type <paramref name="type"/>'s row, one undo step (a run of edits to one cell merges).</summary>
    public static string Set(TableGrid grid, UndoStack history, int type, string key, object? value) =>
        Column(grid, key) is var c and >= 0 ? grid.Set(history, Key(type), c, value) : $"robots: no key {key}";

    /// <summary>
    /// "Make unique": a new type copied from the robot <paramref name="entity"/>'s own, given to that robot only, one
    /// undo step on the document's history (the grid's row and the layer's set together). The new type.
    /// <see cref="MapLayerException"/> when the entity is no robot or no type is free.
    /// </summary>
    public static int MakeUnique(EditDocument doc, TableGrid grid, string layer, int entity)
    {
        if (!doc.Map(layer).TryGetEntityAt(entity, out var e) || EditorWorld.KindOf(e) != EntityKind.Robot)
        {
            throw new MapLayerException($"@{entity} is no robot");
        }

        // The row added on a scratch stack, then replayed as part of the layer's step.
        var scratch = new UndoStack();
        if (grid.AddRow(scratch, Key(e.EnemyType)) is not { } key)
        {
            throw new MapLayerException(Inv($"type {e.EnemyType}: no free robot type (or no such row)"));
        }

        var type = int.Parse(key, CultureInfo.InvariantCulture);
        if (Column(grid, Comment) is var c and >= 0)
        {
            grid.Set(scratch, key, c, Inv($"unique copy of type {e.EnemyType} for {layer} @{entity}"));
        }

        doc.Group("Make unique", () => doc.SetFields(layer, entity, new Dictionary<string, int> { ["enemy"] = type }), new Replay(scratch));
        doc.EndDrag();
        return type;
    }

    private static string NameOf(object? model) =>
        model is string m && m.Length > 0 ? Path.GetFileNameWithoutExtension(m).ToLowerInvariant() : "";

    private static string Inv(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);

    // Edits already done on a scratch stack: redone and undone together.
    private sealed class Replay(UndoStack scratch) : IUndoCommand
    {
        public string Label => "Make unique";

        public void Do()
        {
            while (scratch.Redo())
            {
            }
        }

        public void Undo()
        {
            while (scratch.Undo())
            {
            }
        }

        public bool TryMerge(IUndoCommand next) => false;
    }
}
