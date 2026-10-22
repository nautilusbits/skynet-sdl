using RetroEng.Commands;
using RetroEng.Render;
using SkyNet.Formats;
using SkyNet.Game.Mods;

namespace SkyNet.Edit.Tests;

/// <summary>The texture viewer's Material combo (editor-tools plan, T1) as commands: material.set writes
/// the save mod's materials.json, the mounted mod's material is in effect, undo and redo put the file back.</summary>
[Collection(EditModsCollection.Name)]
public sealed class MaterialEditTests : IDisposable
{
    private const string ModName = "materials-test";
    private readonly string _mods = Directory.CreateTempSubdirectory("skynet-edit-materials-").FullName;

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

    private string Folder => Path.Combine(_mods, ModName);

    private string FilePath => Path.Combine(Folder, MaterialTable.FileName);

    [Fact]
    public void A_material_set_by_command_is_written_in_effect_and_undone_by_the_editors_undo()
    {
        Directory.CreateDirectory(Folder);
        ModContent.Mount(_mods, [ModName]);
        // As the editor does: the edits on the open document's stack (Edit > Undo, Ctrl+Z).
        var doc = new EditDocument();
        var edits = new MaterialEdits(GameKind.SkyNet) { ModFolder = Folder, History = doc.History };
        var registry = new CommandRegistry();
        edits.Register(registry);
        var changes = 0;
        // As the editor does: the folder mounted again (its file list is taken at mount).
        edits.Changed += () =>
        {
            changes++;
            ModContent.Mount(_mods, [ModName]);
        };
        var shippedFirst = Materials.Shipped(GameKind.SkyNet).Resolve(230, 14);

        registry.Run("material.set 230/014 metal");
        Assert.True(File.Exists(FilePath));
        Assert.Equal(new Surface(SurfacePreset.Metal), edits.Own(230, 14));
        Assert.Equal(SurfacePreset.Metal, edits.Effective(230, 14).Preset);

        // A second edit on top, then back to the first, then to before any.
        registry.Run("material.set 230/014 screen");
        Assert.Equal(SurfacePreset.Glass, edits.Effective(230, 14).Preset);
        Assert.Equal("Material 230/014 glass", doc.History.UndoLabel);
        Assert.True(doc.Undo());
        Assert.Equal(SurfacePreset.Metal, edits.Effective(230, 14).Preset);
        Assert.True(doc.Undo());
        Assert.False(File.Exists(FilePath));
        Assert.Null(edits.Own(230, 14));
        Assert.Equal(shippedFirst, edits.Effective(230, 14));

        Assert.True(doc.Redo());
        Assert.Equal(SurfacePreset.Metal, edits.Effective(230, 14).Preset);
        Assert.Equal(new Surface(SurfacePreset.Metal), edits.Own(230, 14));
        // Two sets, two undos, a redo.
        Assert.Equal(5, changes);
    }

    [Fact]
    public void Inherit_removes_the_mods_entry_and_keeps_the_others_and_their_tweaks()
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(FilePath, """{ "textures": { "210/001": { "preset": "wood", "roughness": 0.5 }, "210/002": "stone" } }""");
        var edits = new MaterialEdits(GameKind.SkyNet) { ModFolder = Folder };

        edits.Set(210, 1, SurfacePreset.Metal);
        Assert.Equal(new Surface(SurfacePreset.Metal, 0.5f), edits.Own(210, 1));
        edits.Set(210, 2, null);
        Assert.Null(edits.Own(210, 2));
        Assert.Equal(SurfacePreset.Metal, edits.Own(210, 1)!.Value.Preset);
        Assert.Equal(2, edits.History.UndoCount);
    }

    [Fact]
    public void Without_a_save_mod_nothing_is_written()
    {
        var edits = new MaterialEdits(GameKind.SkyNet);
        Assert.Contains("no mod", edits.Set(210, 1, SurfacePreset.Metal));
        Assert.False(edits.History.CanUndo);
    }
}
