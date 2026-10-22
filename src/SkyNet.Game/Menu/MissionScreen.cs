using System.Drawing;
using System.Numerics;
using RetroEng.Render;
using SkyNet.Formats;
using SkyNet.Game.Hud;
using static SkyNet.Game.Menu.MenuLayout;

namespace SkyNet.Game.Menu;

/// <summary>What the mission screen shows: the mission, its start map (the BRIEF&lt;map&gt;.IMG
/// picture) and its briefing script; the STATISTICS header and rows when the caller has them
/// (<see cref="Campaign.GameSession.BriefingStats"/>, <see cref="Campaign.GameSession.StatsHeader"/>,
/// <see cref="Campaign.GameSession.StatsRows"/>).</summary>
public sealed record MissionBriefing(GameKind Game, int Mission, int Map, Briefing? Briefing, bool Restart = false)
{
    public IReadOnlyList<(string Label, string Value)> Statistics { get; init; } = [];
    /// <summary>The line over the rows (Stat_Header), or Stat_NotAvail when there are none; empty:
    /// <see cref="NoStatistics"/>.</summary>
    public string StatisticsHeader { get; init; } = "";
    /// <summary>The name and call sign the briefing's #N / #C codes stand for (Future Shock's
    /// briefings use them; FUN_0012daa8 from the briefing screen 0x12c800).</summary>
    public Campaign.Pilot Pilot { get; init; } = Campaign.Pilot.None;
}

/// <summary>One page of the BRIEFING tab: its picture (.IMG and palette), title and text.</summary>
public readonly record struct BriefingPage(string Picture, string Palette, string Title, string Text);

/// <summary>
/// The mission screen (FUN_0012c300): the BUTTON04..11 tabs BEGIN / BRIEFING / TACTICAL /
/// STATISTICS across the top, the picture under them, MENU000.IMG at the bottom with the text and
/// its up / down arrows. BRIEFING pages through the objectives, then one page per dialogue turn
/// with the speaker's portrait; TACTICAL shows the [TA] dossiers, each unit's model spinning over
/// TACTBAK.IMG; STATISTICS the caller's rows. Left / right pick a tab, Accept opens it
/// (BEGIN starts the mission), up / down scroll the text and turn the pages, Esc goes to the title.
/// </summary>
public sealed class MissionScreen : MenuScreen
{
    public const string TextFont = "FONT0000.FNT";
    public const string StatsFont = "FONT0003.FNT";
    /// <summary>STRINGS.PRS Stat_NotAvail, for a caller that gives no header.</summary>
    public const string NoStatistics = "STATS ARE NOT YET AVAILABLE.";

    /// <summary>The STATISTICS page's layout (FUN_00134be5, words at 0x536dc..0x536f4, screen
    /// pixels): the header at (15, 25) wrapped to 320 − 2·15; the mission's two rows from (15, 60),
    /// the career's two from (15, 100), the second of each pair a line and 3 pixels lower; each
    /// value 220 right of its label.</summary>
    public static readonly Point StatsHeaderAt = new(15, 25);
    public static readonly Point StatsMissionAt = new(15, 60);
    public static readonly Point StatsCareerAt = new(15, 100);
    public const int StatsValueOffset = 220;
    public const int StatsRowGap = 3;

    private readonly MissionBriefing _briefing;
    private int _lineCount;
    private int _visibleLines = 5;
    private double _clock;

    public MissionScreen(MissionBriefing briefing)
    {
        _briefing = briefing;
        Pages = BuildPages(briefing);
        Dossiers = briefing.Briefing?.Tactical ?? [];
    }

    public override string Name => "mission";

    public IReadOnlyList<BriefingPage> Pages { get; }

    public IReadOnlyList<string> Dossiers { get; }

    public MissionTab Tab { get; private set; } = MissionTab.Briefing;

    public int Page { get; private set; }

    /// <summary>The first text line shown on the page.</summary>
    public int Scroll { get; private set; }

    public int Dossier { get; private set; }

    public override bool OverGame => false;

    public override void Tick(float seconds)
    {
        base.Tick(seconds);
        _clock += seconds;
    }

    public static IReadOnlyList<BriefingPage> BuildPages(MissionBriefing m)
    {
        var pages = new List<BriefingPage>();
        // SkyNET draws BRIEF<map>.IMG in BRIEF2.COL; Future Shock the [NNN] picture in BRIEF.COL.
        var (scene, scenePal) = m.Game == GameKind.FutureShock && m.Briefing is { Picture: >= 0 } b
            ? ($"BRIEF{b.Picture:D3}.IMG", BriefPalette)
            : ($"BRIEF{m.Map:D3}.IMG", ScenePalette);
        if (m.Briefing is not { } brief)
        {
            pages.Add(new BriefingPage(scene, scenePal, $"MISSION {m.Mission}", ""));
            return pages;
        }

        if (brief.Objectives.Length > 0)
        {
            pages.Add(new BriefingPage(scene, scenePal, $"MISSION {m.Mission}  OBJECTIVES{(m.Restart ? " - RESTART" : "")}", m.Pilot.Expand(brief.Objectives)));
        }

        foreach (var line in brief.Lines.Where(l => l.Text.Length > 0))
        {
            pages.Add(new BriefingPage($"BRIEF{line.Speaker:D3}.IMG", BriefPalette, "INCOMING TRANSMISSION", m.Pilot.Expand(line.Text)));
        }

        if (pages.Count == 0)
        {
            pages.Add(new BriefingPage(scene, scenePal, $"MISSION {m.Mission}", ""));
        }

        return pages;
    }

    public override void Opened()
    {
        if (Items.Count > 0)
        {
            return;
        }

        foreach (var (tab, rect, _, _) in BriefTabs)
        {
            var t = tab;
            Add(new MenuItem(tab.ToString().ToLowerInvariant(), rect) { Activate = () => Open(t) });
        }

        Add(new MenuItem("up", BriefUp) { Activate = () => Step(-1) });
        Add(new MenuItem("down", BriefDown) { Activate = () => Step(1) });
        Add(new MenuItem("picture", BriefPicture) { Activate = () => NextDossier(1) });
        Highlight = 0; // BEGIN: Enter starts the mission, as in the Godot port.
    }

    public void Open(MissionTab tab)
    {
        if (tab == MissionTab.Begin)
        {
            Menu.BeginMission();
            return;
        }

        Tab = tab;
        Scroll = 0;
    }

    /// <summary>Down / up: scrolls the page's text, past its end to the next page (TACTICAL: the next dossier).</summary>
    public void Step(int dir)
    {
        if (Tab == MissionTab.Tactical)
        {
            NextDossier(dir);
            return;
        }

        if (Tab != MissionTab.Briefing)
        {
            return;
        }

        var maxScroll = Math.Max(0, _lineCount - _visibleLines);
        if (dir > 0 && Scroll < maxScroll)
        {
            Scroll++;
        }
        else if (dir < 0 && Scroll > 0)
        {
            Scroll--;
        }
        else if (dir > 0 && Page < Pages.Count - 1)
        {
            (Page, Scroll, _lineCount) = (Page + 1, 0, 0);
        }
        else if (dir < 0 && Page > 0)
        {
            (Page, Scroll, _lineCount) = (Page - 1, 0, 0);
        }
    }

    private void NextDossier(int dir)
    {
        if (Tab == MissionTab.Tactical && Dossiers.Count > 0)
        {
            Dossier = ((Dossier + dir) % Dossiers.Count + Dossiers.Count) % Dossiers.Count;
            Scroll = 0;
        }
    }

    /// <summary>Up / down scroll, left / right walk the four tabs only.</summary>
    public override bool Intercept(in MenuInput input)
    {
        if (input.Up || input.Down)
        {
            Step(input.Down ? 1 : -1);
            return true;
        }

        // The wheel scrolls as the arrows do.
        if (input.Wheel != 0f)
        {
            Step(input.Wheel < 0f ? 1 : -1);
            return true;
        }

        if (input.Left || input.Right)
        {
            var tab = Math.Clamp(Highlight, 0, BriefTabs.Length - 1);
            Highlight = (tab + (input.Right ? 1 : BriefTabs.Length - 1)) % BriefTabs.Length;
            return true;
        }

        return false;
    }

    /// <summary>Esc leaves for the title, as the Godot port's briefing does.</summary>
    public override void Back() => Menu.QuitToTitle();

    public override void Draw(MenuPainter p)
    {
        p.FillWindow(MenuPainter.Black);
        var text = "";
        var title = "";
        switch (Tab)
        {
            case MissionTab.Briefing:
            {
                var page = Pages[Math.Min(Page, Pages.Count - 1)];
                if (!p.Image(page.Picture, page.Palette, BriefPicture))
                {
                    var first = Pages[0];
                    p.Image(first.Picture, first.Palette, BriefPicture);
                }

                title = page.Title;
                text = page.Text;
                break;
            }
            case MissionTab.Tactical:
            {
                p.Image(TacticalBackdrop, TacticalPalette, BriefPicture);
                if (Dossiers.Count == 0)
                {
                    text = "NO TACTICAL DATA FOR THIS MISSION.";
                }
                else
                {
                    var name = Dossiers[Dossier];
                    p.Tactical.Draw(name, p.Frame.Canvas(BriefPicture), _clock);
                    title = $"{name.ToUpperInvariant()}  {Dossier + 1}/{Dossiers.Count}";
                    text = p.BriefText($"{name}.TXT") is { } dossier ? Flatten(dossier) : "";
                }

                break;
            }
            case MissionTab.Statistics:
            {
                p.Image(TacticalBackdrop, TacticalPalette, BriefPicture);
                DrawStatistics(p);
                break;
            }
        }

        if (!p.Image(BriefPanel, BriefPalette, BriefPanelRect.X, BriefPanelRect.Y))
        {
            p.Fill(BriefPanelRect, new Vector4(0.1f, 0.16f, 0.16f, 1f));
        }

        foreach (var (tab, rect, grey, green) in BriefTabs)
        {
            if (!p.Image(tab == Tab ? green : grey, BriefPalette, rect.X, rect.Y))
            {
                p.Fill(rect, tab == Tab ? new Vector4(0.2f, 0.5f, 0.25f, 1f) : new Vector4(0.3f, 0.3f, 0.3f, 1f));
                p.TextIn("FONT0004.FNT", tab.ToString().ToUpperInvariant(), rect, MenuPainter.TextPale);
            }
        }

        DrawText(p, title, text);
        if (Highlighted is { } h && Menu.Current == this && h.Id != "picture")
        {
            p.Highlight(h.Rect);
        }
    }

    /// <summary>The header, then the rows two by two: the mission's, then the career's.</summary>
    private void DrawStatistics(MenuPainter p)
    {
        var rows = _briefing.Statistics;
        var header = _briefing.StatisticsHeader.Length > 0 ? _briefing.StatisticsHeader : rows.Count == 0 ? NoStatistics : "";
        var font = p.Font(StatsFont);
        var pitch = (font?.LineHeight ?? 8) + StatsRowGap;
        if (header.Length > 0)
        {
            var lines = font != null
                ? TextLayout.Lines(TextLayout.Wrap(header, c => font.Advance(c), 320 - 2 * StatsHeaderAt.X))
                : [header];
            for (var i = 0; i < lines.Length; i++)
            {
                p.Text(StatsFont, lines[i], StatsHeaderAt.X, StatsHeaderAt.Y + i * pitch, MenuPainter.TextPale);
            }
        }

        for (var i = 0; i < Math.Min(rows.Count, 4); i++)
        {
            var at = i < 2 ? StatsMissionAt : StatsCareerAt;
            var y = at.Y + (i % 2) * pitch;
            p.Text(StatsFont, rows[i].Label, at.X, y, MenuPainter.TextPale);
            p.Text(StatsFont, rows[i].Value, at.X + StatsValueOffset, y, MenuPainter.TextGreen);
        }
    }

    private void DrawText(MenuPainter p, string title, string text)
    {
        if (p.Font(TextFont) is not { } font)
        {
            return;
        }

        var lines = new List<string>();
        if (title.Length > 0)
        {
            lines.Add(title);
        }

        if (text.Length > 0)
        {
            lines.AddRange(TextLayout.Lines(TextLayout.Wrap(text, c => font.Advance(c), BriefText.Width)));
        }

        var pitch = font.LineHeight + 1;
        _visibleLines = Math.Max(1, BriefText.Height / pitch);
        _lineCount = lines.Count;
        Scroll = Math.Min(Scroll, Math.Max(0, _lineCount - _visibleLines));
        for (var i = 0; i < _visibleLines && Scroll + i < lines.Count; i++)
        {
            var row = Scroll + i;
            var colour = row == 0 && title.Length > 0 ? MenuPainter.TextGreen : MenuPainter.TextPale;
            p.Text(TextFont, lines[row], BriefText.X, BriefText.Y + i * pitch, colour);
        }
    }

    private static string Flatten(string text) =>
        string.Join(' ', text.Replace("\r", "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !(l.StartsWith('[') && l.EndsWith(']'))));
}
