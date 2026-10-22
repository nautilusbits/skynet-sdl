using System.Numerics;
using RetroEng.Diagnostics;
using RetroEng.Loop;
using RetroEng.Physics;
using SkyNet.Formats;
using SkyNet.Game.DevTools;
using SkyNet.Game.Player;

namespace SkyNet.Game.Vehicles;

/// <summary>
/// The jeep and the HK as the player's body (docs/game-logic/vehicles.md; the Godot port's
/// fly_camera.gd set_vehicle, _drive, _hover, _hk_ceiling_clamp), one fixed tick at a time through
/// <see cref="PlayerController.Mover"/>. The <see cref="PlayerController"/> keeps the position (the
/// bottom of the hull), the eye and the VIEW: <see cref="PlayerController.Yaw"/> /
/// <see cref="PlayerController.Pitch"/> are where the camera, and so the guns, the use ray and the
/// compass, point.
/// <para><b>Jeep</b> (DOS handler 0x134eec, data ds:0x23700): the keys drive, the mouse turns the
/// TURRET, which is the camera. Throttle 1148 ÷ max(1, v / 21.45) up to 683.6, brake 459 to −140,
/// coasting drag 153, the slope adds 1148·sin; the heading turns only on the ground above 21.45 u/s
/// at wheel·721.6 / v rad/s, at most 1.074, reversed when backing; the wheel goes lock to lock in
/// 0.305 s. The turret's pitch is DOS's ±0x100 of 2048 = ±45° (0x135b04); its yaw is relative to
/// the car (DAT_00069911) and DOS clamps it nowhere, so the turret turns the full circle while the
/// cockpit stays with the car; the guns fire along the view. Port tuning kept: gravity 4500, TURN keys swing
/// the car at 2.4 rad/s, the cab tilts with the ground and shakes with speed, a wall throws the car
/// back at its own speed.</para>
/// <para><b>HK</b> (mover 0x131100): thrust along the view, no gravity; 800 ahead, 400 back at
/// 900 u/s², sidestep 400 (550 with RUN) and climb 200 at 700 u/s²; keyboard yaw a flat 90 °/s,
/// pitch ±45°. The ceiling is the first marker of type 3's Y ([0x390df]): the EYE never ends above
/// it, and a move a hill would carry over it is undone with the speed back at −¼. DOS has no floor
/// band: under the ceiling the HK flies as low as the ground lets it. On a level start the eye
/// rises 400 (entry 0x131fea).</para>
/// </summary>
public sealed class VehicleController : IPlayerMover
{
    // --- seats and bodies (fly_camera.gd VEH_EYE, VEH_CAPSULE) ---
    public const float JeepEye = 92f;
    public const float HkEye = 110f;
    /// <summary>The Godot capsules (55 × 110, 110 × 220) as one sphere each, resting on the position.</summary>
    public const float JeepRadius = 55f;
    public const float HkRadius = 110f;
    /// <summary>The steepest ground the wheels hold (Godot VEH_MAX_SLOPE_DEG; the soldier's is 40°).</summary>
    public const float JeepMaxSlopeDegrees = 75f;
    public static readonly float JeepFloorMinY = MathF.Cos(JeepMaxSlopeDegrees * MathF.PI / 180f);
    public const float JeepFloorSnap = 40f;

    // --- jeep (0x134eec) ---
    public const float JeepMaxSpeed = 683.6f;
    public const float JeepReverseSpeed = 140f;
    public const float JeepAccel = 1148f;
    public const float JeepGear = 21.45f;
    public const float JeepBrake = 459f;
    public const float JeepDrag = 153f;
    public const float JeepTurnK = 721.6f;
    public const float JeepTurnMax = 1.074f;
    public const float JeepWheelRate = 3.28f;
    public const float JeepSkidSpeed = 171f;
    /// <summary>The port's hand tuning (DOS's jeep gravity was never read).</summary>
    public const float JeepGravity = 4500f;
    public const float JeepKeyTurnRate = 2.4f;
    public const float JeepTiltRate = 6f;
    /// <summary>The turret's pitch ±45° (DOS 0x135b04); its yaw about the car is free (no clamp in DOS).</summary>
    public const float JeepAimPitch = MathF.PI / 4f;
    /// <summary>A wall bounce needs this speed (fly_camera.gd _drive).</summary>
    public const float JeepBounceSpeed = 50f;

    // --- HK (0x131100) ---
    public const float HkSpeed = 800f;
    public const float HkBackSpeed = 400f;
    public const float HkAccel = 900f;
    public const float HkStrafe = 400f;
    public const float HkStrafeRun = 550f;
    public const float HkStrafeAccel = 700f;
    public const float HkClimb = 200f;
    public const float HkClimbAccel = 700f;
    public const float HkTurnRate = 1.571f;
    public const float HkPitchMax = 0.785f;
    /// <summary>On a level start the HK's eye rises this much over the marker (0x131fea).</summary>
    public const float HkEntryRise = 400f;

    // --- sounds (DOS sound ids, the Godot port's) ---
    public const int SoundCrash = 30;
    public const int SoundSkid = 29;
    public const int SoundCarStart = 123;
    /// <summary>The engine loops: careng1 (69) for the jeep, hk2 (48) for the HK.</summary>
    public static int EngineSound(VehicleMode mode) => mode switch { VehicleMode.Jeep => 69, VehicleMode.Hk => 48, _ => -1 };

    private const float Skin = 0.25f;
    private const int SlideIterations = 4;

    private readonly CollisionWorld _world;
    private SphereBody _sphere = new(JeepRadius, new Vector3(0f, JeepRadius, 0f));
    private float _bumpT, _skidCooldown;
    private Vector3 _prevPos;
    private float _prevCarYaw, _prevViewYaw, _prevViewPitch, _prevRoll, _prevTiltPitch, _prevTiltRoll, _prevEye;

    public VehicleController(CollisionWorld world, PlayerController body)
    {
        _world = world;
        Body = body;
    }

    public PlayerController Body { get; }
    public VehicleMode Mode { get; private set; }
    /// <summary>The jeep's heading (radians about +Y, 0 looking −Z); the HK's is the view's.</summary>
    public float CarYaw { get; set; }
    /// <summary>The turret relative to the car (jeep).</summary>
    public float AimYaw { get; set; }
    public float AimPitch { get; set; }
    /// <summary>The jeep's signed forward speed (u/s).</summary>
    public float Speed { get; set; }
    /// <summary>The steering wheel, −1..1 (positive: right).</summary>
    public float Wheel { get; private set; }
    public float TiltPitch { get; private set; }
    public float TiltRoll { get; private set; }
    /// <summary>The view's roll (the cab's tilt and shake; 0 in the HK).</summary>
    public float Roll { get; private set; }
    public bool OnFloor { get; private set; }
    public bool OnWall { get; private set; }
    /// <summary>The world Y the HK's eye may not rise above; +∞ none.</summary>
    public float Ceiling { get; set; } = float.PositiveInfinity;
    /// <summary>A sound to play (DOS ids: crash 30, skid 29, car start 123).</summary>
    public Action<int>? Sound { get; set; }
    /// <summary>The jeep's ram test (<see cref="Ramming"/>); null: the car rams nothing. The HK has
    /// none: DOS runs 0x135a7e only from the jeep's mover, and so does the Godot port.</summary>
    public Ramming? Ram { get; set; }

    public SphereBody Sphere => _sphere;

    /// <summary>The marker-3 ceiling of a map (its stored Y, entity Y + 0x10), +∞ when it has none.</summary>
    public static float CeilingOf(MapFile map)
    {
        foreach (var e in map.Entities)
        {
            if (e.Variant == MapEntityVariant.Sprite && e.IsMarker && e.MarkerType == 3)
            {
                return -(e.Y + 0x10);
            }
        }

        return float.PositiveInfinity;
    }

    /// <summary>Climbs into <paramref name="mode"/> (or out, for <see cref="VehicleMode.Foot"/>)
    /// where the body stands, facing its yaw: a fresh seat, stopped, the turret on the bonnet.
    /// <paramref name="entryRise"/>: a level start puts the HK's eye 400 over the marker.</summary>
    public void Enter(VehicleMode mode, bool entryRise = false)
    {
        Mode = mode;
        Speed = 0f;
        Wheel = 0f;
        TiltPitch = TiltRoll = Roll = 0f;
        AimYaw = AimPitch = 0f;
        _bumpT = 0f;
        Body.Velocity = Vector3.Zero;
        CarYaw = Body.Yaw;
        if (mode == VehicleMode.Foot)
        {
            Body.Mover = null;
            Body.Eye = PlayerController.EyeHeight;
            Body.Pitch = 0f;
            return;
        }

        Body.Mover = this;
        if (mode == VehicleMode.Jeep)
        {
            _sphere = new SphereBody(JeepRadius, new Vector3(0f, JeepRadius, 0f));
            Body.Eye = JeepEye;
            Body.Pitch = 0f;
        }
        else
        {
            _sphere = new SphereBody(HkRadius, new Vector3(0f, HkRadius, 0f));
            if (entryRise)
            {
                // The marker's eye + 400; the body hangs HkEye under it.
                Body.Position += new Vector3(0f, Body.Eye + HkEntryRise - HkEye, 0f);
            }

            Body.Eye = HkEye;
            Body.Pitch = Math.Clamp(Body.Pitch, -HkPitchMax, HkPitchMax);
        }

        Snapshot();
    }

    /// <summary>Puts a saved ride's motion back after <see cref="Enter"/> (a load): the heading, the
    /// turret and the speed, with nothing to interpolate from.</summary>
    public void Resume(float carYaw, float aimYaw, float aimPitch, float speed)
    {
        CarYaw = carYaw;
        AimYaw = aimYaw;
        AimPitch = aimPitch;
        Speed = speed;
        Snapshot();
    }

    public void Step(PlayerController body, in TickInput tick, float dt, in PlayerActions a)
    {
        using var zone = Prof.Zone(GameZones.Vehicle);
        Snapshot();
        var fwd = 0f;
        var str = 0f;
        if (Held(tick, a.Forward)) fwd += 1f;
        if (Held(tick, a.Back)) fwd -= 1f;
        if (Held(tick, a.Right)) str += 1f;
        if (Held(tick, a.Left)) str -= 1f;
        var turn = (Held(tick, a.TurnLeft) ? 1f : 0f) - (Held(tick, a.TurnRight) ? 1f : 0f);
        var slide = Held(tick, a.Slide);
        if (turn != 0f && slide)
        {
            str -= turn;
            turn = 0f;
        }

        var tilt = (Held(tick, a.LookUp) ? 1f : 0f) - (Held(tick, a.LookDown) ? 1f : 0f);
        fwd = Math.Clamp(fwd - tick.MoveY, -1f, 1f);
        str = Math.Clamp(str + tick.MoveX, -1f, 1f);
        var mouseYaw = tick.LookX * PlayerController.MouseRadians;
        var mousePitch = tick.LookY * PlayerController.MouseRadians;
        if (Mode == VehicleMode.Jeep)
        {
            AimYaw -= mouseYaw;
            AimPitch = Math.Clamp(AimPitch - mousePitch + tilt * PlayerController.KeyLookRate * dt, -JeepAimPitch, JeepAimPitch);
            CarYaw += turn * JeepKeyTurnRate * dt;
            Drive(dt, fwd, str);
        }
        else
        {
            Body.Yaw += turn * HkTurnRate * dt - mouseYaw;
            Body.Pitch = Math.Clamp(Body.Pitch - mousePitch + tilt * PlayerController.KeyLookRate * dt, -HkPitchMax, HkPitchMax);
            var vert = (Held(tick, a.Jump) ? 1f : 0f) - (Held(tick, a.Crouch) ? 1f : 0f);
            Hover(dt, fwd, str, vert, Held(tick, a.Run));
        }
    }

    // ------------------------------------------------------------------ jeep

    private void Drive(float dt, float fwdIn, float strIn)
    {
        // The state a ram puts back: taken before the wheel turns the heading, as DOS saves it
        // (0x237b4) before 0x135201 steers.
        var prePos = Body.Position;
        var preYaw = CarYaw;
        var grounded = OnFloor;
        Wheel = MathF.Abs(strIn) > 0.1f
            ? Math.Clamp(Wheel + strIn * JeepWheelRate * dt, -1f, 1f)
            : MoveToward(Wheel, 0f, JeepWheelRate * dt);
        var spd = MathF.Abs(Speed);
        if (grounded && spd > JeepGear)
        {
            var rate = Math.Clamp(Wheel * JeepTurnK / spd, -JeepTurnMax, JeepTurnMax);
            CarYaw -= (Speed < 0f ? -rate : rate) * dt;
        }

        var ahead = new Vector3(-MathF.Sin(CarYaw), 0f, -MathF.Cos(CarYaw));
        if (grounded)
        {
            if (fwdIn > 0.1f)
            {
                Speed = Speed < 0f
                    ? MathF.Min(Speed + JeepBrake * dt, 0f)
                    : MathF.Min(Speed + JeepAccel / MathF.Max(1f, MathF.Floor(Speed / JeepGear)) * dt, JeepMaxSpeed);
                _skidCooldown = 0f;
            }
            else if (fwdIn < -0.1f)
            {
                if (Speed > JeepSkidSpeed)
                {
                    _skidCooldown -= dt;
                    if (_skidCooldown <= 0f)
                    {
                        _skidCooldown = 0.9f;
                        Sound?.Invoke(SoundSkid);
                    }
                }

                Speed = MathF.Max(Speed - JeepBrake * dt, -JeepReverseSpeed);
            }
            else
            {
                Speed = MoveToward(Speed, 0f, JeepDrag * dt);
                _skidCooldown = 0f;
            }

            // Downhill pulls, uphill holds back: 1148·sin of the slope along the heading.
            Speed += JeepAccel * Vector3.Dot(Body.FloorNormal, ahead) * dt;
        }

        var vy = grounded ? 0f : Body.Velocity.Y - JeepGravity * dt;
        Body.Velocity = new Vector3(ahead.X * Speed, vy, ahead.Z * Speed);
        Body.Position += _world.Depenetrate(_sphere, Body.Position, Skin);
        var before = Body.Position;
        MoveAndSlide(dt, grounded, JeepFloorMinY);
        var rammed = RamAfterMove(prePos, preYaw, dt);
        if (!rammed && MathF.Abs(Speed) > JeepBounceSpeed && OnWall && Vector3.Distance(Body.Position, before) < MathF.Abs(Speed) * dt * 0.2f)
        {
            // A wall throws the car back at the speed it came in with; the wheel snaps straight.
            Sound?.Invoke(SoundCrash);
            Speed = -Speed;
            Wheel = 0f;
        }

        // The cab tilts with the ground: pitch along the heading, roll across it.
        var wantPitch = 0f;
        var wantRoll = 0f;
        if (OnFloor)
        {
            var n = Body.FloorNormal;
            var right = new Vector3(MathF.Cos(CarYaw), 0f, -MathF.Sin(CarYaw));
            wantPitch = MathF.Asin(Math.Clamp(Vector3.Dot(n, -ahead), -1f, 1f)) * 0.85f;
            wantRoll = MathF.Asin(Math.Clamp(Vector3.Dot(n, right), -1f, 1f)) * 0.7f;
        }

        var k = Math.Clamp(dt * JeepTiltRate, 0f, 1f);
        TiltPitch = float.Lerp(TiltPitch, wantPitch, k);
        TiltRoll = float.Lerp(TiltRoll, wantRoll, k);
        var shake = Math.Clamp(MathF.Abs(Speed) / JeepMaxSpeed, 0f, 1f);
        _bumpT += dt * (6f + 14f * shake);
        // The camera is the turret: the aim turns it, the cab tilts and shakes it.
        Body.Yaw = CarYaw + AimYaw;
        Body.Pitch = AimPitch + TiltPitch + MathF.Sin(_bumpT) * 0.004f * shake;
        Roll = TiltRoll + MathF.Cos(_bumpT * 0.7f) * 0.006f * shake;
        Body.Eye = JeepEye + MathF.Sin(_bumpT * 1.3f) * 3f * shake;
    }

    /// <summary>Runs <see cref="Ram"/> on the move just made and applies its outcome; true when the
    /// move was undone (the wall bounce must not negate the speed a second time).</summary>
    public bool RamAfterMove(Vector3 prePos, float preYaw, float dt)
    {
        if (Ram is not { } ram)
        {
            return false;
        }

        var outcome = ram.Check(prePos, preYaw, Body.Position, CarYaw, Speed, dt);
        switch (outcome)
        {
            case RamOutcome.DroveThrough:
                Sound?.Invoke(SoundCrash);
                Speed *= Ramming.DriveOnFactor;
                return false;
            case RamOutcome.Bounced:
            case RamOutcome.Stopped:
                if (outcome == RamOutcome.Bounced)
                {
                    Sound?.Invoke(SoundCrash);
                }

                // Back where the move started; a bounce negates the speed (0x1355c5) and centres
                // the wheel (0x1355d1), a repeat touch only stops the car.
                Body.Position = prePos;
                CarYaw = preYaw;
                Speed = outcome == RamOutcome.Bounced ? -Speed : 0f;
                if (outcome == RamOutcome.Bounced)
                {
                    Wheel = 0f;
                }

                Body.Velocity = Body.Velocity with { X = 0f, Z = 0f };
                return true;
            default:
                return false;
        }
    }

    // ------------------------------------------------------------------ HK

    private void Hover(float dt, float fwdIn, float strIn, float vert, bool run)
    {
        var (look, right) = (PlayerController.Direction(Body.Yaw, Body.Pitch), new Vector3(MathF.Cos(Body.Yaw), 0f, -MathF.Sin(Body.Yaw)));
        var thrust = fwdIn * (fwdIn > 0f ? HkSpeed : HkBackSpeed);
        var side = strIn * (run ? HkStrafeRun : HkStrafe);
        var want = look * thrust + right * side + Vector3.UnitY * (vert * HkClimb);
        var demand = MathF.Abs(fwdIn) + MathF.Abs(strIn) + MathF.Abs(vert);
        var accel = demand > 0f
            ? (HkAccel * MathF.Abs(fwdIn) + HkStrafeAccel * MathF.Abs(strIn) + HkClimbAccel * MathF.Abs(vert)) / demand
            : HkAccel;
        var v = Body.Velocity;
        var gap = want - v;
        var step = accel * dt;
        v = gap.Length() <= step ? want : v + Vector3.Normalize(gap) * step;

        var capY = Ceiling - Body.Eye;
        if (Body.Position.Y >= capY && v.Y > 0f)
        {
            v.Y = 0f;                         // the stick cannot climb past it
        }

        Body.Velocity = v;
        Body.Position += _world.Depenetrate(_sphere, Body.Position, Skin);
        var was = Body.Position;
        MoveAndSlide(dt, false, null);
        ClampToCeiling(was);
        Roll = 0f;
    }

    /// <summary>After the move the eye never ends above the ceiling; when the hull would then sit
    /// inside what it hit, the move is undone the DOS way (old position, speed −¼).</summary>
    private void ClampToCeiling(Vector3 was)
    {
        var capY = Ceiling - Body.Eye;
        if (float.IsPositiveInfinity(Ceiling) || Body.Position.Y <= capY)
        {
            return;
        }

        var v = Body.Velocity;
        if (v.Y > 0f)
        {
            v.Y = 0f;
        }

        Body.Position = Body.Position with { Y = capY };
        if (_world.Overlaps(_sphere, Body.Position))
        {
            Body.Position = new Vector3(was.X, MathF.Min(was.Y, capY), was.Z);
            v = new Vector3(-v.X * 0.25f, MathF.Min(v.Y, 0f), -v.Z * 0.25f);
        }

        Body.Velocity = v;
    }

    // ------------------------------------------------------------------ the move

    /// <summary>Move, slide along what stops the hull; <paramref name="floorMinY"/> null: floating
    /// (every contact is a wall), else floors by the slope limit and the snap down.</summary>
    private void MoveAndSlide(float dt, bool onFloorBefore, float? floorMinY)
    {
        var motion = Body.Velocity * dt;
        var velocity = Body.Velocity;
        var onFloor = false;
        var floorNormal = Vector3.UnitY;
        var floorOwner = -1;
        OnWall = false;
        var firstPlane = Vector3.Zero;
        for (var i = 0; i < SlideIterations; i++)
        {
            var len = motion.Length();
            if (len < 1e-4f)
            {
                break;
            }

            if (!_world.Sweep(_sphere, Body.Position, motion, out var hit))
            {
                Body.Position += motion;
                break;
            }

            var dir = motion / len;
            var travel = MathF.Max(hit.Time * len - Skin, 0f);
            Body.Position += dir * travel;
            var n = hit.Normal;
            if (floorMinY is { } minY && n.Y >= minY)
            {
                onFloor = true;
                floorNormal = n;
                floorOwner = hit.Owner;
            }
            else if (n.Y > -0.1f)
            {
                OnWall = true;
            }

            var remaining = motion - dir * travel;
            var slid = remaining - n * Vector3.Dot(remaining, n);
            if (i > 0 && Vector3.Dot(slid, firstPlane) < 0f)
            {
                var crease = Vector3.Cross(firstPlane, n);
                slid = crease.LengthSquared() > 1e-8f
                    ? Vector3.Normalize(crease) * Vector3.Dot(remaining, Vector3.Normalize(crease))
                    : Vector3.Zero;
            }

            var into = Vector3.Dot(velocity, n);
            if (into < 0f)
            {
                velocity -= n * into;
            }

            firstPlane = n;
            motion = slid;
        }

        if (floorMinY is { } snapMin && !onFloor && onFloorBefore && velocity.Y <= 0f
            && _world.GroundProbe(_sphere, Body.Position, JeepFloorSnap, out var g) && g.Normal.Y >= snapMin)
        {
            Body.Position -= new Vector3(0f, MathF.Max(g.Time - Skin, 0f), 0f);
            onFloor = true;
            floorNormal = g.Normal;
            floorOwner = g.Owner;
        }

        if (onFloor && velocity.Y < 0f)
        {
            velocity.Y = 0f;
        }

        OnFloor = onFloor;
        Body.Velocity = velocity;
        Body.SetContact(onFloor, floorOwner, floorNormal);
    }

    // ------------------------------------------------------------------ the view

    /// <summary>The eye and the view between the last two ticks, the cab's roll included.</summary>
    public (Vector3 Eye, Vector3 Forward, Vector3 Up, Matrix4x4 View) View(float alpha)
    {
        var eye = Vector3.Lerp(_prevPos + new Vector3(0f, _prevEye, 0f), Body.EyePosition, alpha);
        var q = Quaternion.CreateFromYawPitchRoll(float.Lerp(_prevViewYaw, Body.Yaw, alpha),
            float.Lerp(_prevViewPitch, Body.Pitch, alpha), float.Lerp(_prevRoll, Roll, alpha));
        var forward = Vector3.Transform(-Vector3.UnitZ, q);
        var up = Vector3.Transform(Vector3.UnitY, q);
        return (eye, forward, up, Matrix4x4.CreateLookAt(eye, eye + forward, up));
    }

    /// <summary>
    /// Where the cockpit model is drawn (HUD table 0x44700 {handle, x, y, z}: FUN_00133d26 adds
    /// (x, y, z) &lt;&lt; 8 to every vertex, FUN_0012645c draws it at the camera). DOS y down, z ahead:
    /// the jeep's (26, 22, 8) is (26, −22, −8) here — the eye in the LEFT seat — on a pivot at the eye
    /// that rides the CAR and tilts with the cab, so the turret camera swings across it; the HK's
    /// (0, 4, 16) is (0, −4, −16), riding the camera.
    /// </summary>
    public Matrix4x4 CockpitWorld(float alpha)
    {
        var pos = Vector3.Lerp(_prevPos, Body.Position, alpha);
        var eye = float.Lerp(_prevEye, Body.Eye, alpha);
        if (Mode == VehicleMode.Jeep)
        {
            return Matrix4x4.CreateTranslation(CockpitOffset(Mode))
                * Matrix4x4.CreateFromYawPitchRoll(0f, float.Lerp(_prevTiltPitch, TiltPitch, alpha), float.Lerp(_prevTiltRoll, TiltRoll, alpha))
                * Matrix4x4.CreateTranslation(0f, eye, 0f)
                * Matrix4x4.CreateRotationY(float.Lerp(_prevCarYaw, CarYaw, alpha))
                * Matrix4x4.CreateTranslation(pos);
        }

        return Matrix4x4.CreateTranslation(CockpitOffset(Mode))
            * Matrix4x4.CreateFromYawPitchRoll(float.Lerp(_prevViewYaw, Body.Yaw, alpha), float.Lerp(_prevViewPitch, Body.Pitch, alpha), 0f)
            * Matrix4x4.CreateTranslation(pos + new Vector3(0f, eye, 0f));
    }

    /// <summary>The cockpit's .3D (HUD table 0x44700): HUMMER.3D, HKCOCKPT.3D.</summary>
    public static string CockpitModel(VehicleMode mode) => mode switch
    {
        VehicleMode.Jeep => "HUMMER.3D",
        VehicleMode.Hk => "HKCOCKPT.3D",
        _ => "",
    };

    /// <summary>The HUD table's (x, y, z) in Y-up axes (DOS y down, z ahead).</summary>
    public static Vector3 CockpitOffset(VehicleMode mode) => mode switch
    {
        VehicleMode.Jeep => new Vector3(26f, -22f, -8f),
        VehicleMode.Hk => new Vector3(0f, -4f, -16f),
        _ => Vector3.Zero,
    };

    private void Snapshot()
    {
        _prevPos = Body.Position;
        _prevEye = Body.Eye;
        _prevCarYaw = CarYaw;
        _prevViewYaw = Body.Yaw;
        _prevViewPitch = Body.Pitch;
        _prevRoll = Roll;
        _prevTiltPitch = TiltPitch;
        _prevTiltRoll = TiltRoll;
    }

    private static bool Held(in TickInput tick, int action) => action >= 0 && tick.IsDown(action);

    private static float MoveToward(float from, float to, float by) =>
        MathF.Abs(to - from) <= by ? to : from + MathF.Sign(to - from) * by;
}
