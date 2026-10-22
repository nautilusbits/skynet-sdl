using System.Drawing;

namespace SkyNet.Game.Hud;

/// <summary>Which HUD the game draws: DOS's PANEL0 across the bottom (the view centred above it) or
/// the compact corner widgets over a full-frame view (<see cref="ModernLayout"/>).</summary>
public enum HudStyle { Classic, Modern }

/// <summary>
/// Where the MODERN HUD's widgets go on a <c>width × height</c> frame, in screen pixels. Sized in
/// DOS 320 × 200 pixels (the panel art's own) times <see cref="Unit"/>, a whole number that grows
/// with the height: half the classic panel's scale, so the art stays crisp and the widgets small.
/// <code>
///                  [compass]                     top centre
///
///                      +                         crosshair: the frame's centre
///
/// [RADCOUNT]                          [messages] bottom right, only while typed
/// ARMOR  100 ======
/// HEALTH 100 ======
/// [thrown 3][primary  500]                       bottom left
/// </code>
/// The Godot port's hud_modern.gd had the same corners (gauges and guns bottom left, messages bottom
/// right) with its compass at the bottom centre; here the compass is at the top, clear of the gun.
/// </summary>
public readonly record struct ModernLayout(int Width, int Height, int Unit)
{
    /// <summary>Frame lines per <see cref="Unit"/> step: 2 at 800 lines, 4 at 1440.</summary>
    public const float LinesPerUnit = 400f;
    /// <summary>The gap to the frame's edges and between widgets (DOS pixels).</summary>
    public const int Margin = 6;
    public const int Gap = 3;
    /// <summary>A caption / value / gauge row and its parts (DOS pixels).</summary>
    public const int RowHeight = 8;
    public const int CaptionWidth = 36;
    public const int ValueRight = 60;
    public const int BarLeft = 64;
    public const int BarHeight = 5;
    /// <summary>The panel's icon boxes (DOS's (3,180) and (50,180)) and RADCOUNT.CFA's size.</summary>
    public static readonly Size ThrownBox = new(39, 17), PrimaryBox = new(83, 17), RadSize = new(41, 10);
    public const int PlateWidth = 39 + 4 + 83;
    /// <summary>The message box's padding (DOS pixels).</summary>
    public const int MessagePad = 3;

    /// <summary>The layout of a frame; on a screen of <paramref name="pxPerMm"/> (<see cref="UiMetrics.PxPerMm"/>)
    /// a row stays readable.</summary>
    public static ModernLayout For(int width, int height, float pxPerMm = 0f) =>
        new(width, height, UiMetrics.TextPixel(Math.Max(1, (int)MathF.Round(height / LinesPerUnit, MidpointRounding.AwayFromZero)), RowHeight, pxPerMm));

    private float Px(int dos) => dos * Unit;

    private float Bottom => Height - Px(Margin);

    /// <summary>The bottom-left icon row: the thrown item's box, then the primary's.</summary>
    public RectangleF Thrown => new(Px(Margin), Bottom - Px(ThrownBox.Height), Px(ThrownBox.Width), Px(ThrownBox.Height));

    public RectangleF Primary => new(Thrown.Right + Px(4), Thrown.Y, Px(PrimaryBox.Width), Px(PrimaryBox.Height));

    /// <summary>The HEALTH row above the icons, ARMOR (HULL in a vehicle) above it.</summary>
    public RectangleF Health => Row(Thrown.Y - Px(Gap) - Px(RowHeight));

    public RectangleF Armour => Row(Health.Y - Px(RowHeight));

    /// <summary>RADCOUNT's SAFE / CAUTION / DANGER over the gauges.</summary>
    public RectangleF Rad => new(Px(Margin), Armour.Y - Px(Gap) - Px(RadSize.Height), Px(RadSize.Width), Px(RadSize.Height));

    /// <summary>The whole bottom-left plate (what the deathmatch's chat stays above).</summary>
    public RectangleF Plate => RectangleF.FromLTRB(Px(Margin), Rad.Y, Px(Margin + PlateWidth), Bottom);

    /// <summary>A gauge row's bar (the row's right part, centred down it).</summary>
    public RectangleF Bar(RectangleF row) =>
        new(row.X + Px(BarLeft), row.Y + Px((RowHeight - BarHeight) / 2), Px(PlateWidth - BarLeft), Px(BarHeight));

    /// <summary>The compass: DOS's 37 × 14 window, top centre.</summary>
    public RectangleF Compass
    {
        get
        {
            var w = Px(HudFormat.CompassWindow);
            return new RectangleF(MathF.Floor((Width - w) / 2f), Px(Margin), w, Px(14));
        }
    }

    /// <summary>The message box for <paramref name="content"/> (screen pixels of text) at the bottom
    /// right, padded; above <paramref name="avoid"/> (the touch buttons) when it would cover them, or
    /// left of them when above would reach the crosshair.</summary>
    public RectangleF Messages(SizeF content, RectangleF avoid)
    {
        var w = content.Width + Px(2 * MessagePad);
        var h = content.Height + Px(2 * MessagePad);
        var box = new RectangleF(Width - Px(Margin) - w, Bottom - h, w, h);
        if (avoid.IsEmpty || !box.IntersectsWith(avoid))
        {
            return box;
        }

        // Above the buttons; beside them (to their left) when above would reach the crosshair.
        var above = box with { Y = avoid.Top - Px(Margin) - h };
        var beside = box with { X = avoid.Left - Px(Margin) - w };
        return above.Top >= MessageCeiling || beside.IntersectsWith(Plate) || beside.Left < Px(Margin) ? above : beside;
    }

    /// <summary>The highest the message box reaches: below the crosshair (the frame's middle) and its arms.</summary>
    public float MessageCeiling => MathF.Floor(Height / 2f) + Px(2 * Margin);

    /// <summary>
    /// The most text lines (<paramref name="pitch"/> apart, the last <paramref name="trim"/> short) a
    /// <paramref name="width"/> wide message box holds under <see cref="MessageCeiling"/> where
    /// <see cref="Messages"/> puts it; at least 1. A longer message shows its newest lines, as the
    /// panel's window scrolls.
    /// </summary>
    public int MessageRows(float width, float pitch, float trim, RectangleF avoid)
    {
        var rows = Math.Max(1, (int)(Height / pitch));
        while (rows > 1 && Messages(new SizeF(width, rows * pitch - trim), avoid).Top < MessageCeiling)
        {
            rows--;
        }

        return rows;
    }

    /// <summary>Screen pixels per font pixel: the unit for FONT0004, half of it (at least 1) for the
    /// hi-res set's FONT0010, which is drawn on a grid twice as fine.</summary>
    public int TextPixel(int grid) => Math.Max(1, Unit / Math.Max(1, grid));

    private RectangleF Row(float y) => new(Px(Margin), y, Px(PlateWidth), Px(RowHeight));
}
