using RetroEng.Loop;
using SkyNet.Game.Campaign;

namespace SkyNet.Game.Tests.Campaign;

/// <summary>#30: the click that closes a menu (BEGIN, RETURN) is still held as play resumes; it
/// must not fire or throw.</summary>
public class MenuClickTests
{
    private static readonly CampaignControls Controls = CampaignControls.Create();

    private static TickInput Held(params int[] actions)
    {
        var bits = actions.Aggregate(0UL, (b, a) => b | TickInput.Bit(a));
        return new TickInput(bits, bits, 0UL, 0f, 0f, 0f, 0f, 0f);
    }

    [Fact]
    public void A_held_menu_click_neither_fires_nor_throws()
    {
        var tick = Controls.WithoutTriggers(Held(Controls.Fire, Controls.Throw, Controls.Forward));

        var weapons = Controls.WeaponInput(tick);
        Assert.False(weapons.Fire);
        Assert.False(weapons.Throw);
        Assert.True(tick.IsDown(Controls.Forward));
    }

    [Fact]
    public void The_triggers_count_as_held_until_let_go()
    {
        Assert.True(Controls.TriggersHeld(Held(Controls.Fire)));
        Assert.True(Controls.TriggersHeld(Held(Controls.Throw)));
        Assert.False(Controls.TriggersHeld(Held(Controls.Forward)));
    }
}
