using RetroEng.Render;
using SkyNet.Game.Hud;

namespace SkyNet.Game.Tests.Hud;

public class TextLayoutTests
{
    // A FONT0004-like metric: every glyph 5 wide (advance 6), a space the em width 6.
    private static int Advance(char c) => 6;

    [Fact]
    public void WrapsAfterTheLastSpaceThatFits()
    {
        // 16 characters × 6 = 96 < 98; the 17th would reach 102 ≥ 98.
        var wrapped = TextLayout.Wrap("AAAA BBBB CCCC DDDD EEEE", Advance, MessageBox.WrapPx);
        Assert.Equal("AAAA BBBB CCCC \r\nDDDD EEEE", wrapped);
        foreach (var line in TextLayout.Lines(wrapped))
        {
            Assert.True(line.Length * 6 < MessageBox.WrapPx);
        }
    }

    [Fact]
    public void BreaksAWordWithNoSpaceWhereItIs()
    {
        var wrapped = TextLayout.Wrap(new string('X', 20), Advance, MessageBox.WrapPx);
        Assert.Equal(new string('X', 16) + "\r\n" + new string('X', 4), wrapped);
    }

    [Fact]
    public void LineBreaksBecomeSpaces()
    {
        Assert.Equal("A B C D", TextLayout.Wrap("A\r\nB\nC\rD", Advance, MessageBox.WrapPx));
    }

    [Fact]
    public void BreakIsAtTheWidthNotPastIt()
    {
        // x + advance >= width breaks: with advance 7, 14 chars = 98 must break before the 14th.
        var wrapped = TextLayout.Wrap(new string('X', 14), _ => 7, MessageBox.WrapPx);
        Assert.Equal(new string('X', 13) + "\r\nX", wrapped);
    }
}

public class MessageBoxTests
{
    private static MessageBox Box() => new(_ => 6);

    [Fact]
    public void TypesThirtyCharactersASecond()
    {
        var box = Box();
        box.ShowHint("HELLO WORLD");
        Assert.Equal([""], box.Text);
        Assert.False(box.Tick(1f / 60f));        // half a character: nothing yet
        Assert.True(box.Tick(1f / 60f));         // one
        Assert.Equal("H", box.Text[0]);
        box.Tick(0.1f);                         // three more
        Assert.Equal("HELL", box.Text[0]);
        box.Tick(10f);
        Assert.Equal("HELLO WORLD", box.Text[0]);
        Assert.False(box.Typing);
        Assert.False(box.Tick(1f));             // no expiry: it stays
        Assert.Equal("HELLO WORLD", box.Text[0]);
    }

    [Fact]
    public void ShowsFiveLinesAndScrollsTheOldestOff()
    {
        var box = Box();
        // Seven 3-word lines of 15 characters each (+ space): wraps one per line.
        box.ShowHint(string.Join(' ', Enumerable.Range(1, 7).Select(i => $"L{i}AA BBBB CCCC")));
        box.Finish();
        Assert.Equal(MessageBox.Lines, box.Text.Count);
        Assert.StartsWith("L3AA", box.Text[0]);
        Assert.StartsWith("L7AA", box.Text[^1]);
    }

    [Fact]
    public void ANewMessageClearsTheBoxAndAsksForSound51()
    {
        var box = Box();
        var sounds = new List<int>();
        box.SoundRequested += sounds.Add;
        box.ShowHint("FIRST");
        box.Finish();
        box.ShowObjective("SECOND");
        Assert.Equal([""], box.Text);
        Assert.Equal([0x51, 0x51], sounds);
        box.ShowObjective("AB");                // 2 characters or fewer: DOS skips it
        Assert.Equal("SECOND", box.Stream);
        box.ShowObjective("THIRD", playSound: false);
        Assert.Equal(2, sounds.Count);
        box.Clear();
        Assert.Empty(box.Text);
    }

    [Fact]
    public void TheHiResLayoutWrapsWiderAndHoldsSixLines()
    {
        var box = Box();
        box.ShowHint(string.Join(' ', Enumerable.Range(1, 9).Select(i => $"L{i}AA BBBB CCCC")));
        box.Finish();
        Assert.Equal(MessageBox.Lines, box.Text.Count);
        // FONT0010-like: advance 11; 196 px holds 17 characters, still one 15-character group a line.
        box.SetLayout(_ => 11, MessageBox.HiResWrapPx, MessageBox.HiResLines);
        Assert.Equal((196, 6), (box.WrapWidth, box.LineCount));
        Assert.False(box.Typing);                       // a finished message stays finished
        Assert.Equal(MessageBox.HiResLines, box.Text.Count);
        Assert.StartsWith("L4AA", box.Text[0]);
        Assert.StartsWith("L9AA", box.Text[^1]);
    }

    [Fact]
    public void ARelayoutKeepsTheTypingWhereItWas()
    {
        var box = Box();
        box.ShowHint("HELLO WORLD");
        box.Tick(5.5f / 30f);
        box.SetLayout(_ => 11, MessageBox.HiResWrapPx, MessageBox.HiResLines);
        Assert.Equal(["HELLO"], box.Text);
        Assert.True(box.Typing);
    }
}

public class HiResHudTests
{
    [Fact]
    public void PositionsScaleAsFun0012ad2e()
    {
        Assert.Equal(256, DosHud.HiResX(128));
        Assert.Equal(384, DosHud.HiResY(160));          // the panel's top: 640 × 96 below it
        Assert.Equal(480, DosHud.HiResY(200));
        Assert.Equal(458, DosHud.HiResY(191));          // the counts' row
        Assert.Equal(394, DosHud.HiResY(164));          // the message box's first line
        Assert.Equal(79, DosHud.HiResY(DosHud.MessageBoxRows));
    }

    [Fact]
    public void TheMessageBoxHoldsFiveLowAndSixHiResLines()
    {
        Assert.Equal(MessageBox.Lines, DosHud.MessageLines(DosHud.MessageBoxRows, 5));
        Assert.Equal(MessageBox.HiResLines, DosHud.MessageLines(DosHud.HiResY(DosHud.MessageBoxRows), 11));
        Assert.Equal(MessageBox.HiResWrapPx, DosHud.HiResX(MessageBox.WrapPx));
    }

    [Fact]
    public void TheHiResFontTableSwapsFourForTen()
    {
        Assert.Equal(10, HudFont.HiResFont(HudFont.HudText));
        Assert.Equal((8, 9, 11, 3), (HudFont.HiResFont(0), HudFont.HiResFont(1), HudFont.HiResFont(7), HudFont.HiResFont(3)));
        Assert.Equal("FONT0010.FNT", HudFont.FileName(HudFont.HiResFont(HudFont.HudText)));
    }

    [Fact]
    public void TheHiResTextPixelIsHalfTheLowOne()
    {
        Assert.Equal(3, DosHud.HiResPixelScale(1920, 1080));
        Assert.Equal(2, DosHud.HiResPixelScale(1269, 700));
        Assert.Equal(2, DosHud.HiResPixelScale(1280, 960));
        Assert.Equal(1, DosHud.HiResPixelScale(320, 200));
    }
}

public class HudFormatTests
{
    [Theory]
    [InlineData(-1, null)]
    [InlineData(0, "0")]
    [InlineData(187, "187")]
    [InlineData(9999, "9999")]
    [InlineData(12345, "9999")]
    public void PrimaryCountIsFourDigitsClamped(int ammo, string? expected) => Assert.Equal(expected, HudFormat.PrimaryCount(ammo));

    [Theory]
    [InlineData(-1, null)]
    [InlineData(4, "4")]
    [InlineData(1000, "999")]
    public void SecondaryCountIsThreeDigitsClamped(int count, string? expected) => Assert.Equal(expected, HudFormat.SecondaryCount(count));

    [Theory]
    [InlineData(0f, 0)]
    [InlineData(0.001f, 1)]
    [InlineData(0.5f, 21)]
    [InlineData(0.64f, 26)]
    [InlineData(1f, 41)]
    [InlineData(2f, 41)]
    [InlineData(-1f, 0)]
    public void BarFillRoundsAndKeepsOnePixelWhileAlive(float v, int px) => Assert.Equal(px, HudFormat.BarFill(v));

    [Theory]
    [InlineData(0f, 0)]
    [InlineData(0.99f, 0)]
    [InlineData(1f, 1)]
    [InlineData(34.9f, 1)]
    [InlineData(35f, 2)]
    public void RadiationFrames(float dose, int frame) => Assert.Equal(frame, HudFormat.RadFrame(dose));

    [Fact]
    public void CompassScrollsOneRibbonTurnPerBearingTurn()
    {
        Assert.Equal(0, HudFormat.CompassScroll(0));
        Assert.Equal(-37, HudFormat.CompassScroll(512));      // east: one letter (37 px) along
        Assert.Equal(-74, HudFormat.CompassScroll(1024));
        Assert.Equal(-148, HudFormat.CompassScroll(2047));
        Assert.Equal(512, HudFormat.Bearing(MathF.PI / 2f));
        Assert.Equal((90 << 11) / 360, HudFormat.Bearing(0f, 90));
    }

    [Fact]
    public void ThePanelSpansTheWidthAndTheTextScaleIsWhole()
    {
        // The panel is 320 x 40 stretched to the window's width; text at the nearest whole scale.
        Assert.Equal(4, DosHud.PixelScale(1280, 960));
        Assert.Equal(160, DosHud.PanelPixels(1280, 960));
        Assert.Equal(160, DosHud.PanelPixels(1280, 720));
        Assert.Equal(4, DosHud.PixelScale(1269, 700));
        Assert.Equal(156, DosHud.PanelPixels(1269, 700));
        Assert.Equal(6, DosHud.PixelScale(1920, 1080));
        Assert.Equal(240, DosHud.PanelPixels(1920, 1080));
        Assert.Equal(128, DosHud.PanelPixels(1024, 768));
        // A very wide window: the panel stops at 2/9 of the height (the Godot port's cap).
        Assert.Equal(240, DosHud.PanelPixels(2560, 1080));
        Assert.Equal(1, DosHud.PixelScale(200, 100));
        Assert.Equal(0, DosHud.PanelPixels(1280, 960, HudStyle.Modern));
        // The projection centre: the middle of the view above the panel.
        Assert.Equal(new System.Numerics.Vector2(640, 400), DosHud.ViewCentre(1280, 960));
        Assert.Equal(new System.Numerics.Vector2(634, 272), DosHud.ViewCentre(1269, 700));
    }
}

public class StatusQueueTests
{
    [Fact]
    public void LinesLast210TicksOfTheSeventyHertzClock()
    {
        Assert.Equal(3f, StatusQueue.DefaultSeconds, 4);
        var q = new StatusQueue();
        q.Push("A");
        for (var t = 0; t < StatusQueue.LifeTicks - 1; t++)
        {
            q.Tick(1f / StatusQueue.ClockHz);
        }

        q.Push("B");
        Assert.Equal(["A", "B"], q.Lines);
        q.Tick(1f / StatusQueue.ClockHz);
        Assert.Equal(["B"], q.Lines);
    }

    [Fact]
    public void TwentyOneEntriesAndANewLineIsDroppedWhenFull()
    {
        var q = new StatusQueue();
        for (var i = 0; i < StatusQueue.Capacity; i++)
        {
            Assert.True(q.Push($"L{i}"));
        }

        Assert.False(q.Push("LATE"));
        Assert.Equal(21, q.Count);
        Assert.Equal("L0", q.Lines.First());
        Assert.Equal("L20", q.Lines.Last());
    }

    [Fact]
    public void LinesKeepTheirColourAndAtMost39Characters()
    {
        var q = new StatusQueue();
        q.Push("PICKUP");
        q.Push("PLAYER LEFT", StatusQueue.NetworkColour);
        q.Push("OW!", StatusQueue.AsideColour);
        q.Push(new string('X', 50));
        Assert.Equal([("PICKUP", (byte)0xB3), ("PLAYER LEFT", (byte)0x0C), ("OW!", (byte)0x08)], q.Entries.Take(3));
        Assert.Equal(39, q.Lines.Last().Length);
    }

    [Fact]
    public void LinesStepTheFontHeightPlusTwoFromThree()
    {
        // FONT0004 (5 rows) and FONT0010 (11 rows) in the hi-res set.
        Assert.Equal([3, 10, 17], Enumerable.Range(0, 3).Select(i => StatusQueue.LineY(i, 5)));
        Assert.Equal([3, 16, 29], Enumerable.Range(0, 3).Select(i => StatusQueue.LineY(i, 11)));
        Assert.Equal(4, StatusQueue.LineX);
    }
}

public class BitmapFontTests
{
    [Fact]
    public void AdvancesAreWidthPlusSpacingAndSpaceIsEm()
    {
        var font = BitmapFont.Build(5, 6, [new BitmapGlyphSource('A', 4, new byte[20]), new BitmapGlyphSource('I', 1, new byte[5])]);
        Assert.Equal(5, font.Advance('A'));
        Assert.Equal(2, font.Advance('I'));
        Assert.Equal(6, font.Advance(' '));
        Assert.Equal(6, font.Advance('?'));           // missing: blank, the em width
        Assert.Equal(5 + 6 + 2, font.Measure("A I"));
        Assert.True(font.TryGetGlyph('A', out var g));
        Assert.Equal(4, g.Width);
    }

    [Fact]
    public void AtlasHoldsTheGlyphBits()
    {
        var bits = new byte[] { 1, 0, 0, 1 };
        var font = BitmapFont.Build(2, 3, [new BitmapGlyphSource('X', 2, bits)]);
        Assert.True(font.TryGetGlyph('X', out var g));
        Assert.Equal(1, font.AtlasPixels[g.Y * font.AtlasWidth + g.X]);
        Assert.Equal(0, font.AtlasPixels[g.Y * font.AtlasWidth + g.X + 1]);
        Assert.Equal(1, font.AtlasPixels[(g.Y + 1) * font.AtlasWidth + g.X + 1]);
    }
}

public class ReticleShapeTests
{
    /// <summary>The vehicle reticle: vectors at the frame's resolution for the hi-res set and the MODERN HUD
    /// (the 640 × 480 art's shape), the low set's art blown up only for the CLASSIC HUD (DOS's look).</summary>
    [Theory]
    [InlineData(HudArtSet.HiRes, HudStyle.Classic, true, ReticleDraw.Vectors)]
    [InlineData(HudArtSet.HiRes, HudStyle.Modern, true, ReticleDraw.Vectors)]
    [InlineData(HudArtSet.LowRes, HudStyle.Modern, true, ReticleDraw.Vectors)]
    [InlineData(HudArtSet.LowRes, HudStyle.Classic, true, ReticleDraw.ScaledArt)]
    [InlineData(HudArtSet.LowRes, HudStyle.Modern, false, ReticleDraw.ScaledArt)]
    public void The_modern_hud_draws_the_reticle_at_the_frames_resolution(HudArtSet set, HudStyle style, bool hiresArt, ReticleDraw expected) =>
        Assert.Equal(expected, DosHud.ReticleDrawFor(set, style, hiresArt ? HiresArt.Present : HiresArt.Missing));

    [Fact]
    public void ARingBecomesArcsAndACrossBecomesCentredRects()
    {
        const int n = 120;
        var px = new byte[n * n];
        for (var y = 0; y < n; y++)
        {
            for (var x = 0; x < n; x++)
            {
                var d = MathF.Sqrt((x + 0.5f - 60) * (x + 0.5f - 60) + (y + 0.5f - 60) * (y + 0.5f - 60));
                if (MathF.Abs(d - 50f) < 0.5f)
                {
                    px[y * n + x] = 1;
                }
            }
        }

        for (var i = 40; i < 81; i++)
        {
            px[60 * n + i] = 1;                       // a horizontal bar on pixel row 60
        }

        var shape = ReticleShape.From(n, n, px);
        Assert.NotEmpty(shape.Arcs);
        Assert.All(shape.Arcs, a => Assert.InRange(a.Radius, 49f, 51f));
        var bar = Assert.Single(shape.Rects, r => r.Width > 30);
        Assert.Equal(-0.5f, bar.Y);                   // moved half a pixel onto the centre
    }
}
