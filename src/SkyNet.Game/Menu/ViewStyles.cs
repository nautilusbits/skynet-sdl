using System.Drawing;
using System.Numerics;
using RetroEng.Render;

namespace SkyNet.Game.Menu;

/// <summary>
/// DISPLAY's RENDERER and ASCII COLOUR: the engine's view styles (DEFAULT, RETRO ASCII, POINT CLOUD, ENGRAVING)
/// with parameters fitted to SkyNET's world (a soldier is about 80 units tall) and dark night maps.
/// The styles draw the world only; the HUD and menus stay as they are.
/// </summary>
public static class ViewStyles
{
    private static readonly RenderStyle[] Order = [RenderStyle.Default, RenderStyle.Ascii, RenderStyle.PointCloud, RenderStyle.Engraving];
    private static readonly AsciiColour[] Colours = [AsciiColour.Source, AsciiColour.Green, AsciiColour.Amber];

    /// <summary>The console's and <c>--renderer</c>'s words.</summary>
    public static readonly IReadOnlyDictionary<string, RenderStyle> Words = new Dictionary<string, RenderStyle>
    {
        ["default"] = RenderStyle.Default, ["ascii"] = RenderStyle.Ascii, ["pointcloud"] = RenderStyle.PointCloud,
        ["engraving"] = RenderStyle.Engraving,
    };

    public static readonly IReadOnlyDictionary<string, AsciiColour> ColourWords = new Dictionary<string, AsciiColour>
    {
        ["source"] = AsciiColour.Source, ["green"] = AsciiColour.Green, ["amber"] = AsciiColour.Amber,
    };

    /// <summary>Points every 6 units: about 13 down a soldier, still a lattice on the far ground.</summary>
    public const float PointSpacing = 6f;
    public const float PointDensity = 0.7f;
    public const float PointSize = 1.5f;
    /// <summary>Near black, a touch of blue: the night maps' sky stays dark behind the points.</summary>
    public static readonly Vector3 PointBackground = new(0.02f, 0.03f, 0.05f);
    /// <summary>The night maps are dark: points brightened, none below a grey, the scene dim behind them
    /// and the lattice finer where it would spread over 4 pixels, so robots and walls stay visible.</summary>
    public const float PointGain = 1.8f;
    public const float PointLift = 0.25f;
    public const float PointBackdrop = 0.25f;
    public const float PointMaxPixelSpacing = 4f;

    /// <summary>The night maps are dark: the glyph ramp starts lower and tops out earlier than the engine's.</summary>
    public const float AsciiBlack = 0.03f;
    public const float AsciiWhite = 0.4f;
    public const float AsciiGain = 1.2f;
    /// <summary>Cell rows at any resolution: the small font (3 x 5 glyphs in 4 x 6 cells) at 1280 x 800,
    /// 800 / 6 rows, kept for every frame height (the engine scales the cells, fractionally).</summary>
    public const int AsciiRows = 133;
    public static readonly Size AsciiCell = new(4, 6);
    /// <summary>The cell's own colour, dimmed, behind its glyph: the picture stays readable between the glyphs.</summary>
    public const float AsciiBackdrop = 1f;

    public static RenderStyle Step(RenderStyle s, int step) => Step(Order, s, step);

    public static AsciiColour Step(AsciiColour c, int step) => Step(Colours, c, step);

    public static string Name(RenderStyle s) => s switch
    {
        RenderStyle.Ascii => "RETRO ASCII",
        RenderStyle.PointCloud => "POINT CLOUD",
        RenderStyle.Engraving => "ENGRAVING",
        _ => "DEFAULT",
    };

    public static string Name(AsciiColour c) => c == AsciiColour.Source ? "FULL COLOUR" : c.ToString().ToUpperInvariant();

    /// <summary>RETRO ASCII in <paramref name="colour"/>, <see cref="AsciiRows"/> rows, the ramp fitted to the dark maps.</summary>
    public static AsciiStyle Ascii(AsciiColour colour) => new()
    {
        Colour = colour,
        Font = AsciiFont.Small,
        CellWidth = AsciiCell.Width,
        CellHeight = AsciiCell.Height,
        Rows = AsciiRows,
        Backdrop = AsciiBackdrop,
        InkTint = AsciiTint.Pixel,
        BackdropTint = AsciiTint.Pixel,
        BlackLevel = AsciiBlack,
        WhiteLevel = AsciiWhite,
        Gain = AsciiGain,
        Edges = false,
    };

    public static PointCloudStyle PointCloud() => new()
    {
        Spacing = PointSpacing,
        Density = PointDensity,
        PointSize = PointSize,
        Background = PointBackground,
        Gain = PointGain,
        Lift = PointLift,
        Backdrop = PointBackdrop,
        MaxPixelSpacing = PointMaxPixelSpacing,
        EdgeStrength = 0f,
    };

    /// <summary>The night maps are dark: brightened before the cut, or the print would be solid ink.</summary>
    private const float EngravingBrightness = 0.25f;

    /// <summary>ENGRAVING: the engine's wood engraving with its lines level in SkyNET's Y-up world.</summary>
    public static EngravingStyle Engraving() => new()
    {
        Up = Vector3.UnitY,
        Brightness = EngravingBrightness,
    };

    /// <summary>TERMINATOR VISION's background: near black with the machine palette's red in it.</summary>
    public static readonly Vector3 MachineBackground = new(0.06f, 0.005f, 0.005f);

    /// <summary>TERMINATOR VISION's creases and outlines: the red of the machine palette
    /// (<see cref="Net.DmHud.MachinePalette"/>), which then goes over the points as over everything.</summary>
    public static readonly Vector4 MachineEdge = new(1f, 0.22f, 0.16f, 1f);

    /// <summary>TERMINATOR VISION (the network screens' choice for a TERMINATOR): POINT CLOUD's lattice
    /// on a red-black background with red edges; the machine vision's marks and palette stay on top.</summary>
    public static PointCloudStyle MachineCloud() => new()
    {
        Spacing = PointSpacing,
        Density = PointDensity,
        PointSize = PointSize,
        Background = MachineBackground,
        Gain = PointGain,
        Lift = PointLift,
        Backdrop = PointBackdrop,
        MaxPixelSpacing = PointMaxPixelSpacing,
        EdgeColour = MachineEdge,
        EdgeStrength = 0f,
    };

    /// <summary>The main view's style for a frame: DISPLAY's <paramref name="display"/>, or POINT CLOUD
    /// while <paramref name="machinePoints"/> (<see cref="MachinePoints"/>).</summary>
    public static RenderStyle StyleOf(RenderStyle display, bool machinePoints) => machinePoints ? RenderStyle.PointCloud : display;

    /// <summary>TERMINATOR VISION is drawn this frame: a deathmatch is on and its player sees through the
    /// machine vision with the point cloud chosen (<see cref="Net.DmGame.PointCloudView"/>). No match,
    /// a HUMAN, a dead or drowned machine, or the choice off: DISPLAY's RENDERER.</summary>
    public static bool MachinePoints(Net.DmGame? game) => game?.PointCloudView == true;

    /// <summary>The settings' style on the main view (both eyes in stereo), read by the next frame;
    /// <paramref name="machinePoints"/>: TERMINATOR VISION's point cloud over it.</summary>
    public static void Apply(Renderer renderer, MenuSettings s, bool machinePoints = false)
    {
        renderer.Ascii = Ascii(s.AsciiColour);
        renderer.Engraving = Engraving();
        renderer.PointCloud = machinePoints ? MachineCloud() : PointCloud();
        renderer.Style = StyleOf(s.Style, machinePoints);
    }

    /// <summary>The next of <paramref name="order"/> (wrapping; a value not in it starts from the first).</summary>
    private static T Step<T>(T[] order, T value, int step)
    {
        var i = Array.IndexOf(order, value);
        return order[i < 0 ? 0 : ((i + step) % order.Length + order.Length) % order.Length];
    }
}
