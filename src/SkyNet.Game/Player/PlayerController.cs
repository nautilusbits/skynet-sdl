using System.Numerics;
using RetroEng.Loop;
using RetroEng.Physics;

namespace SkyNet.Game.Player;

/// <summary>The game's action ids the soldier reads from a <see cref="TickInput"/>; −1: not bound.</summary>
public readonly record struct PlayerActions(
    int Forward, int Back, int Left, int Right, int Run, int Jump, int Crouch,
    int TurnLeft = -1, int TurnRight = -1, int LookUp = -1, int LookDown = -1, int Slide = -1);

/// <summary>Something that moves the player's body in place of the soldier's legs (a vehicle,
/// <c>SkyNet.Game.Vehicles.VehicleController</c>): it sets the position, view and eye itself.</summary>
public interface IPlayerMover
{
    void Step(PlayerController body, in TickInput tick, float seconds, in PlayerActions actions);
}

/// <summary>
/// The soldier on foot, one fixed tick at a time, with the Godot port's rules and DOS's numbers
/// (docs/game-logic/player-movement.md; the Godot port's fly_camera.gd _walk/_step_up/_crouch/_fly):
/// <list type="bullet">
/// <item>the keys pull the horizontal speed towards walk 250 / run 400 forward, 250 back, strafe
/// 210 / 310, at 1500 u/s² both ways; diagonals are not normalised;</item>
/// <item>gravity 392 u/s², a 177 u/s jump only when standing on a floor with the eye at its full
/// 75; landing faster than 396 u/s costs (v − 396)·1113/256 points;</item>
/// <item>floors are faces whose normal Y ≥ cos 40°; steeper ones are walls to slide along, never
/// climbed; a walkable slope, terrain or mesh, is crossed at the full horizontal speed (DOS
/// re-finds the floor under the feet, FUN_0012b18c); on a floor the body snaps down up to 150
/// units (Godot's floor_snap_length);</item>
/// <item>a grounded body stopped by a wall tries to step up: rise 80 (then 48, 24, 12), advance,
/// drop onto a walkable floor at least 2 units above the feet;</item>
/// <item>crouch drops the eye to 35 at 100 u/s (the body stays);</item>
/// <item>noclip flies along the view at 3500 u/s (× 2.5 running) through everything.</item>
/// </list>
/// The body is DOS's: three spheres of radius 15 stacked over 76 units (the Godot capsule 11 + 4
/// margin, height 76). <see cref="Position"/> is the feet. Ceilings collide, as in the Godot port.
/// </summary>
public sealed partial class PlayerController
{
    public const float WalkSpeed = 250f;
    public const float RunSpeed = 400f;
    public const float StrafeSpeed = 210f;
    public const float StrafeRunSpeed = 310f;
    public const float GroundAccel = 1500f;
    public const float Gravity = 392f;
    public const float JumpSpeed = 177f;
    public const float EyeHeight = 75f;
    public const float CrouchEye = 35f;
    public const float CrouchRate = 100f;
    public const float FlySpeed = 3500f;
    public const float FlyRun = 2.5f;
    public const float FootMaxSlopeDegrees = 40f;
    public const float StepHeight = 80f;
    public static readonly float[] StepRises = [StepHeight, 48f, 24f, 12f];
    public const float StepProbe = 14f;
    public const float FloorSnap = 150f;
    public const float FallSafeSpeed = 396f;
    public const float FallPointsPerUnit = 1113f / 256f;
    public const float KeyTurnStart = 0.098f;
    public const float KeyTurnMax = 5.50f;
    public const float KeyTurnAccel = 12.57f;
    public const float KeyLookRate = 3.14f;
    public const float PitchMax = 1.178f;
    /// <summary>Mouse look, radians per unit of <see cref="TickInput.LookX"/>: DOS's ≈ 0.176°/pixel.</summary>
    public const float MouseRadians = 0.176f * MathF.PI / 180f;
    public const float BodyRadius = 15f;
    public const float BodyHeight = 76f;
    /// <summary>How far clear of a surface a move stops.</summary>
    public const float Skin = 0.25f;
    private const int SlideIterations = 4;
    /// <summary>Anti-wedge (fly_camera.gd _track_stuck): this long pushing without progress tries
    /// a lift or a push out; this long returns to the last free spot.</summary>
    public const float StuckTime = 0.35f;
    public const float StuckResetTime = 1.8f;

    public static readonly float FloorMinY = MathF.Cos(FootMaxSlopeDegrees * MathF.PI / 180f);

    private readonly CollisionWorld _world;
    private Vector3 _previous;
    private float _previousEye, _previousYaw, _previousPitch;
    private float _turnRamp;
    private float _stuckTime, _goodTime;
    private Vector3? _lastGood;
    private Vector3 _contactNormals;

    public PlayerController(CollisionWorld world, SphereBody? body = null)
    {
        _world = world;
        Body = body ?? SphereBody.Stack(BodyRadius, BodyHeight, 3);
    }

    public SphereBody Body { get; }

    /// <summary>The feet.</summary>
    public Vector3 Position { get; set; }
    public Vector3 Velocity { get; set; }
    /// <summary>Radians about +Y, 0 looking −Z; not wrapped (so it interpolates).</summary>
    public float Yaw { get; set; }
    /// <summary>Radians, up positive, within ±<see cref="PitchMax"/>.</summary>
    public float Pitch { get; set; }
    /// <summary>The eye above the feet: 75, down to 35 crouched (a vehicle's seat sets its own).</summary>
    public float Eye { get; set; } = EyeHeight;
    public bool OnFloor { get; private set; }
    public bool OnWall { get; private set; }
    public bool OnCeiling { get; private set; }
    public Vector3 FloorNormal { get; private set; } = Vector3.UnitY;
    /// <summary>The map record the feet stand on (its file offset), −1 for the ground or none.</summary>
    public int FloorOwner { get; private set; } = -1;
    /// <summary>The map record of the last wall the move slid along (file offset), −1 for none or the ground.</summary>
    public int WallOwner { get; private set; } = -1;
    public bool Noclip { get; set; }
    /// <summary>The deathmatch class's pace under the host's CLASSES (<see cref="Net.DmProtocol.SpeedFactor"/>:
    /// BALANCED's TERMINATOR 0.9, else 1); 1 in the campaign.</summary>
    public float ClassSpeed { get; set; } = 1f;
    /// <summary>A vehicle driving the body instead of the legs; null on foot. Noclip still flies.</summary>
    public IPlayerMover? Mover { get; set; }
    /// <summary>Fall damage taken so far, in DOS's 1000-point scale (the game drains it).</summary>
    public float FallDamage { get; set; }
    /// <summary>How fast the body was falling when it landed this tick (u/s, 0: no landing this
    /// tick). Sound only (DOS's landing sound, FUN_0012b18c); not part of the simulation's state.</summary>
    public float LandedSpeed { get; private set; }

    public Vector3 EyePosition => Position + new Vector3(0f, Eye, 0f);

    /// <summary>Puts the soldier at <paramref name="feet"/>, standing still, looking level.</summary>
    public void Spawn(Vector3 feet, float yaw)
    {
        Position = feet;
        Velocity = Vector3.Zero;
        Yaw = yaw;
        Pitch = 0f;
        Eye = EyeHeight;
        OnFloor = false;
        _turnRamp = 0f;
        _stuckTime = _goodTime = 0f;
        _lastGood = null;
        Snapshot();
    }

    /// <summary>Puts the view at <paramref name="feet"/> looking along yaw and pitch with the eye at
    /// <paramref name="eye"/>, with no glide from where it was (a network demo's camera).</summary>
    public void Place(Vector3 feet, float yaw, float pitch, float eye)
    {
        Position = feet;
        Velocity = Vector3.Zero;
        Yaw = yaw;
        Pitch = Math.Clamp(pitch, -PitchMax, PitchMax);
        Eye = eye;
        Snapshot();
    }

    /// <summary>One tick of <paramref name="seconds"/>.</summary>
    public void Step(in TickInput tick, float seconds, in PlayerActions a)
    {
        Snapshot();
        LandedSpeed = 0f;
        if (Mover != null && !Noclip)
        {
            Mover.Step(this, tick, seconds, a);
            return;
        }

        Yaw -= tick.LookX * MouseRadians * LookScale;
        Pitch = Math.Clamp(Pitch - tick.LookY * MouseRadians * LookScale, -PitchMax, PitchMax);

        var fwd = 0f;
        var str = 0f;
        if (Held(tick, a.Forward)) fwd += 1f;
        if (Held(tick, a.Back)) fwd -= 1f;
        if (Held(tick, a.Right)) str += 1f;
        if (Held(tick, a.Left)) str -= 1f;
        // TURN LEFT / RIGHT turn the view on a ramp, or sidestep with SLIDE held.
        var turn = (Held(tick, a.TurnLeft) ? 1f : 0f) - (Held(tick, a.TurnRight) ? 1f : 0f);
        if (turn != 0f && !Held(tick, a.Slide))
        {
            _turnRamp = MathF.Min(MathF.Max(_turnRamp, KeyTurnStart) + KeyTurnAccel * seconds, KeyTurnMax);
            Yaw += turn * _turnRamp * seconds;
        }
        else
        {
            str -= turn;
            _turnRamp = 0f;
        }

        var tilt = (Held(tick, a.LookUp) ? 1f : 0f) - (Held(tick, a.LookDown) ? 1f : 0f);
        Pitch = Math.Clamp(Pitch + tilt * KeyLookRate * seconds, -PitchMax, PitchMax);
        fwd = Math.Clamp(fwd - tick.MoveY, -1f, 1f);
        str = Math.Clamp(str + tick.MoveX, -1f, 1f);

        var crouch = !Noclip && Held(tick, a.Crouch);
        Eye = MoveToward(Eye, crouch ? CrouchEye : EyeHeight, CrouchRate * seconds);
        var run = Held(tick, a.Run);
        if (Noclip)
        {
            Fly(seconds, fwd, str, (Held(tick, a.Jump) ? 1f : 0f) - (Held(tick, a.Crouch) ? 1f : 0f), run);
        }
        else
        {
            Walk(seconds, fwd, str, run, Held(tick, a.Jump));
        }
    }

    /// <summary>A mover's contact report: the floor under the body (its record, −1 the ground).</summary>
    public void SetContact(bool onFloor, int floorOwner = -1, Vector3? floorNormal = null)
    {
        OnFloor = onFloor;
        FloorOwner = onFloor ? floorOwner : -1;
        FloorNormal = floorNormal ?? Vector3.UnitY;
    }

    /// <summary>The eye and the view between the last two ticks.</summary>
    public (Vector3 Eye, float Yaw, float Pitch, Matrix4x4 View) View(float alpha)
    {
        var eye = Vector3.Lerp(_previous + new Vector3(0f, _previousEye, 0f), EyePosition, alpha);
        var yaw = float.Lerp(_previousYaw, Yaw, alpha);
        var pitch = float.Lerp(_previousPitch, Pitch, alpha);
        return (eye, yaw, pitch, Matrix4x4.CreateLookAt(eye, eye + Direction(yaw, pitch), Vector3.UnitY));
    }

    /// <summary>The unit view direction for a yaw and pitch.</summary>
    public static Vector3 Direction(float yaw, float pitch) =>
        new(-MathF.Sin(yaw) * MathF.Cos(pitch), MathF.Sin(pitch), -MathF.Cos(yaw) * MathF.Cos(pitch));

    private void Snapshot() => (_previous, _previousEye, _previousYaw, _previousPitch) = (Position, Eye, Yaw, Pitch);

    private static bool Held(in TickInput tick, int action) => action >= 0 && tick.IsDown(action);

    private static float MoveToward(float from, float to, float by) =>
        MathF.Abs(to - from) <= by ? to : from + MathF.Sign(to - from) * by;

    private void Walk(float dt, float fwd, float str, bool run, bool jumpHeld)
    {
        // The run modifier lifts the forward cap only; the sidestep has its own pair.
        var f = fwd * (fwd > 0f ? (run ? RunSpeed : WalkSpeed) : WalkSpeed);
        var s = str * (run ? StrafeRunSpeed : StrafeSpeed);
        var ahead = new Vector3(-MathF.Sin(Yaw), 0f, -MathF.Cos(Yaw));
        var side = new Vector3(MathF.Cos(Yaw), 0f, -MathF.Sin(Yaw));
        var want = (ahead * f + side * s) * (SpeedScale * ClassSpeed);
        var h = new Vector3(Velocity.X, 0f, Velocity.Z);
        var gap = want - h;
        var step = AccelNow * dt;
        h = gap.Length() <= step ? want : h + Vector3.Normalize(gap) * step;

        var vy = Velocity.Y;
        if (OnFloor)
        {
            vy = jumpHeld && Eye == EyeHeight ? JumpNow : 0f;
        }
        else
        {
            vy -= GravityNow * dt;
        }

        Velocity = new Vector3(h.X, vy, h.Z);
        Position += _world.Depenetrate(Body, Position, Skin);
        var before = Position;
        var onFloorBefore = OnFloor;
        var fallSpeed = -vy;
        MoveAndSlide(dt, onFloorBefore);
        if (!onFloorBefore && OnFloor)
        {
            LandedSpeed = MathF.Max(0f, fallSpeed);
            if (fallSpeed > FallSafeSpeed)
            {
                FallDamage += (fallSpeed - FallSafeSpeed) * FallPointsPerUnit;
            }
        }

        var moving = h.Length();
        if (onFloorBefore && moving > 1f && OnWall)
        {
            StepUp(h, moving * dt);
        }

        TrackStuck(dt, want.Length() > 1f, Vector3.Distance(Position, before));
    }

    /// <summary>Detailed props can wedge the body off the floor (a crease between two steep faces,
    /// a vehicle's seat): pushing without progress lifts it 48 when there is room, else pushes it
    /// out along the contacts, and at last puts it back where it last walked freely.</summary>
    private void TrackStuck(float dt, bool wantsMove, float moved)
    {
        // Standing on a floor against a wall is not a wedge: the Godot port lifts there too, and
        // the lift then walks up the hills DOS's 40° rule keeps the soldier off.
        if (wantsMove && moved < 0.5f && !OnFloor)
        {
            _stuckTime += dt;
        }
        else
        {
            _stuckTime = 0f;
            if (moved > 2f && OnFloor)
            {
                _goodTime += dt;
                if (_goodTime > 0.5f)
                {
                    _goodTime = 0f;
                    _lastGood = Position;
                }
            }
        }

        if (_stuckTime < StuckTime)
        {
            return;
        }

        var up = new Vector3(0f, 48f, 0f);
        if (!_world.Sweep(Body, Position, up, out _))
        {
            Position += up;
            return;
        }

        if (_contactNormals.LengthSquared() > 1e-4f)
        {
            var push = Vector3.Normalize(_contactNormals) * 30f;
            if (!_world.Sweep(Body, Position, push, out _))
            {
                Position += push;
                return;
            }
        }

        if (_stuckTime > StuckResetTime && _lastGood is { } good)
        {
            Position = good + new Vector3(0f, 10f, 0f);
            Velocity = Vector3.Zero;
            _stuckTime = 0f;
        }
    }

    /// <summary>Godot's move_and_slide for this body: move, slide along what stops it (a crease
    /// between two planes), floors by the slope limit, walls kept from being climbed while on the
    /// floor, then the floor snap.</summary>
    private void MoveAndSlide(float dt, bool onFloorBefore)
    {
        var motion = Velocity * dt;
        OnFloor = OnWall = OnCeiling = false;
        _contactNormals = Vector3.Zero;
        var firstPlane = Vector3.Zero;
        // A body in the air that is not going up slides down a steep face but never up it: DOS
        // refuses a floor ahead past 40° unless the body is rising (FUN_0012b18c). Sliding up it
        // carried a soldier who left the floor for a tick up 42-45° hills.
        var noClimb = !onFloorBefore && Velocity.Y <= 0f;
        for (var i = 0; i < SlideIterations; i++)
        {
            var len = motion.Length();
            if (len < 1e-4f)
            {
                break;
            }

            if (!_world.Sweep(Body, Position, motion, out var hit))
            {
                Position += motion;
                break;
            }

            var dir = motion / len;
            var travel = MathF.Max(hit.Time * len - Skin, 0f);
            Position += dir * travel;
            var n = hit.Normal;
            _contactNormals += n;
            if (n.Y >= FloorMinY)
            {
                OnFloor = true;
                FloorNormal = n;
                FloorOwner = hit.Owner;
            }
            else if (n.Y < -0.1f)
            {
                OnCeiling = true;
            }
            else
            {
                OnWall = true;
                WallOwner = hit.Owner;
                if ((onFloorBefore || OnFloor) && n.Y > 0f)
                {
                    // A slope too steep to stand on is a wall: never walked up.
                    var flat = new Vector3(n.X, 0f, n.Z);
                    n = flat.LengthSquared() > 1e-8f ? Vector3.Normalize(flat) : n;
                }
            }

            var remaining = motion - dir * travel;
            Vector3 slid;
            if (n.Y >= FloorMinY)
            {
                // A walkable floor keeps the horizontal motion and only lifts it onto the plane:
                // DOS moves the feet across at the full speed and re-finds the floor under them
                // (FUN_0012b18c, probe 0x138d00), terrain and meshes alike. Projecting onto the
                // plane instead cost cos² of the slope every tick.
                var across = new Vector3(remaining.X, 0f, remaining.Z);
                slid = across with { Y = -(n.X * across.X + n.Z * across.Z) / n.Y };
            }
            else
            {
                slid = remaining - n * Vector3.Dot(remaining, n);
            }

            if (noClimb && !OnFloor && slid.Y > 0f)
            {
                slid.Y = 0f;
            }

            if (i > 0 && Vector3.Dot(slid, firstPlane) < 0f)
            {
                // Into the previous plane again: follow the crease between the two.
                var crease = Vector3.Cross(firstPlane, n);
                slid = crease.LengthSquared() > 1e-8f
                    ? Vector3.Normalize(crease) * Vector3.Dot(remaining, Vector3.Normalize(crease))
                    : Vector3.Zero;
            }

            // A floor takes only the fall (zeroed below); the walking speed stays.
            var into = Vector3.Dot(Velocity, n);
            if (into < 0f && n.Y < FloorMinY)
            {
                Velocity -= n * into;
                if (noClimb && Velocity.Y > 0f)
                {
                    Velocity = Velocity with { Y = 0f };
                }
            }

            firstPlane = n;
            motion = slid;
        }

        if (!OnFloor && onFloorBefore && Velocity.Y <= 0f
            && _world.GroundProbe(Body, Position, FloorSnap, out var ground) && ground.Normal.Y >= FloorMinY)
        {
            Position -= new Vector3(0f, MathF.Max(ground.Time - Skin, 0f), 0f);
            OnFloor = true;
            FloorNormal = ground.Normal;
            FloorOwner = ground.Owner;
        }

        if (OnFloor && Velocity.Y < 0f)
        {
            Velocity = Velocity with { Y = 0f };
        }
    }

    /// <summary>Stair step (fly_camera.gd _step_up): the same motion from up to 80 higher (then the
    /// lower rises, for a sill under a low lintel), settled back onto a walkable floor above the
    /// feet. Nothing happens when every raised probe is blocked or finds no floor.</summary>
    private void StepUp(Vector3 horizontal, float advance)
    {
        var fwd = Vector3.Normalize(horizontal) * MathF.Max(advance, StepProbe);
        var start = Position;
        foreach (var rise in StepRises)
        {
            if (StepUpFrom(start, fwd, rise))
            {
                return;
            }
        }
    }

    private bool StepUpFrom(Vector3 start, Vector3 fwd, float height)
    {
        // 1. Rise as far as the height allows.
        var rise = Travel(start, new Vector3(0f, height, 0f), out _);
        if (rise < 4f)
        {
            return true;
        }

        var at = start + new Vector3(0f, rise, 0f);
        // 2. Advance from up there; blocked means a wall at this height (a lower rise may pass).
        var fwdLength = fwd.Length();
        var adv = Travel(at, fwd, out _);
        if (adv < StepProbe * 0.5f)
        {
            return false;
        }

        at += fwd / fwdLength * adv;
        // 3. Drop back; it must land on a walkable floor above where the feet started.
        var drop = rise + 2f;
        if (!_world.Sweep(Body, at, new Vector3(0f, -drop, 0f), out var hit) || hit.Normal.Y < FloorMinY)
        {
            return true;
        }

        var landing = at - new Vector3(0f, MathF.Max(hit.Time * drop - Skin, 0f), 0f);
        if (landing.Y - start.Y < 2f)
        {
            return true;
        }

        Position = landing;
        Velocity = Velocity with { Y = 0f };
        OnFloor = true;
        FloorNormal = hit.Normal;
        FloorOwner = hit.Owner;
        return true;
    }

    /// <summary>How far the body gets along <paramref name="motion"/> (units, short of a hit by the skin).</summary>
    private float Travel(Vector3 from, Vector3 motion, out Hit hit)
    {
        var len = motion.Length();
        return _world.Sweep(Body, from, motion, out hit) ? MathF.Max(hit.Time * len - Skin, 0f) : len;
    }

    private void Fly(float dt, float fwd, float str, float vert, bool run)
    {
        var look = Direction(Yaw, Pitch);
        var side = new Vector3(MathF.Cos(Yaw), 0f, -MathF.Sin(Yaw));
        var dir = look * fwd + side * str + Vector3.UnitY * vert;
        Velocity = Vector3.Zero;
        OnFloor = false;
        if (dir.LengthSquared() > 0f)
        {
            Position += Vector3.Normalize(dir) * FlySpeed * (run ? FlyRun : 1f) * dt;
        }
    }
}
