using System.Drawing;
using SkyNet.Game.Enemies;
using static SkyNet.Game.Menu.MenuLayout;

namespace SkyNet.Game.Menu;

/// <summary>
/// GAMEPLAY (OPTIONS → DETAIL → GAMEPLAY, where DOS's RESOLUTION pair was), the port's own screen in
/// the DISPLAY screen's make: rows of game rules, each applied and saved at once, and a hint line for
/// the highlighted row. Rows: ROBOT AI (CLASSIC: DOS's robots; IMPROVED: smarter movement and fire).
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
    private static readonly (string Id, string Label)[] Rows = [("robotai", "ROBOT AI")];

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

    private void Live(Action change)
    {
        change();
        Menu.Changed();
    }

    /// <summary>The text of a row's value box.</summary>
    public string Value(string id) => id switch
    {
        "robotai" => Menu.Settings.RobotAi == RobotAi.Improved ? "IMPROVED" : "CLASSIC",
        _ => "",
    };

    /// <summary>The hint line for a row as it is set.</summary>
    public string HintOf(string id) => id switch
    {
        "robotai" => Menu.Settings.RobotAi == RobotAi.Improved
            ? "ROBOTS MOVE AND AIM SMARTER"
            : "ROBOTS AS IN DOS",
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
            p.TextIn(DosDialog.Font, Value(id), box, MenuPainter.TextGreen);
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
