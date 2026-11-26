using System.Numerics;
using RetroEng.Loop;
using RetroEng.Physics;
using SkyNet.Formats;
using SkyNet.Game.Collision;
using SkyNet.Game.Campaign;
using SkyNet.Game.Player;
using SkyNet.Game.Tests.Behaviour;
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

    // DOS v1.01 0x135744: FORWARD adds 1148 ÷ gear whatever the sign of the speed (the gear is 1
    // when backing, 0x135c67), so a car rolling back down a slope can still drive up it (#46).
    [Fact]
    public void JeepForwardThrottlesWhileRollingBack()
    {
        var (body, car) = Ride(VehicleMode.Jeep);
        car.Speed = -100f;
        Run(body, 1, Hold(Keys.Forward));
        Assert.Equal(-100f + VehicleController.JeepAccel * Dt, car.Speed, 2);
    }

    // DOS v1.01 0x135fb4 adds 0x188 = 392 u/s² to every wheel's fall each tick, the soldier's
    // gravity: a jeep leaving a ramp at speed carries across the MAP.020 moat (#46).
    [Fact]
    public void JeepFallsAtDosGravity()
    {
        var (body, _) = Ride(VehicleMode.Jeep, Floor(-100000f), y: 1000f);
        var before = body.Velocity.Y;
        Run(body, 1, Hold());
        Assert.Equal(-392f * Dt, body.Velocity.Y - before, 2);
    }

    // DOS v1.01 0x1361c2: the car's pitch is the wheels' ground plus the pedal dip; nothing
    // shakes it with speed, so on flat ground the view holds still (#46: "the view wobbles").
    [Fact]
    public void JeepViewHoldsStillOnFlatGround()
    {
        var (body, car) = Ride(VehicleMode.Jeep);
        Run(body, 600, Hold(Keys.Forward));
        for (var i = 0; i < 30; i++)
        {
            Run(body, 1, Hold(Keys.Forward));
            Assert.Equal(VehicleController.JeepEye, body.Eye, 3);
            Assert.Equal(0f, body.Pitch, 4);
            Assert.Equal(0f, car.Roll, 4);
        }
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

    /// <summary>A plane through the origin, y = sx·x + sz·z (car at yaw 0 looks along −Z, right is +X).</summary>
    private static IEnumerable<Triangle> Slope(float sx, float sz, float half = 20000f)
    {
        Vector3 P(float x, float z) => new(x, sx * x + sz * z, z);
        yield return new Triangle(P(-half, -half), P(half, -half), P(half, half));
        yield return new Triangle(P(-half, -half), P(half, half), P(-half, half));
    }

    // Future Shock MAP.020 west of the moat: the ground falls 600 → 360 eastward over 1500 (9°).
    // DOS pitches the car by its wheels' heights (0x136045: asin of the front-to-rear vector)
    // whether or not they touch, so going down at speed (wheels hopping) the nose follows the
    // slope; the port levelled the cab whenever the hull was off the ground (#46 playtest).
    [Fact]
    public void JeepNoseFollowsADownhill()
    {
        if (BehaviourData.Dir is not { } dir || GameDataLocator.OtherGameDir(dir) is not { } fs
            || GameData.Open(fs, dir) is not { } data || LevelCollision.Load(data, 20) is not { } level)
        {
            return;
        }

        var body = new PlayerController(level.World);
        body.Spawn(new Vector3(31100f, 610f, -44950f), -MathF.PI / 2f);          // facing east (+X)
        var car = new VehicleController(level.World, body);
        car.Enter(VehicleMode.Jeep);
        Run(body, 30, Hold());
        car.Speed = VehicleController.JeepMaxSpeed;
        while (body.Position.X < 32200f)
        {
            Run(body, 1, Hold(Keys.Forward));
        }

        var slope = -MathF.Atan(240f / 1500f);
        Assert.InRange(car.TiltPitch, slope - 0.05f, slope + 0.05f);
    }

    // DOS v1.01 0x135f71: a wheel on the ground has no vertical speed (landing zeroes it), so a
    // car climbing a ramp or leaving its top never moves up on its own; the port's slide threw it up.
    [Fact]
    public void JeepGetsNoLaunchFromARamp()
    {
        Vector3 P(float x, float z) => new(x, -0.3f * z, z);                     // rises ahead (−Z)
        var ramp = Floor(-2000f).Concat([new Triangle(P(-500f, 100f), P(500f, 100f), P(500f, -400f)),
            new Triangle(P(-500f, 100f), P(500f, -400f), P(-500f, -400f))]);
        var (body, car) = Ride(VehicleMode.Jeep, ramp, y: 5f);
        car.Speed = 600f;
        var up = 0f;
        for (var i = 0; i < 300 && body.Position.Z > -700f; i++)
        {
            Run(body, 1, Hold(Keys.Forward));
            up = MathF.Max(up, body.Velocity.Y);
        }

        Assert.True(body.Position.Z < -700f, $"stopped at {body.Position} v {car.Speed} floor {car.OnFloor} wall {car.OnWall} up {up}");
        Assert.Equal(0f, up);
    }

    // Future Shock mission 2, MAP.020 (#46, the reporters' saves 8/9): westward the ground climbs
    // 640 → 719 to the moat's edge at x 33800, whose east wall falls 70° to the bottom (0); the
    // far shelf is at 360 from x 33000. DOS wheels never snap down (0x135f71: a wheel lands only
    // when it falls to the ground), so the car leaves the edge and flies onto the shelf; the port
    // glued it down the wall into the moat.
    [Fact]
    public void JeepJumpsTheMission2Moat()
    {
        if (BehaviourData.Dir is not { } dir || GameDataLocator.OtherGameDir(dir) is not { } fs
            || GameData.Open(fs, dir) is not { } data || LevelCollision.Load(data, 20) is not { } level)
        {
            return;
        }

        var body = new PlayerController(level.World);
        body.Spawn(new Vector3(35800f, 650f, -44950f), MathF.PI / 2f);           // facing west (−X)
        var car = new VehicleController(level.World, body);
        car.Enter(VehicleMode.Jeep);
        Run(body, 30, Hold());
        car.Speed = VehicleController.JeepMaxSpeed;
        while (body.Position.X > 33800f)
        {
            Run(body, 1, Hold(Keys.Forward));
        }

        var bottom = float.MaxValue;
        for (var i = 0; i < 180 && !(car.OnFloor && body.Position.X < 33700f); i++)
        {
            Run(body, 1, Hold(Keys.Forward));
            bottom = MathF.Min(bottom, body.Position.Y);
        }

        Assert.True(body.Position.X < 33050f, $"landed at x {body.Position.X:0}, y {body.Position.Y:0}");
        Assert.True(bottom > 300f, $"fell to {bottom:0}");
    }

    // DOS v1.01 0x135dec moves the car by its speed along the heading in x/z alone; the wheels then
    // land on whatever ground is there (0x135f71, up to 60 above). Up a slope the car keeps its
    // ground speed; the port slid the hull along the plane and lost cos² of it.
    [Fact]
    public void JeepKeepsItsGroundSpeedUpASlope()
    {
        var (body, car) = Ride(VehicleMode.Jeep, Slope(0f, -1f));                // 45° rising ahead
        car.Speed = 300f;
        var z = body.Position.Z;
        Run(body, 1, Hold(Keys.Forward));

        Assert.Equal(car.Speed * Dt, z - body.Position.Z, 0);
    }

    // #46 (save 8): flat out from the town, the jeep reaches the moat's edge at ~420 and lands on
    // its far wall (54°); DOS climbs it at full ground speed onto the shelf (x < 33000, y 360).
    // The port crept up it at 40 u/s.
    [Fact]
    public void JeepClimbsOutOfTheMission2Moat()
    {
        if (BehaviourData.Dir is not { } dir || GameDataLocator.OtherGameDir(dir) is not { } fs
            || GameData.Open(fs, dir) is not { } data || LevelCollision.Load(data, 20) is not { } level)
        {
            return;
        }

        var body = new PlayerController(level.World);
        body.Spawn(new Vector3(37100f, 520f, -44790f), MathF.PI / 2f);           // facing west (−X)
        var car = new VehicleController(level.World, body);
        car.Enter(VehicleMode.Jeep);
        Run(body, 30, Hold());
        car.Speed = VehicleController.JeepMaxSpeed;
        for (var i = 0; i < 600 && body.Position.X > 32900f; i++)
        {
            Run(body, 1, Hold(Keys.Forward));
        }

        Assert.True(body.Position.X <= 32900f, $"stuck at x {body.Position.X:0}, y {body.Position.Y:0}, v {car.Speed:0}");
    }

    // DOS v1.01 0x136193: the slope's pull is clamped to the same −140..683.6 as the pedals. Rolling
    // back down this 35° bank the wheels touch only now and then (they never snap down, 0x135f71).
    [Fact]
    public void JeepRollsBackNoFasterThanItReverses()
    {
        var (body, car) = Ride(VehicleMode.Jeep, Slope(0f, -0.7f));               // ground rises ahead
        var fastest = 0f;
        for (var i = 0; i < 600; i++)
        {
            Run(body, 1, Hold());
            fastest = MathF.Min(fastest, car.Speed);
        }

        Assert.Equal(-VehicleController.JeepReverseSpeed, fastest, 2);
    }

    [Fact]
    public void JeepLeansTowardTheLowerSide()
    {
        var (_, car) = Ride(VehicleMode.Jeep, Slope(-0.3f, 0f));                 // right side lower
        var (_, _, up, _) = car.View(1f);
        Assert.True(car.OnFloor);
        Assert.True(up.X > 0.1f, $"up {up}");
        Assert.True(Vector3.Transform(Vector3.UnitY, car.CockpitWorld(1f) with { Translation = Vector3.Zero }).X > 0.1f);
    }

    [Fact]
    public void JeepNoseRisesUphill()
    {
        var (_, car) = Ride(VehicleMode.Jeep, Slope(0f, -0.3f));                 // ground rises ahead (−Z)
        var (_, forward, _, _) = car.View(1f);
        Assert.True(car.OnFloor);
        Assert.True(forward.Y > 0.1f, $"forward {forward}");
    }

    // DOS v1.01 0x135744 / 0x135866 / 0x135969: the throttle lifts the nose by the speed it adds
    // (less 297.5 a second), the brake dips it by half the speed it takes, off the pedals it
    // settles by 8 a DOS frame; at most 32/2048 of a turn either way (#46: no pitch on the pedals).
    [Fact]
    public void JeepPitchesWithThePedals()
    {
        var most = VehicleController.JeepDipMax * MathF.PI / 1024f;
        var (body, _) = Ride(VehicleMode.Jeep);
        Run(body, 5, Hold(Keys.Forward));
        Assert.Equal(most, body.Pitch, 4);                                       // low gears: nose up
        Run(body, 600, Hold(Keys.Forward));
        Assert.Equal(0f, body.Pitch, 4);                                         // top gears: settled
        Run(body, 20, Hold(Keys.Back));
        Assert.Equal(-most, body.Pitch, 4);                                      // braking: nose down
        Run(body, 20, Hold());
        Assert.Equal(0f, body.Pitch, 4);
    }

    // DOS v1.01 0x126abb, fed from 0x135d94: the engine loop plays at (70 + |v·256|·100 >> 16) of
    // 128 of its rate, the speed × 5/4 with no wheel down; idle at a stop (#46: static engine).
    [Fact]
    public void JeepEngineRevsWithSpeed()
    {
        var (body, car) = Ride(VehicleMode.Jeep);
        Assert.Equal(70f / 128f, car.EnginePitch, 4);
        Run(body, 600, Hold(Keys.Forward));
        Assert.Equal((70f + VehicleController.JeepMaxSpeed * 100f / 256f) / 128f, car.EnginePitch, 4);

        var (high, flying) = Ride(VehicleMode.Jeep, y: 5000f);
        flying.Speed = 400f;
        Run(high, 1, Hold());
        Assert.False(flying.OnFloor);
        Assert.Equal((70f + 500f * 100f / 256f) / 128f, flying.EnginePitch, 4);
    }

    // DOS v1.01 0x135a7e: the wheel leans the view by −(wheel·0x4600 × speed·256) >> 26 of 2048,
    // on top of the ground's roll (#46: no roll when turning).
    [Fact]
    public void JeepLeansIntoATurn()
    {
        var (body, car) = Ride(VehicleMode.Jeep);
        car.Speed = 400f;
        Run(body, 30, Hold(Keys.Right));
        var want = -car.Wheel * car.Speed * 0x4600 * 256f / (1 << 26) * MathF.PI / 1024f;
        Assert.Equal(1f, car.Wheel);
        Assert.True(want < -0.05f, $"lean {want}");
        Assert.Equal(want, car.Roll, 4);
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

    // DOS v1.01 0x1257a4: FIRE = dword [0x44476 + mode] {0, 24, 21}, THROW = [0x4449a + mode]
    // {15, 25, 22} (mode 4 jeep, 8 HK): the jeep fires the laser, the HK the plasma (issue #46).
    [Theory]
    [InlineData(VehicleMode.Jeep, 24, 25, 22)]
    [InlineData(VehicleMode.Hk, 21, 22, 18)]
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
    public void EnergyRechargesWhileTheGunFires()
    {
        // DOS 0x13202b adds 300 a second every frame, firing or not.
        var (weapons, guns, _) = Armed(VehicleMode.Jeep);
        var start = weapons.Inventory.Pool(VehicleGuns.EnergyPool);
        for (var i = 0; i < 60; i++)
        {
            guns.Fired(false, FireResult.Fired);
            guns.Tick(weapons.Inventory, Dt);
        }

        Assert.InRange(weapons.Inventory.Pool(VehicleGuns.EnergyPool) - start, 295, 300);
    }

    [Fact]
    public void EmptiedGunFiresAgainAtOneBolt()
    {
        // DOS has no overheat: the next bolt leaves once the pool holds its 100 (a third of a second).
        var (weapons, _, shooter) = Armed(VehicleMode.Jeep);
        var fire = new WeaponInput(true, false, false);
        for (var i = 0; i < 600 && weapons.Inventory.Pool(VehicleGuns.EnergyPool) >= 100; i++)
        {
            weapons.Tick(fire, shooter, Dt);
        }

        Assert.True(weapons.Inventory.Pool(VehicleGuns.EnergyPool) < 100);
        var ticks = 0;
        var before = weapons.Inventory.Pool(VehicleGuns.EnergyPool);
        while (ticks < 120 && weapons.Inventory.Pool(VehicleGuns.EnergyPool) >= before)
        {
            before = weapons.Inventory.Pool(VehicleGuns.EnergyPool);
            weapons.Tick(fire, shooter, Dt);
            ticks++;
        }

        Assert.InRange(ticks / 60f, 0f, 0.45f);
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

    /// <summary>A robot's shot along <paramref name="dir"/> through <paramref name="through"/> (from 600 back):
    /// does it strike the hull?</summary>
    private static bool Struck(Hull hull, Vector3 through, Vector3 dir)
    {
        var weapons = new WeaponSystem(WeaponTable.Shipped, new CollisionWorld()) { Player = hull };
        var ammo = WeaponTable.Shipped.Ammo.First(a => a.Family == AmmoFamily.Hitscan && !a.Blasts && a.Damage > 0);
        var before = hull.Points;
        weapons.EnemyFire(new SkyNet.Game.Tests.Enemies.Soldier(), ammo, through - dir * 600f, dir, 0f);
        return hull.Points < before;
    }

    // DOS strikes the jeep through four spheres of 50 (table 0x38e2d, tested by 0x145232 in the
    // car's frame): 55 up, 20 right of the driver, along the car; the HK through one of 80 at the eye.
    [Fact]
    public void RobotShotsStrikeTheDosJeepSpheres()
    {
        var (body, car) = Ride(VehicleMode.Jeep);
        var hull = new Hull(car, new Soldier(body));
        var at = body.Position;

        Assert.True(Struck(hull, at + new Vector3(0f, 56f, -120f), Vector3.UnitX));    // the nose
        Assert.False(Struck(hull, at + new Vector3(-40f, 71f, 0f), -Vector3.UnitZ));   // left of the car

        car.CarYaw = MathF.PI / 2f;                                                    // the nose to -X
        Assert.False(Struck(hull, at + new Vector3(0f, 56f, -120f), Vector3.UnitX));
        Assert.True(Struck(hull, at + new Vector3(-120f, 56f, 0f), -Vector3.UnitZ));
    }

    [Fact]
    public void RobotShotsStrikeTheDosHkSphere()
    {
        var (body, car) = Ride(VehicleMode.Hk, y: 300f);
        var hull = new Hull(car, new Soldier(body));

        Assert.True(Struck(hull, body.EyePosition + new Vector3(0f, 0f, 75f), Vector3.UnitX));
        Assert.False(Struck(hull, body.EyePosition + new Vector3(0f, 0f, 95f), Vector3.UnitX));
    }

    // #51: the turret's and the HK's view (and the HK's cockpit) take the mouse before the tick,
    // and the next tick turns them by that motion: no jump back.
    [Theory]
    [InlineData(VehicleMode.Jeep, 1f)]
    [InlineData(VehicleMode.Hk, 300f)]
    public void ViewShowsTheMouseBeforeTheTick(VehicleMode mode, float y)
    {
        var (body, car) = Ride(mode, y: y);
        Run(body, 1, Look(100f, 40f));
        var pending = new Vector2(50f, 20f);
        var shown = car.View(0.25f, pending).Forward;
        var cockpit = Vector3.TransformNormal(-Vector3.UnitZ, car.CockpitWorld(0.25f, pending));
        Assert.True(Vector3.Distance(shown, car.View(1f).Forward) > 0.01f, "the pending motion turns the view");

        Run(body, 1, Look(50f, 20f));
        Assert.True(Vector3.Distance(shown, car.View(0f).Forward) < 1e-4f, $"{shown} vs {car.View(0f).Forward}");
        var after = Vector3.TransformNormal(-Vector3.UnitZ, car.CockpitWorld(0f));
        Assert.True(Vector3.Distance(cockpit, after) < 1e-4f, $"cockpit {cockpit} vs {after}");
    }
}
