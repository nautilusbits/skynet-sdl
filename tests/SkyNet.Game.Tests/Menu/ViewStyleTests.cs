using System.Numerics;
using RetroEng.Platform;
using RetroEng.Render;
using SkyNet.Game.Menu;

namespace SkyNet.Game.Tests.Menu;

/// <summary>DISPLAY's RENDERER and ASCII COLOUR: the settings file, the rows, and the styles handed to the renderer.</summary>
public class ViewStyleTests : IDisposable
{
    private static readonly MenuFrame Frame = new(1280, 800);
    private static readonly DisplayModes Modes = new(new Resolution(1920, 1080), new Resolution(1920, 1040), [new(1920, 1080)]);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "skynet-style-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }
    }

    private static (MenuSystem Menu, SettingsRecorder Log) OpenDisplay()
    {
        var log = new SettingsRecorder();
        var menu = new MenuSystem(log, new MenuSettings(), new MenuContext { Display = () => Modes });
        menu.OpenTitle();
        foreach (var id in new[] { "options", "display" })
        {
            menu.Current!.Select(id);
            menu.Update(new MenuInput { Accept = true }, Frame);
        }

        Assert.Equal("display", menu.Current!.Name);
        return (menu, log);
    }

    [Fact]
    public void StyleRoundTrips()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "settings.json");
        Assert.Equal(SaveResult.Saved, SettingsStore.Save(new MenuSettings { Style = RenderStyle.PointCloud, AsciiColour = AsciiColour.Amber }, path));

        var back = SettingsStore.Load(path);
        Assert.Equal((RenderStyle.PointCloud, AsciiColour.Amber), (back.Style, back.AsciiColour));
    }

    [Fact]
    public void MissingStyleIsDefault()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, """{ "renderer": "Hologram" }""");

        var back = SettingsStore.Load(path);
        Assert.Equal((RenderStyle.Default, AsciiColour.Source), (back.Style, back.AsciiColour));
    }

    [Fact]
    public void RendererRowCyclesAndApplies()
    {
        var (menu, log) = OpenDisplay();
        var screen = (DisplayScreen)menu.Current!;
        menu.Current!.Select("renderer");

        // Right: DEFAULT → RETRO ASCII → POINT CLOUD → ENGRAVING → DEFAULT, each handed over at once.
        var seen = new List<string>();
        for (var i = 0; i < 4; i++)
        {
            menu.Update(new MenuInput { Right = true }, Frame);
            seen.Add(screen.Value("renderer"));
            Assert.Equal(menu.Settings.Style, log.Applied[^1].Style);
        }

        Assert.Equal(["RETRO ASCII", "POINT CLOUD", "ENGRAVING", "DEFAULT"], seen);
        menu.Update(new MenuInput { Left = true }, Frame);
        Assert.Equal(RenderStyle.Engraving, menu.Settings.Style);
    }

    [Fact]
    public void AsciiColourRowCycles()
    {
        var (menu, log) = OpenDisplay();
        var screen = (DisplayScreen)menu.Current!;
        menu.Current!.Select("ascii");

        var seen = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            menu.Update(new MenuInput { Accept = true }, Frame);
            seen.Add(screen.Value("ascii"));
        }

        Assert.Equal(["GREEN", "AMBER", "FULL COLOUR"], seen);
        Assert.Equal(AsciiColour.Source, log.Applied[^1].AsciiColour);
    }

    [Fact]
    public void RendererGetsFittedStyles()
    {
        var ascii = ViewStyles.Ascii(AsciiColour.Amber);
        Assert.Equal(AsciiColour.Amber, ascii.Colour);
        Assert.Equal(ViewStyles.AsciiBlack, ascii.BlackLevel);
        // The same cell grid at any resolution: the 1280 x 800 look, 133 rows.
        Assert.Equal(ViewStyles.AsciiRows, ascii.Rows);
        // Points readable on the dark maps: brightened, lifted, the scene behind, finer lattice up close.
        Assert.All([ViewStyles.PointCloud(), ViewStyles.MachineCloud()], c =>
            Assert.Equal((ViewStyles.PointGain, ViewStyles.PointLift, ViewStyles.PointBackdrop, ViewStyles.PointMaxPixelSpacing),
                (c.Gain, c.Lift, c.Backdrop, c.MaxPixelSpacing)));
        Assert.Equal((AsciiFont.Small, 4, 6), (ascii.Font, ascii.CellWidth, ascii.CellHeight));
        // No outline glyphs or lines: the picture shows through the glyphs and points alone.
        Assert.False(ascii.Edges);
        Assert.Equal(0f, ViewStyles.PointCloud().EdgeStrength);
        Assert.Equal(0f, ViewStyles.MachineCloud().EdgeStrength);

        // SkyNET's units: a soldier (80 tall) gets about a dozen points top to bottom.
        var cloud = ViewStyles.PointCloud();
        Assert.InRange(80f / cloud.Spacing, 8f, 20f);
        Assert.Equal(ViewStyles.PointBackground, cloud.Background);
    }

    [Fact]
    public void TerminatorVisionTakesTheMainViewOnlyWhileOn()
    {
        // While on, POINT CLOUD over any RENDERER; off (a HUMAN, dead, no match), DISPLAY's own.
        foreach (var display in ViewStyles.Words.Values)
        {
            Assert.Equal(display, ViewStyles.StyleOf(display, machinePoints: false));
            Assert.Equal(RenderStyle.PointCloud, ViewStyles.StyleOf(display, machinePoints: true));
        }

        Assert.False(ViewStyles.MachinePoints(null)); // the match left: the setting comes back
    }

    [Fact]
    public void TerminatorVisionIsDisplaysCloudInTheMachinesColours()
    {
        var plain = ViewStyles.PointCloud();
        var machine = ViewStyles.MachineCloud();
        Assert.Equal((plain.Spacing, plain.Density, plain.PointSize), (machine.Spacing, machine.Density, machine.PointSize));
        Assert.Equal(ViewStyles.MachineBackground, machine.Background);
        Assert.True(machine.Background.X > 2f * machine.Background.Y && machine.Background.X < 0.1f); // red-black
        Assert.Equal(ViewStyles.MachineEdge, machine.EdgeColour);
        Assert.True(machine.EdgeColour is { W: 1f, X: 1f } && machine.EdgeColour.Y < 0.5f); // its own red, not the surface's
    }

    [Fact]
    public void WordsCoverEveryStyle()
    {
        Assert.Equal([RenderStyle.Default, RenderStyle.Ascii, RenderStyle.PointCloud, RenderStyle.Engraving], ViewStyles.Words.Values.Order());
        Assert.Equal(RenderStyle.Engraving, ViewStyles.Words["engraving"]);
        Assert.Equal(ViewStyles.Words["ascii"], ViewStyles.Step(RenderStyle.Default, 1));
    }

    [Fact]
    public void EngravingRoundTripsAndOldFilesStillLoad()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "settings.json");
        Assert.Equal(SaveResult.Saved, SettingsStore.Save(new MenuSettings { Style = RenderStyle.Engraving }, path));
        Assert.Equal(RenderStyle.Engraving, SettingsStore.Load(path).Style);

        // A file from before ENGRAVING keeps its renderer.
        File.WriteAllText(path, File.ReadAllText(path).Replace("\"Engraving\"", "\"PointCloud\"", StringComparison.Ordinal));
        Assert.Equal(RenderStyle.PointCloud, SettingsStore.Load(path).Style);
    }

    [Fact]
    public void EngravingIsFittedToSkyNet()
    {
        var engraving = ViewStyles.Engraving();
        // SkyNET's world is Y-up: the lines run level across walls and slopes, not up them.
        Assert.Equal(Vector3.UnitY, engraving.Up);
        // The night maps are dark: brightened, or the print would be solid ink.
        Assert.True(engraving.Brightness > 0f);
        Assert.InRange(engraving.Spacing, EngravingStyle.MinSpacing, EngravingStyle.MaxSpacing);
    }
}
