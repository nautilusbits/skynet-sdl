using System.Buffers.Binary;
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
    /// <summary>A hover keeps its origin this far under a ceiling: the eye (<see cref="EyeHeight"/>) and a margin below it.</summary>
    private const float CeilingGap = EyeHeight + 20f;
    /// <summary>A segment looks from 0xF over its record (0x13c4da).</summary>
    private const float SegmentEye = 15f;
    public const float KamikazeReach = 40f;
    public const float MachineMargin = 60f;
    public const float MachineHitDamage = 4f;
    /// <summary>The rotor/engine strip rate of a type without an animation block.</summary>
    public const float StripFps = 12f;
    public const float DefaultTurn = 2.6f;
    /// <summary>A turret's rest pose: DOS aims it at (x, y − 200, z − 300), a world offset.</summary>
    public static readonly Vector3 TurretRest = new(0f, 200f, 300f);
    public const float HitMargin = 1.18f, HitMin = 45f;
    private const int MotionKind = 27, BobRate = 28, BobStep = 29, BobKind = 1;
    /// <summary>The frames a second the bob's per-frame step counts at (DOS's 35, as WaterBreath's).</summary>
    private const float BobFps = 35f;
    /// <summary>The death blast (EnemyKill 0x129a28, value 75 for every robot) strikes this long after.</summary>
    public const float DeathBlastDelay = 0.375f;
    public const int DeathBlast = 75, DeathBlastPad = 40, DeathBlastMin = 37;
    public const int DeathEffectBank = 358;
    /// <summary>A dormant robot's teleport-in: its sound (0x26) and effect (TEXTURE.367).</summary>
    public const int TrapSound = AisData.TrapSound, TrapEffectBank = AisData.TrapEffectBank;

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

    public static readonly HashSet<string> FlyingNames = new(StringComparer.OrdinalIgnoreCase) { "globe", "drone", "scout", "hk_ftr", "hk_bmbr", "flencer" };
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
    /// <summary>The bob's phase (radians) and its drop under the place now and at the last tick's end.</summary>
    private float _bobPhase, _bob, _previousBob;
    private float _stripT;
    private int _kamikaze = -1;
    private readonly float _rootRestYaw;
    /// <summary>The root's pitch and roll after its yaw (<see cref="Tilt"/>).</summary>
    private readonly Matrix4x4 _rootTilt;
    private readonly List<RetroEng.Physics.Triangle> _column = [];

    public Enemy(Robots ctx, int type, Vector3 position, float yaw, int markerOffset = -1)
    {
        _ctx = ctx;
        var ais = ctx.Ais;
        TypeIndex = type;
        Type = type >= 0 && type < ais.Types.Length ? ais.Types[type] : null;
        Position = position;
        Yaw = _rootRestYaw = yaw;
        _rootTilt = Tilt(Type);
        MarkerOffset = markerOffset;
        _senseCount = ctx.NextSensePhase();
        Root = new EnemyPart(type, Type, ctx.Models.Get(Type?.ModelFile ?? "", Type?.Look ?? ""), null, Vector3.Zero, 0f, 0f, 0f);
        AddSegments(Root, 0);
        Segments = [.. Root.Walk()];
        foreach (var p in Root.Walk())
        {
            p.Robot = this;
            if (p != Root)
            {
                p.SenseCount = ctx.NextSensePhase();   // 0x129a9d deals each segment's actor its own (0x12a57e)
            }

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
    public void KeepPose() => (PreviousPosition, PreviousYaw, _previousBob) = (Position, Yaw, _bob);

    /// <summary>The root's world transform between the last two ticks (<see cref="Interpolation"/>).</summary>
    public Matrix4x4 DrawnRoot(float alpha)
    {
        if (alpha >= 1f)
        {
            return Root.World;
        }

        var at = Interpolation.Lerp(PreviousPosition, Position, alpha);
        var yaw = at == Position ? Yaw : Interpolation.LerpAngle(PreviousYaw, Yaw, alpha);
        var bob = new Vector3(0f, float.Lerp(_previousBob, _bob, alpha), 0f);
        return Matrix4x4.CreateRotationY(yaw) * _rootTilt * Matrix4x4.CreateTranslation(at + bob);
    }

    /// <summary>
    /// The type's pitch and roll (params 1 and 3; 0x129a9d copies params 1-3 to the actor's
    /// pitch, yaw, roll), turned after the yaw as FUN_0014e100 does. The hung guns (h &lt; 0:
    /// smltrrt 8/10/12/29, hvytrrt2 21, hvytrrt 22-24) carry roll 1024: upside down.
    /// </summary>
    private static Matrix4x4 Tilt(EnemyType? type)
    {
        if (type?.Params is not { Length: > 3 } p)
        {
            return Matrix4x4.Identity;
        }

        return Matrix4x4.CreateRotationX((float)MapFile.AngleRadians(p[1]))
            * Matrix4x4.CreateRotationZ(-(float)MapFile.AngleRadians(p[3]));
    }

    /// <summary>The root segment's hit points: only they kill the whole robot.</summary>
    public float Hp { get => Root.Hp; private set => Root.Hp = value; }
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
    /// <summary>&gt; 0: dormant (marker sub+2): unseen and untouchable until the soldier comes this
    /// close, then it teleports in (state 12, 0x14305e).</summary>
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

    public Vector3 Centre => Position + new Vector3(0f, _bob, 0f) + Vector3.Transform(BoundsCentre, Matrix4x4.CreateRotationY(Yaw));
    public float Radius { get; private set; } = HitMin;
    /// <summary>Drawn and struck: a dormant robot is neither (0x136dee, 0x13aa1e).</summary>
    public bool Alive => !Dead && !Hidden && DormantDistance <= 0f;
    /// <summary>A blast pads its distance with the root model's radius (.3D +0x0C, 1/256 units;
    /// RAPTOR 57.5) and measures it to the origin (FUN_00124886).</summary>
    public float BlastPad => Root.Model is { Model.Header: { Length: >= ModelRadiusEnd } h }
        ? BinaryPrimitives.ReadInt32LittleEndian(h.AsSpan()[ModelRadiusField..]) / 256f
        : Blast.Pad;
    public Vector3 BlastOrigin => Position;
    private const int ModelRadiusField = 0x0C, ModelRadiusEnd = ModelRadiusField + 4;
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
        Root.World = Matrix4x4.CreateRotationY(Yaw) * _rootTilt * Matrix4x4.CreateTranslation(Position + new Vector3(0f, _bob, 0f));
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

    /// <summary>Makes the actor dormant (marker sub+2, 0x143000).</summary>
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
            _senseCount, _perceived) { Parts = PartsHp(), Gone = PartsGone(), Senses = PartsSenses(), Node = _node, NodeStamp = _nodeStamp };

    private const int SensePerceived = 4, SenseAlerted = 8, SenseSwapped = 16;

    /// <summary>Each segment's counter, last answer, pending alert and swapped muzzle, a byte each
    /// (the root's: its muzzle alone).</summary>
    private byte[] PartsSenses() =>
        [.. Segments.Select(p => (byte)((p.MuzzleSwapped ? SenseSwapped : 0) | (p == Root ? 0
            : (p.SenseCount & 3) | (p.Perceived ? SensePerceived : 0) | (p.Alerted ? SenseAlerted : 0))))];

    /// <summary>Every segment's hit points (root first, <see cref="Segments"/>' order); null while
    /// every segment but the root is whole.</summary>
    private float[]? PartsHp()
    {
        for (var i = 1; i < Segments.Length; i++)
        {
            if (Segments[i].Gone || Segments[i].Hp != Segments[i].FullHp)
            {
                return [.. Segments.Select(p => p.Hp)];
            }
        }

        return null;
    }

    /// <summary>The shot-off segments, a bit each in <see cref="Segments"/>' order.</summary>
    internal ulong GoneBits => PartsGone();

    /// <summary>Marks the segments of <paramref name="bits"/> shot off, without a part or a bang.</summary>
    internal void ShootOff(ulong bits)
    {
        for (var i = 1; i < Segments.Length && i < 64; i++)
        {
            Segments[i].Gone |= (bits & 1UL << i) != 0;
        }
    }

    private ulong PartsGone()
    {
        var bits = 0UL;
        for (var i = 0; i < Segments.Length && i < 64; i++)
        {
            bits |= Segments[i].Gone ? 1UL << i : 0UL;
        }

        return bits;
    }

    /// <summary>A snapshot's segments put back; none kept (an older save): every one whole.</summary>
    private void RestoreParts(in RobotSnapshot s)
    {
        Root.MuzzleSwapped = s.Senses is { Length: > 0 } all && all.Length == Segments.Length && (all[0] & SenseSwapped) != 0;
        for (var i = 1; i < Segments.Length; i++)
        {
            var p = Segments[i];
            p.Hp = s.Parts is { } hp && hp.Length == Segments.Length ? hp[i] : p.FullHp;
            p.Gone = i < 64 && (s.Gone & 1UL << i) != 0;
            if (s.Senses is not { } senses || senses.Length != Segments.Length)
            {
                (p.Perceived, p.Alerted, p.MuzzleSwapped) = (false, false, false);
                continue;
            }

            (p.SenseCount, p.Perceived, p.Alerted) = (senses[i] & 3, (senses[i] & SensePerceived) != 0, (senses[i] & SenseAlerted) != 0);
            p.MuzzleSwapped = (senses[i] & SenseSwapped) != 0;
        }
    }

    /// <summary>Puts a <see cref="Snapshot"/> back; true when the robot was alive and is now dead.</summary>
    internal bool Restore(in RobotSnapshot s)
    {
        var died = !Dead && s.Dead;
        Position = s.Position;
        Yaw = s.Yaw;
        Hp = s.Hp;
        RestoreParts(s);
        Dead = s.Dead;
        Hidden = s.Hidden;
        DormantDistance = s.DormantDistance;
        DeathAct = s.DeathAct;
        _initDone = s.InitDone;
        _alert = s.Alert;
        _senseCount = s.SenseCount & 3;
        _perceived = s.Perceived;
        (_node, _nodeStamp) = (s.Node is > 0 and < PlayerTrail.Length ? s.Node : 0, s.NodeStamp);
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
            // State 12 (0x14305e): within sub+2 it wakes as its own type; till then nothing runs.
            if (player is { } pp && Vector3.Distance(Position, pp) <= DormantDistance)
            {
                DormantDistance = 0f;
                _ctx.Woke(this, pp);
            }

            return;
        }

        Bob(dt);
        UpdateTransforms();
        var senses = Sense(player);
        for (var i = 1; i < Segments.Length; i++)
        {
            // 0x129d71 counts every segment's actor down each tick (0x12a599).
            Segments[i].SenseCount = Segments[i].SenseCount == 0 ? 3 : Segments[i].SenseCount - 1;
        }
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
            if (!seg.Gone)
            {
                Aim(seg, dt, seg == Root ? senses.See : Looks(seg, feet), feet);
            }
        }

        foreach (var m in _machines)
        {
            if (!m.Gone)
            {
                TickMachine(m, dt, senses, feet);
            }
        }

        if (Root.Fire is { } fire && !Root.IsTurret)
        {
            // A walker or hover fires in a firing pose (frame flags 0x100: 0x13c899, 0x1377ad); a
            // hover without a script keeps its table's flags (dw26: drone, scout 0x101).
            var gate = State switch
            {
                7 when Brain is { HasScript: true } => Brain.FiringPose,
                6 when Brain is { HasScript: true } => Brain.FiringPose,
                6 => Type?.Params is { Length: > HoverFlags } p && (p[HoverFlags] & AisAnimBlock.FiringPose) != 0,
                _ => true,
            };
            if (gate)
            {
                TryFire(Root, fire, dt, feet);
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

    /// <summary>FUN_00137965 on the root's own actor: the soldier within its engage range (dw8).</summary>
    private bool InEngageRange(Vector3 feet) => InRange(Root, feet);

    private static bool InRange(EnemyPart seg, Vector3 feet) =>
        Vector3.Distance(feet + new Vector3(0f, AimHeight, 0f), seg.World.Translation) <= (seg.Type?.Range ?? 800);

    /// <summary>A turret segment's own look (FUN_00137965 → 0x13c4da on its actor): within its engage
    /// range, on its own counter, its alert counting as seen; else from its record, 15 over it, to
    /// the soldier. Beyond the range its alert waits.</summary>
    private bool Looks(EnemyPart seg, Vector3 feet)
    {
        if (!InRange(seg, feet))
        {
            return false;
        }

        if (seg.SenseCount != 0)
        {
            return seg.Perceived;
        }

        if (seg.Alerted)
        {
            seg.Alerted = false;
            return seg.Perceived = true;
        }

        var from = seg.World.Translation + new Vector3(0f, SegmentEye, 0f);
        var to = feet + new Vector3(0f, AimHeight, 0f) - from;
        var d = to.Length();
        var clear = d < 1f || !_ctx.World.Raycast(from, to / d, d - 1f, out _);
        return seg.Perceived = DosDistance(feet - seg.World.Translation) <= PerceptionRange && clear;
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
        var from = Position + Vector3.TransformNormal(new Vector3(0f, EyeHeight, 0f), _rootTilt);   // a hung gun's eye is under it
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
        if (State == 6)
        {
            MoveHover(dt, see, player);
            return;
        }

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
            var fwd = MineWay(see, to);
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
            var need = HoverNeed();
            Position += new Vector3(0f, Math.Clamp(need, -FlyerSinkSpeed * dt, FlyerClimbSpeed * dt), 0f);
        }
        else if (!IsFlying)
        {
            SnapToGround(false);
        }
    }

    /// <summary>A hover's motion kind (dw27) runs after its move (v1.01 0x137797: 0x13cf00, table
    /// 0x5c8fc). Kind 1, the bob (0x13cf19): phase += dw28·dt (2048 a turn), then
    /// y += sin(phase)·dw29 every frame, DOS Y down. Summed over <see cref="BobFps"/> frames a
    /// second that sinks the drone 0..2·dw29·fps/ω under its place: 45 every 2 s.</summary>
    private void Bob(float dt)
    {
        if (State != 6 || Type?.Params is not { Length: > BobStep } p || p[MotionKind] != BobKind || p[BobRate] == 0)
        {
            return;
        }

        var w = p[BobRate] / 2048f * MathF.Tau;
        _bobPhase = (_bobPhase + w * dt) % MathF.Tau;
        _bob = -p[BobStep] * BobFps * (1f - MathF.Cos(_bobPhase)) / w;
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

    /// <summary>The vertical step a hover (globe, drone, scout) wants: up to <see cref="FlyerMinAlt"/>
    /// over what lies under it and ahead. Under open sky that is the highest surface (roofs); under a
    /// ceiling it is the floor, and the hover keeps <see cref="CeilingGap"/> under the ceiling
    /// (negative: sink away from it). Indoors the highest surface is the roof, which lifted it into
    /// the ceiling and out of sight for good (#23).</summary>
    private float HoverNeed()
    {
        var ahead = Position + Forward * FlyerLookahead;
        var up = new Vector3(0f, 1f, 0f);
        if (!Cast(Position + up, Position + up * (FlyerMinAlt + CeilingGap), out var roof))
        {
            return MathF.Max(MathF.Max(SurfaceAt(Position), SurfaceAt(ahead)) + FlyerMinAlt - Position.Y, 0f);
        }

        var floor = MathF.Max(FloorUnder(Position), FloorUnder(ahead));
        var climb = MathF.Max(floor + FlyerMinAlt - Position.Y, 0f);
        return MathF.Min(climb, roof.Point.Y - CeilingGap - Position.Y);
    }

    /// <summary>The flat heading of a step: a flying mine in sight goes straight along
    /// <paramref name="to"/>, the soldier's way, as DOS moves a hover (0x137545; its turn is only its
    /// look). Steered by its turn rate it circles him once it overshoots (#23). Others: <see cref="Forward"/>.</summary>
    private Vector3 MineWay(bool see, Vector3 to)
    {
        var flat = to with { Y = 0f };
        if (!Homes(see) || flat.LengthSquared() < 1f)
        {
            return Forward;
        }

        return Vector3.Normalize(flat);
    }

    /// <summary>
    /// CLASSIC hover (state 6, v1.01 0x137545). More than dw6 from the soldier's eye (0x137576) it
    /// flies straight, in 3D, dw4 u/s, at its point on his trail (<see cref="PlayerTrail"/>), lifted
    /// by its shape's height: none held, it takes the newest once it perceives him, else stands. Within
    /// 15 of the point it takes the next (0x13b817) and flies at that one's own place. Blocked, it backs
    /// 3 points (0x13b7d8; none: it lets go). Outdoors dw10 caps its height over the ground
    /// (0x137683). It always turns to face him (dw7). No ground test.
    /// Not ported: the slide along walls (0x13b35e) and its contact codes (0x1376f5); when no next
    /// point exists DOS flies at a stale register (0x13b817 fails with eax/ebx/edx clobbered), the
    /// port keeps its point.
    /// </summary>
    private void MoveHover(float dt, bool see, Vector3 player)
    {
        var eye = player + new Vector3(0f, DosEye, 0f);
        var to = eye - Position;
        Yaw = Wrap(Approach(Yaw, MathF.Atan2(-to.X, -to.Z), TurnRate * dt));
        _blocked = false;
        if (to.Length() > (Type?.Near ?? 0) && !StepHover(dt, see))
        {
            return;
        }

        _nodeStamp = _node != 0 ? _ctx.Trail.Stamp(_node) : 0;   // 0x137738: what its point holds now
    }

    /// <summary>One hover step; false when it holds no point and stands (no stamp refresh).</summary>
    private bool StepHover(float dt, bool see)
    {
        var trail = _ctx.Trail;
        if (_node == 0)
        {
            if (!see || !trail.Ready || trail.Head == 0)
            {
                return false;
            }

            (_node, _nodeStamp) = (trail.Head, trail.Stamp(trail.Head));
        }

        var target = trail.At(_node) + new Vector3(0f, Type?.ShapeHeight ?? 0, 0f);
        if (Vector3.Distance(target, Position) <= TrailReach && trail.Ahead(_node, _nodeStamp, 1) is >= 0 and var next)
        {
            (_node, target) = (next, trail.At(next));
        }

        var way = target - Position;
        var step = way.LengthSquared() > 1e-6f ? Vector3.Normalize(way) * (MoveSpeed * dt) : Vector3.Zero;
        if (PathBlocked(step))
        {
            _blocked = true;
            _node = _node == 0 ? trail.Head : Math.Max(trail.Back(_node, _nodeStamp, 3), 0);
            _nodeStamp = trail.Stamp(_node);
            return true;
        }

        Position += step;
        if (_ctx.Outdoor && Type?.Params is { Length: > HoverCeiling } p && p[HoverCeiling] > 0)
        {
            Position = Position with { Y = MathF.Min(Position.Y, SurfaceAt(Position) + p[HoverCeiling]) };
        }

        return true;
    }

    private const float TrailReach = 15f;
    private const int HoverCeiling = 10;
    private int _node, _nodeStamp;

    /// <summary>A flying mine with the soldier in sight: it homes on him.</summary>
    private bool Homes(bool see) => see && State == 6 && IsFlying && IsKamikaze;

    /// <summary>The first surface below <paramref name="at"/>; −∞ when none.</summary>
    private float FloorUnder(Vector3 at) =>
        Cast(at + new Vector3(0f, 1f, 0f), at - new Vector3(0f, SurfaceDown, 0f), out var hit) ? hit.Point.Y : float.NegativeInfinity;

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
            parentWorld = _rootTilt;    // the yaw turns inside the tilt: a hung gun's is mirrored
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

    /// <summary>DOS fire (0x137d10), once a DOS frame: the soldier within range of the segment's
    /// origin and in its aim cosine (or within 140 and 45°, 0x137d3a), a walker or hover only while it
    /// perceives him (its callers test 0x13c59e), then rand &amp; 0x3ff under rate × the difficulty's
    /// fire factor (0x13c17a). The bolt leaves the muzzle (its X swapped each shot when the speed is
    /// negative) r·17/16 out along the line to the target (0x13c055) at |speed| u/s, no spread.</summary>
    private void TryFire(EnemyPart part, AisFire fire, float dt, Vector3 player)
    {
        if (_ctx.HoldFire || !_ctx.FrameDue)
        {
            return;
        }

        // The target: the eye (0xd49b4); a walker's 18 under it (0x13c8ac: [0x8de9] / 2).
        var aim = player + new Vector3(0f, State == 7 && part == Root ? DosEye - WalkerAimDrop : DosEye, 0f);
        var to = aim - part.World.Translation;
        var dist = to.Length();
        if (dist > fire.Range || dist < 1f)
        {
            return;
        }

        var cos = Vector3.Dot(part.Forward, to / dist);
        if (!(dist < CloseRange && cos > CloseCos) && cos < fire.AimGate / Fixed1 || !part.IsTurret && !Sees)
        {
            return;
        }

        var x = part.MuzzleSwapped != fire.Speed < 0 ? -fire.MuzzleX : fire.MuzzleX;
        var muzzle = Vector3.Transform(new Vector3(x, -fire.MuzzleY, -fire.MuzzleZ), part.World);

        // IMPROVED: no shot into a wall, turrets included (the eye's line and the muzzle's).
        if (Improved && !ClearShot(part, muzzle, aim, player))
        {
            return;
        }

        var chance = (int)((fire.Rate * (long)(_ctx.Difficulty.EnemyFire * Fixed1) + 0x8000) >> 16);
        if (_ctx.Rng.Next(RollRange) >= chance)
        {
            return;
        }

        part.MuzzleSwapped ^= fire.Speed < 0;
        var dir = Vector3.Normalize(aim - muzzle);
        var r = ((int)(part.BlastPad * 256f) + 0x80) >> 8;   // 0x13c2d1: the model radius, rounded
        ShotsFired++;
        _ctx.Shoot(this, fire.Ammo, muzzle + dir * (r + (r >> 4)), dir, MathF.Abs(fire.Speed));
    }

    private const float DosEye = 75f, WalkerAimDrop = 18f, CloseRange = 140f, CloseCos = 0xb504 / 65536f, Fixed1 = 65536f;
    private const int RollRange = 1024, HoverFlags = 26;

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
        if (Indestructible || !Alive)
        {
            return;
        }

        Hp -= MathF.Max(damage.Points, 0f);
        _ctx.HitSound(Position + new Vector3(0f, 40f, 0f));

        // FUN_00139819 (0x1398e6) sets the alert bit 0x8000 on the actor and its children: the next
        // perception test (Sense) counts as seen, and that answer holds until the test after it.
        _alert = true;
        foreach (var c in Root.Children)
        {
            Alert(c);
        }
        if (Hp <= 0f)
        {
            Die();
        }
    }

    /// <summary>ObjHit (0x139819) on one segment's record: its own hit points alone, none below 1;
    /// at zero EnemyKill takes it and its children off (<see cref="Robots"/>), the robot fights on.
    /// The root's is <see cref="TakeDamage"/>: it alone kills the robot.</summary>
    internal void Hit(EnemyPart part, in Damage damage)
    {
        if (part == Root)
        {
            TakeDamage(damage);
            return;
        }

        if (Dead || Hidden || part.Gone || part.Hp <= 0f)
        {
            return;
        }

        part.Hp -= MathF.Max(damage.Points, 0f);
        _ctx.HitSound(part.World.Translation);
        Alert(part);
        if (part.Hp <= 0f)
        {
            _ctx.PartDestroyed(this, part);
        }
    }

    /// <summary>0x12a280: the alert bit on a segment's actor and on every segment under it.</summary>
    private static void Alert(EnemyPart part)
    {
        foreach (var p in part.Walk())
        {
            p.Alerted = true;
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

    /// <summary>A flying mine: ObjHit on itself (the bang 0x26), then the ordinary death blast.</summary>
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

    /// <summary>The death blast's points at distance <paramref name="d"/> (DOS fixed point; 0: none);
    /// <paramref name="pad"/>: the soldier's 40, a robot's model radius (FUN_00124886).</summary>
    public static int DeathBlastDamage(float d, float pad = DeathBlastPad)
    {
        var k = (int)MathF.Floor((DeathBlast + pad - d) * 256f / DeathBlast);
        var dmg = (k * DeathBlast) >> 8;
        return dmg >= DeathBlastMin ? dmg : 0;
    }
}
