using System.Drawing;
using System.Numerics;
using RetroEng.Render;

namespace SkyNet.Game.DevTools;

/// <summary>
/// Draws the developer displays on the canvas in a game bitmap font: the FPS readout at the top
/// right, the debug overlay's lines at the top left (under the console when it is down), each on a
/// dark box, and the objective pointer: a box on the target with its distance, or, off screen, an
/// arrow on a ring round the view's centre. <c>pixel</c> is the canvas units of one font pixel
/// (whole screen pixels keep the font crisp at every window size).
/// </summary>
public sealed class OverlayView(Renderer renderer, BitmapFont font)
{
    private static readonly Vector4 Background = new(0f, 0f, 0f, 0.62f);
    private static readonly Vector4 TextColour = new(0.80f, 0.92f, 0.82f, 1f);
    private static readonly Vector4 PointerColour = new(1f, 0.82f, 0.2f, 1f);
    private static readonly Vector4 Shadow = new(0f, 0f, 0f, 0.85f);
    private const float Margin = 3f;
    /// <summary>The off-screen arrow's ring, a fraction of the canvas height.</summary>
    private const float Ring = 0.42f;
    private const float MarkerHalf = 5f;
    private const float ArrowLength = 9f;
    private const float ArrowDots = 6;

    public BitmapFont Font => font;

    /// <summary>Baseline to baseline at <paramref name="pixel"/>.</summary>
    public float LineHeight(float pixel) => (font.LineHeight + 2) * pixel;

    /// <summary>The readout at the top right.</summary>
    public void DrawFps(string text, float pixel)
    {
        var margin = Margin * pixel;
        var width = font.Measure(text) * pixel + 2f * margin;
        var x = renderer.CanvasWidth - width;
        renderer.FillRect(new RectangleF(x, 0f, width, font.LineHeight * pixel + 2f * margin), Background);
        TextBatcher.Draw(renderer, font, text, new Vector2(x + margin, margin), pixel, TextColour);
    }

    /// <summary>The lines from <paramref name="top"/> at the left; a line starting with '>' (the
    /// pointer) in the pointer's colour.</summary>
    public void DrawLines(IReadOnlyList<string> lines, float top, float pixel)
    {
        var margin = Margin * pixel;
        var widest = 0;
        foreach (var line in lines)
        {
            widest = Math.Max(widest, font.Measure(line));
        }

        var lineHeight = LineHeight(pixel);
        var width = MathF.Min(widest * pixel + 2f * margin, renderer.CanvasWidth);
        renderer.FillRect(new RectangleF(0f, top, width, lines.Count * lineHeight + 2f * margin - 2f * pixel), Background);
        var y = top + margin;
        foreach (var line in lines)
        {
            TextBatcher.Draw(renderer, font, line, new Vector2(margin, y), pixel, line.StartsWith('>') ? PointerColour : TextColour);
            y += lineHeight;
        }
    }

    /// <summary>The pointer at <paramref name="target"/> seen through <paramref name="viewProjection"/>.</summary>
    public void DrawPointer(ObjectiveTarget target, Matrix4x4 viewProjection, float pixel)
    {
        var canvas = new Vector2(renderer.CanvasWidth, Renderer.CanvasHeight);
        var centre = canvas / 2f;
        var clip = Vector4.Transform(new Vector4(target.Position, 1f), viewProjection);
        var behind = clip.W <= 0f;
        var ndc = new Vector2(clip.X, clip.Y) / MathF.Max(MathF.Abs(clip.W), 1e-4f);
        var at = new Vector2((ndc.X * 0.5f + 0.5f) * canvas.X, (0.5f - ndc.Y * 0.5f) * canvas.Y);
        var label = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{target.Distance:0} u");
        var edge = 12f * pixel;
        if (!behind && at.X > edge && at.X < canvas.X - edge && at.Y > edge && at.Y < canvas.Y - edge)
        {
            var half = MarkerHalf * pixel;
            Outline(new RectangleF(at.X - half, at.Y - half, 2f * half, 2f * half), pixel);
            TextBatcher.DrawShadowed(renderer, font, label, at + new Vector2(half + 2f * pixel, -font.LineHeight * pixel / 2f), pixel,
                PointerColour, Shadow);
            return;
        }

        // Off screen (or behind): an arrow of dots on the ring, pointing out towards it.
        var dir = at - centre;
        if (behind)
        {
            dir = -dir;
        }

        dir = dir.LengthSquared() < 1e-6f ? Vector2.UnitY : Vector2.Normalize(dir);
        var tip = centre + dir * (Renderer.CanvasHeight * Ring);
        var side = new Vector2(-dir.Y, dir.X);
        var dot = 2f * pixel;
        for (var i = 0; i < ArrowDots; i++)
        {
            var back = tip - dir * (ArrowLength * pixel * i / ArrowDots);
            var spread = side * (i * 0.6f * pixel);
            Dot(back + spread, dot);
            Dot(back - spread, dot);
        }

        TextBatcher.DrawShadowed(renderer, font, label, tip - dir * (ArrowLength + 6f) * pixel - new Vector2(font.Measure(label) * pixel / 2f, 0f),
            pixel, PointerColour, Shadow);
    }

    private void Dot(Vector2 at, float size) => renderer.FillRect(new RectangleF(at.X - size / 2f, at.Y - size / 2f, size, size), PointerColour);

    private void Outline(RectangleF r, float pixel)
    {
        var w = pixel;
        renderer.FillRect(new RectangleF(r.X, r.Y, r.Width, w), PointerColour);
        renderer.FillRect(new RectangleF(r.X, r.Bottom - w, r.Width, w), PointerColour);
        renderer.FillRect(new RectangleF(r.X, r.Y, w, r.Height), PointerColour);
        renderer.FillRect(new RectangleF(r.Right - w, r.Y, w, r.Height), PointerColour);
    }
}
