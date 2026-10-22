using System.Drawing;
using System.Numerics;
using RetroEng.Render;
using SkyNet.Formats;
using SkyNet.Game.Collision;
using SkyNet.Game.Menu;

namespace SkyNet.Game.Automap;

/// <summary>
/// Draws the automap as SKYNET.EXE v1.01 does (FUN_0013dc00): first the 2D art (0x13df3c),
/// MAPGRID.IMG between MAPBAR1.IMG (the buttons, the hovered one lit from MAPBTN.CFA) and
/// MAPBAR2.IMG; then the 3D over it in MAPGRID's window (0x14fbd0: half 160 × 83, centre (160, 100)),
/// no haze: outdoors
/// the whole terrain, the seen meshes of the 5 × 5 cells round the kept eye (<see cref="AutomapIndex.Shown"/>)
/// where they stand now, and ARROW.3D 40 below the kept eye, turned the soldier's way.
/// <code>
///   y 0   ┌ MAPBAR1.IMG  ↺ ↻ ↑ ↓ + − EXIT ┐
///   y 17  │ MAPGRID.IMG  + the 3D scene    │
///   y 183 └ MAPBAR2.IMG                    ┘
/// </code>
/// The meshes are the level's textures and palette, placed and lit by DOS's extra light
/// (<see cref="AutomapLight"/>), made once per level; at RENDER DETAIL LOW / MED a textured face
/// takes its texture's one DOS colour.
/// </summary>
public sealed class AutomapView(Renderer renderer, GameData data)
{
    private const string Grid = "MAPGRID.IMG";
    private const string TopBar = "MAPBAR1.IMG";
    private const string BottomBar = "MAPBAR2.IMG";
    private const string Lit = "MAPBTN.CFA";
    private const string ArrowModel = "ARROW.3D";
    /// <summary>The game's palette: the automap is drawn in game, not in the menus' MENU.COL.</summary>
    private const string GamePalette = "SKYNET.COL";
    private const int GridTop = 17;
    private const int BottomTop = 183;

    /// <summary>The arrow hangs this far under the kept eye (DOS +0x28, Y down).</summary>
    private const float ArrowDrop = 40f;
    /// <summary>The 3D window: MAPGRID.IMG's place, between the bars.</summary>
    public static readonly Rectangle Window = new(0, GridTop, MenuFrame.ScreenWidth, BottomTop - GridTop);
    /// <summary>DOS's automap far clip (0x6e224 = 0xC0000).</summary>
    private const float Far = 0xC0000;
    private const float Near = 1f;

    private readonly Dictionary<int, Placed?> _placed = [];
    private (Matrix4x4 Place, Placed Mesh)? _arrow;
    private Level? _level;

    /// <summary>The 2D art, on the menus' 320 × 200 frame.</summary>
    public static void DrawArt(MenuPainter p, AutomapScreen screen)
    {
        p.FillWindow(MenuPainter.Black);
        p.Image(Grid, GamePalette, 0, GridTop);
        p.Image(TopBar, GamePalette, 0, 0);
        p.Image(BottomBar, GamePalette, 0, BottomTop);
        if (screen.Hovered != AutomapButton.None)
        {
            p.Cel(Lit, (int)screen.Hovered, Point.Empty, GamePalette);
        }
    }

    /// <summary>The 3D scene into <paramref name="area"/> (canvas units: <see cref="Window"/>).</summary>
    public void DrawScene(Level level, AutomapScreen screen, AutomapIndex index, AutomapSeen seen, RectangleF area)
    {
        if (area.Width <= 0f || area.Height <= 0f)
        {
            return;
        }

        if (!ReferenceEquals(level, _level))
        {
            _placed.Clear();
            _arrow = null;
            _level = level;
        }

        var camera = screen.Camera;
        var eye = camera.Eye;
        var (yaw, pitch) = camera.Look;
        var view = Matrix4x4.CreateLookAt(eye, eye + Player.PlayerController.Direction(yaw, pitch), Vector3.UnitY);
        // DOS's focal over the window's half height (its pixels 1.2 tall).
        var fov = 2f * MathF.Atan(Window.Height / 2f * Level.DosPixelAspect / Level.DosFocal);
        var aspect = area.Width / area.Height;
        var projection = renderer.ReversedZ
            ? Projection.ReversedInfinite(fov, aspect, Near)
            : Matrix4x4.CreatePerspectiveFieldOfView(fov, aspect, Near, Far);

        var rotation = view with { M41 = 0f, M42 = 0f, M43 = 0f };
        Matrix4x4.Invert(rotation * projection, out var clipToDirection);

        renderer.BeginInset(new View(view * projection, clipToDirection, eye), area, InsetContent.Own, InsetLayer.OverCanvas);
        // No haze in the automap (DOS installs a no-op cue, 0x14a63c(-1)).
        if (level.Outdoor && level.Ground is { } ground)
        {
            ground.Draw(renderer, eye, Far, ground.Material with { Unfogged = true });
        }

        var style = AutomapLight.StyleOf(level.Detail);
        foreach (var e in index.Shown(seen, camera.Target))
        {
            DrawRecord(level, e.Offset, style);
        }

        DrawArrow(level, camera);
        renderer.EndInset();
    }

    /// <summary>A seen record where it stands now (a door open, a lift up); none once destroyed.</summary>
    private void DrawRecord(Level level, int offset, AutomapStyle style)
    {
        if (PlacedOf(level, offset) is not { } placed)
        {
            return;
        }

        // The mesh is placed and lit as the map put it; a mover's pose moves it from there.
        var world = Matrix4x4.Identity;
        if (level.Poses?.TryGetPose(offset, out var pose) == true)
        {
            if (!pose.Visible)
            {
                return;
            }

            world = pose.Delta;
        }

        Draw(level, placed, world, style);
    }

    /// <summary>ARROW.3D under the kept eye, its nose the soldier's way (flat green: it is untextured).</summary>
    private void DrawArrow(Level level, AutomapCamera camera)
    {
        var at = camera.Target - Vector3.UnitY * ArrowDrop;
        var place = Matrix4x4.CreateRotationY(camera.Heading) * Matrix4x4.CreateTranslation(at);
        if (_arrow is not { } arrow || arrow.Place != place || !renderer.Exists(arrow.Mesh.Mesh.Mesh))
        {
            _arrow = data.LoadModel(ArrowModel) is { } model && Build(level, model, place) is { } built ? (place, built) : null;
        }

        if (_arrow is { } drawn)
        {
            Draw(level, drawn.Mesh, Matrix4x4.Identity, AutomapStyle.Textured);
        }
    }

    /// <summary>Queues a placed mesh out of the haze; flat parts take their texture's DOS colour.</summary>
    private void Draw(Level level, Placed placed, in Matrix4x4 world, AutomapStyle style)
    {
        foreach (var part in placed.Mesh.Parts)
        {
            var material = part.Material;
            if (style == AutomapStyle.Flat && part.Texture is { } t && placed.Colours.TryGetValue(t.Handle, out var colour))
            {
                material = Material.FlatIndex(colour, level.Palette, RenderState.Solid) with { Shade = 0, VertexShade = true };
            }

            placed.Mesh.Draw(renderer, part with { Material = material with { Unfogged = true } }, 0, world);
        }
    }

    private static string? NameOf(MapFile map, MapEntity e) =>
        e.NameIndex >= 0 && e.NameIndex < map.Names.Length && map.Names[e.NameIndex] is { Length: > 0 } name ? name + ".3D" : null;

    /// <summary>A record's mesh placed in the world and lit by <see cref="AutomapLight"/>; built
    /// once per level (null: no model).</summary>
    private Placed? PlacedOf(Level level, int offset)
    {
        if (_placed.TryGetValue(offset, out var placed) && (placed == null || renderer.Exists(placed.Mesh.Mesh)))
        {
            return placed;
        }

        placed = level.Map.TryGetEntityAt(offset, out var e) && NameOf(level.Map, e) is { } file && data.LoadModel(file) is { } model
            ? Build(level, model, LevelCollision.Placement(e))
            : null;
        _placed[offset] = placed;
        return placed;
    }

    /// <summary>A model placed by <paramref name="place"/>, each vertex lit to a shade row, with
    /// its textures' flat colours (TEXTURE.NNN record byte +1) by texture handle.</summary>
    private Placed? Build(Level level, Model3D model, in Matrix4x4 place)
    {
        var batch = new StaticBatch();
        batch.Add(ModelMesh.Surfaces(model, level.Textures), place, (_, normal) => AutomapLight.Shade(normal));
        if (batch.Build(renderer, level.Palette, RenderState.Solid, vertexShade: true) is not { } mesh)
        {
            return null;
        }

        var colours = new Dictionary<TextureHandle, int>();
        foreach (var word in model.Faces.Select(f => f.Texture).Where(w => w > 0xFF).Distinct())
        {
            if (level.Textures.Get(word >> 7, word & 0x7F, opaque: true) is { } t && level.Textures.Record(word >> 7, word & 0x7F) is { } r)
            {
                colours[t.Handle] = r.Colour;
            }
        }

        return new Placed(mesh, colours);
    }

    /// <summary>A placed, lit mesh and its parts' flat colours by texture handle.</summary>
    private sealed record Placed(ModelMesh Mesh, Dictionary<TextureHandle, int> Colours);
}
