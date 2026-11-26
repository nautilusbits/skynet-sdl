using RetroEng.Loop;
using RetroEng.Platform;
using SkyNet.Game.Campaign;

namespace SkyNet.Game.Tests.Campaign;

/// <summary>The campaign's default keys bind on every platform's key names (#74).</summary>
public class CampaignControlsTests
{
    [Fact]
    public void DefaultsBindWithoutSdl()
    {
        var c = CampaignControls.Create();
        var input = new Input(c.Actions);

        Assert.Equal(["Left Alt"], input.Bindings(c.Slide));
    }

    [Fact]
    public void MacSpellingsBind()
    {
        // macOS's SDL names Alt "Left Option": a binding saved there must load anywhere.
        var actions = new ActionSet();
        var slide = actions.Add("Slide", "Left Option");
        var input = new Input(actions);

        Assert.Single(input.Bindings(slide));
    }
}
