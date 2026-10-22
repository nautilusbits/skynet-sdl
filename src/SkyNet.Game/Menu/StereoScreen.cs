using System.Drawing;
using System.Globalization;
using System.Numerics;
using RetroEng.Platform;
using RetroEng.Render;
using static SkyNet.Game.Menu.MenuLayout;

namespace SkyNet.Game.Menu;

/// <summary>The 3D VIEW screen's steps and names, without SDL or the GPU.</summary>
public static class StereoOptions
{
    /// <summary>DEPTH's steps (world units; the soldier's eye is 75 high).</summary>
    public static readonly int[] Depths = [50, 100, 150, 200, 300, 500, 800, 1200, 2000];
    /// <summary>CANVAS DEPTH's steps (0: at the screen, the convergence depth).</summary>
    public static readonly int[] CanvasDepths = [0, 50, 100, 150, 200, 300, 500];

    public static string StepMode(string mode, int step) => Step(StereoOutput.Names, StereoOutput.Parse(mode)?.Name ?? "off", step);

    public static int StepSeparation(int separation, int step) => Math.Clamp(separation + step, 0, MenuSettings.MaxStereoSeparation);

    public static int StepDepth(int depth, int step) => Step(Depths, Nearest(Depths, depth), step);

    public static int StepCanvasDepth(int depth, int step) => Step(CanvasDepths, Nearest(CanvasDepths, depth), step);

    public static string StepViewer(string viewer, int step) => Step(LensProfile.Names, viewer, step);

    public static HeadTracking StepHead(HeadTracking head, int step) => Step([HeadTracking.Off, HeadTracking.Gyro], head, step);

    public static AimCoupling StepAim(AimCoupling aim, int step) => Step([AimCoupling.Coupled, AimCoupling.Decoupled], aim, step);

    public static string ModeName(string mode) => StereoOutput.Parse(mode)?.Name switch
    {
        "sbs" => "SIDE BY SIDE",
        "sbsr" => "SBS SWAPPED",
        "sbsfull" => "SBS FULL",
        "crossview" => "CROSS VIEW",
        "tab" => "TOP / BOTTOM",
        "tabr" => "T / B SWAPPED",
        "int" => "INTERLACED",
        "intr" => "INT. SWAPPED",
        "viewer" => "PHONE VIEWER",
        _ => "OFF",
    };

    public static string ViewerName(MenuSettings s) => s.Viewer switch
    {
        "generic" => "GENERIC",
        "cardboard1" => "CARDBOARD",
        "cardboard2" => "CARDBOARD 2",
        "flat" => "NO LENS",
        _ => string.Create(CultureInfo.InvariantCulture, $"CUSTOM {s.LensK1 / 100f:0.00} {s.LensK2 / 100f:0.00}"),
    };

    /// <summary>The value nearest <paramref name="value"/> in <paramref name="order"/> (a depth set elsewhere).</summary>
    private static int Nearest(int[] order, int value) => order.MinBy(v => Math.Abs(v - value));

    private static T Step<T>(T[] order, T value, int step)
    {
        var i = Array.IndexOf(order, value);
        return order[i < 0 ? 0 : ((i + step) % order.Length + order.Length) % order.Length];
    }
}

/// <summary>
/// 3D VIEW (OPTIONS → DISPLAY → 3D VIEW), the port's own screen in the DISPLAY screen's make: MODE (the
/// stereo output: off, side by side, top / bottom, interlaced, the phone viewer), SEPARATION (the eyes'
/// distance apart), DEPTH (what shows at the screen), CANVAS DEPTH (where the HUD and menus show), VIEWER
/// (the phone viewer's lens), HEAD TRACKING (the phone's gyroscope), AIM (the head turns the aim, or
/// looks on its own) and BACKLIGHT (the phone screen's brightness; auto: full in the viewer). Every row
/// applies at once (also in game) and is saved. Not DOS's STEREO on OPTIONS, which swaps the sound's channels.
/// </summary>
public sealed class StereoScreen : PanelScreen
{
    /// <summary>The DISPLAY screen's row to here.</summary>
    public const string Caption = "3D VIEW";

    public static readonly Size PanelSize = DisplayScreen.PanelSize;
    public const int RowTop = DisplayScreen.RowTop;
    public const int RowPitch = DisplayScreen.RowPitch;
    public static readonly Rectangle ExitRect = DisplayScreen.ExitRect;
    public static readonly Rectangle BoxAt = DisplayScreen.BoxAt;

    /// <summary>The row ids, top to bottom, and their labels.</summary>
    public static readonly (string Id, string Label)[] Rows =
    [
        ("mode", "MODE"), ("separation", "SEPARATION"), ("depth", "DEPTH"), ("canvas", "CANVAS DEPTH"),
        ("viewer", "VIEWER"), ("head", "HEAD TRACKING"), ("aim", "AIM"), ("brightness", "BACKLIGHT"),
    ];

    public static Rectangle ValueBox(int i) => new(BoxAt.X, RowTop + i * RowPitch, BoxAt.Width, BoxAt.Height);

    public static Rectangle RowRect(int i) => new(4, RowTop + i * RowPitch - 1, PanelSize.Width - 8, RowPitch - 1);

    public StereoScreen() : base(OptionsImage, PanelSize, DetailScale)
    {
    }

    public override string Name => "3dview";

    public override void Opened()
    {
        if (Items.Count > 0)
        {
            return;
        }

        var s = Menu.Settings;
        Row(0, d => s.Stereo = StereoOptions.StepMode(s.Stereo, d));
        Row(1, d => s.StereoSeparation = StereoOptions.StepSeparation(s.StereoSeparation, d));
        Row(2, d => s.StereoDepth = StereoOptions.StepDepth(s.StereoDepth, d));
        Row(3, d => s.CanvasDepth = StereoOptions.StepCanvasDepth(s.CanvasDepth, d));
        Row(4, d => s.Viewer = StereoOptions.StepViewer(s.Viewer, d));
        Row(5, d => s.HeadTracking = StereoOptions.StepHead(s.HeadTracking, d));
        Row(6, d => s.Aim = StereoOptions.StepAim(s.Aim, d));
        Row(7, d => s.ScreenBrightness = ScreenBrightnessLevels.Step(s.ScreenBrightness, d));
        Add(new MenuItem("exit", At(ExitRect))
        {
            Lit = new LitArt(OptionsLit, 11, new Point(Origin.X + ExitRect.X - OptionsExit.X + OptionsLitAt.X, Origin.Y + ExitRect.Y - OptionsExit.Y + OptionsLitAt.Y)),
            Activate = Menu.Pop,
        });
        Highlight = 0;
    }

    /// <summary>A stepping row that applies at once: left / right by one, Accept or a click on by one.</summary>
    private void Row(int i, Action<int> step) =>
        Add(new MenuItem(Rows[i].Id, At(RowRect(i)))
        {
            Adjust = d =>
            {
                step(d);
                Menu.Changed();
            },
            Activate = () =>
            {
                step(1);
                Menu.Changed();
            },
        });

    /// <summary>The text of a row's value box.</summary>
    public string Value(string id)
    {
        var s = Menu.Settings;
        var c = CultureInfo.InvariantCulture;
        return id switch
        {
            "mode" => StereoOptions.ModeName(s.Stereo),
            "separation" => string.Create(c, $"{s.StereoSeparation}"),
            "depth" => string.Create(c, $"{s.StereoDepth}"),
            "canvas" => s.CanvasDepth == 0 ? "AT THE SCREEN" : string.Create(c, $"{s.CanvasDepth}"),
            "viewer" => StereoOptions.ViewerName(s),
            "head" => s.HeadTracking == HeadTracking.Off ? "OFF" : "GYROSCOPE",
            "aim" => s.Aim == AimCoupling.Coupled ? "WITH THE HEAD" : "ON ITS OWN",
            "brightness" => ScreenBrightnessLevels.Name(s.ScreenBrightness),
            _ => "",
        };
    }

    public override void Draw(MenuPainter p)
    {
        DosDialog.Panel(p, new Rectangle(Origin, Size));
        p.TextIn(DosDialog.TitleFont, Caption, At(new Rectangle(0, 4, Size.Width, 14)), MenuPainter.TextPale);
        for (var i = 0; i < Rows.Length; i++)
        {
            var (id, label) = Rows[i];
            var box = At(ValueBox(i));
            p.Text(DosDialog.Font, label, Origin.X + 10, box.Y + 2, MenuPainter.TextPale);
            DosDialog.Box(p, box);
            p.TextIn(DosDialog.Font, Value(id), box, MenuPainter.TextGreen);
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
