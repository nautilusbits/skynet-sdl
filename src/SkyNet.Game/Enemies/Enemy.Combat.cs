using System.Numerics;

namespace SkyNet.Game.Enemies;

/// <summary>What a save keeps of an IMPROVED robot's fight (<see cref="RobotMemory.Combat"/>, format 14).</summary>
public readonly record struct RobotCombat(bool Contact, float LostT, float ReactT, float AimErr, Vector3 AimDir, Vector3 LastFeet,
    Vector3 TargetVel, bool Token, float TokenT, int TokenShots, float TokenRest, Vector3? Cover, float CoverT, bool CoverHeld,
    float CoverRest, int CoverTry, int Strafe, float StrafeT, Vector3? Flank, int FlankSide, float StaleT);

/// <summary>
/// ROBOT AI IMPROVED's combat: robots that fire like people, never better than DOS's perfect aim
/// (the owner's ruling: the maps rely on numbers; DOS's damage, fire roll and difficulty stay).
/// <code>
///   sees him ──► reaction (0.3-0.8 s) ──► aim swings onto him at its turn rate ──► fires
///                                          error: large on acquire, shrinks while tracking,
///                                          grows when he turns sharply or it is hit
///   attack tokens: at most <see cref="Robots.MaxAttackers"/> roaming robots fire freely (lowest
///   index first); the rest fire at <see cref="WaitFire"/> of the rate and flank him at range (T-800s
///   keep coming), drones hold off
///   hurt below <see cref="CoverHp"/>: small walkers and drones go where he cannot see them, hold, return
/// </code>
/// No lead on moving targets (it would raise the hit rate).
/// </summary>
public sealed partial class Enemy
{
    /// <summary>Whether the robot perceives the soldier (the combat helpers' parameter).</summary>
    private enum Sight
    {
        Unseen,
        Seen,
    }

    private static Sight SightOf(bool see) => see ? Sight.Seen : Sight.Unseen;

    /// <summary>The fighting kinds, for <see cref="Styles"/>.</summary>
    private enum CombatKind
    {
        Walker,
        Terminator,
        Hover,
        Hk,
        Tank,
        Turret,
    }

    /// <summary>A kind's flavour: reaction (s), aim turn (× its turn rate).</summary>
    private readonly record struct CombatStyle(float Reaction, float AimTurn);

    /// <summary>By <see cref="CombatKind"/>: T-800s slow to start and to swing, drones quick.</summary>
    private static readonly CombatStyle[] Styles =
    [
        new(0.5f, 1f),     // walker
        new(0.8f, 0.7f),   // terminator
        new(0.3f, 1f),     // hover
        new(0.4f, 1f),     // hk
        new(0.6f, 0.8f),   // tank
        new(0.5f, 1f),     // turret
    ];

    /// <summary>The reaction varies by this share either way (own stream).</summary>
    private const float ReactJitter = 0.25f;
    /// <summary>Contact lost this long (s) counts as lost: the next sighting is a new acquire.</summary>
    private const float ReacquireGrace = 1f;
    /// <summary>Aim error (radians): on acquire, the floor it shrinks to (~0.5°: never perfect), the cap.</summary>
    private const float ErrStart = 0.1f, ErrMin = 0.008f, ErrMax = 0.3f;
    /// <summary>Without a token a robot still fires, at this share of DOS's fire roll (tuned so the
    /// scripted fight keeps 75-100 % of the hits before phase 2: the maps rely on numbers).</summary>
    private const float WaitFire = 0.6f;
    /// <summary>The error's excess over <see cref="ErrMin"/> shrinks by e every this long (s): ~1.5 s to settle.</summary>
    private const float ErrDecay = 0.5f;
    /// <summary>The error added when he turns sharply, and when the robot is hit (radians).</summary>
    private const float ErrTurn = 0.08f, ErrHit = 0.1f;
    /// <summary>A shot misses by at least this share of the error (never a dead-centre shot).</summary>
    private const float ErrFloorShare = 0.5f;
    /// <summary>He turns sharply: moving faster than this (u/s) at more than acos(<see cref="TurnCos"/>) from his last way.</summary>
    private const float MinMove = 40f, TurnCos = 0.5f;
    /// <summary>The least aim swing (rad/s), and the aim must be this close (cos) to him to fire.</summary>
    private const float AimTurnMin = 1f, AimReadyCos = 0.955f;
    /// <summary>A token: held for at most this many shots or seconds, then rested this long (s).</summary>
    private const int BurstShots = 5;
    private const float BurstTime = 3f, TokenRest = 1.5f;
    /// <summary>A robot not ticked for this long (s) gives its token back (out of the activity window).</summary>
    private const float StaleTime = 0.5f;
    /// <summary>A loaded target speed is held to this (u/s; the soldier's fastest ride is far below).</summary>
    private const float MaxTargetSpeed = 4000f;
    /// <summary>Flanking without a token: this far round him (radians), within these ranges (units).</summary>
    private const float FlankArc = 0.6f, FlankMin = 300f, FlankMax = 1000f;
    /// <summary>A drone without a token holds within this of him (units).</summary>
    private const float HoverHold = 600f;
    /// <summary>A tank keeps at least this far from him (units).</summary>
    private const float TankKeep = 600f;
    /// <summary>A drone strafes at this share of its speed, flipping every 1-2 s.</summary>
    private const float StrafeShare = 0.35f, StrafeMin = 1f, StrafeVar = 1f;
    /// <summary>Hurt below this share of its hit points, a small robot or drone breaks his line of sight.</summary>
    private const float CoverHp = 0.35f;
    /// <summary>Cover: candidates tried per tick and in all, the time to get there, to hold, and to rest before the next (s).</summary>
    private const int CoverPerTick = 2;
    private const float CoverMax = 6f, CoverHold = 3f, CoverRest = 10f;
    /// <summary>A cover spot is kept this far back from a mesh edge (units).</summary>
    private const float CoverEdge = 30f;
    /// <summary>Cover candidates: these headings from away-from-him (degrees), at these reaches (units).</summary>
    private static readonly float[] CoverAngles = [0f, 50f, -50f, 100f, -100f, 150f];
    private static readonly float[] CoverReach = [300f, 600f];
    private static readonly int CoverTries = CoverAngles.Length * CoverReach.Length;

    private bool _contact;
    private float _lostT;
    private float _reactT;
    private float _aimErr = ErrStart;
    private Vector3 _aimDir = -Vector3.UnitZ;
    private Vector3 _lastFeet;
    private Vector3 _targetVel;
    private bool _token;
    private float _tokenT;
    private int _tokenShots;
    private float _tokenRest;
    /// <summary>Seconds since its last combat tick (<see cref="Robots"/> ages it each tick): kept in a
    /// save, so a load deals the tokens as the run without one.</summary>
    private float _staleT = StaleTime + 1f;
    private Vector3? _cover;
    private float _coverT;
    private bool _coverHeld;
    private float _coverRest;
    private int _coverTry;
    private Vector3? _flank;
    private int _flankSide = 1;
    private int _strafe = 1;
    private float _strafeT;

    /// <summary>The aim error now (radians; tests).</summary>
    internal float AimError => _aimErr;

    /// <summary>Holds an attack token (tests, <see cref="Robots"/>).</summary>
    internal bool HoldsToken => _token;

    /// <summary>On its way to or holding cover (tests).</summary>
    internal bool InCover => _cover != null;

    private CombatKind Kind => State switch
    {
        9 => CombatKind.Hk,
        13 => CombatKind.Tank,
        7 => Terminator ? CombatKind.Terminator : CombatKind.Walker,
        6 => IsFlying ? CombatKind.Hover : CombatKind.Tank,
        _ => CombatKind.Turret,
    };

    private CombatStyle Style => Styles[(int)Kind];

    /// <summary>Fires only with a token: a roaming robot with a gun (turrets are fixed defences).</summary>
    private bool NeedsToken => Roams && !IsKamikaze;

    /// <summary>May break his line of sight when hurt: a small walker or a drone; never a T-800 or a tank.</summary>
    private bool CanCover => !IsStationary && !IsKamikaze
        && ((State == 7 && !Terminator && Spacing <= SmallSpacing) || (State == 6 && IsFlying));

    /// <summary>Without a token, in sight: it repositions instead of closing in (a T-800 keeps coming).</summary>
    private bool Waits(Sight sight) => sight == Sight.Seen && NeedsToken && !_token && Kind != CombatKind.Terminator;

    // ------------------------------------------------------------------ the tick

    /// <summary>A tick's combat state against the soldier at <paramref name="feet"/>: in contact while
    /// the robot or one of its guns perceives him.</summary>
    private void UpdateCombat(float dt, in AisSenses senses, Vector3 feet)
    {
        _staleT = 0f;
        var contact = senses.See || GunSees();
        if (contact && !_contact)
        {
            Acquire(feet);
        }

        _lostT = contact ? 0f : _lostT + dt;
        if (_lostT > ReacquireGrace)
        {
            _contact = false;
        }

        _tokenRest = MathF.Max(_tokenRest - dt, 0f);
        if (_token)
        {
            _tokenT += dt;
        }

        if (_contact && dt > 0f)
        {
            Track(dt, feet);
        }

        _lastFeet = feet;
        UpdateCover(dt, feet);
    }

    /// <summary>A first sighting (or one after a loss): the reaction starts, the aim is off by
    /// <see cref="ErrStart"/> and points where its gun does.</summary>
    private void Acquire(Vector3 feet)
    {
        _contact = true;
        _reactT = Style.Reaction * (1f + ReactJitter * (_improvedRng.NextSingle() * 2f - 1f));
        _aimErr = MathF.Max(_aimErr, ErrStart);
        _aimDir = Gun()?.Forward ?? Forward;
        (_lastFeet, _targetVel) = (feet, Vector3.Zero);
    }

    /// <summary>The first part with a gun, whole; null: none.</summary>
    private EnemyPart? Gun()
    {
        foreach (var p in Segments)
        {
            if (p.Fire != null && !p.Gone)
            {
                return p;
            }
        }

        return null;
    }

    /// <summary>Tracking: the reaction runs out, the error settles (and grows when he turns
    /// sharply), the aim swings onto his eye at the type's turn rate.</summary>
    private void Track(float dt, Vector3 feet)
    {
        _reactT = MathF.Max(_reactT - dt, 0f);
        var vel = Flat(feet - _lastFeet) / dt;
        if (vel.LengthSquared() > MinMove * MinMove)
        {
            var was = _targetVel.LengthSquared() > MinMove * MinMove;
            if (was && Vector3.Dot(Vector3.Normalize(vel), Vector3.Normalize(_targetVel)) < TurnCos)
            {
                _aimErr += ErrTurn;
            }

            _targetVel = vel;
        }

        _aimErr = MathF.Min(ErrMin + (_aimErr - ErrMin) * MathF.Exp(-dt / ErrDecay), ErrMax);
        var to = feet + new Vector3(0f, DosEye, 0f) - Centre;
        if (to.LengthSquared() > 1f)
        {
            _aimDir = RotateToward(_aimDir, Vector3.Normalize(to), AimTurn * dt);
        }
    }

    /// <summary>The aim's swing (rad/s): the fastest of the body's and its guns' turn, by its kind.</summary>
    private float AimTurn
    {
        get
        {
            var rate = TurnRate;
            foreach (var p in _turrets)
            {
                if (p.Type?.Turn is > 0 and var t)
                {
                    rate = MathF.Max(rate, t / 2048f * MathF.Tau);
                }
            }

            return MathF.Max(rate, AimTurnMin) * Style.AimTurn;
        }
    }

    /// <summary><paramref name="from"/> turned toward <paramref name="to"/> by at most <paramref name="step"/> radians (unit vectors).</summary>
    private static Vector3 RotateToward(Vector3 from, Vector3 to, float step)
    {
        var angle = MathF.Acos(Math.Clamp(Vector3.Dot(from, to), -1f, 1f));
        if (angle <= step)
        {
            return to;
        }

        var axis = Vector3.Cross(from, to);
        if (axis.LengthSquared() < 1e-8f)
        {
            axis = MathF.Abs(from.Y) < 0.9f ? Vector3.Cross(from, Vector3.UnitY) : Vector3.Cross(from, Vector3.UnitX);
        }

        return Vector3.Normalize(Vector3.Transform(from, Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), step)));
    }

    /// <summary>A hit throws the aim off (<see cref="ErrHit"/>).</summary>
    private void Flinch()
    {
        if (Improved)
        {
            _aimErr = MathF.Min(_aimErr + ErrHit, ErrMax);
        }
    }

    // ------------------------------------------------------------------ fire

    /// <summary>May fire at <paramref name="aim"/> now: reacted, not hiding, its aim on him; without
    /// a token it needs, only on a <see cref="WaitFire"/> draw of its own stream.</summary>
    private bool Ready(Vector3 aim)
    {
        if (!_contact || _reactT > 0f || _cover != null)
        {
            return false;
        }

        var to = aim - Centre;
        if (to.LengthSquared() >= 1f && Vector3.Dot(_aimDir, Vector3.Normalize(to)) < AimReadyCos)
        {
            return false;
        }

        return !NeedsToken || _token || _improvedRng.NextSingle() < WaitFire;
    }

    /// <summary>The bolt's way from <paramref name="muzzle"/>: along the tracked aim, off by the
    /// error in a direction drawn from its own stream.</summary>
    private Vector3 Aimed(Vector3 muzzle, Vector3 aim)
    {
        _tokenShots++;
        var reach = (aim - Centre).Length();
        var d = Centre + _aimDir * reach - muzzle;
        d = d.LengthSquared() > 1e-6f ? Vector3.Normalize(d) : _aimDir;
        var right = Vector3.Cross(d, Vector3.UnitY);
        right = right.LengthSquared() > 1e-6f ? Vector3.Normalize(right) : Vector3.UnitX;
        var up = Vector3.Cross(right, d);
        var turn = _improvedRng.NextSingle() * MathF.Tau;
        var size = _aimErr * (ErrFloorShare + (1f - ErrFloorShare) * _improvedRng.NextSingle());
        return Vector3.Normalize(d + (right * MathF.Cos(turn) + up * MathF.Sin(turn)) * MathF.Tan(size));
    }

    // ------------------------------------------------------------------ tokens (Robots.DealTokens)

    /// <summary>Asks for a token: in contact and reacted (no token idles through a reaction), rested,
    /// not hiding, ticked lately.</summary>
    internal bool WantsToken => Alive && NeedsToken && _contact && _reactT <= 0f && _tokenRest <= 0f && _cover == null && Fresh;

    /// <summary>Keeps its token: in contact, not hiding, ticked lately.</summary>
    internal bool KeepsToken => Alive && _contact && _cover == null && Fresh;

    /// <summary>Its burst is over: the token goes to one waiting, if any.</summary>
    internal bool BurstOver => _tokenT >= BurstTime || _tokenShots >= BurstShots;

    private bool Fresh => _staleT <= StaleTime;

    /// <summary>A tick passed (<see cref="Robots"/>, before the robots run).</summary>
    internal void AgeCombat(float dt) => _staleT = MathF.Min(_staleT + dt, StaleTime + 1f);

    /// <summary>Tests: the next tick searches for cover from scratch (no rest, no candidate tried).</summary>
    internal void SeekCoverNow() => (_coverRest, _coverTry) = (0f, 0);

    internal void TakeToken() => (_token, _tokenT, _tokenShots) = (true, 0f, 0);

    /// <summary>The token goes back; it rests before asking again (others get a turn).</summary>
    internal void DropToken() => (_token, _tokenRest) = (false, TokenRest);

    // ------------------------------------------------------------------ moves

    /// <summary>Flanking without a token: a spot round him at its range, as far as it can get
    /// (<see cref="WayTo"/>; none: it holds), kept until reached (then the other way).</summary>
    private Vector3 Flank(Vector3 player, float near)
    {
        if (_flank is { } spot && Flat(spot - Position).Length() > Arrive)
        {
            return spot;
        }

        var from = Flat(Position - player);
        var reach = Math.Clamp(from.Length(), MathF.Max(near * SideStepOut, FlankMin), FlankMax);
        var a = YawOf(from.LengthSquared() > 1f ? from : Forward) + _flankSide * FlankArc;
        _flankSide = -_flankSide;
        var way = Flat(player + Heading(a) * reach - Position);
        var len = way.Length();
        var dir = len > Arrive ? way / len : Vector3.Zero;
        _flank = len > Arrive && !LongBlocked(dir, len) && !RobotAhead(dir, len) ? WayTo(dir, len) ?? Position : Position;
        return _flank.Value;
    }

    /// <summary>A drone's sideways drift while it fights (flips every 1-2 s or when it meets something).</summary>
    private Vector3 Strafe(float dt, Sight sight)
    {
        if (!Improved || Kind != CombatKind.Hover || IsKamikaze || sight != Sight.Seen || _cover != null)
        {
            return Vector3.Zero;
        }

        _strafeT -= dt;
        if (_strafeT <= 0f || _blocked)
        {
            _strafe = -_strafe;
            _strafeT = StrafeMin + StrafeVar * _improvedRng.NextSingle();
        }

        var right = new Vector3(MathF.Cos(Yaw), 0f, -MathF.Sin(Yaw));
        return right * (_strafe * MoveSpeed * StrafeShare * dt);
    }

    // ------------------------------------------------------------------ cover

    /// <summary>Cover: hurt and fighting, it looks for a spot he cannot see (a few candidates a
    /// tick); there (or out of his sight) it holds, then fights on and rests before the next.</summary>
    private void UpdateCover(float dt, Vector3 feet)
    {
        if (_cover is { } spot)
        {
            _coverT -= dt;
            if (!_coverHeld && (Flat(spot - Position).Length() <= Arrive || !Sees))
            {
                (_coverHeld, _coverT) = (true, CoverHold);
            }

            if (_coverT <= 0f)
            {
                (_cover, _coverHeld, _coverRest) = (null, false, CoverRest);
            }

            return;
        }

        if (_coverRest > 0f)
        {
            _coverRest -= dt;
            return;
        }

        if (!CanCover || !_contact || Hp >= MaxHp * CoverHp)
        {
            _coverTry = 0;
            return;
        }

        for (var n = 0; n < CoverPerTick && _coverTry < CoverTries; n++, _coverTry++)
        {
            if (CoverAt(_coverTry, feet) is { } found)
            {
                (_cover, _coverT, _coverHeld, _coverTry) = (found, CoverMax, false, 0);
                return;
            }
        }

        if (_coverTry >= CoverTries)
        {
            (_coverTry, _coverRest) = (0, CoverRest);   // none round here: fight on
        }
    }

    /// <summary>Candidate <paramref name="i"/>: a spot away from him it can get to, with level
    /// between his eye and its own there; null: not one.</summary>
    private Vector3? CoverAt(int i, Vector3 feet)
    {
        var away = Flat(Position - feet);
        var a = YawOf(away.LengthSquared() > 1f ? away : -Forward) + Rad(CoverAngles[i % CoverAngles.Length]);
        var dir = Heading(a);
        var reach = CoverReach[i / CoverAngles.Length];
        if (WayTo(dir, reach) is not { } spot)
        {
            return null;
        }

        var eye = feet + new Vector3(0f, DosEye, 0f);
        return Cast(eye, spot + new Vector3(0f, EyeHeight, 0f), out _) ? spot : null;
    }

    /// <summary>How far along <paramref name="dir"/> it gets, up to <paramref name="reach"/>: a flyer's
    /// hull sweep, a walker's nav mesh (or its probes without one).</summary>
    private Vector3? WayTo(Vector3 dir, float reach)
    {
        var spot = Position + dir * reach;
        if (IsFlying)
        {
            return HullHits(Hull(), Position, spot - Position) ? null : spot;
        }

        if (NavBody is not { } body || _ctx.Nav?.QueryFor(body) is not { } nav)
        {
            return LongBlocked(dir, reach) || DropAhead(dir) ? null : spot;
        }

        var foot = new Vector3(0f, FootOffset, 0f);
        if (!nav.Raycast(Position + foot, spot + foot, out var hit))
        {
            return spot;
        }

        var got = Flat(hit - Position).Length() - CoverEdge;
        return got < Arrive ? null : Position + dir * got;
    }

    // ------------------------------------------------------------------ memory

    private RobotCombat SaveCombat() =>
        new(_contact, _lostT, _reactT, _aimErr, _aimDir, _lastFeet, _targetVel, _token, _tokenT, _tokenShots, _tokenRest, _cover, _coverT,
            _coverHeld, _coverRest, _coverTry, _strafe, _strafeT, _flank, _flankSide, _staleT);

    /// <summary>A saved fight put back, its numbers held to their ranges; none (an older save): no contact.</summary>
    private void LoadCombat(RobotCombat? combat)
    {
        var c = combat ?? new RobotCombat(false, 0f, 0f, ErrStart, Forward, Position, Vector3.Zero, false, 0f, 0, 0f, null, 0f, false, 0f, 0, 1, 0f,
            null, 1, StaleTime + 1f);

        // Each number held to what the robot can reach itself: a crafted save must not silence it.
        var react = Style.Reaction * (1f + ReactJitter);
        (_contact, _lostT, _reactT, _aimErr) = (c.Contact, c.LostT, Math.Clamp(c.ReactT, 0f, react), Math.Clamp(c.AimErr, ErrMin, ErrMax));
        _aimDir = c.AimDir.LengthSquared() > 1e-6f ? Vector3.Normalize(c.AimDir) : Forward;
        var speed = c.TargetVel.Length();
        (_lastFeet, _targetVel) = (c.LastFeet, speed > MaxTargetSpeed ? c.TargetVel * (MaxTargetSpeed / speed) : c.TargetVel);
        (_token, _tokenT, _tokenShots) = (c.Token, Math.Clamp(c.TokenT, 0f, BurstTime), Math.Clamp(c.TokenShots, 0, BurstShots));
        _tokenRest = Math.Clamp(c.TokenRest, 0f, TokenRest);
        _cover = CanCover ? c.Cover : null;
        (_coverT, _coverHeld, _coverRest) = (Math.Clamp(c.CoverT, 0f, MathF.Max(CoverMax, CoverHold)), c.CoverHeld, Math.Clamp(c.CoverRest, 0f, CoverRest));
        _coverTry = Math.Clamp(c.CoverTry, 0, CoverTries);
        (_strafe, _strafeT) = (c.Strafe < 0 ? -1 : 1, Math.Clamp(c.StrafeT, 0f, StrafeMin + StrafeVar));
        (_flank, _flankSide, _staleT) = (c.Flank, c.FlankSide < 0 ? -1 : 1, Math.Clamp(c.StaleT, 0f, StaleTime + 1f));
    }
}
