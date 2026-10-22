using System.Drawing;
using System.Globalization;
using System.Numerics;
using RetroEng.Render;

namespace SkyNet.Game.Hud;

/// <summary>The MODERN HUD: the panel's read-outs as corner widgets over the full-frame view
/// (<see cref="ModernLayout"/>), drawn with the panel's own art, font and palette.</summary>
public sealed partial class DosHud
{
    /// <summary>The boxes' backing and the gauges' trough: dark, the scene showing through.</summary>
    private static readonly Vector4 Backing = new(0.02f, 0.03f, 0.04f, 0.45f), Trough = new(0f, 0f, 0f, 0.6f);
    private const float EdgeAlpha = 0.3f, CaptionDim = 0.7f;

    private void DrawModern(in HudState s, HudText caught, Frame f, RectangleF avoid)
    {
        var l = ModernLayout.For(f.Width, f.Height, PxPerMm);
        var text = _art.Colour(TextIndex);
        var edge = text with { W = EdgeAlpha };
        DrawCompass(s, l, f, edge);

        // RADCOUNT's SAFE / CAUTION / DANGER, as on the panel.
        var rad = _art.Cel("RADCOUNT.CFA");
        if (rad.Length > 0)
        {
            Blit(rad[Math.Min(HudFormat.RadFrame(s.Radiation), rad.Length - 1)], Canvas(l.Rad, f));
        }

        // The gauges: the panel's two bars with captions and a value in percent (DOS shows none).
        var font = TextFont;
        var tp = l.TextPixel(TextGrid);
        GaugeRow(s.Vehicle != HudVehicle.None ? "HULL" : "ARMOR", s.Armour, l.Armour, l, f);
        GaugeRow("HEALTH", s.Health / MathF.Max(s.MaxHealth, 1f), l.Health, l, f);

        // The icons in their boxes, the counts in their bottom-right corner (as the panel's).
        IconBox(_art.WeaponIcon(s.SecondaryRecord), s.SecondaryRecord >= 0 ? HudFormat.SecondaryCount(s.SecondaryAmmo) : null, l.Thrown, l, f, edge);
        IconBox(_art.WeaponIcon(s.PrimaryRecord), HudFormat.PrimaryCount(s.PrimaryAmmo), l.Primary, l, f, edge);

        // The message window, only while it has text: every typed line, the box growing upward to
        // under the crosshair; past that the newest lines, scrolling as the panel's window does.
        var typed = caught.Typed;
        if (font == null || typed.All(string.IsNullOrWhiteSpace))
        {
            return;
        }

        var pitch = (font.LineHeight + 1) * tp;
        var width = Messages.WrapWidth * tp;
        var rows = Math.Min(typed.Count, l.MessageRows(width, pitch, tp, avoid));
        var first = typed.Count - rows;
        var box = l.Messages(new SizeF(width, rows * pitch - tp), avoid);
        _renderer.FillRect(Canvas(box, f), Backing);
        _renderer.FrameRect(Canvas(box, f), l.Unit * f.Unit, edge);
        var pad = ModernLayout.MessagePad * l.Unit;
        for (var i = 0; i < rows; i++)
        {
            Say(font, typed[first + i], new Vector2(box.X + pad, box.Y + pad + i * pitch), tp, f, text);
        }
    }

    /// <summary>COMPASS.IMG through DOS's window, framed, a mark over its middle.</summary>
    private void DrawCompass(in HudState s, ModernLayout l, Frame f, Vector4 edge)
    {
        if (s.Bearing < 0 || _art.Image("COMPASS.IMG") is not { } compass)
        {
            return;
        }

        var r = l.Compass;
        var k = compass.Width / (float)HudFormat.CompassRibbon;
        var src = new RectangleF(-HudFormat.CompassScroll(s.Bearing) * k, 0, HudFormat.CompassWindow * k, compass.Height);
        var u = l.Unit;
        var frame = RectangleF.Inflate(r, u, u);
        _renderer.FillRect(Canvas(frame, f), Backing);
        _renderer.DrawImage(compass.Handle, _art.Palette, compass.Size, src, Canvas(r, f), Vector4.One);
        _renderer.FrameRect(Canvas(frame, f), u * f.Unit, edge);

        // The lubber mark: where the view faces, under the frame.
        var mid = MathF.Floor(r.X + r.Width / 2f);
        _renderer.FillRect(f.Pixels(mid - u / 2, frame.Bottom, Math.Max(u, 1), 2 * u), _art.Colour(TextIndex));
    }

    /// <summary>A caption, the value in percent right-aligned, the PANELBAR gauge cut at the value.</summary>
    private void GaugeRow(string caption, float fraction, RectangleF row, ModernLayout l, Frame f)
    {
        var v = float.IsNaN(fraction) ? 0f : Math.Clamp(fraction, 0f, 1f);
        var text = _art.Colour(TextIndex);
        if (TextFont is { } font)
        {
            var tp = l.TextPixel(TextGrid);
            var y = row.Y + MathF.Floor((row.Height - font.LineHeight * tp) / 2f);
            Say(font, caption, new Vector2(row.X, y), tp, f, (text * CaptionDim) with { W = 1f });
            var percent = ((int)MathF.Round(v * 100f)).ToString(CultureInfo.InvariantCulture);
            Say(font, percent, new Vector2(row.X + ModernLayout.ValueRight * l.Unit, y), tp, f, text, TextAlign.Right);
        }

        var bar = l.Bar(row);
        _renderer.FillRect(Canvas(bar, f), Trough);
        var filled = MathF.Round(bar.Width * v);
        if (v > 0f && filled < 1f)
        {
            filled = 1f;
        }

        if (filled > 0f && _art.Image("PANELBAR.IMG") is { } art)
        {
            var src = new RectangleF(0, 0, art.Width * filled / bar.Width, art.Height);
            _renderer.DrawImage(art.Handle, _art.Palette, art.Size, src, Canvas(bar with { Width = filled }, f), Vector4.One);
        }
    }

    /// <summary>An icon box: backing, the panel's icon, the count at its bottom right.</summary>
    private void IconBox(HudImage? icon, string? count, RectangleF box, ModernLayout l, Frame f, Vector4 edge)
    {
        _renderer.FillRect(Canvas(box, f), Backing);
        _renderer.FrameRect(Canvas(box, f), l.Unit * f.Unit, edge);
        Blit(icon, Canvas(box, f));
        if (count == null || TextFont is not { } font)
        {
            return;
        }

        var tp = l.TextPixel(TextGrid);
        var at = new Vector2(box.Right - 2 * l.Unit, box.Bottom - 2 * l.Unit - font.LineHeight * tp);
        Say(font, count, at, tp, f, _art.Colour(TextIndex), TextAlign.Right);
    }

    /// <summary>Text on whole screen pixels over the status lines' shadow.</summary>
    private void Say(BitmapFont font, string text, Vector2 at, int pixel, Frame f, Vector4 colour, TextAlign align = TextAlign.Left) =>
        TextBatcher.DrawShadowed(_renderer, font, text, new Vector2(MathF.Round(at.X), MathF.Round(at.Y)) * f.Unit, pixel * f.Unit, colour,
            _art.Colour(StatusShadowIndex), align: align);

    private static RectangleF Canvas(RectangleF r, Frame f) => f.Pixels(r.X, r.Y, r.Width, r.Height);
}
