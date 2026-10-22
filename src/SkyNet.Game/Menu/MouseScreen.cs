using System.Drawing;
using System.Globalization;
using System.Numerics;
using static SkyNet.Game.Menu.MenuLayout;

namespace SkyNet.Game.Menu;

/// <summary>
/// MOUSE.IMG, MOUSE SENSITIVITY (CONTROLS → MOUSE; SKYNET.EXE v1.01 loop 0x140c77, hotspots
/// 0x141a8b): HORIZONTAL and VERTICAL bars of 11 segments, REVERSE VERTICAL and EXIT, as DOS has it.
/// A bar keeps DOS's divisor (CONTROLS.DAT +0x44 / +0x48: 12 − the segments lit, 3 at first, 9 lit);
/// REVERSE VERTICAL is +0x60 bit 0, lit with MOUSBTN.CFA while on. Left / right step a bar, a click
/// sets it at the segment under the pointer, Accept steps it on (wrapping); every change applies to
/// the look at once (on foot, the jeep's turret, the HK) and is saved.
/// </summary>
public sealed class MouseScreen : PanelScreen
{
    public MouseScreen() : base(MouseImage, MouseSize, MenuLayout.MouseScale)
    {
    }

    public override string Name => "mouse";

    public override void Opened()
    {
        if (Items.Count > 0)
        {
            return;
        }

        var s = Menu.Settings;
        Bar("horizontal", MouseHorizontal, () => s.MouseH, v => s.MouseH = v);
        Bar("vertical", MouseVertical, () => s.MouseV, v => s.MouseV = v);
        Add(new MenuItem("reverse", At(MouseReverse)) { Lit = Lit(MouseLit, MouseLitAt, 0), Activate = () => Set(() => s.MouseInvertY = !s.MouseInvertY) });
        Add(new MenuItem("exit", At(MouseExit)) { Lit = Lit(MouseLit, MouseLitAt, 1), Activate = Menu.Pop });
        Highlight = 0;
    }

    /// <summary>The segments lit for a click at <paramref name="t"/> (0..1) across a bar's well.</summary>
    public static int LitAt(float t)
    {
        var x = MouseHorizontal.X + t * MouseHorizontal.Width;
        return Math.Clamp((int)MathF.Floor((x - MouseSegmentX) / MouseSegmentPitch) + 1, 1, MenuSettings.MouseSteps);
    }

    private void Bar(string id, Rectangle well, Func<int> get, Action<int> put)
    {
        int Lit() => MenuSettings.MouseLit(get());
        void SetLit(int lit) => Set(() => put(MenuSettings.MouseValue(lit)));
        Add(new MenuItem(id, At(well))
        {
            Adjust = d => SetLit(Lit() + d),
            Activate = () => SetLit(Lit() >= MenuSettings.MouseSteps ? 1 : Lit() + 1),
            ClickAt = t => SetLit(LitAt(t)),
        });
    }

    private void Set(Action change)
    {
        change();
        Menu.Changed();
    }

    protected override void DrawContents(MenuPainter p)
    {
        var s = Menu.Settings;
        Segments(p, 0, MenuSettings.MouseLit(s.MouseH));
        Segments(p, 1, MenuSettings.MouseLit(s.MouseV));
        if (s.MouseInvertY)
        {
            p.Mark(Find("reverse")!, Lit(MouseLit, MouseLitAt, 0));
        }
    }

    /// <summary>The art has every segment lit: the ones past <paramref name="lit"/> are covered with the well.</summary>
    private void Segments(MenuPainter p, int row, int lit)
    {
        for (var i = lit; i < MenuSettings.MouseSteps; i++)
        {
            var x = MouseSegmentX + i * MouseSegmentPitch;
            var w = i == MenuSettings.MouseSteps - 1 ? MouseSegmentWidth + 1 : MouseSegmentWidth;
            var segment = At(new Rectangle(x, MouseSegmentY[row], w, MouseSegmentHeight));
            if (!p.ImagePart(MouseImage, MenuPalette, MouseSegmentGap, segment))
            {
                p.Fill(segment, DosDialog.Dark);
            }
        }
    }
}

/// <summary>
/// GAMEPAD (CONTROLS → GAMEPAD, DOS's JOYSTICK), the port's own screen in the DISPLAY screen's make. DOS's
/// JOYSTICK.IMG page (loop 0x140990) offers CALIBRATE, DISABLE, a joystick type and CENTER THRESHOLD
/// LOW / MED / HIGH for an analogue PC stick, none of which an SDL gamepad needs; its threshold
/// lives on here as DEADZONE. Rows: LOOK HORIZONTAL and LOOK VERTICAL (the right stick's rate, %),
/// INVERT Y, DEADZONE (the right stick's, % of its reach), VIBRATION (the pad shakes when the soldier
/// is hit; turning it on tries it), BUTTONS (every action's button, <see cref="PadBindScreen"/>),
/// and the connected pad's name. Every row applies and saves at once.
/// </summary>
public sealed class GamepadScreen : PanelScreen
{
    public static readonly Size PanelSize = DisplayScreen.PanelSize;
    public static readonly Rectangle NameRect = new(0, DisplayScreen.RowTop + 6 * DisplayScreen.RowPitch + 4, PanelSize.Width, 10);
    public static readonly Rectangle ExitRect = DisplayScreen.ExitRect;
    public const string NoPad = "NO GAMEPAD CONNECTED";

    /// <summary>The row ids, top to bottom, and their labels.</summary>
    public static readonly (string Id, string Label)[] Rows =
    [
        ("padh", "LOOK HORIZONTAL"), ("padv", "LOOK VERTICAL"), ("invert", "INVERT Y"), ("deadzone", "DEADZONE"),
        ("vibration", "VIBRATION"), ("buttons", PadBindScreen.Caption),
    ];

    public GamepadScreen() : base(OptionsImage, PanelSize, DetailScale)
    {
    }

    public override string Name => "gamepad";

    public override void Opened()
    {
        if (Items.Count > 0)
        {
            return;
        }

        var s = Menu.Settings;
        Slider(0, () => s.PadH, v => s.PadH = v, MenuSettings.MinPadLook, MenuSettings.MaxPadLook, MenuSettings.PadLookStep);
        Slider(1, () => s.PadV, v => s.PadV = v, MenuSettings.MinPadLook, MenuSettings.MaxPadLook, MenuSettings.PadLookStep);
        Toggle(2, () => s.PadInvertY = !s.PadInvertY);
        Slider(3, () => s.PadDeadZone, v => s.PadDeadZone = v, MenuSettings.MinPadDeadZone, MenuSettings.MaxPadDeadZone, MenuSettings.PadDeadZoneStep);
        Toggle(4, () =>
        {
            s.PadVibration = !s.PadVibration;
            if (s.PadVibration)
            {
                Menu.Context.Rumble?.Invoke(0.5f, 0.5f, 250);
            }
        });
        Add(new MenuItem(Rows[5].Id, At(DisplayScreen.RowRect(5))) { Activate = () => Menu.Push(new PadBindScreen()) });
        Add(new MenuItem("exit", At(ExitRect))
        {
            Lit = new LitArt(OptionsLit, 11, new Point(Origin.X + ExitRect.X - OptionsExit.X + OptionsLitAt.X, Origin.Y + ExitRect.Y - OptionsExit.Y + OptionsLitAt.Y)),
            Activate = Menu.Pop,
        });
        Highlight = 0;
    }

    /// <summary>A bar row: left / right by a step, Accept on by a step (wrapping), a click sets it.</summary>
    private void Slider(int i, Func<int> get, Action<int> put, int min, int max, int step)
    {
        var box = DisplayScreen.ValueBox(i);
        var row = DisplayScreen.RowRect(i);
        Add(new MenuItem(Rows[i].Id, At(row))
        {
            Adjust = d => Live(() => put(Math.Clamp(get() + d * step, min, max))),
            Activate = () => Live(() => put(get() >= max ? min : Math.Min(max, get() + step))),
            ClickAt = t =>
            {
                var x = row.X + t * row.Width;
                var f = Math.Clamp((x - box.X) / box.Width, 0f, 1f);
                Live(() => put(min + (int)MathF.Round(f * (max - min) / step) * step));
            },
        });
    }

    private void Toggle(int i, Action flip) =>
        Add(new MenuItem(Rows[i].Id, At(DisplayScreen.RowRect(i))) { Adjust = _ => Live(flip), Activate = () => Live(flip) });

    private void Live(Action change)
    {
        change();
        Menu.Changed();
    }

    /// <summary>The text of a row's value box.</summary>
    public string Value(string id)
    {
        var s = Menu.Settings;
        return id switch
        {
            "padh" => Percent(s.PadH),
            "padv" => Percent(s.PadV),
            "invert" => s.PadInvertY ? "ON" : "OFF",
            "deadzone" => Percent(s.PadDeadZone),
            "vibration" => s.PadVibration ? "ON" : "OFF",
            "buttons" => "EDIT...",
            _ => "",
        };
    }

    /// <summary>The line under the rows: the pad plugged in, or <see cref="NoPad"/>.</summary>
    public string PadLine => Menu.Context.GamepadName() is { Length: > 0 } name ? name.ToUpperInvariant() : NoPad;

    private static string Percent(int v) => string.Create(CultureInfo.InvariantCulture, $"{v}%");

    /// <summary>A bar row's value and range; null for the other rows.</summary>
    private (int Value, int Min, int Max)? Bar(string id)
    {
        var s = Menu.Settings;
        return id switch
        {
            "padh" => (s.PadH, MenuSettings.MinPadLook, MenuSettings.MaxPadLook),
            "padv" => (s.PadV, MenuSettings.MinPadLook, MenuSettings.MaxPadLook),
            "deadzone" => (s.PadDeadZone, MenuSettings.MinPadDeadZone, MenuSettings.MaxPadDeadZone),
            _ => null,
        };
    }

    public override void Draw(MenuPainter p)
    {
        DosDialog.Panel(p, new Rectangle(Origin, Size));
        p.TextIn(DosDialog.TitleFont, "GAMEPAD", At(new Rectangle(0, 4, Size.Width, 14)), MenuPainter.TextPale);
        for (var i = 0; i < Rows.Length; i++)
        {
            var (id, label) = Rows[i];
            var box = At(DisplayScreen.ValueBox(i));
            p.Text(DosDialog.Font, label, Origin.X + 10, box.Y + 2, MenuPainter.TextPale);
            DosDialog.Box(p, box);
            if (Bar(id) is var (value, min, max))
            {
                var w = (box.Width - 2) * (Math.Clamp(value, min, max) - min) / (max - min);
                p.Fill(new Rectangle(box.X + 1, box.Y + 1, w, box.Height - 2), new Vector4(0.35f, 0.85f, 0.45f, 0.85f));
                p.TextIn(DosDialog.Font, Value(id), box, MenuPainter.TextPale);
                continue;
            }

            p.TextIn(DosDialog.Font, Value(id), box, MenuPainter.TextGreen);
        }

        var line = PadLine;
        p.TextIn(DosDialog.Font, line, At(NameRect), line == NoPad ? DosDialog.Pending : MenuPainter.TextGreen);
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
