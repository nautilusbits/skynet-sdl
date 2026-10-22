using System.Drawing;
using System.Globalization;
using System.Numerics;
using RetroEng.Render;
using SkyNet.Game.Hud;

namespace SkyNet.Game.Net;

/// <summary>
/// The deathmatch's read-outs over the view (step 5; dm_game.gd's HUD layer, laid out the way the
/// campaign's HUD text is): the hit flash; FRAGS, RANK and the round's clock at the top right, one
/// to a status line's row; the centre line; the chat log and the line being typed at the bottom left
/// above the panel; the scoreboard (TAB held) and the end-of-round screen (the reason, the board,
/// ENTER for the host's new round). The kill feed is DOS's: the top-left status lines
/// (<see cref="DmGame.Status"/>). A TERMINATOR sees through DOS's machine vision (SKYNET.EXE v1.01
/// FUN_00132b00 and 0x134a39): every other player 50 to 2500 units away whose point falls in the
/// view is marked with the name and the range, through walls, and the whole screen goes through
/// SKYNTTRM.COL (and under BALANCED CLASSES it runs at 0.9 of the pace,
/// <see cref="DmProtocol.SpeedFactor"/>). A HUMAN with the motion detector in hand sees DOS's plot
/// on the scanner's own screen instead (FUN_0011ff00, <see cref="MotionDetector.Scan"/>).
/// <para>Text is the HUD's own (<see cref="DosHud.TextFont"/>) at the status lines' scale and in
/// their palette colours (<see cref="DmHudLayout"/>): FONT0004 on DOS's 320 × 200 grid, or in the
/// hi-res set FONT0010 on the 640 × 480 grid, never a size of its own.</para>
/// </summary>
public sealed class DmHud(Renderer renderer)
{
    public static readonly Vector4 Text = new(0.85f, 1f, 0.9f, 1f), Shadow = new(0f, 0f, 0f, 0.85f), Mine = new(0.56f, 1f, 0.63f, 1f),
        Title = new(0.42f, 0.92f, 0.48f, 1f), BoardFill = new(0.02f, 0.05f, 0.05f, 0.85f), BoardEdge = new(0.35f, 0.6f, 0.55f, 1f),
        Dim = new(0.02f, 0.03f, 0.05f, 0.7f), Flash = new(0.8f, 0.1f, 0.05f, 1f);

    /// <summary>The machine vision's palette: a machine avatar plays with the VGA palette set to
    /// SKYNTTRM.COL instead of SKYNET.COL ([0x53700] := [0x5330d], loaded by FUN_00134a00). Over
    /// SKYNET.COL that palette is c' ≈ 0.70·c + (75, 0, 0) per channel (rms 4.7 / 0.3 / 1.9 over the
    /// 256 colours; black and the pure reds stay), drawn the way the water's palette is
    /// (<see cref="Water.WaterLook.Fit"/>): (1 − α)·c + α·C over the whole screen, HUD included.</summary>
    public const string MachinePaletteFile = "SKYNTTRM.COL";
    public static readonly Vector4 MachinePalette = new(252.4f / 255f, 0.5f / 255f, 1.8f / 255f, 0.298f);

    private float _unit;
    private DmHudLayout _layout = null!;
    private Vector4 _status, _statusShadow;
    private HudStyle _style;

    /// <summary>Queues the read-outs for a <paramref name="width"/> × <paramref name="height"/> pixel
    /// screen, in <paramref name="hud"/>'s text (font, scale, colours). <paramref name="big"/>: the
    /// banner font of the centre line and the round's end. <paramref name="board"/>: TAB is held.
    /// The marks were placed when <paramref name="game"/> was caught (<see cref="Capture"/>).</summary>
    public void Draw(DmHudFrame game, DosHud hud, BitmapFont? big, int width, int height, bool board)
    {
        if (hud.TextFont is not { } font || width <= 0 || height <= 0)
        {
            return;
        }

        _unit = Renderer.CanvasHeight / height;
        _style = hud.Style;
        _layout = new DmHudLayout(font, big ?? font, hud.TextScale(width, height), hud.TextGrid, width, height, hud.BottomPixels(width, height));
        _status = hud.Colour(DosHud.StatusIndex);
        _statusShadow = hud.Colour(DosHud.StatusShadowIndex);

        if (game.HitFlash > 0f)
        {
            Fill(0, 0, width, height, Flash with { W = game.HitFlash });
        }

        if (game.MachineVision)
        {
            Marks(game);
            Fill(0, 0, width, height, MachinePalette);
        }
        else if (game.Scanner is { } plot && game.ScannerAt is { } gun)
        {
            Scanner(plot, gun, hud);
        }

        ScoreLines(game);

        // Not under the board: the board and the round's end have the screen's middle.
        if (game.Centre is { } centre && !board && game.RoundOver == null)
        {
            Say(_layout.Big, centre, _layout.CentreLine, _layout.BigPixel, Text, TextAlign.Centre);
        }

        // The chat log ends above the line being typed, the newest lowest.
        var l = _layout;
        for (var i = 0; i < game.Chat.Count; i++)
        {
            Say(l.Font, game.Chat[i].Text, l.ChatLine(i, game.Chat.Count), l.Pixel, game.Chat[i].Colour);
        }

        if (game.ChatOpen)
        {
            var text = $"SAY: {game.ChatText}_";
            Fill(l.ChatBox(text), BoardFill);
            Say(l.Font, text, l.ChatTyped, l.Pixel, Text);
        }

        if (game.RoundOver is { } reason)
        {
            Fill(0, 0, width, height, Dim);
            var box = Scoreboard(game);
            var end = l.RoundEnd(box, reason, game.IsHost);
            Say(l.Big, reason, end.Reason, l.BigPixel, Title, TextAlign.Centre);
            Say(l.Font, end.Key, end.KeyAt, l.Pixel, Text, TextAlign.Centre);
            Say(l.Font, DmHudLayout.MenuKey, end.MenuAt, l.Pixel, Text, TextAlign.Centre);
        }
        else if (board)
        {
            Scoreboard(game);
        }
    }

    /// <summary>Copies what <see cref="Draw"/> reads of <paramref name="game"/> into <paramref name="frame"/>
    /// for a <paramref name="width"/> × <paramref name="height"/> screen seen through
    /// <paramref name="viewProjection"/> from <paramref name="eye"/> (the marks' places), for a draw on another thread.
    /// <paramref name="gun"/>: where the gun in hand is drawn (<see cref="Weapons.ViewModel.Placed"/>), the
    /// scanner's plot goes on its screen. <paramref name="style"/>: the HUD's (the view's height under the panel).</summary>
    public static void Capture(DmGame game, DmHudFrame frame, int width, int height, in Matrix4x4 viewProjection, Vector3 eye,
        RectangleF? gun, HudStyle style = HudStyle.Classic)
    {
        frame.HitFlash = game.HitFlash;
        frame.MachineVision = game.MachineVision;
        frame.DetectorActive = game.DetectorActive;
        frame.Clock = game.Clock;
        frame.Marks.Clear();
        if (frame.MachineVision)
        {
            frame.Marks.AddRange(MotionDetector.Marks(game.Avatars.Values, eye, viewProjection, width, height - DosHud.PanelPixels(width, height, style), height));
        }

        (frame.Scanner, frame.ScannerAt) = frame.DetectorActive ? (game.Scanner(), gun) : (null, null);

        frame.Rows.Clear();
        foreach (var p in game.Scoreboard())
        {
            frame.Rows.Add(new DmRow(p.Id, p.Name, p.Frags, p.Deaths, p.Points, p.Bot));
        }

        frame.Settings = game.Settings;
        frame.LocalId = game.LocalId;
        frame.IsHost = game.IsHost;
        frame.TimeLeft = game.TimeLeft;
        frame.Centre = game.Centre;
        frame.Chat.Clear();
        frame.Chat.AddRange(game.Chat);
        frame.ChatOpen = game.ChatOpen;
        frame.ChatText = game.ChatText;
        frame.RoundOver = game.RoundOver;
    }

    /// <summary>"FRAGS n[/limit]", "RANK r/N" and the clock (m:ss, timed rounds) at the top right, in
    /// the status lines' colours and rows.</summary>
    private void ScoreLines(DmHudFrame game)
    {
        var rows = game.Rows;
        var rank = rows.FindIndex(p => p.Id == game.LocalId) + 1;
        var lines = DmHudLayout.ScoreTexts(rank > 0 ? rows[rank - 1].Frags : 0, game.Settings.FragLimit, rank, rows.Count,
            game.Settings.TimeLimitMinutes > 0 ? game.TimeLeft : null);
        for (var i = 0; i < lines.Count; i++)
        {
            TextBatcher.DrawShadowed(renderer, _layout.Font, lines[i], _layout.ScoreLine(i) * _unit, _layout.Pixel * _unit,
                _status, _statusShadow, align: TextAlign.Right);
        }
    }

    /// <summary>The board (dm_game.gd _refresh_scores): the game's name, arena and CLASSES, then PLAYER /
    /// FRAGS / DEATHS / POINTS (NETLEVEL.PRS scoring, DOS's ranking for the goal) with BOT on the
    /// bots, this machine's row in green. Returns its rectangle in screen pixels.</summary>
    private RectangleF Scoreboard(DmHudFrame game)
    {
        var rows = game.Rows;
        var s = game.Settings;
        var c = CultureInfo.InvariantCulture;
        string[] heads = [s.ServerName, string.Create(c, $"MAP.{s.Map:000} {s.Arena}   {DmProtocol.RulesName(s.Classes)} CLASSES")];
        var (l, board) = BoardScaled(heads, rows.Select(p => p.Name).ToList(), rows.Count);
        Fill(board.Box, BoardFill);
        renderer.FrameRect(Scale(board.Box), l.Dos * _unit, BoardEdge);
        var ty = board.Top;
        foreach (var head in heads)
        {
            Say(l.Font, head, new Vector2(board.Columns[0], ty), l.Pixel, Title);
            ty += l.BoardPitch;
        }

        string[] titles = ["PLAYER", "FRAGS", "DEATHS", "POINTS"];
        for (var i = 0; i < titles.Length; i++)
        {
            Say(l.Font, titles[i], new Vector2(board.Columns[i], ty), l.Pixel, Title);
        }

        foreach (var p in rows)
        {
            ty += l.BoardPitch;
            var colour = p.Id == game.LocalId ? Mine : Text;
            string[] cells = [p.Name, p.Frags.ToString(c), p.Deaths.ToString(c), p.Points.ToString(c), p.Bot ? "BOT" : ""];
            for (var i = 0; i < cells.Length; i++)
            {
                Say(l.Font, cells[i], new Vector2(board.Columns[i], ty), l.Pixel, colour);
            }
        }

        return board.Box;
    }

    /// <summary>Frame lines per board DOS pixel (screen pixels per DOS pixel = height / this, whole):
    /// 2 at 720 lines, 4 at 1080, 5 at 1440. The status lines' scale is too small for 16 players.</summary>
    private const float BoardLinesPerDos = 300f;

    /// <summary>The widest share of the frame the board may take.</summary>
    private const float BoardMaxWidth = 0.6f;

    /// <summary>The board's layout at its own text scale: one DOS pixel <see cref="BoardLinesPerDos"/>
    /// of the height, a whole number of font pixels (crisp), never below the status lines' and stepped
    /// down until the board fits <see cref="BoardMaxWidth"/> and the view above the panel.</summary>
    private (DmHudLayout Layout, BoardLayout Board) BoardScaled(string[] heads, List<string> names, int rows)
    {
        var l = _layout;
        var grid = l.Dos / l.Pixel;
        var dos = Math.Max(1, (int)MathF.Round(l.Height / BoardLinesPerDos, MidpointRounding.AwayFromZero));
        var bottom = l.Height - l.ViewBottom;
        for (var pixel = dos / grid; pixel > l.Pixel; pixel--)
        {
            var big = new DmHudLayout(l.Font, l.Big, pixel, grid, l.Width, l.Height, bottom);
            var board = big.Board(heads, names, rows);
            if (board.Box.Width <= l.Width * BoardMaxWidth && board.Box.Bottom <= l.ViewBottom)
            {
                return (big, board);
            }
        }

        return (l, l.Board(heads, names, rows));
    }

    /// <summary>FUN_00132b00's marks (<see cref="MotionDetector.Marks"/>): a dot with a black edge
    /// on every player in reach, the name and the range beside it in palette 0xb8's green, lit while
    /// under the crosshair (FUN_00011da0); a dead body reads "NAME TERMINATED". The offsets are DOS's
    /// 320 × 200 pixels scaled to the screen (doubled on the hi-res grid as DOS doubles them).</summary>
    private void Marks(DmHudFrame game)
    {
        var (width, height) = (_layout.Width, _layout.Height);
        var k = (float)_layout.Dos;
        var crosshair = DosHud.ViewCentre(width, height, _style);
        var half = MathF.Max(MotionDetector.DotDosPixels * height / 200f * 0.5f, 3f);
        foreach (var m in game.Marks)
        {
            var (x, y) = (m.At.X, m.At.Y);
            if (m.Dead)
            {
                Say(_layout.Font, $"{m.Name} TERMINATED", new Vector2(x - 48f * k, y), _layout.Pixel, MotionDetector.Colour);
                continue;
            }

            var c = MotionDetector.Flashes(m.At, crosshair, width, height, game.Clock) ? MotionDetector.Lit : MotionDetector.Colour;
            Fill(new RectangleF(x - half - 1f, y - half - 1f, half * 2f + 2f, half * 2f + 2f), Shadow);
            Fill(new RectangleF(x - half, y - half, half * 2f, half * 2f), c);
            Say(_layout.Font, m.Name, new Vector2(x + 4f * k, y - 6f * k), _layout.Pixel, c); // DOS places text by its top-left corner
            Say(_layout.Font, MotionDetector.Range(m.Distance), new Vector2(x + 4f * k, y + 2f * k), _layout.Pixel, c);
        }
    }

    /// <summary>FUN_0011ff00's plot over the scanner's art at <paramref name="gun"/> (screen pixels):
    /// a palette 0xb3 pixel for each blip, the target's in 0x0c and its name centred on the bar
    /// under the screen, one pixel of the low frame being <see cref="Weapons.ViewModelLayout.Scale"/>.</summary>
    private void Scanner(ScannerPlot plot, RectangleF gun, DosHud hud)
    {
        var s = Weapons.ViewModelLayout.Scale(_layout.Width, _layout.Height);
        var (blip, target) = (hud.Colour(MotionDetector.BlipIndex), hud.Colour(MotionDetector.TargetIndex));
        foreach (var b in plot.Blips)
        {
            Fill(gun.X + b.At.X * s, gun.Y + b.At.Y * s, s, s, b.Target ? target : blip);
        }

        if (plot.Target is not { } name)
        {
            return;
        }

        var at = new Vector2(gun.X + (MotionDetector.NameAt.X + 0.5f) * s, gun.Y + MotionDetector.NameAt.Y * s);
        Say(_layout.Font, name, at, Math.Max(1, (int)MathF.Round(s / hud.TextGrid)), target, TextAlign.Centre);
    }

    private RectangleF Scale(RectangleF r) => new(r.X * _unit, r.Y * _unit, r.Width * _unit, r.Height * _unit);

    private void Fill(float x, float y, float w, float h, Vector4 colour) => Fill(new RectangleF(x, y, w, h), colour);

    private void Fill(RectangleF r, Vector4 colour) => renderer.FillRect(Scale(r), colour);

    private void Say(BitmapFont font, string text, Vector2 at, int pixel, Vector4 colour, TextAlign align = TextAlign.Left) =>
        TextBatcher.DrawShadowed(renderer, font, text, new Vector2(MathF.Round(at.X), MathF.Round(at.Y)) * _unit, pixel * _unit, colour, Shadow,
            align: align);
}

/// <summary>Where <see cref="DmHud"/> puts its text, in screen pixels, on the campaign HUD's grid:
/// <see cref="Pixel"/> screen pixels per font pixel (the status lines' scale,
/// <see cref="DosHud.TextScale"/>) and <see cref="Dos"/> per DOS 320 × 200 pixel (the font's
/// pixel times its grid: 2 on the hi-res 640 × 480 grid, as FUN_0012ad2e doubles DOS's positions).
/// The banner font (the centre line, the round's end) is drawn at one DOS pixel a font pixel, as
/// the campaign's banner text is. Nothing here depends on the renderer, so the layout is checked
/// at any screen size without one.</summary>
public sealed class DmHudLayout
{
    /// <summary>The DOS pixels between the board's columns and round its text.</summary>
    public const int ColumnGap = 10, BoardPad = 4;

    public const string MenuKey = "ESC  MENU";

    /// <param name="bottom">Screen pixels the HUD keeps at the bottom (<see cref="DosHud.BottomPixels"/>); null: the panel's.</param>
    public DmHudLayout(BitmapFont font, BitmapFont big, int pixel, int grid, int width, int height, int? bottom = null)
    {
        (Font, Big, Pixel, Width, Height) = (font, big, Math.Max(1, pixel), width, height);
        Dos = Pixel * Math.Max(1, grid);
        BigPixel = DosHud.PixelScale(width, height);
        ViewBottom = height - (bottom ?? DosHud.PanelPixels(width, height));
    }

    public BitmapFont Font { get; }
    public BitmapFont Big { get; }
    public int Pixel { get; }
    public int Dos { get; }
    public int BigPixel { get; }
    public int Width { get; }
    public int Height { get; }

    /// <summary>The bottom of the view: the panel's top edge.</summary>
    public int ViewBottom { get; }

    /// <summary>A status line's pitch in screen pixels (StatusQueue.LineY: the font's height + 2).</summary>
    public int LinePitch => (Font.LineHeight + 2) * Pixel;

    /// <summary>The board's row pitch: a status line's, so sixteen players and the two title rows fit
    /// the view above the panel.</summary>
    public int BoardPitch => LinePitch;

    /// <summary>The score lines' texts: FRAGS n[/limit], RANK r/N and, in a timed round, the clock.</summary>
    public static List<string> ScoreTexts(int frags, int limit, int rank, int players, double? timeLeft)
    {
        var c = CultureInfo.InvariantCulture;
        var lines = new List<string>
        {
            string.Create(c, $"FRAGS {frags}{(limit > 0 ? $"/{limit}" : "")}"),
            string.Create(c, $"RANK {rank}/{players}"),
        };
        if (timeLeft is { } t)
        {
            var s = Math.Max(0, (int)Math.Ceiling(t));
            lines.Add(string.Create(c, $"{s / 60}:{s % 60:00}"));
        }

        return lines;
    }

    /// <summary>Score line <paramref name="index"/>'s top-right corner: on status line
    /// <paramref name="index"/>'s row, as far from the right edge as the status lines are from the left.</summary>
    public Vector2 ScoreLine(int index) =>
        new(Width - StatusQueue.LineX * Pixel, StatusQueue.LineY(index, Font.LineHeight) * Pixel);

    /// <summary>The rectangle a score line covers (its shadow included).</summary>
    public RectangleF ScoreRect(int index, string text)
    {
        var at = ScoreLine(index);
        var w = (Font.Measure(text) + 1) * Pixel;
        return new RectangleF(at.X - w + Pixel, at.Y, w, (Font.LineHeight + 1) * Pixel);
    }

    /// <summary>A status line's rectangle (DosHud.DrawStatus's place for it).</summary>
    public RectangleF StatusRect(int index, string text) =>
        new(StatusQueue.LineX * Pixel, StatusQueue.LineY(index, Font.LineHeight) * Pixel, (Font.Measure(text) + 1) * Pixel, (Font.LineHeight + 1) * Pixel);

    /// <summary>The centre line's top-centre: DOS row 40, a fifth of the screen down.</summary>
    public Vector2 CentreLine => new(MathF.Floor(Width / 2f), 40 * BigPixel);

    /// <summary>Chat line <paramref name="index"/> of <paramref name="count"/>: at DOS x 4, the newest
    /// lowest, above the line being typed, above the panel.</summary>
    public Vector2 ChatLine(int index, int count) =>
        new(StatusQueue.LineX * Dos, ChatTyped.Y - (count - index) * LinePitch - 2 * Dos);

    /// <summary>The line being typed: its text's top-left corner.</summary>
    public Vector2 ChatTyped => new(StatusQueue.LineX * Dos + 2 * Dos, ViewBottom - 4 * Dos - Font.LineHeight * Pixel);

    /// <summary>The box behind the line being typed.</summary>
    public RectangleF ChatBox(string text)
    {
        var at = ChatTyped;
        var w = MathF.Max(160 * Dos, (Font.Measure(text) + 1) * Pixel + 4 * Dos);
        return new RectangleF(at.X - 2 * Dos, at.Y - 2 * Dos, MathF.Min(w, Width - at.X), Font.LineHeight * Pixel + 4 * Dos);
    }

    /// <summary>The board: the box and its column lefts (PLAYER, FRAGS, DEATHS, POINTS, BOT) and
    /// first text row, centred in the view above the panel, each column as wide as its widest cell:
    /// the <paramref name="heads"/> lines (the game's name; the arena and the CLASSES), the column
    /// titles, then <paramref name="rows"/> players.</summary>
    public BoardLayout Board(IReadOnlyList<string> heads, IEnumerable<string> names, int rows)
    {
        var name = Math.Max(Font.Measure("PLAYER"), names.Select(n => Font.Measure(n)).DefaultIfEmpty(0).Max());
        int[] widths = [name, Font.Measure("FRAGS"), Font.Measure("DEATHS"), Font.Measure("POINTS"), Font.Measure("BOT")];
        var columns = new float[widths.Length];
        var inner = 0f;
        for (var i = 0; i < widths.Length; i++)
        {
            columns[i] = inner;
            inner += widths[i] * Pixel + (i < widths.Length - 1 ? ColumnGap * Dos : 0);
        }

        inner = MathF.Max(inner, heads.Select(h => Font.Measure(h) * Pixel).DefaultIfEmpty(0).Max());
        var w = inner + 2 * BoardPad * Dos;
        var h = BoardPitch * (rows + heads.Count + 1) - 2 * Pixel + 2 * BoardPad * Dos;
        var x = MathF.Round((Width - w) / 2f);
        var y = MathF.Round(MathF.Max(0f, (ViewBottom - h) / 2f));
        for (var i = 0; i < columns.Length; i++)
        {
            columns[i] += x + BoardPad * Dos;
        }

        return new BoardLayout(new RectangleF(x, y, w, h), columns, y + BoardPad * Dos);
    }

    /// <summary>The round's end round the board: the reason (banner font) above it, ENTER (or the
    /// wait) and ESC under it.</summary>
    public RoundEndLayout RoundEnd(RectangleF board, string reason, bool host)
    {
        var mid = MathF.Floor(Width / 2f);
        var reasonY = MathF.Max(0f, board.Top - (Big.LineHeight + 4) * BigPixel);
        var keyY = board.Bottom + 4 * Dos;
        return new RoundEndLayout(new Vector2(mid, reasonY), host ? "ENTER  NEW ROUND" : "WAITING FOR THE HOST",
            new Vector2(mid, keyY), new Vector2(mid, keyY + LinePitch));
    }
}

/// <summary>The board's box, its column lefts and its first row's top (screen pixels).</summary>
public readonly record struct BoardLayout(RectangleF Box, float[] Columns, float Top);

/// <summary>The round end's three lines (top-centre points, screen pixels) and the key line's text.</summary>
public readonly record struct RoundEndLayout(Vector2 Reason, string Key, Vector2 KeyAt, Vector2 MenuAt);

/// <summary>A scoreboard row as caught for a frame (<see cref="DmPlayer"/>).</summary>
public readonly record struct DmRow(int Id, string Name, int Frags, int Deaths, int Points, bool Bot);

/// <summary>What <see cref="DmHud"/> draws of a <see cref="DmGame"/> for one frame (<see cref="DmHud.Capture"/>),
/// pooled: drawn on another thread while the next tick runs (engine-threads plan B1).</summary>
public sealed class DmHudFrame
{
    public float HitFlash { get; internal set; }
    public bool MachineVision { get; internal set; }
    public bool DetectorActive { get; internal set; }
    public double Clock { get; internal set; }
    public List<DetectorMark> Marks { get; } = [];
    /// <summary>The scanner's plot and where its gun is drawn, while it is in hand.</summary>
    public ScannerPlot? Scanner { get; internal set; }
    public RectangleF? ScannerAt { get; internal set; }
    public List<DmRow> Rows { get; } = [];
    public DmSettings Settings { get; internal set; } = new();
    public int LocalId { get; internal set; }
    public bool IsHost { get; internal set; }
    public double TimeLeft { get; internal set; }
    public string? Centre { get; internal set; }
    public List<DmGame.Line> Chat { get; } = [];
    public bool ChatOpen { get; internal set; }
    public string ChatText { get; internal set; } = "";
    public string? RoundOver { get; internal set; }
}