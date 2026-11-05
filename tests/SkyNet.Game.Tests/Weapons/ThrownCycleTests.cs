using System.Numerics;
using RetroEng.Loop;
using RetroEng.Physics;
using SkyNet.Game.Campaign;
using SkyNet.Game.Weapons;

namespace SkyNet.Game.Tests.Weapons;

/// <summary>The touch screen's ITEM button (#32): the next owned, non-empty thrown item, F1-F5 order.</summary>
public class ThrownCycleTests
{
    private const int Grenade = 16;

    [Fact]
    public void CycleSkipsEmptyAndWraps()
    {
        var table = WeaponTable.Shipped;
        var inv = new Inventory(table);
        foreach (var r in Inventory.ThrowKeys)
        {
            inv.Grant(r);
            inv.AddAmmo(table.Weapons[r].Pool, 99);
        }

        // The grenade's pool emptied, the rest kept.
        var pool = table.Weapons[Grenade].Pool;
        Assert.DoesNotContain(Inventory.ThrowKeys, r => r != Grenade && table.Weapons[r].Pool == pool);
        inv.AddAmmo(pool, -999);

        // F1-F5 order 15, 14, 16, 18, 19: the grenade skipped, 19 wraps to 15.
        var seen = new List<int>();
        for (var i = 0; i < 4; i++)
        {
            Assert.True(inv.CycleThrown());
            seen.Add(inv.Thrown);
        }

        Assert.Equal([14, 18, 19, 15], seen);
    }

    [Fact]
    public void TouchActionCyclesThroughWeapons()
    {
        var controls = CampaignControls.Create();
        Assert.Contains(controls.Actions.Touch, s => s.Action == controls.NextThrown);

        var weapons = new WeaponSystem(WeaponTable.Shipped, new CollisionWorld());
        foreach (var r in Inventory.ThrowKeys)
        {
            weapons.Inventory.Grant(r);
            weapons.Inventory.AddAmmo(WeaponTable.Shipped.Weapons[r].Pool, 99);
        }

        var bit = TickInput.Bit(controls.NextThrown);
        var input = controls.WeaponInput(new TickInput(bit, bit, 0UL, 0f, 0f, 0f, 0f, 0f));
        Assert.True(input.CycleThrown);

        weapons.Tick(input, new Shooter(Vector3.Zero, 0f, 0f), 1f / 60f);
        Assert.Equal(14, weapons.Inventory.Thrown);
    }
}
