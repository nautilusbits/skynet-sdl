using System.Drawing;
using System.Numerics;
using RetroEng.Render;
using SkyNet.Formats;
using SkyNet.Game.Enemies;
using SkyNet.Game.Net;

namespace SkyNet.Game.Menu;

/// <summary>
/// The host screen's turning avatar (NETMENU1's left box: FUN_00128b3b draws the chosen avatar's idle
/// body and head turning through 0x134200 / 0x1348e4; the Godot port's menu.gd did the same in a
/// SubViewport): the idle models in the avatar's skin, drawn as the arena draws avatars (the game
/// palette, unshaded) into an inset over the menu's canvas.
/// The meshes are built on first use and again when a renderer scope released them (a level's end).
/// </summary>
public sealed class AvatarPreview(Renderer renderer, GameData data)
{
    /// <summary>Turns per second (radians) and idle frames per second.</summary>
    public const float TurnRate = 1.2f, IdleRate = 6f;

    private readonly EnemyModels _models = new(data);
    private readonly Dictionary<(string File, int Frame, int Skin), ModelMesh?> _meshes = [];
    private TextureStore? _textures;
    private TextureHandle _palette;

    /// <summary>Queues avatar <paramref name="number"/> into <paramref name="area"/> (canvas units),
    /// turned <paramref name="seconds"/> × <see cref="TurnRate"/>.</summary>
    public void Draw(int number, RectangleF area, double seconds)
    {
        if (area.Width <= 0f || area.Height <= 0f || !Ready())
        {
            return;
        }

        var look = DmAvatarTable.Get(number);
        var body = _models.Get(look.IdleBody) ?? _models.Get(look.Body);
        if (body == null)
        {
            return;
        }

        var head = _models.Get(body.File == look.IdleBody ? look.IdleHead : look.Head);
        var frame = (int)(seconds * IdleRate) % Math.Max(1, body.Frames);

        // The camera: level with the chest, far enough back that the whole body fits the box's height.
        var b = body.Bounds;
        var height = b.Max.Y - b.Min.Y;
        var fov = MathF.PI / 6f;
        var distance = height * 0.54f / MathF.Tan(fov * 0.5f);
        var target = new Vector3(0f, height * 0.5f, 0f);
        var eye = target + new Vector3(0f, height * 0.08f, distance);
        var view = Matrix4x4.CreateLookAt(eye, target, Vector3.UnitY);
        var aspect = area.Width / area.Height;
        var projection = renderer.ReversedZ
            ? Projection.ReversedInfinite(fov, aspect, 4f)
            : Matrix4x4.CreatePerspectiveFieldOfView(fov, aspect, 4f, distance * 4f);
        var rotation = view with { M41 = 0f, M42 = 0f, M43 = 0f };
        Matrix4x4.Invert(rotation * projection, out var clipToDirection);

        var centre = (b.Min.X + b.Max.X) * 0.5f;
        var middle = (b.Min.Z + b.Max.Z) * 0.5f;
        var world = Matrix4x4.CreateTranslation(-centre, -b.Min.Y, -middle) * Matrix4x4.CreateRotationY((float)(seconds * TurnRate % (2 * Math.PI)));
        renderer.BeginInset(new View(view * projection, clipToDirection, eye), area, InsetContent.Own, InsetLayer.OverCanvas);
        var now = (long)(seconds * 1000);
        Draw(body, frame, look, world, now);
        if (head != null && frame < head.Frames)
        {
            Draw(head, frame, look, world, now);
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

        var rgba = new byte[256 * 4];
        for (var i = 0; i < 256; i++)
        {
            var (r, g, b) = palette[i];
            (rgba[i * 4], rgba[i * 4 + 1], rgba[i * 4 + 2], rgba[i * 4 + 3]) = (r, g, b, 255);
        }

        _palette = renderer.CreatePalette(rgba);
        _textures = new TextureStore(renderer, data);
        _meshes.Clear();
        return true;
    }

    private void Draw(EnemyModelInfo model, int frame, DmAvatarLook look, in Matrix4x4 world, long nowMs)
    {
        var key = (model.File, frame, look.Skin);
        if (!_meshes.TryGetValue(key, out var mesh) || mesh != null && !renderer.Exists(mesh.Mesh))
        {
            var batch = new StaticBatch();
            batch.Add(ModelMesh.Surfaces(model.Model, _textures!, Math.Clamp(frame, 0, model.Frames - 1), smooth: true, skin: look.SkinRemap),
                Matrix4x4.Identity);
            _meshes[key] = mesh = batch.Build(renderer, _palette, RenderState.Solid);
        }

        if (mesh == null)
        {
            return;
        }

        foreach (var p in mesh.Parts)
        {
            renderer.Draw(mesh.Mesh, p.First, p.Count, p.Material, p.Texture?.FrameAt(nowMs) ?? 0, world);
        }
    }
}
