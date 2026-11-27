using System.Numerics;
using RetroEng.Loop;
using RetroEng.Physics;
using SkyNet.Game.Navigation;

namespace SkyNet.Game.Enemies;

/// <summary>
/// ROBOT AI IMPROVED (<see cref="RobotAi.Improved"/>). DOS's states, scripts, attacks, weapons and
/// damage stay; only how a robot moves and when it fires change:
/// <code>
///   flyers   swept body (never inside the level) + look-ahead: turn or climb round props;
///            hovers keep apart from other hovers
///   walkers  probe ahead (middle and shoulders) → follow the obstacle round; no progress for 1 s →
///            back off;
///            keep apart from other robots
///   all      see in a cone, hear shots and blasts, alert states (Enemy.Alert.cs): patrol, investigate,
///            search round what it knows; side-step when DOS would walk in place next to him; fire
///            only along a clear line
/// </code>
/// CLASSIC never reaches this code, so its runs stay DOS's (DosParityTests).
/// </summary>
public sealed partial class Enemy
{
    /// <summary>How long a sighting is remembered (s).</summary>
    private const float MemoryTime = 20f;
    /// <summary>A spot is reached within this (flat units).</summary>
    private const float Arrive = 60f;
    /// <summary>Following an obstacle: headings are probed this far ahead; it is given up after this long (s).</summary>
    private const float FollowProbe = 80f, FollowMax = 8f;
    /// <summary>Headings a patrol leg tries for a clear way.</summary>
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
    /// <summary>A homing mine's: the climb first.</summary>
    private static readonly float[] MineAvoidAngles = [0f, 35f, 70f, 110f, 150f];
    /// <summary>The walker's probes across: both shoulders, then the middle.</summary>
    private static readonly float[] Probes = [1f, -1f, 0f];
    /// <summary>The flyer's turns: the goal's side first, then the other.</summary>
    private static readonly int[] Turns = [1, -1];
    /// <summary>The flyer's floor samples ahead, as shares of its reach.</summary>
    private static readonly float[] FloorAhead = [0.34f, 0.67f, 1f];
    /// <summary>A climb goes this many body heights up.</summary>
    private const float ClimbHeights = 1.5f;
    /// <summary>A climb lower than this is not tried (units).</summary>
    private const float MinRise = 8f;
    /// <summary>The flyer's hull: sphere radius bounds, the gap kept from surfaces.</summary>
    private const float HullMin = 8f, HullMax = 150f, Skin = 2f;
    private const int SlideIterations = 3, DepenetrateRounds = 4;
    /// <summary>Following re-probes its heading and the way out every this many ticks (11 rays per
    /// heading tried); the heading is kept between.</summary>
    private const int FollowEvery = 4;
    /// <summary>A clear step probe holds for this many more ticks while the walker has moved less than
    /// <see cref="ClearSlack"/> of the probe's 40-unit margin and turned less than acos(<see cref="ClearCos"/>)
    /// (a raptor at 74 u/s: one probe per 4 ticks, 5 u).</summary>
    private const int ClearReuse = 3;
    private const float ClearSlack = 20f, ClearCos = 0.999f;
    /// <summary>Re-aim the hull's spheres once the yaw moved this much (radians).</summary>
    private const float HullYawStep = 0.05f;

    private Vector3 _halfSize = new(30f);
    /// <summary>IMPROVED's draws: this robot's own stream, never the shared <see cref="Robots.Rng"/>.</summary>
    private readonly SimRandom _improvedRng;

    /// <summary>The own stream's state, for the session hash (IMPROVED only).</summary>
    internal ulong ImprovedRngState => _improvedRng.State;

    private Vector3? _lastSeen;
    private float _memoryT;
    private bool _searching;
    private Vector3 _searchSpot;
    private float _searchT;
    private bool _following;
    private float _followT;
    private int _followSide = 1;
    private float _metAt;
    private float _followYaw;
    private int _followProbe;
    private int _clearTicks;
    private Vector3 _clearDir;
    private Vector3 _clearFrom;
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
    private List<Vector3>? _hullLocal;
    private SphereBody? _keptHull;
    private float _hullRadius;

    /// <summary>How a walker's step is steered.</summary>
    private enum WalkMode
    {
        /// <summary>To a goal: nav path, obstacle following, backing off when stuck.</summary>
        Route,
        /// <summary>The soldier seen in weapon range, cut off on the mesh: face him and close straight in; a blocked step
        /// stands (and fires) instead of walking round (#23).</summary>
        Engage,
    }

    private bool Improved => _ctx.Ai == RobotAi.Improved;

    /// <summary>The robot's half width on the ground, for keeping apart.</summary>
    private float Spacing => MathF.Max(MathF.Max(_halfSize.X, _halfSize.Z), 20f);

    private static Vector3 Flat(Vector3 v) => v with { Y = 0f };

    private static Vector3 Heading(float yaw) => new(-MathF.Sin(yaw), 0f, -MathF.Cos(yaw));

    private static float YawOf(Vector3 flat) => MathF.Atan2(-flat.X, -flat.Z);

    private static float Rad(float degrees) => degrees * MathF.PI / 180f;

    // ------------------------------------------------------------------ memory

    /// <summary>What a save keeps of IMPROVED (<see cref="RobotSnapshot.Memory"/>).</summary>
    private RobotMemory SaveMemory() =>
        new(_lastSeen, _memoryT, _searching, _searchSpot, _searchT, _wanderTarget, _wanderT, _backOffT, _backOffYaw, _improvedRng.State)
        {
            Alert = _alertState, AlertT = _alertT, Post = _post, Spot = _spot, Combat = SaveCombat(),
            Search = new RobotSearch(_seenAt, _seenVel, _searchDir, _lead, _slot, _leadT),
        };

    /// <summary>A saved memory put back; none (an older save): nothing remembered, the stream kept.
    /// Called after the perception state is restored.</summary>
    private void LoadMemory(RobotMemory? memory)
    {
        var m = memory ?? new RobotMemory(null, 0f, false, default, 0f, default, 0f, 0f, 0f, _improvedRng.State);
        (_lastSeen, _memoryT, _searching, _searchSpot, _searchT) = (m.LastSeen, m.MemoryT, m.Searching, m.SearchSpot, m.SearchT);
        (_wanderTarget, _wanderT, _backOffT, _backOffYaw) = (m.WanderTarget, m.WanderT, m.BackOffT, m.BackOffYaw);
        _improvedRng.State = m.Rng;
        (_alertState, _alertT, _post, _spot) = (m.Alert, m.AlertT, m.Post ?? _post, m.Spot);
        LoadCombat(m.Combat);
        LoadSearch(m.Search);
        _pausing = false;

        // Not saved: a held robot holds where it stands, a search looks round again from here.
        (_holdAt, _face, _lookBase, _looks) = (Position, null, Yaw, 0);
        if (memory == null && _perceived)
        {
            _alertState = RobotAlert.Combat;   // an older save: a robot that saw him was fighting
        }
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
        var target = see ? player : Goal(dt);
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

        // Hurt: to cover. Without a token: a walker flanks him at range, a drone holds off.
        var waits = _cover == null && Waits(SightOf(see));
        if (_cover is { } hide)
        {
            target = hide;
            sideStep = true;
        }
        else if (waits && State == 7 && moving)
        {
            target = Flank(player, near);
            sideStep = true;
        }
        else
        {
            _flank = null;
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

        // The soldier seen in weapon range where the mesh has no way to him: no path, no walking
        // round; it turns to him so the script reaches its fire pose (picked by his bearing, #23).
        var cutOff = nav == NavStep.Hold || (nav == NavStep.Corner && _path == PathResult.Partial);
        var mode = see && !sideStep && State == 7 && cutOff && Root.Fire is { } gun && dist <= gun.Range ? WalkMode.Engage : WalkMode.Route;
        if (mode == WalkMode.Engage)
        {
            nav = NavStep.Straight;
            corner = target;
            left = dist;
        }

        _step = nav;
        if (nav == NavStep.Corner)
        {
            // The mesh vouches for the floor up to the corner: no drop probe past it (a catwalk's turn).
            to = Flat(corner - Position);
            clear = MathF.Min(LeaveProbe, to.Length());
            _dropLook = Math.Clamp(to.Length(), DropLookMin, DropLook);
        }

        var wantYaw = !see && _face is { } face ? face : to.Length() > 1f ? YawOf(to) : Yaw;
        if (State == 13 && Brain != null && Brain.HasVar(44))
        {
            wantYaw = Brain.Var(44, 0) / 2048f * MathF.Tau;
        }

        if (State == 13 && see && speed > 0f && dist <= MathF.Max(near, TankKeep))
        {
            speed = 0f;   // a tank keeps its range
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
            // A hover holds in place where it pauses or holds (DOS hovers stand off the trail); the HK
            // circles, a mine keeps drifting (it homes once it sees him).
            var holds = State == 6 && !IsKamikaze && ((!moving && (!see || _cover != null)) || (waits && dist <= HoverHold));
            Fly(dt, wantYaw, holds ? 0f : speed, see, player);
            return;
        }

        Walk(dt, wantYaw, left, clear, moving ? speed : 0f, mode);
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
    private void Walk(float dt, float goalYaw, float goalDist, float clear, float speed, WalkMode mode)
    {
        var wantYaw = goalYaw;
        var trying = MathF.Abs(speed) > 0.5f;
        var wants = trying;
        if (mode == WalkMode.Engage)
        {
            _following = false;
            _backOffT = 0f;
        }

        if (_backOffT > 0f)
        {
            _backOffT -= dt;
            wantYaw = _backOffYaw;
        }
        else if (_following && trying)
        {
            _followT += dt;
            var probe = --_followProbe <= 0;
            var leave = probe && goalDist < _metAt + LeaveSlack && !GroundBlocked(Heading(goalYaw), FollowProbe) && !LongBlocked(Heading(goalYaw), clear);
            if (leave || _followT > FollowMax)
            {
                _following = false;
                _followSide = leave ? _followSide : -_followSide;
            }
            else
            {
                // Hysteresis: a clear heading still being turned to is kept, not re-probed from the
                // half-made turn (that flipped it ±170° per probe, #23).
                var turning = MathF.Abs(Wrap(_followYaw - Yaw)) > FacingSlack;
                if (probe && !(turning && !GroundBlocked(Heading(_followYaw), FollowProbe)))
                {
                    _followYaw = FollowHeading();
                }

                if (probe)
                {
                    _followProbe = FollowEvery;
                }

                wantYaw = _followYaw;
            }
        }

        Yaw = Wrap(Approach(Yaw, wantYaw, TurnRate * dt));

        // At a nav path's corner, slow while turning (in place past a right angle): a catwalk's turn is
        // not overshot. Not while following or backing off: those headings are not the corner's.
        if (_step == NavStep.Corner && trying && !_following && _backOffT <= 0f)
        {
            speed *= MathF.Max(MathF.Cos(Wrap(wantYaw - Yaw)), 0f);
            trying = MathF.Abs(speed) > 0.5f;
        }

        _blocked = false;
        if (trying)
        {
            var dir = speed < 0f ? -Forward : Forward;
            if (StepBlocked(dir, MathF.Abs(speed) * dt))
            {
                _blocked = true;
                if (mode == WalkMode.Engage)
                {
                    // Standing its ground facing him is engaging, not being stuck.
                    wants = false;
                }
                else if (!_following && _backOffT <= 0f)
                {
                    _following = true;
                    _followProbe = 0;
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
        // Stuck counts the wish to move: a turn slowed to a standstill is checked too.
        Progress(dt, wants, MathF.Abs(Wrap(wantYaw - Yaw)) < FacingSlack, speed);
        SnapToGround(false);
    }

    /// <summary>The step's <see cref="GroundBlocked"/>, its level rays reused for a few ticks while
    /// the way was clear (robots are checked every tick).</summary>
    private bool StepBlocked(Vector3 dir, float length)
    {
        if (_clearTicks > 0 && Vector3.Dot(dir, _clearDir) > ClearCos && Flat(Position - _clearFrom).Length() < ClearSlack)
        {
            _clearTicks--;
            return RobotAhead(dir, length);
        }

        if (GroundBlocked(dir, length))
        {
            _clearTicks = 0;
            return true;
        }

        _clearTicks = ClearReuse;
        _clearDir = dir;
        _clearFrom = Position;
        return false;
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
        foreach (var k in Probes)
        {
            var s = side * k;
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
        var all = _ctx.All;
        for (var i = 0; i < all.Count; i++)
        {
            var o = all[i];
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

        var rng = _improvedRng;
        _backOffYaw = Wrap(Yaw + MathF.PI + (rng.NextSingle() * 2f - 1f) * MathF.PI * 0.5f);
        _backOffT = BackOffTime;
        _following = false;
        _followSide = -_followSide;
        _leadT = 0f;   // a search spot it cannot reach: it looks round from here
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
        var all = _ctx.All;
        for (var i = 0; i < all.Count; i++)
        {
            var o = all[i];
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
    /// turned with the yaw (re-aimed when the yaw moved).</summary>
    private SphereBody Hull()
    {
        if (_hull != null && MathF.Abs(Wrap(Yaw - _hullYaw)) < HullYawStep)
        {
            return _hull;
        }

        // The spheres about the body at yaw 0 are laid out once; a turn only re-aims them.
        if (_hullLocal == null)
        {
            HullLayout();
        }

        var turn = Matrix4x4.CreateRotationY(Yaw);
        var turned = new Vector3[_hullLocal!.Count];
        for (var i = 0; i < turned.Length; i++)
        {
            turned[i] = Vector3.Transform(_hullLocal[i], turn);
        }

        _hull = new SphereBody(_hullRadius, turned);
        _hullYaw = Yaw;
        return _hull;
    }

    /// <summary>The hull's sphere radius and centres about the body at yaw 0.</summary>
    private void HullLayout()
    {
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

        _hullLocal = offsets;
        _hullRadius = r;
    }

    /// <summary>The hull moved by <paramref name="motion"/> from <paramref name="from"/> meets the level.</summary>
    private bool HullHits(SphereBody hull, Vector3 from, Vector3 motion) =>
        motion.LengthSquared() > 1e-6f && _ctx.World.Sweep(hull, from, motion, out _);

    private void Fly(float dt, float wantYaw, float speed, bool see, Vector3 player)
    {
        // Out of the level: after a re-aim or a touch (the swept move keeps it out otherwise), and on
        // the look tick (a mover).
        var hull = Hull();
        var looks = _senseCount == 0;
        if (hull != _keptHull || _blocked || looks)
        {
            _keptHull = hull;
            Position += _ctx.World.Depenetrate(hull, Position, Skin, DepenetrateRounds);
        }

        // A mine within its hull and blast reach of the soldier closes in: the wall he stands at
        // neither turns it away nor holds its hull off (it circled ~87 u out, short of 65, #23).
        if (Homes(see) && Flat(player - Position).Length() <= _hullRadius + (Type?.Near ?? 40) + KamikazeReach)
        {
            _avoidT = 0f;
            _climbTo = null;
            _blocked = false;
            Yaw = Wrap(Approach(Yaw, wantYaw, TurnRate * dt));
            Position += MineWay(see, player - Position) * (speed * dt) + new Vector3(0f, Lift(dt, speed, see, player), 0f);
            return;
        }

        // Look ahead along the flight (backwards while backing off), every fourth tick as the robot
        // looks (staggered, <see cref="SenseCount"/>); a clear way keeps the course.
        var sign = speed < 0f ? -1f : 1f;
        var look = MathF.Max(MathF.Abs(speed) * FlyLookSeconds, FlyLookMin);
        if (Homes(see))
        {
            // A mine looks no further than the soldier: a wall behind him is no obstacle (#23).
            look = MathF.Min(look, Flat(player - Position).Length());
        }

        if (_avoidT > 0f)
        {
            _avoidT -= dt;
            wantYaw = _avoidYaw;
        }
        else if (looks && MathF.Abs(speed) > 0.5f && HullHits(hull, Position, Probe(SightOf(see), player) * (sign * look)))
        {
            Avoid(hull, wantYaw, sign, look, player);
            wantYaw = _avoidT > 0f ? _avoidYaw : wantYaw;
        }

        if (_avoidT <= 0f)
        {
            _climbTo = null;
        }

        Yaw = Wrap(Approach(Yaw, wantYaw, TurnRate * dt * (_avoidT > 0f ? AvoidTurnBoost : 1f)));
        hull = Hull();
        var way = _avoidT > 0f && !(Homes(see) && _climbTo != null) ? Forward : FlyWay(SightOf(see), player);
        var motion = way * (speed * dt) + new Vector3(0f, Lift(dt, speed, see, player), 0f) + Strafe(dt, SightOf(see));
        _blocked = Slide(hull, motion);
        FlyApart(dt, hull);
    }

    /// <summary>Where a flyer looks ahead: a homing mine or a hover going to cover along its way
    /// (it flies there, whatever its slow turn faces), others along their facing.</summary>
    private Vector3 Probe(Sight sight, Vector3 player) => Homes(sight == Sight.Seen) || Hiding ? FlyWay(sight, player) : Heading(Yaw);

    /// <summary>A hover (6) on its way to cover.</summary>
    private bool Hiding => State == 6 && _cover != null;

    /// <summary>A flyer's flat heading: a hover straight to its cover (as DOS flies a hover at its
    /// point, 0x137545), else <see cref="MineWay"/>.</summary>
    private Vector3 FlyWay(Sight sight, Vector3 player)
    {
        if (!Hiding)
        {
            return MineWay(sight == Sight.Seen, player - Position);
        }

        var to = Flat(_cover!.Value - Position);
        return to.LengthSquared() > 1f ? Vector3.Normalize(to) : Vector3.Zero;
    }

    /// <summary>The hover's keep-apart reach: half its largest side.</summary>
    private float FlyRoom => MathF.Max(BodySize * 0.5f, 20f);
    /// <summary>Kept beyond the touch, so the chase drawing them in does not close the gap (units).</summary>
    private const float FlySlack = 8f;

    /// <summary>A hover that keeps apart: not an HK (state 9), not a mine (it goes for the soldier).</summary>
    private bool Spreads => IsFlying && State != 9 && !IsKamikaze;

    /// <summary>Two hovers closer than their reaches push this one out sideways, up to its
    /// speed, sliding along the level. DOS lets them pass through each other (robots carry 0x20, the
    /// hover's slide masks it, 0x13b35e): a group on the soldier's trail stacked into one.</summary>
    private void FlyApart(float dt, SphereBody hull)
    {
        if (!Spreads)
        {
            return;
        }

        var push = Vector3.Zero;
        var all = _ctx.All;
        for (var i = 0; i < all.Count; i++)
        {
            var o = all[i];
            if (o == this || !o.Alive || !o.Spreads)
            {
                continue;
            }

            var room = FlyRoom + o.FlyRoom + FlySlack;
            if (Vector3.DistanceSquared(Centre, o.Centre) >= room * room)
            {
                continue;
            }

            // On one spot: a heading fixed by the pair's places in the list, opposite for each (no dice).
            var d = Flat(Centre - o.Centre);
            var len = d.Length();
            if (len < 1e-3f)
            {
                var me = 0;
                while (all[me] != this)
                {
                    me++;
                }

                var (lo, hi) = me < i ? (me, i) : (i, me);
                d = Heading(lo * 2.4f + hi * 1.1f) * (me < i ? 1f : -1f);
                len = 0f;
            }
            else
            {
                d /= len;
            }

            push += d * (room - len) * 0.5f;
        }

        var size = push.Length();
        if (size < 1e-3f)
        {
            return;
        }

        Slide(hull, push / size * MathF.Min(size, MathF.Max(MoveSpeed, 30f) * dt));
    }

    /// <summary>A clear way round what lies ahead: a turn toward the goal's side first, the climb
    /// over it, then wider turns either side; held for <see cref="AvoidTime"/>. A homing mine tries
    /// the climb first, along its way: turning off took it away from a low prop it could pass over.</summary>
    private void Avoid(SphereBody hull, float wantYaw, float sign, float look, Vector3 player)
    {
        var first = Wrap(wantYaw - Yaw) >= 0f ? 1 : -1;
        var rise = MathF.Max(BodyHeight, 40f) * ClimbHeights;
        var homes = Homes(Sees);
        var ahead = Probe(SightOf(Sees), player);
        if (homes)
        {
            rise = MathF.Min(rise, Headroom(rise));   // its eye kept under a ceiling: it would lose him
        }

        foreach (var angle in homes ? MineAvoidAngles : AvoidAngles)
        {
            if (angle == 0f)
            {
                var up = new Vector3(0f, rise, 0f);
                if (rise >= MinRise && !HullHits(hull, Position, up) && !HullHits(hull, Position + up, ahead * (sign * look)))
                {
                    _avoidYaw = Yaw;
                    _climbTo = Position.Y + rise;
                    _avoidT = AvoidTime;
                    return;
                }

                continue;
            }

            foreach (var turn in Turns)
            {
                var side = turn * first;
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

    /// <summary>How far it may rise (up to <paramref name="rise"/>) and keep its eye under a ceiling.</summary>
    private float Headroom(float rise)
    {
        var up = new Vector3(0f, 1f, 0f);
        const float gap = EyeHeight + Skin;
        return Cast(Position + up, Position + up * (rise + gap), out var roof) ? roof.Point.Y - gap - Position.Y : rise;
    }

    /// <summary>The vertical step: DOS's altitude rules (<see cref="Move"/>), raised to a climb's height.</summary>
    private float Lift(float dt, float speed, bool see, Vector3 player)
    {
        if (State == 9)
        {
            var minAlt = MathF.Max((Type?.Altitude ?? 384) - 100f, FlyerMinAlt);
            var reach = MathF.Max(Type?.AvoidRadius ?? 400, FlyerLookahead);
            var floor = SurfaceAt(Position);
            foreach (var f in FloorAhead)
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
            var dy = MathF.Max(player.Y + 40f, _climbTo ?? float.NegativeInfinity) - Position.Y;
            return Math.Clamp(dy, -speed * dt, speed * dt);
        }

        var need = MathF.Max(HoverNeed(), (_climbTo ?? float.NegativeInfinity) - Position.Y);
        return Math.Clamp(need, -FlyerSinkSpeed * dt, FlyerClimbSpeed * dt);
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

    /// <summary>Nothing of the level between the robot's eye and the soldier, nor between the bolt's start and the aim.</summary>
    private bool ClearShot(EnemyPart part, Vector3 start, Vector3 aim, Vector3 player)
    {
        if (part.IsTurret && !LineOfSight(player))
        {
            return false;
        }

        return !Cast(start, aim, out _);
    }
}
