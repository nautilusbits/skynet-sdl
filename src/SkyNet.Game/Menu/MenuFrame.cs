using System.Drawing;
using System.Numerics;
using RetroEng.Render;

namespace SkyNet.Game.Menu;

/// <summary>
/// Where DOS pixels of the 320 × 200 menu screen land on the renderer's canvas. The plain frame
/// stretches the screen over the whole window (as the Godot port's full-rect title and bar); a
/// <see cref="Panel"/> frame scales a dialog uniformly with the window and centres it. Edges fall on
/// whole screen pixels and text is drawn at the nearest whole scale (<see cref="TextScale"/>), so
/// the art stays nearest-sampled and the fonts crisp. Menu rectangles are in DOS pixels; the canvas
/// is <see cref="Renderer.CanvasHeight"/> units high. Plain maths, no GPU: tests build one for any size.
/// </summary>
public readonly record struct MenuFrame(int Width, int Height)
{
    public const int ScreenWidth = 320;
    public const int ScreenHeight = 200;

    /// <summary>The Godot port's canvas (1280 × 720, stretched to the window keeping its aspect):
    /// its panel scales are in its pixels.</summary>
    public const float GodotWidth = 1280f;
    public const float GodotHeight = 720f;

    /// <summary>The screen of a canvas <paramref name="canvasWidth"/> units wide on a target
    /// <paramref name="height"/> pixels high: in stereo an eye's (the viewer's: half the frame's width).</summary>
    public static MenuFrame ForCanvas(float canvasWidth, int height) =>
        new((int)MathF.Round(canvasWidth / Renderer.CanvasHeight * height), height);

    /// <summary>The screen on a window: stretched over it at 4:3 and wider (as ever); on a narrower
    /// one shown at 4:3 across its width, centred down (no tall stretch on a square screen).</summary>
    public static MenuFrame Fit(int width, int height)
    {
        var frame = new MenuFrame(width, height);
        if (width * 3 >= height * 4)
        {
            return frame;
        }

        var shown = width * 3 / 4f;
        return frame with { ScaleY = shown / ScreenHeight, OriginY = MathF.Round((height - shown) * 0.5f) };
    }

    /// <summary>Screen pixels of DOS (0, 0).</summary>
    public float OriginX { get; init; }

    public float OriginY { get; init; }

    /// <summary>Screen pixels per DOS pixel across and down.</summary>
    public float ScaleX { get; init; } = Width / (float)ScreenWidth;

    public float ScaleY { get; init; } = Height / (float)ScreenHeight;

    /// <summary>Canvas units per screen pixel.</summary>
    public float Unit => Renderer.CanvasHeight / Math.Max(Height, 1);

    /// <summary>Screen pixels per font pixel: the nearest whole number to the art's scale.</summary>
    public int TextScale => Math.Max(1, (int)MathF.Round(MathF.Min(ScaleX, ScaleY), MidpointRounding.AwayFromZero));

    /// <summary>Canvas units one font pixel (or a 1-pixel outline) covers.</summary>
    public float Pixel => TextScale * Unit;

    /// <summary>A dialog of <paramref name="dos"/> (its DOS rectangle) scaled uniformly by the Godot
    /// port's <paramref name="factor"/> for its 1280 × 720 canvas, centred on the window.</summary>
    public MenuFrame Panel(Rectangle dos, float factor)
    {
        var k = factor * MathF.Min(Width / GodotWidth, Height / GodotHeight);
        // Never past the window.
        k = MathF.Min(k, MathF.Min(Width / (float)Math.Max(dos.Width, 1), Height / (float)Math.Max(dos.Height, 1)));
        return this with
        {
            ScaleX = k,
            ScaleY = k,
            OriginX = MathF.Round((Width - dos.Width * k) * 0.5f - dos.X * k),
            OriginY = MathF.Round((Height - dos.Height * k) * 0.5f - dos.Y * k),
        };
    }

    /// <summary>A DOS x (or y) in whole screen pixels.</summary>
    public float ScreenX(float x) => MathF.Round(OriginX + x * ScaleX);

    public float ScreenY(float y) => MathF.Round(OriginY + y * ScaleY);

    /// <summary>A DOS rectangle on the canvas, its edges on whole screen pixels.</summary>
    public RectangleF Canvas(float x, float y, float w, float h)
    {
        float l = ScreenX(x), t = ScreenY(y), r = ScreenX(x + w), b = ScreenY(y + h);
        return new RectangleF(l * Unit, t * Unit, (r - l) * Unit, (b - t) * Unit);
    }

    public RectangleF Canvas(Rectangle r) => Canvas(r.X, r.Y, r.Width, r.Height);

    public Vector2 CanvasPoint(float x, float y) => new(ScreenX(x) * Unit, ScreenY(y) * Unit);

    /// <summary>The whole window on the canvas.</summary>
    public RectangleF Window => new(0f, 0f, Width * Unit, Height * Unit);

    /// <summary>A canvas point (the pointer, <see cref="Renderer.CanvasPoint"/>) in DOS pixels.</summary>
    public Vector2 ToDos(Vector2 canvas) =>
        new((canvas.X / Unit - OriginX) / ScaleX, (canvas.Y / Unit - OriginY) / ScaleY);
}
