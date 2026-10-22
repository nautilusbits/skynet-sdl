using RetroEng.Editor;
using SkyNet.Formats;
using SkyNet.Game.Mods;

namespace SkyNet.Edit;

/// <summary>What model an entity draws, as a mod replaces it: a prop's DOS model, a robot type's, a sprite's row.</summary>
public enum ModelSlot { None, Prop, Robot, Sprite }

/// <summary>
/// The model a level entity draws and its "Replace model..." / "Remove replacement" in the Details panel, through
/// <see cref="ModelReplace"/> (the one service writing replacements, the Assets viewer's too).
/// <code>
///   prop    "BOX"      ─► models/BOX.glb + models/BOX.json
///   robot   "FLENCER"  ─► models/FLENCER.robot.glb      (the robot type's model: every robot of it)
///   sprite  "214/007"  ─► models/sprites/X.glb + sprite_models row
/// </code>
/// </summary>
public readonly record struct EntityModel(ModelSlot Slot, string Name)
{
    /// <summary><paramref name="e"/>'s model; <paramref name="robotModel"/> gives a robot type's model file (null: none).</summary>
    public static EntityModel Of(MapFile map, MapEntity e, Func<int, string?> robotModel) => EditorWorld.KindOf(e) switch
    {
        EntityKind.Prop or EntityKind.Actor when map.EntityName(e) is { Length: > 0 } name => new(ModelSlot.Prop, ModelReplace.NameOf(name)),
        EntityKind.Robot when robotModel(e.EnemyType) is { } file => new(ModelSlot.Robot, ModelReplace.NameOf(file)),
        EntityKind.Pickup or EntityKind.Sprite => new(ModelSlot.Sprite, SpriteModels.KeyOf(e.SpriteIndex)),
        _ => new(ModelSlot.None, ""),
    };

    /// <summary>Whether the save mod (<paramref name="modFolder"/>, or the sprite grid) replaces it.</summary>
    public bool Replaced(string modFolder, TableGrid? sprites) => Slot switch
    {
        ModelSlot.Prop => ModelReplace.Has(modFolder, Name, ModelTarget.Prop),
        ModelSlot.Robot => ModelReplace.Has(modFolder, Name, ModelTarget.Robot),
        ModelSlot.Sprite => sprites?.Row(Name) is { } row && sprites.RowChanged(row),
        _ => false,
    };

    /// <summary>Puts <paramref name="glb"/> in as the replacement, one undo step; <paramref name="dosFrames"/>: the DOS
    /// model's frames (a prop's new sidecar covers them all). <see cref="InvalidDataException"/> when it does not read.</summary>
    public string Replace(UndoStack history, string modFolder, string glb, TableGrid? sprites, int dosFrames = 1, Action? changed = null)
    {
        switch (Slot)
        {
            case ModelSlot.Prop:
                return ModelReplace.Replace(history, modFolder, Name, ModelTarget.Prop, glb, dosFrames, changed);
            case ModelSlot.Robot:
                return ModelReplace.Replace(history, modFolder, Name, ModelTarget.Robot, glb, changed: changed);
            case ModelSlot.Sprite when sprites != null:
                var said = ModelReplace.Sprite(sprites, history, modFolder, Name, glb).Said;
                changed?.Invoke();
                return said;
            default:
                return "This entity draws no model a mod replaces";
        }
    }

    /// <summary>Takes the save mod's replacement out, one undo step.</summary>
    public string Remove(UndoStack history, string modFolder, TableGrid? sprites, Action? changed = null)
    {
        switch (Slot)
        {
            case ModelSlot.Prop:
                return ModelReplace.Remove(history, modFolder, Name, ModelTarget.Prop, changed);
            case ModelSlot.Robot:
                return ModelReplace.Remove(history, modFolder, Name, ModelTarget.Robot, changed);
            case ModelSlot.Sprite when sprites != null:
                var said = ModelReplace.RemoveSprite(sprites, history, Name);
                changed?.Invoke();
                return said;
            default:
                return "This entity draws no model a mod replaces";
        }
    }
}
