using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Hexa.NET.ImGui;
using RetroEng.Diagnostics;
using RetroEng.Editor;
using RetroEng.Physics;
using RetroEng.Platform;
using RetroEng.Render;
using RetroEng.World;
using SDL;
using SkyNet.Formats;
using SkyNet.Game;
using SkyNet.Game.Baking;
using SkyNet.Game.Behaviour;
using SkyNet.Game.Campaign;
using SkyNet.Game.Collision;
using SkyNet.Game.DevTools;
using SkyNet.Game.Enhanced;
using SkyNet.Game.Menu;
using SkyNet.Game.Player;
using static SDL.SDL3;

namespace SkyNet.Edit;

/// <summary>The editor's window: one mission's world, one instance's level drawn at a time (robots
/// static, nothing ticks), a fly camera and the ImGui panels. A frame loop of its own: the game's
/// fixed-tick loop and modes stay in SkyNet.App.</summary>
internal sealed partial class EditorApp : IDisposable
{
    private const float Near = 1f;
    private const float FarPlane = 100000f;
    private const float FastFactor = 4f, LookPerPixel = 0.004f;

    private readonly EditorOptions _options;
    // The data set shown (SkyNET or Future Shock: Open Mission switches, as the game's SwitchGame does)
    // and its map blocks; the other data set when it is there.
    private GameData _data;
    private BlockStore? _blocks;
    private GameData? _otherData;
    // The deathmatch arena open (File > Open Arena); null: a mission.
    private NetLevel? _arena;
    private readonly LogRing _ring;
    private readonly MenuSettings _settings = new();
    private readonly Window _window;
    private readonly Renderer _renderer;
    private readonly ImGuiHost _imgui;
    private readonly Input _input = new();
    private readonly FileDialogs _dialogs = new();
    // The enhanced look's lights, as the game gathers them.
    private readonly EnhancedEffects _effects = new();
    // The weather's cover world and the level it is of (CoverWorld).
    private CollisionWorld? _coverWorld;
    private Level? _coverOf;
    private readonly List<string> _logLines = [];
    private Renderer.Scope _levelScope;
    private EditorWorld? _world;
    private Level? _level;
    private string? _levelInstance;
    private OutlinerItem? _selected;
    private readonly SelectionOutline _outline;
    private EntityPicking? _picking;
    // The shown level's robots, as the game stands them (View > Robots); null: hidden.
    private EditorRobots? _robots;
    // A pick in the view selected a row: the outliner opens its tree and scrolls to it once.
    private bool _reveal;
    private Vector3 _eye;
    private float _yaw, _pitch;
    private long _lastFrame;
    // --capture: the frames saved and the clock they drive (null: the wall clock), and this frame's file.
    private readonly FrameCapture? _capture;
    private string? _captureShot;

    public EditorApp(EditorOptions options, GameData data, BlockStore? blocks, LogRing ring, EditorPrefs prefs, string? prefsPath, string modsDir, string[] systemMods,
        SplashScreen? splash = null)
    {
        (_options, _data, _blocks, _ring, _splash) = (options, data, blocks, ring, splash);
        _capture = options.Capture is { } dir ? new FrameCapture(dir, options.CaptureFps) : null;
        _otherData = OtherData(data);
        (_prefs, _prefsPath, _modsDir, _systemMods) = (prefs, prefsPath, modsDir, systemMods);
        _forceMenu = options.OpenMenu;
        _saveMod = options.Mod == null && prefs.SaveMod is { } kept && SaveTarget.Folder(modsDir, kept, systemMods, out _) != null
            ? kept : SaveTarget.Default(modsDir, SkyNet.Game.Mods.ModContent.Enabled, systemMods);
        _settings.Look = options.Look ?? prefs.DefaultLook;
        prefs.ApplyTo(_settings);
        (_lookKept, _saveModKept) = (_settings.Look, _saveMod);
        ViewFromPrefs();
        RegisterPrefCommands();
        RegisterGraphCommands();
        RegisterNewCommands();
        RegisterTerrainCommands();
        RegisterDetailCommands();
        RegisterOutlinerCommands();
        RegisterArrangeCommands();
        RegisterAboutCommands();
        // Hidden until its first frame (Reveal): no blank window while the level loads.
        _splash?.Step(SplashScreen.Renderer);
        _window = new Window("SkyNET mission editor", options.Width, options.Height,
            options.Offscreen ? Visibility.Hidden : Visibility.Deferred, resizable: true);
        RestoreWindow();
        _renderer = new Renderer(_window)
        {
            HiddenSize = new Resolution(options.Width, options.Height),
            // .3D faces wind counter-clockwise seen from their visible side; DOS culls the back.
            FrontFace = Winding.CounterClockwise,
            // No shared depth pre-pass, as the game (AppHost.DefaultDepthSharing): AMD hid robots with it.
            DepthSharing = DepthSharing.Off,
        };
        _imgui = new ImGuiHost(_window, _renderer) { ShowDemo = options.ImGuiDemo, PanelTitle = "SkyNET panel" };
        _imgui.LayoutPath = LayoutFile(options, prefsPath);
        _imgui.Build += Panels;
        // The first start (no scale kept) takes the system's; an automatic scale follows the window's display.
        _prefsChanged |= _prefs.FollowSystemScale(_imgui.SystemScale);
        _imgui.DisplayScaleChanged += scale => _prefsChanged |= _prefs.FollowSystemScale(scale);
        _outline = new SelectionOutline(_renderer);
        MakeExtraOutlines();
        _ghostOutline = new SelectionOutline(_renderer) { Colour = GhostColour };
        _markerGizmos = _renderer.CreateDynamicMesh(256);
        _lightGizmos = _renderer.CreateDynamicMesh(256);
        // Before the level's mark: a re-bake after an edit releases everything made after it.
        _transformGizmo = new TransformGizmo(_renderer);
        _linkLines = _renderer.CreateDynamicMesh(256);
        MakeViewTarget();
        _levelScope = _renderer.Mark();
        Log.Info("Edit", $"GPU: {_renderer.Gpu.Device} on {GpuBackends.Name(_renderer.Gpu.Backend)}; look {_settings.Look}");
    }

    /// <summary>The data set's map blocks through the mods folder, baked first where missing or stale
    /// (as the game does); null when the folder cannot be written (maps then bake from the raw files).</summary>
    public static BlockStore? OpenBlocks(string modsDir, GameData data)
    {
        var store = new BlockStore(modsDir, data);
        try
        {
            if (store.Stale() is { Count: > 0 } stale)
            {
                Log.Info("Bake", $"{stale.Count} map blocks to bake into {store.ZipPath}");
                store.Bake(stale, (done, total) => Log.Info("Bake", $"{done}/{total}"));
            }

            if (store.StaleMissions() is { Count: > 0 } missions)
            {
                store.BakeMissions(missions);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warn("Bake", $"The map blocks cannot be written ({e.Message}): maps are built from the raw files");
        }

        store.Mount();
        return store;
    }

    /// <summary>The other game's data beside <paramref name="data"/> (Future Shock beside SkyNET's, SkyNET's
    /// beside Future Shock's), null when it is not there.</summary>
    public static GameData? OtherData(GameData data)
    {
        var dir = data.Game == GameKind.FutureShock ? data.SkyNetDir : GameDataLocator.OtherGameDir(data.Dir);
        return dir != null && GameData.Open(dir, data.SkyNetDir) is { } other && other.Game != data.Game ? other : null;
    }

    /// <summary>The data set of <paramref name="game"/> (null when it is not there).</summary>
    private GameData? DataOf(GameKind game) => _data.Game == game ? _data : _otherData?.Game == game ? _otherData : null;

    /// <summary>Makes <paramref name="game"/>'s data the one shown: the level goes, its map blocks open (baked
    /// where stale, as the game does). False (nothing changed) when that data is not there.</summary>
    public bool UseGame(GameKind game)
    {
        if (_data.Game == game)
        {
            return true;
        }

        if (DataOf(game) is not { } next)
        {
            Log.Error("Edit", $"{game}: no data (Future Shock's lives beside SkyNET's, as the installer puts it)");
            return false;
        }

        _renderer.Release(_levelScope);
        (_renderer.ShadeTable, _renderer.Fog, _renderer.PlaneRemap, _renderer.PlaneGrade) = (default, null, null, null);
        (_level, _levelInstance, _picking, _world, _doc, _selected) = (null, null, null, null, null, null);
        _effects.Forget();
        _levelScope = _renderer.Mark();
        _blocks?.Dispose();
        (_otherData, _data) = (_data, next);
        _blocks = OpenBlocks(_modsDir, next);
        Log.Info("Edit", $"Data: {next.Game}");
        return true;
    }

    /// <summary>Opens what <paramref name="target"/> names, switching the data set first when it is the other game's.</summary>
    public bool Open(OpenTarget target)
    {
        if (!UseGame(target.Game))
        {
            return false;
        }

        return target.Arena ? OpenArena(target.Number) : Open(target.Number);
    }

    /// <summary>Opens deathmatch arena MAP.<paramref name="map"/> (a NETLEVEL.PRS level, or any map) as a one-instance world.</summary>
    public bool OpenArena(int map)
    {
        if (!UseGame(GameKind.SkyNet))
        {
            return false;
        }

        _splash?.Step(SplashScreen.Arena(map));
        var file = ArenaWorld.Build(map);
        if (_data.LoadMap(map) == null)
        {
            Log.Error("Edit", $"Arena MAP.{map:D3} is not in the data");
            return false;
        }

        _arena = ArenaWorld.Levels(_data).FirstOrDefault(l => l.Map == map)
            ?? new NetLevel(MissionFile.MapName(map), map, 0, "", "", 0, 0, 0, System.Collections.Immutable.ImmutableDictionary<string, int>.Empty, false);
        _paletteOf = null;
        _doc = EditDocument.Load(_data, file, SkyNet.Game.Mods.ModContent.Files);
        // An arena's world is the editor's own: it has no mission file to save.
        _doc.MissionPath = null;
        _world = new EditorWorld(file, EditedMap);
        DocumentOpened();
        var first = file.World.Start.Instance;
        _selected = new OutlinerItem(first);
        Log.Info("Edit", $"Arena {_arena.Name} (MAP.{map:D3})");
        return Show(first);
    }

    /// <summary>Mission <paramref name="number"/>'s world as the game reads it: a mod's or the system
    /// ZIP's file, else generated from the data.</summary>
    public MissionFile? LoadMission(int number)
    {
        if (MissionTable.Playable(_data.Game).ByNumber(number) is not { } mission)
        {
            Log.Error("Edit", $"No mission {number} in {_data.Game}'s mission table or the mods");
            return null;
        }

        return _blocks?.LoadMission(number) ?? MissionGenerator.Build(_data, mission);
    }

    /// <summary>Opens a mission: its world in the outliner, its start instance (or --instance) shown.</summary>
    public bool Open(int number, string? instance = null)
    {
        _splash?.Step(SplashScreen.Mission(number, _data.Game));
        if (LoadMission(number) is not { } file)
        {
            return false;
        }

        _arena = null;
        _paletteOf = null;
        _doc = EditDocument.Load(_data, file, SkyNet.Game.Mods.ModContent.Files);
        _world = new EditorWorld(file, EditedMap);
        DocumentOpened();
        if (_data.Game == GameKind.SkyNet)
        {
            _prefs.Opened(number);
        }

        SavePrefs();
        var first = instance != null && file.World.Instance(instance) != null ? instance : file.World.Start.Instance;
        _selected = new OutlinerItem(first);
        Log.Info("Edit", $"Mission {number}: {file.World.Instances.Count} instances, {file.World.Links.Count} links, start {file.World.Start.Instance}");
        return Show(first, file.World.Start.Instance == first ? file.World.Start.Arrival : 0);
    }

    /// <summary>Builds an instance's level (the old one's GPU resources released) and puts the camera
    /// above its player start for <paramref name="arrival"/>.</summary>
    public bool Show(string instanceId, int arrival = 0, bool keepCamera = false)
    {
        if (_world?.World.Instance(instanceId) is not { } instance)
        {
            return false;
        }

        var clock = Stopwatch.StartNew();
        _renderer.Release(_levelScope);
        (_renderer.ShadeTable, _renderer.Fog, _renderer.PlaneRemap, _renderer.PlaneGrade) = (default, null, null, null);
        (_level, _levelInstance, _picking, _robots) = (null, null, null, null);
        _effects.Forget();
        _levelScope = _renderer.Mark();
        var map = EditorWorld.MapOf(instance);
        try
        {
            // A layer edited since it was opened bakes here from the document (E6 keeps such blocks); unsaved
            // robot types (UnsavedRobots) bake every map, a stored block has the saved ones.
            var edits = ViewEdits(instance);
            var stored = edits == null && SkyNet.Data.GameTables.RobotResolver == null;
            _splash?.Step(SplashScreen.Map);
            _level = Level.Load(_renderer, _data, map, staticRobots: false,
                block: stored ? _blocks?.Load(MissionFile.BlockMap(instance)) : null, edited: edits);
        }
        catch (Exception e) when (e is IOException or InvalidDataException)
        {
            Log.Error("Edit", $"{instance.Id}: MAP.{map:D3} does not load ({e.Message})");
            return false;
        }

        _levelInstance = instance.Id;
        _picking = new EntityPicking(_level.Map, _level.Block);
        _robots = _showRobots ? EditorRobots.For(_renderer, _data, _level) : null;
        _linksStale = true;
        _level.Detail = _settings.Detail;
        _level.FarOverride = DrawDistance;
        ApplyLook();

        var (feet, yaw, _) = PlayerStart.Find(_level.Map, _level.Wld, arrival);
        _start = (feet + new Vector3(0f, 250f, 0f), yaw, -0.25f);
        if (!keepCamera)
        {
            (_eye, _yaw, _pitch) = _start;
            PlaceShown();
        }

        ApplyVisibility();
        Log.Info("Edit", string.Create(CultureInfo.InvariantCulture,
            $"{instance.Id}: MAP.{map:D3} ({(_level.Outdoor ? "outdoor" : "indoor")}, {_level.Map.Entities.Length} entities) in {clock.Elapsed.TotalMilliseconds:0} ms"));
        return true;
    }

    /// <summary>The window's loop until it is closed (or --frames runs out): the exit code.</summary>
    public int Run()
    {
        if (!(_options.Arena is { } arena ? OpenArena(arena) : PlaceOverridden ? Open(_options.Mission ?? 1, _options.Instance) : RestorePlace()))
        {
            return 1;
        }

        if (_options.Select is { } select && !SelectAndFly(select))
        {
            return 1;
        }

        if (_options.Pos is { } pos)
        {
            _eye = pos;
        }

        _yaw = _options.Yaw is { } y ? y * MathF.PI / 180f : _yaw;
        _pitch = _options.Pitch is { } p ? p * MathF.PI / 180f : _pitch;
        if (_options.Place is { } place && !ArmPlace(place))
        {
            return 1;
        }

        if (_options.Move is { } move && !MoveEntity(move.Key, move.X, move.Y, move.Z))
        {
            return 1;
        }

        if (_options.Preview is { } fromCamera && !StartPreview(fromCamera))
        {
            return 1;
        }

        // Ready: the first frame below shows the window (Reveal) and closes the splash.
        _lastFrame = Stopwatch.GetTimestamp();
        _looping = true;
        for (var frame = 1; frame <= _options.Frames; frame++)
        {
            if (!_window.PumpEvents(_playInput ?? _input))
            {
                // A close with unsaved edits asks first; a second close goes.
                if ((_doc?.IsDirty == true || _robotModels.Dirty) && !_askExit)
                {
                    _askExit = true;
                }
                else
                {
                    break;
                }
            }

            // Folders and files picked in the OS dialogs reach their boxes here, on the main thread.
            _dialogs.Pump();

            if (_quit)
            {
                break;
            }

            if (_window.Minimised)
            {
                _window.WaitEvents();
                continue;
            }

            var now = Stopwatch.GetTimestamp();
            var dt = _capture != null ? (float)_capture.Step : (float)Math.Min(Stopwatch.GetElapsedTime(_lastFrame, now).TotalSeconds, 0.1);
            _lastFrame = now;
            CaptureClock();
            ScriptFrame();
            _fps = _fps <= 0f ? 1f / MathF.Max(dt, 1e-4f) : _fps * 0.95f + 0.05f / MathF.Max(dt, 1e-4f);
            PlayTestFrame();
            if (_preview != null)
            {
                PreviewFrame(dt, frame == _options.Frames ? _options.Screenshot : null);
                continue;
            }

            if (!ScriptedClick())
            {
                return 1;
            }

            Fly(dt);
            if (!TerrainInput(dt))
            {
                GizmoInput();
                ClickPick();
            }

            _viewClicked = false;

            RebuildWhenDue();
            var last = frame == _options.Frames;
            Draw(last ? _options.Screenshot : null, last ? _options.WindowScreenshot : null);
        }

        if (_capture != null)
        {
            Log.Info("Edit", $"Capture: {_capture.Taken} frames at {_capture.Fps} fps in {_capture.Dir}");
        }

        var saved = _options.SaveAs == null || SaveTo(_options.SaveAs, all: true);
        return saved && !LayoutFailed() ? 0 : 1;
    }

    /// <summary>--capture: this frame's file, and the levels' animation by the capture's clock.</summary>
    private void CaptureClock()
    {
        if (_capture == null)
        {
            return;
        }

        var ms = _capture.Milliseconds;
        _captureShot = _capture.Take();
        if (_level != null)
        {
            _level.ClockMs = ms;
        }

        if (_previewLevel != null)
        {
            _previewLevel.ClockMs = ms;
        }
    }

    /// <summary>--click, once the Viewport window has its size after --dock and --float: a pick (or the armed item
    /// placed) at that pixel of the view; false when the armed item finds nothing there.</summary>
    private bool ScriptedClick()
    {
        if (_options.Click is not { } click || _clickDone || _layoutFrame <= LayoutActFrame || _viewArea.Size.X < 1f)
        {
            return true;
        }

        _clickDone = true;
        var size = _viewArea.Size;
        if (_armed == null)
        {
            PickAt(click.X, click.Y, size.X, size.Y);
            return true;
        }

        return PlaceAt(click.X, click.Y, size.X, size.Y) != null;
    }

    /// <summary>--move: entity <paramref name="key"/> of the shown level moved by (dx, dy, dz), one undo step.</summary>
    private bool MoveEntity(int key, int dx, int dy, int dz)
    {
        if (LayerOf(_levelInstance) is not { } layer || !_doc!.Map(layer).TryGetEntityAt(key, out var e))
        {
            Log.Error("Edit", $"--move: the shown level has no entity {key}");
            return false;
        }

        _doc.SetFields(layer, key, new Dictionary<string, int> { ["x"] = e.X + dx, ["y"] = e.Y + dy, ["z"] = e.Z + dz }, label: "Move");
        _doc.EndDrag();
        Log.Info("Edit", $"Moved {layer} entity {key} by {dx}, {dy}, {dz}");
        return true;
    }

    /// <summary>--check-missions: every mission of the data set opened, each instance shown and drawn
    /// once with the outliner and every entity's properties built. The errors are counted by the log.</summary>
    public int CheckMissions()
    {
        var failed = 0;
        foreach (var mission in MissionTable.For(_data.Game).Missions)
        {
            if (!Open(mission.Number))
            {
                failed++;
                continue;
            }

            foreach (var instance in _world!.World.Instances)
            {
                if (!Show(instance.Id))
                {
                    failed++;
                    continue;
                }

                _world.Properties(new OutlinerItem(instance.Id));
                var rows = 0;
                foreach (var kind in EditorWorld.Kinds)
                {
                    foreach (var e in _world.Entities(instance.Id, kind))
                    {
                        rows += _world.Properties(new OutlinerItem(instance.Id, e.FileOffset)).Count;
                        _ = EditorWorld.Label(_world.Map(instance.Id)!, e);
                    }
                }

                _selected = new OutlinerItem(instance.Id);
                _window.PumpEvents(_input);
                Draw(null);
                Log.Info("Edit", $"Mission {mission.Number} {instance.Id}: drawn, {rows} property rows");
            }
        }

        return failed == 0 ? 0 : 1;
    }

    // The fly camera: right mouse held looks and WASD moves along the view, Q/E down/up, Shift faster
    // (without the right mouse W and E pick the gizmo).
    private unsafe void Fly(float dt)
    {
        float mx, my;
        var buttons = SDL_GetRelativeMouseState(&mx, &my);
        var looking = Looking((buttons & SDL_MouseButtonFlags.SDL_BUTTON_RMASK) != 0);
        _window.CaptureMouse(looking ? Capture.On : Capture.Off);
        if (looking)
        {
            var perPixel = LookPerPixel * _prefs.MouseSensitivity;
            _yaw -= mx * perPixel;
            _pitch = Math.Clamp(_pitch - (_prefs.InvertY ? -my : my) * perPixel, -1.55f, 1.55f);
        }

        if (!ViewKeys || !looking)
        {
            return;
        }

        int numKeys;
        var keys = SDL_GetKeyboardState(&numKeys);
        var count = numKeys;
        bool Held(SDL_Scancode s) => (int)s < count && keys[(int)s];
        var forward = PlayerController.Direction(_yaw, _pitch);
        var right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitY));
        var move = Vector3.Zero;
        move += Held(SDL_Scancode.SDL_SCANCODE_W) ? forward : Vector3.Zero;
        move -= Held(SDL_Scancode.SDL_SCANCODE_S) ? forward : Vector3.Zero;
        move += Held(SDL_Scancode.SDL_SCANCODE_D) ? right : Vector3.Zero;
        move -= Held(SDL_Scancode.SDL_SCANCODE_A) ? right : Vector3.Zero;
        move += Held(SDL_Scancode.SDL_SCANCODE_E) ? Vector3.UnitY : Vector3.Zero;
        move -= Held(SDL_Scancode.SDL_SCANCODE_Q) ? Vector3.UnitY : Vector3.Zero;
        if (move != Vector3.Zero)
        {
            var fast = Held(SDL_Scancode.SDL_SCANCODE_LSHIFT) || Held(SDL_Scancode.SDL_SCANCODE_RSHIFT);
            _eye += Vector3.Normalize(move) * _prefs.CameraSpeed * (fast ? FastFactor : 1f) * dt;
        }
    }

    // A left click in the view selects the nearest entity under the pointer.
    private void ClickPick()
    {
        if (!_viewClicked)
        {
            return;
        }

        // A click on a gizmo handle drags, it does not pick.
        if (_dragConsumedClick || _drag != null)
        {
            _dragConsumedClick = _drag != null;
            return;
        }

        var (x, y) = (ViewMouse.X, ViewMouse.Y);
        var (w, h) = (ViewportArea.Size.X, ViewportArea.Size.Y);
        if (_armed != null)
        {
            PlaceAt(x, y, w, h);
            return;
        }

        PickAt(x, y, w, h);
    }

    /// <summary>Selects the nearest entity of the shown instance under a pixel of a
    /// <paramref name="width"/> x <paramref name="height"/> view (nothing hit: the selection stays).</summary>
    public int? PickAt(float x, float y, float width, float height)
    {
        if (_picking == null || _levelInstance == null || _level == null)
        {
            return null;
        }

        var forward = PlayerController.Direction(_yaw, _pitch);
        var view = Matrix4x4.CreateLookAt(_eye, _eye + forward, Vector3.UnitY);
        var ray = PickRay.FromView(view, FieldOfView, width / MathF.Max(height, 1f), PickRay.Ndc(x, y, width, height));
        if (_picking.Pick(ray, _level.Outdoor ? GroundField : null, Unpickable()) is not { } offset)
        {
            Log.Info("Edit", string.Create(CultureInfo.InvariantCulture, $"Pick at {x:0}, {y:0}: nothing"));
            return null;
        }

        if (GraphPick(offset))
        {
            return offset;
        }

        if (_pickNext is { } from)
        {
            // Pick in view (Triggers panel): the click sets the link's next, the selection stays.
            _pickNext = null;
            SetNext(from.Layer, from.Key, offset);
            return offset;
        }

        if (!AddToSelection(new OutlinerItem(_levelInstance, offset)))
        {
            Select(new OutlinerItem(_levelInstance, offset));
        }

        Log.Info("Edit", string.Create(CultureInfo.InvariantCulture, $"Pick at {x:0}, {y:0} of {width:0} x {height:0} from {_eye.X:0.##},{_eye.Y:0.##},{_eye.Z:0.##} yaw {_yaw * 180f / MathF.PI:0.###} pitch {_pitch * 180f / MathF.PI:0.###}: {_levelInstance} @{offset}"));
        return offset;
    }

    /// <summary>Selects an outliner item; the outliner shows its row.</summary>
    private void Select(OutlinerItem item)
    {
        _selected = item;
        _extra.Clear();
        _reveal = true;
    }

    // The selected entity's outline, when it is on the shown instance.
    private void DrawSelection()
    {
        if (_selected is { IsInstance: false } item && item.Instance == _levelInstance
            && _picking != null && _picking.TryGet(item.Entity, out var target))
        {
            var (min, max) = (target.Min, target.Max);
            var triangles = Preview(item.Entity, target.Triangles.Span, ref min, ref max);
            _outline.Draw(min, max, triangles);
        }

        DrawExtraSelection();
    }

    /// <summary>The look and ENHANCED OPTIONS on the shown level, as the game's ApplyLook (no rebuild):
    /// Show, and a setting changed in Preferences.</summary>
    private void ApplyLook()
    {
        if (_level == null)
        {
            return;
        }

        _renderer.Lighting = _settings.Look == Look.Enhanced ? LookLighting.For(_level, _settings) : null;
        _renderer.Gamma = _renderer.Lighting != null ? Renderer.DefaultGamma : _settings.Gamma;
        _renderer.AntiAliasing = _renderer.Lighting != null ? _settings.AntiAliasing : AntiAliasing.Off;
        EnhancedEffects.Apply(_level, _settings, effects: _showEffects);
        _level.Apply(_settings.Look);
        Material[] materials = [.. _level.Materials()];
        if (_renderer.Lighting != null)
        {
            foreach (var m in materials)
            {
                _renderer.Prepare(m);
            }
        }

        _splash?.Step(SplashScreen.Shaders);
        _renderer.WarmUp(materials);
        _renderer.FlushUploads();
    }

    /// <summary>The shown level's collision world for the weather's cover (where dust and rain settle),
    /// as the game's session gives it; built on first need, outdoors with a mod's weather only.</summary>
    private CollisionWorld? CoverWorld()
    {
        if (_level is not { Outdoor: true } level || _renderer.Lighting == null || !Weather.Current.Any)
        {
            return null;
        }

        if (!ReferenceEquals(_coverOf, level))
        {
            // The ground is the live field the terrain edits keep current.
            (_coverWorld, _coverOf) = (LevelCollision.FromBlock(level.Block, level.Map, null).World, level);
            if (GroundField is { } ground)
            {
                _coverWorld.Add(ground);
            }
        }

        return _coverWorld;
    }

    private float? DrawDistance => _prefs.DrawDistance > 0f ? _prefs.DrawDistance : null;

    /// <summary>A changed view distance into <paramref name="level"/>: the far clip, the depth cue and the
    /// enhanced haze follow it, as the game's far command (indoors a clip alone).</summary>
    private void FollowDrawDistance(Level level)
    {
        if (level.FarOverride == DrawDistance)
        {
            return;
        }

        level.FarOverride = DrawDistance;
        if (_renderer.Lighting != null)
        {
            _renderer.Lighting = LookLighting.For(level, _settings);
        }

        level.Apply(_settings.Look);
    }

    private void Draw(string? screenshot, string? windowShot = null)
    {
        var forward = PlayerController.Direction(_yaw, _pitch);
        var view = Matrix4x4.CreateLookAt(_eye, _eye + forward, Vector3.UnitY);
        _renderer.ReversedZ = _renderer.Lighting != null;
        var area = ViewportArea;
        var projection = _renderer.ReversedZ
            ? Projection.ReversedInfinite(FieldOfView, area.Aspect, Near)
            : Matrix4x4.CreatePerspectiveFieldOfView(FieldOfView, area.Aspect, Near, FarPlane);
        var rotation = view with { M41 = 0f, M42 = 0f, M43 = 0f };
        Matrix4x4.Invert(rotation * projection, out var clipToDirection);
        var sky = new Vector4(0f, 0f, 0f, 1f);
        if (_level != null)
        {
            FollowDrawDistance(_level);
        }

        if (_level != null)
        {
            sky = _level.SkyColour;
            _level.Draw(_eye, FieldOfView / Math.Max(area.Pixels.Height, 1));
            _robots?.Draw(_capture is { } c ? (long)(c.Seconds * 1000f) : Environment.TickCount64);
            DrawTerrain();
            DrawGizmos();
            DrawLinks();
            DrawSelection();
            DrawGhost();
            DrawTransformGizmo();
        }

        // View > Effects off leaves out the lamp posts' pools (and, with the level, the flames and particles).
        _effects.Gather(_renderer, _level, _data, _eye, _capture?.Seconds ?? Environment.TickCount64 / 1000f, lampPosts: _showEffects, world: CoverWorld());
        sky = Storm.SkyOf(sky, _effects.Storm.Frame);

        var lens = new Lens(view, FieldOfView, area.Aspect, Near, FarPlane);
        DrawViewport(new View(view * projection, clipToDirection, _eye) { Lens = lens }, sky, screenshot, windowShot);
    }

    // ---- the panels ----

    private void Panels()
    {
        _tracker.Update();
        CaptureImGui();
        McpFrame();
        var style = ImGui.GetStyle();
        (style.FontScaleMain, style.FontSizeBase) = (_prefs.UiScale, _prefs.FontSize);
        _imgui.StyleScale = _prefs.UiScale;
        if (_preview != null)
        {
            DockSpace(0f, 0f, keepAlive: true);
            PreviewOverlay();
            return;
        }

        var size = ImGui.GetIO().DisplaySize;
        var top = MainMenu();
        var bottom = StatusBar();
        DockSpace(top, bottom);
        ViewportWindow();
        var side = MathF.Max(260f, size.X * 0.22f);
        var logHeight = MathF.Max(120f, size.Y * 0.22f);
        var height = size.Y - logHeight - top - bottom;
        if (_prefs.ShowOutliner)
        {
            Outliner(new Vector2(0f, top), new Vector2(side, height));
        }

        var placeHeight = _prefs.ShowPlace ? MathF.Round(height * (_prefs.ShowProperties ? 0.45f : 1f)) : 0f;
        if (_prefs.ShowProperties)
        {
            PropertiesPanel(new Vector2(size.X - side, top), new Vector2(side, height - placeHeight));
        }

        if (_prefs.ShowPlace)
        {
            PlacePanel(new Vector2(size.X - side, top + height - placeHeight), new Vector2(side, placeHeight));
        }

        var middle = MathF.Max(320f, side * 1.35f);
        if (_prefs.ShowTriggers)
        {
            TriggersPanel(new Vector2(side, top), new Vector2(middle, MathF.Round(height * 0.6f)));
        }

        if (_prefs.ShowMission)
        {
            MissionPanel(new Vector2(side, top + MathF.Round(height * 0.6f)), new Vector2(middle, height - MathF.Round(height * 0.6f)));
        }

        if (_prefs.ShowValidation)
        {
            ValidationPanel(new Vector2(side + middle, top), new Vector2(middle, MathF.Round(height * 0.5f)));
        }

        AssetsPanelFrame(new Vector2(side, top), new Vector2(MathF.Max(size.X - 2f * side, 320f), MathF.Round(height * 0.6f)),
            _prefs.ShowLog ? size.Y - logHeight - bottom : size.Y - bottom);
        TablesFrame(new Vector2(side, top + MathF.Round(height * 0.6f)), new Vector2(MathF.Max(size.X - 2f * side, 320f), height - MathF.Round(height * 0.6f)));
        // The Effects window over the view's free part, right of the middle panels.
        var effectsAt = side + (_prefs.ShowTriggers || _prefs.ShowMission ? middle : 0f) + (_prefs.ShowValidation ? middle : 0f);
        var effectsEnd = size.X - (_prefs.ShowProperties || _prefs.ShowPlace ? side : 0f);
        EffectsFrame(new Vector2(effectsAt, top), effectsEnd - effectsAt, height);
        GraphFrame(new Vector2(effectsAt, top), effectsEnd - effectsAt, height);
        TerrainPanel(new Vector2(size.X - side, top), new Vector2(side, MathF.Round(height * 0.7f)));
        if (_prefs.ShowLog)
        {
            LogPanel(new Vector2(0f, size.Y - logHeight - bottom), new Vector2(size.X, logHeight));
        }

        Windows();
        LayoutActions();
        DragFrame();
    }

    // The camera to an entity (its instance shown first), a little back and above it.
    private void FlyTo(OutlinerItem item)
    {
        if (_world?.Map(item.Instance) is not { } map || !map.TryGetEntityAt(item.Entity, out var e))
        {
            return;
        }

        if (item.Instance != _levelInstance && !Show(item.Instance))
        {
            return;
        }

        var target = BehaviourModel.Position(e);
        var back = PlayerController.Direction(_yaw, 0f) * 400f;
        _eye = target - back + new Vector3(0f, 250f, 0f);
        var to = target - _eye;
        _pitch = MathF.Atan2(to.Y, new Vector2(to.X, to.Z).Length());
    }

    private void PropertiesPanel(Vector2 at, Vector2 size)
    {
        PanelAt(at, size);
        if (BeginPanel(EditorPanel.Details))
        {
            if (_world == null || _selected is not { } item)
            {
                ImGui.TextDisabled("Select an instance or an entity in the outliner.");
            }
            else if (!DetailsPanel() && ImGui.BeginTable("props", 2, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.Resizable))
            {
                foreach (var (name, value) in _world.Properties(item))
                {
                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(0);
                    ImGui.TextUnformatted(name);
                    ImGui.TableSetColumnIndex(1);
                    PropertyValue(name, value);
                }

                ImGui.EndTable();
            }
        }

        ImGui.End();
    }

    /// <summary>A View-menu panel's window with its X: closing it clears the menu's tick.</summary>
    private bool BeginPanel(EditorPanel panel)
    {
        var open = true;
        var shown = ImGui.Begin(EditorPanels.Title(panel), ref open);
        if (!open)
        {
            EditorPanels.Hide(_prefs, panel);
            _prefsChanged = true;
        }

        return shown;
    }

    private void LogPanel(Vector2 at, Vector2 size)
    {
        PanelAt(at, size);
        if (BeginPanel(EditorPanel.Log))
        {
            _logLines.Clear();
            _ring.CopyLast(500, _logLines);
            var bottom = ImGui.GetScrollY() >= ImGui.GetScrollMaxY() - 1f;
            foreach (var line in _logLines)
            {
                ImGui.TextUnformatted(line);
            }

            if (bottom)
            {
                ImGui.SetScrollHereY(1f);
            }
        }

        ImGui.End();
    }

    public void Dispose()
    {
        EndPlayTest();
        StopMcp();
        KeepWindow();
        _assets?.Forget();
        _audio?.Dispose();
        _imgui.Dispose();
        _renderer.Dispose();
        _blocks?.Dispose();
        _window.Dispose();
    }
}
