using System.Drawing;
using System.Numerics;
using SkyNet.Game.Hud;
using SkyNet.Game.Weapons;

namespace SkyNet.Game.Tests.Hud;

/// <summary>The MODERN HUD's layout (<see cref="ModernLayout"/>) and the full-frame view it leaves.</summary>
public class ModernHudTests
{
    public static TheoryData<int, int> Screens() => new() { { 1280, 800 }, { 3440, 1440 }, { 1920, 1080 }, { 1280, 960 }, { 640, 480 } };

    [Theory]
    [InlineData(800, 2)]
    [InlineData(1440, 4)]
    [InlineData(1080, 3)]
    [InlineData(480, 1)]
    public void UnitGrowsWithTheHeightAtHalfThePanelsScale(int height, int unit) =>
        Assert.Equal(unit, ModernLayout.For(height * 2, height).Unit);

    [Theory]
    [MemberData(nameof(Screens))]
    public void WidgetsSitInTheirCornersInsideTheFrame(int w, int h)
    {
        var l = ModernLayout.For(w, h);
        var frame = new RectangleF(0, 0, w, h);
        foreach (var r in new[] { l.Plate, l.Rad, l.Armour, l.Health, l.Thrown, l.Primary, l.Compass, l.Bar(l.Health) })
        {
            Assert.True(frame.Contains(r), $"{r} leaves {w}x{h}");
        }

        // Bottom left, stacked without overlap; the compass at the top centre.
        Assert.True(l.Plate.Left < w / 4f && l.Plate.Bottom > h * 0.9f);
        Assert.True(l.Rad.Bottom <= l.Armour.Top && l.Armour.Bottom <= l.Health.Top && l.Health.Bottom <= l.Thrown.Top);
        Assert.True(l.Thrown.Right <= l.Primary.Left);
        Assert.True(l.Plate.Contains(l.Primary) && l.Plate.Contains(l.Rad));
        Assert.Equal(w / 2f, l.Compass.X + l.Compass.Width / 2f, 1f);
        Assert.True(l.Compass.Bottom < h * 0.1f);
    }

    [Theory]
    [InlineData(1280, 800, 0.25f, 0.15f)]
    [InlineData(3440, 1440, 0.16f, 0.15f)]
    public void ThePlateIsCompact(int w, int h, float maxWidth, float maxHeight)
    {
        var plate = ModernLayout.For(w, h).Plate;
        Assert.True(plate.Width <= w * maxWidth, $"{plate.Width} of {w}");
        Assert.True(plate.Height <= h * maxHeight, $"{plate.Height} of {h}");
        // Far less than the panel it replaces.
        Assert.True(plate.Width * plate.Height < w * DosHud.PanelPixels(w, h) / 4f);
    }

    [Fact]
    public void MessagesGoBottomRightAndClimbOverTouchButtons()
    {
        var l = ModernLayout.For(1280, 800);
        var box = l.Messages(new SizeF(196, 60), RectangleF.Empty);
        Assert.Equal(1280 - ModernLayout.Margin * 2, box.Right);
        Assert.Equal(800 - ModernLayout.Margin * 2, box.Bottom);

        var buttons = new RectangleF(1000, 700, 280, 100);
        var moved = l.Messages(new SizeF(196, 60), buttons);
        Assert.True(moved.Bottom <= buttons.Top && !moved.IntersectsWith(buttons));

        // Buttons elsewhere leave it where it was.
        Assert.Equal(box, l.Messages(new SizeF(196, 60), new RectangleF(1000, 300, 280, 200)));
    }

    private const string LongMessage = "FIND THE ENTRANCE TO THE RESISTANCE BASE. IT IS HIDDEN SOMEWHERE IN THE CANYON NORTH " +
        "OF THE CRASH SITE. WATCH OUT FOR HUNTER KILLERS AND THE TERMINATOR PATROLS ON THE RIDGE.";

    [Fact]
    public void TheMessageBoxKeepsEveryTypedLine()
    {
        var box = new MessageBox(_ => 5);
        box.Start(LongMessage, false);
        box.Finish();

        // The panel's window scrolled the first lines off; the whole message is still there.
        Assert.True(box.Typed.Count > box.LineCount);
        Assert.Equal(box.LineCount, box.Text.Count);
        Assert.Equal(TextLayout.Lines(box.Stream), box.Typed);
        Assert.Equal(box.Text, box.Typed.TakeLast(box.LineCount));

        box.Start("NEXT", false);
        Assert.Equal([""], box.Typed);
    }

    [Theory]
    [InlineData(1280, 800, false)]
    [InlineData(1920, 1080, false)]
    [InlineData(3440, 1440, false)]
    [InlineData(1280, 800, true)]
    [InlineData(1920, 1080, true)]
    [InlineData(3440, 1440, true)]
    public void ALongMessageShowsWholeBelowTheCrosshair(int w, int h, bool touch)
    {
        var l = ModernLayout.For(w, h);
        var box = new MessageBox(_ => 5);
        box.Start(LongMessage, false);
        box.Finish();
        var tp = l.TextPixel(1);
        var pitch = 6 * tp;
        // The touch layout's lower-right buttons: a column from the middle down.
        var buttons = touch ? new RectangleF(w * 0.85f, h * 0.46f, w * 0.14f, h * 0.34f) : RectangleF.Empty;

        var rows = l.MessageRows(box.WrapWidth * tp, pitch, tp, buttons);
        Assert.True(rows >= box.Typed.Count, $"{rows} rows for {box.Typed.Count} lines at {w}x{h}");

        var r = l.Messages(new SizeF(box.WrapWidth * tp, box.Typed.Count * pitch - tp), buttons);
        Assert.True(new RectangleF(0, 0, w, h).Contains(r), $"{r} leaves {w}x{h}");
        Assert.True(r.Top > h / 2f && r.Top >= l.Compass.Bottom, $"{r} covers the crosshair");
        Assert.False(r.IntersectsWith(buttons) || r.IntersectsWith(l.Plate));
    }

    [Fact]
    public void TooLongAMessageStopsBelowTheCrosshair()
    {
        var l = ModernLayout.For(640, 480);
        var buttons = new RectangleF(400, 300, 240, 180);
        var rows = l.MessageRows(98, 6, 1, buttons);
        Assert.True(rows >= 1);

        var r = l.Messages(new SizeF(98, rows * 6 - 1), buttons);
        Assert.True(r.Top >= l.MessageCeiling && r.Top > 240 && !r.IntersectsWith(buttons));
        Assert.True(l.Messages(new SizeF(98, (rows + 1) * 6 - 1), buttons).Top < l.MessageCeiling);
    }

    [Fact]
    public void TextIsWholePixelsOnEitherFontGrid()
    {
        Assert.Equal((2, 1), (ModernLayout.For(1280, 800).TextPixel(1), ModernLayout.For(1280, 800).TextPixel(2)));
        Assert.Equal((4, 2), (ModernLayout.For(3440, 1440).TextPixel(1), ModernLayout.For(3440, 1440).TextPixel(2)));
        Assert.Equal(1, ModernLayout.For(640, 480).TextPixel(2));
    }

    [Theory]
    [MemberData(nameof(Screens))]
    public void TheViewIsTheFullFrameWithTheCrosshairInItsMiddle(int w, int h)
    {
        Assert.Equal(0, DosHud.PanelPixels(w, h, HudStyle.Modern));
        Assert.Equal(new Vector2(MathF.Floor(w / 2f), MathF.Floor(h / 2f)), DosHud.ViewCentre(w, h, HudStyle.Modern));
        // CLASSIC keeps DOS's centre above the panel.
        Assert.True(DosHud.ViewCentre(w, h).Y < DosHud.ViewCentre(w, h, HudStyle.Modern).Y);
    }

    [Fact]
    public void TheGunStandsOnTheFrameBottomAtTheSameSize()
    {
        var classic = ViewModelLayout.Place(1280, 800, 156, new Size(72, 87));
        var modern = ViewModelLayout.Place(1280, 800, 156, new Size(72, 87), HudStyle.Modern);
        Assert.Equal(classic.Size, modern.Size);
        Assert.Equal(classic.X, modern.X);
        Assert.Equal(DosHud.PanelPixels(1280, 800), modern.Bottom - classic.Bottom);
    }
}
