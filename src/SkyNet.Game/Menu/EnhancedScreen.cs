using System.Drawing;
using System.Globalization;
using System.Numerics;
using RetroEng.Render;
using static SkyNet.Game.Menu.MenuLayout;

namespace SkyNet.Game.Menu;

/// <summary>The ENHANCED OPTIONS screen's steps and names, without SDL or the GPU.</summary>
public static class EnhancedOptions
{
    private static readonly ShadowQuality[] ShadowOrder = [ShadowQuality.Off, ShadowQuality.Low, ShadowQuality.High];
    private static readonly AntiAliasing[] AaOrder = [AntiAliasing.Off, AntiAliasing.Fxaa, AntiAliasing.Msaa2, AntiAliasing.Msaa4];
    private static readonly ReliefLevel[] ReliefOrder = [ReliefLevel.Off, ReliefLevel.Low, ReliefLevel.Medium, ReliefLevel.High];
    private static readonly int[] Budgets = [MenuSettings.LowLightBudget, MenuSettings.HighLightBudget];
    private static readonly ParticleLevel[] ParticleOrder = [ParticleLevel.Off, ParticleLevel.Low, ParticleLevel.High];
    private static readonly TextureFiltering[] FilterOrder = [TextureFiltering.Nearest, TextureFiltering.Bilinear, TextureFiltering.Trilinear,
        TextureFiltering.Anisotropic2, TextureFiltering.Anisotropic4, TextureFiltering.Anisotropic8, TextureFiltering.Anisotropic16];

    public static ParticleLevel StepParticles(ParticleLevel p, int step) => Step(ParticleOrder, p, step);

    public static string Name(ParticleLevel p) => p.ToString().ToUpperInvariant();

    public static ShadowQuality StepShadows(ShadowQuality q, int step) => Step(ShadowOrder, q, step);

    public static ReliefLevel StepRelief(ReliefLevel r, int step) => Step(ReliefOrder, r, step);

    public static string Name(ReliefLevel r) => r.ToString().ToUpperInvariant();

    public static AntiAliasing StepAntiAliasing(AntiAliasing a, int step) => Step(AaOrder, a, step);

    public static TextureFiltering StepFilter(TextureFiltering f, int step) => Step(FilterOrder, f, step);

    public static int StepLightBudget(int budget, int step) => Step(Budgets, budget, step);

    public static int StepAmbient(int ambient, int step) =>
        Math.Clamp(ambient + step * MenuSettings.AmbientStep, MenuSettings.MinAmbient, MenuSettings.MaxAmbient);

    public static string Name(ShadowQuality q) => q switch
    {
        ShadowQuality.Off => "OFF",
        ShadowQuality.Low => "LOW",
        _ => "HIGH",
    };

    public static string Name(AntiAliasing a) => a switch
    {
        AntiAliasing.Fxaa => "FXAA",
        AntiAliasing.Msaa2 => "MSAA 2X",
        AntiAliasing.Msaa4 => "MSAA 4X",
        _ => "OFF",
    };

    public static string Name(TextureFiltering f) => f switch
    {
        TextureFiltering.Nearest => "NEAREST",
        TextureFiltering.Bilinear => "BILINEAR",
        TextureFiltering.Trilinear => "TRILINEAR",
        TextureFiltering.Anisotropic2 => "ANISOTROPIC 2X",
        TextureFiltering.Anisotropic4 => "ANISOTROPIC 4X",
        TextureFiltering.Anisotropic8 => "ANISOTROPIC 8X",
        _ => "ANISOTROPIC 16X",
    };

    public static string LightsName(int budget) => budget <= MenuSettings.LowLightBudget
        ? string.Create(CultureInfo.InvariantCulture, $"LOW ({MenuSettings.LowLightBudget})")
        : string.Create(CultureInfo.InvariantCulture, $"HIGH ({MenuSettings.HighLightBudget})");

    /// <summary>The next of <paramref name="order"/> after <paramref name="value"/> (wrapping; a value
    /// not in it starts from the first).</summary>
    private static T Step<T>(T[] order, T value, int step)
    {
        var i = Array.IndexOf(order, value);
        return order[i < 0 ? 0 : ((i + step) % order.Length + order.Length) % order.Length];
    }
}

/// <summary>
/// ENHANCED OPTIONS (DISPLAY → ENHANCED...), the port's own screen in the DISPLAY screen's make:
/// SHADOWS, ANTI-ALIASING, LIGHTS (the point-light budget), AMBIENT (the light all around, 50..200 %:
/// how dark walls away from the sun are), AMBIENT OCCLUSION, GLOW and SURFACE RELIEF (HD textures'
/// relief maps: their normal and roughness), PARTICLES (dust, ash and smoke) and TEXTURE FILTER. Every row applies and saves at
/// once (the game's ApplyLook). They belong to the ENHANCED look: under ORIGINAL they can be set
/// ahead, greyed, with a line saying so.
/// </summary>
public sealed class EnhancedScreen : PanelScreen
{
    /// <summary>The DISPLAY screen's button to here.</summary>
    public const string Caption = "ENHANCED...";
    public const string OriginalNote = "FOR THE ENHANCED LOOK ONLY";

    public static readonly Size PanelSize = DisplayScreen.PanelSize;
    public const int RowTop = DisplayScreen.RowTop;
    /// <summary>A pixel tighter than DISPLAY's: nine rows and the note fit above EXIT.</summary>
    public const int RowPitch = DisplayScreen.RowPitch - 1;
    public static readonly Rectangle ExitRect = DisplayScreen.ExitRect;
    /// <summary>The value boxes start further right than DISPLAY's: AMBIENT OCCLUSION is long.</summary>
    public static readonly Rectangle BoxAt = new(112, 0, 102, 10);
    public static readonly Rectangle NoteRect = new(0, RowTop + 9 * RowPitch - 1, ExitRect.X, 10);

    /// <summary>The row ids, top to bottom, and their labels.</summary>
    public static readonly (string Id, string Label)[] Rows =
    [
        ("shadows", "SHADOWS"), ("aa", "ANTI-ALIASING"), ("lights", "LIGHTS"), ("ambient", "AMBIENT"),
        ("occlusion", "AMBIENT OCCLUSION"), ("glow", "GLOW"), ("relief", "SURFACE RELIEF"), ("particles", "PARTICLES"),
        ("filter", "TEXTURE FILTER"),
    ];

    public static Rectangle ValueBox(int i) => new(BoxAt.X, RowTop + i * RowPitch, BoxAt.Width, BoxAt.Height);

    /// <summary>A row's clickable strip (DISPLAY's at this screen's pitch).</summary>
    public static Rectangle RowRect(int i) => new(4, RowTop + i * RowPitch - 1, PanelSize.Width - 8, RowPitch - 1);

    public EnhancedScreen() : base(OptionsImage, PanelSize, DetailScale)
    {
    }

    public override string Name => "enhanced";

    /// <summary>The look is ORIGINAL: the rows are kept for ENHANCED and shown greyed.</summary>
    public bool ForOriginal => Menu.Settings.Look != Look.Enhanced;

    public override void Opened()
    {
        if (Items.Count > 0)
        {
            return;
        }

        var s = Menu.Settings;
        Row(0, d => s.Shadows = EnhancedOptions.StepShadows(s.Shadows, d));
        Row(1, d => s.AntiAliasing = EnhancedOptions.StepAntiAliasing(s.AntiAliasing, d));
        Row(2, d => s.LightBudget = EnhancedOptions.StepLightBudget(s.LightBudget, d));
        var bar = ValueBox(3);
        var row = RowRect(3);
        Add(new MenuItem(Rows[3].Id, At(row))
        {
            Adjust = d => Live(() => s.Ambient = EnhancedOptions.StepAmbient(s.Ambient, d)),
            Activate = () => Live(() => s.Ambient = s.Ambient >= MenuSettings.MaxAmbient
                ? MenuSettings.MinAmbient
                : EnhancedOptions.StepAmbient(s.Ambient, 1)),
            ClickAt = t =>
            {
                var x = row.X + t * row.Width;
                var f = Math.Clamp((x - bar.X) / bar.Width, 0f, 1f);
                var steps = (MenuSettings.MaxAmbient - MenuSettings.MinAmbient) / MenuSettings.AmbientStep;
                Live(() => s.Ambient = MenuSettings.MinAmbient + (int)MathF.Round(f * steps) * MenuSettings.AmbientStep);
            },
        });
        Row(4, _ => s.AmbientOcclusion = !s.AmbientOcclusion);
        Row(5, _ => s.Glow = !s.Glow);
        Row(6, d => s.Relief = EnhancedOptions.StepRelief(s.Relief, d));
        Row(7, d => s.Particles = EnhancedOptions.StepParticles(s.Particles, d));
        Row(8, d => s.TextureFiltering = EnhancedOptions.StepFilter(s.TextureFiltering, d));
        Add(new MenuItem("exit", At(ExitRect))
        {
            Lit = new LitArt(OptionsLit, 11, new Point(Origin.X + ExitRect.X - OptionsExit.X + OptionsLitAt.X, Origin.Y + ExitRect.Y - OptionsExit.Y + OptionsLitAt.Y)),
            Activate = Menu.Pop,
        });
        Highlight = 0;
    }

    /// <summary>A stepping row that applies at once: left / right by one, Accept or a click on by one.</summary>
    private void Row(int i, Action<int> step) =>
        Add(new MenuItem(Rows[i].Id, At(RowRect(i))) { Adjust = d => Live(() => step(d)), Activate = () => Live(() => step(1)) });

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
            "shadows" => EnhancedOptions.Name(s.Shadows),
            "aa" => EnhancedOptions.Name(s.AntiAliasing),
            "lights" => EnhancedOptions.LightsName(s.LightBudget),
            "ambient" => string.Create(CultureInfo.InvariantCulture, $"{s.Ambient}%"),
            "occlusion" => s.AmbientOcclusion ? "ON" : "OFF",
            "glow" => s.Glow ? "ON" : "OFF",
            "relief" => EnhancedOptions.Name(s.Relief),
            "particles" => EnhancedOptions.Name(s.Particles),
            "filter" => EnhancedOptions.Name(s.TextureFiltering),
            _ => "",
        };
    }

    public override void Draw(MenuPainter p)
    {
        DosDialog.Panel(p, new Rectangle(Origin, Size));
        p.TextIn(DosDialog.TitleFont, "ENHANCED OPTIONS", At(new Rectangle(0, 4, Size.Width, 14)), MenuPainter.TextPale);
        var grey = ForOriginal;
        var valueColour = grey ? Greyed : MenuPainter.TextGreen;
        var labelColour = grey ? Greyed : MenuPainter.TextPale;
        for (var i = 0; i < Rows.Length; i++)
        {
            var (id, label) = Rows[i];
            var box = At(ValueBox(i));
            p.Text(DosDialog.Font, label, Origin.X + 10, box.Y + 2, labelColour);
            DosDialog.Box(p, box);
            if (id == "ambient")
            {
                var s = Menu.Settings;
                var w = (box.Width - 2) * (s.Ambient - MenuSettings.MinAmbient) / (MenuSettings.MaxAmbient - MenuSettings.MinAmbient);
                p.Fill(new Rectangle(box.X + 1, box.Y + 1, w, box.Height - 2),
                    grey ? new Vector4(0.35f, 0.4f, 0.4f, 0.85f) : new Vector4(0.35f, 0.85f, 0.45f, 0.85f));
                p.TextIn(DosDialog.Font, Value(id), box, labelColour);
                continue;
            }

            p.TextIn(DosDialog.Font, Value(id), box, valueColour);
        }

        if (grey)
        {
            p.TextIn(DosDialog.Font, OriginalNote, At(NoteRect), DosDialog.Pending);
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

    private static readonly Vector4 Greyed = new(0.45f, 0.5f, 0.5f, 1f);
}
