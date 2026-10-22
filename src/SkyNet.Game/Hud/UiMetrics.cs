using RetroEng.Render;

namespace SkyNet.Game.Hud;

/// <summary>DISPLAY's UI SCALE: AUTO keeps text readable on small, dense screens (phones), 1X draws it
/// as the resolution gives, 1.5X and 2X raise AUTO's minimum (on a desktop too).</summary>
public enum UiScale { Auto, X1, X1_5, X2 }

/// <summary>
/// Sizes that follow the screen's physical size, not only its pixels: text keeps at least
/// <see cref="MinTextMm"/> a line where the screen's size is known (a phone), and the view's
/// field of view below 4:3. Plain maths: tests run it for any screen.
/// </summary>
public static class UiMetrics
{
    /// <summary>The shortest text line AUTO allows, in millimetres.</summary>
    private const float MinTextMm = 2.5f;

    /// <summary>Densities believed (60-800 dpi): an OEM's wrong one stays sane.</summary>
    private const float MinPxPerMm = 60f / 25.4f, MaxPxPerMm = 800f / 25.4f;

    /// <summary>A 96 dpi desktop: what 1.5X and 2X assume where the size is unknown.</summary>
    private const float DesktopPxPerMm = 96f / 25.4f;

    /// <summary>The 4:3 frame the field of view is set for; narrower frames widen it down.</summary>
    private const float BaseAspect = 4f / 3f;

    /// <summary>Screen pixels per millimetre the UI is sized for: the screen's own (<paramref name="widthMm"/>,
    /// 0 when unknown) times the mode's factor; 0 for no minimum (1X, or AUTO on an unknown screen).</summary>
    public static float PxPerMm(int widthPx, float widthMm, UiScale mode)
    {
        if (mode == UiScale.X1)
        {
            return 0f;
        }

        var known = widthMm > 0f && widthPx > 0;
        if (!known && mode == UiScale.Auto)
        {
            return 0f;
        }

        var measured = known ? Math.Clamp(widthPx / widthMm, MinPxPerMm, MaxPxPerMm) : DesktopPxPerMm;
        return measured * mode switch { UiScale.X1_5 => 1.5f, UiScale.X2 => 2f, _ => 1f };
    }

    /// <summary>Screen pixels per font pixel: <paramref name="baseScale"/>, or more so a
    /// <paramref name="linePx"/> font line is at least <see cref="MinTextMm"/> tall.</summary>
    public static int TextPixel(int baseScale, int linePx, float pxPerMm)
    {
        if (pxPerMm <= 0f || linePx <= 0)
        {
            return baseScale;
        }

        return Math.Max(baseScale, (int)MathF.Ceiling(MinTextMm * pxPerMm / linePx));
    }

    /// <summary>The console's (and FPS readout's) font pixel in canvas units: whole screen pixels, one
    /// up to 1080 lines, two to 1800..., raised by <see cref="TextPixel"/>.</summary>
    public static float ConsolePixel(int height, int linePx, float pxPerMm)
    {
        var pixels = Math.Max(1, (int)MathF.Round(height / 720f - 0.01f));
        return TextPixel(pixels, linePx, pxPerMm) * Renderer.CanvasHeight / Math.Max(height, 1);
    }

    /// <summary>The vertical field of view for a frame of <paramref name="aspect"/>: <paramref name="fovY"/>
    /// at 4:3 and wider (they see more across); narrower frames keep 4:3's width and see more down.</summary>
    public static float FovY(float fovY, float aspect)
    {
        if (aspect >= BaseAspect || aspect <= 0f)
        {
            return fovY;
        }

        return 2f * MathF.Atan(MathF.Tan(fovY / 2f) * BaseAspect / aspect);
    }
}
