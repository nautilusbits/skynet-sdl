using System.Diagnostics;
using System.Numerics;
using SkyNet.Game.Audio;

namespace SkyNet.Edit;

/// <summary>View > Tables (editor-tools plan, T6): the game tables as grids (<see cref="TablesWindow"/>),
/// edits on Edit > Undo's stack, Save (the window's, File > Save, table.save) into the save mod.</summary>
internal sealed partial class EditorApp
{
    private TablesWindow? _tables;
    private UnsavedRobots? _unsavedRobots;
    private bool _showTables;
    // The tables the window edits, by name (the MCP table tools list them).
    private string[] _tableNames = [];

    // The window over the data set's models and sounds; its table.* commands registered.
    private void TablesFor(GameAssets assets)
    {
        IReadOnlyList<ITableSource> sources = [.. TableHints.Sources(assets.ModelFiles(), SoundIds.Names), new SoundSettingsSource()];
        _tableNames = [.. sources.Select(s => s.Name)];
        _tables = new TablesWindow(sources, () => EditHistory, () => _saveMod != null ? SaveTarget.Folder(_modsDir, _saveMod, _systemMods, out _) : null);
        _tables.Shown += () => _showTables = true;
        _tables.Saved += () =>
        {
            if (_saveMod != null)
            {
                MountOnTop(_saveMod);
            }
        };
        _tables.Register(_commands);
        EffectsFor(_tables);

        // Unsaved robot types in effect: the shown level built again with them, once the edits pause.
        _unsavedRobots?.Dispose();
        _unsavedRobots = new UnsavedRobots(_tables);
        _unsavedRobots.Changed += () => _rebuildDue = Stopwatch.GetTimestamp() + (long)(RebuildDelay * Stopwatch.Frequency);
    }

    private void TablesFrame(Vector2 at, Vector2 size)
    {
        if (!_showTables || _tables == null)
        {
            return;
        }

        PanelAt(at, size);
        _tables.Draw(ref _showTables);
    }
}
