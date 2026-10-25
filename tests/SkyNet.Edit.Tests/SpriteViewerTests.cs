using RetroEng.Content;
using RetroEng.Editor;
using SkyNet.Data;
using SkyNet.Formats;
using SkyNet.Game.Mods;

namespace SkyNet.Edit.Tests;

/// <summary>The sprite viewer (editor-tools plan, T3): what uses a sprite, and "Replace with 3D model..."
/// copying the .glb into the save mod and writing the <c>sprite_models</c> row the game reads.</summary>
[Collection(EditModsCollection.Name)]
public sealed class SpriteViewerTests : IDisposable
{
    private const string ModName = "sprites-test";
    private const int FiftyCal = 25600;
    private readonly string _dir = Directory.CreateTempSubdirectory("skynet-edit-sprites-").FullName;

    public SpriteViewerTests() => ModContent.Unmount();

    public void Dispose()
    {
        ModContent.Unmount();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string Mods => Directory.CreateDirectory(Path.Combine(_dir, "mods")).FullName;

    private string Folder => Directory.CreateDirectory(Path.Combine(Mods, ModName)).FullName;

    [Fact]
    public void A_pickup_sprite_names_its_item_and_key()
    {
        // 200/000: the .50 cal item, which gives the machine gun.
        Assert.Equal(FiftyCal, SpriteUsage.IndexOf(200, 0));
        Assert.Equal("200/000", SpriteViewer.SpriteKey(200, 0));
        Assert.StartsWith("50cal: weapon MACHINE GUN", SpriteUsage.Item(FiftyCal));
        Assert.Null(SpriteUsage.Item(SpriteUsage.IndexOf(300, 1)));

        // Items are drawn 1:1, scenery sprites two units a texel.
        Assert.Equal(1f, SpriteUsage.UnitsPerTexel(FiftyCal));
        Assert.Equal(1f, SpriteUsage.UnitsPerTexel(SpriteUsage.IndexOf(300, 1)));
    }

    [MapDataFact]
    public void Uses_are_the_maps_entities_drawn_as_the_sprite()
    {
        var map = GameData.Open(EditDocumentTests.DataDir!)!.LoadMap(210)!;
        var sprites = map.Entities.Where(e => e.Variant == MapEntityVariant.Sprite && !e.IsMarker).ToList();
        var sprite = sprites.GroupBy(e => e.SpriteIndex).OrderByDescending(g => g.Count()).First();

        var uses = SpriteUsage.Uses([("a", map), ("b", null), ("c", map)], sprite.Key);

        // Each instance's entities in file order; an instance whose map does not load has none; markers never count.
        var offsets = sprite.Select(e => e.FileOffset).ToList();
        Assert.Equal([.. offsets.Select(o => new OutlinerItem("a", o)), .. offsets.Select(o => new OutlinerItem("c", o))], uses);
        var marker = map.Entities.First(e => e.IsMarker);
        Assert.Empty(SpriteUsage.Uses([("a", map)], marker.SpriteIndex));
    }

    [Fact]
    public void Replace_copies_the_model_and_writes_the_row_the_game_reads()
    {
        var outside = Path.Combine(Directory.CreateDirectory(Path.Combine(_dir, "blender")).FullName, "medkit.glb");
        File.WriteAllBytes(outside, [1, 2, 3]);
        var grid = new TableGrid(new SpriteModelSource(), null, Folder);
        var history = new UndoStack();

        // The .glb lands in the mod's models/sprites/; one picked there stays put.
        Assert.Equal("medkit.glb", SpriteModelEdit.CopyIn(outside, Folder));
        var inside = Path.Combine(Folder, "models", "sprites", "medkit.glb");
        Assert.Equal([1, 2, 3], File.ReadAllBytes(inside));
        Assert.Equal("medkit.glb", SpriteModelEdit.CopyIn(inside, Folder));
        Assert.Throws<InvalidDataException>(() => SpriteModelEdit.CopyIn(Path.ChangeExtension(outside, ".obj"), Folder));

        // A new row at defaults, an undo step.
        SpriteModelEdit.SetRow(grid, history, "214/007", "medkit.glb");
        Assert.Equal(new SpriteModelRow("214/007", "medkit.glb", 0, 0, 0, 0, 0), SpriteModelSource.RowOf(grid.Row("214/007")!));
        history.Undo();
        Assert.Null(grid.Row("214/007"));
        history.Redo();

        // Another model for the same sprite keeps the row's placement.
        grid.Set(history, "214/007", SpriteModelSource.ColumnOf("spin"), 90.0);
        SpriteModelEdit.SetRow(grid, history, "214/007", "medkit2.glb");
        Assert.Equal(new SpriteModelRow("214/007", "medkit2.glb", 0, 0, 90, 0, 0), SpriteModelSource.RowOf(grid.Row("214/007")!));

        // Saved: tables/sprite_models.json, as the game reads it through the mod.
        Assert.Equal(["tables/sprite_models.json"], grid.Save());
        VirtualFileSystem mod = new ContentRoot(Mods).Mount([], [ModName]);
        Assert.Equal(new SpriteModelRow("214/007", "medkit2.glb", 0, 0, 90, 0, 0), Assert.Single(SpriteModels.Rows(mod)));

        // Read back into a new grid: the save mod's own row, over an empty base.
        var again = new TableGrid(new SpriteModelSource(), null, Folder);
        Assert.Single(again.Rows);
        Assert.True(again.RowChanged(again.Rows[0]));
        Assert.False(again.Dirty);
    }

    [Fact]
    public void A_row_the_game_would_not_read_is_not_saved()
    {
        var grid = new TableGrid(new SpriteModelSource(), null, Folder);
        var history = new UndoStack();
        SpriteModelEdit.SetRow(grid, history, "214/007", "medkit.glb");
        grid.Set(history, "214/007", SpriteModelSource.ColumnOf("scale"), -1.0);

        Assert.Throws<InvalidDataException>(() => grid.Save());
        Assert.False(File.Exists(Path.Combine(Folder, "tables", "sprite_models.json")));
    }
}
