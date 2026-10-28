using System.Drawing;
using System.Numerics;
using RetroEng.Platform;
using RetroEng.Render;

namespace SkyNet.Game.Hud;

/// <summary>Draws the on-screen controls in the HUD's green-on-dark: each button a dim box with
/// bright corner brackets and a pixel icon (FIRE a crosshair, JUMP an arrow, WPN± chevrons, pause
/// two bars) or its word, filled green while held; the stick a dotted ring and a knob, faint at
/// rest. The game shows through (the boxes are a third opaque).
/// <code>
///   ┌─    ─┐
///      ┼        FIRE: crosshair and a small label
///    FIRE
///   └─    ─┘
/// </code></summary>
public static class TouchView
{
    /// <summary>How a button is drawn.</summary>
    internal enum Shade { Idle, Held, Selected }

    private static readonly Vector4 Fill = new(0.02f, 0.06f, 0.03f, 0.35f);
    private static readonly Vector4 HeldFill = new(0.30f, 0.85f, 0.40f, 0.55f);
    private static readonly Vector4 SelectedFill = new(0.85f, 0.75f, 0.20f, 0.45f);
    private static readonly Vector4 Edge = new(0.40f, 0.95f, 0.45f, 0.35f);
    private static readonly Vector4 Bracket = new(0.55f, 1f, 0.65f, 0.9f);
    private static readonly Vector4 Ink = new(0.55f, 1f, 0.65f, 0.95f);
    private static readonly Vector4 HeldInk = new(0.9f, 1f, 0.9f, 1f);

    /// <summary>A bracket's arm and the icon's size, parts of the side.</summary>
    private const float Arm = 0.22f;
    private const float IconPart = 0.42f;

    /// <summary>The stick at rest, its alpha against a pushed one.</summary>
    private const float RestAlpha = 0.45f;
    private const int RingDots = 28;
    private const float KnobPart = 0.45f;

    /// <summary>Words drawn for an engine label (the rest as is).</summary>
    /// <summary>The engine's pause button: its icon only.</summary>
    private const string PauseLabel = "||";

    private static readonly Dictionary<string, string> Words = new(StringComparer.Ordinal) { ["ALT"] = "THROW" };

    /// <summary>The play frame's controls (window pixels → the canvas).</summary>
    public static void Draw(Renderer renderer, TouchControls touch, Vector2 screen, BitmapFont? font)
    {
        if (touch.TapOnly)
        {
            return;
        }

        var unit = Renderer.CanvasHeight / MathF.Max(screen.Y, 1f);
        var line = MathF.Max(1f, MathF.Round(2f * touch.Scale)) * unit;
        foreach (var button in touch.Layout(screen))
        {
            var a = button.Area;
            var area = new RectangleF(a.X * unit, a.Y * unit, a.Width * unit, a.Height * unit);
            Button(renderer, area, button.Label, touch.IsHeld(button.Action) ? Shade.Held : Shade.Idle, font, line, unit);
        }

        var radius = touch.StickRadius(screen);
        var home = touch.StickHome(screen) ?? TouchLayout.Centre(TouchLayout.StickDefault, screen, touch.ButtonSide(screen));
        var centre = touch.Stick ?? home;
        var knob = centre;
        if (touch.StickAt is { } at && touch.Stick is { } stick)
        {
            var push = at - stick;
            knob = push.Length() > radius ? stick + Vector2.Normalize(push) * radius : at;
        }

        Stick(renderer, centre * unit, knob * unit, radius * unit, line, touch.Stick != null ? Shade.Held : Shade.Idle);
    }

    /// <summary>A button in canvas units: its box, brackets, and icon or word.</summary>
    internal static void Button(Renderer renderer, RectangleF area, string label, Shade shade, BitmapFont? font, float line, float unit)
    {
        renderer.FillRect(area, shade switch { Shade.Held => HeldFill, Shade.Selected => SelectedFill, _ => Fill });
        renderer.FrameRect(area, line, Edge);
        Brackets(renderer, area, line);

        var ink = shade == Shade.Idle ? Ink : HeldInk;
        var centre = new Vector2(area.X + area.Width / 2f, area.Y + area.Height / 2f);
        var icon = IconPart * area.Width;
        var drawn = Icon(renderer, label, centre - new Vector2(0f, 0.08f * area.Height), icon, line * 1.5f, ink);
        if (font == null || label == PauseLabel)
        {
            return;
        }

        // The word: big when there is no icon, small under it when there is; never thinner than a line.
        var word = Words.GetValueOrDefault(label, label);
        var room = drawn ? 0.16f : 0.3f;
        var fit = MathF.Min(area.Width * 0.85f / MathF.Max(font.Measure(word), 1f), area.Height * room / font.LineHeight);
        var pixel = MathF.Max(line, MathF.Round(fit / unit) * unit);
        var y = drawn ? area.Bottom - 0.08f * area.Height - font.LineHeight * pixel : centre.Y - font.LineHeight * pixel / 2f;
        TextBatcher.Draw(renderer, font, word, new Vector2(centre.X, y), pixel, ink, TextAlign.Centre);
    }

    /// <summary>The stick in canvas units: a dotted ring of the full push and the knob.</summary>
    internal static void Stick(Renderer renderer, Vector2 centre, Vector2 knob, float radius, float line, Shade shade)
    {
        var alpha = shade == Shade.Idle ? RestAlpha : 1f;
        var dot = 1.5f * line;
        for (var i = 0; i < RingDots; i++)
        {
            var t = i * MathF.Tau / RingDots;
            var p = centre + radius * new Vector2(MathF.Cos(t), MathF.Sin(t));
            renderer.FillRect(new RectangleF(p.X - dot / 2f, p.Y - dot / 2f, dot, dot), Bracket with { W = Bracket.W * alpha });
        }

        Disc(renderer, centre, radius, Fill with { W = Fill.W * alpha });
        Disc(renderer, knob, KnobPart * radius, (shade == Shade.Selected ? SelectedFill : HeldFill) with { W = HeldFill.W * alpha });
    }

    /// <summary>The bright corners of a box (the HUD's bracket look).</summary>
    private static void Brackets(Renderer renderer, RectangleF a, float line)
    {
        var arm = Arm * a.Width;
        foreach (var (x, y, dx, dy) in new[] { (a.Left, a.Top, 1, 1), (a.Right, a.Top, -1, 1), (a.Left, a.Bottom, 1, -1), (a.Right, a.Bottom, -1, -1) })
        {
            renderer.FillRect(Span(x, y, dx * arm, dy * line), Bracket);
            renderer.FillRect(Span(x, y, dx * line, dy * arm), Bracket);
        }
    }

    /// <summary>A rectangle from a corner by a signed size.</summary>
    private static RectangleF Span(float x, float y, float w, float h) =>
        new(MathF.Min(x, x + w), MathF.Min(y, y + h), MathF.Abs(w), MathF.Abs(h));

    /// <summary>A label's pixel icon about a point (side <paramref name="size"/>); false when it has none.</summary>
    private static bool Icon(Renderer r, string label, Vector2 c, float size, float stroke, Vector4 ink)
    {
        var h = size / 2f;
        switch (label)
        {
            case "FIRE":
                // A crosshair: four ticks and a dot.
                Line(r, c + new Vector2(-h, 0f), c + new Vector2(-h / 3f, 0f), stroke, ink);
                Line(r, c + new Vector2(h / 3f, 0f), c + new Vector2(h, 0f), stroke, ink);
                Line(r, c + new Vector2(0f, -h), c + new Vector2(0f, -h / 3f), stroke, ink);
                Line(r, c + new Vector2(0f, h / 3f), c + new Vector2(0f, h), stroke, ink);
                r.FillRect(new RectangleF(c.X - stroke / 2f, c.Y - stroke / 2f, stroke, stroke), ink);
                return true;
            case "JUMP":
                Chevron(r, c, h, new Vector2(0f, -1f), stroke, ink);
                Chevron(r, c + new Vector2(0f, h * 0.6f), h, new Vector2(0f, -1f), stroke, ink);
                return true;
            case "WPN+":
                Chevron(r, c, h, new Vector2(1f, 0f), stroke, ink);
                return true;
            case "WPN-":
                Chevron(r, c, h, new Vector2(-1f, 0f), stroke, ink);
                return true;
            case PauseLabel:
                r.FillRect(new RectangleF(c.X - h * 0.6f, c.Y - h, h * 0.4f, 2f * h), ink);
                r.FillRect(new RectangleF(c.X + h * 0.2f, c.Y - h, h * 0.4f, 2f * h), ink);
                return true;
            default:
                return false;
        }
    }

    /// <summary>A chevron pointing along <paramref name="dir"/> (a unit axis).</summary>
    private static void Chevron(Renderer r, Vector2 c, float h, Vector2 dir, float stroke, Vector4 ink)
    {
        var side = new Vector2(dir.Y, dir.X);
        var tip = c + dir * h * 0.5f;
        Line(r, tip, tip - dir * h * 0.6f + side * h * 0.7f, stroke, ink);
        Line(r, tip, tip - dir * h * 0.6f - side * h * 0.7f, stroke, ink);
    }

    /// <summary>A line of square dots (the HUD's pixel look).</summary>
    private static void Line(Renderer r, Vector2 a, Vector2 b, float stroke, Vector4 ink)
    {
        var steps = Math.Max(1, (int)MathF.Ceiling(Vector2.Distance(a, b) / (stroke * 0.5f)));
        for (var i = 0; i <= steps; i++)
        {
            var p = Vector2.Lerp(a, b, i / (float)steps);
            r.FillRect(new RectangleF(p.X - stroke / 2f, p.Y - stroke / 2f, stroke, stroke), ink);
        }
    }

    /// <summary>A filled circle of horizontal strips.</summary>
    private static void Disc(Renderer r, Vector2 c, float radius, Vector4 colour)
    {
        const int Strips = 12;
        var strip = 2f * radius / Strips;
        for (var i = 0; i < Strips; i++)
        {
            var y = -radius + (i + 0.5f) * strip;
            var half = MathF.Sqrt(MathF.Max(radius * radius - y * y, 0f));
            r.FillRect(new RectangleF(c.X - half, c.Y + y - strip / 2f, 2f * half, strip), colour);
        }
    }
}
