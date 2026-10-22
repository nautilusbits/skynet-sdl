using System.Numerics;
using Hexa.NET.ImGui;
using RetroEng.Commands;
using RetroEng.Diagnostics;
using RetroEng.Editor;
using SkyNet.Data;
using SkyNet.Formats;
using SkyNet.Game.Mods;

namespace SkyNet.Edit;

/// <summary>View > Assets (editor-tools plan, T0): the engine's content browser over the game's assets
/// (<see cref="GameAssets"/>), scoped to the shown map, "Override in mod" writing into the mod a Save goes
/// to. Its actions are the editor's asset commands (asset.list, asset.open, asset.override, ...), which
/// --command runs too (screenshots; later a remote tool).</summary>
internal sealed partial class EditorApp
{
    private readonly CommandRegistry _commands = new();
    private AssetsPanel? _assetsPanel;
    private GameAssets? _assets;
    // The model and robot viewer (T2): the asset viewer's pane and the "Model Viewer" window.
    private ModelViewer? _modelViewer;
    // The Model Viewer window's first place: under the Assets panel, over the 3D view, above the Log.
    private const float ModelViewWidth = 520f, ModelViewHeight = 360f, ModelViewMinHeight = 200f;
    // The entry selected last frame: selecting a model or robot opens its viewer (and the Model Viewer).
    private string? _selectedKey;
    // The texture viewer's material edits (T1), into the save mod's materials.json.
    private MaterialEdits? _materials;
    // Edit > Undo's stack while no mission is open (the asset viewer's edits); a mission brings its own.
    private readonly UndoStack _noDocHistory = new();
    // The data set and the shown level the panel was made for (a game switch or a new level redoes it).
    private GameData? _assetsData;
    private string? _assetsScope;
    // The panel up for this run only (a command showed it), not kept in the preferences.
    private bool _assetsShown, _commandRan;

    /// <summary>Whether the Assets panel is up (View > Assets, or a command for this run).</summary>
    private bool ShowAssets => _prefs.ShowAssets || _assetsShown;

    private void AssetsPanelFrame(Vector2 at, Vector2 size, float logTop)
    {
        if (!_commandRan && _options.Command is { } line)
        {
            _commandRan = true;
            RunCommand(line);
        }

        if (!ShowAssets)
        {
            return;
        }

        var panel = AssetsFor(_data);
        AssetsScope(panel.Browser);
        var folder = _saveMod != null ? SaveTarget.Folder(_modsDir, _saveMod, _systemMods, out _) : null;
        (panel.Commands.ModFolder, panel.Commands.ModName) = (folder, folder != null ? _saveMod : null);
        MaterialsTo(folder);
        PanelAt(at, size);
        OpenOnSelect(panel.Browser);
        var open = true;
        panel.Draw(ref open);
        PlayKey.AssetsFocused = AssetsFocused();
        ModelViewerFrame(logTop);
        if (open)
        {
            return;
        }

        (_prefs.ShowAssets, _assetsShown, _prefsChanged) = (false, false, true);
        _assets?.Forget();
    }

    // A model or robot selected: its viewer opened at once (a click shows its 3D, no double-click needed).
    private void OpenOnSelect(AssetBrowser browser)
    {
        var selected = browser.Selected;
        if (selected?.Key == _selectedKey)
        {
            return;
        }

        _selectedKey = selected?.Key;
        if (selected is { Kind: GameAssets.Models or RobotAssets.Robots } && browser.Opened?.Key != selected.Key)
        {
            browser.Opened = selected;
        }
    }

    // Whether the Assets panel (the window just drawn, appended to) has the focus: Space then plays its viewer's asset.
    private static bool AssetsFocused()
    {
        var focused = ImGui.Begin(AssetsPanel.Title) && ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows);
        ImGui.End();
        return focused;
    }

    // The Model Viewer under the Assets panel as it stands, half its width, down to the Log at most.
    private unsafe void ModelViewerFrame(float logTop)
    {
        if (_modelViewer == null)
        {
            return;
        }

        var main = ImGui.GetMainViewport();
        var assets = ImGuiP.FindWindowByName(AssetsPanel.Title);
        var (at, width) = assets.Handle == null
            ? (Vector2.Zero, ModelViewWidth)
            : (assets.Handle->Pos - main.Pos + new Vector2(0f, assets.Handle->Size.Y), assets.Handle->Size.X * 0.5f);
        var height = Math.Clamp(logTop - at.Y, ModelViewMinHeight, ModelViewHeight);
        at.Y = MathF.Min(at.Y, logTop - height);
        _modelViewer.Window(at, new Vector2(MathF.Min(width, ModelViewWidth), height));
    }

    /// <summary>Runs an editor command line (commands split by ';'), its output logged.</summary>
    public void RunCommand(string line)
    {
        AssetsFor(_data);
        foreach (var output in _commands.Run(line))
        {
            Log.Info("Edit", output);
        }
    }

    /// <summary>The panel over <paramref name="data"/>'s assets, made again when the data set changed; its
    /// commands registered (replacing the last panel's).</summary>
    private AssetsPanel AssetsFor(GameData data)
    {
        if (_assetsPanel != null && ReferenceEquals(_assetsData, data))
        {
            _materials!.History = EditHistory;
            return _assetsPanel;
        }

        _assets?.Forget();
        _modelViewer?.Release();
        _modelViewer = new ModelViewer(_renderer, data);
        ModelsFor(_modelViewer);
        _assets = new GameAssets(data, _renderer, _modsDir) { Viewer = _modelViewer, Dragging = AssetDragSource };
        var commands = new AssetCommands(new AssetBrowser([_assets, new RobotAssets(GameTables.Ais, _modelViewer) { Dragging = AssetDragSource }]));
        commands.Overridden += AssetOverridden;
        commands.Revealed += _ => _assetsShown = true;
        commands.Register(_commands);
        _materials = new MaterialEdits(data.Game) { History = EditHistory };
        _materials.Changed += MaterialsChanged;
        _materials.Register(_commands);
        _commands.Add("edit.undo", "edit.undo", _ => EditHistory.UndoLabel is { } u && EditHistory.Undo() ? $"Undone: {u}" : "Nothing to undo",
            "takes back the last edit (Edit > Undo)");
        _commands.Add("edit.redo", "edit.redo", _ => EditHistory.RedoLabel is { } r && EditHistory.Redo() ? $"Redone: {r}" : "Nothing to redo",
            "does the last undone edit again (Edit > Redo)");
        _assets.Materials = _materials;
        _assets.ExportModel = ExportGltf;
        _commands.Add("model.gltf", "model.gltf <name|all> [dir]", a => ModelGltf(data, a.Word(0), a.OptionalWord(1) ?? GltfExport.DefaultDir),
            "writes .3D model name (or every model) as name.glb, a robot with segments also as name.robot.glb (skinned, a clip per AIS block)");
        TablesFor(_assets);
        SpritesFor(_assets);
        SoundsFor(_assets, data);
        _assetsPanel = new AssetsPanel(commands);
        (_assetsData, _assetsScope) = (data, null);
        AssetsFromPrefs();
        return _assetsPanel;
    }

    // The shown map's assets as the browser's scope, set again when another level is shown.
    private void AssetsScope(AssetBrowser browser)
    {
        var scope = _level == null ? null : $"MAP.{_level.Number:D3}";
        if (scope == _assetsScope)
        {
            return;
        }

        _assetsScope = scope;
        browser.SetScope(scope, _level != null ? _assets!.MapKeys(_level.Map) : null);
    }

    // Assets > Export glTF...: the folder asked for, then model.gltf into it (logged).
    private void ExportGltf(string model)
    {
        _dialogs.OpenFolder(_window, null, picked =>
        {
            if (picked != null)
            {
                RunCommand($"model.gltf {model} \"{picked}\"");
            }
        }, $"Export {model} as glTF");
    }

    // model.gltf: GltfExport.Run's files, said in the command's output.
    private static string ModelGltf(GameData data, string which, string dir) =>
        GltfExport.Run(data, which, dir) == 0 ? $"{which}: glTF written to {Path.GetFullPath(dir)}" : $"{which}: not exported (see the log)";

    // Material edits go to the save mod's folder (earlier steps keep the folder they wrote).
    private void MaterialsTo(string? folder)
    {
        if (_materials == null || _materials.ModFolder == folder)
        {
            return;
        }

        _materials.ModFolder = folder;
    }

    /// <summary>Edit > Undo's stack: the mission's, else the one for edits without a mission.</summary>
    private UndoStack EditHistory => _doc?.History ?? _noDocHistory;

    // A material written: the save mod mounted again (on top when it was off; a folder's file list is
    // taken at mount, and the file may be new or gone), the shown level's textures given their materials again.
    private void MaterialsChanged()
    {
        if (_saveMod != null)
        {
            MountOnTop(_saveMod);
        }

        _level?.Textures.ApplySurfaces();
    }

    // The mod just written mounted on top, so the browser shows the asset as the mod's.
    private void AssetOverridden(AssetEntry entry, IReadOnlyList<string> written)
    {
        if (_saveMod == null)
        {
            return;
        }

        MountOnTop(_saveMod);
        _assetsPanel?.Browser.Refresh();
    }
}
