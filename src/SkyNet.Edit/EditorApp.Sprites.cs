using RetroEng.Commands;
using SkyNet.Data;
using SkyNet.Game.Mods;

namespace SkyNet.Edit;

/// <summary>The sprite viewer's side in the editor (editor-tools plan, T3): the open mission's uses, the
/// <c>sprite_models</c> row in the Tables grid, "Replace with 3D model..." (sprite.model) and sprite.uses.</summary>
internal sealed partial class EditorApp : ISpriteHost
{
    // The sprite viewer for the assets of a data set, its commands registered.
    private void SpritesFor(GameAssets assets)
    {
        assets.SpriteView = new SpriteViewer(this, _modelViewer);
        _commands.Add("sprite.model", "sprite.model <bank/record> <file.glb>", a => SpriteModel(a.Word(0), a.Word(1)),
            "draws the sprite as that .glb in the enhanced look: copies it into the save mod's models/sprites/, sets its sprite_models row (Tables > Save writes it)");
        _commands.Add("sprite.uses", "sprite.uses <bank/record>", a => SpriteUses(a.Word(0)),
            "the sprite's pickup item and the open mission's entities drawn as it");
    }

    IReadOnlyList<OutlinerItem>? ISpriteHost.Uses(int sprite) =>
        _world is { } world ? SpriteUsage.Uses(world.World.Instances.Select(i => (i.Id, world.Map(i.Id))), sprite) : null;

    void ISpriteHost.JumpTo(OutlinerItem item)
    {
        Select(item);
        FlyTo(item);
    }

    SpriteModelRow? ISpriteHost.Mapped(string key) =>
        _tables?.Grid(TableNames.SpriteModels)?.Row(key) is { } row ? SpriteModelSource.RowOf(row) : null;

    string? ISpriteHost.SaveMod => SaveFolder() != null ? _saveMod : null;

    void ISpriteHost.Replace(string key) =>
        _dialogs.OpenFile(_window, null, picked =>
        {
            if (picked != null)
            {
                RunCommand($"sprite.model {key} \"{picked}\"");
            }
        }, $"Replace sprite {key} with a 3D model (.glb)");

    void ISpriteHost.EditRow(string key) => _tables?.Show(TableNames.SpriteModels, key);

    private string? SaveFolder() => _saveMod != null ? SaveTarget.Folder(_modsDir, _saveMod, _systemMods, out _) : null;

    // sprite.model: the .glb into the save mod, the row in the grid (an undo step), the row shown.
    private string SpriteModel(string sprite, string glb)
    {
        if (!SpriteModels.TryKey(sprite, out var index))
        {
            throw new CommandUsageException($"{sprite} is not BANK/RECORD (214/007)");
        }

        if (SaveFolder() is not { } folder || _tables == null)
        {
            return "No mod to write to (File > Save As picks one)";
        }

        string model;
        try
        {
            model = SpriteModelEdit.CopyIn(glb, folder);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return $"{sprite}: not replaced ({e.Message})";
        }

        // Mounted again so the mods' files hold the model (the Model Viewer reads it there).
        MountOnTop(_saveMod!);
        if (_tables.Grid(TableNames.SpriteModels) is not { } grid)
        {
            return $"{sprite}: {TableNames.SpriteModels} does not read (see the log)";
        }

        var key = SpriteModels.KeyOf(index);
        var said = SpriteModelEdit.SetRow(grid, EditHistory, key, model);
        _tables.Show(TableNames.SpriteModels, key);
        return $"{said} -> {SkyNet.Game.Mods.SpriteModels.Folder}{model}; Tables > Save writes it";
    }

    // sprite.uses: the item, then each entity as "instance @offset".
    private string SpriteUses(string sprite)
    {
        if (!SpriteModels.TryKey(sprite, out var index))
        {
            throw new CommandUsageException($"{sprite} is not BANK/RECORD (214/007)");
        }

        var lines = new List<string> { $"{SpriteModels.KeyOf(index)}: {SpriteUsage.Item(index) ?? "no pickup item"}" };
        if (((ISpriteHost)this).Uses(index) is not { } uses)
        {
            lines.Add("no mission open");
            return string.Join("\n", lines);
        }

        lines.Add($"{uses.Count} entities on the open mission");
        lines.AddRange(uses.Select(u => FormattableString.Invariant($"  {u.Instance} @{u.Entity}")));
        return string.Join("\n", lines);
    }
}
