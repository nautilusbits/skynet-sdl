using System.Numerics;
using SkyNet.Formats;
using SkyNet.Game.Weapons;

namespace SkyNet.Game.Enemies;

/// <summary>
/// One robot: the DOS type data (SKYNET.EXE's enemy table) driving its families — walkers (7),
/// hovers and chasers (6), flyers (9), tanks (13), turret segments (2/8), script-only machines
/// (10), dormant traps (12) — through its AIS script (<see cref="AisBrain"/>). Ported from the
/// Godot port's enemy.gd (_tick_data, _move_data, _aim_segment, _try_fire, take_damage, _die,
/// _detonate_trap); the per-family slope, step and drop limits are that port's, not DOS's. World
/// axes: Y up, yaw 0 faces −Z (DOS +Z), forward = (−sin yaw, 0, −cos yaw).
/// </summary>
public sealed partial class Enemy : IDamageable
{
    /// <summary>DOS perception cap (FUN_0013c4da: &lt; 0x7d1).</summary>
    public const float PerceptionRange = 2000f;
    /// <summary>Default aim cone: 0x8c of 2048 (24.6°).</summary>
    public const float AimCone = 140f / 2048f * MathF.Tau;
    /// <summary>Calibration (the Godot port's): the DOS bolt speed field × 5 is units/s; shots per
    /// second = rate / 128 (DOS rolls rand&amp;1023 &lt; rate × difficulty once per frame).</summary>
    public const float BoltSpeedScale = 5f;
    public const float FireRateDiv = 128f;
    public const float AimSpread = 0.055f * 0.6f;
    /// <summary>Where the robots aim and look: this high above the soldier's feet.</summary>
    public const float AimHeight = 60f;
    public const float EyeHeight = 60f;
    public const float FlyerSinkSpeed = 250f, FlyerClimbSpeed = 300f, FlyerMinAlt = 250f, FlyerLookahead = 400f;
    public const float WanderRadius = 1200f;
    public const float SlopeTerminator = 0.79f, SlopeOther = 0.47f;
    public const float StepTerminator = 40f, StepOther = 20f;
    public const float MaxDropTerminator = 96f, MaxDropOther = 60f;
    public const float SinkRecover = 110f;
    /// <summary><see cref="SurfaceAt"/>'s column: this far above and below the point.</summary>
    private const float SurfaceUp = 6000f, SurfaceDown = 20000f;
    public const float KamikazeReach = 40f;
    public const float MachineMargin = 60f;
    public const float MachineHitDamage = 4f;
    /// <summary>The rotor/engine strip rate of a type without an animation block.</summary>
    public const float StripFps = 12f;
    public const float DefaultTurn = 2.6f;
    /// <summary>A turret's rest pose: DOS aims it at (x, y − 200, z − 300), a world offset.</summary>
    public static readonly Vector3 TurretRest = new(0f, 200f, 300f);
    public const float HitMargin = 1.18f, HitMin = 45f;
    /// <summary>The death blast (EnemyKill 0x129a28, value 75 for every robot) strikes this long after.</summary>
    public const float DeathBlastDelay = 0.375f;
    public const int DeathBlast = 75, DeathBlastPad = 40, DeathBlastMin = 37;
    public const int DeathEffectBank = 358;
    /// <summary>A dormant trap's bang (0x26).</summary>
    public const int TrapSound = AisData.TrapSound;

    /// <summary>Ground speed of a walker, measured from its walk cycle's root motion (enemy_anim.gd
    /// WALK_SPEED): DOS moves state-7 actors by the model's planted foot, not the table speed.</summary>
    public static readonly IReadOnlyDictionary<string, float> WalkSpeed = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase)
    {
        ["endoskel"] = 60f, ["endorfl"] = 59f, ["t600pst"] = 59f, ["t600rfl"] = 59f, ["t800pst"] = 59f, ["t800rfl"] = 59f,
        ["raptor"] = 74f, ["spidbot"] = 57f, ["t-rexleg"] = 74f,
    };

    public static readonly HashSet<string> Stationary = new(StringComparer.OrdinalIgnoreCase)
    {
        "smltrrt", "guntwr1", "guntwr3", "hvytrrt", "hvytrrt2", "hvytrrt3", "smlcanon", "smlplsm", "tribrrl", "msslpodr",
        "msslpodl", "hvyplsm", "hvybrrl", "hmslpodl", "hmslpodr", "bosstrrt", "bossgun", "grabbase",
    };

    public static readonly HashSet<string> FlyingNames = new(StringComparer.OrdinalIgnoreCase) { "globe", "drone", "scout", "hk_ftr", "hk_bmbr" };
    public static readonly HashSet<string> PassiveNames = new(StringComparer.OrdinalIgnoreCase) { "boxtrck" };

    private readonly Robots _ctx;
    private readonly List<EnemyPart> _turrets = [];
    private readonly List<EnemyPart> _machines = [];
    private bool _initDone;
    private bool _alert;
    private int _senseCount;
    private bool _perceived;
    private bool _blocked;
    private Vector3 _wanderTarget;
    private float _wanderT;
    private float _stripT;
    private int _kamikaze = -1;
    private readonly float _rootRestYaw;
    private readonly List<RetroEng.Physics.Triangle> _column = [];

    public Enemy(Robots ctx, int type, Vector3 position, float yaw, int markerOffset = -1)
    {
        _ctx = ctx;
        var ais = ctx.Ais;
        TypeIndex = type;
        Type = type >= 0 && type < ais.Types.Length ? ais.Types[type] : null;
        Position = position;
        Yaw = _rootRestYaw = yaw;
        MarkerOffset = markerOffset;
        _senseCount = ctx.NextSensePhase();
        Root = new EnemyPart(type, Type, ctx.Models.Get(Type?.ModelFile ?? "", Type?.Look ?? ""), null, Vector3.Zero, 0f, 0f, 0f);
        AddSegments(Root, 0);
        Segments = [.. Root.Walk()];
        foreach (var p in Root.Walk())
        {
            if (p.IsTurret)
            {
                _turrets.Add(p);
            }
            else if (p != Root && p.IsMachine && p.Model is { Frames: > 1 })
            {
                p.Brain = new AisBrain(ais, p.TypeIndex, ctx.NewRng());
                _machines.Add(p);
            }
        }

        var name = Type?.Name ?? "";
        IsStationary = Stationary.Contains(name);
        IsFlying = FlyingNames.Contains(name);
        IsPassive = PassiveNames.Contains(name);
        Hp = MaxHp = Type?.HitPoints ?? 60;
        MoveSpeed = Type?.Speed is > 0 and var s ? s : 480f;
        if (State == 7 && WalkSpeed.TryGetValue(name, out var ws))
        {
            MoveSpeed = ws;
        }

        TurnRate = Type?.Turn is > 0 and var t ? t / 2048f * MathF.Tau : DefaultTurn;
        FootOffset = MathF.Min(Root.Model?.FootY ?? 0f, 0f);
        Brain = Type != null ? new AisBrain(ais, type, ctx.NewRng()) : null;
        UpdateTransforms();
        KeepPose();
        FitBounds();
        if (Brain is { HasScript: true })
        {
            Brain.Update(0f, AisSenses.None);
            Root.Frame = ClampFrame(Root, Brain.Frame);
        }
    }

    public int TypeIndex { get; }
    public EnemyType? Type { get; }
    public string Name => Type?.Name ?? $"type{TypeIndex}";
    public int State => Type?.State ?? 0;
    public EnemyPart Root { get; }
    /// <summary><see cref="Root"/>'s <see cref="EnemyPart.Walk"/> made once: the frame's capture walks it without an iterator.</summary>
    internal EnemyPart[] Segments { get; }
    public AisBrain? Brain { get; }
    public IReadOnlyList<EnemyPart> Turrets => _turrets;
    public IReadOnlyList<EnemyPart> Machines => _machines;
    public Vector3 Position { get; set; }
    /// <summary>Radians; 0 faces −Z.</summary>
    public float Yaw { get; set; }
    /// <summary>The root's place and yaw at the previous tick's end (render interpolation; never simulated).</summary>
    public Vector3 PreviousPosition { get; private set; }
    public float PreviousYaw { get; private set; }

    /// <summary>A tick's start: the pose now becomes the one the frames glide from.</summary>
    public void KeepPose() => (PreviousPosition, PreviousYaw) = (Position, Yaw);

    /// <summary>The root's world transform between the last two ticks (<see cref="Interpolation"/>).</summary>
    public Matrix4x4 DrawnRoot(float alpha)
    {
        if (alpha >= 1f)
        {
            return Root.World;
        }

        var at = Interpolation.Lerp(PreviousPosition, Position, alpha);
        var yaw = at == Position ? Yaw : Interpolation.LerpAngle(PreviousYaw, Yaw, alpha);
        return Matrix4x4.CreateRotationY(yaw) * Matrix4x4.CreateTranslation(at);
    }
    public float Hp { get; private set; }
    public float MaxHp { get; }
    public float MoveSpeed { get; }
    public float TurnRate { get; }
    /// <summary>How far the feet hang below the origin (≤ 0).</summary>
    public float FootOffset { get; }
    public bool IsStationary { get; }
    public bool IsFlying { get; }
    public bool IsPassive { get; }
    public bool Dead { get; private set; }
    /// <summary>Built hidden at an 0xF3 spawn sprite until its chain fires.</summary>
    public bool Hidden { get; private set; }
    /// <summary>A path vehicle (state 11): driven by the behaviour, no AI, no shooting.</summary>
    public bool OnPath { get; set; }
    /// <summary>Hit points 0 on a path vehicle: ObjHit does nothing below 1 HP.</summary>
    public bool Indestructible { get; set; }
    /// <summary>&gt; 0: a dormant trap that goes off when the soldier comes this close.</summary>
    public float DormantDistance { get; private set; }
    /// <summary>The start marker's file offset (-1 none).</summary>
    public int MarkerOffset { get; }
    /// <summary>The act the marker hands its actor (sub+4) and the state bits (sub+5): bit 2 runs it
    /// on death (EnemiesStartMarked 0x12a439). 0 = none; 0xFF once spent.</summary>
    public int DeathAct { get; set; }
    public int DeathState { get; set; }
    /// <summary>The source sprite of an 0xF3 spawn (-1 none).</summary>
    public int SpawnSource { get; init; } = -1;
    /// <summary>Whether the player was perceived on the last tick.</summary>
    public bool Sees { get; private set; }

    /// <summary>The perception counter (DOS actor +0x24): the robot looks when it reaches 0.</summary>
    public int SenseCount => _senseCount;

    /// <summary>The last perception answer (DOS flag 0x100), held between looks.</summary>
    public bool Perceived => _perceived;

    /// <summary>A hit's alert bit (0x8000) not yet spent by a look.</summary>
    public bool Alerted => _alert;
    public bool Blocked => _blocked;
    /// <summary>The body box (centre, half-size) about the origin at yaw 0, all segments included.</summary>
    public Vector3 BoundsCentre { get; private set; }
    public float BodyHeight { get; private set; }
    public float BodySize { get; private set; }
    /// <summary>Counted shots fired (tests and logs).</summary>
    public int ShotsFired { get; private set; }

    public Vector3 Centre => Position + Vector3.Transform(BoundsCentre, Matrix4x4.CreateRotationY(Yaw));
    public float Radius { get; private set; } = HitMin;
    public bool Alive => !Dead && !Hidden;
    public Vector3 Forward => new(-MathF.Sin(Yaw), 0f, -MathF.Cos(Yaw));

    private void AddSegments(EnemyPart parent, int depth)
    {
        if (depth >= 4 || parent.Type == null)
        {
            return;
        }

        foreach (var seg in parent.Type.Segments)
        {
            var ais = _ctx.Ais;
            if (seg.ChildType < 0 || seg.ChildType >= ais.Types.Length)
            {
                continue;
            }

            var t = ais.Types[seg.ChildType];
            var part = new EnemyPart(seg.ChildType, t, _ctx.Models.Get(t.ModelFile, t.Look), parent, new Vector3(seg.X, -seg.Y, -seg.Z),
                (float)MapFile.AngleRadians(seg.Yaw), (float)MapFile.AngleRadians(seg.Pitch), (float)MapFile.AngleRadians(seg.Roll));
            parent.Children.Add(part);
            AddSegments(part, depth + 1);
        }
    }

    /// <summary>Places every segment in the world from the root's position and yaw.</summary>
    public void UpdateTransforms()
    {
        Root.World = Matrix4x4.CreateRotationY(Yaw) * Matrix4x4.CreateTranslation(Position);
        foreach (var c in Root.Children)
        {
            Place(c);
        }

        static void Place(EnemyPart p)
        {
            p.World = p.Local * p.Parent!.World;
            foreach (var c in p.Children)
            {
                Place(c);
            }
        }
    }

    private void FitBounds()
    {
        var inverse = Matrix4x4.CreateTranslation(-Position) * Matrix4x4.CreateRotationY(-Yaw);
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var p in Root.Walk())
        {
            if (p.Model is not { } m)
            {
                continue;
            }

            var toRoot = p.World * inverse;
            foreach (var corner in Corners(m.Bounds.Min, m.Bounds.Max))
            {
                var q = Vector3.Transform(corner, toRoot);
                min = Vector3.Min(min, q);
                max = Vector3.Max(max, q);
            }
        }

        if (min.X > max.X)
        {
            (min, max) = (new Vector3(-30f), new Vector3(30f));
        }

        var size = max - min;
        _halfSize = size * 0.5f;
        BoundsCentre = (min + max) * 0.5f;
        BodyHeight = size.Y;
        BodySize = MathF.Max(size.X, MathF.Max(size.Y, size.Z));
        Radius = MathF.Max(BodySize * 0.5f * HitMargin, HitMin);
    }

    private static IEnumerable<Vector3> Corners(Vector3 a, Vector3 b)
    {
        for (var i = 0; i < 8; i++)
        {
            yield return new Vector3((i & 1) != 0 ? b.X : a.X, (i & 2) != 0 ? b.Y : a.Y, (i & 4) != 0 ? b.Z : a.Z);
        }
    }

    private static int ClampFrame(EnemyPart p, int f) => Math.Clamp(f, 0, Math.Max(0, (p.Model?.Frames ?? 1) - 1));

    // ------------------------------------------------------------------ lifecycle

    /// <summary>Marks the actor as a dormant trap (marker sub+2).</summary>
    public void MakeDormant(float distance) => DormantDistance = MathF.Max(distance, 1f);

    public void HideUntilSpawned() => Hidden = true;

    /// <summary>Gone already (destroyed on an earlier visit): dead, quietly. True when it was alive.</summary>
    public bool Retire()
    {
        if (Dead)
        {
            return false;
        }

        Dead = true;
        DeathAct = 0xFF;
        return true;
    }

    /// <summary>The 0xF3 sprite's chain fired: the robot steps out.</summary>
    public bool SpawnIn()
    {
        if (!Hidden || Dead)
        {
            return false;
        }

        Hidden = false;
        KeepPose();
        return true;
    }

    /// <summary>What a save keeps of this robot (<see cref="Robots.Snapshot"/>); the brain and the
    /// turrets' aim start again on load.</summary>
    internal RobotSnapshot Snapshot() =>
        new(MarkerOffset, SpawnSource, Position, Yaw, Hp, Dead, Hidden, DormantDistance, DeathAct, _initDone, _alert,
            _senseCount, _perceived);

    /// <summary>Puts a <see cref="Snapshot"/> back; true when the robot was alive and is now dead.</summary>
    internal bool Restore(in RobotSnapshot s)
    {
        var died = !Dead && s.Dead;
        Position = s.Position;
        Yaw = s.Yaw;
        Hp = s.Hp;
        Dead = s.Dead;
        Hidden = s.Hidden;
        DormantDistance = s.DormantDistance;
        DeathAct = s.DeathAct;
        _initDone = s.InitDone;
        _alert = s.Alert;
        _senseCount = s.SenseCount & 3;
        _perceived = s.Perceived;
        UpdateTransforms();
        KeepPose();
        return died;
    }

    // ------------------------------------------------------------------ the tick

    /// <summary>The first tick's placement: a walker's feet put on the floor (once; an editor shows
    /// the robots so, without ticking them).</summary>
    public void Settle()
    {
        if (_initDone || Dead || Hidden || OnPath)
        {
            return;
        }

        if (!IsFlying && !IsStationary)
        {
            SnapToGround(true);
        }

        _initDone = true;
    }

    /// <summary>One AI tick of <paramref name="dt"/> seconds against the soldier at <paramref name="player"/> (feet; null: none).</summary>
    public void Tick(float dt, Vector3? player)
    {
        if (Dead || Hidden || OnPath)
        {
            return;
        }

        Settle();
        if (DormantDistance > 0f)
        {
            if (player is { } pp && Vector3.Distance(Position, pp) < DormantDistance)
            {
                Detonate();
            }

            return;
        }

        UpdateTransforms();
        var senses = Sense(player);
        Sees = senses.See;
        if (Improved)
        {
            Remember(senses, player);
        }

        if (Brain != null)
        {
            Brain.Update(dt, senses);
            if (Brain.FrameChanged)
            {
                Root.Frame = ClampFrame(Root, Brain.Frame);
            }
            else if (Brain.Anim == null && Root.Model is { Frames: > 1 } m && (IsFlying || IsPassive || State is 6 or 9))
            {
                CycleStrip(m.Frames, dt);
            }

            foreach (var id in Brain.Sounds)
            {
                _ctx.Sound(id, Position + new Vector3(0f, 40f, 0f));
            }
        }

        if (IsPassive || player is not { } feet)
        {
            return;
        }

        if (State == 6 && IsKamikaze && senses.See
            && Vector3.Distance(Position, feet + new Vector3(0f, 40f, 0f)) <= (Type?.Near ?? 40) + KamikazeReach)
        {
            Detonate();
            return;
        }

        if (State is 7 or 6 or 9 or 13 && !IsStationary)
        {
            Move(dt, senses, feet);
        }

        UpdateTransforms();
        foreach (var seg in _turrets)
        {
            Aim(seg, dt, senses.See, feet);
        }

        foreach (var m in _machines)
        {
            TickMachine(m, dt, senses, feet);
        }

        if (Root.Fire is { } fire && !Root.IsTurret)
        {
            var gate = State == 7 && Brain is { HasScript: true } ? Brain.FiringPose : true;
            if (gate)
            {
                TryFire(Root, fire, dt, feet);
            }
            else
            {
                Root.Cooldown -= dt;
            }
        }
    }

    private void CycleStrip(int frames, float dt)
    {
        _stripT += dt;
        while (_stripT >= 1f / StripFps)
        {
            _stripT -= 1f / StripFps;
            Root.Frame = (Root.Frame + 1) % frames;
        }
    }

    /// <summary>The DOS 11-bit angle of a flat direction (0 = −Z, the actor's yaw frame).</summary>
    public static int DosAngle(Vector3 dir)
    {
        if (dir.X * dir.X + dir.Z * dir.Z < 1f)
        {
            return 0;
        }

        return (int)MathF.Round(MathF.Atan2(-dir.X, -dir.Z) / MathF.Tau * 2048f) & 0x7FF;
    }

    /// <summary>
    /// What the DOS handler perceives (FUN_0013c4da). The actor's counter (+0x24, dealt round-robin
    /// 0..3 at spawn by 0x12a57e, counted down every tick by 0x12a598) makes it look only every fourth
    /// tick and keep the answer (flag 0x100) in between. On a looking tick the alert bit 0x8000 a hit
    /// set (FUN_00139819 → 0x12a280) is spent and counts as seen, with no range or line-of-sight test;
    /// otherwise seen = DOS distance (FUN_0014d794) ≤ 2000 and a clear line. A turret (state 2,
    /// FUN_00137965) looks only while the soldier is within a head's engage range: beyond it the alert
    /// stays pending. Nothing else remembers the shooter.
    /// </summary>
    public AisSenses Sense(Vector3? player)
    {
        _senseCount = _senseCount == 0 ? 3 : _senseCount - 1;
        if (player is not { } p)
        {
            return new AisSenses(false, 1e9f, 0, 0, _blocked);
        }

        var to = p - Position;
        var flat = new Vector3(to.X, 0f, to.Z);
        var dist = flat.Length();
        var angle = DosAngle(flat);
        var facing = (int)MathF.Round(Yaw / MathF.Tau * 2048f) & 0x7FF;
        var see = State == 2 && !InEngageRange(p) ? false : Perceive(p);
        return new AisSenses(see, dist, (angle - facing) & 0x7FF, angle, _blocked);
    }

    private bool Perceive(Vector3 p)
    {
        if (_senseCount != 0)
        {
            return _perceived;
        }

        if (_alert)
        {
            _alert = false;
            return _perceived = true;
        }

        return _perceived = DosDistance(p - Position) <= PerceptionRange && LineOfSight(p);
    }

    /// <summary>FUN_00137965: some head has the soldier within its engage range (dw8).</summary>
    private bool InEngageRange(Vector3 feet)
    {
        if (_turrets.Count == 0)
        {
            return true;
        }

        var aim = feet + new Vector3(0f, AimHeight, 0f);
        foreach (var seg in _turrets)
        {
            if (Vector3.Distance(aim, seg.World.Translation) <= (seg.Type!.Range ?? 800))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>FUN_0014d794: the largest axis plus a quarter of each of the other two.</summary>
    public static float DosDistance(Vector3 d)
    {
        var x = MathF.Abs(d.X);
        var y = MathF.Abs(d.Y);
        var z = MathF.Abs(d.Z);
        var max = MathF.Max(x, MathF.Max(y, z));
        return max + (x + y + z - max) / 4f;
    }

    /// <summary>No level geometry between the actor's eye and the soldier's.</summary>
    public bool LineOfSight(Vector3 playerFeet)
    {
        var from = Position + new Vector3(0f, EyeHeight, 0f);
        var to = playerFeet + new Vector3(0f, AimHeight, 0f) - from;
        var d = to.Length();
        return d < 1f || !_ctx.World.Raycast(from, to / d, d - 1f, out _);
    }

    // ------------------------------------------------------------------ movement

    private static float Approach(float cur, float target, float step)
    {
        var d = Wrap(target - cur);
        return MathF.Abs(d) <= step ? target : cur + MathF.Sign(d) * step;
    }

    private static float Wrap(float a)
    {
        a %= MathF.Tau;
        if (a > MathF.PI) a -= MathF.Tau;
        if (a < -MathF.PI) a += MathF.Tau;
        return a;
    }

    private bool Terminator => TypeIndex is >= 33 and <= 38;

    private void Move(float dt, in AisSenses senses, Vector3 player)
    {
        if (Improved)
        {
            MoveImproved(dt, senses, player);
            return;
        }

        var see = senses.See;
        Vector3 target;
        if (see)
        {
            target = player;
            _wanderT = 0f;
        }
        else
        {
            target = Wander(dt);
        }

        var to = target - Position;
        to.Y = 0f;
        var dist = to.Length();
        var speed = MoveSpeed;
        var wantYaw = dist > 1f ? MathF.Atan2(-to.X, -to.Z) : Yaw;
        if (State == 13 && Brain != null)
        {
            if (Brain.HasVar(44))
            {
                wantYaw = Brain.Var(44, 0) / 2048f * MathF.Tau;
            }

            speed = Brain.Var(48, Type?.Speed ?? 0);
        }
        else if (State == 9 && Brain != null)
        {
            speed = Brain.Var(56, Type?.ForwardSpeed ?? 400);
        }

        Yaw = Wrap(Approach(Yaw, wantYaw, TurnRate * dt));
        var moving = true;
        if (State == 7 && Brain != null)
        {
            moving = (Brain.Looping || (Brain.OneShotWalker && Brain.FiringPose && (Brain.AnimFlags & AisBrain.AnimDone) == 0))
                && !Brain.FreezeAnim;
        }

        var near = Type?.Near ?? 100;
        if ((State == 13 && speed < 0f) || State == 9)
        {
            moving = true;                       // hovers never park: they overfly
        }
        else if (dist <= near)
        {
            moving = false;
        }

        _blocked = false;
        if (moving && MathF.Abs(speed) > 0.5f)
        {
            var fwd = Forward;
            var step = fwd * speed * dt;
            var groundBound = State != 9 && !IsFlying;
            if (PathBlocked(step) || (groundBound && (TooSteep(fwd) || DropAhead(fwd))))
            {
                _blocked = true;
                if (State == 9)
                {
                    Yaw = Wrap(Yaw + 1.2f * dt);
                }
                else if (!see)
                {
                    _wanderT = 0f;
                }
            }
            else
            {
                Position += step;
            }
        }

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
            var dy = wantY - Position.Y;
            var urgent = Position.Y < floor + minAlt * 0.5f;
            var vmax = (dy > 0f ? FlyerClimbSpeed * (urgent ? 3f : 1f) : FlyerSinkSpeed) * dt;
            Position += new Vector3(0f, Math.Clamp(dy, -vmax, vmax), 0f);
        }
        else if (IsFlying && !(State == 6 && IsKamikaze && see))
        {
            var fl = MathF.Max(SurfaceAt(Position), SurfaceAt(Position + Forward * FlyerLookahead));
            var need = fl + FlyerMinAlt - Position.Y;
            if (need > 0f)
            {
                Position += new Vector3(0f, MathF.Min(need, FlyerClimbSpeed * dt), 0f);
            }
        }
        else if (State == 6 && IsFlying && IsKamikaze && see)
        {
            var dy = player.Y + 40f - Position.Y;
            Position += new Vector3(0f, Math.Clamp(dy, -speed * dt, speed * dt), 0f);
        }
        else if (!IsFlying)
        {
            SnapToGround(false);
        }
    }

    private Vector3 Wander(float dt)
    {
        _wanderT -= dt;
        if (_wanderT <= 0f || _wanderTarget == Vector3.Zero)
        {
            var rng = _ctx.Rng;
            _wanderT = 4f + 5f * rng.NextSingle();
            var a = rng.NextSingle() * MathF.Tau;
            _wanderTarget = Position + new Vector3(MathF.Cos(a), 0f, MathF.Sin(a)) * (300f + (WanderRadius - 300f) * rng.NextSingle());
        }

        return _wanderTarget;
    }

    /// <summary>A segment cast: the first surface from <paramref name="from"/> to <paramref name="to"/>.</summary>
    private bool Cast(Vector3 from, Vector3 to, out RetroEng.Physics.Hit hit)
    {
        var d = to - from;
        var len = d.Length();
        hit = default;
        return len > 1e-3f && _ctx.World.Raycast(from, d / len, len, out hit);
    }

    /// <summary>Highest surface under <paramref name="at"/>; its own Y when none.</summary>
    public float SurfaceAt(Vector3 at)
    {
        var from = at + new Vector3(0f, SurfaceUp, 0f);
        var ray = at - new Vector3(0f, SurfaceDown, 0f) - from;
        var len = ray.Length();
        var dir = ray / len;
        dir /= dir.Length();

        // The engine's ray gathers the column again every 512 units (51 times for these 26 000):
        // gather it once and take the nearest crossing as that ray computes it (same answer, bit for bit).
        _column.Clear();
        _ctx.World.Gather(new RetroEng.Physics.Box(Vector3.Min(from, from + ray), Vector3.Max(from, from + ray)).Grow(1f), _column);
        var best = float.MaxValue;
        foreach (var tri in _column)
        {
            if (RetroEng.Physics.Intersect.Ray(from, dir, in tri, out var d) && d <= len && d < best)
            {
                best = d;
            }
        }

        return best <= len ? (from + dir * best).Y : at.Y;
    }

    private bool PathBlocked(Vector3 step)
    {
        var from = Position + new Vector3(0f, 60f, 0f);
        var len = step.Length();
        return len > 1e-4f && Cast(from, from + step / len * (len + 40f), out _);
    }

    /// <summary>A drop deeper than the type's limit <paramref name="look"/> ahead.</summary>
    private bool DropAhead(Vector3 fwd, float look = DropLook)
    {
        var ahead = Position + fwd * look;
        var maxDrop = Terminator ? MaxDropTerminator : MaxDropOther;
        return !Cast(ahead + new Vector3(0f, 60f, 0f), ahead + new Vector3(0f, FootOffset - maxDrop - 60f, 0f), out _);
    }

    private bool TooSteep(Vector3 fwd)
    {
        var feetY = Position.Y + FootOffset;
        var step = Terminator ? StepTerminator : StepOther;
        var near = Position + fwd * 40f;
        var top = feetY + step + 1f;
        if (Cast(new Vector3(Position.X, top, Position.Z), new Vector3(near.X, top, near.Z), out _))
        {
            return true;
        }

        if (Cast(new Vector3(near.X, top, near.Z), new Vector3(near.X, feetY - 400f, near.Z), out var hn) && hn.Point.Y - feetY > step)
        {
            return true;
        }

        var limit = Terminator ? SlopeTerminator : SlopeOther;
        var maxRise = 120f * MathF.Tan(limit);
        var ahead = Position + fwd * 120f;
        if (!Cast(new Vector3(ahead.X, feetY + maxRise + 1f, ahead.Z), new Vector3(ahead.X, feetY - 400f, ahead.Z), out var hit))
        {
            return false;
        }

        var rise = hit.Point.Y - feetY;
        return rise > 0f && MathF.Atan2(rise, 120f) > limit;
    }

    /// <summary>Puts the feet on the floor below (one step up, one ledge down; at placement 60 up and
    /// 400 down, trying the floor plates around a seam, popping out from under a plate).</summary>
    public bool SnapToGround(bool init)
    {
        var feetY = Position.Y + FootOffset;
        var up = init ? 60f : Terminator ? StepTerminator : StepOther;
        var down = init ? 400f : Terminator ? MaxDropTerminator : MaxDropOther;
        var found = FloorRay(Position, feetY + up, feetY - down, false, out var hit);
        if (!found && init)
        {
            foreach (var r in new[] { 40f, 80f })
            {
                for (var i = 0; i < 8 && !found; i++)
                {
                    var a = MathF.Tau * i / 8f;
                    var off = new Vector3(MathF.Cos(a) * r, 0f, MathF.Sin(a) * r);
                    if (FloorRay(Position + off, feetY + up, feetY - down, false, out hit))
                    {
                        found = true;
                        Position += off;
                    }
                }

                if (found)
                {
                    break;
                }
            }
        }

        if (!found)
        {
            if (FloorRay(Position, feetY + (init ? 240f : SinkRecover), feetY + up, true, out var pop))
            {
                Position = Position with { Y = pop.Point.Y - FootOffset };
                return true;
            }

            return init;
        }

        Position = Position with { Y = hit.Point.Y - FootOffset };
        return true;
    }

    private bool FloorRay(Vector3 at, float top, float bottom, bool floorOnly, out RetroEng.Physics.Hit hit)
    {
        if (!Cast(new Vector3(at.X, top, at.Z), new Vector3(at.X, bottom, at.Z), out hit))
        {
            return false;
        }

        return !floorOnly || hit.Normal.Y >= 0.5f;
    }

    // ------------------------------------------------------------------ turrets, machines, fire

    /// <summary>
    /// DOS turret state 2 (FUN_00137965), once per tick: the head tracks the soldier while he is
    /// inside the engage range and perceived, and turns toward <see cref="TurretRest"/> otherwise —
    /// it keeps turning at its own rate and clamps onto the target, never sweeps; it fires only when
    /// engaged.
    /// </summary>
    private void Aim(EnemyPart seg, float dt, bool see, Vector3 player)
    {
        var t = seg.Type!;
        var origin = seg.World.Translation;
        var engaged = false;
        var toG = TurretRest;
        if (see)
        {
            var toP = player + new Vector3(0f, AimHeight, 0f) - origin;
            if (toP.Length() <= (t.Range ?? 800))
            {
                toG = toP;
                engaged = true;
            }
        }

        var parentWorld = seg.Parent?.World ?? Matrix4x4.CreateRotationY(Yaw);
        if (seg == Root)
        {
            parentWorld = Matrix4x4.Identity;
        }

        Matrix4x4.Invert(parentWorld with { M41 = 0f, M42 = 0f, M43 = 0f }, out var inv);
        var d = Vector3.TransformNormal(toG, inv);
        var rate = (t.Turn is > 0 and var tr ? tr : 512) / 2048f * MathF.Tau * dt;
        var amin = (t.AngleMin ?? -2048) / 2048f * MathF.Tau;
        var amax = (t.AngleMax ?? 2048) / 2048f * MathF.Tau;
        if ((t.Axis ?? 1) == 1)
        {
            var want = MathF.Atan2(-d.X, -d.Z);
            var rest = seg == Root ? _rootRestYaw : seg.RestYaw;
            var rel = Wrap(want - rest);
            if (amax - amin < MathF.Tau - 0.01f)
            {
                rel = Math.Clamp(rel, amin, amax);
            }

            if (seg == Root)
            {
                Yaw = Wrap(Approach(Yaw, rest + rel, rate));
            }
            else
            {
                seg.Yaw = Wrap(Approach(seg.Yaw, rest + rel, rate));
            }
        }
        else
        {
            // DOS pitch is positive downward; +X rotation raises the nose here.
            var want = MathF.Atan2(d.Y, MathF.Sqrt(d.X * d.X + d.Z * d.Z));
            var lo = -amax;
            var hi = -amin;
            want = Math.Clamp(want, MathF.Min(lo, hi), MathF.Max(lo, hi));
            seg.Pitch = Approach(seg.Pitch, want, rate);
        }

        UpdateTransforms();
        if (!engaged)
        {
            seg.Cooldown -= dt;
            return;
        }

        if (seg.Fire is { } fire)
        {
            TryFire(seg, fire, dt, player);
        }
    }

    /// <summary>A machine segment: its script drives its frames; the posed claw hurts the soldier
    /// it touches (DOS 0x122854: 4 points per probe, no difficulty factor).</summary>
    private void TickMachine(EnemyPart m, float dt, in AisSenses senses, Vector3 player)
    {
        var brain = m.Brain!;
        brain.Update(dt, senses);
        if (brain.FrameChanged)
        {
            m.Frame = ClampFrame(m, brain.Frame);
        }

        foreach (var id in brain.Sounds)
        {
            _ctx.Sound(id, m.World.Translation);
        }

        if (m.Model is not { } model)
        {
            return;
        }

        var tip = Vector3.Transform(model.Tips[Math.Clamp(m.Frame, 0, model.Tips.Length - 1)], m.World);
        if (Vector3.Distance(tip, player + new Vector3(0f, 40f, 0f)) <= MachineMargin)
        {
            _ctx.HurtPlayer(MachineHitDamage, DamageKind.Melee, tip);
        }
    }

    /// <summary>Fires from <paramref name="part"/> when the soldier is in range, in the aim cone and
    /// (a walker's gun, not a turret's) in sight, paced by the rate and the difficulty's fire multiplier.</summary>
    private void TryFire(EnemyPart part, AisFire fire, float dt, Vector3 player)
    {
        part.Cooldown -= dt;
        if (part.Cooldown > 0f || _ctx.HoldFire)
        {
            return;
        }

        var aim = player + new Vector3(0f, AimHeight, 0f);
        var muzzle = Vector3.Transform(new Vector3(fire.MuzzleX, -fire.MuzzleY, -fire.MuzzleZ), part.World);
        var to = aim - muzzle;
        var dist = to.Length();
        if (dist > fire.Range || dist < 1f)
        {
            return;
        }

        var cone = AimCone;
        if (fire.AimGate > 4096)
        {
            cone = MathF.Acos(Math.Clamp(fire.AimGate / 65536f, -1f, 1f));
        }
        else if (fire.AimGate > 0)
        {
            cone = fire.AimGate / 2048f * MathF.Tau;
        }

        var fwd = part.Forward;
        var cos = Vector3.Dot(fwd, to / dist);
        // DOS turret fire (0x137d10 from FUN_00137965) asks range, aim cone and rate, never sight.
        if (MathF.Acos(Math.Clamp(cos, -1f, 1f)) > cone || !part.IsTurret && !LineOfSight(player))
        {
            return;
        }

        // IMPROVED: no shot into a wall, turrets included (the eye's line and the muzzle's).
        if (Improved && !ClearShot(part, muzzle, aim, player))
        {
            return;
        }

        var rate = MathF.Max(fire.Rate, 1f);
        var rng = _ctx.Rng;
        part.Cooldown = FireRateDiv / rate * (0.8f + 0.5f * rng.NextSingle()) / MathF.Max(_ctx.Difficulty.EnemyFire, 0.01f);
        var dir = Vector3.Normalize(to / dist + new Vector3(rng.NextSingle() * 2f - 1f, rng.NextSingle() * 2f - 1f, rng.NextSingle() * 2f - 1f) * AimSpread);
        ShotsFired++;
        _ctx.Shoot(this, fire.Ammo, muzzle, dir, MathF.Max(MathF.Abs(fire.Speed), 200f) * BoltSpeedScale);
    }

    /// <summary>No fire parameters of its own and no armed segment: a flying mine.</summary>
    public bool IsKamikaze
    {
        get
        {
            if (_kamikaze < 0)
            {
                _kamikaze = Root.Walk().Any(p => p.Fire != null) ? 0 : 1;
            }

            return _kamikaze == 1;
        }
    }

    // ------------------------------------------------------------------ damage and death

    public void TakeDamage(in Damage damage)
    {
        if (Indestructible || Dead || Hidden)
        {
            return;
        }

        Hp -= MathF.Max(damage.Points, 0f);
        _ctx.HitSound(Position + new Vector3(0f, 40f, 0f));
        if (DormantDistance > 0f)
        {
            if (Hp <= 0f)
            {
                Detonate();
            }

            return;
        }

        // FUN_00139819 (0x1398e6) sets the alert bit 0x8000 on the actor and its children: the next
        // perception test (Sense) counts as seen, and that answer holds until the test after it.
        _alert = true;
        if (Hp <= 0f)
        {
            Die();
        }
    }

    /// <summary>Raised once as the machine is destroyed, when its state byte has bit 2 and it has an act.</summary>
    private void FireDeathAct()
    {
        if (DeathAct <= 0 || DeathAct >= 0xFE || (DeathState & 4) == 0)
        {
            return;
        }

        var act = DeathAct;
        DeathAct = 0xFF;
        _ctx.RaiseDeathAct(act, MarkerOffset, this);
    }

    /// <summary>EnemyKill: instant explosion, the act on death, the death blast 0.375 s later.</summary>
    public void Die()
    {
        if (Dead)
        {
            return;
        }

        Dead = true;
        FireDeathAct();
        _ctx.Destroyed(this, false);
    }

    /// <summary>A dormant trap or flying mine: ObjHit on itself (the bang 0x26), then the ordinary death blast.</summary>
    public void Detonate()
    {
        if (Dead)
        {
            return;
        }

        Dead = true;
        FireDeathAct();
        _ctx.Destroyed(this, true);
    }

    /// <summary>The death blast's points at distance <paramref name="d"/> (DOS fixed point; 0: none).</summary>
    public static int DeathBlastDamage(float d)
    {
        var k = (int)MathF.Floor((DeathBlast + DeathBlastPad - d) * 256f / DeathBlast);
        var dmg = (k * DeathBlast) >> 8;
        return dmg >= DeathBlastMin ? dmg : 0;
    }
}
