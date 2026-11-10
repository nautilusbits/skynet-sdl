using System.Drawing;
using SkyNet.Formats;
using SkyNet.Game.Menu;
using SkyNet.Game.Tests.Behaviour;

namespace SkyNet.Game.Tests.Menu;

/// <summary>Future Shock's loading art (DEMOSCR3.IMG) is its demo menu: BEGIN top left, a red DOS top
/// right. The loading frame covers them (<see cref="MenuLayout.ShockLoadingButtons"/>).</summary>
public class ShockLoadingArtTests
{
    // The corners checked: the "THE" of the title starts at x 130, below them.
    private const int CornerWidth = 80;
    private const int CornerHeight = 20;

    // Brighter than the dark dither round the buttons (its channels stay under 30).
    private const int ButtonLevel = 40;

    private sealed class ShockDataFactAttribute : FactAttribute
    {
        public ShockDataFactAttribute()
        {
            if (BehaviourData.Dir is not { } dir || GameDataLocator.OtherGameDir(dir) == null)
            {
                Skip = "no Future Shock data beside SKYNET_DATA's SkyNET folder";
            }
        }
    }

    // A patch's mean level this close to the art just below it: it reads as the same background.
    private const double MeanTolerance = 6.0;

    [ShockDataFact]
    public void ButtonsAreCovered()
    {
        var (img, pal) = Art();

        // Every bright pixel in the top corners is a button's, and lies under a cover.
        var bright = 0;
        for (var y = 0; y < CornerHeight; y++)
        {
            for (var x = 0; x < img.Width; x++)
            {
                if (x >= CornerWidth && x < img.Width - CornerWidth)
                {
                    continue;
                }

                if (Level(img, pal, x, y) <= ButtonLevel)
                {
                    continue;
                }

                bright++;
                Assert.True(MenuLayout.ShockLoadingButtons.Any(c => c.Contains(x, y)), $"button pixel ({x}, {y}) uncovered");
            }
        }

        Assert.True(bright > 0, "no buttons in the art: the cover is not needed");
        Assert.Equal(MenuLayout.ShockLoadingButtons, MenuLayout.ShockLoadingPatches.Select(p => p.Target));
    }

    [ShockDataFact]
    public void PatchesMatchBackground()
    {
        var (img, pal) = Art();
        foreach (var (source, target) in MenuLayout.ShockLoadingPatches)
        {
            Assert.Equal(target.Size, source.Size);

            // Plain background: no bright pixel, and as dark as the strip below it.
            var below = source with { Y = source.Bottom };
            Assert.True(Max(img, pal, source) <= ButtonLevel, $"patch {source} is not plain background");
            Assert.InRange(Mean(img, pal, source) - Mean(img, pal, below), -MeanTolerance, MeanTolerance);
        }
    }

    private static (ImgFile Img, Palette Pal) Art()
    {
        var data = GameData.Open(GameDataLocator.OtherGameDir(BehaviourData.Dir!)!, BehaviourData.Dir)!;
        return (ImgFile.Parse(data.ColBytes(MenuLayout.ShockLoadingImage)!)!, Palette.Parse(data.ColBytes(MenuLayout.ShockLoadingPalette)!)!);
    }

    // A pixel's brightest channel.
    private static int Level(ImgFile img, Palette pal, int x, int y)
    {
        var (r, g, b) = pal[img.Pixels[y * img.Width + x]];
        return Math.Max(r, Math.Max(g, b));
    }

    private static IEnumerable<int> Levels(ImgFile img, Palette pal, Rectangle area) =>
        from y in Enumerable.Range(area.Y, area.Height)
        from x in Enumerable.Range(area.X, area.Width)
        select Level(img, pal, x, y);

    private static int Max(ImgFile img, Palette pal, Rectangle area) => Levels(img, pal, area).Max();

    private static double Mean(ImgFile img, Palette pal, Rectangle area) => Levels(img, pal, area).Average();
}
