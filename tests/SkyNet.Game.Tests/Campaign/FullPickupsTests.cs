using System.Numerics;
using SkyNet.Game.Campaign;
using SkyNet.Game.Demos;
using SkyNet.Game.Menu;
using SkyNet.Game.Tests.Behaviour;
using SkyNet.Game.Weapons;

namespace SkyNet.Game.Tests.Campaign;

/// <summary>Issue #25: FULL PICKUPS. TAKE (default) consumes every item as DOS does; LEAVE leaves
/// health, armour and ammo on the ground while that stat is full. A weapon is always taken.</summary>
public class FullPickupsTests
{
    private sealed class StatusLog : ISessionListener
    {
        public List<string> Lines { get; } = [];

        public void Status(string text) => Lines.Add(text);
    }

    private static bool IsAmmo(PickupItem i) => i.Weapon < 0 && i.Pool >= 0 && i.Amount > 0;

    private static bool IsHealth(PickupItem i) => i.HealPercent > 0;

    private static bool IsArmour(PickupItem i) => i.Armour > 0;

    private static bool IsWeapon(PickupItem i) => i.Weapon >= 0 && i.Pool >= 0;

    [BehaviourDataFact]
    public void TakeConsumesFullHealthAsDos()
    {
        var (s, offset, at, _) = Start(IsHealth, FullPickups.Take);

        Take(s, at);
        Assert.Contains(offset, s.TakenPickups);
    }

    [BehaviourDataFact]
    public void LeaveKeepsHealthWhenFull()
    {
        var (s, offset, at, log) = Start(IsHealth, FullPickups.Leave);

        // Two ticks on the item: it stays, and its line shows once.
        Take(s, at);
        Take(s, at);
        Assert.DoesNotContain(offset, s.TakenPickups);
        Assert.Equal([GameSession.HealthFull], log.Lines);
    }

    [BehaviourDataFact]
    public void LeaveTakesHealthWhenHurt()
    {
        var (s, offset, at, _) = Start(IsHealth, FullPickups.Leave);
        s.Soldier.Health = Soldier.MaxHealth / 2f;

        Take(s, at);
        Assert.Contains(offset, s.TakenPickups);
    }

    [BehaviourDataFact]
    public void LeaveKeepsArmourWhenFull()
    {
        var (s, offset, at, log) = Start(IsArmour, FullPickups.Leave);

        Take(s, at);
        Assert.DoesNotContain(offset, s.TakenPickups);
        Assert.Equal([GameSession.ArmourFull], log.Lines);
    }

    [BehaviourDataFact]
    public void LeaveKeepsAmmoWhenFull()
    {
        var (s, offset, at, log) = Start(IsAmmo, FullPickups.Leave);

        Take(s, at);
        Assert.DoesNotContain(offset, s.TakenPickups);
        Assert.Equal([GameSession.AmmoFull], log.Lines);
    }

    [BehaviourDataFact]
    public void TakeConsumesFullAmmoAsDos()
    {
        var (s, offset, at, _) = Start(IsAmmo, FullPickups.Take);

        Take(s, at);
        Assert.Contains(offset, s.TakenPickups);
    }

    [BehaviourDataFact]
    public void LeaveStillTakesAWeapon()
    {
        var (s, offset, at, _) = Start(IsWeapon, FullPickups.Leave);
        var weapon = ItemAt(s, offset).Weapon;
        s.Weapons.Inventory.Take(weapon);

        Take(s, at);
        Assert.Contains(offset, s.TakenPickups);
        Assert.True(s.Weapons.Inventory.Owns(weapon));
    }

    [Fact]
    public void ADemoWithoutTheFieldTakes()
    {
        var demo = new RetroEng.Loop.DemoData();
        Assert.Equal(FullPickups.Take, CampaignDemo.FullPickupsOf(demo));
        demo.SetField(CampaignDemo.KeyFullPickups, nameof(FullPickups.Leave));
        Assert.Equal(FullPickups.Leave, CampaignDemo.FullPickupsOf(demo));
    }

    [Fact]
    public void SettingDefaultsToTakeAndRoundTrips()
    {
        Assert.Equal(FullPickups.Take, new MenuSettings().FullPickups);

        var path = Path.Combine(Path.GetTempPath(), $"fullpickups-{Environment.ProcessId}-{Guid.NewGuid():n}.json");
        try
        {
            Assert.Equal(SaveResult.Saved, SettingsStore.Save(new MenuSettings { FullPickups = FullPickups.Leave }, path));
            Assert.Equal(FullPickups.Leave, SettingsStore.Load(path).FullPickups);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // Mission 1 begun, health, armour and every pool full; the first item of the kind sought.
    private static (GameSession, int, Vector3, StatusLog) Start(Func<PickupItem, bool> kind, FullPickups rule)
    {
        var log = new StatusLog();
        var s = new GameSession(BehaviourData.Data) { Listener = log };
        s.StartMission(s.Table.ByNumber(1)!.Value);
        s.FullPickups = rule;
        s.Soldier.Health = Soldier.MaxHealth;
        s.Soldier.Armour = 1f;
        var inv = s.Weapons.Inventory;
        for (var p = 0; p < inv.Table.Pools.Length; p++)
        {
            inv.AddAmmo(p, inv.Table.Pools[p].Max);
        }

        var (offset, at) = s.PickupsLeft.First(p => kind(ItemAt(s, p.Offset)));
        return (s, offset, at, log);
    }

    private static PickupItem ItemAt(GameSession s, int offset) =>
        PickupTable.Items[s.Map!.Entities.First(e => e.FileOffset == offset).SpriteIndex];

    // The feet put down at the spot for one tick.
    private static void Take(GameSession s, Vector3 feet)
    {
        var keys = new SkyNet.Game.Player.PlayerActions(0, 1, 2, 3, 4, 5, 6);
        s.Player.Spawn(feet, 0f);
        s.Tick(default, keys, new WeaponInput(false, false, false), false);
    }
}
