using System.Drawing;
using RetroEng.Render;
using SkyNet.Formats;
using SkyNet.Game.Storage;
using static SkyNet.Game.Menu.MenuLayout;

namespace SkyNet.Game.Menu;

/// <summary>
/// UNKNOWN GAME DATA (issue #20): over the title once a run when a game's data is a release the
/// port does not know (<see cref="MenuContext.UnknownData"/>). It names the game and the files
/// that differ; CONTINUE (or Esc) plays on, IGNORE plays on and stops warning of this data
/// (<see cref="MenuSettings.IgnoredData"/>), QUIT quits. The log lists every file's size and hash.
/// </summary>
public sealed class DataNotice(IReadOnlyList<DataCheck> unknown) : PanelScreen(OptionsImage, PanelSize, DetailScale)
{
    public static readonly Size PanelSize = new(260, 132);
    public static readonly Rectangle TextRect = new(10, 22, 240, 84);
    public static readonly Rectangle ContinueRect = new(8, 110, 78, 18);
    public static readonly Rectangle IgnoreRect = new(91, 110, 78, 18);
    public static readonly Rectangle QuitRect = new(PanelSize.Width - 86, 110, 78, 18);

    public const string Title = "UNKNOWN GAME DATA";

    /// <summary>The most file names a game's line lists before "AND N MORE".</summary>
    private const int ListedFiles = 4;

    private const string Advice =
        "IT MAY DRAW OR PLAY WRONG. THE LOG (LOGS/SKYNET.LOG) LISTS EVERY FILE'S SIZE AND HASH: " +
        "SEND IT TO US TO ADD THIS VERSION. IGNORE: NO MORE WARNINGS FOR THIS DATA.";

    public override string Name => "data";

    /// <summary>The unknown data's identity (the games and their files' hashes, 16 hex digits):
    /// IGNORE keeps it, and other data warns again.</summary>
    public static string Key(IReadOnlyList<DataCheck> unknown)
    {
        var files = unknown.SelectMany(c => c.Files.Select(f => $"{c.Game}:{f.Name}:{f.Sha256}")).Order(StringComparer.Ordinal);
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join(";", files)));
        return Convert.ToHexStringLower(hash)[..16];
    }

    /// <summary>The notice's text, e.g. "FUTURE SHOCK DATA IS A VERSION THIS GAME DOES NOT KNOW
    /// (MDMDMAPS.BSA DIFFERS). IT MAY ...".</summary>
    public static string Text(IReadOnlyList<DataCheck> unknown) =>
        string.Join(" ", unknown.Select(Line)) + " " + Advice;

    // One game's sentence, its list cut at ListedFiles.
    private static string Line(DataCheck check)
    {
        var game = check.Game == GameKind.FutureShock ? "FUTURE SHOCK" : "SKYNET";
        var names = string.Join(", ", check.Differs.Take(ListedFiles));
        var more = check.Differs.Count - ListedFiles;
        if (more > 0)
        {
            names += $" AND {more} MORE";
        }

        var verb = check.Differs.Count == 1 ? "DIFFERS" : "DIFFER";
        return $"{game} DATA IS A VERSION THIS GAME DOES NOT KNOW ({names} {verb}).";
    }

    public override void Opened()
    {
        if (Items.Count > 0)
        {
            return;
        }

        Add(new MenuItem("continue", At(ContinueRect)) { Activate = Menu.Pop });
        Add(new MenuItem("ignore", At(IgnoreRect)) { Activate = Ignore });
        Add(new MenuItem("quit", At(QuitRect)) { Activate = Menu.Actions.Quit });
        Highlight = 0;
    }

    public override void Back() => Menu.Pop();

    // No more warnings for this data: saved with the settings.
    private void Ignore()
    {
        Menu.Settings.IgnoredData = Key(unknown);
        Menu.Changed();
        Menu.Pop();
    }

    public override void Draw(MenuPainter p)
    {
        DosDialog.Panel(p, new Rectangle(Origin, Size));
        p.TextIn(DosDialog.TitleFont, Title, At(new Rectangle(0, 4, Size.Width, 14)), MenuPainter.TextPale);
        if (p.Font(DosDialog.Font) is { } font)
        {
            var r = At(TextRect);
            var y = r.Y;
            foreach (var line in ModsScreen.Wrap(font, Text(unknown), r.Width))
            {
                if (y + font.LineHeight > r.Bottom)
                {
                    break;
                }

                p.Text(DosDialog.Font, line, r.X + r.Width / 2f, y, MenuPainter.TextGreen, TextAlign.Centre);
                y += font.LineHeight + 2;
            }
        }

        DosDialog.Button(p, At(ContinueRect), "CONTINUE");
        DosDialog.Button(p, At(IgnoreRect), "IGNORE");
        DosDialog.Button(p, At(QuitRect), "QUIT");
        if (Highlighted is { } h && Menu.Current == this)
        {
            p.Highlight(h);
        }
    }
}
