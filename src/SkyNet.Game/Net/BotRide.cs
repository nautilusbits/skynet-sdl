using System.Numerics;
using RetroEng.Loop;
using RetroEng.Physics;
using SkyNet.Game.Navigation;
using SkyNet.Game.Player;
using SkyNet.Game.Vehicles;
using SkyNet.Game.Weapons;

namespace SkyNet.Game.Net;

/// <summary>What bots need to drive on an arena: the jeep's nav mesh (null: no jeep parked, or
/// straight at the goal) and the HK's ceiling (the map's marker 3; +∞ none).</summary>
internal sealed record BotRides(NavMesh? Jeep, float Ceiling)
{
    public static readonly BotRides None = new(null, float.PositiveInfinity);
}

/// <summary>How a ride goes at its goal.</summary>
internal enum RideStyle
{
    /// <summary>Gets there and stops (a roam goal, a last seen place, an orbit point).</summary>
    Arrive,
    /// <summary>Full speed into it (the jeep's ram).</summary>
    Ram,
    /// <summary>Down to the ground and still (the HK before its driver climbs out).</summary>
    Land,
}

/// <summary>
/// A bot's jeep or HK: the campaign's <see cref="VehicleController"/> on the bot's body, fed a
/// stick and keys as a human drives it; the jeep's turret turned as a human's mouse turns it.
/// <code>
///   jeep: path (jeep mesh) ─► pursuit point ─► wheel ∝ heading error
///                               └► speed cap: turn radius, next corner, goal ─► throttle / brake
///         no progress for 1 s ─► back up 1 s, wheel the other way (3 in 12 s: StuckLong)
///   HK:   goal ─► level speed;  highest ground under and ahead + Cruise ─► height
///         blocked ahead ─► climb and slow, at the ceiling sideways;  speeds ─► stick, sidestep, climb
/// </code>
/// No clock of its own: <see cref="Drive"/> runs one tick.
/// </summary>
internal sealed class BotRide
{
    /// <summary>The keys a bot driver holds: RUN (the HK's fast sidestep), climb, sink.</summary>
    private static readonly PlayerActions Keys = new(Forward: -1, Back: -1, Left: -1, Right: -1, Run: 0, Jump: 1, Crouch: 2);

    // ---- jeep ----
    /// <summary>The pursuit point runs this far ahead on the path: speed × LookTime within the bounds.</summary>
    private const float LookMin = 100f, LookMax = 450f, LookTime = 0.5f;
    /// <summary>A corner turning more than this (rad) slows the car and holds the pursuit point.</summary>
    private const float SharpTurn = 0.35f;
    /// <summary>Wheel per radian of heading error.</summary>
    private const float SteerGain = 2.5f;
    /// <summary>A corner this near is passed; the goal this near is reached.</summary>
    private const float CornerReached = 150f, Arrived = 150f;
    /// <summary>The goal moved this far, the car this far off its leg: a new path.</summary>
    private const float RepathMoved = 200f, OffPath = 250f, RepathEvery = 1f;
    /// <summary>The slowest a car goes that still has to turn (the wheel bites over 21.45 u/s).</summary>
    private const float CrawlSpeed = 90f;
    /// <summary>A heading error over BackErr backs the car round until under DriveErr (rad).</summary>
    private const float BackErr = 2.2f, DriveErr = 1f;
    /// <summary>A back spell: at most BackMax, a stall once past BackStall under StallSpeed backwards; the next waits BackWait (s).</summary>
    private const float BackMax = 4f, BackStall = 0.5f, StallSpeed = 30f, BackWait = 1.5f;
    /// <summary>Brakes used for the planned stops: a share of the jeep's 459, for margin.</summary>
    private const float PlanBrake = VehicleController.JeepBrake * 0.7f;
    /// <summary>Throttle when under the cap by this, brake when over by this.</summary>
    private const float SpeedSlackUp = 15f, SpeedSlackDown = 40f;
    /// <summary>The feelers: reach (speed × FeelTime within the bounds), the side ones' angle (rad),
    /// how hard a blocked one swerves, how high the swept sphere rides; a face steeper than
    /// ClimbNormalY's (35°, the mesh's slope) blocks.</summary>
    private const float FeelTime = 0.6f, FeelMin = 120f, FeelMax = 400f, FeelerAngle = 0.6f, AvoidGain = 2f, FeelLift = 10f;
    private const float ClimbNormalY = 0.82f;
    /// <summary>Under this progress in a check window it is stuck: back up.</summary>
    private const float StuckWindow = 1f, StuckMoved = 40f, ReverseFor = 1f;
    /// <summary>This many stuck spells within the window: stuck for long (the brain gets out).</summary>
    private const int StuckLongCount = 3;
    private const float StuckLongWindow = 12f;

    // ---- HK ----
    /// <summary>Height of the hull's bottom over the highest ground under and ahead.</summary>
    private const float Cruise = 300f;
    /// <summary>The ground is read here ahead along the flight (and right under).</summary>
    private static readonly float[] GroundAhead = [0f, 400f, 900f];
    /// <summary>Ground rays start this far over the hull (a ridge ahead taller than the HK).</summary>
    private const float GroundFrom = 900f, GroundReach = 6000f;
    /// <summary>Keeps the eye this far under the ceiling.</summary>
    private const float CeilingMargin = 40f;
    /// <summary>Vertical speed per unit of height error, and its bounds.</summary>
    private const float HeightGain = 2f, SinkMax = 350f, RiseMax = 500f;
    /// <summary>The sweep ahead: this much plus a second of flight, the hull's sphere a little wider.</summary>
    private const float SweepBase = 250f, SweepWiden = 1.1f;
    /// <summary>The pace under a ceiling too low to cruise; a touch faster than CrashSpeed is a crash.</summary>
    private const float LowCeilingPace = 0.5f, CrashSpeed = 150f;
    /// <summary>A climb over something ahead aims this far over the current height.</summary>
    private const float ClimbOver = 400f;
    /// <summary>Climb keys go down past this share of the climb speed.</summary>
    private const float KeyDeadband = 0.3f;
    /// <summary>A gap this small under the hull is landed.</summary>
    private const float LandedGap = 30f;
    /// <summary>The HK's sideways tries when the way ahead is shut (radians off the heading).</summary>
    private static readonly float[] Detours = [1.0f, -1.0f, 1.8f, -1.8f];

    // ---- guns (VehicleGuns' pool 10) ----
    private const float EnergyStart = 1000f;

    private readonly CollisionWorld _world;
    private readonly WeaponTable _table;
    private readonly NavQuery? _nav;
    private readonly float _ceiling;
    private readonly float _turretRate;
    private readonly List<Vector3> _corners = [];
    private readonly List<float> _stuckAt = [];
    private readonly List<Vector3> _points = [];
    private Vector3? _pathTo;
    private int _corner;
    private float _repathT, _clock;
    private float _checkT, _reverseT, _reverseWheel;
    private Vector3 _checkAt;
    private float _str;
    private bool _touching, _backing;
    private float _backT, _backWait, _backedAt = float.NegativeInfinity;
    private float _energy = EnergyStart, _sinceShot = 10f;
    private bool _overheated;

    /// <param name="turretRate">How fast the turret (the HK's nose) swings to the aim (rad/s).</param>
    public BotRide(PlayerController body, CollisionWorld world, WeaponTable table, BotRides rides, float turretRate)
    {
        _world = world;
        _table = table;
        _nav = rides.Jeep != null ? new NavQuery(rides.Jeep) : null;
        _ceiling = rides.Ceiling;
        _turretRate = turretRate;
        Controller = new VehicleController(world, body)
        {
            Ceiling = rides.Ceiling,
            Sound = id => Bounces += id == VehicleController.SoundCrash ? 1 : 0, // a wall bounce or a ram
        };
    }

    public VehicleController Controller { get; }
    public VehicleMode Mode => Controller.Mode;
    public bool Riding => Controller.Mode != VehicleMode.Foot;
    /// <summary>Seconds driven, stuck spells, the jeep's bounces (off walls, rams), the HK's crashes (touches of the level).</summary>
    public float Seconds { get; private set; }
    public int Stuck { get; private set; }
    public int Bounces { get; private set; }
    public int Crashes { get; private set; }
    /// <summary>The goal of the last <see cref="Drive"/> is reached (within <see cref="Arrived"/>).</summary>
    public bool AtGoal { get; private set; }
    /// <summary>Stuck often lately: the vehicle cannot get on.</summary>
    public bool StuckLong => _stuckAt.Count(t => _clock - t <= StuckLongWindow) >= StuckLongCount;
    /// <summary>The HK's hull rests on something.</summary>
    public bool Landed => Riding && _world.Raycast(Controller.Body.Position + new Vector3(0f, 5f, 0f), -Vector3.UnitY, LandedGap + 5f, out _);
    /// <summary>The jeep's path corners still ahead (none in the HK).</summary>
    public IEnumerable<Vector3> Path => _corners.Skip(_corner);
    /// <summary>What the jeep's feelers also keep clear of besides the level (feet and radius): the
    /// parked vehicles, the other bodies; the brain fills it every tick.</summary>
    public List<(Vector3 At, float Radius)> Obstacles { get; } = [];
    /// <summary>The ram (the jeep's; set by the brain, its targets the other players).</summary>
    public Ramming? Ram
    {
        get => Controller.Ram;
        set => Controller.Ram = value;
    }

    /// <summary>Into <paramref name="mode"/> where the body stands, facing <paramref name="yaw"/>
    /// (the parked vehicle's heading): a fresh seat, the guns charged.</summary>
    public void Enter(VehicleMode mode, float yaw)
    {
        Controller.Body.Yaw = yaw;
        Controller.Enter(mode);
        Controller.Ram?.Reset();
        _corners.Clear();
        _pathTo = null;
        _stuckAt.Clear();
        _reverseT = _checkT = 0f;
        _backing = false;
        _backT = _backWait = 0f;
        _checkAt = Controller.Body.Position;
        _energy = EnergyStart;
        _overheated = false;
        _touching = false;
    }

    /// <summary>The jeep's mesh is within <paramref name="reach"/> of <paramref name="at"/> (no mesh: anywhere).</summary>
    public bool OnMesh(Vector3 at, Vector3 reach) => _nav == null || _nav.Nearest(at, reach, out _);

    /// <summary>Back on foot (the body keeps its place).</summary>
    public void Leave()
    {
        Controller.Enter(VehicleMode.Foot);
        _corners.Clear();
        _pathTo = null;
    }

    // ---- guns ----

    /// <summary>The vehicle's gun is ready for <paramref name="record"/> (the energy pool has its
    /// cost, the gun not overheated); spends it when so. Records off pool 10 (rockets) always.</summary>
    public bool Spend(WeaponRecord record)
    {
        if (record.Pool != VehicleGuns.EnergyPool)
        {
            return true;
        }

        if (_overheated || _energy < record.Cost)
        {
            _overheated = true;
            return false;
        }

        _energy -= record.Cost;
        _sinceShot = 0f;
        return true;
    }

    /// <summary>VehicleGuns.Tick's recharge: 450 a second once the trigger rested 0.35 s.</summary>
    private void Recharge(float dt)
    {
        _sinceShot += dt;
        if (_sinceShot < VehicleGuns.RegenDelay)
        {
            return;
        }

        _energy = MathF.Min(_table.Pools[VehicleGuns.EnergyPool].Max, _energy + VehicleGuns.RegenPerSecond * dt);
        if (_overheated && _energy >= VehicleGuns.CoolLevel)
        {
            _overheated = false;
        }
    }

    // ---- one tick ----

    /// <summary>One tick towards <paramref name="goal"/> (null: hold still) aiming at
    /// <paramref name="aim"/> (null: ahead).</summary>
    public void Drive(float dt, Vector3? goal, Vector3? aim, RideStyle style)
    {
        _clock += dt;
        Seconds += dt;
        Recharge(dt);
        if (Mode == VehicleMode.Jeep)
        {
            Jeep(dt, goal, aim, style);
            return;
        }

        Hk(dt, goal, aim, style);
    }

    // ---- jeep ----

    private void Jeep(float dt, Vector3? goal, Vector3? aim, RideStyle style)
    {
        var car = Controller;
        var p = car.Body.Position;
        Turret(dt, aim);
        AtGoal = goal is { } g0 && Flat(g0 - p) < Arrived;
        if (goal is not { } g || (AtGoal && style != RideStyle.Ram))
        {
            Step(dt, Throttle(0f), 0f, 0UL);
            return;
        }

        // Backing out of a jam: the wheel the other way, a second of reverse.
        if (_reverseT > 0f)
        {
            _reverseT -= dt;
            Step(dt, -1f, _reverseWheel, 0UL);
            return;
        }

        var to = Pursuit(g, dt, out var cornerDist, out var cornerTurn, out var left);
        if (left < Arrived && style != RideStyle.Ram)
        {
            AtGoal = true; // a partial path's end: as near as the mesh goes
            Step(dt, Throttle(0f), 0f, 0UL);
            return;
        }

        var err = Wrap(Yaw(to - p) - car.CarYaw);
        _str = Math.Clamp(-err * SteerGain, -1f, 1f);

        // The way lies behind: back round (a backing car's nose swings the other way) until it
        // lies ahead enough to drive, the rear meets something, the car stalls or BackMax passes;
        // then a while forward on full lock before backing again.
        _backWait -= dt;
        if (!_backing && _backWait <= 0f && MathF.Abs(err) > BackErr && Feel(car.CarYaw + MathF.PI, FeelMin) >= 1f)
        {
            _backing = true;
            _backT = 0f;
        }

        if (_backing)
        {
            _backT += dt;
            _backedAt = _clock;
            var stalled = _backT > BackStall && car.Speed > -StallSpeed;
            var blocked = Feel(car.CarYaw + MathF.PI, FeelMin) < 1f;
            if (MathF.Abs(err) < DriveErr || stalled || blocked || _backT > BackMax)
            {
                _backing = false;
                _backWait = BackWait;
            }

            Step(dt, -1f, err >= 0f ? 1f : -1f, 0UL);
            Progress(dt, err);
            return;
        }

        // The speed cap: the circle that reaches the pursuit point, the next corner, the goal.
        var cap = VehicleController.JeepMaxSpeed;
        var look = Flat(to - p);
        var sin = MathF.Abs(MathF.Sin(err));
        if (sin > 1e-3f)
        {
            cap = MathF.Min(cap, SpeedFor(look / (2f * sin)));
        }

        if (MathF.Abs(err) > MathF.PI / 2f)
        {
            cap = MathF.Min(cap, CrawlSpeed);
        }

        if (cornerTurn > SharpTurn)
        {
            var corner = MathF.Max(CrawlSpeed, VehicleController.JeepMaxSpeed * (1f - cornerTurn / MathF.PI) * 0.6f);
            cap = MathF.Min(cap, MathF.Sqrt(corner * corner + 2f * PlanBrake * cornerDist));
        }

        if (style == RideStyle.Arrive)
        {
            cap = MathF.Min(cap, MathF.Max(CrawlSpeed, MathF.Sqrt(2f * PlanBrake * left)));
        }

        // Feelers: what the mesh does not know (a parked vehicle, a prop it steps over) or a corner
        // cut too close; the car swerves to the freer side and slows to stop short of it.
        var reach = Math.Clamp(MathF.Abs(car.Speed) * FeelTime, FeelMin, FeelMax);
        var free = Feel(car.CarYaw, reach);
        if (free < 1f)
        {
            var leftFree = Feel(car.CarYaw + FeelerAngle, reach);
            var rightFree = Feel(car.CarYaw - FeelerAngle, reach);
            var swerve = leftFree >= rightFree ? -1f : 1f; // a positive wheel turns right
            _str = Math.Clamp(_str + swerve * (1f - free) * AvoidGain, -1f, 1f);
            cap = MathF.Min(cap, MathF.Max(CrawlSpeed, MathF.Sqrt(2f * PlanBrake * free * reach)));
        }

        Step(dt, Throttle(cap), _str, 0UL);
        Progress(dt, err);
    }

    /// <summary>How far along heading <paramref name="yaw"/> the hull's sphere goes before it meets
    /// something it cannot drive up, or one of the <see cref="Obstacles"/> (a share of
    /// <paramref name="reach"/>; 1: clear). The sphere rides a little high, so the ground under it
    /// is no contact.</summary>
    private float Feel(float yaw, float reach)
    {
        var p = Controller.Body.Position;
        var dir = Ahead(yaw);
        var free = 1f;
        var centre = p + new Vector3(0f, VehicleController.JeepRadius + FeelLift, 0f);
        if (_world.SweepSphere(centre, VehicleController.JeepRadius, dir * reach, out var hit) && hit.Normal.Y < ClimbNormalY)
        {
            free = hit.Time;
        }

        // The circles: where the line, widened by the hull, first meets one (one already touched is passed).
        var from = new Vector2(p.X, p.Z);
        var d = new Vector2(dir.X, dir.Z);
        foreach (var (at, radius) in Obstacles)
        {
            var r = radius + VehicleController.JeepRadius;
            var oc = from - new Vector2(at.X, at.Z);
            var b = Vector2.Dot(oc, d);
            var c = oc.LengthSquared() - r * r;
            var disc = b * b - c;
            if (c <= 0f || b >= 0f || disc < 0f)
            {
                continue;
            }

            free = MathF.Min(free, (-b - MathF.Sqrt(disc)) / reach);
        }

        return free;
    }

    /// <summary>The top speed whose tightest circle is <paramref name="radius"/>: the wheel turns
    /// at most 1.074 rad/s, and 721.6 / v above 672 u/s.</summary>
    private static float SpeedFor(float radius)
    {
        var slow = VehicleController.JeepTurnMax * radius;
        var turnSlow = VehicleController.JeepTurnK / VehicleController.JeepTurnMax;
        var v = slow < turnSlow ? slow : MathF.Sqrt(VehicleController.JeepTurnK * radius);
        return MathF.Max(CrawlSpeed, v * 0.9f);
    }

    /// <summary>The stick for <paramref name="cap"/>: throttle under it, brake well over it, else coast.</summary>
    private float Throttle(float cap)
    {
        var v = Controller.Speed;
        if (v < cap - SpeedSlackUp)
        {
            return 1f;
        }

        return v > cap + SpeedSlackDown && v > 0f ? -1f : 0f;
    }

    /// <summary>The turret (the view) swings to <paramref name="aim"/> at the turret rate; with
    /// nothing to aim at, back over the bonnet.</summary>
    private void Turret(float dt, Vector3? aim)
    {
        var car = Controller;
        var wantYaw = 0f;
        var wantPitch = 0f;
        if (aim is { } a)
        {
            var d = a - car.Body.EyePosition;
            wantYaw = Wrap(Yaw(d) - car.CarYaw);
            wantPitch = MathF.Atan2(d.Y, Flat(d)) - car.TiltPitch;
        }

        var step = _turretRate * dt;
        car.AimYaw = Wrap(car.AimYaw + Math.Clamp(Wrap(wantYaw - car.AimYaw), -step, step));
        car.AimPitch = Math.Clamp(car.AimPitch + Math.Clamp(wantPitch - car.AimPitch, -step, step),
            -VehicleController.JeepAimPitch, VehicleController.JeepAimPitch);
    }

    /// <summary>The pursuit point: <see cref="LookMin"/>-<see cref="LookMax"/> along the path to
    /// <paramref name="goal"/>; also the distance to and the turn at the next corner, and the path left.</summary>
    private Vector3 Pursuit(Vector3 goal, float dt, out float cornerDist, out float cornerTurn, out float left)
    {
        var p = Controller.Body.Position;
        Plan(goal, dt);
        var points = _points;
        points.Clear();
        points.Add(p);
        points.AddRange(_corners.Skip(_corner));
        if (_nav == null || _corners.Count == 0)
        {
            points.Add(goal); // no mesh, or none under the car: straight at it
        }

        cornerDist = points.Count > 2 ? Flat(points[1] - p) : float.MaxValue;
        cornerTurn = points.Count > 2 ? Turn(points[1] - p, points[2] - points[1]) : 0f;
        left = 0f;
        for (var i = 1; i < points.Count; i++)
        {
            left += Flat(points[i] - points[i - 1]);
        }

        // No further than a sharp corner ahead (not across its inside), unless it is near.
        var look = Math.Clamp(MathF.Abs(Controller.Speed) * LookTime, LookMin, LookMax);
        if (cornerTurn > SharpTurn)
        {
            look = MathF.Min(look, MathF.Max(LookMin, cornerDist));
        }

        for (var i = 1; i < points.Count; i++)
        {
            var leg = Flat(points[i] - points[i - 1]);
            if (leg >= look)
            {
                return Vector3.Lerp(points[i - 1], points[i], look / MathF.Max(leg, 1f));
            }

            look -= leg;
        }

        return points[^1];
    }

    /// <summary>A path to <paramref name="goal"/> on the jeep's mesh when the goal moved, the car
    /// strayed or <see cref="RepathEvery"/> passed; the corners passed are skipped.</summary>
    private void Plan(Vector3 goal, float dt)
    {
        var p = Controller.Body.Position;
        _repathT -= dt;
        if (_nav != null && (_pathTo is not { } old || Flat(old - goal) > RepathMoved || _repathT <= 0f || OffLeg(p)))
        {
            _nav.FindPath(p, goal, _corners);
            _pathTo = goal;
            _corner = _corners.Count > 0 ? 1 : 0; // the first is the start
            _repathT = RepathEvery;
        }

        while (_corner < _corners.Count && Flat(_corners[_corner] - p) < CornerReached)
        {
            _corner++;
        }
    }

    private bool OffLeg(Vector3 p)
    {
        if (_corner == 0 || _corner >= _corners.Count)
        {
            return false;
        }

        var a = _corners[_corner - 1];
        var b = _corners[_corner];
        var ab = new Vector2(b.X - a.X, b.Z - a.Z);
        var ap = new Vector2(p.X - a.X, p.Z - a.Z);
        var t = ab.LengthSquared() > 1f ? Math.Clamp(Vector2.Dot(ap, ab) / ab.LengthSquared(), 0f, 1f) : 0f;
        return (ap - ab * t).Length() > OffPath;
    }

    /// <summary>No progress for <see cref="StuckWindow"/> while it means to move: a spell of reverse.</summary>
    private void Progress(float dt, float err)
    {
        var p = Controller.Body.Position;
        _checkT += dt;
        if (_checkT < StuckWindow)
        {
            return;
        }

        _checkT = 0f;
        var moved = Flat(p - _checkAt);
        _checkAt = p;
        // Moving, or a back spell in the window (a turn, not a jam).
        if (moved >= StuckMoved || _clock - _backedAt < StuckWindow)
        {
            return;
        }

        // Backing up with the wheel the way that swings the nose to the heading wanted
        // (a backing car turns the other way).
        Stuck++;
        _stuckAt.Add(_clock);
        _stuckAt.RemoveAll(t => _clock - t > StuckLongWindow);
        _reverseT = ReverseFor;
        _reverseWheel = err >= 0f ? 1f : -1f;
        _repathT = 0f;
    }

    // ---- HK ----

    private void Hk(float dt, Vector3? goal, Vector3? aim, RideStyle style)
    {
        var body = Controller.Body;
        var p = body.Position;

        // The level speed wanted: at the goal, slowing in to stop on it.
        var want = Vector3.Zero;
        AtGoal = goal is { } g0 && Flat(g0 - p) < Arrived;
        if (goal is { } g && style != RideStyle.Land)
        {
            var to = g - p;
            to.Y = 0f;
            var d = to.Length();
            var speed = MathF.Min(VehicleController.HkSpeed, MathF.Sqrt(2f * VehicleController.HkAccel * 0.5f * d));
            want = d > 1f ? to / d * speed : Vector3.Zero;
        }

        // The height: over the highest ground under and ahead; climbing over what blocks the way,
        // and going round it at the ceiling.
        var heading = want.LengthSquared() > 1f ? Vector3.Normalize(want) : Ahead(body.Yaw);
        var targetY = style == RideStyle.Land ? Ground(p) : Ground(p, heading) + Cruise;
        var cap = _ceiling - VehicleController.HkEye - CeilingMargin;
        if (style != RideStyle.Land && want.LengthSquared() > 1f && Blocked(p, want))
        {
            targetY = MathF.Max(targetY, p.Y + ClimbOver);
            want *= 0.25f;
            if (cap - p.Y < ClimbOver)
            {
                want = Detour(p, want);
            }
        }

        // Under a low ceiling (no room for Cruise) slower: no climbing over what comes.
        if (cap - Ground(p, heading) < Cruise)
        {
            want *= LowCeilingPace;
        }

        targetY = MathF.Min(targetY, cap);
        var vy = Math.Clamp((targetY - p.Y) * HeightGain, -SinkMax, RiseMax);

        // The nose: at the aim, else along the way; pitched at the aim or along the climb.
        var face = aim is { } a ? Yaw(a - body.EyePosition) : want.LengthSquared() > 100f ? Yaw(want) : body.Yaw;
        var pitch = aim is { } b
            ? MathF.Atan2((b - body.EyePosition).Y, Flat(b - body.EyePosition))
            : MathF.Atan2(vy, want.Length() + 1f);
        var step = _turretRate * dt;
        body.Yaw = Wrap(body.Yaw + Math.Clamp(Wrap(face - body.Yaw), -step, step));
        body.Pitch = Math.Clamp(pitch, -VehicleController.HkPitchMax, VehicleController.HkPitchMax);

        var (fwd, str, vert) = Mix(want, vy);
        var pace = body.Velocity.Length();
        var keys = TickInput.Bit(Keys.Run) | (vert > 0 ? TickInput.Bit(Keys.Jump) : 0UL) | (vert < 0 ? TickInput.Bit(Keys.Crouch) : 0UL);
        Step(dt, fwd, str, keys);

        // A touch of the level at speed that was not a landing is a crash.
        var touching = Controller.OnWall && style != RideStyle.Land && pace > CrashSpeed;
        if (touching && !_touching)
        {
            Crashes++;
        }

        _touching = touching;
    }

    /// <summary>The stick and the climb keys for level speed <paramref name="want"/> and climb
    /// <paramref name="vy"/>: thrust along the nose (its pitch lifts or sinks it), the sidestep across,
    /// the climb keys for what the nose leaves (a key held past <see cref="KeyDeadband"/> of it).</summary>
    private (float Fwd, float Str, int Vert) Mix(Vector3 want, float vy)
    {
        var body = Controller.Body;
        var look = PlayerController.Direction(body.Yaw, body.Pitch);
        var ahead = Ahead(body.Yaw);
        var right = new Vector3(MathF.Cos(body.Yaw), 0f, -MathF.Sin(body.Yaw));
        var cos = MathF.Max(new Vector2(look.X, look.Z).Length(), 0.1f);
        var along = Vector3.Dot(want, ahead);
        var thrust = along >= 0f ? VehicleController.HkSpeed : VehicleController.HkBackSpeed;
        var fwd = Math.Clamp(along / (thrust * cos), -1f, 1f);
        var lift = fwd * thrust * look.Y;

        // The height first: thrust that lifts or sinks more than the keys make up is cut.
        if (MathF.Abs(look.Y) > 1e-3f && MathF.Abs(vy - lift) > VehicleController.HkClimb)
        {
            var room = vy - MathF.Sign(vy - lift) * VehicleController.HkClimb;
            fwd = Math.Clamp(room / (thrust * look.Y), MathF.Min(0f, fwd), MathF.Max(0f, fwd));
            lift = fwd * thrust * look.Y;
        }

        var str = Math.Clamp(Vector3.Dot(want, right) / VehicleController.HkStrafeRun, -1f, 1f);
        var need = (vy - lift) / VehicleController.HkClimb;
        var vert = need > KeyDeadband ? 1 : need < -KeyDeadband ? -1 : 0;
        return (fwd, str, vert);
    }

    /// <summary>The highest ground under <paramref name="p"/> and at the spots ahead along
    /// <paramref name="heading"/>.</summary>
    private float Ground(Vector3 p, Vector3 heading)
    {
        var top = float.MinValue;
        foreach (var k in GroundAhead)
        {
            top = MathF.Max(top, Ground(p + heading * k));
        }

        return top;
    }

    /// <summary>The first surface under a point over <paramref name="p"/>; its own height when none.</summary>
    private float Ground(Vector3 p)
    {
        var from = p + new Vector3(0f, GroundFrom, 0f);
        return _world.Raycast(from, -Vector3.UnitY, GroundReach, out var hit) ? from.Y - hit.Time : p.Y - Cruise;
    }

    /// <summary>The hull's sphere would hit the level within a second's flight plus <see cref="SweepBase"/>.</summary>
    private bool Blocked(Vector3 p, Vector3 want)
    {
        var r = VehicleController.HkRadius;
        var dir = Vector3.Normalize(want);
        var reach = SweepBase + want.Length();
        return _world.SweepSphere(p + new Vector3(0f, r, 0f), r * SweepWiden, dir * reach, out _);
    }

    /// <summary>The first free way off the heading (<see cref="Detours"/>), at half speed; none: hover.</summary>
    private Vector3 Detour(Vector3 p, Vector3 want)
    {
        var speed = MathF.Max(want.Length(), VehicleController.HkStrafe * 0.5f);
        var yaw = Yaw(want);
        foreach (var off in Detours)
        {
            var d = Ahead(yaw + off) * speed;
            if (!Blocked(p, d))
            {
                return d;
            }
        }

        return Vector3.Zero;
    }

    // ---- shared ----

    /// <summary>The controller's tick with the stick at (<paramref name="str"/>, <paramref name="fwd"/>).</summary>
    private void Step(float dt, float fwd, float str, ulong keys) =>
        Controller.Body.Step(new TickInput(keys, 0, 0, 0f, 0f, 0f, str, -fwd), dt, Keys);

    /// <summary>The heading turn (0..π) from leg <paramref name="a"/> to leg <paramref name="b"/>.</summary>
    private static float Turn(Vector3 a, Vector3 b)
    {
        var u = new Vector2(a.X, a.Z);
        var v = new Vector2(b.X, b.Z);
        if (u.LengthSquared() < 1f || v.LengthSquared() < 1f)
        {
            return 0f;
        }

        return MathF.Acos(Math.Clamp(Vector2.Dot(Vector2.Normalize(u), Vector2.Normalize(v)), -1f, 1f));
    }

    private static Vector3 Ahead(float yaw) => new(-MathF.Sin(yaw), 0f, -MathF.Cos(yaw));

    private static float Yaw(Vector3 d) => MathF.Atan2(-d.X, -d.Z);

    private static float Flat(Vector3 v) => new Vector2(v.X, v.Z).Length();

    private static float Wrap(float a)
    {
        a %= MathF.Tau;
        return a > MathF.PI ? a - MathF.Tau : a < -MathF.PI ? a + MathF.Tau : a;
    }
}
