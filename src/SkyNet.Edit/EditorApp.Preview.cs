using System.Globalization;
using System.Numerics;
using Hexa.NET.ImGui;
using RetroEng.Diagnostics;
using RetroEng.Platform;
using RetroEng.Render;
using SkyNet.Formats;
using SkyNet.Game;
using SkyNet.Game.Behaviour;
using SkyNet.Game.Campaign;
using SkyNet.Game.Enemies;
using SkyNet.Game.Enhanced;
using SkyNet.Game.Player;
using SkyNet.Game.Weapons;

namespace SkyNet.Edit;

/// <summary>Play > Preview and Play > Fire trigger (editor plan step E9): the edited mission played in the
/// editor's view by the game's session and keys until Esc, then the edited level built again; a selected
/// record's chain fired once on a headless copy, what happened written to the log.</summary>
internal sealed partial class EditorApp
{
    private PreviewSession? _preview;
    private Input? _playInput;
    private Level? _previewLevel;
    private EnemyRenderer? _previewRobots;
    private WeaponRenderer? _previewWeapons;
    private (string Id, int Map)? _previewShown;
    private string? _returnTo;
    private float _previewClock;
    private TransfrmPrs? _transfrm;

    /// <summary>A preview runs (the editor's panels and keys rest).</summary>
    public bool Previewing => _preview != null;

    // The Play menu of the menu bar.
    private void PlayMenu()
    {
        if (ImGui.MenuItem("Preview", "F5", false, _doc != null && _levelInstance != null))
        {
            StartPreview(fromCamera: false);
        }

        if (ImGui.MenuItem("Preview from camera", "Shift+F5", false, _doc != null && _levelInstance != null))
        {
            StartPreview(fromCamera: true);
        }

        ImGui.Separator();
        var running = GameRunning;
        if (ImGui.MenuItem(running ? "Reload in game window" : "Play-test in game window", "Ctrl+F5", false, _doc?.Mission != null && _levelInstance != null))
        {
            PlayTestInGame(fromCamera: false);
        }

        if (ImGui.MenuItem("Play-test from camera", "Ctrl+Shift+F5", false, !running && _doc?.Mission != null && _levelInstance != null))
        {
            PlayTestInGame(fromCamera: true);
        }

        ImGui.Separator();
        if (ImGui.MenuItem("Fire trigger", "F6", false, SelectedEntity(out _, out _)))
        {
            FireSelected();
        }
    }

    // F5 / Shift+F5 / Ctrl+F5 / Ctrl+Shift+F5 / F6 with the editor's keys (Shortcuts calls it).
    private void PlayKeys(ImGuiIOPtr io)
    {
        if (io.KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.F5, false))
        {
            PlayTestInGame(fromCamera: io.KeyShift);
        }
        else if (ImGui.IsKeyPressed(ImGuiKey.F5, false))
        {
            StartPreview(fromCamera: io.KeyShift);
        }
        else if (ImGui.IsKeyPressed(ImGuiKey.F6, false))
        {
            FireSelected();
        }
    }

    /// <summary>A preview's baked block of an instance whose layer and terrain are unedited; null: built from the map.</summary>
    private SkyNet.Game.Baking.MapBlock? PreviewBlock(RetroEng.World.WorldInstance i) =>
        ShownEdits(i) == null && EditedWld(EditorWorld.MapOf(i)) == null ? _blocks?.Load(MissionFile.BlockMap(i)) : null;

    /// <summary>Starts the preview on the shown instance: at its player start, or where the camera is.</summary>
    public bool StartPreview(bool fromCamera)
    {
        if (_preview != null || _doc?.Mission == null || _levelInstance == null || _world == null)
        {
            return false;
        }

        // An agent's play-test holds the map resolvers until it ends.
        if (_play != null)
        {
            Log.Info("Preview", "An MCP play-test is running; preview after it");
            return false;
        }

        var mission = _arena == null ? MissionTable.For(_data.Game).ByNumber(_world.Mission.Number) : null;
        var arrival = _levelInstance == _doc.Mission.World.Start.Instance ? _doc.Mission.World.Start.Arrival : 0;
        Vector3? feet = fromCamera ? _eye - new Vector3(0f, PlayerController.EyeHeight, 0f) : null;
        try
        {
            _preview = new PreviewSession(_data, _doc, mission, _levelInstance, arrival, PreviewBlock, feet, _yaw, EditedWld);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException or RetroEng.World.WorldFormatException)
        {
            Log.Error("Preview", $"{_levelInstance}: does not start ({e.Message})");
            return false;
        }

        _returnTo = _levelInstance;
        _playInput = new Input(_preview.Controls.Actions);
        _previewShown = null;
        _previewClock = 0f;
        _drag = null;
        _renderer.Release(_levelScope);
        (_level, _picking) = (null, null);
        _effects.Forget();
        _levelScope = _renderer.Mark();
        Log.Info("Preview", $"{_levelInstance}: playing{(fromCamera ? " from the camera" : "")} (Esc returns to editing)");
        return true;
    }

    /// <summary>Ends the preview: the session goes, the shown instance is built again as edited.</summary>
    public void StopPreview()
    {
        if (_preview == null)
        {
            return;
        }

        _preview.Dispose();
        (_preview, _playInput, _previewLevel, _previewRobots, _previewWeapons, _previewShown) = (null, null, null, null, null, null);
        _effects.Forget();
        _window.CaptureMouse(Capture.Off);
        Log.Info("Preview", "Back to editing");
        if (_returnTo is { } id)
        {
            Show(id, keepCamera: true);
        }
    }

    // One frame of the preview: the keys, the game's ticks, the level the session is on, drawn.
    private void PreviewFrame(float dt, string? screenshot)
    {
        var preview = _preview!;
        var session = preview.Session;
        if (_playInput!.WasPressed(preview.Controls.Pause))
        {
            StopPreview();
            Draw(screenshot);
            return;
        }

        if (!_options.Offscreen)
        {
            _window.CaptureMouse(Capture.On);
        }

        _previewClock += dt;
        while (_previewClock >= GameSession.Dt)
        {
            _previewClock -= GameSession.Dt;
            preview.Tick(_playInput.Latch());
        }

        if (session.Instance is { } instance && _previewShown != (instance.Id, session.MapNumber))
        {
            BuildPreviewLevel(preview, instance);
        }

        DrawPreview(session, _previewClock / GameSession.Dt, screenshot);
    }

    // The session's map built for the view (the editor's level scope reused).
    private void BuildPreviewLevel(PreviewSession preview, RetroEng.World.WorldInstance instance)
    {
        var session = preview.Session;
        _renderer.Release(_levelScope);
        (_renderer.ShadeTable, _renderer.Fog, _renderer.PlaneRemap, _renderer.PlaneGrade) = (default, null, null, null);
        _levelScope = _renderer.Mark();
        var edits = _doc!.Layers.Contains(instance.Layer) ? ShownEdits(instance) : null;
        _previewLevel = Level.Load(_renderer, preview.Data, session.MapNumber, staticRobots: false,
            block: edits == null ? _blocks?.Load(session.BlockMap) : null, edited: edits);
        _previewRobots = new EnemyRenderer(_renderer, new TextureStore(_renderer, preview.Data), _previewLevel.Palette, _previewLevel);
        _previewLevel.Detail = _settings.Detail;
        _previewLevel.FarOverride = DrawDistance;
        _previewLevel.Poses = session.Behaviour;
        _previewLevel.HiddenSprites = session.TakenPickups;
        _renderer.Lighting = _settings.Look == SkyNet.Game.Menu.Look.Enhanced ? LookLighting.For(_previewLevel, _settings) : null;
        _renderer.Gamma = _renderer.Lighting != null ? Renderer.DefaultGamma : _settings.Gamma;
        _renderer.AntiAliasing = _renderer.Lighting != null ? _settings.AntiAliasing : AntiAliasing.Off;
        // The shots, blasts and drops as the game draws them (its impact dust and explosions in ENHANCED).
        _previewWeapons = new WeaponRenderer(_renderer, preview.Data, _previewLevel.Palette);
        session.Weapons.EffectFrames = _previewWeapons.FramesOf;
        EnhancedEffects.Apply(_previewLevel, _settings, _previewWeapons);
        _previewWeapons.Apply(_renderer.Lighting);
        _previewLevel.Apply(_settings.Look);
        Material[] materials = [.. _previewLevel.Materials(), .. _previewWeapons.Preload(session.Weapons.Table)];
        foreach (var m in _renderer.Lighting != null ? materials : [])
        {
            _renderer.Prepare(m);
        }

        _renderer.WarmUp(materials);
        _renderer.FlushUploads();
        _previewShown = (instance.Id, session.MapNumber);
        Log.Info("Preview", string.Create(CultureInfo.InvariantCulture, $"{instance.Id}: MAP.{session.MapNumber:D3}{(edits != null ? " as edited" : "")}"));
    }

    private void DrawPreview(GameSession session, float alpha, string? screenshot)
    {
        var (eye, _, _, view) = session.Player.View(alpha);
        _renderer.ReversedZ = _renderer.Lighting != null;
        var projection = _renderer.ReversedZ
            ? Projection.ReversedInfinite(FieldOfView, _renderer.AspectRatio, Near)
            : Matrix4x4.CreatePerspectiveFieldOfView(FieldOfView, _renderer.AspectRatio, Near, FarPlane);
        var rotation = view with { M41 = 0f, M42 = 0f, M43 = 0f };
        Matrix4x4.Invert(rotation * projection, out var clipToDirection);
        var sky = new Vector4(0f, 0f, 0f, 1f);
        _renderer.Lights = default;
        if (_previewLevel is { } level)
        {
            FollowDrawDistance(level);
            sky = level.SkyColour;
            level.WaterSurface = session.Water?.Surface;
            level.Drops = session.Drops;
            level.Draw(eye, FieldOfView / Math.Max(_renderer.Target.Height, 1));
            if (session.Robots is { } robots)
            {
                _previewRobots?.Draw(robots, (long)(session.Clock * 1000f));
            }

            if (_previewWeapons is { } weapons)
            {
                weapons.Draw(session.Weapons, eye);
                weapons.DrawDrops(session.Drops, eye, (long)(session.Clock * 1000f));
            }

            // Everything the game lights: the map's lights as its behaviour switches them, the lamp posts, the shots.
            _effects.Gather(_renderer, level, _preview!.Data, eye, session.Clock, session.Behaviour, session.Weapons, world: session.World);
            sky = Storm.SkyOf(sky, _effects.Storm.Frame);
        }

        Reveal();
        _renderer.Present(new View(view * projection, clipToDirection, eye) { Lens = new Lens(view, FieldOfView, _renderer.AspectRatio, Near, FarPlane) }, sky, screenshot ?? _captureShot);
        CloseSplash();
        SkyNet.Game.DevTools.FrameCapture.Mirror(screenshot, _captureShot);
        if (screenshot != null)
        {
            Log.Info("Edit", $"Screenshot: {screenshot}");
        }
    }

    // The panels while previewing: one line over the view.
    private void PreviewOverlay()
    {
        var session = _preview!.Session;
        var main = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(main.Pos + new Vector2(8f, 8f));
        ImGui.SetNextWindowViewport(main.ID);
        ImGui.SetNextWindowBgAlpha(0.6f);
        if (ImGui.Begin("##preview", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoInputs | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoDocking))
        {
            ImGui.TextUnformatted(string.Create(CultureInfo.InvariantCulture,
                $"PREVIEW  {session.Instance?.Id} MAP.{session.MapNumber:D3}  health {session.Soldier.Health:0}  {session.Phase}  -  Esc returns to editing"));
        }

        ImGui.End();
    }

    /// <summary>Fires the selected record's chain once on a headless copy of its edited map; the log lists what happened.</summary>
    public FireResult? FireSelected()
    {
        if (!SelectedEntity(out var layer, out var e))
        {
            Log.Warn("Fire", "Select a record of the shown level first");
            return null;
        }

        _transfrm ??= BehaviourModel.LoadTransfrm(_data);
        var map = _doc!.Map(layer);
        if (TriggerFire.Fire(map, _transfrm, e.FileOffset) is not { } result)
        {
            return null;
        }

        foreach (var line in TriggerFire.Describe(map, result))
        {
            Log.Info("Fire", line);
        }

        return result;
    }
}
