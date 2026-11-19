using RetroEng.Platform;
using SkyNet.Game.Campaign;

namespace SkyNet.Game.Tests.Campaign;

/// <summary>The campaign's default keys, and the Alt spelling the engine's key table asks for.</summary>
public class CampaignControlsTests
{
    [Fact]
    public void TheSpellingFollowsWhetherSdlIsUp()
    {
        // SDL names the key as the platform does; the engine's own table spells it the Windows way.
        Assert.Equal(OperatingSystem.IsMacOS() ? "Left Option" : "Left Alt", CampaignControls.AltKeyFor(true));
        Assert.Equal("Left Alt", CampaignControls.AltKeyFor(false));
    }

    [Fact]
    public void TheDefaultsBindWithoutSdl()
    {
        var c = CampaignControls.Create();
        var input = new Input(c.Actions);

        Assert.Equal(["Left Alt"], input.Bindings(c.Slide));
    }
}
