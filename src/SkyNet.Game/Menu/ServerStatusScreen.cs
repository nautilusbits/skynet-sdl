using System.Drawing;
using System.Globalization;
using RetroEng.Render;
using SkyNet.Game.Net;
using static SkyNet.Game.Menu.MenuLayout;

namespace SkyNet.Game.Menu;

/// <summary>
/// SERVER STATUS (the host screen's START with DEDICATED Y): a dedicated server running inside the
/// game, with no player of its own, drawn in the DOS panel kit (OPTIONS.IMG's stone and wells, as
/// MODS and DISPLAY): the server's name and port, the arena and the rotation, the time left, the
/// players (kills, deaths, points, idle), the last log lines; NEXT MAP and QUIT. The screen keeps the
/// server going (its <see cref="Tick"/> is the server's frame); QUIT stops it for everybody and goes
/// back to the host screen, Back or Esc ask first (STOP THE SERVER? YES / NO, the server running on
/// under the box). A PUBLIC server shows what the master server says of it.
/// </summary>
public sealed class ServerStatusScreen : PanelScreen
{
    public static readonly Size PanelSize = new(300, 186);
    public const int Rows = 6;
    public const int LogRows = 4;
    public static readonly Rectangle InfoRect = new(6, 20, 288, 24);
    public static readonly Rectangle PlayersRect = new(6, 47, 288, Rows * 10 + 4);
    public static readonly Rectangle LogRect = new(6, 115, 288, LogRows * 10 + 4);
    public static readonly Rectangle NextRect = new(6, 164, 90, 18);
    public static readonly Rectangle QuitRect = new(PanelSize.Width - OptionsExit.Width - 6, 164, OptionsExit.Width + 6, 18);

    private readonly DmDedicated _server;
    private bool _quit;

    public ServerStatusScreen(DmDedicated server) : base(OptionsImage, PanelSize, DetailScale) => _server = server;

    public override string Name => "serverstatus";

    public DmDedicated Server => _server;

    public override void Opened()
    {
        if (Items.Count > 0)
        {
            return;
        }

        Add(new MenuItem("next", At(NextRect)) { Activate = () => _server.Next() });
        Add(new MenuItem("quit", At(QuitRect)) { Activate = Quit });
        Highlight = 1;
    }

    /// <summary>The server's frame: the wire, the rules, the level, the rotation.</summary>
    public override void Tick(float seconds)
    {
        _server.Update(seconds);
        if (_server.Stopped)
        {
            Quit();
        }
    }

    public override void Back() =>
        Menu.Push(ConfirmBox.StopServer(seconds => _server.Update(seconds), () =>
        {
            Menu.Pop();
            Quit();
        }));

    private void Quit()
    {
        if (_quit)
        {
            return;
        }

        _quit = true;
        _server.Dispose();
        Menu.Actions.StopDedicated();
        Menu.Pop();
    }

    /// <summary>The info box's two lines.</summary>
    public (string Top, string Bottom) Info()
    {
        var s = _server.Server.Settings;
        var state = _server.Intermission is { } wait ? string.Create(CultureInfo.InvariantCulture, $"NEXT MAP IN {wait:0}")
            : s.RoundSeconds > 0 ? string.Create(CultureInfo.InvariantCulture, $"{(int)_server.Server.TimeLeft / 60}:{(int)_server.Server.TimeLeft % 60:00} LEFT")
            : "NO TIME LIMIT";
        var top = string.Create(CultureInfo.InvariantCulture, $"{_server.Config.Name.ToUpperInvariant()}  PORT {_server.Port}{PublicTag()}");
        var bottom = string.Create(CultureInfo.InvariantCulture,
            $"MAP.{s.Map:000} {_server.Arena.ToUpperInvariant()} ({_server.RotationIndex + 1}/{_server.Config.Rotation.Length})  {s.Goal.ToString().ToUpperInvariant()} {s.GoalLimit}  {state}");
        return (top, bottom);
    }

    /// <summary>The master server's word on a PUBLIC server, for the info box.</summary>
    public string PublicTag()
    {
        if (_server.Host.Master is not { } m)
        {
            return _server.Config.Public ? "  NOT LISTED" : "";
        }

        return m.State switch
        {
            RetroEng.Net.Master.MasterEntryState.Listed => m.Reach == RetroEng.Net.Master.MasterReach.Direct ? "  LISTED" : "  LISTED (NAT)",
            RetroEng.Net.Master.MasterEntryState.Refused => "  MASTER REFUSED",
            RetroEng.Net.Master.MasterEntryState.Pending => "  MASTER CHECKING",
            _ => m.Master == null ? "  MASTER LOOKUP" : "  MASTER SILENT",
        };
    }

    public override void Draw(MenuPainter p)
    {
        DosDialog.Panel(p, new Rectangle(Origin, Size));
        p.TextIn(DosDialog.TitleFont, "SERVER STATUS", At(new Rectangle(0, 4, Size.Width, 14)), MenuPainter.TextPale);
        var (top, bottom) = Info();
        var info = At(InfoRect);
        DosDialog.Box(p, info);
        p.Text(DosDialog.Font, top, info.X + 4, info.Y + 3, MenuPainter.TextGreen);
        p.Text(DosDialog.Font, bottom, info.X + 4, info.Y + 13, MenuPainter.TextPale);

        var list = At(PlayersRect);
        DosDialog.Box(p, list);
        var board = DmServer.Rank(_server.Server.Players.Values, _server.Server.Settings.Goal);
        var humans = _server.Server.Humans;
        p.Text(DosDialog.Font, string.Create(CultureInfo.InvariantCulture, $"{humans}/{_server.Server.Settings.MaxPlayers} PLAYERS  {_server.Server.BotCount} BOTS"),
            list.Right - 4, list.Y + 3, ModsScreen.Greyed, TextAlign.Right);
        if (board.Count == 0)
        {
            p.Text(DosDialog.Font, "WAITING FOR PLAYERS", list.X + 4, list.Y + 3, ModsScreen.Greyed);
        }

        for (var i = 0; i < Math.Min(Rows - 1, board.Count); i++)
        {
            var pl = board[i];
            var y = list.Y + 13 + i * 10;
            p.Text(DosDialog.Font, pl.Name.ToUpperInvariant(), list.X + 4, y, pl.Bot ? ModsScreen.Greyed : MenuPainter.TextPale);
            var score = string.Create(CultureInfo.InvariantCulture, $"{pl.Frags} K  {pl.Deaths} D  {pl.Points} P");
            p.Text(DosDialog.Font, score, list.X + 150, y, MenuPainter.TextGreen);
            var where = pl.Bot ? "BOT" : _server.Host.AddressOf(pl.Id)?.Address.ToString() ?? "";
            p.Text(DosDialog.Font, where, list.Right - 4, y, ModsScreen.Greyed, TextAlign.Right);
        }

        var log = At(LogRect);
        DosDialog.Box(p, log);
        var lines = _server.Lines;
        for (var i = 0; i < Math.Min(LogRows, lines.Count); i++)
        {
            var line = lines[lines.Count - Math.Min(LogRows, lines.Count) + i].ToUpperInvariant();
            p.Text(DosDialog.Font, Fit(p, line, log.Width - 8), log.X + 4, log.Y + 3 + i * 10, ModsScreen.Greyed);
        }

        DosDialog.Button(p, At(NextRect), "NEXT MAP");
        DosDialog.Button(p, At(QuitRect), "QUIT");
        if (Highlighted is { } hl && Menu.Current == this)
        {
            p.Highlight(hl);
        }
    }

    private static string Fit(MenuPainter p, string text, int width)
    {
        if (p.Font(DosDialog.Font) is not { } font)
        {
            return text;
        }

        while (text.Length > 1 && font.Measure(text) > width)
        {
            text = text[..^1];
        }

        return text;
    }
}
