using System.Drawing;
using SkyNet.Formats;
using SkyNet.Game.Tests.Behaviour;
using SkyNet.Game.Weapons;

namespace SkyNet.Game.Tests.Weapons;

/// <summary>
/// Issue #44: WEAPON06 (rocket launcher) and WEAPON11 (plasma cannon) end in a hard cut that DOS's
/// 320-pixel screen clipped; a wider screen showed it. Such art's last column is stretched to the
/// screen's right edge; the gun's own rectangle (its aim) stays.
/// </summary>
public class ViewModelEdgeTests
{
    private static readonly Size Rocket = new(158, 147), Plasma = new(216, 98);
    private const int RocketX = 175, PlasmaX = 128;

    [BehaviourDataFact]
    public void OnlyTheRocketAndPlasmaCannonArtIsCut()
    {
        using var a = BehaviourData.Data.OpenArchive("MDMDIMGS.BSA");
        var cut = Enumerable.Range(0, 14).Where(r => CfaFile.Parse(a.Read($"WEAPON{r:D2}.CFA")!) is { } c
            && ViewModelLayout.HardCut(c.Frames[0].AsSpan(), c.Width, c.Height)).ToArray();

        Assert.Equal([6, 11], cut);
    }

    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(2560, 1080)]
    [InlineData(3440, 1440)]
    public void TheCutIsFilledToTheEdgeAndTheGunStays(int w, int h)
    {
        var gun = ViewModelLayout.Place(w, h, RocketX, Rocket);
        var fill = ViewModelLayout.EdgeFill(gun, w);

        Assert.NotNull(fill);
        Assert.Equal(gun.Right, fill.Value.Left);
        Assert.Equal(w, fill.Value.Right);
        Assert.Equal(gun.Top, fill.Value.Top);
        Assert.Equal(gun.Bottom, fill.Value.Bottom);
    }

    [Fact]
    public void ArtPastTheEdgeNeedsNoFill()
    {
        Assert.Null(ViewModelLayout.EdgeFill(ViewModelLayout.Place(1920, 1080, PlasmaX, Plasma), 1920));
        Assert.Null(ViewModelLayout.EdgeFill(ViewModelLayout.Place(1280, 960, RocketX, Rocket), 1280));
    }
}
