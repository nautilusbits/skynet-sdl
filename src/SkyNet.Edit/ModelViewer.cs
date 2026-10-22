using System.Globalization;
using System.Numerics;
using Hexa.NET.ImGui;
using RetroEng.Content;
using RetroEng.Render;
using SkyNet.Data;
using SkyNet.Formats;
using SkyNet.Game;
using SkyNet.Game.Enemies;
using SkyNet.Game.Menu;
using SkyNet.Game.Mods;

namespace SkyNet.Edit;

/// <summary>The editor's side of the model viewer's mod actions (each one undo step, through <see cref="ModelReplace"/>).</summary>
public interface IModelHost
{
    /// <summary>The save mod's folder; null: none chosen (File > Save As picks one).</summary>
    string? SaveFolder { get; }

    /// <summary>Asks for a folder and exports <paramref name="model"/> as glTF (a robot's template beside it).</summary>
    void Export(string model);

    /// <summary>Asks for a .glb and makes it <paramref name="model"/>'s replacement in the save mod.</summary>
    void Replace(string model, ModelTarget target);

    /// <summary>Deletes the save mod's replacement of <paramref name="model"/>.</summary>
    void Remove(string model, ModelTarget target);

    /// <summary>Writes <paramref name="model"/>'s sidecar in the save mod.</summary>
    void SetSidecar(string model, ModelSidecar sidecar);

    /// <summary>The save mod's whole-robot replacement of <paramref name="model"/>, unsaved edits included.</summary>
    RobotModelState RobotState(string model);

    /// <summary>Asks for a folder and exports robot type <paramref name="type"/> assembled, as NAME.robot.glb.</summary>
    void ExportRobot(int type);

    /// <summary>Opens .3D <paramref name="model"/> ("T-REXHED") in the Assets viewer (a robot's part).</summary>
    void OpenModel(string model);

    /// <summary>Whether the view draws the enhanced look (a replacement's materials need its lighting).</summary>
    bool Enhanced { get; }

    /// <summary>Switches the view to the enhanced look.</summary>
    void UseEnhanced();
}

/// <summary>
/// The model and robot viewer (editor-tools plan, T2): the asset viewer's pane holds the playback
/// (an AIS block by name, or the .3D's frames) and, for a robot, its enemy-table row and AI
/// parameters as read-only property tables; the "Model Viewer" window shows the subject drawn by
/// the game's <see cref="EnemyRenderer"/> in the viewport's look, turned by the mouse.
/// The window shows a view target of its own (<see cref="ViewerTarget"/>): it docks, floats, goes to another monitor.
/// <code>
///   asset viewer pane ─► Controls (anim, play, step, tables)
///   Model Viewer window ─► picture ─► Render, before Present: Begin(orbit camera, studio scene) ─► EnemyRenderer ─► End
/// </code></summary>
public sealed class ModelViewer(Renderer renderer, GameData data)
{
    public const string WindowId = "###ModelViewer";
    private const float Fov = MathF.PI / 4f;
    private const float OrbitSpeed = 0.01f, ZoomStep = 0.88f, MinZoom = 0.2f, MaxZoom = 8f;
    private const float StartYaw = 0.6f, StartPitch = 0.3f, MaxPitch = 1.5f;
    private static readonly Vector4 Backdrop = new(0.16f, 0.17f, 0.19f, 1f);
    private static readonly string[] SourceLabels = ["AIS block", "All .3D frames"];
    private static readonly string[] FitLabels = [.. Enum.GetValues<ModelFit>().Select(f => f.ToString().ToLowerInvariant())];
    private static readonly string[] AlignLabels = ["base", "centre", "origin"];
    private static readonly Vector4 ReplacedColour = new(0.55f, 0.85f, 0.55f, 1f), ErrorColour = new(1f, 0.5f, 0.4f, 1f);
    private const float DragSpeed = 0.5f, ScaleSpeed = 0.01f, MinScale = 0.001f, MaxScale = 1000f, FieldWidth = 220f;
    private const string NoMod = "No mod to write to: File > Save As picks one.";
    private const string RestPose = "(rest pose)";

    private readonly EnemyModels _models = new(data);
    private ModelSubject? _subject;
    private string? _subjectKey;
    // The camera about the subject's box centre: turned, raised, and how far (× the fitting distance).
    private float _yaw = StartYaw, _pitch = StartPitch, _zoom = 1f;
    private bool _controlsShown;
    // The window's 3D picture.
    private readonly ViewerTarget _view = new(renderer);
    private double _lastTime = -1.0;
    // The game's robot renderer, made again when the shown level (its palette, its scope) changes.
    private EnemyRenderer? _robotView;
    private object? _robotViewFor = NoLevel;
    private static readonly object NoLevel = new();
    private readonly RobotFrame _frame = new();
    // A sprite's mapped model (T3): the row, the sprite's height in units, its mesh by the mods it was read from.
    private (SpriteModelRow Row, float Height)? _sprite;
    private (string Model, VirtualFileSystem? Files, ModelReplacement? Replacement)? _spriteRead;
    private ModelMesh? _spriteMesh;
    private object? _spriteMeshFor = NoLevel;
    private TextureHandle? _gamePalette;
    // The subject's mod replacement in effect (drawn in the enhanced look unless the original is asked for).
    private bool _replaced, _showOriginal;
    // The sidecar's fields as edited, the replacement they were read from, the frames as typed.
    private (ModelSidecar Sidecar, object Read, string Frames, string? Error)? _draft;
    // The window placed once this run (then where the user puts it); shown last frame.
    private bool _placed, _wasShown;
    // The title whose window the user closed (shown again for another subject).
    private string? _closedFor;

    /// <summary>The mod actions' side (replace, remove, export, sidecar); null: no buttons.</summary>
    public IModelHost? Host { get; set; }

    /// <summary>Whether a replaced subject is drawn as the original (the comparison toggle).</summary>
    public bool ShowOriginal
    {
        get => _showOriginal;
        set => _showOriginal = value;
    }

    /// <summary>Whether the subject shown has a mod replacement in effect.</summary>
    public bool Replaced => _replaced;

    /// <summary>The Model Viewer window for the model a <c>sprite_models</c> row draws <paramref name="row"/>'s
    /// sprite as (<paramref name="height"/>: the sprite's, in units), placed as the game places it; a static model
    /// of the mounted mods' <c>models/</c> (an animated one at rest).</summary>
    public void SpriteModel(SpriteModelRow row, float height)
    {
        (_subjectKey, _subject, _sprite) = (null, null, (row, height));
        _controlsShown = true;
    }

    /// <summary>The asset viewer's pane for a .3D (<paramref name="file"/>, "BOX.3D").</summary>
    public void ModelControls(string file)
    {
        Use("model:" + file, () => ModelSubject.ForModel(_models, file));
        Replacement(file, ModelTarget.Prop);
        Controls();
    }

    /// <summary>The asset viewer's pane for enemy type <paramref name="type"/>.</summary>
    public void RobotControls(AisData ais, int type)
    {
        Use("robot:" + type.ToString(CultureInfo.InvariantCulture), () => ModelSubject.ForRobot(ais, _models, type));
        var name = type >= 0 && type < ais.Types.Length ? ModelReplace.NameOf(ais.Types[type].ModelFile).ToUpperInvariant() : null;
        if (name != null)
        {
            WholeRobot(ais, type, name);
        }

        Controls();
        if (name != null)
        {
            Clips(name);
            Parts(ais, type);
        }

        if (_subject?.Type is { } t)
        {
            RobotTables.Draw(t);
        }
    }

    // The whole robot's replacement: export, replace, revert, its state (unsaved until File > Save) and who else it draws.
    private void WholeRobot(AisData ais, int type, string name)
    {
        var folder = Host?.SaveFolder;
        _replaced = ModContent.Robot(name) != null;
        var state = Host?.RobotState(name) ?? RobotModelState.None;
        var path = ModelReplace.GlbPath(name, ModelTarget.Robot);
        if (Host is { } host)
        {
            RobotActions(host, type, name, folder, state);
        }

        ImGui.TextColored(_replaced ? ReplacedColour : ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled], state switch
        {
            RobotModelState.Unsaved => $"Replaced by {path} (unsaved: File > Save writes it)",
            RobotModelState.Reverted => $"Original (unsaved: File > Save deletes {path})",
            RobotModelState.Saved => $"Replaced by {path} (save mod)",
            _ when _replaced => $"Replaced by {path} (another mod)",
            _ => "Original: Replace with glTF... puts one model in place of the whole robot.",
        });
        var shared = ais.Types.Where(t => t.Index != type && ModelReplace.NameOf(t.ModelFile).Equals(name, StringComparison.OrdinalIgnoreCase))
            .Select(t => t.Index.ToString(CultureInfo.InvariantCulture)).ToArray();
        if (shared.Length > 0)
        {
            ImGui.TextDisabled($"Also drawn for the robot types built on {name}: {string.Join(", ", shared)}");
        }

        if (!_replaced)
        {
            return;
        }

        ImGui.SameLine();
        ImGui.Checkbox("Show original", ref _showOriginal);
        if (Host is { Enhanced: false } h)
        {
            ImGui.TextDisabled("Drawn unlit: its materials show in the ENHANCED look.");
            ImGui.SameLine();
            if (ImGui.SmallButton("Switch to ENHANCED"))
            {
                h.UseEnhanced();
            }
        }
    }

    private static void RobotActions(IModelHost host, int type, string name, string? folder, RobotModelState state)
    {
        if (ImGui.Button("Export as glTF..."))
        {
            host.ExportRobot(type);
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip($"{name}.robot.glb: the robot assembled, a joint per segment, a clip per AIS block. Edit it, then Replace with glTF...");
        }

        ImGui.SameLine();
        ImGui.BeginDisabled(folder == null);
        if (ImGui.Button("Replace with glTF..."))
        {
            host.Replace(name, ModelTarget.Robot);
        }

        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.SetTooltip(folder == null ? NoMod : $"One .glb or .gltf for the whole robot, {ModelReplace.GlbPath(name, ModelTarget.Robot)} in the save mod (File > Save writes it)");
        }

        ImGui.SameLine();
        ImGui.BeginDisabled(state is not (RobotModelState.Saved or RobotModelState.Unsaved));
        if (ImGui.Button("Revert to original"))
        {
            host.Remove(name, ModelTarget.Robot);
        }

        ImGui.EndDisabled();
    }

    // The clips the game plays for the robot's blocks: the replacement's by name, or the names a replacement gives them.
    private void Clips(string name)
    {
        if (_subject is not { Anims.Count: > 0 } s)
        {
            return;
        }

        var replacement = ModContent.Robot(name);
        var open = replacement != null ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None;
        if (!ImGui.CollapsingHeader("Clips", open))
        {
            return;
        }

        if (replacement == null)
        {
            ImGui.TextWrapped($"A replacement's clips by name: {string.Join(", ", s.Anims.Select(a => a.Name))} (docs/modding.md, Robot template).");
            return;
        }

        var clips = replacement.Model.Clips.Select(c => c.Name).ToArray();
        if (!ImGui.BeginTable("##clips", 2, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
        {
            return;
        }

        ImGui.TableSetupColumn("AIS block");
        ImGui.TableSetupColumn("plays clip");
        ImGui.TableHeadersRow();
        foreach (var row in RobotAssets.ClipMap(s.Anims, clips))
        {
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            ImGui.TextUnformatted(row.Block);
            ImGui.TableSetColumnIndex(1);
            if (row.Clip == null)
            {
                ImGui.TextColored(ErrorColour, RestPose);
                continue;
            }

            ImGui.TextUnformatted(row.Clip);
        }

        ImGui.EndTable();
    }

    // The robot's segments and wreck parts, each opening its .3D (its own replacement there, for advanced use).
    private void Parts(AisData ais, int type)
    {
        var parts = RobotAssets.Parts(ais, type);
        if (parts.Count == 0 || !ImGui.CollapsingHeader($"Parts ({parts.Count})"))
        {
            return;
        }

        ImGui.TextDisabled("Each part's .3D, replaced alone (a static mesh per frame) in its viewer.");
        foreach (var (part, i) in parts.Select((p, i) => (p, i)))
        {
            ImGui.PushID(i);
            if (ImGui.SmallButton("Open") && Host is { } host)
            {
                host.OpenModel(part.Model);
            }

            ImGui.SameLine();
            ImGui.TextUnformatted(string.Create(CultureInfo.InvariantCulture,
                $"{(part.Kind == RobotPartKind.Segment ? "segment" : "wreck")}  {part.Model}  (type {part.Type})"));
            ImGui.PopID();
        }
    }

    // The subject for key, made when another is asked for; the camera back to its start.
    private void Use(string key, Func<ModelSubject?> make)
    {
        (_sprite, _replaced) = (null, false);
        if (_subjectKey == key)
        {
            return;
        }

        (_subjectKey, _subject) = (key, make());
        (_yaw, _pitch, _zoom) = (StartYaw, StartPitch, 1f);
    }

    // The playback: the block or the .3D's frames, play/pause, step, the step slider.
    private void Controls()
    {
        _controlsShown = true;
        if (_subject is not { } s)
        {
            ImGui.TextDisabled("No preview: it does not load.");
            return;
        }

        ImGui.TextDisabled($"{s.Parts.Count} part(s) assembled, {s.ModelFrames} .3D frame(s); drawn in the Model Viewer window");
        var source = (int)s.Source;
        ImGui.BeginDisabled(s.Anims.Count == 0);
        ImGui.SetNextItemWidth(140f);
        if (ImGui.Combo("##source", ref source, SourceLabels, SourceLabels.Length))
        {
            s.Show((PlaybackSource)source);
        }

        ImGui.EndDisabled();
        if (s.Source == PlaybackSource.Block)
        {
            ImGui.SameLine();
            AnimCombo(s);
        }

        if (ImGui.Button("<"))
        {
            s.StepBy(-1);
        }

        ImGui.SameLine();
        if (ImGui.Button(s.Playing ? "Pause" : "Play"))
        {
            s.TogglePlay();
        }

        PlayKey.Tooltip();
        PlayKey.Poll(s, PlayFocus.AssetViewer);

        ImGui.SameLine();
        if (ImGui.Button(">"))
        {
            s.StepBy(1);
        }

        ImGui.SameLine();
        var step = s.Step;
        ImGui.SetNextItemWidth(MathF.Max(ImGui.GetContentRegionAvail().X - 90f, 60f));
        if (ImGui.SliderInt("##step", ref step, 0, Math.Max(0, s.Steps - 1)))
        {
            s.Playing = false;
            s.GoTo(step);
        }

        ImGui.SameLine();
        ImGui.TextUnformatted($"frame {s.Frame}");
    }

    // Export, "Replace with 3D model...", "Remove replacement", the comparison toggle and a prop's sidecar.
    private void Replacement(string model, ModelTarget target)
    {
        var name = ModelReplace.NameOf(model);
        var folder = Host?.SaveFolder;
        var prop = target == ModelTarget.Prop ? ModContent.Model(name) : null;
        _replaced = target == ModelTarget.Prop ? prop != null : ModContent.Robot(name) != null;
        var own = folder != null && ModelReplace.Has(folder, name, target);
        if (Host is { } host)
        {
            Actions(host, name, target, folder, own);
        }

        if (!_replaced)
        {
            ImGui.TextDisabled($"No replacement: a mod's {ModelReplace.GlbPath(name, target)} draws instead in the enhanced look.");
            return;
        }

        ImGui.TextColored(ReplacedColour, $"Replaced by {ModelReplace.GlbPath(name, target)} ({(own ? "save mod" : "another mod")}), enhanced look");
        ImGui.SameLine();
        ImGui.Checkbox("Show original", ref _showOriginal);
        if (Host is { Enhanced: false } h)
        {
            ImGui.TextDisabled("Drawn unlit: its materials show in the ENHANCED look.");
            ImGui.SameLine();
            if (ImGui.SmallButton("Switch to ENHANCED"))
            {
                h.UseEnhanced();
            }
        }

        if (prop != null)
        {
            Sidecar(name, prop, own);
        }
    }

    private static void Actions(IModelHost host, string name, ModelTarget target, string? folder, bool own)
    {
        if (ImGui.Button(target == ModelTarget.Robot ? "Export template..." : "Export glTF..."))
        {
            host.Export(name);
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(target == ModelTarget.Robot
                ? $"{name}.robot.glb: a joint per segment, a clip per AIS block. Edit it, then Replace with 3D model..."
                : $"{name}.glb: a start for its replacement");
        }

        ImGui.SameLine();
        ImGui.BeginDisabled(folder == null);
        if (ImGui.Button("Replace with 3D model..."))
        {
            host.Replace(name, target);
        }

        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.SetTooltip(folder == null ? NoMod : $"Copies a .glb into the save mod as {string.Join(" + ", ModelReplace.Paths(name, target))}");
        }

        ImGui.SameLine();
        ImGui.BeginDisabled(!own);
        if (ImGui.Button("Remove replacement"))
        {
            host.Remove(name, target);
        }

        ImGui.EndDisabled();
    }

    // The sidecar's fields: each change written as one undo step (a drag when let go); another mod's are read-only.
    private void Sidecar(string name, ModelReplacement read, bool own)
    {
        if (_draft is not { } d || !ReferenceEquals(d.Read, read))
        {
            d = (read.Sidecar, read, ModelReplace.FramesText(read.Sidecar.Frames, read.Sidecar.AllFrames), null);
        }

        var s = d.Sidecar;
        var commit = false;
        ImGui.BeginDisabled(!own || Host == null);
        if (ImGui.BeginTable("##sidecar", 2, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("field", ImGuiTableColumnFlags.WidthFixed, 70f);
            ImGui.TableSetupColumn("value", ImGuiTableColumnFlags.WidthStretch);

            var rotation = s.Rotation;
            Row("rotation");
            ImGui.DragFloat3("##rotation", ref rotation, DragSpeed, -360f, 360f, "%.1f");
            commit |= ImGui.IsItemDeactivatedAfterEdit();
            s = s with { Rotation = rotation };

            var fit = (int)s.Fit;
            Row("fit");
            if (ImGui.Combo("##fit", ref fit, FitLabels, FitLabels.Length))
            {
                (s, commit) = (s with { Fit = (ModelFit)fit }, true);
            }

            var scale = s.Scale;
            Row("scale");
            ImGui.DragFloat("##scale", ref scale, ScaleSpeed, MinScale, MaxScale, "%.3f");
            commit |= ImGui.IsItemDeactivatedAfterEdit();
            s = s with { Scale = Math.Clamp(scale, MinScale, MaxScale) };

            var align = (int)s.Align;
            Row("align");
            if (ImGui.Combo("##align", ref align, AlignLabels, AlignLabels.Length))
            {
                (s, commit) = (s with { Align = (ModelAlign)align }, true);
            }

            var offset = s.Offset;
            Row("offset");
            ImGui.DragFloat3("##offset", ref offset, DragSpeed, 0f, 0f, "%.1f");
            commit |= ImGui.IsItemDeactivatedAfterEdit();
            s = s with { Offset = offset };

            // Frames as typed ("all", "0, 2-5"), read as the game reads them when the field is left.
            var frames = d.Frames;
            Row("frames");
            ImGui.InputTextWithHint("##frames", "all, or 0, 2-5", ref frames, 256);
            if (ImGui.IsItemDeactivatedAfterEdit())
            {
                try
                {
                    var (list, all) = ModelReplace.ParseFrames(frames);
                    (s, commit, d.Error) = (s with { Frames = list, AllFrames = all }, true, null);
                }
                catch (FormatException e)
                {
                    d.Error = e.Message;
                }
            }

            d.Frames = frames;
            ImGui.EndTable();
        }

        ImGui.EndDisabled();
        if (d.Error != null)
        {
            ImGui.TextColored(ErrorColour, "frames: " + d.Error);
        }

        if (s.FrameModels.Count > 0)
        {
            ImGui.TextDisabled($"frameModels: {string.Join(", ", s.FrameModels.Select(f => f.Model))} (kept; edit the .json)");
        }

        _draft = (s, d.Read, d.Frames, d.Error);
        if (commit && Host != null)
        {
            Host.SetSidecar(name, s);
        }
    }

    // A field's row: its name, then the value's cell (the next item's width set).
    private static void Row(string field)
    {
        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        ImGui.TextUnformatted(field);
        ImGui.TableSetColumnIndex(1);
        ImGui.SetNextItemWidth(MathF.Min(ImGui.GetContentRegionAvail().X, FieldWidth));
    }

    private static void AnimCombo(ModelSubject s)
    {
        var current = s.Anims[s.AnimIndex];
        ImGui.SetNextItemWidth(MathF.Max(ImGui.GetContentRegionAvail().X, 120f));
        if (!ImGui.BeginCombo("##anim", Label(current)))
        {
            return;
        }

        for (var i = 0; i < s.Anims.Count; i++)
        {
            if (ImGui.Selectable(Label(s.Anims[i]), i == s.AnimIndex))
            {
                s.Select(i);
            }
        }

        ImGui.EndCombo();
    }

    /// <summary>"walk: frames 5..20 (16 steps, 16 fps)".</summary>
    private static string Label(RobotAnim a)
    {
        var f = a.Block.Frames;
        return string.Create(CultureInfo.InvariantCulture,
            $"{a.Name}: frames {f[0].Frame}..{f[^1].Frame} ({f.Length} steps, {a.Block.Fps} fps)##{a.Block.Va}");
    }

    /// <summary>The Model Viewer window, after the asset viewer (shown while its pane showed a model this frame).</summary>
    public void Window(Vector2 at, Vector2 size)
    {
        var shown = _controlsShown;
        _controlsShown = false;
        var title = _sprite is { } sprite ? $"{sprite.Row.Model} for sprite {sprite.Row.Sprite}" : _subject?.Title;
        if (title != _closedFor)
        {
            _closedFor = null;
        }

        var appearing = !_wasShown;
        _wasShown = shown && title != null && _closedFor == null;
        if (!_wasShown)
        {
            return;
        }

        var now = ImGui.GetTime();
        _subject?.Advance(_lastTime < 0.0 ? 0f : (float)(now - _lastTime));
        _lastTime = now;

        var main = ImGui.GetMainViewport();
        // Beside the Assets panel the first time this run (a layout from before may hide it), then where the user puts it.
        var place = _placed ? ImGuiCond.FirstUseEver : ImGuiCond.Always;
        ImGui.SetNextWindowPos(main.Pos + at, place);
        ImGui.SetNextWindowSize(size, place);
        _placed = true;
        if (appearing)
        {
            ImGui.SetNextWindowFocus();
        }

        const ImGuiWindowFlags flags = ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;
        var open = true;
        if (ImGui.Begin($"Model Viewer: {title}{WindowId}", ref open, flags))
        {
            Orbit();
            if (_sprite == null && _subject is { } s)
            {
                PlayKey.Poll(s);
            }
        }

        ImGui.End();

        // Closed: hidden until another subject is picked.
        if (!open)
        {
            _closedFor = title;
        }
    }

    // The 3D area: drag turns, the wheel zooms, a double-click puts the camera back.
    private void Orbit()
    {
        var at = ImGui.GetCursorScreenPos();
        var size = Vector2.Max(ImGui.GetContentRegionAvail(), new Vector2(32f));
        _view.Picture("##orbit", size);
        if (ImGui.IsItemActive() && ImGui.IsMouseDragging(ImGuiMouseButton.Left, 0f))
        {
            var d = ImGui.GetIO().MouseDelta;
            _yaw -= d.X * OrbitSpeed;
            _pitch = Math.Clamp(_pitch + d.Y * OrbitSpeed, -MaxPitch, MaxPitch);
        }

        if (ImGui.IsItemHovered())
        {
            var wheel = ImGui.GetIO().MouseWheel;
            if (wheel != 0f)
            {
                _zoom = Math.Clamp(_zoom * MathF.Pow(ZoomStep, wheel), MinZoom, MaxZoom);
            }

            if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
            {
                (_yaw, _pitch, _zoom) = (StartYaw, StartPitch, 1f);
            }
        }

        ImGui.GetWindowDrawList().AddText(at + new Vector2(6f, 4f), ImGui.GetColorU32(ImGuiCol.TextDisabled),
            "drag: turn   wheel: zoom   double-click: reset");
    }

    /// <summary>Queues the window's view (before Present): the subject at <see cref="ViewerStudio.Subject"/> in the
    /// target's own scene, drawn by the game's robot renderer with <paramref name="level"/>'s palette (the game
    /// palette without a level) in the renderer's look.</summary>
    public void Render(Level? level)
    {
        if (!_view.Shown)
        {
            return;
        }

        if (_sprite is { } sprite)
        {
            RenderSprite(level, sprite.Row, sprite.Height);
            return;
        }

        if (_subject is not { } s || RobotView(level) is not { } view)
        {
            return;
        }

        view.LookOverride = _replaced && !_showOriginal ? Look.Enhanced : Look.Original;
        s.Robot.Position = ViewerStudio.Subject;
        var bounds = s.Bounds();
        if (!BeginView((bounds.Min, bounds.Max)))
        {
            return;
        }

        view.Capture(s.Robots, _frame);
        view.Draw(_frame, s.ClockMs);
        _view.End();
    }

    // The sprite's model, standing on the origin as on the sprite's foot, turning and bobbing as in game.
    private void RenderSprite(Level? level, SpriteModelRow row, float height)
    {
        if (SpriteMesh(level, row) is not { } read || _spriteMesh is not { } mesh)
        {
            return;
        }

        // Framed for every turn and the whole bob: the placed box's corners, spun about Y.
        var model = new SpriteModel { Sprite = 0, Row = row, Static = read.Replacement, Box = read.Box };
        var p = ViewerStudio.Subject;
        var still = model.World(p, height, 0f, 0);
        var (lo, hi) = read.Box;
        var corners = Enumerable.Range(0, 8).Select(i => Vector3.Transform(new Vector3((i & 1) == 0 ? lo.X : hi.X, (i & 2) == 0 ? lo.Y : hi.Y, (i & 4) == 0 ? lo.Z : hi.Z), still)).ToArray();
        var reach = corners.Max(c => MathF.Sqrt((c.X - p.X) * (c.X - p.X) + (c.Z - p.Z) * (c.Z - p.Z)));
        var bob = (float)row.Bob;
        if (!BeginView((p + new Vector3(-reach, corners.Min(c => c.Y) - bob, -reach), p + new Vector3(reach, corners.Max(c => c.Y) + bob, reach))))
        {
            return;
        }

        var world = model.World(p, height, Environment.TickCount64 / 1000f, 0);
        foreach (var part in mesh.Parts)
        {
            mesh.Draw(renderer, part, 0, world);
        }

        _view.End();
    }

    // The row's model read from the mounted mods (again when they or the file change) and its mesh for the level's palette.
    private (ModelReplacement Replacement, (Vector3 Min, Vector3 Max) Box)? SpriteMesh(Level? level, SpriteModelRow row)
    {
        var files = ModContent.Files;
        if (_spriteRead is not { } read || read.Model != row.Model || !ReferenceEquals(read.Files, files))
        {
            var name = Path.GetFileNameWithoutExtension(row.Model);
            read = (row.Model, files, files != null && name.Length > 0 ? ModelReplacement.Load(files, name) : null);
            (_spriteRead, _spriteMesh, _spriteMeshFor) = (read, null, NoLevel);
        }

        if (read.Replacement is not { } replacement || PaletteOf(level) is not { } palette)
        {
            return null;
        }

        if (!ReferenceEquals(_spriteMeshFor, level))
        {
            (_spriteMesh, _spriteMeshFor) = (ReplacementMesh.Build(renderer, palette, replacement, Matrix4x4.Identity), level);
        }

        var corners = replacement.Model.Primitives.SelectMany(p => p.Positions).ToArray();
        var box = corners.Length > 0 ? (corners.Aggregate(Vector3.Min), corners.Aggregate(Vector3.Max)) : (Vector3.Zero, Vector3.Zero);
        return (replacement, box);
    }

    // Opens the window's view over the backdrop in the studio scene, the camera orbiting the box's centre.
    private bool BeginView((Vector3 Min, Vector3 Max) box)
    {
        var centre = (box.Min + box.Max) * 0.5f;
        var radius = MathF.Max(1f, (box.Max - box.Min).Length() * 0.5f);
        // Fitted in the narrower of the two fields of view (a tall docked panel: the horizontal one).
        var half = MathF.Min(MathF.Tan(Fov * 0.5f), MathF.Tan(Fov * 0.5f) * _view.Aspect);
        var distance = radius / half * _zoom;
        // Yaw 0 looks at the robot's face: it faces −Z.
        var eye = centre + distance * new Vector3(MathF.Sin(_yaw) * MathF.Cos(_pitch), MathF.Sin(_pitch), -MathF.Cos(_yaw) * MathF.Cos(_pitch));
        var near = MathF.Max(1f, distance - radius * 2f);
        var far = distance + radius * 2f;
        return _view.Begin(_view.Camera(eye, centre, Fov, near, far), Backdrop, ViewerStudio.Scene(renderer.Lighting, far));
    }

    /// <summary>Frees the window's view target (the viewer is made again for another data set).</summary>
    internal void Release() => _view.Release();

    // The renderer for the shown level's palette and scope; no level: the game palette.
    private EnemyRenderer? RobotView(Level? level)
    {
        if (_robotView != null && ReferenceEquals(_robotViewFor, level))
        {
            return _robotView;
        }

        if (PaletteOf(level) is not { } palette)
        {
            return null;
        }

        // No level for the renderer itself: its indoor shade would be the level's light at the origin.
        _robotView = new EnemyRenderer(renderer, new TextureStore(renderer, data), palette);
        _robotViewFor = level;
        return _robotView;
    }

    // The shown level's palette; no level: the game palette (made once).
    private TextureHandle? PaletteOf(Level? level)
    {
        if (level != null)
        {
            return level.Palette;
        }

        if (_gamePalette is { } made)
        {
            return made;
        }

        if (data.LoadPalette() is not { } p)
        {
            return null;
        }

        var rgba = new byte[Palette.Colors * 4];
        for (var i = 0; i < Palette.Colors; i++)
        {
            var (r, g, b) = p[i];
            (rgba[i * 4], rgba[i * 4 + 1], rgba[i * 4 + 2], rgba[i * 4 + 3]) = (r, g, b, 255);
        }

        _gamePalette = renderer.CreatePalette(rgba);
        return _gamePalette;
    }
}
