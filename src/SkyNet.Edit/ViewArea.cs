using System.Numerics;
using RetroEng.Editor;
using RetroEng.Platform;

namespace SkyNet.Edit;

/// <summary>
/// The 3D view's place: the Viewport window's picture in ImGui's coordinates (points; the desktop's once
/// panels have windows of their own) and its pixels per point. Picks, the gizmo, drops, the brush and
/// pastes take a point of the view in its own coordinates, origin at its top-left.
/// <code>
///   ImGui point ──(- Min)──► view point (0..Size) ──Ndc──► PickRay through the camera at Aspect
///   view (points) ──(× Scale)──► the view target's pixels
/// </code>
/// </summary>
public readonly record struct ViewArea(Vector2 Min, Vector2 Size, float Scale = 1f)
{
    public float Aspect => Size.X / MathF.Max(Size.Y, 1f);

    /// <summary>The view target's size: the area in pixels, at least one.</summary>
    public Resolution Pixels => new(Math.Max(1, (int)MathF.Round(Size.X * Scale)), Math.Max(1, (int)MathF.Round(Size.Y * Scale)));

    /// <summary>An ImGui point in the view's coordinates.</summary>
    public Vector2 Local(Vector2 point) => point - Min;

    public bool Contains(Vector2 point) =>
        point.X >= Min.X && point.Y >= Min.Y && point.X < Min.X + Size.X && point.Y < Min.Y + Size.Y;

    /// <summary>The ray through a view point (<see cref="Local"/>) of a camera looking by <paramref name="view"/>.</summary>
    public PickRay Ray(Matrix4x4 view, float fieldOfView, Vector2 local) =>
        PickRay.FromView(view, fieldOfView, Aspect, PickRay.Ndc(local.X, local.Y, Size.X, Size.Y));
}
