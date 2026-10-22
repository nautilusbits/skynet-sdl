using System.Text.RegularExpressions;
using SkyNet.Formats;
using SkyNet.Game.Menu;
using SkyNet.Game.Tests.Audio;
using Xunit;

namespace SkyNet.Game.Tests.Menu;

/// <summary>The mission screen's TACTICAL tab against both games' data.</summary>
public partial class TacticalTests
{
    [GeneratedRegex(@"^\d{3}\.TXT$", RegexOptions.IgnoreCase)]
    private static partial Regex BriefingName();

    /// <summary>SkyNET, then Future Shock when it sits beside it.</summary>
    private static IEnumerable<GameData> Games()
    {
        var skynet = AudioTestData.Open();
        yield return skynet;

        if (GameDataLocator.OtherGameDir(skynet.Dir) is { } shock && GameData.Open(shock, skynet.Dir) is { } fs)
        {
            yield return fs;
        }
    }

    /// <summary>A .COL as MenuPainter.Palette finds it: the front archive, loose, the UI palette.</summary>
    private static Palette PaletteOf(GameData data, BsaArchive images, string col) =>
        Palette.Parse(images.Read(col) ?? data.ColBytes(col) ?? data.UiPaletteBytes()!)!;

    /// <summary>TACTBAK.IMG is a dim grey-green circuit board in BRIEF.COL (mean RGB 55, 68, 61);
    /// through BRIEF2.COL it turned into bright cyan noise (17, 110, 86).</summary>
    [AudioDataFact]
    public void Backdrop_is_dim_grey_green()
    {
        foreach (var data in Games())
        {
            var (path, variant) = data.FrontArchive("MDMDIMGS.BSA");
            using var images = BsaArchive.Open(path, variant);
            var img = ImgFile.Parse(images.Read(MenuLayout.TacticalBackdrop)!)!;
            var pal = PaletteOf(data, images, MenuLayout.TacticalPalette);

            double r = 0, g = 0;
            foreach (var i in img.Pixels)
            {
                r += pal[i].R;
                g += pal[i].G;
            }

            (r, g) = (r / img.Pixels.Length, g / img.Pixels.Length);
            Assert.True(g < 80 && r > 0.7 * g, $"{data.Game}: mean R {r:F0} G {g:F0}");
        }
    }

    /// <summary>Every [TA] dossier of every mission has its spinning model and its text.</summary>
    [AudioDataFact]
    public void Every_dossier_has_model_and_text()
    {
        foreach (var data in Games())
        {
            var (path, variant) = data.FrontArchive("MDMDBRIF.BSA");
            using var front = BsaArchive.Open(path, variant);
            using var own = data.OpenArchive("MDMDBRIF.BSA");
            var names = own.Entries.Where(e => BriefingName().IsMatch(e.Name))
                .SelectMany(e => Briefing.Parse(own.Read(e.Name)!).Tactical)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            Assert.True(names.Count > 10, $"{data.Game}: {names.Count} dossiers");

            foreach (var name in names)
            {
                Assert.True(TacticalPreview.Load(data, name) is { Faces.Length: > 0 }, $"{data.Game}: {name}.3D");
                Assert.True(front.Read($"{name}.TXT") is { Length: > 0 }, $"{data.Game}: {name}.TXT");
            }
        }
    }
}
