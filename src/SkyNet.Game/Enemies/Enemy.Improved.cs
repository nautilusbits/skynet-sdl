using System.Numerics;
using RetroEng.Physics;

namespace SkyNet.Game.Enemies;

/// <summary>
/// ROBOT AI IMPROVED (<see cref="RobotAi.Improved"/>). DOS's states, scripts, attacks, weapons and
/// damage stay; only how a robot moves and when it fires change:
/// <code>
///   flyers   swept body (never inside the level) + look-ahead: turn or climb round props
///   walkers  probe ahead (middle and shoulders) → follow the obstacle round; no progress for 1 s →
///            back off;
///            keep apart from other robots
///   all      remember the last sighting: go there, then search round the soldier; side-step when
///            DOS would walk in place next to him; fire only along a clear line
/// </code>
/// CLASSIC never reaches this code, so its runs stay DOS's (DosParityTests).
/// </summary>
public sealed partial class Enemy
{
    /// <summary>How long a sighting is remembered (s).</summary>
    private const float MemoryTime = 20f;
    /// <summary>A spot is reached within this (flat units).</summary>
    private const float Arrive = 60f;
    /// <summary>The search round the soldier: radius and how often a new spot is picked.</summary>
    private const float SearchRadius = 500f, SearchRetarget = 5f;
    /// <summary>Following an obstacle: headings are probed this far ahead; it is given up after this long (s).</summary>
    private const float FollowProbe = 80f, FollowMax = 8f;
    /// <summary>Headings a wander tries for a clear way.</summary>
    private const int RoamTries = 6;
    /// <summary>The obstacle is left once the goal's way is clear this far ahead and the goal is no more
    /// than <see cref="LeaveSlack"/> further than where the obstacle was met (units).</summary>
    private const float LeaveProbe = 400f, LeaveSlack = 150f;
    /// <summary>A face steeper than this (|normal Y| below) met at knee height is a wall.</summary>
    private const float WallNormalY = 0.7f;
    /// <summary>The headings tried while following, from the last one (degrees; negative: toward the
    /// obstacle, positive: away from it).</summary>
    private static readonly float[] FollowAngles = [-60f, -30f, 0f, 30f, 60f, 90f, 120f, 150f];
    /// <summary>Stuck: trying to move, facing its way, for this long with less than this share of
    /// its speed covered; it then backs off for <see cref="BackOffTime"/>.</summary>
    private const float StuckTime = 1f, StuckShare = 0.2f, BackOffTime = 1.5f, StuckLong = 3f;
    /// <summary>Facing within this of where it wants to go counts as trying (radians).</summary>
    private const float FacingSlack = 0.35f;
    /// <summary>The side-step: within near × this of the soldier, step to near × <see cref="SideStepOut"/>
    /// from him, this far round (radians).</summary>
    private const float SideStepNear = 1.2f, SideStepOut = 2.5f, SideStepArc = 1.05f;
    /// <summary>A flyer looks ahead this many seconds of flight, within these bounds (units).</summary>
    private const float FlyLookSeconds = 0.8f, FlyLookMin = 150f;
    /// <summary>An avoidance is kept this long (s); the turn is this much faster meanwhile.</summary>
    private const float AvoidTime = 0.8f, AvoidTurnBoost = 1.5f;
    /// <summary>The flyer's headings tried, either side (degrees); 0 is the climb.</summary>
    private static readonly float[] AvoidAngles = [35f, 0f, 70f, 110f, 150f];
    /// <summary>A climb goes this many body heights up.</summary>
    private const float ClimbHeights = 1.5f;
    /// <summary>The flyer's hull: sphere radius bounds, the gap kept from surfaces.</summary>
    private const float HullMin = 8f, HullMax = 150f, Skin = 2f;
    private const int SlideIterations = 3, DepenetrateRounds = 4;
    /// <summary>Re-aim the hull's spheres once the yaw moved this much (radians).</summary>
    private const float HullYawStep = 0.05f;

    private Vector3 _halfSize = new(30f);
    private Vector3? _lastSeen;
    private float _memoryT;
    private bool _searching;
    private Vector3 _searchSpot;
    private float _searchT;
    private bool _following;
    private float _followT;
    private int _followSide = 1;
    private float _metAt;
    private float _backOffT;
    private float _backOffYaw;
    private float _stuckT;
    private float _stillT;
    private Vector3 _stuckFrom;
    private Vector3? _sideStep;
    private int _sideStepSide = 1;
    private float _avoidT;
    private float _avoidYaw;
    private float? _climbTo;
    private SphereBody? _hull;
    private float _hullYaw = float.NaN;

    private bool Improved => _ctx.Ai == RobotAi.Improved;

    /// <summary>The robot's half width on the ground, for keeping apart.</summary>
    private float Spacing => MathF.Max(MathF.Max(_halfSize.X, _halfSize.Z), 20f);

    private static Vector3 Flat(Vector3 v) => v with { Y = 0f };

    private static Vector3 Heading(float yaw) => new(-MathF.Sin(yaw), 0f, -MathF.Cos(yaw));

    private static float YawOf(Vector3 flat) => MathF.Atan2(-flat.X, -flat.Z);

    private static float Rad(float degrees) => degrees * MathF.PI / 180f;

    // ------------------------------------------------------------------ memory

    /// <summary>A sighting is kept: where, and for <see cref="MemoryTime"/>.</summary>
    private void Remember(in AisSenses senses, Vector3? player)
    {
        if (!senses.See || player is not { } p)
        {
            return;
        }

        _lastSeen = p;
        _memoryT = MemoryTime;
        _searching = false;
    }

    /// <summary>Where an active robot goes while it does not see the soldier: the last sighting, then
    /// spots round the soldier; with nothing remembered, DOS's wander.</summary>
    private Vector3 Hunt(float dt, Vector3 player)
    {
        if (_lastSeen is not { } seen || _memoryT <= 0f)
        {
            _lastSeen = null;
            return Roam(dt);
        }

        _memoryT -= dt;
        if (!_searching)
        {
            if (Flat(seen - Position).Length() > Arrive)
            {
                return seen;
            }

            _searching = true;
            _searchT = 0f;
        }

        _searchT -= dt;
        if (_searchT <= 0f || Flat(_searchSpot - Position).Length() <= Arrive)
        {
            var rng = _ctx.Rng;
            var a = rng.NextSingle() * MathF.Tau;
            _searchSpot = player + new Vector3(MathF.Cos(a), 0f, MathF.Sin(a)) * (SearchRadius * rng.NextSingle());
            _searchT = SearchRetarget;
        }

        return _searchSpot;
    }

    /// <summary>DOS's wander (a new spot every 4-9 s, up to <see cref="WanderRadius"/> away), but toward
    /// a spot whose way is clear for a while: a few headings are tried; a blocked wander spot is
    /// dropped at once.</summary>
    private Vector3 Roam(float dt)
    {
        _wanderT -= dt;
        if (_wanderT > 0f && _wanderTarget != Vector3.Zero && !(_blocked && !_following))
        {
            return _wanderTarget;
        }

        var rng = _ctx.Rng;
        _wanderT = 4f + 5f * rng.NextSingle();
        var reach = 300f + (WanderRadius - 300f) * rng.NextSingle();
        var a0 = rng.NextSingle() * MathF.Tau;
        for (var i = 0; i < RoamTries; i++)
        {
            var dir = Heading(a0 + i * MathF.Tau / RoamTries);
            if (!LongBlocked(dir))
            {
                return _wanderTarget = Position + dir * reach;
            }
        }

        return _wanderTarget = Position + Heading(a0) * reach;
    }

    /// <summary>Next to the soldier, where DOS walks in place: a spot further out and round him, kept
    /// until reached (then the other side next time).</summary>
    private Vector3 SideStep(Vector3 player, float near)
    {
        if (_sideStep is { } spot && Flat(spot - Position).Length() > Arrive)
        {
            return spot;
        }

        var from = Flat(Position - player);
        var a = YawOf(from.LengthSquared() > 1f ? from : Forward) + _sideStepSide * SideStepArc;
        _sideStepSide = -_sideStepSide;
        _sideStep = player + Heading(a) * MathF.Max(near * SideStepOut, 200f);
        return _sideStep.Value;
    }

    // ------------------------------------------------------------------ the move

    private void MoveImproved(float dt, in AisSenses senses, Vector3 player)
    {
        var see = senses.See;
        var speed = MoveSpeed;
        if (State == 13 && Brain != null)
        {
            speed = Brain.Var(48, Type?.Speed ?? 0);
        }
        else if (State == 9 && Brain != null)
        {
            speed = Brain.Var(56, Type?.ForwardSpeed ?? 400);
        }

        // The walker moves while its script plays a walk cycle, as in DOS.
        var moving = true;
        if (State == 7 && Brain != null)
        {
            moving = (Brain.Looping || (Brain.OneShotWalker && Brain.FiringPose && (Brain.AnimFlags & AisBrain.AnimDone) == 0))
                && !Brain.FreezeAnim;
        }

        var flyer = State == 9 || IsFlying;
        var near = Type?.Near ?? 100;
        var target = see ? player : Hunt(dt, player);
        var sideStep = false;
        if (see && !flyer && State != 13 && moving && Flat(player - Position).Length() <= near * SideStepNear)
        {
            target = SideStep(player, near);
            sideStep = true;
        }
        else if (!see || Flat(player - Position).Length() > near * SideStepOut)
        {
            _sideStep = null;
        }

        var to = Flat(target - Position);
        var dist = to.Length();

        // A walker on its nav path heads for the next corner; the stop stays the goal's distance.
        var clear = LeaveProbe;
        _dropLook = DropLook;
        var nav = NavStep.Straight;
        var corner = target;
        var left = dist;
        if (!flyer)
        {
            nav = Steer(target, dt, out corner, out left);
        }

        _step = nav;
        if (nav == NavStep.Corner)
        {
            // The mesh vouches for the floor up to the corner: no drop probe past it (a catwalk's turn).
            to = Flat(corner - Position);
            clear = MathF.Min(LeaveProbe, to.Length());
            _dropLook = Math.Clamp(to.Length(), DropLookMin, DropLook);
        }

        var wantYaw = to.Length() > 1f ? YawOf(to) : Yaw;
        if (State == 13 && Brain != null && Brain.HasVar(44))
        {
            wantYaw = Brain.Var(44, 0) / 2048f * MathF.Tau;
        }

        if ((State == 13 && speed < 0f) || State == 9)
        {
            moving = true;
        }
        else if (dist <= (see && !sideStep ? near : Arrive * 0.5f) || nav == NavStep.Hold)
        {
            moving = false;
        }

        if (flyer)
        {
            Fly(dt, wantYaw, speed, see, player);
            return;
        }

        Walk(dt, wantYaw, left, clear, moving ? speed : 0f);
    }

    // ------------------------------------------------------------------ walkers

    /// <summary>
    /// A ground robot's step. Blocked on the way to its goal, it follows the obstacle (a wall, a
    /// ledge, a prop, another robot) with it on one side, until the goal's way is clear again from a
    /// spot nearer than where it met it (the "bug" walk):
    /// <code>
    ///   goal ◄─────────┐            met the wall at M; follows it round the end;
    ///        ███████████  M ◄─ R    leaves it once the goal is clear and nearer than from M
    /// </code>
    /// The goal's way counts as clear when nothing stands in it for <paramref name="clear"/> units
    /// (on a nav path: up to its next corner).
    /// </summary>
    private void Walk(float dt, float goalYaw, float goalDist, float clear, float speed)
    {
        var wantYaw = goalYaw;
        var trying = MathF.Abs(speed) > 0.5f;
        if (_backOffT > 0f)
        {
            _backOffT -= dt;
            wantYaw = _backOffYaw;
        }
        else if (_following && trying)
        {
            _followT += dt;
            var leave = goalDist < _metAt + LeaveSlack && !GroundBlocked(Heading(goalYaw), FollowProbe) && !LongBlocked(Heading(goalYaw), clear);
            if (leave || _followT > FollowMax)
            {
                _following = false;
                _followSide = leave ? _followSide : -_followSide;
            }
            else
            {
                wantYaw = FollowHeading();
            }
        }

        Yaw = Wrap(Approach(Yaw, wantYaw, TurnRate * dt));

        // On a nav path, slow while turning (in place past a right angle): a catwalk's turn is not overshot.
        if (_step == NavStep.Corner && trying)
        {
            speed *= MathF.Max(MathF.Cos(Wrap(wantYaw - Yaw)), 0f);
            trying = MathF.Abs(speed) > 0.5f;
        }

        _blocked = false;
        if (trying)
        {
            var dir = speed < 0f ? -Forward : Forward;
            if (GroundBlocked(dir, MathF.Abs(speed) * dt))
            {
                _blocked = true;
                if (!_following && _backOffT <= 0f)
                {
                    _following = true;
                    _followT = 0f;
                    _metAt = goalDist;
                }
            }
            else
            {
                Position += dir * (MathF.Abs(speed) * dt);
            }
        }

        KeepApart(dt);
        Progress(dt, trying, MathF.Abs(Wrap(wantYaw - Yaw)) < FacingSlack, speed);
        SnapToGround(false);
    }

    /// <summary>The way along the obstacle: from the facing, the turn back toward the obstacle
    /// first (hugging it), then straight on, then away from it, then back.</summary>
    private float FollowHeading()
    {
        var away = _followSide;
        foreach (var angle in FollowAngles)
        {
            var yaw = Wrap(Yaw + away * Rad(angle));
            if (!GroundBlocked(Heading(yaw), FollowProbe))
            {
                return yaw;
            }
        }

        return Wrap(Yaw + MathF.PI);
    }

    /// <summary>A step of <paramref name="length"/> along <paramref name="dir"/> meets a wall or prop
    /// (three rays: the middle and both shoulders), a step too high or steep, a drop, or a robot.</summary>
    private bool GroundBlocked(Vector3 dir, float length)
    {
        var step = dir * length;
        if (PathBlocked(step) || TooSteep(dir) || DropAhead(dir, _dropLook))
        {
            return true;
        }

        // Shoulders at the eye's height, and all three just over a step's height (a kerb, a knee-high
        // wall), the whole way.
        var side = new Vector3(-dir.Z, 0f, dir.X) * Shoulder;
        var knee = Position.Y + FootOffset + (Terminator ? StepTerminator : StepOther) + 1f;
        foreach (var s in new[] { side, -side, Vector3.Zero })
        {
            var eye = Position + s + new Vector3(0f, 60f, 0f);
            if (s != Vector3.Zero && Cast(eye, eye + dir * (length + 40f), out _))
            {
                return true;
            }

            // A slope or stair tread met low is ground (TooSteep judges it); a steep face is a wall.
            var low = eye with { Y = knee };
            if (Cast(low, low + dir * (length + 40f), out var hit) && MathF.Abs(hit.Normal.Y) < WallNormalY)
            {
                return true;
            }
        }

        return RobotAhead(dir, length);
    }

    /// <summary>A wall at knee or eye height within <paramref name="reach"/> along <paramref name="dir"/>.</summary>
    private bool LongBlocked(Vector3 dir, float reach = LeaveProbe)
    {
        var knee = Position + new Vector3(0f, FootOffset + (Terminator ? StepTerminator : StepOther) + 1f, 0f);
        var eye = Position + new Vector3(0f, 60f, 0f);
        return (Cast(knee, knee + dir * reach, out var hit) && MathF.Abs(hit.Normal.Y) < WallNormalY)
            || Cast(eye, eye + dir * reach, out _);
    }

    /// <summary>Another robot stands in the way within one step and both spacings.</summary>
    private bool RobotAhead(Vector3 dir, float length)
    {
        foreach (var o in _ctx.All)
        {
            if (o == this || !o.Alive || o.IsFlying || o.State == 9)
            {
                continue;
            }

            var d = Flat(o.Position - Position);
            var along = Vector3.Dot(d, dir);
            var room = Spacing + o.Spacing;
            if (along <= 0f || along > room + length)
            {
                continue;
            }

            if ((d - dir * along).Length() < room * 0.8f)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Stuck: a second of trying, facing its way, with almost no ground covered (or
    /// <see cref="StuckLong"/> s of it whatever the facing: wedged, turning in place). It backs off the
    /// other way (a random half circle behind), follows walls on the other side next, and gives up a
    /// remembered spot it cannot reach (the search round the soldier takes over).</summary>
    private void Progress(float dt, bool trying, bool facing, float speed)
    {
        if (!trying)
        {
            _stuckT = _stillT = 0f;
            _stuckFrom = Position;
            return;
        }

        _stuckT += dt;
        if (_stuckT < StuckTime)
        {
            return;
        }

        var moved = Flat(Position - _stuckFrom).Length();
        _stuckT = 0f;
        _stuckFrom = Position;
        if (moved >= MathF.Abs(speed) * StuckTime * StuckShare)
        {
            _stillT = 0f;
            return;
        }

        _stillT += StuckTime;
        if (!facing && _stillT < StuckLong)
        {
            return;
        }

        _stillT = 0f;

        var rng = _ctx.Rng;
        _backOffYaw = Wrap(Yaw + MathF.PI + (rng.NextSingle() * 2f - 1f) * MathF.PI * 0.5f);
        _backOffT = BackOffTime;
        _following = false;
        _followSide = -_followSide;
        _searching |= _lastSeen != null;
        _sideStep = null;
        _pathTo = null;
        Unstuck++;
    }

    /// <summary>Times the robot found itself stuck and backed off (tests, logs).</summary>
    public int Unstuck { get; private set; }

    /// <summary>Two ground robots closer than their spacings push this one out, up to its speed, where
    /// the floor allows.</summary>
    private void KeepApart(float dt)
    {
        if (IsStationary)
        {
            return;
        }

        var push = Vector3.Zero;
        foreach (var o in _ctx.All)
        {
            if (o == this || !o.Alive || o.IsFlying || o.State == 9)
            {
                continue;
            }

            var d = Flat(Position - o.Position);
            var len = d.Length();
            var room = Spacing + o.Spacing;
            if (len >= room || len < 1e-3f)
            {
                continue;
            }

            push += d / len * (room - len) * 0.5f;
        }

        var size = push.Length();
        if (size < 1e-3f)
        {
            return;
        }

        var dir = push / size;
        push = dir * MathF.Min(size, MathF.Max(MoveSpeed, 30f) * dt);
        if (!PathBlocked(push) && !DropAhead(dir))
        {
            Position += push;
        }
    }

    // ------------------------------------------------------------------ flyers

    /// <summary>The flyer's hull: spheres of the body's half height along its long axis and across,
    /// turned with the yaw (rebuilt when the yaw moved).</summary>
    private SphereBody Hull()
    {
        if (_hull != null && MathF.Abs(Wrap(Yaw - _hullYaw)) < HullYawStep)
        {
            return _hull;
        }

        var h = _halfSize;
        var longZ = h.Z >= h.X;
        var lengthHalf = longZ ? h.Z : h.X;
        var widthHalf = longZ ? h.X : h.Z;
        var r = Math.Clamp(MathF.Max(h.Y, widthHalf * 0.7f), HullMin, HullMax);
        var along = longZ ? Vector3.UnitZ : Vector3.UnitX;
        var across = longZ ? Vector3.UnitX : Vector3.UnitZ;
        var offsets = new List<Vector3> { BoundsCentre };
        var reach = lengthHalf - r;
        if (reach > 1f)
        {
            offsets.AddRange([BoundsCentre + along * reach, BoundsCentre - along * reach]);
            if (reach > r)
            {
                offsets.AddRange([BoundsCentre + along * reach * 0.5f, BoundsCentre - along * reach * 0.5f]);
            }
        }

        if (widthHalf - r > 1f)
        {
            offsets.AddRange([BoundsCentre + across * (widthHalf - r), BoundsCentre - across * (widthHalf - r)]);
        }

        var turn = Matrix4x4.CreateRotationY(Yaw);
        _hull = new SphereBody(r, [.. offsets.Select(o => Vector3.Transform(o, turn))]);
        _hullYaw = Yaw;
        return _hull;
    }

    /// <summary>The hull moved by <paramref name="motion"/> from <paramref name="from"/> meets the level.</summary>
    private bool HullHits(SphereBody hull, Vector3 from, Vector3 motion) =>
        motion.LengthSquared() > 1e-6f && _ctx.World.Sweep(hull, from, motion, out _);

    private void Fly(float dt, float wantYaw, float speed, bool see, Vector3 player)
    {
        var hull = Hull();
        Position += _ctx.World.Depenetrate(hull, Position, Skin, DepenetrateRounds);

        // Look ahead along the flight (backwards while backing off); a clear way keeps the course.
        var sign = speed < 0f ? -1f : 1f;
        var look = MathF.Max(MathF.Abs(speed) * FlyLookSeconds, FlyLookMin);
        if (_avoidT > 0f)
        {
            _avoidT -= dt;
            wantYaw = _avoidYaw;
        }
        else if (MathF.Abs(speed) > 0.5f && HullHits(hull, Position, Heading(Yaw) * (sign * look)))
        {
            Avoid(hull, wantYaw, sign, look);
            wantYaw = _avoidT > 0f ? _avoidYaw : wantYaw;
        }

        if (_avoidT <= 0f)
        {
            _climbTo = null;
        }

        Yaw = Wrap(Approach(Yaw, wantYaw, TurnRate * dt * (_avoidT > 0f ? AvoidTurnBoost : 1f)));
        hull = Hull();
        var motion = Forward * (speed * dt) + new Vector3(0f, Lift(dt, speed, see, player), 0f);
        _blocked = Slide(hull, motion);
    }

    /// <summary>A clear way round what lies ahead: a turn toward the goal's side first, the climb
    /// over it, then wider turns either side; held for <see cref="AvoidTime"/>.</summary>
    private void Avoid(SphereBody hull, float wantYaw, float sign, float look)
    {
        var first = Wrap(wantYaw - Yaw) >= 0f ? 1 : -1;
        var rise = MathF.Max(BodyHeight, 40f) * ClimbHeights;
        foreach (var angle in AvoidAngles)
        {
            if (angle == 0f)
            {
                var up = new Vector3(0f, rise, 0f);
                if (!HullHits(hull, Position, up) && !HullHits(hull, Position + up, Heading(Yaw) * (sign * look)))
                {
                    _avoidYaw = Yaw;
                    _climbTo = Position.Y + rise;
                    _avoidT = AvoidTime;
                    return;
                }

                continue;
            }

            foreach (var side in new[] { first, -first })
            {
                var yaw = Wrap(Yaw + side * Rad(angle));
                if (HullHits(hull, Position, Heading(yaw) * (sign * look)))
                {
                    continue;
                }

                _avoidYaw = yaw;
                _avoidT = AvoidTime;
                return;
            }
        }

        // Boxed in: turn about (the swept move still keeps the hull out of the level).
        _avoidYaw = Wrap(Yaw + MathF.PI);
        _avoidT = AvoidTime;
    }

    /// <summary>The vertical step: DOS's altitude rules (<see cref="Move"/>), raised to a climb's height.</summary>
    private float Lift(float dt, float speed, bool see, Vector3 player)
    {
        if (State == 9)
        {
            var minAlt = MathF.Max((Type?.Altitude ?? 384) - 100f, FlyerMinAlt);
            var reach = MathF.Max(Type?.AvoidRadius ?? 400, FlyerLookahead);
            var floor = SurfaceAt(Position);
            foreach (var f in new[] { 0.34f, 0.67f, 1f })
            {
                floor = MathF.Max(floor, SurfaceAt(Position + Forward * (reach * f)));
            }

            var wantY = MathF.Max(player.Y + 31f, floor + minAlt);
            if (_climbTo is { } climb)
            {
                wantY = MathF.Max(wantY, climb);
            }

            var dy = wantY - Position.Y;
            var urgent = Position.Y < floor + minAlt * 0.5f || _climbTo != null;
            var vmax = (dy > 0f ? FlyerClimbSpeed * (urgent ? 3f : 1f) : FlyerSinkSpeed) * dt;
            return Math.Clamp(dy, -vmax, vmax);
        }

        if (State == 6 && IsKamikaze && see)
        {
            var dy = player.Y + 40f - Position.Y;
            return Math.Clamp(dy, -speed * dt, speed * dt);
        }

        var fl = MathF.Max(SurfaceAt(Position), SurfaceAt(Position + Forward * FlyerLookahead));
        var need = MathF.Max(fl + FlyerMinAlt - Position.Y, (_climbTo ?? float.NegativeInfinity) - Position.Y);
        return need > 0f ? MathF.Min(need, FlyerClimbSpeed * dt) : 0f;
    }

    /// <summary>Moves the hull by <paramref name="motion"/>, sliding along what it meets; true when it met something.</summary>
    private bool Slide(SphereBody hull, Vector3 motion)
    {
        var met = false;
        for (var i = 0; i < SlideIterations; i++)
        {
            var len = motion.Length();
            if (len < 1e-4f)
            {
                break;
            }

            if (!_ctx.World.Sweep(hull, Position, motion, out var hit))
            {
                Position += motion;
                break;
            }

            met = true;
            var dir = motion / len;
            var travel = MathF.Max(hit.Time * len - Skin, 0f);
            Position += dir * travel;
            var rest = motion - dir * travel;
            motion = rest - hit.Normal * Vector3.Dot(rest, hit.Normal);
        }

        return met;
    }

    // ------------------------------------------------------------------ fire

    /// <summary>Nothing of the level between the robot's eye and the soldier, nor between the muzzle and the aim.</summary>
    private bool ClearShot(EnemyPart part, Vector3 muzzle, Vector3 aim, Vector3 player)
    {
        if (part.IsTurret && !LineOfSight(player))
        {
            return false;
        }

        return !Cast(muzzle, aim, out _);
    }
}
