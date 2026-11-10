using System.Numerics;
using RetroEng.Loop;
using RetroEng.Physics;
using SkyNet.Game.Campaign;
using SkyNet.Game.Tests.Behaviour;
using SkyNet.Game.Weapons;

namespace SkyNet.Game.Tests.Weapons;

/// <summary>#44: number keys pick a weapon group and cycle in it (DOS FUN_00125981, lists at
/// 0x43668); a pickup selects its gun only when it is new (FUN_0011d970).</summary>
public class WeaponKeyTests
{
    private const float Dt = 1f / 60f;

    private static Inventory Kit(params int[] records)
    {
        var inv = new Inventory(WeaponTable.Shipped);
        foreach (var r in records)
        {
            inv.Grant(r);
        }

        return inv;
    }

    /// <summary>Key <paramref name="key"/> (1-7) pressed once through the controls.</summary>
    private static void Press(WeaponSystem weapons, CampaignControls controls, int key)
    {
        var bit = TickInput.Bit(controls.WeaponKeys[key - 1]);
        var input = controls.WeaponInput(new TickInput(bit, bit, 0UL, 0f, 0f, 0f, 0f, 0f));
        weapons.Tick(input, new Shooter(Vector3.Zero, 0f, 0f), Dt);
        weapons.Inventory.Tick(1f);
    }

    [Fact]
    public void KeySevenCyclesThePlasmas()
    {
        var controls = CampaignControls.Create();
        var weapons = new WeaponSystem(WeaponTable.Shipped, new CollisionWorld());
        foreach (var r in new[] { 9, 10, 11 })
        {
            weapons.Inventory.Grant(r);
        }

        var seen = new List<int>();
        for (var i = 0; i < 4; i++)
        {
            Press(weapons, controls, 7);
            seen.Add(weapons.Inventory.Primary);
        }

        // Cannon, rifle, pistol, then round again.
        Assert.Equal([11, 10, 9, 11], seen);
    }

    [Fact]
    public void KeysGroupTheGuns()
    {
        var controls = CampaignControls.Create();
        var weapons = new WeaponSystem(WeaponTable.Shipped, new CollisionWorld());
        for (var r = 0; r <= WeaponTable.SuperUzi; r++)
        {
            weapons.Inventory.Grant(r);
        }

        int[] firsts = [0, 3, 4, 5, 6, 8, 11];
        for (var key = 1; key <= firsts.Length; key++)
        {
            Press(weapons, controls, key);
            Assert.Equal(firsts[key - 1], weapons.Inventory.Primary);
        }
    }

    [Fact]
    public void GroupSkipsUnownedAndHeld()
    {
        // The SkyNET start kit: PIPE, UZI, ASSAULT RIFLE, SHOTGUN, LASER RIFLE; the UZI in hand.
        var inv = Kit(0, 1, 2, 4, 7);
        Assert.Equal(1, inv.Primary);

        var seen = new List<int>();
        for (var i = 0; i < 3; i++)
        {
            Assert.True(inv.SelectGroup(1));
            seen.Add(inv.Primary);
            inv.Tick(1f);
        }

        // MACHINE GUN and SUPER UZI not owned: ASSAULT RIFLE, UZI, ASSAULT RIFLE.
        Assert.Equal([2, 1, 2], seen);

        // Another group starts at its head; one owned gun already held does nothing.
        Assert.True(inv.SelectGroup(5));
        Assert.Equal(7, inv.Primary);
        Assert.False(inv.SelectGroup(5));
    }

    [BehaviourDataFact]
    public void PickupSelectsOnlyNewGuns()
    {
        var session = new GameSession(BehaviourData.Data);
        var inv = session.Weapons.Inventory;
        Assert.True(inv.Select(2));

        // An UZI already owned: ammo only, the ASSAULT RIFLE stays in hand.
        session.Apply(PickupTable.Items[25737]);
        Assert.Equal(2, inv.Primary);

        // The MACHINE GUN is new: owned and in hand.
        Assert.False(inv.Owns(3));
        session.Apply(PickupTable.Items[25600]);
        Assert.True(inv.Owns(3));
        Assert.Equal(3, inv.Primary);
    }
}
