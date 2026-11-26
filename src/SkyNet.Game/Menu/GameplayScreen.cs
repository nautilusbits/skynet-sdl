using System.Drawing;
using System.Numerics;
using SkyNet.Game.Audio;
using SkyNet.Game.Campaign;
using SkyNet.Game.Enemies;
using static SkyNet.Game.Menu.MenuLayout;

namespace SkyNet.Game.Menu;

/// <summary>
/// GAMEPLAY (OPTIONS → DETAIL → GAMEPLAY, where DOS's RESOLUTION pair was), the port's own screen in
/// the DISPLAY screen's make: rows of game rules, each applied and saved at once, and a hint line for
/// the highlighted row. Rows: ROBOT AI (CLASSIC: DOS's robots; IMPROVED: smarter movement and fire),
/// FULL PICKUPS (TAKE: DOS's; LEAVE: items whose stat is full stay on the ground), MUSIC (ORIGINAL:
/// the port's synth; SOUNDFONT: a .sf2 from the soundfonts folder, issue #70) and SOUNDFONT (which .sf2;
/// NONE, greyed, when the folder has none, and MUSIC stays ORIGINAL).
/// <code>
///   ┌──────── GAMEPLAY ────────┐
///   │ ROBOT AI     [IMPROVED]  │  ← rows, DisplayScreen's pitch
///   │ ...room for more rows... │
///   │ hint of the row          │
///   │                   [EXIT] │
///   └──────────────────────────┘
/// </code>
/// </summary>
internal sealed class GameplayScreen : PanelScreen
{
    /// <summary>DETAIL's button to here.</summary>
    public const string Caption = "GAMEPLAY";

    private static readonly Size PanelSize = new(DisplayScreen.PanelSize.Width, 112);
    private static readonly Rectangle ExitRect = new(PanelSize.Width - OptionsExit.Width, PanelSize.Height - OptionsExit.Height - 4, OptionsExit.Width, OptionsExit.Height);
    private static readonly Rectangle HintRect = new(4, ExitRect.Y - 14, PanelSize.Width - 8, 10);

    /// <summary>The row ids, top to bottom, and their labels.</summary>
    private static readonly (string Id, string Label)[] Rows =
        [("robotai", "ROBOT AI"), ("fullpickups", "FULL PICKUPS"), ("music", "MUSIC"), ("soundfont", "SOUNDFONT")];

    /// <summary>Where a SOUNDFONT comes from, said when the folder has none.</summary>
    private const string NoSoundFontHint = "PUT .SF2 FILES IN SOUNDFONTS";
    private static readonly Vector4 Greyed = new(0.45f, 0.5f, 0.5f, 1f);

    /// <summary>The .sf2 ids found when the screen opened.</summary>
    private IReadOnlyList<string> _fonts = [];

    public GameplayScreen() : base(OptionsImage, PanelSize, DetailScale)
    {
    }

    public override string Name => "gameplay";

    public override void Opened()
    {
        if (Items.Count > 0)
        {
            return;
        }

        var s = Menu.Settings;
        Row(0, () => s.RobotAi = s.RobotAi == RobotAi.Improved ? RobotAi.Classic : RobotAi.Improved);
        Row(1, () => s.FullPickups = s.FullPickups == FullPickups.Leave ? FullPickups.Take : FullPickups.Leave);
        _fonts = Menu.Context.SoundFonts();
        Row(2, FlipMusic);
        Add(new MenuItem(Rows[3].Id, At(DisplayScreen.RowRect(3))) { Adjust = d => Live(() => StepFont(d)), Activate = () => Live(() => StepFont(1)) });
        Add(new MenuItem("exit", At(ExitRect))
        {
            Lit = new LitArt(OptionsLit, 11, new Point(Origin.X + ExitRect.X - OptionsExit.X + OptionsLitAt.X, Origin.Y + ExitRect.Y - OptionsExit.Y + OptionsLitAt.Y)),
            Activate = Menu.Pop,
        });
        Highlight = 0;
    }

    /// <summary>A two-value row: every step (left, right, Accept, a click) flips it.</summary>
    private void Row(int i, Action flip) =>
        Add(new MenuItem(Rows[i].Id, At(DisplayScreen.RowRect(i))) { Adjust = _ => Live(flip), Activate = () => Live(flip) });

    /// <summary>MUSIC: ORIGINAL ⇄ SOUNDFONT; without a .sf2 it stays ORIGINAL (the hint says why).</summary>
    private void FlipMusic()
    {
        var s = Menu.Settings;
        if (s.MusicSynth == MusicSynth.SoundFont || _fonts.Count == 0)
        {
            s.MusicSynth = MusicSynth.Original;
            return;
        }

        s.MusicSynth = MusicSynth.SoundFont;
        s.SoundFont = _fonts[FontIndex()];
    }

    /// <summary>SOUNDFONT: the next or previous .sf2, wrapping.</summary>
    private void StepFont(int step)
    {
        if (_fonts.Count == 0)
        {
            return;
        }

        var i = ((FontIndex() + step) % _fonts.Count + _fonts.Count) % _fonts.Count;
        Menu.Settings.SoundFont = _fonts[i];
    }

    /// <summary>The setting's .sf2 in the list; 0 when it is not there (the first found plays).</summary>
    private int FontIndex()
    {
        var i = _fonts.ToList().FindIndex(f => f.Equals(Menu.Settings.SoundFont, StringComparison.OrdinalIgnoreCase));
        return Math.Max(i, 0);
    }

    private void Live(Action change)
    {
        change();
        Menu.Changed();
    }

    /// <summary>The text of a row's value box.</summary>
    public string Value(string id) => id switch
    {
        "robotai" => Menu.Settings.RobotAi == RobotAi.Improved ? "IMPROVED" : "CLASSIC",
        "fullpickups" => Menu.Settings.FullPickups == FullPickups.Leave ? "LEAVE" : "TAKE",
        "music" => Menu.Settings.MusicSynth == MusicSynth.SoundFont && _fonts.Count > 0 ? "SOUNDFONT" : "ORIGINAL",
        "soundfont" => _fonts.Count > 0 ? SoundFonts.Label(_fonts[FontIndex()]) : "NONE",
        _ => "",
    };

    /// <summary>The hint line for a row as it is set.</summary>
    public string HintOf(string id) => id switch
    {
        "robotai" => Menu.Settings.RobotAi == RobotAi.Improved
            ? "ROBOTS MOVE AND AIM SMARTER"
            : "ROBOTS AS IN DOS",
        "fullpickups" => Menu.Settings.FullPickups == FullPickups.Leave
            ? "ITEMS STAY WHEN STAT IS FULL"
            : "ITEMS ALWAYS TAKEN, AS IN DOS",
        "music" or "soundfont" when _fonts.Count == 0 => NoSoundFontHint,
        "music" => Value("music") == "SOUNDFONT" ? "GENERAL MIDI FROM A .SF2 FILE" : "THE PORT'S BUILT-IN SYNTH",
        "soundfont" => "LEFT / RIGHT PICKS THE .SF2",
        _ => "",
    };

    public override void Draw(MenuPainter p)
    {
        DosDialog.Panel(p, new Rectangle(Origin, Size));
        p.TextIn(DosDialog.TitleFont, Caption, At(new Rectangle(0, 4, Size.Width, 14)), MenuPainter.TextPale);
        for (var i = 0; i < Rows.Length; i++)
        {
            var (id, label) = Rows[i];
            var box = At(DisplayScreen.ValueBox(i));
            p.Text(DosDialog.Font, label, Origin.X + 10, box.Y + 2, MenuPainter.TextPale);
            DosDialog.Box(p, box);
            p.TextIn(DosDialog.Font, Value(id), box, id == "soundfont" && _fonts.Count == 0 ? Greyed : MenuPainter.TextGreen);
        }

        // The hint of the highlighted row (none on EXIT).
        if (Highlighted is { } row && HintOf(row.Id) is { Length: > 0 } hint)
        {
            p.TextIn(DosDialog.Font, hint, At(HintRect), DosDialog.Pending);
        }

        if (!p.ImagePart(OptionsImage, MenuPalette, OptionsExit, At(ExitRect)))
        {
            DosDialog.Button(p, At(ExitRect), "EXIT");
        }

        if (Highlighted is { } h && Menu.Current == this)
        {
            p.Highlight(h);
        }
    }
}
