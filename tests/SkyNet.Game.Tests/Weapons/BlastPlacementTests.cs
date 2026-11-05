using System.Numerics;
using SkyNet.Game.Weapons;

namespace SkyNet.Game.Tests.Weapons;

/// <summary>
/// Where a shot's explosion sits (DOS v1.01 0x12344b-0x123486, issue #36): 50 units back along its
/// flight from the hit point, and outdoors at least 8 above the terrain.
/// </summary>
public class BlastPlacementTests
{
    [Fact]
    public void StepsFiftyBackAlongTheFlight()
    {
        var at = Blast.Placement(new Vector3(100, 0, 0), new Vector3(0, -300, 0), null);

        Assert.Equal(new Vector3(100, 50, 0), at);
    }

    [Fact]
    public void StaysEightOverTheTerrain()
    {
        // A rocket skimming the ground: the step back lands under it, the terrain lifts it.
        var at = Blast.Placement(new Vector3(0, 10, 0), new Vector3(0, 30, -40), (_, _) => 20f);

        Assert.Equal(new Vector3(0, 28, 40), at);
    }

    [Fact]
    public void StandsStillWithoutAFlight()
    {
        Assert.Equal(new Vector3(1, 2, 3), Blast.Placement(new Vector3(1, 2, 3), Vector3.Zero, null));
    }
}
