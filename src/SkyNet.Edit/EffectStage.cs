using System.Numerics;
using Hexa.NET.ImGui;
using RetroEng.Json;
using RetroEng.Render;
using SkyNet.Data;
using SkyNet.Game.Enhanced;

namespace SkyNet.Edit;

/// <summary>
/// The Effects window's preview (editor-tools plan, T5): one effect run by the game's own effect code
/// (<see cref="EffectPreview"/>) with the window's row, at <see cref="ViewerStudio.Subject"/> on a floor, in the
/// window's own view target and scene (<see cref="ViewerStudio"/>: studio light, the effect's own lights) over a
/// dark backdrop. The camera orbits the effect as the Model Viewer's does.
/// <code>
///   EffectsWindow.Step ─► Show(row) / Advance(dt) ─► EffectPreview.Spawn / Advance
///   EffectsWindow ─► Picture(size) ─► EditorApp.Draw ─► Render ─► Begin(orbit camera, studio scene)
///                                                        ─► floor, EffectPreview.Draw(row) ─► End
/// </code></summary>
public sealed class EffectStage(Renderer renderer) : IEffectStage
{
    private const float Fov = MathF.PI / 4f;
    private const float OrbitSpeed = 0.01f, ZoomStep = 0.88f, MinZoom = 0.25f, MaxZoom = 6f;
    private const float StartYaw = 0.6f, StartPitch = 0.15f, MaxPitch = 1.5f;
    // The effect's height (× its scale: a fire's flames, a blast's cloud) and the share of the area it fills.
    private const float Height = 1.8f, Fill = 0.8f;
    // The effect's centre above its spawn point (× its scale): a fire's foot is its bottom.
    private const float Centre = 0.7f;
    private static readonly Vector4 Backdrop = new(0.16f, 0.17f, 0.19f, 1f);
    // The floor under the effect (× its scale: wide enough to catch a fire's light) and its colour (linear).
    private const float FloorReach = 6f;
    private static readonly Vector4 FloorColour = new(0.22f, 0.22f, 0.23f, 1f);
    private static readonly ushort[] FloorIndices = [0, 1, 2, 0, 2, 3];

    // The game's preview of one effect (spawned at the viewers' place), the shown effect's key, the last row that reads.
    private readonly EffectPreview _preview = new();
    // The window's 3D picture.
    private readonly ViewerTarget _view = new(renderer);
    private MeshHandle _floor;
    private string? _key;
    private EffectRow? _row;
    // The camera about the effect's centre: turned, raised, and how far (× the distance that fits it in the area).
    private float _yaw = StartYaw, _pitch = StartPitch, _zoom = 1f;

    /// <summary>Whether the last row handed over reads (false: the preview keeps the one before).</summary>
    public bool RowReads { get; private set; } = true;

    /// <summary>Where the shown effect was spawned.</summary>
    public Vector3 SubjectAt => _preview.At;

    /// <summary>What the 3D area says over the preview: how to drive it, or why it shows nothing.</summary>
    public string Hint => renderer.Lighting == null ? "the effects are the ENHANCED look's: switch the view's look"
        : !RowReads ? "a value the game would not read: the preview keeps the last that reads"
        : "drag: turn   wheel: zoom   double-click: reset";

    public void Show(string key, IReadOnlyList<GridColumn> columns, object?[] row)
    {
        if (_key != key)
        {
            (_key, _row) = (key, null);
            _preview.Spawn(key, ViewerStudio.Subject);
        }

        var read = RowOf(columns, row);
        RowReads = read != null;
        _row = read ?? _row;
    }

    public void Trigger()
    {
        if (_key != null)
        {
            _preview.Spawn(_key, ViewerStudio.Subject);
        }
    }

    public void Advance(float seconds) => _preview.Advance(seconds);

    /// <summary>The 3D area at the cursor, <paramref name="size"/> points: the picture, turned and zoomed by the mouse.</summary>
    public void Picture(Vector2 size)
    {
        _view.Picture("##effectView", size);
        Orbit();
    }

    // The 3D area's mouse (right after its item): drag turns, the wheel zooms, a double-click resets.
    private void Orbit()
    {
        if (ImGui.IsItemActive() && ImGui.IsMouseDragging(ImGuiMouseButton.Left, 0f))
        {
            var d = ImGui.GetIO().MouseDelta;
            _yaw -= d.X * OrbitSpeed;
            _pitch = Math.Clamp(_pitch + d.Y * OrbitSpeed, -MaxPitch, MaxPitch);
        }

        if (!ImGui.IsItemHovered())
        {
            return;
        }

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

    /// <summary>Queues the window's view (before Present) over the backdrop: the floor and the effect, in the
    /// target's own scene; no effect in the original look.</summary>
    public void Render()
    {
        if (!_view.Shown || _key == null || _row is not { } row)
        {
            return;
        }

        // The distance at which the effect's height fills the area, the camera orbiting its centre.
        var size = Height * _preview.Scale;
        var distance = size / (2f * MathF.Tan(Fov * 0.5f) * Fill) * _zoom;
        var centre = ViewerStudio.Subject + Vector3.UnitY * (Centre * _preview.Scale);
        var eye = centre + distance * new Vector3(MathF.Sin(_yaw) * MathF.Cos(_pitch), MathF.Sin(_pitch), -MathF.Cos(_yaw) * MathF.Cos(_pitch));
        var near = MathF.Max(1f, distance - size * 2f);
        var far = distance + MathF.Max(size * 4f, _preview.Scale * FloorReach * 2f);
        var lights = ViewerStudio.EffectLights(_preview.Effect, _preview.Scale, _preview.Age);
        var scene = ViewerStudio.Scene(renderer.Lighting, far, lights);
        if (!_view.Begin(_view.Camera(eye, centre, Fov, near, far), Backdrop, scene))
        {
            return;
        }

        DrawFloor(scene.Lighting);
        _preview.Draw(renderer, row, eye, lighting: scene.Lighting);
        _view.End();
    }

    // A lit square under the effect (the enhanced look only): its lights fall on it.
    private void DrawFloor(Lighting? lighting)
    {
        if (lighting == null)
        {
            return;
        }

        if (!renderer.Exists(_floor))
        {
            var up = Vertex.PackNormal(Vector3.UnitY);
            _floor = renderer.CreateMesh(
            [
                new Vertex(new Vector3(-1f, 0f, -1f), Vector2.Zero, Vertex.White, up),
                new Vertex(new Vector3(1f, 0f, -1f), Vector2.UnitX, Vertex.White, up),
                new Vertex(new Vector3(1f, 0f, 1f), Vector2.One, Vertex.White, up),
                new Vertex(new Vector3(-1f, 0f, 1f), Vector2.UnitY, Vertex.White, up),
            ], FloorIndices);
        }

        var material = Material.Flat(FloorColour, new RenderState(Queues.Opaque, Cull.None)) with { Shading = Shading.Lit };
        var world = Matrix4x4.CreateScale(_preview.Scale * FloorReach) * Matrix4x4.CreateTranslation(ViewerStudio.Subject);
        renderer.Draw(_floor, 0, FloorIndices.Length, material, 0, world);
    }

    /// <summary>The game's row of a grid row's cells, read as the game reads the file; null when it would not read.</summary>
    public static EffectRow? RowOf(IReadOnlyList<GridColumn> columns, object?[] cells)
    {
        var item = JsonItem.NewObject();
        for (var c = 0; c < columns.Count; c++)
        {
            item.Add(columns[c].Key, TableGrid.JsonOf(cells[c]));
        }

        var file = JsonItem.NewObject().Add("rows", JsonItem.NewArray([item], i => i));
        try
        {
            return TableSchemas.Effects.Apply(EffectTable.Base with { Rows = [] }, file).Rows.Single();
        }
        catch (Exception e) when (e is InvalidDataException or JsonDataException)
        {
            return null;
        }
    }
}
