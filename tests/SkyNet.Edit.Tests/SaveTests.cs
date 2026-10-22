using System.Text;
using SkyNet.Formats;
using SkyNet.Game.Baking;
using SkyNet.Game.Campaign;
using SkyNet.Game.Mods;

namespace SkyNet.Edit.Tests;

/// <summary>The editor's tests that mount mods run alone (the mounted mods are global).</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class EditModsCollection
{
    public const string Name = "Editor mounted mods";
}

/// <summary>File > Save (editor plan step E6): the document's layers written to a folder mod, read back
/// by opening the mission with that mod, and played by the game as the editor showed them.</summary>
[Collection(EditModsCollection.Name)]
public sealed class SaveTests : IDisposable
{
    private readonly string _mods = Directory.CreateTempSubdirectory("skynet-edit-save-").FullName;

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

    private static string Snapshot(EditDocument doc)
    {
        var s = new StringBuilder();
        foreach (var layer in doc.Layers)
        {
            s.Append(layer).Append(' ').Append(doc.MapNumber(layer)).Append(' ').Append(Convert.ToHexString(doc.Layer(layer).ToBytes())).Append('\n');
            var map = doc.Map(layer);
            s.AppendJoin('|', map.Names).Append('\n');
            foreach (var e in map.Entities)
            {
                s.Append(EditDocument.EntityText(e)).Append('\n');
            }
        }

        return s.ToString();
    }

    [MapDataFact]
    public void Edit_save_reopen_gives_the_same_document_and_the_game_bakes_the_same_block()
    {
        var data = GameData.Open(EditDocumentTests.DataDir!)!;
        var mission = MissionGenerator.Build(data, MissionTable.For(data.Game).ByNumber(1)!.Value);
        var doc = EditDocument.Load(data, mission, null);
        var layer = mission.World.Start.Instance is var start && mission.World.Instance(start) is { } i ? i.Layer : doc.Layers.First();
        var number = doc.MapNumber(layer);
        Assert.False(doc.IsDirty);

        // Move a mesh, give another one hit points, delete a third, add a crate.
        var meshes = doc.Map(layer).Entities.Where(e => e.Variant == MapEntityVariant.Mesh).ToArray();
        doc.SetFields(layer, meshes[0].FileOffset, new Dictionary<string, int> { ["x"] = meshes[0].X + 512, ["z"] = meshes[0].Z - 256, ["yaw"] = 1024 });
        doc.EndDrag();
        doc.SetField(layer, meshes[1].FileOffset, "hitPoints", 7);
        doc.DeleteEntity(layer, meshes[2].FileOffset);
        doc.AddEntity(layer, new Dictionary<string, int> { ["variant"] = 1, ["x"] = meshes[0].X, ["y"] = meshes[0].Y, ["z"] = meshes[0].Z }, "CRATE04");
        Assert.True(doc.IsDirty);
        Assert.Equal([layer], doc.ChangedLayers.ToArray());

        var folder = SaveTarget.Folder(_mods, "e6", ["skynet", "shock"], out var error);
        Assert.Null(error);
        var written = doc.Save(folder!);
        Assert.Equal([ModInfo.FileName, MapLayer.PathOf(layer)], written.ToArray());
        Assert.False(doc.IsDirty);
        Assert.Empty(doc.ChangedLayers);
        Assert.Equal("e6", ModInfo.Parse(File.ReadAllBytes(Path.Combine(folder!, ModInfo.FileName))).Name);
        var bytes = File.ReadAllBytes(Path.Combine(folder!, MapLayer.PathOf(layer)));
        Assert.Equal(doc.Layer(layer).ToBytes(), bytes);
        Assert.Empty(doc.Save(folder!));

        // Reopened with the mod: the same document, nothing changed.
        ModContent.Mount(_mods, ["e6"]);
        var back = EditDocument.Load(data, mission, ModContent.Files);
        Assert.Equal(Snapshot(doc), Snapshot(back));
        Assert.Equal(bytes, back.Layer(layer).ToBytes());
        Assert.False(back.IsDirty);
        Assert.Empty(back.ChangedLayers);

        // The game's LoadMap plays the saved layer, and its bake equals the editor's in-memory bake.
        var played = data.LoadMap(number)!;
        Assert.Equal(doc.Map(layer).Entities.Select(EditDocument.EntityText), played.Entities.Select(EditDocument.EntityText));
        var game = MapBaker.Bake(data, number)!;
        Assert.Contains(game.Inputs, x => x.Name == BakeInputs.LayerPrefix + layer);
        var editor = MapBaker.Bake(data, number, edited: doc.Map(layer))!;
        // Only the inputs differ: the game's block records the layer file it was baked with.
        typeof(MapBlock).GetProperty(nameof(MapBlock.Inputs))!.SetValue(game, editor.Inputs);
        typeof(MapBlock).GetProperty(nameof(MapBlock.SourceHash))!.SetValue(game, editor.SourceHash);
        Assert.Equal(editor.ToBytes(), game.ToBytes());
    }

    [Fact]
    public void Zip_and_system_mods_are_read_only_and_save_goes_to_the_winning_folder_mod()
    {
        File.WriteAllBytes(Path.Combine(_mods, "packed.zip"), []);
        Directory.CreateDirectory(Path.Combine(_mods, "mine"));
        string[] system = ["skynet", "shock"];
        foreach (var name in new[] { "skynet", "shock", "packed" })
        {
            Assert.Null(SaveTarget.Folder(_mods, name, system, out var error));
            Assert.EndsWith(SaveTarget.ZipReadOnly, error, StringComparison.Ordinal);
        }

        Assert.Null(SaveTarget.Folder(_mods, "..\\x", system, out _));
        Assert.Equal(Path.Combine(_mods, "mine"), SaveTarget.Folder(_mods, "mine", system, out _));
        Assert.Equal(Path.Combine(_mods, "new"), SaveTarget.Folder(_mods, " new ", system, out _));
        Assert.Equal("mine", SaveTarget.Default(_mods, ["packed", "mine"], system));
        Assert.Null(SaveTarget.Default(_mods, ["mine", "packed"], system));
        Assert.Null(SaveTarget.Default(_mods, [], system));
    }

    [Fact]
    public void Save_as_browse_takes_the_mods_folder_or_a_folder_mod_in_it_and_refuses_the_rest()
    {
        Directory.CreateDirectory(Path.Combine(_mods, "mine"));
        Directory.CreateDirectory(Path.Combine(_mods, "skynet"));
        string[] system = ["skynet", "shock"];
        Assert.Equal("typed", SaveTarget.Browsed(_mods, _mods + Path.DirectorySeparatorChar, "typed", system, out var error));
        Assert.Null(error);
        Assert.Equal("mine", SaveTarget.Browsed(_mods, Path.Combine(_mods, "mine"), "typed", system, out _));
        Assert.Null(SaveTarget.Browsed(_mods, Path.Combine(_mods, "skynet"), "typed", system, out error));
        Assert.EndsWith(SaveTarget.ZipReadOnly, error, StringComparison.Ordinal);
        Assert.Null(SaveTarget.Browsed(_mods, Path.Combine(_mods, "mine", "deeper"), "typed", system, out error));
        Assert.NotNull(error);
        Assert.Null(SaveTarget.Browsed(_mods, Path.GetTempPath(), "typed", system, out _));
    }
}
