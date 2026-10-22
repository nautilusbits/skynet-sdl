using System.Collections.Immutable;
using RetroEng.Diagnostics;
using SkyNet.Data;
using SkyNet.Formats;

namespace SkyNet.Edit;

/// <summary>
/// The Tables grid's robot types in effect before File > Save writes them: while the grid holds unsaved rows,
/// <see cref="GameTables.RobotResolver"/> gives them, so the shown level, Play > Preview and the checks build
/// robots of a type just made unique or changed (as <see cref="PreviewSession"/> wraps the unsaved layers).
/// <code>
///   robots grid edited ─► rows as robots/*.json in memory ─► GameTables.RobotResolver ─► Ais, EnemyTypes, bake
///   saved, or undone to the files ─► the resolver before (the mods' files)
/// </code>
/// </summary>
public sealed class UnsavedRobots : IDisposable
{
    private readonly TablesWindow _tables;
    private readonly Func<ImmutableArray<EnemyType>, ImmutableArray<EnemyType>>? _previous;

    public UnsavedRobots(TablesWindow tables)
    {
        (_tables, _previous) = (tables, GameTables.RobotResolver);
        tables.Edited += Edited;
    }

    /// <summary>Raised after the robot types in effect changed (the app builds the shown level again).</summary>
    public event Action? Changed;

    private void Edited(TableGrid grid)
    {
        if (grid.Source is not RobotTableSource source)
        {
            return;
        }

        try
        {
            GameTables.RobotResolver = grid.Dirty ? source.Resolver(grid.Rows) : _previous;
        }
        catch (InvalidDataException e)
        {
            Log.Warn("Edit", $"Robots: {e.Message}; the saved types stay in effect");
            GameTables.RobotResolver = _previous;
        }

        Changed?.Invoke();
    }

    public void Dispose()
    {
        _tables.Edited -= Edited;
        GameTables.RobotResolver = _previous;
    }
}
