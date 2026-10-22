using System.Drawing;
using System.Globalization;
using System.Numerics;
using RetroEng.Platform;
using RetroEng.Render;
using SkyNet.Game.Hud;
using static SkyNet.Game.Menu.MenuLayout;

namespace SkyNet.Game.Menu;

/// <summary>What the display offers: its desktop mode, its usable area (no task bar) and the
/// sizes of its own modes (exclusive fullscreen).</summary>
public sealed record DisplayModes(Resolution Desktop, Resolution Usable, IReadOnlyList<Resolution> Fullscreen)
{
    /// <summary>A 1920 × 1080 desktop with no modes of its own (tests, a display SDL cannot read).</summary>
    public static readonly DisplayModes Fallback = new(new Resolution(1920, 1080), new Resolution(1920, 1040), []);
}

/// <summary>The window part of the settings, chosen on the DISPLAY screen and tried with APPLY
/// before it is kept: the mode, the window's size, exclusive fullscreen's size (null: the desktop's).</summary>
public readonly record struct DisplayChoice(WindowMode Mode, Resolution Window, Resolution? Fullscreen)
{
    public static DisplayChoice From(MenuSettings s) => new(s.WindowMode, s.WindowSize, s.FullscreenSize);

    public void ApplyTo(MenuSettings s)
    {
        s.WindowMode = Mode;
        s.WindowSize = Window;
        s.FullscreenSize = Fullscreen;
    }

    /// <summary>The size the RESOLUTION row shows: the window's, the desktop's (borderless), or the
    /// fullscreen mode's.</summary>
    public Resolution Shown(DisplayModes modes) => Mode switch
    {
        WindowMode.Windowed => Window,
        WindowMode.Borderless => modes.Desktop,
        _ => Fullscreen ?? modes.Desktop,
    };
}

/// <summary>The DISPLAY screen's lists and steps, without SDL or the GPU.</summary>
public static class DisplayOptions
{
    public static readonly string[] ModeNames = ["WINDOWED", "BORDERLESS", "FULLSCREEN"];

    /// <summary>The sizes RESOLUTION steps through: windowed, the 4:3 / 16:9 / 16:10 presets that fit
    /// the usable area and the window's own size; borderless, the desktop's only; fullscreen, the
    /// display's modes (the desktop's when SDL lists none), smallest first.</summary>
    public static IReadOnlyList<Resolution> Sizes(WindowMode mode, DisplayModes modes, Resolution window) => mode switch
    {
        WindowMode.Windowed => Resolutions.Windowed(modes.Usable, window),
        WindowMode.Borderless => [modes.Desktop],
        _ => modes.Fullscreen.Count == 0
            ? [modes.Desktop]
            : modes.Fullscreen.Distinct().OrderBy(r => r.Area).ThenBy(r => r.Width).ToList(),
    };

    public static DisplayChoice StepMode(DisplayChoice c, int step) =>
        c with { Mode = (WindowMode)Wrap((int)c.Mode + step, ModeNames.Length) };

    /// <summary>The next size of the choice's mode (borderless has one: the desktop's).</summary>
    public static DisplayChoice StepSize(DisplayChoice c, DisplayModes modes, int step)
    {
        var list = Sizes(c.Mode, modes, c.Window);
        var next = Resolutions.Step(list, c.Shown(modes), step);
        return c.Mode switch
        {
            WindowMode.Windowed => c with { Window = next },
            WindowMode.Fullscreen => c with { Fullscreen = next },
            _ => c,
        };
    }

    public static VSync StepVSync(VSync v, int step) => Order[Wrap(Array.IndexOf(Order, v) + step, Order.Length)];

    private static readonly VSync[] Order = [VSync.Off, VSync.On, VSync.Adaptive];

    public static string Name(VSync v) => v switch
    {
        VSync.Off => "OFF",
        VSync.Adaptive => "ADAPTIVE",
        _ => "ON",
    };

    /// <summary>RENDER SCALE's values: AUTO first, then the fixed ones.</summary>
    private static readonly int[] ScaleOrder = [Renderer.AutoScale, .. Renderer.Scales];

    public static int StepScale(int scale, int step)
    {
        var i = Array.IndexOf(ScaleOrder, scale);
        return i < 0 ? Renderer.FullScale : ScaleOrder[Wrap(i + step, ScaleOrder.Length)];
    }

    /// <summary>RENDER SCALE's box: "50%", or "AUTO 63%" with the scale AUTO picked (null: "AUTO").</summary>
    public static string ScaleName(int scale, int? inUse) => scale != Renderer.AutoScale
        ? string.Create(CultureInfo.InvariantCulture, $"{scale}%")
        : inUse is { } p ? string.Create(CultureInfo.InvariantCulture, $"AUTO {p}%") : "AUTO";

    public static int StepBrightness(int brightness, int step) =>
        Math.Clamp(brightness + step * MenuSettings.BrightnessStep, MenuSettings.MinBrightness, MenuSettings.MaxBrightness);

    public static string Name(Resolution r) => string.Create(CultureInfo.InvariantCulture, $"{r.Width} X {r.Height}");

    private static int Wrap(int i, int n) => (i % n + n) % n;
}

/// <summary>Drawing for the dialogs DOS has no image of (DISPLAY, KEEP THESE SETTINGS?): the stone
/// of OPTIONS.IMG cut and repeated, a bevel in its dark and light, its value-box well and its EXIT.</summary>
internal static class DosDialog
{
    /// <summary>A clean patch of OPTIONS.IMG's stone (top left, beside the title).</summary>
    public static readonly Rectangle Stone = new(3, 3, 56, 17);
    /// <summary>OPTIONS.IMG's MUSIC well (the dark value box).</summary>
    public static readonly Rectangle Well = OptionsMusic;
    public static readonly Vector4 Dark = new(0.05f, 0.08f, 0.08f, 1f);
    public static readonly Vector4 Light = new(0.45f, 0.55f, 0.55f, 1f);
    public static readonly Vector4 Pending = new(1f, 0.85f, 0.4f, 1f);
    public const string TitleFont = "FONT0003.FNT";
    public const string Font = "FONT0004.FNT";

    public static void Panel(MenuPainter p, Rectangle r)
    {
        Plain(p, r);
        Bevel(p, r, raised: true);
    }

    /// <summary>The stone alone, no bevel: covers baked art (DETAIL's RESOLUTION).</summary>
    public static void Plain(MenuPainter p, Rectangle r)
    {
        if (!p.ImageTiled(OptionsImage, MenuPalette, Stone, r))
        {
            p.Fill(r, new Vector4(0.12f, 0.17f, 0.17f, 1f));
        }
    }

    /// <summary>A one-pixel bevel: light top-left and dark bottom-right (raised), or the other way.</summary>
    public static void Bevel(MenuPainter p, Rectangle r, bool raised)
    {
        var (a, b) = raised ? (Light, Dark) : (Dark, Light);
        p.Fill(new Rectangle(r.X, r.Y, r.Width, 1), a);
        p.Fill(new Rectangle(r.X, r.Y, 1, r.Height), a);
        p.Fill(new Rectangle(r.X, r.Bottom - 1, r.Width, 1), b);
        p.Fill(new Rectangle(r.Right - 1, r.Y, 1, r.Height), b);
    }

    /// <summary>A value box: OPTIONS.IMG's dark well stretched over <paramref name="r"/>.</summary>
    public static void Box(MenuPainter p, Rectangle r)
    {
        if (!p.ImagePart(OptionsImage, MenuPalette, Well, r))
        {
            p.Fill(r, new Vector4(0.03f, 0.05f, 0.06f, 1f));
            Bevel(p, r, raised: false);
        }
    }

    /// <summary>A button with no art: the stone, a bevel and its caption in the title font.</summary>
    public static void Button(MenuPainter p, Rectangle r, string caption, bool enabled = true)
    {
        Panel(p, r);
        p.TextIn(TitleFont, caption, r, enabled ? MenuPainter.TextPale : new Vector4(0.45f, 0.5f, 0.5f, 1f));
    }
}

/// <summary>
/// DISPLAY (OPTIONS → DISPLAY), the port's own screen: DOS has none, so it is composed from
/// OPTIONS.IMG's stone, wells and EXIT and the menu fonts, as the Godot port cut its display panel
/// from the DETAIL art. Rows: WINDOW (windowed / borderless / fullscreen) and RESOLUTION, which wait
/// for APPLY and then KEEP THESE SETTINGS? (<see cref="KeepBox"/>); VSYNC, RENDER SCALE, BRIGHTNESS,
/// LOOK, RENDERER (<see cref="ViewStyles"/>), ASCII COLOUR, FPS COUNTER and HUD STYLE, which apply and save at once. Left / right step a row, Accept or a click
/// steps it on, a click on BRIGHTNESS sets it; Esc or EXIT drops a choice not applied. ENHANCED...
/// opens the enhanced look's own options (<see cref="EnhancedScreen"/>; greyed under ORIGINAL); the
/// 3D VIEW row opens the stereo and phone-viewer options (<see cref="StereoScreen"/>).
/// </summary>
public sealed class DisplayScreen : PanelScreen
{
    public static readonly Size PanelSize = new(224, 192);
    public const int RowTop = 22;
    public const int RowPitch = 12;
    public static readonly Rectangle BoxAt = new(104, 0, 110, 10);
    /// <summary>The buttons' row, under the twelve rows.</summary>
    public const int ButtonTop = 174;
    private static readonly Vector4 Greyed = new(0.45f, 0.5f, 0.5f, 1f);
    public static readonly Rectangle ApplyRect = new(0, ButtonTop, 70, 18);
    /// <summary>ENHANCED...: the ENHANCED OPTIONS screen (<see cref="EnhancedScreen"/>), between APPLY and EXIT.</summary>
    public static readonly Rectangle EnhancedRect = new(74, ButtonTop, 103, 18);
    /// <summary>EXIT, cut from OPTIONS.IMG with its lit frame (OPTBTN.CFA 11).</summary>
    public static readonly Rectangle ExitRect = new(PanelSize.Width - OptionsExit.Width, ButtonTop, OptionsExit.Width, OptionsExit.Height);

    /// <summary>The row ids, top to bottom, and their labels.</summary>
    public static readonly (string Id, string Label)[] Rows =
    [
        ("mode", "WINDOW"), ("size", "RESOLUTION"), ("vsync", "VSYNC"), ("scale", "RENDER SCALE"),
        ("brightness", "BRIGHTNESS"), ("look", "LOOK"), ("renderer", "RENDERER"), ("ascii", "ASCII COLOUR"),
        ("fps", "FPS COUNTER"), ("hud", "HUD STYLE"), ("uiscale", "UI SCALE"), ("3dview", StereoScreen.Caption),
    ];

    /// <summary>UI SCALE's values, in <see cref="UiScale"/>'s order.</summary>
    private static readonly string[] UiScaleNames = ["AUTO", "1X", "1.5X", "2X"];
    private const int UiScales = 4;

    /// <summary>WINDOW and RESOLUTION: rows 0-1, hidden on a fixed fullscreen (Android).</summary>
    private const int WindowRows = 2;

    private DisplayModes _modes = DisplayModes.Fallback;

    /// <summary>The first row shown; the rest move up into the hidden rows' place.</summary>
    private int _first;

    public DisplayScreen() : base(OptionsImage, PanelSize, DetailScale)
    {
    }

    public override string Name => "display";

    /// <summary>The window choice on the screen (applied with APPLY).</summary>
    public DisplayChoice Pending { get; private set; }

    /// <summary>The choice differs from what is in use.</summary>
    public bool Dirty => Pending != DisplayChoice.From(Menu.Settings);

    public DisplayModes Modes => _modes;

    public static Rectangle RowRect(int i) => new(4, RowTop + i * RowPitch - 1, PanelSize.Width - 8, RowPitch - 1);

    public static Rectangle ValueBox(int i) => new(BoxAt.X, RowTop + i * RowPitch, BoxAt.Width, BoxAt.Height);

    public override void Opened()
    {
        _modes = Menu.Context.Display?.Invoke() ?? DisplayModes.Fallback;
        Pending = DisplayChoice.From(Menu.Settings);
        if (Items.Count > 0)
        {
            return;
        }

        var s = Menu.Settings;
        _first = Menu.Context.Window == WindowKind.FixedFullscreen ? WindowRows : 0;
        if (_first == 0)
        {
            Row(0, d => Pending = DisplayOptions.StepMode(Pending, d));
            Row(1, d =>
            {
                if (Pending.Mode == WindowMode.Borderless)
                {
                    Menu.ShowToast("BORDERLESS TAKES THE DESKTOP'S SIZE");
                }

                Pending = DisplayOptions.StepSize(Pending, _modes, d);
            });
        }

        Row(2, d => Live(() => s.VSync = DisplayOptions.StepVSync(s.VSync, d)));
        Row(3, d => Live(() => s.RenderScale = DisplayOptions.StepScale(s.RenderScale, d)));
        var bar = ValueBox(4 - _first);
        var row = RowRect(4 - _first);
        Add(new MenuItem(Rows[4].Id, At(row))
        {
            Adjust = d => Live(() => s.Brightness = DisplayOptions.StepBrightness(s.Brightness, d)),
            Activate = () => Live(() => s.Brightness = s.Brightness >= MenuSettings.MaxBrightness
                ? MenuSettings.MinBrightness
                : DisplayOptions.StepBrightness(s.Brightness, 1)),
            ClickAt = t =>
            {
                var x = row.X + t * row.Width;
                var f = Math.Clamp((x - bar.X) / bar.Width, 0f, 1f);
                var steps = (MenuSettings.MaxBrightness - MenuSettings.MinBrightness) / MenuSettings.BrightnessStep;
                Live(() => s.Brightness = MenuSettings.MinBrightness + (int)MathF.Round(f * steps) * MenuSettings.BrightnessStep);
            },
        });
        Row(5, _ =>
        {
            if (s.Look == Look.Original && !Menu.Context.EnhancedAvailable)
            {
                Menu.ShowToast("ENHANCED: COMING SOON");
                return;
            }

            Live(() => s.Look = s.Look == Look.Original ? Look.Enhanced : Look.Original);
        });
        Row(6, d => Live(() => s.Style = ViewStyles.Step(s.Style, d)));
        Row(7, d => Live(() => s.AsciiColour = ViewStyles.Step(s.AsciiColour, d)));
        Row(8, _ => Live(() => s.ShowFps = !s.ShowFps));
        Row(9, _ => Live(() => s.HudStyle = s.HudStyle == HudStyle.Classic ? HudStyle.Modern : HudStyle.Classic));
        Row(10, d => Live(() => s.UiScale = (UiScale)(((int)s.UiScale + d + UiScales) % UiScales)));
        Add(new MenuItem(Rows[11].Id, At(RowRect(11 - _first))) { Activate = () => Menu.Push(new StereoScreen()) });
        Add(new MenuItem("apply", At(ApplyRect)) { Activate = Apply });
        Add(new MenuItem("enhanced", At(EnhancedRect)) { Activate = () => Menu.Push(new EnhancedScreen()) });
        Add(new MenuItem("exit", At(ExitRect))
        {
            Lit = new LitArt(OptionsLit, 11, new Point(Origin.X + ExitRect.X - OptionsExit.X + OptionsLitAt.X, Origin.Y + ExitRect.Y - OptionsExit.Y + OptionsLitAt.Y)),
            Activate = Menu.Pop,
        });
        Highlight = 0;
    }

    /// <summary>A stepping row: left / right by one, Accept or a click on by one.</summary>
    private void Row(int i, Action<int> step) =>
        Add(new MenuItem(Rows[i].Id, At(RowRect(i - _first))) { Adjust = step, Activate = () => step(1) });

    private void Live(Action change)
    {
        change();
        Menu.Changed();
    }

    /// <summary>APPLY: the window choice is put in use (not saved) and KEEP THESE SETTINGS? asks.</summary>
    public void Apply()
    {
        if (!Dirty)
        {
            Menu.ShowToast("NOTHING TO APPLY");
            return;
        }

        var previous = DisplayChoice.From(Menu.Settings);
        Pending.ApplyTo(Menu.Settings);
        Menu.Try();
        Menu.Push(new KeepBox(previous));
    }

    /// <summary>The text of a row's value box.</summary>
    public string Value(string id)
    {
        var s = Menu.Settings;
        return id switch
        {
            "mode" => DisplayOptions.ModeNames[(int)Pending.Mode],
            "size" => DisplayOptions.Name(Pending.Shown(_modes)),
            "vsync" => DisplayOptions.Name(s.VSync),
            "scale" => DisplayOptions.ScaleName(s.RenderScale, Menu.Context.RenderScaleInUse?.Invoke()),
            "brightness" => string.Create(CultureInfo.InvariantCulture, $"{s.Brightness}%"),
            "look" => s.Look == Look.Enhanced ? "ENHANCED" : "ORIGINAL",
            "renderer" => ViewStyles.Name(s.Style),
            "ascii" => ViewStyles.Name(s.AsciiColour),
            "fps" => s.ShowFps ? "ON" : "OFF",
            "hud" => s.HudStyle == HudStyle.Modern ? "MODERN" : "CLASSIC",
            "uiscale" => UiScaleNames[(int)s.UiScale],
            "3dview" => StereoOptions.ModeName(s.Stereo) + " ...",
            _ => "",
        };
    }

    public override void Draw(MenuPainter p)
    {
        var panel = new Rectangle(Origin, Size);
        DosDialog.Panel(p, panel);
        p.TextIn(DosDialog.TitleFont, "DISPLAY", At(new Rectangle(0, 4, Size.Width, 14)), MenuPainter.TextPale);
        var applied = DisplayChoice.From(Menu.Settings);
        for (var i = _first; i < Rows.Length; i++)
        {
            var (id, label) = Rows[i];
            var box = At(ValueBox(i - _first));
            p.Text(DosDialog.Font, label, Origin.X + 10, box.Y + 2, MenuPainter.TextPale);
            DosDialog.Box(p, box);
            if (id == "brightness")
            {
                var s = Menu.Settings;
                var w = (box.Width - 2) * (s.Brightness - MenuSettings.MinBrightness) / (MenuSettings.MaxBrightness - MenuSettings.MinBrightness);
                p.Fill(new Rectangle(box.X + 1, box.Y + 1, w, box.Height - 2), new Vector4(0.35f, 0.85f, 0.45f, 0.85f));
                p.TextIn(DosDialog.Font, Value(id), box, MenuPainter.TextPale);
                continue;
            }

            var changed = id == "mode" ? Pending.Mode != applied.Mode : id == "size" && Pending.Shown(_modes) != applied.Shown(_modes);
            // ASCII COLOUR is kept for RETRO ASCII: greyed under the other renderers.
            var unused = id == "ascii" && Menu.Settings.Style != RenderStyle.Ascii;
            p.TextIn(DosDialog.Font, Value(id), box, changed ? DosDialog.Pending : unused ? Greyed : MenuPainter.TextGreen);
        }

        DosDialog.Button(p, At(ApplyRect), "APPLY", Dirty);
        DosDialog.Button(p, At(EnhancedRect), EnhancedScreen.Caption, Menu.Settings.Look == Look.Enhanced);
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

/// <summary>
/// KEEP THESE SETTINGS? after APPLY on DISPLAY: YES keeps (and saves) the new window; NO, Esc, or
/// <see cref="Seconds"/> without an answer put the old one back. Its YES / NO are QUIT.IMG's, lit
/// with QUITBTN.CFA, on a stone box of the DISPLAY screen's make, drawn over that screen.
/// </summary>
public sealed class KeepBox : PanelScreen
{
    public const float Seconds = 10f;
    public static readonly Size BoxSize2 = new(150, 56);
    /// <summary>QUIT.IMG's YES / NO strip.</summary>
    public static readonly Rectangle Strip = new(0, BoxYes.Y, BoxSize.Width, BoxYes.Height);
    public static readonly Point StripAt = new((BoxSize2.Width - BoxSize.Width) / 2, BoxSize2.Height - BoxYes.Height - 3);

    private readonly DisplayChoice _previous;

    public KeepBox(DisplayChoice previous) : base(QuitImage, BoxSize2, DetailScale) => _previous = previous;

    public override string Name => "keep";

    public override bool ShowsBelow => true;

    /// <summary>Seconds until the old window comes back.</summary>
    public float Left { get; private set; } = Seconds;

    public override void Opened()
    {
        if (Items.Count > 0)
        {
            return;
        }

        var at = new Point(StripAt.X - Strip.X, StripAt.Y - Strip.Y);
        Add(new MenuItem("yes", At(new Rectangle(at.X + BoxYes.X, at.Y + BoxYes.Y, BoxYes.Width, BoxYes.Height)))
        {
            Lit = Lit(QuitLit, new Point(at.X + BoxLitAt.X, at.Y + BoxLitAt.Y), 0),
            Activate = Keep,
        });
        Add(new MenuItem("no", At(new Rectangle(at.X + BoxNo.X, at.Y + BoxNo.Y, BoxNo.Width, BoxNo.Height)))
        {
            Lit = Lit(QuitLit, new Point(at.X + BoxLitAt.X, at.Y + BoxLitAt.Y), 1),
            Activate = Revert,
        });
        Highlight = 1;
    }

    public override void Tick(float seconds)
    {
        Left -= seconds;
        if (Left <= 0f)
        {
            Revert();
        }
    }

    public override void Back() => Revert();

    /// <summary>YES: the window stays and is saved.</summary>
    public void Keep()
    {
        Menu.Pop();
        Menu.Changed();
    }

    /// <summary>NO, Esc or the time out: the old window comes back.</summary>
    public void Revert()
    {
        if (Menu.Current != this)
        {
            return;
        }

        _previous.ApplyTo(Menu.Settings);
        Menu.Pop();
        Menu.Try();
        Menu.ShowToast("DISPLAY SETTINGS REVERTED");
    }

    public override void Draw(MenuPainter p)
    {
        DosDialog.Panel(p, new Rectangle(Origin, Size));
        p.TextIn(DosDialog.Font, "KEEP THESE SETTINGS?", At(new Rectangle(0, 6, Size.Width, 10)), MenuPainter.TextPale);
        var left = (int)MathF.Ceiling(Math.Max(Left, 0f));
        p.TextIn(DosDialog.Font, string.Create(CultureInfo.InvariantCulture, $"REVERTING IN {left}"), At(new Rectangle(0, 18, Size.Width, 10)), DosDialog.Pending);
        var strip = At(new Rectangle(StripAt, Strip.Size));
        if (!p.ImagePart(QuitImage, MenuPalette, Strip, strip))
        {
            foreach (var item in Items)
            {
                DosDialog.Button(p, item.Rect, item.Id.ToUpperInvariant());
            }
        }

        if (Highlighted is { } h && Menu.Current == this)
        {
            p.Highlight(h);
        }
    }
}
