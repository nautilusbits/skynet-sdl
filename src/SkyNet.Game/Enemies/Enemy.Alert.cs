using System.Numerics;
using SkyNet.Game.Navigation;

namespace SkyNet.Game.Enemies;

/// <summary>An IMPROVED robot's alertness (<see cref="RobotMemory.Alert"/>).</summary>
public enum RobotAlert
{
    /// <summary>Patrols round its post: short legs, pauses with a look round.</summary>
    Idle,
    /// <summary>Heard something or was alerted: turns to it and holds.</summary>
    Suspicious,
    /// <summary>Goes to the spot it heard or was alerted to.</summary>
    Investigate,
    /// <summary>Sees the soldier: DOS's script fights him.</summary>
    Combat,
    /// <summary>Lost him or found nothing: looks round the known spot, then gives up.</summary>
    Search,
    /// <summary>Gave up: back to its post, then idle.</summary>
    Return,
}

/// <summary>What a save keeps of an IMPROVED robot's search (<see cref="RobotMemory.Search"/>, format 15):
/// where it last saw him and his way then, the way it searches along, the leads taken, its order
/// among the searchers of that spot, the time left to reach the lead.</summary>
public readonly record struct RobotSearch(Vector3 SeenAt, Vector3 SeenVel, Vector3 Way, int Lead, int Slot, float LeadT);

/// <summary>
/// ROBOT AI IMPROVED's perception and alert states. Only where a robot goes and faces; DOS's script
/// still plays the animations and picks the firing pose.
/// <code>
///   Idle ──heard / alerted──► Suspicious ──1 s──► Investigate ──arrived──► Search ──28 s──► Return ──► Idle
///     ├── saw a robot hit, his shot struck near ──► Investigate (where he shot from, no hold)
///     └──────────────── sees him (any state) ──► Combat ──lost him──► Search (his last known spot)
///   Search: his last known spot ─► on along his way (400 u) ─► two leads beyond (300 u, ±60°),
///           a look round at each; searchers of one spot turn off his way by their order (0, +40°, −40°)
/// </code>
/// It sees in a cone (<see cref="ConeWalker"/>, <see cref="ConeFlyer"/>; an orb all round) unless in combat; a hit still
/// counts as seen (DOS's alert bit). It hears shots and blasts (<see cref="Robots.Noise"/>), not steps.
/// Entering combat alerts the robots round it (<see cref="AlarmRadius"/>): they investigate its spot.
/// </summary>
public sealed partial class Enemy
{
    /// <summary>The view cones, full width (degrees): walkers and tanks; hovers and HKs see wider; a
    /// flying mine (an orb) all round.</summary>
    private const float ConeWalker = 120f, ConeFlyer = 200f;
    /// <summary>Suspicious: held this long facing the noise (s).</summary>
    private const float SuspiciousTime = 1f;
    /// <summary>Investigate and Return give up after this long (s): a spot it cannot reach.</summary>
    private const float WalkMax = 20f;
    /// <summary>Search: how long it looks before giving up (s), and round what radius once its leads are
    /// done (units).</summary>
    private const float SearchTime = 28f, SearchRadius = 250f;
    /// <summary>A robot this near one hit, or seeing it this far off, is a witness (units).</summary>
    internal const float WitnessNear = 600f, WitnessSight = 1500f;
    /// <summary>His shot striking this near a robot is a shot at it (units).</summary>
    internal const float NearMiss = 300f;
    /// <summary>The leads after his last known spot: on along his way, then beyond it turned either
    /// side (degrees); one shorter than <see cref="MinLead"/> is turned (<see cref="LeadTurns"/>) (units).</summary>
    private const float AheadReach = 400f, BeyondReach = 300f, BeyondTurn = 60f, MinLead = 100f;
    /// <summary>Leads taken before it looks round at random: the way on and two beyond.</summary>
    private const int SearchLeads = 3;
    /// <summary>A lead not reached in this long is looked round from where it got (s; a cut-off spot).</summary>
    private const float LeadMax = 8f;
    /// <summary>Searchers of one spot (within <see cref="SplitRadius"/>) turn off his way by their order (degrees).</summary>
    private static readonly float[] SlotTurns = [0f, 40f, -40f];
    private const float SplitRadius = 600f;
    private const int MaxSlot = 7;
    /// <summary>A walker's lead: snapped to the mesh within this across and up or down, its path at most this many times its reach.</summary>
    private const float LeadSnap = 100f, LeadUp = 150f, LeadDetour = 1.8f;
    /// <summary>A lead blocked straight on tries these turns (degrees).</summary>
    private static readonly float[] LeadTurns = [0f, 45f, -45f, 90f, -90f];
    /// <summary>It looks every this many ticks (<see cref="Sense"/>): his way is measured over them.</summary>
    private const int LookTicks = 4;
    /// <summary>A saved way this near unit length (or zero) is kept as it is.</summary>
    private const float UnitSlack = 1e-4f;
    /// <summary>When it last told the others of a hit on it (<see cref="Robots.Clock"/>): once a tick.</summary>
    private double _witnessClock = double.NaN;
    /// <summary>A T-800 searches this many times longer: relentless.</summary>
    private const float TerminatorSearch = 2f;
    /// <summary>A look round: each heading (from the arrival's yaw, degrees) held this long (s).</summary>
    private static readonly float[] LookTurns = [90f, -90f, 180f];
    private const float LookHold = 1.2f;
    /// <summary>Patrol: legs up to this far from the post (units), pauses 2-5 s, a look up to this far round.</summary>
    private const float PatrolRadius = 300f, PauseMin = 2f, PauseMax = 5f, PatrolLook = 90f, PatrolLeg = 8f;
    /// <summary>Robots within this of one entering combat are alerted (units).</summary>
    internal const float AlarmRadius = 800f;

    private RobotAlert _alertState;
    private float _alertT;
    private Vector3 _post;
    private Vector3 _spot;
    private Vector3 _holdAt;
    private float? _face;
    private bool _pausing;
    private float _lookYaw;
    private float _lookBase;
    private int _looks;
    private int _heardSeq;
    private Vector3 _seenAt;
    private Vector3 _seenVel;
    private Vector3 _searchDir;
    private int _lead;
    private int _slot;
    private float _leadT;

    /// <summary>Its alertness (tests, the session hash).</summary>
    internal RobotAlert Alertness => _alertState;

    /// <summary>Moves on its own: a walker, hover, HK or tank (not a turret).</summary>
    private bool Roams => State is 7 or 6 or 9 or 13 && !IsStationary;

    /// <summary>The soldier at <paramref name="p"/> inside the view cone; always outside IMPROVED,
    /// for a fixed robot, and in combat (no flicker while it tracks him).</summary>
    private bool InCone(Vector3 p)
    {
        if (!Improved || !Roams || (IsFlying && IsKamikaze) || _alertState == RobotAlert.Combat)
        {
            return true;
        }

        var to = Flat(p - Position);
        if (to.LengthSquared() < 1f)
        {
            return true;
        }

        var half = Rad((IsFlying || State == 9 ? ConeFlyer : ConeWalker) * 0.5f);
        return MathF.Abs(Wrap(YawOf(to) - Yaw)) <= half;
    }

    /// <summary>A tick's perception into the alert state: a sighting is combat (kept for
    /// <see cref="MemoryTime"/>), losing him on a look is a search there; else a noise on a look.</summary>
    private void Remember(float dt, in AisSenses senses, Vector3? player)
    {
        if (senses.See && player is { } p)
        {
            if (_senseCount == 0)
            {
                SeenWay(dt, p);
            }

            _lastSeen = p;
            _memoryT = MemoryTime;
            if (_alertState != RobotAlert.Combat)
            {
                _alertState = RobotAlert.Combat;
                _ctx.Alarm(this);
            }

            return;
        }

        if (_senseCount != 0)
        {
            return;
        }

        if (_alertState == RobotAlert.Combat)
        {
            var spot = _lastSeen ?? Position;
            StartSearch(spot, _seenVel.LengthSquared() > MinMove * MinMove ? _seenVel : spot - Position);
            return;
        }

        if (Roams && _ctx.Heard(Position, ref _heardSeq, out var at))
        {
            Alarmed(at);
        }
    }

    /// <summary>Told of <paramref name="spot"/> (a noise, a neighbour's alarm): it turns to it, then
    /// goes there; one already on its way just takes the new spot.</summary>
    internal void Alarmed(Vector3 spot)
    {
        if (_alertState == RobotAlert.Combat || !Roams)
        {
            return;
        }

        _spot = spot;
        if (_alertState is RobotAlert.Investigate or RobotAlert.Search)
        {
            Enter(RobotAlert.Investigate, WalkMax);
            return;
        }

        _holdAt = Position;
        Enter(RobotAlert.Suspicious, SuspiciousTime);
    }

    /// <summary>Saw a robot hit (or his shot struck near it): it goes at once, with no hold, for
    /// <paramref name="origin"/>, where he shot from (not where he goes after).</summary>
    internal void Witnessed(Vector3 origin)
    {
        if (!Improved || !Roams || _alertState == RobotAlert.Combat)
        {
            return;
        }

        _spot = origin;
        _searching = false;
        Enter(RobotAlert.Investigate, WalkMax);
    }

    /// <summary>A hit on it: under IMPROVED the robots round it witness it (<see cref="Robots.Witness"/>).</summary>
    private void WitnessHit()
    {
        if (!Improved || _witnessClock == _ctx.Clock)
        {
            return;
        }

        _witnessClock = _ctx.Clock;   // a blast or a spread striking it again this tick: told once
        _ctx.Witness(this);
    }

    /// <summary><paramref name="p"/> in its cone with a clear line: it watches the spot.</summary>
    internal bool Watches(Vector3 p) => InCone(p) && LineOfSight(p);

    private void Enter(RobotAlert state, float time)
    {
        _alertState = state;
        _alertT = time;
    }

    /// <summary>A look that sees him: his flat way since the last one (none on a first sighting).</summary>
    private void SeenWay(float dt, Vector3 p)
    {
        var v = _alertState == RobotAlert.Combat && dt > 0f ? Flat(p - _seenAt) / (dt * LookTicks) : Vector3.Zero;
        var speed = v.Length();
        _seenVel = speed > MaxTargetSpeed ? v * (MaxTargetSpeed / speed) : v;
        _seenAt = p;
    }

    /// <summary>A search of <paramref name="spot"/>, then on along <paramref name="way"/> (flat; none: its facing).</summary>
    private void StartSearch(Vector3 spot, Vector3 way)
    {
        _spot = spot;
        _searchSpot = spot;
        _searching = false;
        way = Flat(way);
        _searchDir = way.LengthSquared() > 1e-6f ? Vector3.Normalize(way) : Heading(Yaw);
        (_lead, _leadT, _slot) = (0, WalkMax, Searchers(spot));
        Enter(RobotAlert.Search, Terminator ? SearchTime * TerminatorSearch : SearchTime);
    }

    /// <summary>The others already searching round <paramref name="spot"/>: its order among them.</summary>
    private int Searchers(Vector3 spot)
    {
        var n = 0;
        var all = _ctx.All;
        for (var i = 0; i < all.Count; i++)
        {
            var o = all[i];
            if (o != this && o.Alive && o._alertState == RobotAlert.Search && Flat(o._spot - spot).Length() <= SplitRadius)
            {
                n++;
            }
        }

        return Math.Min(n, MaxSlot);
    }

    /// <summary>Where a roaming robot goes while it does not see the soldier, by its alert state;
    /// <see cref="_face"/> set: the yaw to turn to while it stands.</summary>
    private Vector3 Goal(float dt)
    {
        _face = null;
        _alertT -= dt;
        _memoryT -= dt;
        if (_memoryT <= 0f)
        {
            _lastSeen = null;
        }

        switch (_alertState)
        {
            case RobotAlert.Suspicious:
                FaceSpot();
                if (_alertT <= 0f)
                {
                    Enter(RobotAlert.Investigate, WalkMax);
                }

                return _holdAt;
            case RobotAlert.Investigate:
                if (Flat(_spot - Position).Length() > Arrive && _alertT > 0f)
                {
                    return _spot;
                }

                StartSearch(_spot, Heading(Yaw));
                return Search(dt);
            case RobotAlert.Combat:
                return _lastSeen ?? Position;      // between looks: on to where he was
            case RobotAlert.Search:
                return Search(dt);
            case RobotAlert.Return:
                if (Flat(_post - Position).Length() > Arrive && _alertT > 0f)
                {
                    return _post;
                }

                Enter(RobotAlert.Idle, 0f);
                _pausing = false;
                _wanderT = 0f;
                return Patrol(dt);
            default:
                return Patrol(dt);
        }
    }

    private void FaceSpot()
    {
        var to = Flat(_spot - Position);
        if (to.LengthSquared() > 1f)
        {
            _face = YawOf(to);
        }
    }

    /// <summary>Search: to the spot, a look round (<see cref="LookTurns"/>), then the next lead
    /// (<see cref="NextLead"/>; never the soldier's live place); out of time, back to the post.</summary>
    private Vector3 Search(float dt)
    {
        if (_alertT <= 0f)
        {
            Enter(RobotAlert.Return, WalkMax);
            return _post;
        }

        if (!_searching)
        {
            _leadT -= dt;
            if (Flat(_searchSpot - Position).Length() > Arrive && _leadT > 0f)
            {
                return _searchSpot;
            }

            // Stuck or out of time near where it last looked round: on to the next lead.
            var again = _leadT <= 0f && Flat(Position - _holdAt).Length() < MinLead;
            _searching = true;
            _looks = again ? LookTurns.Length : 0;
            _lookBase = Yaw;
            _searchT = LookHold;
            _holdAt = Position;
        }

        if (_looks < LookTurns.Length)
        {
            _face = Wrap(_lookBase + Rad(LookTurns[_looks]));
            _searchT -= dt;
            if (_searchT <= 0f)
            {
                _looks++;
                _searchT = LookHold;
            }

            return _holdAt;
        }

        // Looked round: the next lead (none found this tick: it holds and probes on next tick).
        if (NextLead() is not { } next)
        {
            return _holdAt;
        }

        (_searchSpot, _searching, _leadT) = (next, false, LeadMax);
        return _searchSpot;
    }

    /// <summary>The spot after this one: on along his way (turned by its order among the searchers),
    /// then beyond it turned either side in turn; one lead probed a tick (null: it could not get
    /// there, the next tick tries the next). With the leads done, a spot within
    /// <see cref="SearchRadius"/> of here.</summary>
    private Vector3? NextLead()
    {
        if (_lead < SearchLeads)
        {
            _lead++;
            var side = _slot % 2 == 0 ? 1f : -1f;
            return _lead == 1
                ? Lead(_spot, Turn(_searchDir, SlotTurns[_slot % SlotTurns.Length]), AheadReach, side)
                : Lead(_searchSpot, Turn(_searchDir, (_lead % 2 == 0 ? BeyondTurn : -BeyondTurn) * side), BeyondReach, side);
        }

        _lead = SearchLeads + 1;
        var rng = _improvedRng;
        var a = rng.NextSingle() * MathF.Tau;
        return _searchSpot + Heading(a) * (SearchRadius * rng.NextSingle());
    }
    /// <summary>A spot about <paramref name="reach"/> from <paramref name="from"/> along <paramref name="dir"/>
    /// it can get to, at least <see cref="MinLead"/> off it and off the robot; else turned
    /// (<see cref="LeadTurns"/>); null: none.</summary>
    private Vector3? Lead(Vector3 from, Vector3 dir, float reach, float side)
    {
        foreach (var turn in LeadTurns)
        {
            if (LeadAlong(from, Turn(dir, turn * side), reach) is { } spot
                && Flat(spot - from).Length() >= MinLead && Flat(spot - Position).Length() >= MinLead)
            {
                return spot;
            }
        }

        return null;
    }

    /// <summary>Where it gets toward <paramref name="from"/> + <paramref name="dir"/> × <paramref name="reach"/>:
    /// a walker on its nav mesh the mesh point there with a path no longer than <see cref="LeadDetour"/>
    /// × the reach (round a corner, through a doorway the mesh holds); a flyer the whole way if its hull
    /// passes at its height; else as far as a ray at eye height goes.</summary>
    private Vector3? LeadAlong(Vector3 from, Vector3 dir, float reach)
    {
        if (IsFlying)
        {
            var aloft = new Vector3(from.X, Position.Y, from.Z);
            return HullHits(Hull(), aloft, dir * reach) ? null : from + dir * reach;
        }

        if (NavBody is { } body && _ctx.Nav?.QueryFor(body) is { } nav)
        {
            return MeshLead(nav, from, from + dir * reach, reach);
        }

        var eye = from + new Vector3(0f, EyeHeight, 0f);
        return Cast(eye, eye + dir * reach, out var hit) ? from + dir * (Flat(hit.Point - eye).Length() - CoverEdge) : from + dir * reach;
    }

    /// <summary>The mesh point nearest <paramref name="want"/> with a found path from <paramref name="from"/>
    /// no longer than <see cref="LeadDetour"/> × <paramref name="reach"/>. The path goes into the robot's
    /// own corner list (no allocation); its next step plans afresh.</summary>
    private Vector3? MeshLead(NavQuery nav, Vector3 from, Vector3 want, float reach)
    {
        if (!nav.Nearest(want, new Vector3(LeadSnap, LeadUp, LeadSnap), out var spot))
        {
            return null;
        }

        var found = nav.FindPath(from, spot, _corners) == PathResult.Found;
        _pathTo = null;
        var length = 0f;
        for (var i = 1; i < _corners.Count; i++)
        {
            length += Vector3.Distance(_corners[i - 1], _corners[i]);
        }

        _corners.Clear();
        return found && length <= reach * LeadDetour ? spot : null;
    }

    private static Vector3 Turn(Vector3 dir, float degrees) => Heading(YawOf(dir) + Rad(degrees));

    /// <summary>A saved search put back, held to what the robot reaches itself; none (an older save):
    /// his way unknown.</summary>
    private void LoadSearch(RobotSearch? search)
    {
        var s = search ?? new RobotSearch(_lastSeen ?? Position, Vector3.Zero, Vector3.Zero, 0, 0, WalkMax);
        var speed = s.SeenVel.Length();
        (_seenAt, _seenVel) = (s.SeenAt, speed > MaxTargetSpeed ? s.SeenVel * (MaxTargetSpeed / speed) : s.SeenVel);

        // A unit way (or none: a robot that never searched) is kept as saved, so a save loaded saves
        // the same; any other is a crafted one, made flat and unit.
        var way = Flat(s.Way);
        var len = way.Length();
        _searchDir = MathF.Abs(len - 1f) <= UnitSlack || len <= UnitSlack ? way : Vector3.Normalize(way);
        (_lead, _slot, _leadT) = (Math.Clamp(s.Lead, 0, SearchLeads + 1), Math.Clamp(s.Slot, 0, MaxSlot), Math.Clamp(s.LeadT, 0f, WalkMax));
    }

    /// <summary>Idle: a pause (2-5 s, turned to a look round), then a short leg to a spot within
    /// <see cref="PatrolRadius"/> of the post whose way is clear (#57: DOS robots stand; the old wander never stopped).</summary>
    private Vector3 Patrol(float dt)
    {
        _wanderT -= dt;
        var rng = _improvedRng;
        if (_pausing)
        {
            _face = _lookYaw;
            if (_wanderT > 0f)
            {
                return _holdAt;
            }

            _pausing = false;
            _wanderT = PatrolLeg;
            _wanderTarget = PatrolSpot();
            return _wanderTarget;
        }

        if (_wanderT > 0f && Flat(_wanderTarget - Position).Length() > Arrive && !(_blocked && !_following))
        {
            return _wanderTarget;
        }

        _pausing = true;
        _wanderT = PauseMin + (PauseMax - PauseMin) * rng.NextSingle();
        _lookYaw = Wrap(Yaw + Rad(PatrolLook) * (rng.NextSingle() * 2f - 1f));
        _holdAt = Position;
        _face = _lookYaw;
        return _holdAt;
    }

    /// <summary>A spot round the post: a few headings tried for a clear way from here.</summary>
    private Vector3 PatrolSpot()
    {
        var rng = _improvedRng;
        var a0 = rng.NextSingle() * MathF.Tau;
        var reach = PatrolRadius * (0.3f + 0.7f * rng.NextSingle());
        for (var i = 0; i < RoamTries; i++)
        {
            var spot = _post + Heading(a0 + i * MathF.Tau / RoamTries) * reach;
            var to = Flat(spot - Position);
            if (to.LengthSquared() < 1f || !LongBlocked(Vector3.Normalize(to)))
            {
                return spot;
            }
        }

        return _post;
    }
}
