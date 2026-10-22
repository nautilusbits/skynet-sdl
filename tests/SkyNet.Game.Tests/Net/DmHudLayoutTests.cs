using System.Drawing;
using RetroEng.Render;
using SkyNet.Game.Hud;
using SkyNet.Game.Net;
using SkyNet.Game.Tests.Behaviour;

namespace SkyNet.Game.Tests.Net;

/// <summary>The deathmatch HUD's layout (<see cref="DmHudLayout"/>) on the campaign HUD's text grid,
/// at the usual window sizes and in both HUD art sets: nothing overprints, nothing leaves the view.</summary>
public class DmHudLayoutTests
{
    public static TheoryData<int, int, bool> Screens()
    {
        var data = new TheoryData<int, int, bool>();
        foreach (var (w, h) in new[] { (1280, 720), (1920, 1080), (3440, 1440), (1280, 960), (640, 480) })
        {
            data.Add(w, h, false);
            data.Add(w, h, true);
        }

        return data;
    }

    private static DmHudLayout Layout(int width, int height, bool hiRes)
    {
        var data = BehaviourData.Data;
        var font = HudFont.Load(data, HudFont.FileName(hiRes ? HudFont.HiResFont(HudFont.HudText) : HudFont.HudText))!;
        var big = HudFont.Load(data, "FONT0000.FNT")!;
        var pixel = hiRes ? DosHud.HiResPixelScale(width, height) : DosHud.PixelScale(width, height);
        return new DmHudLayout(font, big, pixel, hiRes ? 2 : 1, width, height);
    }

    private static bool Inside(RectangleF outer, RectangleF r) =>
        r.Left >= outer.Left && r.Top >= outer.Top && r.Right <= outer.Right && r.Bottom <= outer.Bottom;

    [BehaviourDataTheory]
    [MemberData(nameof(Screens))]
    public void The_text_is_the_status_lines_size(int width, int height, bool hiRes)
    {
        var l = Layout(width, height, hiRes);
        // One DOS row is height / 200; a status line is 7 of them (FONT0004's 5 + 2), give or take
        // the whole-number rounding, never the oversized lines of a font of its own.
        Assert.InRange(l.LinePitch, height / 200f * 5f, height / 200f * 9f);
        Assert.InRange(l.Font.LineHeight * l.Pixel, height / 200f * 3f, height / 200f * 7f);
    }

    [BehaviourDataTheory]
    [MemberData(nameof(Screens))]
    public void The_score_lines_neither_overprint_each_other_nor_the_status_lines(int width, int height, bool hiRes)
    {
        var l = Layout(width, height, hiRes);
        var texts = DmHudLayout.ScoreTexts(99, 99, 16, 16, 59 * 60 + 59);
        Assert.Equal(["FRAGS 99/99", "RANK 16/16", "59:59"], texts);
        var screen = new RectangleF(0, 0, width, height);
        var score = texts.Select((t, i) => l.ScoreRect(i, t)).ToList();
        for (var i = 0; i < score.Count; i++)
        {
            Assert.True(Inside(screen, score[i]));
            for (var j = i + 1; j < score.Count; j++)
            {
                Assert.False(score[i].IntersectsWith(score[j]), $"score lines {i} and {j}");
            }

            // A kill-feed line as long as STRINGS.PRS's join line on the same row.
            Assert.False(score[i].IntersectsWith(l.StatusRect(i, "HK-UNIT HAS ENTERED THE GAME.")), $"score line {i} over the status line");
        }
    }

    [BehaviourDataTheory]
    [MemberData(nameof(Screens))]
    public void The_board_and_the_round_end_fit_the_view_above_the_panel(int width, int height, bool hiRes)
    {
        var l = Layout(width, height, hiRes);
        var view = new RectangleF(0, 0, width, l.ViewBottom);
        var names = Enumerable.Range(1, 16).Select(i => $"PLAYER {i}").ToList();
        var board = l.Board(["SOMEBODY'S GAME", "MAP.605 Highway/City   BALANCED CLASSES"], names, names.Count);
        Assert.True(Inside(view, board.Box), $"board {board.Box} outside {view}");
        Assert.True(board.Columns.Zip(board.Columns.Skip(1)).All(c => c.First < c.Second));

        var end = l.RoundEnd(board.Box, "PLAYER 1 REACHED 10000 POINTS", host: true);
        Assert.True(end.Reason.Y + l.Big.LineHeight * l.BigPixel <= board.Box.Top || end.Reason.Y == 0);
        Assert.True(end.KeyAt.Y >= board.Box.Bottom);
        Assert.True(end.MenuAt.Y >= end.KeyAt.Y + l.Font.LineHeight * l.Pixel);
    }

    // Below 720 lines DmHud steps the scale down until the board fits.
    [BehaviourDataTheory]
    [InlineData(1280, 720, false)]
    [InlineData(1280, 720, true)]
    [InlineData(1920, 1080, false)]
    [InlineData(1920, 1080, true)]
    [InlineData(1920, 1200, true)]
    [InlineData(2560, 1440, false)]
    [InlineData(2560, 1440, true)]
    [InlineData(3440, 1440, false)]
    [InlineData(1280, 960, false)]
    public void The_board_at_its_own_scale_fits_six_tenths_of_the_frame(int width, int height, bool hiRes)
    {
        // DmHud's board scale: a DOS pixel is height / 300 screen pixels, whole font pixels.
        var grid = hiRes ? 2 : 1;
        var pixel = Math.Max(1, (int)MathF.Round(height / 300f, MidpointRounding.AwayFromZero) / grid);
        var l = Layout(width, height, hiRes);
        var big = new DmHudLayout(l.Font, l.Big, pixel, grid, width, height);
        var names = Enumerable.Range(1, 16).Select(i => $"PLAYER {i}").ToList();
        string[] heads = ["ICE'S GAME", "MAP.607 Terminator Outpost   BALANCED CLASSES"];
        var box = big.Board(heads, names, names.Count).Box;
        Assert.True(box.Width <= width * 0.6f, $"board {box} wider than 60 % of {width}");
        Assert.True(Inside(new RectangleF(0, 0, width, big.ViewBottom), box), $"board {box} outside the view");
        Assert.True(pixel >= ModernLayout.For(width, height).TextPixel(grid), "smaller than the MODERN HUD's text");
    }

    [BehaviourDataTheory]
    [MemberData(nameof(Screens))]
    public void The_chat_sits_above_the_panel_the_newest_lowest(int width, int height, bool hiRes)
    {
        var l = Layout(width, height, hiRes);
        const int count = 6;
        var box = l.ChatBox("SAY: GOOD GAME_");
        Assert.True(box.Bottom <= l.ViewBottom);
        for (var i = 0; i < count; i++)
        {
            var at = l.ChatLine(i, count);
            Assert.True(at.Y + l.Font.LineHeight * l.Pixel <= box.Top);
            Assert.True(at.Y > l.ScoreLine(3).Y);
            if (i > 0)
            {
                Assert.True(at.Y >= l.ChatLine(i - 1, count).Y + l.Font.LineHeight * l.Pixel);
            }
        }
    }
}
