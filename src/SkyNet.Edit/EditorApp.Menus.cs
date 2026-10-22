using System.Globalization;
using System.Numerics;
using System.Reflection;
using Hexa.NET.ImGui;
using RetroEng.Diagnostics;
using RetroEng.Editor;
using RetroEng.Platform;
using RetroEng.Render;
using SkyNet.Formats;
using SkyNet.Game.Campaign;
using SkyNet.Game.Menu;
using SkyNet.Game.Mods;

namespace SkyNet.Edit;

/// <summary>The editor's main menu bar (File, Edit, View, Settings, Help), its windows, the status bar
/// and what they switch: the mission, the mods, the look, the panels and the entity kinds drawn.</summary>
internal sealed partial class EditorApp
{
    private const string UnsavedPopup = "Unsaved changes";

    private EditorPrefs _prefs;
    private readonly string? _prefsPath;
    private readonly string _modsDir;
    private readonly string[] _systemMods;
    private readonly MeshHandle _markerGizmos, _lightGizmos;
    private readonly List<Vertex> _gizmoVertices = [];
    private readonly List<string> _lastLine = [];
    private readonly Dictionary<GameKind, Dictionary<int, string>> _missionNames = [];
    private EditDocument? _doc;
    private (Vector3 Eye, float Yaw, float Pitch) _start;
    private int _markerVertexCount, _lightVertexCount;
    private bool _showMarkers = true, _showLights = true, _showSprites = true, _showRobots = true, _showEffects = true;
    private bool _gizmosStale = true;
    private bool _showOpen, _showArenas, _showMods, _showSettings, _showKeys;
    private bool _askExit, _askShown, _quit, _prefsChanged;
    private OpenTarget? _askOpen;
    private string? _forceMenu;
    private int _forceStep;
    private float _fps;
    private IReadOnlyList<ModEntry> _catalog = [];
    private List<string> _modEdit = [];

    private float FieldOfView => _prefs.FieldOfView * MathF.PI / 180f;

    /// <summary>The document has edits not saved.</summary>
    public bool IsDirty => _doc?.IsDirty == true;

    /// <summary>The renderer's live textures and meshes (the mission switch leak check).</summary>
    public (int Textures, int Meshes) ResourceCount => (_renderer.TextureCount, _renderer.MeshCount);

    // ---- the menu bar: its height ----

    private float MainMenu()
    {
        if (!ImGui.BeginMainMenuBar())
        {
            return 0f;
        }

        if (Menu("File"))
        {
            if (ImGui.MenuItem("Open Mission...", "Ctrl+O"))
            {
                _showOpen = true;
            }

            if (ImGui.MenuItem("Open Arena...", "", false, DataOf(GameKind.SkyNet) != null))
            {
                _showArenas = true;
            }

            if (ImGui.MenuItem("New Map...", "", false, _world != null && _arena == null))
            {
                OpenNewMap();
            }

            if (ImGui.MenuItem("New Mission..."))
            {
                OpenNewMission();
            }

            if (ImGui.BeginMenu("Recent missions", _prefs.Recent.Count > 0))
            {
                foreach (var n in _prefs.Recent.ToArray())
                {
                    if (ImGui.MenuItem(MissionLabel(GameKind.SkyNet, n)))
                    {
                        RequestOpen(new OpenTarget(GameKind.SkyNet, n));
                    }
                }

                ImGui.EndMenu();
            }

            if (ImGui.MenuItem("Mods..."))
            {
                OpenModsWindow();
            }

            ImGui.Separator();
            if (ImGui.MenuItem(_saveMod != null ? $"Save to {_saveMod}" : "Save", "Ctrl+S", false, _doc != null || _robotModels.Dirty))
            {
                Save();
            }

            if (ImGui.MenuItem("Save As Mod...", "", false, _doc != null))
            {
                AskSaveAs();
            }

            ImGui.Separator();
            if (ImGui.MenuItem("Exit", "Alt+F4"))
            {
                RequestExit();
            }

            ImGui.EndMenu();
        }

        if (Menu("Edit"))
        {
            var history = EditHistory;
            if (ImGui.MenuItem(history.UndoLabel is { } u ? "Undo " + u : "Undo", "Ctrl+Z", false, history.CanUndo))
            {
                history.Undo();
            }

            if (ImGui.MenuItem(history.RedoLabel is { } r ? "Redo " + r : "Redo", "Ctrl+Y", false, history.CanRedo))
            {
                history.Redo();
            }

            ImGui.Separator();
            var selected = SelectedEntity(out _, out _);
            if (ImGui.MenuItem("Delete", "Del", false, selected))
            {
                DeleteSelected();
            }

            if (ImGui.MenuItem("Duplicate", "Ctrl+D", false, selected))
            {
                DuplicateSelected();
            }

            ArrangeMenuItems(selected);
            ImGui.EndMenu();
        }

        if (Menu("View"))
        {
            ViewMenu();
            ImGui.EndMenu();
        }

        if (Menu("Play"))
        {
            PlayMenu();
            ImGui.EndMenu();
        }

        if (Menu("Settings"))
        {
            if (ImGui.MenuItem("Preferences..."))
            {
                _showSettings = true;
            }

            if (ImGui.MenuItem("Reset settings"))
            {
                ResetSettings();
            }

            ImGui.EndMenu();
        }

        if (Menu("Help"))
        {
            if (ImGui.MenuItem("Keys", "F1"))
            {
                _showKeys = true;
            }

            if (ImGui.MenuItem("About"))
            {
                _about.Show();
            }

            ImGui.EndMenu();
        }

        GizmoToolbar();
        TerrainToolbar();
        var height = ImGui.GetWindowSize().Y;
        ImGui.EndMainMenuBar();
        Shortcuts();
        GizmoKeys();
        return height;
    }

    // A main menu; --open-menu clicks it open (the pointer moved over it, pressed, released).
    private bool Menu(string name)
    {
        var open = ImGui.BeginMenu(name);
        if (_forceMenu == name)
        {
            var io = ImGui.GetIO();
            var at = (ImGui.GetItemRectMin() + ImGui.GetItemRectMax()) * 0.5f;
            io.AddMousePosEvent(at.X, at.Y);
            if (_forceStep == 1 || _forceStep == 2)
            {
                io.AddMouseButtonEvent(0, _forceStep == 1);
            }

            if (++_forceStep > 2)
            {
                _forceMenu = null;
            }
        }

        return open;
    }

    private void ViewMenu()
    {
        if (ImGui.BeginMenu("Look"))
        {
            foreach (var look in new[] { Look.Original, Look.Enhanced })
            {
                if (ImGui.MenuItem(look == Look.Original ? "ORIGINAL" : "ENHANCED", "", _settings.Look == look) && _settings.Look != look)
                {
                    _settings.Look = look;
                    Reload();
                }
            }

            ImGui.EndMenu();
        }

        ImGui.Separator();
        var outliner = _prefs.ShowOutliner;
        var properties = _prefs.ShowProperties;
        var log = _prefs.ShowLog;
        var place = _prefs.ShowPlace;
        var triggers = _prefs.ShowTriggers;
        var mission = _prefs.ShowMission;
        var validation = _prefs.ShowValidation;
        var assets = ShowAssets;
        if (ImGui.MenuItem("Outliner", "", ref outliner) | ImGui.MenuItem("Details", "", ref properties) | ImGui.MenuItem("Place", "", ref place)
            | ImGui.MenuItem("Triggers", "", ref triggers) | ImGui.MenuItem("Mission", "", ref mission) | ImGui.MenuItem("Validation", "", ref validation)
            | ImGui.MenuItem("Assets", "", ref assets) | ImGui.MenuItem("Log", "", ref log))
        {
            (_prefs.ShowOutliner, _prefs.ShowProperties, _prefs.ShowLog, _prefs.ShowPlace) = (outliner, properties, log, place);
            (_prefs.ShowTriggers, _prefs.ShowMission, _prefs.ShowValidation) = (triggers, mission, validation);
            (_prefs.ShowAssets, _assetsShown) = (assets, false);
            _prefsChanged = true;
        }

        if (ImGui.MenuItem("Tables", "", ref _showTables))
        {
            AssetsFor(_data);
        }

        if (ImGui.MenuItem("Effect editor", "", ref _showEffectsWindow))
        {
            AssetsFor(_data);
        }

        ImGui.MenuItem("Trigger graph", "", ref _showGraph);

        if (ImGui.MenuItem("Reset layout"))
        {
            ResetLayout();
        }

        if (ImGui.MenuItem("Fullscreen", "F11", _tracker.Fullscreen))
        {
            _tracker.Fullscreen = !_tracker.Fullscreen;
        }

        if (ImGui.MenuItem("Link lines", "", ref _showLinks))
        {
            _linksStale = true;
        }

        ImGui.Separator();
        if (ImGui.MenuItem("Markers", "", ref _showMarkers) | ImGui.MenuItem("Lights", "", ref _showLights))
        {
            _gizmosStale = true;
        }

        if (ImGui.MenuItem("Sprites", "", ref _showSprites))
        {
            ApplyVisibility();
        }

        if (ImGui.MenuItem("Robots", "", ref _showRobots))
        {
            // The robots are placed again with the level (EditorRobots).
            Reload();
        }

        // The enhanced look's lamp-post pools, flames, smoke, embers and dust (nothing in ORIGINAL).
        if (ImGui.MenuItem("Effects", "", ref _showEffects, _settings.Look == Look.Enhanced))
        {
            Reload();
        }

        ImGui.Separator();
        SnapMenu();
        ImGui.Separator();
        var speed = _prefs.CameraSpeed;
        var fov = _prefs.FieldOfView;
        if (ImGui.SliderFloat("Camera speed", ref speed, 100f, 10000f, "%.0f", ImGuiSliderFlags.Logarithmic) | ImGui.SliderFloat("FOV", ref fov, 30f, 120f, "%.0f deg"))
        {
            (_prefs.CameraSpeed, _prefs.FieldOfView) = (speed, fov);
            _prefsChanged = true;
        }

        ViewDistanceSlider();

        if (ImGui.MenuItem("Reset camera to the player start", "Home"))
        {
            (_eye, _yaw, _pitch) = _start;
        }

        if (ImGui.BeginMenu("Go to instance", _world != null))
        {
            foreach (var instance in _world!.World.Instances)
            {
                var label = string.Create(CultureInfo.InvariantCulture, $"{instance.Id}  MAP.{EditorWorld.MapOf(instance):D3}");
                if (ImGui.MenuItem(label, "", instance.Id == _levelInstance) && instance.Id != _levelInstance)
                {
                    _selected = new OutlinerItem(instance.Id);
                    Show(instance.Id);
                }
            }

            ImGui.EndMenu();
        }
    }

    private void Shortcuts()
    {
        var io = ImGui.GetIO();
        if (io.WantTextInput)
        {
            return;
        }

        if (io.KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.O, false))
        {
            _showOpen = true;
        }
        else if (io.KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.S, false))
        {
            Save();
        }
        else if (io.KeyCtrl && (ImGui.IsKeyPressed(ImGuiKey.Y, false) || (io.KeyShift && ImGui.IsKeyPressed(ImGuiKey.Z, false))))
        {
            EditHistory.Redo();
        }
        else if (io.KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.Z, false))
        {
            EditHistory.Undo();
        }
        else if (ImGui.IsKeyPressed(ImGuiKey.F11, false))
        {
            _tracker.Fullscreen = !_tracker.Fullscreen;
        }
        else if (ImGui.IsKeyPressed(ImGuiKey.F1, false))
        {
            _showKeys = true;
        }
        else if (ViewKeys && ImGui.IsKeyPressed(ImGuiKey.Home, false))
        {
            (_eye, _yaw, _pitch) = _start;
        }
        else
        {
            PlaceKeys(io);
            ClipboardKeys(io);
            PlayKeys(io);
            TerrainKeys(io);
        }
    }

    // ---- switching ----

    /// <summary>What is open now (null: nothing).</summary>
    private OpenTarget? Current => _world == null ? null
        : _arena != null ? new OpenTarget(GameKind.SkyNet, _arena.Map, Arena: true) : new OpenTarget(_world.Mission.Game, _world.Mission.Number);

    /// <summary>Opens a mission or an arena, asking first when the document has unsaved edits.</summary>
    private void RequestOpen(OpenTarget target)
    {
        if (IsDirty)
        {
            _askOpen = target;
            return;
        }

        Open(target);
    }

    private void RequestExit()
    {
        if (IsDirty || _robotModels.Dirty)
        {
            _askExit = true;
            return;
        }

        _quit = true;
    }

    /// <summary>The shown instance built again where the camera is (look or robots changed).</summary>
    private void Reload()
    {
        if (_levelInstance != null)
        {
            Show(_levelInstance, keepCamera: true);
        }
    }

    /// <summary>--cycle-missions: each mission opened and drawn in turn; exit 1 when a mission opened again
    /// holds other texture or mesh counts than the first time (a leak) or does not open.</summary>
    public int CycleMissions(IReadOnlyList<int> missions)
    {
        var seen = new Dictionary<int, (int Textures, int Meshes)>();
        var failed = 0;
        foreach (var n in missions)
        {
            if (!Open(n))
            {
                failed++;
                continue;
            }

            for (var i = 0; i < 2; i++)
            {
                _window.PumpEvents(_input);
                Draw(null);
            }

            var count = ResourceCount;
            Log.Info("Edit", $"Mission {n}: {count.Textures} textures, {count.Meshes} meshes");
            if (seen.TryGetValue(n, out var before) && before != count)
            {
                Log.Error("Edit", $"Mission {n} opened again holds {count.Textures} textures / {count.Meshes} meshes, {before.Textures} / {before.Meshes} the first time");
                failed++;
            }

            seen[n] = count;
        }

        return failed == 0 ? 0 : 1;
    }

    // ---- the view's entity kinds ----

    // The sprites (pickups included) hidden from the level; the gizmos rebuilt.
    private void ApplyVisibility()
    {
        if (_level == null)
        {
            return;
        }

        _level.HiddenSprites = _showSprites ? null
            : _level.Map.Entities.Where(e => EditorWorld.KindOf(e) is EntityKind.Sprite or EntityKind.Pickup).Select(e => e.FileOffset).ToHashSet();
        _gizmosStale = true;
    }

    // Markers and lights draw nothing of their own: their pick boxes, when shown.
    private void DrawGizmos()
    {
        if (_level == null || _picking == null)
        {
            return;
        }

        if (_gizmosStale)
        {
            _gizmosStale = false;
            _markerVertexCount = Gizmos(_markerGizmos, _showMarkers, EntityKind.Marker);
            _lightVertexCount = Gizmos(_lightGizmos, _showLights, EntityKind.Light);
        }

        if (_markerVertexCount > 0)
        {
            _renderer.DrawLines(_markerGizmos, 0, _markerVertexCount, Material.Flat(new Vector4(0.2f, 0.9f, 1f, 1f), SelectionOutline.State) with { Unfogged = true });
        }

        if (_lightVertexCount > 0)
        {
            _renderer.DrawLines(_lightGizmos, 0, _lightVertexCount, Material.Flat(new Vector4(1f, 0.55f, 0.15f, 1f), SelectionOutline.State) with { Unfogged = true });
        }
    }

    private int Gizmos(MeshHandle mesh, bool shown, EntityKind kind)
    {
        _gizmoVertices.Clear();
        if (shown)
        {
            foreach (var e in _level!.Map.Entities)
            {
                if (EditorWorld.KindOf(e) == kind && _picking!.TryGet(e.FileOffset, out var target))
                {
                    SelectionOutline.Box(_gizmoVertices, target.Min, target.Max);
                }
            }
        }

        if (_gizmoVertices.Count > 0)
        {
            _renderer.UpdateMesh(mesh, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_gizmoVertices));
        }

        return _gizmoVertices.Count;
    }

    // ---- the status bar: its height ----

    private float StatusBar()
    {
        var size = ImGui.GetIO().DisplaySize;
        var height = ImGui.GetFrameHeight();
        var main = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(main.Pos + new Vector2(0f, size.Y - height));
        ImGui.SetNextWindowSize(new Vector2(size.X, height));
        ImGui.SetNextWindowViewport(main.ID);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(8f, 3f));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 0f);
        const ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoSavedSettings
            | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoBringToFrontOnFocus | ImGuiWindowFlags.NoDocking;
        if (ImGui.Begin("##status", flags))
        {
            // The status clipped short of the versions in the right corner.
            var version = ImGui.CalcTextSize(EditorVersions.Line).X;
            var right = ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X;
            var start = ImGui.GetCursorScreenPos();
            ImGui.PushClipRect(start, new Vector2(ImGui.GetWindowPos().X + right - version - 16f, start.Y + height), true);
            ImGui.TextUnformatted(StatusText());
            ImGui.PopClipRect();
            ImGui.SameLine(right - version);
            ImGui.TextDisabled(EditorVersions.Line);
        }

        ImGui.End();
        ImGui.PopStyleVar(2);
        return height;
    }

    /// <summary>The status bar's line: mission, instance and map, look, camera, selection, modified, FPS, last log line.</summary>
    private string StatusText()
    {
        var mission = _arena != null ? "arena " + _arena.MapName
            : _world != null ? (_world.Mission.Game == GameKind.FutureShock ? "FS " : "") + _world.Mission.Number.ToString(CultureInfo.InvariantCulture) : "-";
        var map = _levelInstance != null && _world?.World.Instance(_levelInstance) is { } instance
            ? string.Create(CultureInfo.InvariantCulture, $"{instance.Id} MAP.{EditorWorld.MapOf(instance):D3}") : "-";
        var selection = "-";
        if (_selected is { } item)
        {
            selection = item.IsInstance ? "instance " + item.Instance
                : _world?.Map(item.Instance) is { } m && m.TryGetEntityAt(item.Entity, out var e)
                    ? string.Create(CultureInfo.InvariantCulture, $"{EditorWorld.KindOf(e)} @{item.Entity}")
                    : string.Create(CultureInfo.InvariantCulture, $"@{item.Entity}");
        }

        _lastLine.Clear();
        _ring.CopyLast(1, _lastLine);
        var last = _lastLine.Count > 0 ? _lastLine[0] : "";
        var yaw = ((_yaw * 180f / MathF.PI) % 360f + 360f) % 360f;
        return string.Create(CultureInfo.InvariantCulture,
            $"Mission {mission} | {map} | {(_settings.Look == Look.Enhanced ? "ENHANCED" : "ORIGINAL")} | cam {_eye.X:0}, {_eye.Y:0}, {_eye.Z:0} yaw {yaw:0} | sel {selection} | {(IsDirty ? "modified" : "unmodified")} | {_fps:0} fps{McpStatus} | {last}");
    }

    // ---- the windows ----

    private void Windows()
    {
        OpenMissionWindow();
        OpenArenaWindow();
        NewMapWindow();
        NewMissionWindow();
        ModsWindow();
        SettingsWindow();
        TextWindow("Keys", ref _showKeys, EditorKeys.Text);
        AboutWindow();
        UnsavedModal();
        SaveAsModal();
        CapturePrefs();
        SaveWhenDue();
    }

    private void SavePrefs()
    {
        if (_prefsPath != null)
        {
            _prefs.Save(_prefsPath);
        }
    }

    private void OpenMissionWindow()
    {
        if (!_showOpen)
        {
            return;
        }

        ImGui.SetNextWindowSize(new Vector2(460f, 380f), ImGuiCond.FirstUseEver);
        if (ImGui.Begin("Open Mission", ref _showOpen))
        {
            ImGui.TextUnformatted("Double-click a mission to open it");
            foreach (var game in new[] { GameKind.SkyNet, GameKind.FutureShock })
            {
                if (DataOf(game) == null)
                {
                    continue;
                }

                ImGui.SeparatorText(game == GameKind.FutureShock ? "FUTURE SHOCK" : "SKYNET");
                ImGui.PushID((int)game);
                foreach (var mission in MissionTable.Playable(game).Missions)
                {
                    var target = new OpenTarget(game, mission.Number);
                    if (ImGui.Selectable(MissionLabel(game, mission.Number), Current == target, ImGuiSelectableFlags.AllowDoubleClick)
                        && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                    {
                        _showOpen = false;
                        RequestOpen(target);
                    }
                }

                ImGui.PopID();
            }
        }

        ImGui.End();
    }

    // File > Open Arena: the deathmatch arenas the host menu offers (NETLEVEL.PRS), SkyNET's data.
    private void OpenArenaWindow()
    {
        if (!_showArenas)
        {
            return;
        }

        ImGui.SetNextWindowSize(new Vector2(460f, 320f), ImGuiCond.FirstUseEver);
        if (ImGui.Begin("Open Arena", ref _showArenas))
        {
            ImGui.TextUnformatted("Deathmatch arenas: double-click to open");
            ImGui.Separator();
            foreach (var level in DataOf(GameKind.SkyNet) is { } data ? ArenaWorld.Levels(data) : [])
            {
                var target = new OpenTarget(GameKind.SkyNet, level.Map, Arena: true);
                var label = string.Create(CultureInfo.InvariantCulture, $"{level.MapName}  {level.Name}");
                if (ImGui.Selectable(label, Current == target, ImGuiSelectableFlags.AllowDoubleClick)
                    && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                {
                    _showArenas = false;
                    RequestOpen(target);
                }
            }
        }

        ImGui.End();
    }

    /// <summary>A mission's menu line: its number, start map and the briefing's first objective.</summary>
    private string MissionLabel(GameKind game, int number)
    {
        if (!_missionNames.TryGetValue(game, out var names))
        {
            _missionNames[game] = names = DataOf(game) is { } data ? MissionNames(data) : [];
        }

        return names.TryGetValue(number, out var label) ? label : string.Create(CultureInfo.InvariantCulture, $"{number,2}");
    }

    private static Dictionary<int, string> MissionNames(GameData data)
    {
        var names = new Dictionary<int, string>();
        BsaArchive? brif = null;
        try
        {
            brif = data.Exists("MDMDBRIF.BSA") ? data.OpenArchive("MDMDBRIF.BSA") : null;
        }
        catch (Exception e) when (e is IOException or InvalidDataException)
        {
            Log.Warn("Edit", $"MDMDBRIF.BSA does not open ({e.Message}): the missions go by number");
        }

        using (brif)
        {
            foreach (var m in MissionTable.Playable(data.Game).Missions)
            {
                var name = string.Create(CultureInfo.InvariantCulture, $"{m.StartMap:D3}.TXT");
                var title = (brif?.Read(name) ?? ModMissions.Briefing(name)) is { } bytes
                    ? Briefing.Parse(bytes).Missions.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s)) ?? "" : "";
                title = title.Trim();
                if (title.Length > 56)
                {
                    title = title[..55] + "...";
                }

                names[m.Number] = string.Create(CultureInfo.InvariantCulture, $"{m.Number,2}  MAP.{m.StartMap:D3}  {title}");
            }
        }

        return names;
    }

    private void OpenModsWindow()
    {
        _catalog = ModCatalog.Scan(_modsDir, _systemMods);
        _modEdit = [.. ModOrder.Effective(_prefs.Mods, _options.Mod != null ? ModOrder.Parse(_options.Mod) : null, _systemMods)];
        _showMods = true;
    }

    private void ModsWindow()
    {
        if (!_showMods)
        {
            return;
        }

        ImGui.SetNextWindowSize(new Vector2(480f, 400f), ImGuiCond.FirstUseEver);
        if (ImGui.Begin("Mods", ref _showMods))
        {
            ImGui.TextWrapped($"Folder: {_modsDir}");
            ImGui.Separator();
            ImGui.TextUnformatted("Available (tick to enable):");
            foreach (var mod in _catalog.Where(m => !m.System))
            {
                var on = _modEdit.Any(n => string.Equals(n, mod.Name, StringComparison.OrdinalIgnoreCase));
                ImGui.BeginDisabled(mod.Problem != null);
                if (ImGui.Checkbox($"{mod.Title}##{mod.Name}", ref on))
                {
                    ModOrder.Set(_modEdit, mod.Name, on);
                }

                ImGui.EndDisabled();
                if (mod.Problem != null || mod.Info?.Description != null)
                {
                    ImGui.SameLine();
                    ImGui.TextDisabled(mod.Problem ?? mod.Info!.Description);
                }
            }

            if (!_catalog.Any(m => !m.System))
            {
                ImGui.TextDisabled("no mods in the folder");
            }

            ImGui.Separator();
            ImGui.TextUnformatted("Load order (later wins):");
            for (var i = 0; i < _modEdit.Count; i++)
            {
                ImGui.PushID(i);
                if (ImGui.ArrowButton("up", ImGuiDir.Up) && i > 0)
                {
                    (_modEdit[i - 1], _modEdit[i]) = (_modEdit[i], _modEdit[i - 1]);
                }

                ImGui.SameLine();
                if (ImGui.ArrowButton("down", ImGuiDir.Down) && i + 1 < _modEdit.Count)
                {
                    (_modEdit[i + 1], _modEdit[i]) = (_modEdit[i], _modEdit[i + 1]);
                }

                ImGui.SameLine();
                ImGui.TextUnformatted(string.Create(CultureInfo.InvariantCulture, $"{i + 1}. {_modEdit[i]}"));
                ImGui.PopID();
            }

            ImGui.Separator();
            if (ImGui.Button("Apply and reload"))
            {
                _prefs.Mods = [.. _modEdit];
                _prefsChanged = true;
                MountMods(ModOrder.Effective(_modEdit, null, _systemMods));
                Log.Info("Edit", _modEdit.Count == 0 ? "No mods" : "Mods: " + string.Join(", ", _modEdit));
                _showMods = false;
                if (Current is { } current)
                {
                    RequestOpen(current);
                }
            }
        }

        ImGui.End();
    }

    /// <summary>The view distance (View menu, Preferences): world units; the far left end is the map's own.</summary>
    private void ViewDistanceSlider()
    {
        var far = _prefs.DrawDistance;
        var label = far <= 0f ? "map's own" : "%.0f";
        if (!ImGui.SliderFloat("View distance", ref far, 0f, EditorPrefs.MaxDrawDistance, label, ImGuiSliderFlags.Logarithmic))
        {
            return;
        }

        // Below the range's start: back to the map's own.
        _prefs.DrawDistance = far < EditorPrefs.MinDrawDistance ? 0f : EditorPrefs.ViewDistance(far);
        _prefsChanged = true;
    }

    private void SettingsWindow()
    {
        if (!_showSettings)
        {
            return;
        }

        ImGui.SetNextWindowSize(new Vector2(520f, 560f), ImGuiCond.FirstUseEver);
        if (ImGui.Begin("Preferences", ref _showSettings))
        {
            var p = _prefs;
            var (speed, sensitivity, invert, fov, scale, font) = (p.CameraSpeed, p.MouseSensitivity, p.InvertY, p.FieldOfView, p.UiScale, p.FontSize);
            var look = p.DefaultLook == Look.Enhanced ? 1 : 0;
            var data = p.DataDir ?? "";
            var mods = p.ModsDir ?? "";
            var changed = ImGui.SliderFloat("Camera speed", ref speed, 100f, 10000f, "%.0f", ImGuiSliderFlags.Logarithmic);
            changed |= ImGui.SliderFloat("Mouse sensitivity", ref sensitivity, 0.1f, 5f, "%.2f");
            changed |= ImGui.Checkbox("Invert Y", ref invert);
            changed |= ImGui.SliderFloat("Field of view", ref fov, 30f, 120f, "%.0f deg");
            ViewDistanceSlider();
            var lookPicked = ImGui.Combo("Look", ref look, "ORIGINAL\0ENHANCED\0");
            changed |= lookPicked;
            changed |= ImGui.InputText("Data folder", ref data, 512);
            BrowseFolder("##data", data, "Data folder", d => p.DataDir = d);
            changed |= ImGui.InputText("Mods folder", ref mods, 512);
            BrowseFolder("##mods", mods, "Mods folder", m => p.ModsDir = m);
            ImGui.TextDisabled("The folders take effect at the next start; the command line overrides them.");
            var byHand = ImGui.SliderFloat("UI scale", ref scale, EditorPrefs.MinUiScale, EditorPrefs.MaxUiScale, "%.2f");
            byHand |= ImGui.SliderFloat("Font size", ref font, 8f, 40f, "%.0f pt");
            ImGui.SameLine();
            if (ImGui.Button("Reset to system"))
            {
                p.ResetToSystemScale(_imgui.SystemScale);
                (scale, font) = (p.UiScale, p.FontSize);
                _prefsChanged = true;
            }

            ImGui.TextDisabled(p.UiScaleAuto ? "The scale follows the display the editor is on." : "The scale is set by hand.");
            if (byHand)
            {
                p.UiScaleAuto = false;
            }

            if (changed || byHand)
            {
                (p.CameraSpeed, p.MouseSensitivity, p.InvertY, p.FieldOfView, p.UiScale, p.FontSize) = (speed, sensitivity, invert, fov, scale, font);
                p.DefaultLook = look == 1 ? Look.Enhanced : Look.Original;
                if (lookPicked && _settings.Look != p.DefaultLook)
                {
                    // The look is one setting: the view takes it now.
                    _settings.Look = p.DefaultLook;
                    Reload();
                }

                p.DataDir = data.Trim() is { Length: > 0 } d ? d : null;
                p.ModsDir = mods.Trim() is { Length: > 0 } m ? m : null;
                _prefsChanged = true;
            }

            // The game's ENHANCED OPTIONS: the view takes a change at once, as the game's screen does.
            ImGui.SeparatorText("ENHANCED OPTIONS");
            if (EnhancedSettings(p))
            {
                p.ApplyTo(_settings);
                ApplyLook();
                _prefsChanged = true;
            }

            if (_settings.Look != Look.Enhanced)
            {
                ImGui.TextDisabled(EnhancedScreen.OriginalNote);
            }

            McpSettings(p);
            ImGui.Separator();
            var splash = p.ShowSplash;
            if (ImGui.Checkbox("Show splash screen", ref splash))
            {
                p.ShowSplash = splash;
                _prefsChanged = true;
            }

            ImGui.TextDisabled(_prefsPath != null ? "Saved to " + _prefsPath : "Not saved (this run has no preferences file)");
        }

        ImGui.End();
    }

    /// <summary>The ENHANCED OPTIONS rows on <paramref name="p"/>, with the game screen's steps and names;
    /// true when one changed.</summary>
    private static bool EnhancedSettings(EditorPrefs p)
    {
        var changed = Choice("Shadows", p.Shadows, EnhancedOptions.StepShadows, EnhancedOptions.Name, v => p.Shadows = v);
        changed |= Choice("Anti-aliasing", p.AntiAliasing, EnhancedOptions.StepAntiAliasing, EnhancedOptions.Name, v => p.AntiAliasing = v);
        changed |= Choice("Lights", p.LightBudget, EnhancedOptions.StepLightBudget, EnhancedOptions.LightsName, v => p.LightBudget = v);

        // Ambient in the game's steps (e.g. 100 %, 110 %).
        var ambient = p.Ambient;
        if (ImGui.SliderInt("Ambient", ref ambient, MenuSettings.MinAmbient, MenuSettings.MaxAmbient, "%d %%"))
        {
            ambient = EnhancedOptions.StepAmbient((int)MathF.Round(ambient / (float)MenuSettings.AmbientStep) * MenuSettings.AmbientStep, 0);
            changed |= ambient != p.Ambient;
            p.Ambient = ambient;
        }

        var (occlusion, glow) = (p.AmbientOcclusion, p.Glow);
        if (ImGui.Checkbox("Ambient occlusion", ref occlusion) | ImGui.Checkbox("Glow", ref glow))
        {
            (p.AmbientOcclusion, p.Glow, changed) = (occlusion, glow, true);
        }

        changed |= Choice("Surface relief", p.Relief, EnhancedOptions.StepRelief, EnhancedOptions.Name, v => p.Relief = v);
        changed |= Choice("Particles", p.Particles, EnhancedOptions.StepParticles, EnhancedOptions.Name, v => p.Particles = v);
        return changed;
    }

    /// <summary>A combo of every value <paramref name="step"/> walks through from <paramref name="current"/>
    /// (in order); the one picked goes to <paramref name="set"/>.</summary>
    private static bool Choice<T>(string label, T current, Func<T, int, T> step, Func<T, string> name, Action<T> set)
    {
        List<T> all = [current];
        for (var v = step(current, 1); !EqualityComparer<T>.Default.Equals(v, current); v = step(v, 1))
        {
            all.Add(v);
        }

        all.Sort();
        var i = all.IndexOf(current);
        if (!ImGui.Combo(label, ref i, string.Join('\0', all.Select(name)) + '\0') || i < 0
            || EqualityComparer<T>.Default.Equals(all[i], current))
        {
            return false;
        }

        set(all[i]);
        return true;
    }

    /// <summary>A Browse button after the path box before it: the OS's folder dialog, starting in the box's
    /// folder; the picked folder goes to <paramref name="set"/> on the main thread.</summary>
    private void BrowseFolder(string id, string current, string title, Action<string> set)
    {
        ImGui.SameLine();
        if (ImGui.Button("Browse..." + id))
        {
            _dialogs.OpenFolder(_window, current, picked =>
            {
                if (picked != null)
                {
                    set(picked);
                    _prefsChanged = true;
                }
            }, title);
        }
    }

    private static void TextWindow(string title, ref bool open, string text)
    {
        if (!open)
        {
            return;
        }

        ImGui.SetNextWindowSize(new Vector2(640f, 420f), ImGuiCond.FirstUseEver);
        if (ImGui.Begin(title, ref open))
        {
            ImGui.TextUnformatted(text);
        }

        ImGui.End();
    }

    // Unsaved edits: the open or exit waits for Discard.
    private void UnsavedModal()
    {
        if ((_askExit || _askOpen != null) && !_askShown)
        {
            ImGui.OpenPopup(UnsavedPopup);
            _askShown = true;
        }

        if (!ImGui.BeginPopupModal(UnsavedPopup, ImGuiWindowFlags.AlwaysAutoResize))
        {
            return;
        }

        ImGui.TextUnformatted("The mission has unsaved edits." + (_saveMod != null ? $" Save them to {_saveMod} or discard them?" : " Discard them?"));
        var save = _saveMod != null && ImGui.Button("Save");
        if (_saveMod != null)
        {
            ImGui.SameLine();
        }

        if ((save && Save()) || ImGui.Button("Discard"))
        {
            if (_askExit)
            {
                _quit = true;
            }
            else if (_askOpen is { } target)
            {
                _doc?.MarkSaved();
                Open(target);
            }

            CloseAsk();
        }

        ImGui.SameLine();
        if (ImGui.Button("Cancel"))
        {
            CloseAsk();
        }

        ImGui.EndPopup();
    }

    private void CloseAsk()
    {
        (_askExit, _askOpen, _askShown) = (false, null, false);
        ImGui.CloseCurrentPopup();
    }
}
