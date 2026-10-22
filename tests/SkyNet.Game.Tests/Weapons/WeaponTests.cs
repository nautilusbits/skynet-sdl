using System.Numerics;
using RetroEng.Physics;
using SkyNet.Formats;
using SkyNet.Game.Weapons;

namespace SkyNet.Game.Tests.Weapons;

public class WeaponTests
{
    private const float Dt = 1f / 60f;

    // The Godot port's fly_camera.gd _weapons rows: (record, rate, damage, pool, cost).
    public static readonly (int Record, int Rate, float Damage, int Pool, int Cost)[] GodotWeapons =
    [
        (1, 5, 10, 0, 1), (2, 4, 20, 0, 3), (3, 8, 20, 0, 4), (4, 1, 50, 1, 1), (5, 1, 200, 2, 1),
        (6, 1, 400, 3, 1), (7, 6, 25, 4, 2), (8, 6, 50, 4, 5), (9, 3, 25, 4, 1), (10, 3, 50, 4, 2),
        (11, 3, 100, 4, 10), (12, 20, 50, 12, 1),
    ];

    // THROWABLES: (record, pool, damage, ammo type, sprite bank, sprite record, effect bank, impact sound, bounce).
    public static readonly (int Record, int Pool, float Damage, int Ammo, int Bank, int Sprite, int Fx, int Sound, bool Bounce)[] GodotThrown =
    [
        (14, 5, 200, 12, 217, 4, 366, 34, true), (15, 6, 125, 13, 217, 1, 366, 34, false),
        (16, 2, 200, 3, 217, 2, 356, 33, false), (18, 8, 800, 14, 217, 0, 356, 36, true),
        (19, 9, 15000, 20, 200, 4, 356, 36, false),
    ];

    private static void AssertGodotValues(WeaponTable t)
    {
        foreach (var (record, rate, damage, pool, cost) in GodotWeapons)
        {
            var w = t.Weapons[record];
            Assert.Equal(rate, w.Rate);
            Assert.Equal(damage, t.AmmoOf(w)!.Strength);
            Assert.Equal(pool, w.Pool);
            Assert.Equal(cost, w.Cost);
        }

        foreach (var g in GodotThrown)
        {
            var w = t.Weapons[g.Record];
            var a = t.AmmoOf(w)!;
            Assert.Equal((g.Pool, g.Ammo), (w.Pool, w.AmmoType));
            Assert.Equal(g.Damage, a.Strength);
            Assert.Equal(AmmoFamily.Gravity, a.Family);
            Assert.Equal((g.Bank << 7) | g.Sprite, a.Sprite);
            Assert.Equal((g.Fx, g.Sound, g.Bounce), (a.ImpactBank, a.ImpactSound, a.Bounces));
            Assert.Equal((500, 392), (g.Record == 19 ? 500 : w.Speed, w.Lift));
        }

        // The pipe: ammo type 17, 50 points, reach 120, PIPEHIT1 (id 0), swung at 2/s.
        var pipe = t.AmmoOf(t.Weapons[0])!;
        Assert.Equal((17, 50f, 120, 0), (pipe.Index, pipe.Strength, pipe.Reach, pipe.ImpactSound));
        Assert.Equal(2, t.Weapons[0].Rate);
        Assert.Equal([500, 50, 0, 0, 500, 25, 20, 0, 2, 1, 1000, 40, 9999], t.Pools.Select(p => p.Initial));
        Assert.Equal([750, 200, 99, 99, 800, 99, 99, 99, 99, 99, 2000], t.Pools.Take(11).Select(p => p.Max));
        Assert.Equal([0, 1, 2, 4, 7, 14, 15, 16, 18, 19], t.StartList.Take(10));
    }

    [Fact]
    public void FutureShockStartsWithThePipeAlone()
    {
        // SKYNET.EXE's Future Shock list (13 entries before SkyNET's): the pipe on foot, then the
        // thrown items and vehicle guns (the Godot port's START_WEAPONS_SHOCK).
        var shock = WeaponTable.For(GameKind.FutureShock);
        Assert.Equal([0, 14, 15, 16, 18, 19, 20, 21, 22, 23, 24, 25], shock.StartList.ToArray());
        var inventory = new Inventory(shock);
        Assert.Equal(WeaponTable.Pipe, inventory.Primary);
        Assert.False(inventory.Owns(1));
    }

    [Fact]
    public void ShippedTableMatchesTheGodotPort()
    {
        var t = WeaponTable.Shipped;
        Assert.Equal("embedded", t.Source);
        AssertGodotValues(t);
        // The rocket: bolt family, ROCKET.3D, effect bank 363, the follow-camera flag.
        var rocket = t.AmmoOf(t.Weapons[6])!;
        Assert.Equal((AmmoFamily.Bolt, "ROCKET.3D", 363, true), (rocket.Family, rocket.Model, rocket.ImpactBank, rocket.FollowCamera));
        Assert.Equal(AmmoFamily.Hitscan, t.AmmoOf(t.Weapons[1])!.Family);
        Assert.Equal(new[] { 4500, 1200, 1800 }, new[] { t.Weapons[1].Speed, t.Weapons[6].Speed, t.Weapons[7].Speed });
    }

    [Fact]
    public void UziFiresFiveTimesASecond()
    {
        var inv = new Inventory(WeaponTable.Shipped);
        Assert.Equal(1, inv.Primary);
        var shots = new List<int>();
        for (var tick = 0; tick < 60; tick++)
        {
            inv.Tick(Dt);
            if (inv.TryFire(inv.Primary) == FireResult.Fired)
            {
                shots.Add(tick);
            }
        }

        Assert.Equal([0, 12, 24, 36, 48], shots);
        Assert.Equal(495, inv.Pool(0));
    }

    [Fact]
    public void EmptyPoolClicksDryAndThrowHasItsOwnCooldown()
    {
        var inv = new Inventory(WeaponTable.Shipped);
        Assert.True(inv.Select(4));                       // shotgun: 50 shells, 1/s
        Assert.Equal(FireResult.Cooling, inv.TryFire(4)); // the select pause
        inv.Tick(Inventory.SelectDelay);
        inv.AddAmmo(1, -50);
        Assert.Equal(FireResult.Dry, inv.TryFire(4));
        Assert.Equal(Inventory.DryDelay, inv.FireCooldown, 4);

        // The gun's cooldown does not hold the throw, and an empty throw still costs its second.
        Assert.Equal(FireResult.Fired, inv.TryThrow());   // a molotov (20)
        Assert.Equal(19, inv.Pool(6));
        Assert.Equal(FireResult.Cooling, inv.TryThrow());
        Assert.True(inv.SelectThrown(16));                // grenade: pool 2 starts empty
        inv.Tick(1f);
        Assert.Equal(FireResult.Empty, inv.TryThrow());
        Assert.Equal(1f, inv.ThrowCooldown, 4);
        Assert.False(inv.Select(3));                      // the machine gun is not in the start kit
        Assert.True(inv.Cycle(1));                        // shotgun → laser rifle (5, 6 not owned)
        Assert.Equal(7, inv.Primary);
    }

    [Fact]
    public void BounceKeepsTwoThirdsOrDetonates()
    {
        Assert.Equal(Projectile.BounceOutcome.Bounce, Projectile.Bounce(new Vector3(300, -30, 0), -Vector3.UnitX, out var v));
        Assert.Equal(new Vector3(-200, -20, 0), v);
        Assert.Equal(Projectile.BounceOutcome.Detonate, Projectile.Bounce(new Vector3(60, 0, 0), -Vector3.UnitX, out _));   // 40 < 50
        Assert.Equal(Projectile.BounceOutcome.Detonate, Projectile.Bounce(new Vector3(0, -140, 0), Vector3.UnitY, out _));  // floor, 93 ≤ 100
        Assert.Equal(Projectile.BounceOutcome.Bounce, Projectile.Bounce(new Vector3(140, 0, 0), -Vector3.UnitX, out _));    // wall, 93
    }

    private static IEnumerable<Triangle> Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, int owner = -1)
    {
        yield return new Triangle(a, b, c, owner);
        yield return new Triangle(a, c, d, owner);
    }

    /// <summary>A wall facing −X at x = <paramref name="x"/>.</summary>
    private static IEnumerable<Triangle> Wall(float x, int owner = 7) =>
        Quad(new(x, -1000, -1000), new(x, -1000, 1000), new(x, 1000, 1000), new(x, 1000, -1000), owner);

    [Fact]
    public void PipeBombBouncesOffAWallAtTwoThirdsSpeed()
    {
        var world = new CollisionWorld().Add(new TriangleGrid(Wall(300f)));
        var weapons = new WeaponSystem(WeaponTable.Shipped, world);
        var reports = new List<ShotReport>();
        weapons.Reported += reports.Add;
        // Looking +X (yaw −90°): thrown at 500 ahead and 392 up.
        var shooter = new Shooter(Vector3.Zero, -MathF.PI / 2f, 0f);
        weapons.Tick(new WeaponInput(Fire: false, Throw: true, Run: false, SelectThrown: 14), shooter, Dt);
        var p = Assert.Single(weapons.Projectiles);
        Vector3 before = default;
        for (var i = 0; i < 120 && p.Bounces == 0; i++)
        {
            before = p.Velocity - new Vector3(0, Projectile.Gravity * Dt, 0);
            weapons.Tick(default, shooter, Dt);
        }

        Assert.Equal(1, p.Bounces);
        Assert.Equal(-before.X * 2f / 3f, p.Velocity.X, 2f);
        Assert.Equal(before.Y * 2f / 3f, p.Velocity.Y, 2f);
        Assert.True(p.Position.X < 300f);
        Assert.Contains(reports, r => r.What == "bounce" && r.Owner == 7);
        Assert.Equal(24, weapons.Inventory.Pool(5));
    }

    [Fact]
    public void RadialBlastFallsOff()
    {
        // Rocket, S = 400: R = 400, 440 at the centre, 200 at 240 units, nothing past.
        Assert.Equal(440f, Blast.Damage(400, 0), 3);
        Assert.Equal(300f, Blast.Damage(400, 140), 3);
        Assert.Equal(200f, Blast.Damage(400, 240), 3);
        Assert.Equal(0f, Blast.Damage(400, 241));
        Assert.Equal(240f, Blast.Reach(400));
        // R clamps to 500: a satchel reaches 290; a weak blast to 5 + 40 / 2.
        Assert.Equal(290f, Blast.Reach(15000));
        Assert.Equal(15000f * 0.5f, Blast.Damage(15000, 290), 1);
        Assert.Equal(42.5f, Blast.Reach(5));
    }

    private sealed class Dummy(Vector3 centre, float radius = 30f) : IDamageable
    {
        public List<Damage> Hits { get; } = [];
        public Vector3 Centre => centre;
        public float Radius => radius;
        public bool Alive => true;
        public void TakeDamage(in Damage damage) => Hits.Add(damage);
    }

    [Fact]
    public void BlastNeedsALineOfSight()
    {
        var world = new CollisionWorld().Add(new TriangleGrid(Wall(100f, owner: 3)));
        var weapons = new WeaponSystem(WeaponTable.Shipped, world);
        var open = new Dummy(new Vector3(0, 0, 140));
        var hidden = new Dummy(new Vector3(140, 0, 0));
        weapons.Targets.Add(open);
        weapons.Targets.Add(hidden);
        weapons.BlastAt(Vector3.Zero, 400);
        Assert.Equal(300f, Assert.Single(open.Hits).Points, 3);
        Assert.Empty(hidden.Hits);
    }

    /// <summary>A target that leaves the target list when it is hit, as a breaking crate does.</summary>
    private sealed class Breaker(Vector3 centre, DamageTargets list) : IDamageable
    {
        public int Hits { get; private set; }
        public Vector3 Centre => centre;
        public float Radius => 30f;
        public bool Alive => Hits == 0;
        public void TakeDamage(in Damage damage)
        {
            Hits++;
            list.Remove(this);
        }
    }

    [Fact]
    public void ABlastSurvivesTargetsLeavingTheListAsTheyBreak()
    {
        var weapons = new WeaponSystem(WeaponTable.Shipped, new CollisionWorld());
        var a = new Breaker(new Vector3(0, 0, 100), weapons.Targets);
        var b = new Breaker(new Vector3(100, 0, 0), weapons.Targets);
        weapons.Targets.Add(a);
        weapons.Targets.Add(b);
        weapons.BlastAt(Vector3.Zero, 400);
        Assert.Equal((1, 1, 0), (a.Hits, b.Hits, weapons.Targets.Count));
    }

    /// <summary>A target that answers its first hit at once: fires a shot of its own, or (as a level
    /// ending would) drops every shot in flight.</summary>
    private sealed class Retaliator(Vector3 centre, WeaponSystem weapons, AmmoRecord ammo, bool clears) : IDamageable
    {
        public int Hits { get; private set; }
        public Vector3 Centre => centre;
        public float Radius => 30f;
        public bool Alive => true;
        public void TakeDamage(in Damage damage)
        {
            if (Hits++ > 0)
            {
                return;
            }

            if (clears)
            {
                weapons.Clear();
            }
            else
            {
                weapons.EnemyFire(this, ammo, centre, Vector3.UnitX, 400f);
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AShotsHitMayAddOrDropShotsWhileTheyFly(bool clears)
    {
        // The shots in flight are stepped while a hit's consequences run: a target that fires back
        // or a level that clears must not break the walk ("Collection was modified").
        var weapons = new WeaponSystem(WeaponTable.Shipped, new CollisionWorld());
        var bolt = WeaponTable.Shipped.Ammo.First(a => a.Family == AmmoFamily.Bolt && a.Life > 1f);
        var shooter = new Dummy(new Vector3(0, 0, 100));
        var target = new Retaliator(new Vector3(0, 0, -200), weapons, bolt, clears);
        weapons.Targets.Add(target);
        weapons.EnemyFire(shooter, bolt, Vector3.Zero, -Vector3.UnitZ, 400f);
        for (var t = 0; t < 60 && target.Hits == 0; t++)
        {
            weapons.Tick(default, new Shooter(new Vector3(0, 0, 500), 0f, 0f), 1f / 60f);
        }

        Assert.Equal(1, target.Hits);
        if (clears)
        {
            Assert.Empty(weapons.Projectiles);
        }
        else
        {
            Assert.Same(target, Assert.Single(weapons.Projectiles).Source);
        }
    }

    [Fact]
    public void UziHitsATargetAndThePipeReachesOnly120()
    {
        var weapons = new WeaponSystem(WeaponTable.Shipped, new CollisionWorld()) { DamageToEnemies = 1f };
        var target = new Dummy(new Vector3(0, 0, -400));
        weapons.Targets.Add(target);
        var shooter = new Shooter(Vector3.Zero, 0f, 0f);  // looking −Z
        for (var i = 0; i < 60; i++)
        {
            weapons.Tick(new WeaponInput(Fire: true, Throw: false, Run: false), shooter, Dt);
        }

        Assert.Equal(5, target.Hits.Count);
        Assert.All(target.Hits, h => Assert.Equal((10f, DamageKind.Direct), (h.Points, h.Kind)));

        weapons.Tick(new WeaponInput(false, false, false, Select: 0), shooter, Dt);
        for (var i = 0; i < 30; i++)
        {
            weapons.Tick(new WeaponInput(Fire: true, Throw: false, Run: false), shooter, Dt);
        }

        Assert.Equal(5, target.Hits.Count);  // 400 away: out of reach
        var near = new Dummy(new Vector3(0, 0, -170));
        weapons.Targets.Add(near);
        for (var i = 0; i < 30; i++)
        {
            weapons.Tick(new WeaponInput(Fire: true, Throw: false, Run: false), shooter, Dt);
        }

        Assert.Equal((50f, DamageKind.Melee), (near.Hits[0].Points, near.Hits[0].Kind));
    }
    [Fact]
    public void The_own_shots_flash_is_told_from_a_robots()
    {
        // A 3D gun in hand draws its own flash (flashSize), so the renderer hides the DOS one of the
        // player's own shot only; a robot's stays.
        var table = WeaponTable.Shipped;
        var weapons = new WeaponSystem(table, new CollisionWorld());
        weapons.Inventory.Grant(8);
        weapons.Inventory.AddAmmo(table.Weapons[8].Pool, 50);
        Assert.True(weapons.Inventory.Select(8));
        weapons.Inventory.Tick(1f);
        Assert.Equal(FireResult.Fired, weapons.Fire(new Shooter(Vector3.Zero, 0f, 0f)));
        Assert.Equal(EffectKind.OwnFlash, Assert.Single(weapons.Effects).Kind);

        var robots = new WeaponSystem(table, new CollisionWorld());
        robots.EnemyFire(new Dummy(Vector3.Zero), table.AmmoOf(table.Weapons[8])!, new Vector3(0, 0, -100), -Vector3.UnitZ, 1000f);
        Assert.Equal(EffectKind.Flash, Assert.Single(robots.Effects).Kind);
    }
}
