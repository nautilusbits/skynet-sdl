using System.Numerics;
using RetroEng.Loop;
using RetroEng.Physics;
using SkyNet.Formats;
using SkyNet.Game.Campaign;
using SkyNet.Game.Player;
using SkyNet.Game.Vehicles;
using SkyNet.Game.Weapons;

namespace SkyNet.Game.Tests.Vehicles;

public class VehicleTests
{
    private const float Dt = 1f / 60f;
    private static readonly PlayerActions Keys = new(Forward: 0, Back: 1, Left: 2, Right: 3, Run: 4, Jump: 5, Crouch: 6,
        TurnLeft: 7, TurnRight: 8, LookUp: 9, LookDown: 10, Slide: 11);

    private static TickInput Hold(params int[] actions) => Look(0f, 0f, actions);

    private static TickInput Look(float x, float y, params int[] actions)
    {
        ulong down = 0;
        foreach (var a in actions) down |= TickInput.Bit(a);
        return new TickInput(down, 0, 0, x, y, 0, 0, 0);
    }

    private static IEnumerable<Triangle> Floor(float y = 0f, float half = 200000f)
    {
        yield return new Triangle(new(-half, y, -half), new(half, y, -half), new(half, y, half));
        yield return new Triangle(new(-half, y, -half), new(half, y, half), new(-half, y, half));
    }

    private static (PlayerController Body, VehicleController Car) Ride(VehicleMode mode, IEnumerable<Triangle>? world = null, float y = 1f)
    {
        var w = new CollisionWorld().Add(new TriangleGrid(world ?? Floor()));
        var body = new PlayerController(w);
        body.Spawn(new Vector3(0, y, 0), 0f);
        var car = new VehicleController(w, body);
        car.Enter(mode);
        Run(body, 30, Hold());
        return (body, car);
    }

    private static void Run(PlayerController p, int ticks, TickInput input)
    {
        for (var i = 0; i < ticks; i++) p.Step(input, Dt, Keys);
    }

    // ---------------------------------------------------------------- jeep

    [Fact]
    public void JeepReachesDosTopSpeedThroughItsGears()
    {
        var (body, car) = Ride(VehicleMode.Jeep);
        Assert.True(car.OnFloor);
        Run(body, 1, Hold(Keys.Forward));
        Assert.Equal(VehicleController.JeepAccel * Dt, car.Speed, 2);          // first gear: the full 1148
        Run(body, 60 * 30, Hold(Keys.Forward));
        Assert.Equal(VehicleController.JeepMaxSpeed, car.Speed, 2);
        Assert.Equal(-VehicleController.JeepMaxSpeed, body.Velocity.Z, 1);
        Assert.Equal(0f, body.Position.Y, 0);
    }

    [Fact]
    public void JeepBrakesIntoReverseAndCoastsToAStop()
    {
        var (body, car) = Ride(VehicleMode.Jeep);
        Run(body, 600, Hold(Keys.Back));
        Assert.Equal(-VehicleController.JeepReverseSpeed, car.Speed, 2);
        Run(body, 60, Hold());
        Assert.Equal(0f, car.Speed, 2);                                          // drag 153 u/s² > 140
    }

    [Fact]
    public void JeepWheelGoesLockToLockInAThirdOfASecondAndTurnsOnlyWhenRolling()
    {
        var (body, car) = Ride(VehicleMode.Jeep);
        Run(body, 19, Hold(Keys.Right));                                         // 0.317 s ≥ 0.305
        Assert.Equal(1f, car.Wheel);
        Assert.Equal(0f, car.CarYaw);                                            // standing: no turn
        Run(body, 600, Hold(Keys.Forward));
        Run(body, 30, Hold(Keys.Forward, Keys.Right));
        var before = car.CarYaw;
        Run(body, 1, Hold(Keys.Forward, Keys.Right));
        var rate = (before - car.CarYaw) / Dt;
        var expected = MathF.Min(VehicleController.JeepTurnK / car.Speed, VehicleController.JeepTurnMax);
        Assert.Equal(expected, rate, 2);
        Assert.True(rate <= VehicleController.JeepTurnMax);
    }

    [Fact]
    public void JeepTurnRateIsClampedAtLowSpeed()
    {
        var (body, car) = Ride(VehicleMode.Jeep);
        Run(body, 12, Hold(Keys.Forward));                                       // ~ 140 u/s: 721.6 / v > 1.074
        Run(body, 19, Hold(Keys.Right));
        var v = car.Speed;
        Assert.True(v > VehicleController.JeepGear && VehicleController.JeepTurnK / v > VehicleController.JeepTurnMax);
        var before = car.CarYaw;
        Run(body, 1, Hold(Keys.Right));
        Assert.Equal(VehicleController.JeepTurnMax, (before - car.CarYaw) / Dt, 3);
    }

    [Fact]
    public void JeepTurretPitchIsClampedAt45DegreesAndTheViewIsTheTurret()
    {
        var (body, car) = Ride(VehicleMode.Jeep);
        Run(body, 1, Look(0f, -100000f));                                        // mouse far up
        Assert.Equal(MathF.PI / 4f, car.AimPitch, 5);
        Run(body, 1, Look(0f, 100000f));
        Assert.Equal(-MathF.PI / 4f, car.AimPitch, 5);
        Run(body, 120, Hold(Keys.LookUp));
        Assert.Equal(MathF.PI / 4f, car.AimPitch, 5);
        Assert.Equal(VehicleController.JeepEye, body.Eye, 1);
    }

    [Fact]
    public void JeepTurretTurnsTheFullCircleAndTheCockpitStaysWithTheCar()
    {
        var (body, car) = Ride(VehicleMode.Jeep);
        var cockpit = car.CockpitWorld(1f);
        var carYaw = car.CarYaw;
        var half = MathF.PI / PlayerController.MouseRadians;                     // mouse counts for 180°
        Run(body, 1, Look(-half / 2f, 0f));
        Run(body, 1, Look(-half / 2f, 0f));
        Assert.Equal(MathF.PI, car.AimYaw, 4);                                   // no clamp (DOS DAT_00069911)
        Assert.Equal(carYaw, car.CarYaw, 5);
        Assert.Equal(car.CarYaw + car.AimYaw, body.Yaw, 5);                      // the view is the turret
        var look = PlayerController.Direction(body.Yaw, body.Pitch);             // the guns' Shooter
        Assert.Equal(1f, look.Z, 3);                                             // firing backwards (+Z)
        Assert.Equal(cockpit, car.CockpitWorld(1f));                             // the cockpit rides the car
        Run(body, 1, Look(-half * 2f, 0f));
        Assert.Equal(3f * MathF.PI, car.AimYaw, 3);                              // and keeps on turning
    }

    [Fact]
    public void JeepBouncesOffAWall()
    {
        var wall = new[]
        {
            new Triangle(new(-5000, -10, -800), new(5000, -10, -800), new(5000, 500, -800)),
            new Triangle(new(-5000, -10, -800), new(5000, 500, -800), new(-5000, 500, -800)),
        };
        var (body, car) = Ride(VehicleMode.Jeep, Floor().Concat(wall));
        var flipped = false;
        for (var i = 0; i < 600 && !flipped; i++)
        {
            Run(body, 1, Hold(Keys.Forward));
            flipped = car.Speed < 0f;
        }

        Assert.True(flipped);
        Assert.True(body.Position.Z > -800f);
    }

    // ---------------------------------------------------------------- HK

    [Fact]
    public void HkFliesAt800AheadAndKeepsItsPitchWithin45Degrees()
    {
        var (body, car) = Ride(VehicleMode.Hk, y: 300f);
        Run(body, 1, Look(0f, -100000f));
        Assert.Equal(VehicleController.HkPitchMax, body.Pitch, 5);
        Run(body, 1, Look(0f, 100000f));
        Assert.Equal(-VehicleController.HkPitchMax, body.Pitch, 5);
        body.Pitch = 0f;
        Run(body, 120, Hold(Keys.Forward));
        Assert.Equal(-VehicleController.HkSpeed, body.Velocity.Z, 1);
        Assert.Equal(VehicleController.HkEye, body.Eye);
        Assert.Equal(VehicleMode.Hk, car.Mode);
    }

    [Fact]
    public void HkEyeNeverRisesAboveTheMarkerCeiling()
    {
        var (body, car) = Ride(VehicleMode.Hk, y: 300f);
        car.Ceiling = 600f;
        for (var i = 0; i < 600; i++)
        {
            Run(body, 1, Hold(Keys.Jump, Keys.Forward));
            Assert.True(body.EyePosition.Y <= 600f + 1e-3f);
        }

        Assert.Equal(600f, body.EyePosition.Y, 1);
    }

    [Fact]
    public void HkCeilingTurnsAHillIntoAWall()
    {
        // A slope rising ahead (−Z) from y 0 to 2000 over 2000 units, under a ceiling at 700.
        var hill = new[]
        {
            new Triangle(new(-5000, 0, -500), new(5000, 0, -500), new(5000, 2000, -2500)),
            new Triangle(new(-5000, 0, -500), new(5000, 2000, -2500), new(-5000, 2000, -2500)),
        };
        var (body, car) = Ride(VehicleMode.Hk, Floor().Concat(hill), 300f);
        car.Ceiling = 700f;
        Run(body, 600, Hold(Keys.Forward));
        Assert.True(body.EyePosition.Y <= 700f + 1e-3f);
        Assert.True(body.Position.Z > -1500f);                                   // held off the hill top
    }

    [Fact]
    public void HkHasNoAltitudeBandOnlyTheGroundAndTheCeiling()
    {
        var (body, _) = Ride(VehicleMode.Hk, y: 300f);
        Run(body, 60 * 20, Hold(Keys.Jump));                                     // no ceiling: 200 u/s up
        Assert.True(body.Position.Y > 3000f);
        Run(body, 60 * 40, Hold(Keys.Crouch));
        Assert.InRange(body.Position.Y, 0f, 5f);                                 // down to the ground itself
    }

    [Fact]
    public void HkEntryRisesTheEye400OverTheMarker()
    {
        var w = new CollisionWorld().Add(new TriangleGrid(Floor()));
        var body = new PlayerController(w);
        body.Spawn(new Vector3(0, 100, 0), 0f);
        var marker = body.EyePosition.Y;
        new VehicleController(w, body).Enter(VehicleMode.Hk, entryRise: true);
        Assert.Equal(marker + VehicleController.HkEntryRise, body.EyePosition.Y, 3);
    }

    // ---------------------------------------------------------------- modes

    [Theory]
    [InlineData(GameKind.SkyNet, 210, VehicleMode.Foot)]
    [InlineData(GameKind.SkyNet, 220, VehicleMode.Jeep)]
    [InlineData(GameKind.SkyNet, 221, VehicleMode.Jeep)]
    [InlineData(GameKind.SkyNet, 260, VehicleMode.Jeep)]
    [InlineData(GameKind.SkyNet, 270, VehicleMode.Hk)]
    [InlineData(GameKind.SkyNet, 271, VehicleMode.Hk)]
    [InlineData(GameKind.SkyNet, 280, VehicleMode.Foot)]
    [InlineData(GameKind.FutureShock, 20, VehicleMode.Jeep)]
    [InlineData(GameKind.FutureShock, 60, VehicleMode.Hk)]
    [InlineData(GameKind.FutureShock, 100, VehicleMode.Jeep)]
    [InlineData(GameKind.FutureShock, 103, VehicleMode.Foot)]
    [InlineData(GameKind.FutureShock, 129, VehicleMode.Foot)]
    [InlineData(GameKind.FutureShock, 170, VehicleMode.Jeep)]
    public void ModeComesFromTheMissionTable(GameKind game, int map, VehicleMode mode) =>
        Assert.Equal(mode, VehicleModes.ForMission(game, null, map));

    // ---------------------------------------------------------------- guns

    private static (WeaponSystem Weapons, VehicleGuns Guns, Shooter Shooter) Armed(VehicleMode mode)
    {
        var weapons = new WeaponSystem(WeaponTable.Shipped, new CollisionWorld());
        var guns = new VehicleGuns(mode);
        foreach (var r in VehicleGuns.Records(mode)) weapons.Inventory.Grant(r);
        weapons.Mount = guns;
        return (weapons, guns, new Shooter(new Vector3(0, 100, 0), 0f, 0f));
    }

    [Theory]
    [InlineData(VehicleMode.Jeep, 20, 22, 18)]
    [InlineData(VehicleMode.Hk, 24, 25, 22)]
    public void EachModeMountsItsRecords(VehicleMode mode, int gun, int pod, int gunAmmo)
    {
        Assert.Equal(gun, VehicleGuns.GunOf(mode));
        Assert.Equal(pod, VehicleGuns.PodOf(mode));
        var (weapons, _, shooter) = Armed(mode);
        weapons.Inventory.AddAmmo(VehicleGuns.EnergyPool, 5000);               // full: no regen this tick
        var energy = weapons.Inventory.Pool(VehicleGuns.EnergyPool);
        weapons.Tick(new WeaponInput(true, false, false, Select: 3, Cycle: 1), shooter, Dt);
        Assert.Single(weapons.Projectiles);
        Assert.Equal(gunAmmo, weapons.Projectiles[0].Ammo.Index);
        Assert.Equal(energy - 100, weapons.Inventory.Pool(VehicleGuns.EnergyPool));
        Assert.Equal(WeaponTable.Shipped.Weapons[Inventory.DefaultPrimary].Index, weapons.Inventory.Primary); // keys ignored

        for (var i = 0; i < 60; i++) weapons.Tick(new WeaponInput(false, false, false), shooter, Dt);
        var rockets = weapons.Inventory.Pool(VehicleGuns.RocketPool);
        weapons.Tick(new WeaponInput(false, true, false), shooter, Dt);
        Assert.Equal(rockets - 1, weapons.Inventory.Pool(VehicleGuns.RocketPool));
        Assert.Contains(weapons.Projectiles, p => p.Weapon.Index == pod && p.Ammo.Model == "ROCKET.3D");
    }

    [Fact]
    public void JeepBarrelsTakeTurns()
    {
        var (weapons, _, shooter) = Armed(VehicleMode.Jeep);
        weapons.Tick(new WeaponInput(true, false, false), shooter, Dt);
        for (var i = 0; i < 10; i++) weapons.Tick(new WeaponInput(true, false, false), shooter, Dt);
        Assert.True(weapons.Projectiles.Count >= 2);
        Assert.True(weapons.Projectiles[0].Position.X * weapons.Projectiles[1].Position.X < 0f);
    }

    [Fact]
    public void EnergyOverheatsThenRegensToTheCoolLevel()
    {
        var (weapons, guns, shooter) = Armed(VehicleMode.Jeep);
        for (var i = 0; i < 600 && !guns.Overheated; i++) weapons.Tick(new WeaponInput(true, false, false), shooter, Dt);
        Assert.True(guns.Overheated);
        Assert.True(weapons.Inventory.Pool(VehicleGuns.EnergyPool) < 100);
        var ticks = 0;
        while (guns.Overheated && ticks < 600)
        {
            weapons.Tick(new WeaponInput(true, false, false), shooter, Dt);
            ticks++;
        }

        Assert.False(guns.Overheated);
        Assert.InRange(weapons.Inventory.Pool(VehicleGuns.EnergyPool), VehicleGuns.CoolLevel - 100, VehicleGuns.CoolLevel + 10);
        Assert.InRange(ticks / 60f, VehicleGuns.RegenDelay + 0.7f, VehicleGuns.RegenDelay + 1.1f); // ~400 at 450/s
    }

    // ---------------------------------------------------------------- hull

    [Fact]
    public void HullTakesTheHitsAndItsLossKillsTheSoldier()
    {
        var (body, car) = Ride(VehicleMode.Jeep);
        var soldier = new Soldier(body);
        var hull = new Hull(car, soldier);
        var weapons = new WeaponSystem(WeaponTable.Shipped, new CollisionWorld()) { Player = hull };
        weapons.BlastAt(hull.Centre + new Vector3(50, 0, 0), 400f);
        Assert.True(hull.Points < Hull.JeepPoints);
        Assert.Equal(Soldier.MaxHealth, soldier.Health);
        Assert.Equal(hull.Points / Hull.JeepPoints, hull.Fraction, 5);
        hull.Hit(Hull.JeepPoints);
        Assert.Equal(0f, hull.Points);
        Assert.False(soldier.Alive);
        Assert.False(hull.Alive);
        Assert.Equal(Hull.HkPoints, Hull.PointsFor(VehicleMode.Hk));
    }

    [Fact]
    public void A_god_mode_soldier_loses_nothing_in_the_jeep()
    {
        // Bug: god mode set only Soldier.God; the jeep's own blast near a wall
        // emptied the hull, which killed the soldier anyway.
        var (body, car) = Ride(VehicleMode.Jeep);
        var soldier = new Soldier(body) { God = true };
        var hull = new Hull(car, soldier);
        var weapons = new WeaponSystem(WeaponTable.Shipped, new CollisionWorld()) { Player = hull };

        weapons.BlastAt(hull.Centre + new Vector3(30, 0, 0), 400f);
        hull.Hit(Hull.JeepPoints);

        Assert.Equal(Hull.JeepPoints, hull.Points);
        Assert.True(soldier.Alive);
    }
}
