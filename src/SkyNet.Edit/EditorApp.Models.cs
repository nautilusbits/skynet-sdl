using System.Globalization;
using System.Numerics;
using RetroEng.Commands;
using RetroEng.Diagnostics;
using SkyNet.Data;
using SkyNet.Game.Enemies;
using SkyNet.Game.Mods;

namespace SkyNet.Edit;

/// <summary>The model viewer's mod side in the editor: "Replace with 3D model..." (model.replace), "Remove
/// replacement" (model.remove), the sidecar's fields (model.sidecar) and the comparison toggle (model.original),
/// each write one undo step through <see cref="ModelReplace"/>; a whole robot's "Replace with glTF..."
/// (robot.replace), "Revert to original" (robot.revert) and "Export as glTF..." (robot.gltf), its replace and
/// revert unsaved until File > Save (<see cref="RobotModelEdits"/>, previewed through <see cref="MountMods"/>).</summary>
internal sealed partial class EditorApp : IModelHost
{
    private const string On = "on", Off = "off";

    // The whole-robot replacements not saved yet.
    private readonly RobotModelEdits _robotModels = new();

    // The commands of the model viewer's actions.
    private void ModelsFor(ModelViewer viewer)
    {
        viewer.Host = this;
        _robotModels.Changed -= RobotModelsChanged;
        _robotModels.Changed += RobotModelsChanged;
        _commands.Add("model.replace", "model.replace <model> <file.glb>", a => ModelReplaceCmd(a.Word(0), ModelTarget.Prop, a.Word(1)),
            "copies the .glb into the save mod as models/NAME.glb with a NAME.json sidecar (defaults when new); Edit > Undo takes it back");
        _commands.Add("robot.replace", "robot.replace <model|type> <file.glb|file.gltf>", a => RobotReplaceCmd(RobotModel(a.Word(0)), a.Word(1)),
            "the whole robot as one glTF model, models/NAME.robot.glb in the save mod (a .gltf packed with its files); unsaved until File > Save, Edit > Undo takes it back");
        _commands.Add("model.remove", "model.remove <model>", a => ModelRemove(a.Word(0), ModelTarget.Prop),
            "deletes the save mod's models/NAME.glb and NAME.json (one undo step)");
        _commands.Add("robot.revert", "robot.revert <model|type>", a => RobotRevertCmd(RobotModel(a.Word(0))),
            "the original robot again: File > Save deletes the save mod's models/NAME.robot.glb (one undo step)");
        _commands.Add("robot.remove", "robot.remove <model|type>", a => RobotRevertCmd(RobotModel(a.Word(0))), "robot.revert");
        _commands.Add("robot.gltf", "robot.gltf <type> [dir]", a => RobotGltf(a.Word(0), a.OptionalWord(1) ?? GltfExport.DefaultDir),
            "writes robot type's assembled model as NAME.robot.glb (a joint per segment, a clip per AIS block): a start for robot.replace");
        _commands.Add("model.sidecar", "model.sidecar <model> <rotation|fit|scale|align|offset|frames> <value>",
            a => ModelSidecarCmd(a.Word(0), a.Word(1), a.Word(2)),
            "sets a field of the save mod's models/NAME.json (rotation/offset x,y,z; fit none|height|width|box|stretch; align base|centre|origin; frames all|0,2-5)");
        _commands.Add("model.original", "model.original on|off", a => ModelOriginal(a.Word(0)),
            "the Model Viewer draws a replaced model or robot as the original (on) or the replacement (off)");
    }

    string? IModelHost.SaveFolder => SaveFolder();

    void IModelHost.Export(string model) => ExportGltf(model);

    bool IModelHost.Enhanced => _settings.Look == SkyNet.Game.Menu.Look.Enhanced;

    void IModelHost.UseEnhanced()
    {
        _settings.Look = SkyNet.Game.Menu.Look.Enhanced;
        Reload();
    }

    void IModelHost.Replace(string model, ModelTarget target) =>
        _dialogs.OpenFile(_window, null, picked =>
        {
            if (picked != null)
            {
                RunCommand($"{(target == ModelTarget.Robot ? "robot" : "model")}.replace {model} \"{picked}\"");
            }
        }, $"Replace {model} with a 3D model (.glb)");

    void IModelHost.Remove(string model, ModelTarget target) =>
        RunCommand(target == ModelTarget.Robot ? $"robot.revert {model}" : $"model.remove {model}");

    RobotModelState IModelHost.RobotState(string model)
    {
        _robotModels.ModFolder = SaveFolder();
        return _robotModels.StateOf(model);
    }

    void IModelHost.ExportRobot(int type) =>
        _dialogs.OpenFolder(_window, null, picked =>
        {
            if (picked != null)
            {
                RunCommand(string.Create(CultureInfo.InvariantCulture, $"robot.gltf {type} \"{picked}\""));
            }
        }, "Export the robot as glTF");

    void IModelHost.OpenModel(string model) => RunCommand($"asset.open models {model}");

    void IModelHost.SetSidecar(string model, ModelSidecar sidecar) => RetroEng.Diagnostics.Log.Info("Edit", SidecarWrite(model, sidecar));

    // model.replace / robot.replace: the files into the save mod (one undo step), the mod mounted again.
    private string ModelReplaceCmd(string model, ModelTarget target, string glb)
    {
        if (SaveFolder() is not { } folder)
        {
            return "No mod to write to (File > Save As picks one)";
        }

        var name = ModelReplace.NameOf(model);
        var frames = new EnemyModels(_data).Get(name + ".3D")?.Frames ?? 1;
        try
        {
            return ModelReplace.Replace(EditHistory, folder, name, target, glb, frames, ModelsChanged) + "; Edit > Undo takes it back";
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return $"{name}: not replaced ({e.Message})";
        }
    }

    private string ModelRemove(string model, ModelTarget target)
    {
        if (SaveFolder() is not { } folder)
        {
            return "No mod to write to (File > Save As picks one)";
        }

        try
        {
            return ModelReplace.Remove(EditHistory, folder, model, target, ModelsChanged);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return $"{model}: not removed ({e.Message})";
        }
    }

    // robot.replace: staged unsaved (one undo step), the mods composed again.
    private string RobotReplaceCmd(string model, string file)
    {
        if ((_robotModels.ModFolder = SaveFolder()) == null)
        {
            return "No mod to write to (File > Save As picks one)";
        }

        try
        {
            return _robotModels.Replace(EditHistory, model.ToUpperInvariant(), file) + "; Edit > Undo takes it back";
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return $"{model}: not replaced ({e.Message})";
        }
    }

    private string RobotRevertCmd(string model)
    {
        if ((_robotModels.ModFolder = SaveFolder()) == null)
        {
            return "No mod to write to (File > Save As picks one)";
        }

        return _robotModels.Revert(EditHistory, model.ToUpperInvariant());
    }

    // robot.gltf: the type's template (whatever segments it has) into dir.
    private string RobotGltf(string word, string dir)
    {
        var types = GameTables.Ais.Types;
        if (!int.TryParse(word, NumberStyles.None, CultureInfo.InvariantCulture, out var type) || type >= types.Length)
        {
            throw new CommandUsageException("robot.gltf <type> [dir]");
        }

        if (new GltfExport(_data, GameTables.Ais).Export(types[type]) is not { } glb)
        {
            return $"type {type}: its model does not load";
        }

        var path = Path.Combine(dir, GltfExport.FileName(ModelReplace.NameOf(types[type].ModelFile).ToUpperInvariant(), RobotSegments.With));
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(path, glb);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return $"type {type}: not exported ({e.Message})";
        }

        return $"type {type}: glTF written to {Path.GetFullPath(path)}";
    }

    // The unsaved robot edits written with File > Save into folder (the save mod's).
    private IReadOnlyList<string> SaveRobotModels(string folder)
    {
        if (!_robotModels.Dirty)
        {
            return [];
        }

        _robotModels.ModFolder = folder;
        return _robotModels.Save();
    }

    /// <summary>The mods <paramref name="enabled"/> mounted for the game, the unsaved robot edits over the save mod.</summary>
    private void MountMods(IReadOnlyList<string> enabled)
    {
        if (!_robotModels.Dirty || _saveMod == null)
        {
            ModContent.Mount(_modsDir, enabled);
            return;
        }

        ModContent.Mount(_robotModels.Compose(_modsDir, enabled, _saveMod));
    }

    // A robot edit staged, undone or saved: the mods composed again (the viewer and the level's robots redraw).
    private void RobotModelsChanged()
    {
        if (_saveMod == null)
        {
            return;
        }

        MountOnTop(_saveMod);
        _assetsPanel?.Browser.Refresh();
    }

    // model.sidecar: one field of the save mod's sidecar changed, written as the viewer's grid writes it.
    private string ModelSidecarCmd(string model, string field, string value)
    {
        if (SaveFolder() is not { } folder || ModelReplace.ReadSidecar(folder, model) is not { } s)
        {
            return $"{model}: the save mod has no models/{ModelReplace.NameOf(model)}.glb";
        }

        try
        {
            s = field switch
            {
                "rotation" => s with { Rotation = Vec(value) },
                "offset" => s with { Offset = Vec(value) },
                "scale" => s with { Scale = float.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture) },
                "fit" => s with { Fit = Enum.Parse<ModelFit>(value, ignoreCase: true) },
                "align" => s with { Align = value == "center" ? ModelAlign.Centre : Enum.Parse<ModelAlign>(value, ignoreCase: true) },
                "frames" => ModelReplace.ParseFrames(value) switch { var (f, all) => s with { Frames = f, AllFrames = all } },
                _ => throw new CommandUsageException($"no field {field}: rotation, fit, scale, align, offset or frames"),
            };
        }
        catch (Exception e) when (e is FormatException or ArgumentException)
        {
            return $"{field} '{value}': {e.Message}";
        }

        return SidecarWrite(model, s);

        static Vector3 Vec(string v) => v.Split(',') is [var x, var y, var z]
            ? new Vector3(F(x), F(y), F(z))
            : throw new FormatException("x,y,z");

        static float F(string t) => float.Parse(t, NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    private string SidecarWrite(string model, ModelSidecar sidecar)
    {
        if (SaveFolder() is not { } folder)
        {
            return "No mod to write to (File > Save As picks one)";
        }

        try
        {
            return ModelReplace.SetSidecar(EditHistory, folder, model, sidecar, ModelsChanged);
        }
        catch (Exception e) when (e is IOException or FormatException or UnauthorizedAccessException)
        {
            return $"{model}: sidecar not written ({e.Message})";
        }
    }

    private string ModelOriginal(string state)
    {
        if (state is not (On or Off))
        {
            throw new CommandUsageException("model.original on|off");
        }

        AssetsFor(_data);
        _modelViewer!.ShowOriginal = state == On;
        return _modelViewer.ShowOriginal ? "Model Viewer: the original" : "Model Viewer: the replacement";
    }

    // A robot's model by its root model's name or its enemy type ("37" ─► T800PST).
    private static string RobotModel(string word)
    {
        if (!int.TryParse(word, NumberStyles.None, CultureInfo.InvariantCulture, out var type))
        {
            return word;
        }

        var types = GameTables.Ais.Types;
        return type < types.Length && types[type].ModelFile.Length > 0
            ? ModelReplace.NameOf(types[type].ModelFile)
            : throw new CommandUsageException($"no robot type {word}");
    }

    // A replacement written or taken back: the mod mounted again (its files are listed at mount), the list told.
    private void ModelsChanged()
    {
        if (_saveMod != null)
        {
            MountOnTop(_saveMod);
        }

        _assetsPanel?.Browser.Refresh();
    }
}
