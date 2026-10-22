using System.Numerics;
using Hexa.NET.ImGui;
using RetroEng.Editor;
using RetroEng.Platform;
using RetroEng.Render;

namespace SkyNet.Edit;

/// <summary>
/// An asset viewer's 3D picture (the Model Viewer, the Effects window): a view target of its own, shown as an
/// image in its window wherever that window is (docked, floated, another monitor), like the Viewport.
/// The target draws its own content (RetroEng 0.25): only what is drawn between <see cref="Begin"/> and
/// <see cref="End"/>, lit by its own scene (<see cref="ViewerStudio"/>), not the level.
/// <code>
///   window ─► Picture: image of last frame's target, a button over it (orbit) ─► pixels, aspect
///   Render (before Present) ─► Begin(orbit camera, backdrop, scene) ─► subject's draws ─► End
/// </code>
/// </summary>
internal sealed class ViewerTarget(Renderer renderer)
{
    private ViewTarget? _target;
    // The target was drawn (its picture is current); the area's pixels as of the last window frame.
    private bool _drawn;
    private Resolution _pixels;

    /// <summary>Whether the window showed the picture since the last draw (Draw draws only then).</summary>
    public bool Shown { get; private set; }

    /// <summary>The picture's width over its height.</summary>
    public float Aspect => _pixels.Width / (float)Math.Max(_pixels.Height, 1);

    /// <summary>The picture over <paramref name="size"/> at the cursor and an invisible button <paramref name="id"/>
    /// over it (its item for the caller's mouse).</summary>
    public unsafe void Picture(string id, Vector2 size)
    {
        var at = ImGui.GetCursorScreenPos();
        if (_drawn && _target != null)
        {
            var picture = new ImTextureRef(null, new ImTextureID(ImGuiHost.TextureId(_target.Texture)));
            ImGui.GetWindowDrawList().AddImage(picture, at, at + size);
        }

        ImGui.InvisibleButton(id, size);
        var scale = ImGui.GetWindowViewport().FramebufferScale.X;
        _pixels = new ViewArea(at, size, scale > 0f ? scale : 1f).Pixels;
        Shown = true;
    }

    /// <summary>Opens the target's own content: the draws until <see cref="End"/> go into it through
    /// <paramref name="view"/> over <paramref name="backdrop"/>, lit by <paramref name="scene"/>.
    /// False (nothing opened) while the window did not show the picture.</summary>
    public bool Begin(View view, Vector4 backdrop, TargetScene scene)
    {
        if (!Shown)
        {
            return false;
        }

        _target ??= renderer.CreateViewTarget(_pixels);
        _target.Size = _pixels;
        renderer.BeginTarget(_target, view, backdrop, scene);
        (_drawn, Shown) = (true, false);
        return true;
    }

    /// <summary>Closes the content <see cref="Begin"/> opened: later draws go to the frame's scene.</summary>
    public void End() => renderer.EndTarget();

    /// <summary>The camera at <paramref name="eye"/> looking at <paramref name="centre"/>, its depth from
    /// <paramref name="near"/> to <paramref name="far"/>.</summary>
    public View Camera(Vector3 eye, Vector3 centre, float fov, float near, float far)
    {
        var look = Matrix4x4.CreateLookAt(eye, centre, Vector3.UnitY);
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(fov, Aspect, near, far);
        if (renderer.ReversedZ)
        {
            // Depth 1 at the near plane, 0 at the far one (near and far swapped in the depth row).
            (projection.M33, projection.M43) = (near / (far - near), near * far / (far - near));
        }

        Matrix4x4.Invert((look with { M41 = 0f, M42 = 0f, M43 = 0f }) * projection, out var clipToDirection);
        // The lens: the target's own point lights (ViewerStudio) are binned through it.
        return new View(look * projection, clipToDirection, eye) { Lens = new Lens(look, fov, Aspect, near, far) };
    }

    /// <summary>Frees the target (the viewer is made again).</summary>
    public void Release()
    {
        if (_target != null)
        {
            renderer.Destroy(_target);
        }

        (_target, _drawn, Shown) = (null, false, false);
    }
}
