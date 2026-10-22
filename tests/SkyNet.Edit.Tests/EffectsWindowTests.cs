using RetroEng.Commands;
using RetroEng.Content;
using RetroEng.Editor;
using RetroEng.Json;
using SkyNet.Data;
using SkyNet.Game.Audio;
using SkyNet.Game.Enhanced;
using SkyNet.Game.Mods;

namespace SkyNet.Edit.Tests;

/// <summary>The Effects window (editor-tools plan, T5): an edit reaches the preview at once, edits are undo
/// steps, a field or a row resets to the default, and Save writes only the changed effects into the mod.</summary>
[Collection(EditModsCollection.Name)]
public sealed class EffectsWindowTests : IDisposable
{
    private const string ModName = "effects-test";
    private readonly string _mods = Directory.CreateTempSubdirectory("skynet-edit-effects-").FullName;
    private readonly UndoStack _history = new();
    private readonly FakeStage _stage = new();
    private readonly TableGrid _grid;
    private readonly EffectsWindow _window;

    public EffectsWindowTests()
    {
        ModContent.Unmount();
        var source = TableHints.Sources(["DRONE.3D"], SoundIds.Names).Single(s => s.Name == TableNames.Effects);
        _grid = new TableGrid(source, null, Folder);
        _window = new EffectsWindow(() => _grid, () => _history) { Stage = _stage };
    }

    public void Dispose()
    {
        ModContent.Unmount();
        try
        {
            Directory.Delete(_mods, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string Folder => Directory.CreateDirectory(Path.Combine(_mods, ModName)).FullName;

    private double Shown(string field) => Convert.ToDouble(_stage.Row![_grid.Columns.Select(c => c.Key).ToList().IndexOf(field)]);

    [Fact]
    public void Every_effect_is_listed_with_the_games_values()
    {
        Assert.Equal(EffectTable.Names, _grid.Rows.Select(TableGrid.KeyOf));
        Assert.Equal(EffectTable.Fire, _window.Selected);
        Assert.Equal(0, _grid.ChangedRows);
    }

    [Fact]
    public void An_edit_shows_in_the_preview_at_once_and_undo_takes_it_back()
    {
        Assert.True(_window.Select(EffectTable.Smoke));
        _window.Step(0.1f);
        Assert.Equal((EffectTable.Smoke, 1.0), (_stage.Key, Shown(EffectTable.Size)));

        _window.Set(EffectTable.Size, 2.5);
        _window.Step(0.1f);
        Assert.Equal(2.5, Shown(EffectTable.Size));
        Assert.Equal(2.5, EffectStage.RowOf(_grid.Columns, _stage.Row!)!.Size);

        Assert.True(_history.Undo());
        _window.Step(0.1f);
        Assert.Equal(1.0, Shown(EffectTable.Size));
        Assert.False(_grid.Dirty);
    }

    [Fact]
    public void A_field_and_a_row_reset_to_the_default()
    {
        _window.Set(EffectTable.Size, 3.0);
        _window.Set(EffectTable.R, 0.5);
        _window.Reset(EffectTable.Size);
        _window.Step(0f);
        Assert.Equal((1.0, 0.5), (Shown(EffectTable.Size), Shown(EffectTable.R)));

        _window.ResetRow();
        Assert.Equal(0, _grid.ChangedRows);
        Assert.Equal(4, _history.UndoCount);
    }

    [Fact]
    public void Save_writes_only_the_changed_effects_and_the_game_reads_them()
    {
        _window.Select(EffectTable.Explosion);
        _window.Set(EffectTable.Size, 1.75);
        var saved = 0;
        _window.Saved += () => saved++;
        Assert.Contains("tables/effects.json", _window.Save());
        Assert.Equal(1, saved);

        var rows = JsonItem.Parse(File.ReadAllBytes(Path.Combine(Folder, "tables", "effects.json"))).Member("rows").Items;
        Assert.Equal(EffectTable.Explosion, Assert.Single(rows).Member("effect").AsString());

        using var mods = new ContentRoot(_mods).Mount([], [ModName]);
        var table = EffectTable.Of(mods);
        Assert.Equal(1.75, table[EffectTable.Explosion].Size);
        Assert.Equal(EffectTable.Defaults[EffectTable.Fire], table[EffectTable.Fire]);
    }

    [Fact]
    public void A_value_the_game_would_not_read_is_not_previewed_or_saved()
    {
        _window.Set(EffectTable.Size, -1.0);
        _window.Step(0f);
        Assert.Null(EffectStage.RowOf(_grid.Columns, _stage.Row!));
        Assert.Contains("not saved", _window.Save());
        Assert.False(File.Exists(Path.Combine(Folder, "tables", "effects.json")));
    }

    [Fact]
    public void Commands_open_and_trigger_an_effect()
    {
        var registry = new CommandRegistry();
        _window.Register(registry);
        var shown = false;
        _window.Shown += () => shown = true;
        Assert.Equal("Effects: explosion", Assert.Single(registry.Run("effect.open explosion")));
        Assert.True(shown);
        Assert.Contains("triggered", Assert.Single(registry.Run("effect.trigger")));
        Assert.Equal(1, _stage.Triggers);
    }

    [Fact]
    public void The_preview_spawns_its_effect_at_the_origin()
    {
        // Its own view target's scene holds nothing else: no need to stand far from the level any more.
        var stage = new EffectStage(null!);
        var row = _grid.Rows.First(r => TableGrid.KeyOf(r) == EffectTable.Fire);
        stage.Show(EffectTable.Fire, _grid.Columns, row);
        Assert.Equal(System.Numerics.Vector3.Zero, stage.SubjectAt);
    }

    // The window's stage without a renderer: what it was last shown.
    private sealed class FakeStage : IEffectStage
    {
        public string? Key { get; private set; }

        public object?[]? Row { get; private set; }

        public int Triggers { get; private set; }

        public void Show(string key, IReadOnlyList<GridColumn> columns, object?[] row) => (Key, Row) = (key, (object?[])row.Clone());

        public void Trigger() => Triggers++;

        public void Advance(float seconds)
        {
        }
    }
}
