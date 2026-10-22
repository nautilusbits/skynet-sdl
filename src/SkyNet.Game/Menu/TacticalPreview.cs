using System.Drawing;
using System.Numerics;
using RetroEng.Physics;
using RetroEng.Render;
using SkyNet.Formats;

namespace SkyNet.Game.Menu;

/// <summary>
/// The TACTICAL tab's spinning unit (FUN_0012cbc0, mode DAT_0004f833 = 1): the dossier's
/// &lt;name&gt;.3D from MDMDOBJS.BSA turning on the spot over TACTBAK.IMG, drawn as the Godot port's
/// tactical_view.gd did (the game palette, the camera a little above, the whole model in view).
/// The meshes are built on first use and again when a renderer scope released them.
/// </summary>
public sealed class TacticalPreview(Renderer renderer, GameData data)
{
    /// <summary>DOS turns _DAT_0004f864 by 0xC0 of 2048 units a second (≈ 34°/s).</summary>
    public const float TurnRate = 2f * MathF.PI * 0xC0 / 2048f;

    private const float Fov = MathF.PI / 4f;
    private const float FitMargin = 0.95f;
    private const float EyeLift = 0.3f;

    private readonly Dictionary<string, (ModelMesh? Mesh, Vector3 Centre, float Radius)> _meshes = new(StringComparer.OrdinalIgnoreCase);
    private TextureStore? _textures;
    private TextureHandle _palette;

    /// <summary>A dossier's model ("tachkftr" → TACHKFTR.3D); null when missing.</summary>
    public static Model3D? Load(GameData data, string name) => data.LoadModel($"{name.ToUpperInvariant()}.3D");

    /// <summary>Queues dossier <paramref name="name"/> into <paramref name="area"/> (canvas units),
    /// turned <paramref name="seconds"/> × <see cref="TurnRate"/>.</summary>
    public void Draw(string name, RectangleF area, double seconds)
    {
        if (area.Width <= 0f || area.Height <= 0f || !Ready() || Mesh(name) is not { Mesh: { } mesh } entry)
        {
            return;
        }

        // The camera: back far enough that the bounding sphere fits, a little above the middle.
        var distance = entry.Radius / MathF.Tan(Fov * 0.5f) * FitMargin;
        var eye = new Vector3(0f, entry.Radius * EyeLift, distance);
        var view = Matrix4x4.CreateLookAt(eye, Vector3.Zero, Vector3.UnitY);
        var aspect = area.Width / area.Height;
        var near = MathF.Max(1f, distance - entry.Radius * 2f);
        var projection = renderer.ReversedZ
            ? Projection.ReversedInfinite(Fov, aspect, near)
            : Matrix4x4.CreatePerspectiveFieldOfView(Fov, aspect, near, distance + entry.Radius * 2f);
        var rotation = view with { M41 = 0f, M42 = 0f, M43 = 0f };
        Matrix4x4.Invert(rotation * projection, out var clipToDirection);

        // Turned about its own centre, not a corner of its box.
        var turn = (float)(seconds * TurnRate % (2 * Math.PI));
        var world = Matrix4x4.CreateTranslation(-entry.Centre) * Matrix4x4.CreateRotationY(turn);
        var now = (long)(seconds * 1000);
        renderer.BeginInset(new View(view * projection, clipToDirection, eye), area, InsetContent.Own, InsetLayer.OverCanvas);
        foreach (var p in mesh.Parts)
        {
            renderer.Draw(mesh.Mesh, p.First, p.Count, p.Material, p.Texture?.FrameAt(now) ?? 0, world);
        }

        renderer.EndInset();
    }

    /// <summary>The palette and the textures, made again when a scope released them.</summary>
    private bool Ready()
    {
        if (_textures != null && renderer.Exists(_palette))
        {
            return true;
        }

        if (data.LoadPalette() is not { } palette)
        {
            return false;
        }

        var rgba = new byte[Palette.Colors * 4];
        for (var i = 0; i < Palette.Colors; i++)
        {
            var (r, g, b) = palette[i];
            (rgba[i * 4], rgba[i * 4 + 1], rgba[i * 4 + 2], rgba[i * 4 + 3]) = (r, g, b, 255);
        }

        _palette = renderer.CreatePalette(rgba);
        _textures = new TextureStore(renderer, data);
        _meshes.Clear();
        return true;
    }

    /// <summary>The model's mesh, its box centre and bounding radius; built once per name.</summary>
    private (ModelMesh? Mesh, Vector3 Centre, float Radius)? Mesh(string name)
    {
        if (_meshes.TryGetValue(name, out var entry) && (entry.Mesh == null || renderer.Exists(entry.Mesh.Mesh)))
        {
            return entry;
        }

        if (Load(data, name) is not { Faces.Length: > 0 } model)
        {
            _meshes[name] = (null, Vector3.Zero, 0f);
            return null;
        }

        var box = Box.Empty;
        foreach (var v in model.Vertices)
        {
            box = box.Include(v.ToYUp());
        }

        var batch = new StaticBatch();
        batch.Add(ModelMesh.Surfaces(model, _textures!), Matrix4x4.Identity);
        var mesh = batch.Build(renderer, _palette, RenderState.Solid);
        entry = (mesh, (box.Min + box.Max) * 0.5f, MathF.Max(1f, (box.Max - box.Min).Length() * 0.5f));
        _meshes[name] = entry;
        return entry;
    }
}
