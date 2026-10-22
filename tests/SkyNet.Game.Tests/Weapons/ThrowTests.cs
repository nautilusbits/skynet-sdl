using System.Numerics;
using RetroEng.Physics;
using SkyNet.Game.Campaign;
using SkyNet.Game.Player;
using SkyNet.Game.Tests.Behaviour;
using SkyNet.Game.Vehicles;
using SkyNet.Game.Weapons;

namespace SkyNet.Game.Tests.Weapons;

/// <summary>The THROW key on foot: its own register and its own cooldown beside the gun's
/// (docs game-logic/throwables.md), and back after a ride.</summary>
public class ThrowTests
{
    private const float Dt = 1f / 60f;

    private static IEnumerable<Triangle> Floor(float y, float half = 100000f)
    {
        yield return new Triangle(new(-half, y, -half), new(half, y, -half), new(half, y, half));
        yield return new Triangle(new(-half, y, -half), new(half, y, half), new(-half, y, half));
    }

    [Fact]
    public void GunAndThrowFireInTheSameTickOnTheirOwnCooldowns()
    {
        var weapons = new WeaponSystem(WeaponTable.Shipped, new CollisionWorld().Add(new TriangleGrid(Floor(-100f))));
        var fired = new List<(int Record, bool Thrown)>();
        var reports = new List<ShotReport>();
        weapons.Fired += (r, t) => fired.Add((r, t));
        weapons.Reported += reports.Add;
        var inv = weapons.Inventory;
        Assert.Equal(1, inv.Primary);                                 // UZI in hand
        Assert.Equal(Inventory.DefaultThrown, inv.Thrown);            // MOLOTOV (15) selected
        Assert.Equal(15, inv.Thrown);
        Assert.Equal(20, inv.AmmoFor(15));
        Assert.Equal(500, inv.AmmoFor(1));
        var shooter = new Shooter(Vector3.Zero, 0f, 0f);

        weapons.Tick(new WeaponInput(Fire: true, Throw: true, Run: false), shooter, Dt);
        Assert.Equal([(1, false), (15, true)], fired);
        Assert.Equal(499, inv.AmmoFor(1));
        Assert.Equal(19, inv.AmmoFor(15));
        var molotov = Assert.Single(weapons.Projectiles);
        Assert.Equal(15, molotov.Weapon.Index);

        // Both held: the UZI fires again after its 0.2 s while the throw waits out its second.
        fired.Clear();
        for (var t = 1; t < 60; t++)
        {
            weapons.Tick(new WeaponInput(Fire: true, Throw: true, Run: false), shooter, Dt);
        }

        Assert.Equal(4, fired.Count(f => !f.Thrown));
        Assert.DoesNotContain(fired, f => f.Thrown);
        weapons.Tick(new WeaponInput(Fire: true, Throw: true, Run: false), shooter, Dt);
        Assert.Contains((15, true), fired);
        Assert.Equal(18, inv.AmmoFor(15));

        // The first molotov came down on the floor and burst there.
        Assert.Contains(reports, r => r.Weapon == 15 && r.What == "burst");
    }

    [BehaviourDataFact]
    public void ThrowOnFootWorksAfterARide()
    {
        var s = new GameSession(BehaviourData.Data);
        var rig = new VehicleRig(s);
        s.StartMission(s.Table.ByNumber(1)!.Value);
        Assert.False(rig.Riding);
        var keys = new PlayerActions(0, 1, 2, 3, 4, 5, 6);
        var thrown = new List<int>();
        s.Weapons.Fired += (r, t) =>
        {
            if (t)
            {
                thrown.Add(r);
            }
        };

        // In the jeep THROW is the rocket pod: the molotovs stay in the pool.
        rig.Board(VehicleMode.Jeep);
        Assert.NotNull(s.Weapons.Mount);
        s.Tick(default, keys, new WeaponInput(Fire: false, Throw: true, Run: false), false);
        Assert.Empty(thrown);
        Assert.Equal(20, s.Weapons.Inventory.AmmoFor(15));

        // Out of it: no mount, and THROW lobs the selected molotov again.
        rig.Board(VehicleMode.Foot);
        Assert.Null(s.Weapons.Mount);
        Assert.Same(s.Soldier, s.Weapons.Player);
        for (var t = 0; t < 60; t++)
        {
            s.Tick(default, keys, new WeaponInput(Fire: false, Throw: false, Run: false), false);
        }

        s.Tick(default, keys, new WeaponInput(Fire: true, Throw: true, Run: false), false);
        Assert.Equal([15], thrown);
        Assert.Equal(19, s.Weapons.Inventory.AmmoFor(15));
        Assert.Contains(s.Weapons.Projectiles, p => p.Weapon.Index == 15);
    }
}
