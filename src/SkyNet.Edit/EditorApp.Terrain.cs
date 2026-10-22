using System.Globalization;
using System.Numerics;
using Hexa.NET.ImGui;
using RetroEng.Diagnostics;
using RetroEng.Editor;
using RetroEng.Physics;
using RetroEng.Render;
using SkyNet.Formats;
using SkyNet.Game;
using static SDL.SDL3;

namespace SkyNet.Edit;

/// <summary>
/// The Terrain mode (plan step T7, Unreal's Landscape mode cut down): the toolbar toggle (Shift+2,
/// Shift+1 back to selecting), the Terrain panel (tools, brush, tile palette, heightmap files,
/// Generate…), the brush under the pointer and the edited field drawn live.
/// <code>
///  pointer ─► MouseRay ─► BrushMath.Raycast ─► SculptBrush.Frame ─► ITerrainModel (one undo step per stroke)
///                                                                        │ TakeDirty
///  Draw ◄─ TerrainModeView (dirty chunks rebuilt, ring) ◄────────────────┘
/// </code>
/// </summary>
internal sealed partial class EditorApp
{
    private const float TileThumb = 40f;
    private const float PreviewSide = 160f;
    private static readonly TerrainTool[] TerrainTools = Enum.GetValues<TerrainTool>();
    private static readonly string[] TileTurns = ["0", "90", "180", "270"];
    private static readonly string[] Falloffs = Enum.GetNames<BrushFalloff>();

    private bool _terrainMode, _terrainBlocked, _showGenerate;
    private readonly SculptBrush _brush = new();
    private TerrainActions? _terrainActions;
    private TerrainModeView? _terrainView;
    private Vector3? _terrainHit;
    // terrain.cursor: the pointer pixel for scripted runs (--offscreen has no mouse).
    private Vector2? _terrainCursor;
    // The models of the document's maps (by map number), and those edited since it opened.
    private readonly Dictionary<int, ITerrainModel> _terrainModels = [];
    private readonly HashSet<ITerrainModel> _terrainEdited = [];
    private EditDocument? _terrainDoc;
    private GenerateSettings _generate = new(1, 0.5f, 0f, 2400f);
    // The tile palette's thumbnails, made for one level (released with it).
    private (Level? Of, List<(int Tile, TextureHandle Image, int W, int H)> Tiles) _tileThumbs = (null, []);
    private (HeightGrid? Of, TextureHandle Image) _pendingImage;
    private (Level? Of, LiveGround? Ground) _liveGround;
    private bool _groundStale;

    /// <summary>The terrain model of a shown outdoor level, its steps on the document's history (null: no WLD).</summary>
    private ITerrainModel? NewTerrainModel(Level level, EditDocument doc) =>
        TerrainEdit.Open(_data, level.Number, doc.History) is { } terrain ? new TerrainModeModel(terrain) : null;

    /// <summary>Save: every changed terrain into the mod folder; the paths written.</summary>
    private List<string> SaveTerrains(string folder) =>
        [.. _terrainModels.Values.Select(m => m.Save(folder)).OfType<string>()];

    private TerrainActions TerrainActions => _terrainActions ??= new TerrainActions(() => TerrainView(ModelUse.Create)?.Model);

    private enum ModelUse
    {
        Existing,
        Create,
    }

    /// <summary>The shown level's terrain view: made for a new level (its model kept per map number
    /// while the document is open); null indoors or without a model.</summary>
    private TerrainModeView? TerrainView(ModelUse use)
    {
        if (_level is not { Ground: not null } level || _doc == null)
        {
            return null;
        }

        if (!ReferenceEquals(_terrainDoc, _doc))
        {
            (_terrainDoc, _terrainView) = (_doc, null);
            _terrainModels.Clear();
            _terrainEdited.Clear();
        }

        if (ReferenceEquals(_terrainView?.Level, level))
        {
            return _terrainView;
        }

        if (!_terrainModels.TryGetValue(level.Number, out var model))
        {
            if (use == ModelUse.Existing || NewTerrainModel(level, _doc) is not { } made)
            {
                return null;
            }

            _terrainModels[level.Number] = model = made;
        }

        // A level built again (another instance, an entity edit) bakes the DOS ground: an edited field draws over it.
        _terrainView = new TerrainModeView(_renderer, level, model);
        if (_terrainEdited.Contains(model))
        {
            _terrainView.Activate();
        }

        return _terrainView;
    }

    private void SetTerrainMode(bool on)
    {
        if (!on && TerrainView(ModelUse.Existing) is { } view)
        {
            _brush.End(view.Model);
        }

        (_terrainMode, _terrainHit) = (on, null);
    }

    /// <summary>The brush for this frame; true when the Terrain mode has the left button (no picks, no gizmo).</summary>
    private unsafe bool TerrainInput(float dt)
    {
        var view = TerrainView(_terrainMode ? ModelUse.Create : ModelUse.Existing);
        view?.Update();
        if (view?.Active == true)
        {
            _terrainEdited.Add(view.Model);
        }

        RefreshGround(view);

        if (!_terrainMode)
        {
            return false;
        }

        var buttons = SDL_GetMouseState(null, null);
        var (x, y) = _terrainCursor is { } c ? (c.X, c.Y) : (ViewMouse.X, ViewMouse.Y);
        var left = (buttons & SDL.SDL_MouseButtonFlags.SDL_BUTTON_LMASK) != 0;
        var looking = (buttons & SDL.SDL_MouseButtonFlags.SDL_BUTTON_RMASK) != 0;

        // A press on a panel is the panel's until released (a slider dragged out does not paint).
        _terrainBlocked = left && (_terrainBlocked || (!_viewHovered && !_brush.Stroking));
        if (view == null)
        {
            _terrainHit = null;
            return true;
        }

        var ray = MouseRay(x, y);
        _terrainHit = looking || (!_viewHovered && !_brush.Stroking) ? null : BrushMath.Raycast(view.Model, ray.Origin, ray.Direction);
        var io = ImGui.GetIO();
        _brush.Frame(view.Model, new BrushInput(_terrainHit, left && !_terrainBlocked && !looking, io.KeyShift, io.KeyCtrl), dt);
        return true;
    }

    // Picking, floor snap and drops stand on the edited ground: each change rewrites its corners of the
    // collision field, dab by dab. The weather's cover world is made again once no stroke is on.
    private void RefreshGround(TerrainModeView? view)
    {
        if (view == null)
        {
            return;
        }

        if (ReferenceEquals(_liveGround.Of, view.Level) && _liveGround.Ground is { } ground)
        {
            foreach (var cells in view.Changed)
            {
                ground.Apply(view.Model, cells);
            }
        }

        _groundStale |= view.Rebuilt > 0;
        if (!_groundStale || _brush.Stroking)
        {
            return;
        }

        _groundStale = false;
        _coverOf = null;
    }

    /// <summary>The shown level's ground for picks and collision, the edits in; made on first need
    /// (an edited model's field, else the level's WLD), then kept in step by <see cref="RefreshGround"/>.</summary>
    private HeightField? GroundField
    {
        get
        {
            if (_level is not { } level)
            {
                return null;
            }

            if (!ReferenceEquals(_liveGround.Of, level))
            {
                var edited = _terrainModels.TryGetValue(level.Number, out var model) && _terrainEdited.Contains(model);
                var wld = edited ? model!.Ground() : level.Wld;
                _liveGround = (level, wld != null ? LiveGround.Of(wld) : null);
            }

            return _liveGround.Ground?.Field;
        }
    }

    /// <summary>A map's WLD as edited (the preview plays it); null: as the data has it.</summary>
    private byte[]? EditedWld(int map) => _terrainModels.TryGetValue(map, out var m) ? m.Edited() : null;

    /// <summary>Play-test: the edited terrains into the scratch mod.</summary>
    private void WriteTerrainCopies(string folder)
    {
        foreach (var model in _terrainModels.Values)
        {
            model.WriteCopy(folder);
        }
    }

    private void DrawTerrain()
    {
        if (TerrainView(ModelUse.Existing) is not { } view)
        {
            return;
        }

        view.Draw();
        if (_terrainMode && _terrainHit is { } hit)
        {
            view.DrawRing(hit, _brush.Radius, _brush.Falloff);
        }
    }

    // ---- the toolbar, the keys ----

    private void TerrainToolbar()
    {
        ImGui.Separator();
        var on = _terrainMode;
        if (ImGui.Checkbox("Terrain (Shift+2)", ref on))
        {
            SetTerrainMode(on);
        }
    }

    // Shift+1/2 the mode; in it [ ] the radius. Not while typing or flying.
    private unsafe void TerrainKeys(ImGuiIOPtr io)
    {
        if (!ViewKeys || io.KeyCtrl || (SDL_GetMouseState(null, null) & SDL.SDL_MouseButtonFlags.SDL_BUTTON_RMASK) != 0)
        {
            return;
        }

        if (io.KeyShift && ImGui.IsKeyPressed(ImGuiKey.Key2, false))
        {
            SetTerrainMode(true);
        }
        else if (io.KeyShift && ImGui.IsKeyPressed(ImGuiKey.Key1, false))
        {
            SetTerrainMode(false);
        }
        else if (_terrainMode && ImGui.IsKeyPressed(ImGuiKey.RightBracket))
        {
            _brush.Grow();
        }
        else if (_terrainMode && ImGui.IsKeyPressed(ImGuiKey.LeftBracket))
        {
            _brush.Shrink();
        }
    }

    // ---- the panel ----

    private void TerrainPanel(Vector2 at, Vector2 size)
    {
        if (!_terrainMode)
        {
            return;
        }

        PanelAt(at, size);
        var open = true;
        if (ImGui.Begin("Terrain", ref open))
        {
            if (TerrainView(ModelUse.Create) is not { } view)
            {
                ImGui.TextDisabled(_level is { Ground: not null } ? "No terrain model for this level." : "Show an outdoor level to edit its terrain.");
            }
            else
            {
                TerrainToolRows();
                BrushRows();
                ImGui.Separator();
                TerrainFileRows();
                PendingRows();
                ImGui.Separator();
                ImGui.TextDisabled(string.Create(CultureInfo.InvariantCulture,
                    $"[ ] radius, Shift invert, Ctrl+click pick. Chunks rebuilt: {view.Rebuilt}"));
            }
        }

        ImGui.End();
        if (!open)
        {
            SetTerrainMode(false);
        }

        GenerateWindow();
    }

    private void TerrainToolRows()
    {
        for (var i = 0; i < TerrainTools.Length; i++)
        {
            var tool = TerrainTools[i];
            if (i % 4 != 0)
            {
                ImGui.SameLine();
            }

            if (ImGui.RadioButton(tool.ToString(), _brush.Tool == tool))
            {
                _brush.Tool = tool;
            }
        }
    }

    private void BrushRows()
    {
        var (radius, strength, falloff) = (_brush.Radius, _brush.Strength, (int)_brush.Falloff);
        if (ImGui.SliderFloat("Radius", ref radius, SculptBrush.MinRadius, SculptBrush.MaxRadius, "%.0f", ImGuiSliderFlags.Logarithmic))
        {
            _brush.Radius = radius;
        }

        if (ImGui.SliderFloat("Strength", ref strength, 0f, 1f, "%.2f"))
        {
            _brush.Strength = strength;
        }

        if (ImGui.Combo("Falloff", ref falloff, Falloffs, Falloffs.Length))
        {
            _brush.Falloff = (BrushFalloff)falloff;
        }

        if (_brush.Tool == TerrainTool.Flatten)
        {
            var target = _brush.Target;
            if (ImGui.DragFloat("Height", ref target, 8f, 0f, 0f, "%.0f"))
            {
                _brush.Target = target;
            }
        }

        if (_brush.Tool == TerrainTool.Paint)
        {
            TilePalette();
        }
    }

    // The level's TEXTURE.302 tiles as thumbnails; a click picks the tile, the combo its turn.
    private void TilePalette()
    {
        var turn = _brush.Tile >> 6;
        ImGui.SetNextItemWidth(ImGui.GetFontSize() * 5f);
        if (ImGui.Combo("Turn", ref turn, TileTurns, TileTurns.Length))
        {
            _brush.Tile = (_brush.Tile & 0x3F) | (turn << 6);
        }

        var perRow = Math.Max(1, (int)(ImGui.GetContentRegionAvail().X / (TileThumb + ImGui.GetStyle().ItemSpacing.X)));
        var n = 0;
        foreach (var (tile, image, w, h) in TileThumbs())
        {
            if (n++ % perRow != 0)
            {
                ImGui.SameLine();
            }

            var selected = (_brush.Tile & 0x3F) == tile;
            var at = ImGui.GetCursorScreenPos();
            AssetPreview.Image(image, w, h, new Vector2(TileThumb));
            if (ImGui.IsItemClicked())
            {
                _brush.Tile = (_brush.Tile & 0xC0) | tile;
            }

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(tile.ToString(CultureInfo.InvariantCulture));
            }

            if (selected)
            {
                ImGui.GetWindowDrawList().AddRect(at, at + new Vector2(TileThumb), 0xFF33DDFFu, 0f, ImDrawFlags.None, 2f);
            }
        }
    }

    private List<(int Tile, TextureHandle Image, int W, int H)> TileThumbs()
    {
        if (ReferenceEquals(_tileThumbs.Of, _level))
        {
            return _tileThumbs.Tiles;
        }

        var tiles = new List<(int, TextureHandle, int, int)>();
        var bank = _level?.Textures.Bank(Terrain.TileBank);
        var rgb = _data.LoadPalette()?.Rgb;
        for (var t = 0; bank != null && rgb != null && t < Math.Min(bank.Records.Length, 64); t++)
        {
            var r = bank.Records[t];
            if (r.IsEmpty)
            {
                continue;
            }

            var rgba = new byte[r.Width * r.Height * 4];
            for (var i = 0; i < r.Width * r.Height; i++)
            {
                var index = r.Pixels[i];
                (rgba[i * 4], rgba[i * 4 + 1], rgba[i * 4 + 2], rgba[i * 4 + 3]) = (rgb.Value[index * 3], rgb.Value[index * 3 + 1], rgb.Value[index * 3 + 2], 255);
            }

            // Scoped: released with the level.
            tiles.Add((t, _renderer.CreateRgbaTexture(r.Width, r.Height, rgba), r.Width, r.Height));
        }

        _tileThumbs = (_level, tiles);
        return tiles;
    }

    private void TerrainFileRows()
    {
        if (ImGui.Button("Import heightmap PNG..."))
        {
            AskFile(FileUse.Open, "Import heightmap PNG", p => TerrainActions.ImportPng(p));
        }

        ImGui.SameLine();
        if (ImGui.Button("Export heightmap PNG..."))
        {
            AskFile(FileUse.Save, "Export heightmap PNG", p => Report(TerrainActions.ExportPng(p), $"Heightmap written to {p}"));
        }

        if (ImGui.Button("Import mesh (glTF)..."))
        {
            AskFile(FileUse.Open, "Import mesh as heightmap", p => TerrainActions.ImportMesh(p));
        }

        ImGui.SameLine();
        if (ImGui.Button("Generate..."))
        {
            _showGenerate = true;
        }

        if (TerrainActions.Error is { } error)
        {
            ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), error);
        }
    }

    private enum FileUse
    {
        Open,
        Save,
    }

    private void AskFile(FileUse use, string title, Action<string> picked)
    {
        Action<string?> done = p =>
        {
            if (p != null)
            {
                picked(p);
            }
        };
        if (use == FileUse.Open)
        {
            _dialogs.OpenFile(_window, null, done, title);
            return;
        }

        _dialogs.SaveFile(_window, null, done, title);
    }

    private static void Report(bool done, string line)
    {
        if (done)
        {
            Log.Info("Edit", line);
        }
    }

    // An import or a generated field waiting: its picture, Apply or Discard.
    private void PendingRows()
    {
        if (TerrainActions.Pending is not { } grid)
        {
            return;
        }

        ImGui.Separator();
        ImGui.TextUnformatted($"Preview: {TerrainActions.PendingLabel}");
        var image = PendingImage(grid);
        AssetPreview.Image(image, grid.Size, grid.Size, new Vector2(PreviewSide));
        if (ImGui.Button("Apply"))
        {
            TerrainActions.ApplyPending();
        }

        ImGui.SameLine();
        if (ImGui.Button("Discard"))
        {
            TerrainActions.Discard();
        }
    }

    // The pending field as gray levels, low black, high white.
    private TextureHandle PendingImage(HeightGrid grid)
    {
        if (ReferenceEquals(_pendingImage.Of, grid))
        {
            return _pendingImage.Image;
        }

        if (!_pendingImage.Image.IsNone)
        {
            _renderer.Destroy(_pendingImage.Image);
        }

        var (lo, hi) = (grid.Heights.Min(), grid.Heights.Max());
        var span = MathF.Max(hi - lo, 1f);
        var rgba = new byte[grid.Heights.Length * 4];
        for (var i = 0; i < grid.Heights.Length; i++)
        {
            var g = (byte)Math.Clamp((grid.Heights[i] - lo) / span * 255f, 0f, 255f);
            (rgba[i * 4], rgba[i * 4 + 1], rgba[i * 4 + 2], rgba[i * 4 + 3]) = (g, g, g, 255);
        }

        _pendingImage = (grid, _renderer.CreateRgbaTexture(grid.Size, grid.Size, rgba, scoped: false));
        return _pendingImage.Image;
    }

    private void GenerateWindow()
    {
        if (!_showGenerate)
        {
            return;
        }

        ImGui.SetNextWindowSize(new Vector2(320f, 0f), ImGuiCond.FirstUseEver);
        if (ImGui.Begin("Generate terrain", ref _showGenerate, ImGuiWindowFlags.NoDocking))
        {
            var (seed, rough, lo, hi) = (_generate.Seed, _generate.Roughness, _generate.MinHeight, _generate.MaxHeight);
            ImGui.InputInt("Seed", ref seed);
            ImGui.SliderFloat("Roughness", ref rough, 0f, 1f, "%.2f");
            ImGui.DragFloat("Lowest", ref lo, 8f, 0f, hi, "%.0f");
            ImGui.DragFloat("Highest", ref hi, 8f, lo, 0f, "%.0f");
            _generate = new GenerateSettings(seed, rough, lo, hi);
            if (ImGui.Button("Preview"))
            {
                TerrainActions.Generate(_generate);
            }

            ImGui.SameLine();
            ImGui.TextDisabled("then Apply in the Terrain panel");
        }

        ImGui.End();
    }

    // ---- commands (scripted runs, the console) ----

    private void RegisterTerrainCommands()
    {
        var tools = TerrainTools.ToDictionary(t => t.ToString().ToLowerInvariant(), t => t);
        _commands.Add("terrain.mode", "terrain.mode [on|off]", a =>
        {
            SetTerrainMode(a.Switch(0, _terrainMode));
            return _terrainMode ? "Terrain mode" : "Select mode";
        }, "the Terrain mode on or off (Shift+2 / Shift+1)");
        _commands.Add("terrain.tool", "terrain.tool <raise|lower|smooth|flatten|noise|paint|flip>", a =>
        {
            _brush.Tool = a.Choice(0, tools);
            return $"Tool {_brush.Tool}";
        }, "picks the terrain tool");
        var falloffs = Enum.GetValues<BrushFalloff>().ToDictionary(f => f.ToString().ToLowerInvariant(), f => f);
        _commands.Add("terrain.brush", "terrain.brush <radius> [strength] [constant|linear|smooth|sphere]", a =>
        {
            _brush.Radius = a.Float(0, SculptBrush.MinRadius, SculptBrush.MaxRadius);
            _brush.Strength = a.OptionalFloat(1, 0f, 1f) ?? _brush.Strength;
            _brush.Falloff = a.OptionalWord(2) != null ? a.Choice(2, falloffs) : _brush.Falloff;
            return string.Create(CultureInfo.InvariantCulture, $"Brush {_brush.Radius:0} / {_brush.Strength:0.##} / {_brush.Falloff}");
        }, "sets the brush radius (world units), strength (0..1) and falloff");
        _commands.Add("terrain.cursor", "terrain.cursor <x> <y>", a =>
        {
            _terrainCursor = new Vector2(a.Float(0), a.Float(1));
            return $"Terrain pointer at {_terrainCursor}";
        }, "puts the brush's pointer at a pixel of the view (scripted runs)");
        _commands.Add("terrain.import", "terrain.import <png|gltf>", a =>
        {
            var path = a.Rest(0);
            var ok = path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? TerrainActions.ImportPng(path) : TerrainActions.ImportMesh(path);
            return ok ? $"{path}: preview ready (terrain.apply)" : $"{path}: {TerrainActions.Error}";
        }, "reads a heightmap PNG or a glTF mesh as a pending field");
        _commands.Add("terrain.export", "terrain.export <png>", a =>
            TerrainActions.ExportPng(a.Rest(0)) ? $"Heightmap written to {a.Rest(0)}" : $"{a.Rest(0)}: {TerrainActions.Error}",
            "writes the shown terrain's heights as a grayscale PNG");
        _commands.Add("terrain.generate", "terrain.generate <seed> [roughness] [lowest] [highest]", a =>
        {
            _generate = new GenerateSettings(a.Int(0), a.OptionalFloat(1, 0f, 1f) ?? _generate.Roughness,
                a.OptionalFloat(2) ?? _generate.MinHeight, a.OptionalFloat(3) ?? _generate.MaxHeight);
            return TerrainActions.Generate(_generate) ? "Generated: preview ready (terrain.apply)" : $"Generate: {TerrainActions.Error}";
        }, "generates a pending field");
        _commands.Add("terrain.apply", "terrain.apply", _ => TerrainActions.ApplyPending() ? "Applied" : "Nothing pending",
            "puts the pending field on the terrain (one undo step)");
    }
}
