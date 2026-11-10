using System.Numerics;
using RetroEng.Physics;
using SkyNet.Game.Weapons;

namespace SkyNet.Game.Tests.Weapons;

/// <summary>Issue #44: DOS's launch of the lobbed shots and the rocket launcher's lock
/// (SKYNET.EXE v1.01 0x1263cc, 0x1261b1, 0x12311f, 0x123fdc).</summary>
public class DosLaunchTests
{
    private const float Dt = 1f / 60f;
    // No key: default(WeaponInput) would select record 0.
    private static readonly WeaponInput Idle = new(Fire: false, Throw: false, Run: false);

    // A target that can be moved after the shot leaves.
    private sealed class Mover(Vector3 centre) : IDamageable
    {
        public List<Damage> Hits { get; } = [];
        public Vector3 Centre { get; set; } = centre;
        public float Radius => 30f;
        public bool Alive => true;
        public void TakeDamage(in Damage damage) => Hits.Add(damage);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(0.5f)]
    public void Throw_leaves_half_up_the_view_and_falls_at_the_records_392(float pitch)
    {
        // 0x1263cc: the way is the view's forward minus half its down (0x38d88), normalised, times
        // +0x18 (500); +0x1c (392) is the fall's acceleration, not a speed upward.
        var weapons = new WeaponSystem(WeaponTable.Shipped, new CollisionWorld());
        var shooter = new Shooter(Vector3.Zero, 0f, pitch);
        weapons.Tick(new WeaponInput(Fire: false, Throw: true, Run: false, SelectThrown: 14), shooter, Dt);

        var p = Assert.Single(weapons.Projectiles);
        var (f, _, u) = WeaponSystem.Basis(0f, pitch);
        var launch = Vector3.Normalize(f + u * 0.5f) * 500f;
        var expected = launch - Vector3.UnitY * 392f * Dt;
        Assert.Equal(expected.X, p.Velocity.X, 2f);
        Assert.Equal(expected.Y, p.Velocity.Y, 2f);
        Assert.Equal(expected.Z, p.Velocity.Z, 2f);
    }

    [Fact]
    public void Launcher_shell_flies_along_the_view_and_falls_at_392()
    {
        // 0x1261b1: the same acceleration from +0x1c, the speed (2200) along the shot only.
        var weapons = new WeaponSystem(WeaponTable.Shipped, new CollisionWorld());
        weapons.Inventory.Grant(5);
        weapons.Inventory.AddAmmo(WeaponTable.Shipped.Weapons[5].Pool, 5);
        Assert.True(weapons.Inventory.Select(5));
        weapons.Inventory.Tick(1f);
        weapons.Tick(new WeaponInput(Fire: true, Throw: false, Run: false), new Shooter(Vector3.Zero, 0f, 0f), Dt);

        var p = Assert.Single(weapons.Projectiles);
        Assert.Equal(0f, p.Velocity.X, 2f);
        Assert.Equal(-392f * Dt, p.Velocity.Y, 2f);
        Assert.Equal(-2200f, p.Velocity.Z, 2f);
    }

    private static WeaponSystem RocketInHand()
    {
        var weapons = new WeaponSystem(WeaponTable.Shipped, new CollisionWorld()) { DamageToEnemies = 1f };
        weapons.Inventory.Grant(6);
        weapons.Inventory.AddAmmo(WeaponTable.Shipped.Weapons[6].Pool, 5);
        Assert.True(weapons.Inventory.Select(6));
        weapons.Inventory.Tick(1f);
        return weapons;
    }

    [Fact]
    public void Rocket_locked_at_launch_follows_its_target()
    {
        // 0x125bef picks what is under the crosshair; 0x126656 hands it to a rocket of ammo flag 4,
        // which 0x123fdc turns toward it each tick.
        var weapons = RocketInHand();
        var target = new Mover(new Vector3(0f, 0f, -1500f));
        weapons.Targets.Add(target);
        var shooter = new Shooter(Vector3.Zero, 0f, 0f);
        weapons.Tick(Idle, shooter, Dt);
        Assert.Same(target, weapons.Lock);

        weapons.Tick(new WeaponInput(Fire: true, Throw: false, Run: false), shooter, Dt);
        Assert.Single(weapons.Projectiles);
        target.Centre = new Vector3(250f, 0f, -1500f);
        for (var i = 0; i < 240 && weapons.Projectiles.Count > 0; i++)
        {
            weapons.Tick(Idle, shooter, Dt);
        }

        Assert.NotEmpty(target.Hits);
    }

    [Fact]
    public void Rocket_with_nothing_under_the_crosshair_flies_straight()
    {
        var weapons = RocketInHand();
        var target = new Mover(new Vector3(250f, 0f, -1500f));
        weapons.Targets.Add(target);
        var shooter = new Shooter(Vector3.Zero, 0f, 0f);
        weapons.Tick(new WeaponInput(Fire: true, Throw: false, Run: false), shooter, Dt);
        Assert.Null(weapons.Lock);
        for (var i = 0; i < 240 && weapons.Projectiles.Count > 0; i++)
        {
            weapons.Tick(Idle, shooter, Dt);
        }

        Assert.Empty(target.Hits);
    }

    [Fact]
    public void Only_a_steered_rocket_in_hand_locks()
    {
        var weapons = new WeaponSystem(WeaponTable.Shipped, new CollisionWorld());
        weapons.Targets.Add(new Mover(new Vector3(0f, 0f, -800f)));
        weapons.Tick(Idle, new Shooter(Vector3.Zero, 0f, 0f), Dt);
        Assert.Null(weapons.Lock);   // the UZI
    }
}
