using System.Collections.Immutable;

namespace SkyNet.Formats;

/// <summary>The kinds of 16-byte AIS op <c>{fn, a, b, c}</c>, named by their handler in the VM (FUN_0012a500 in v1.00).</summary>
public enum AisOpKind
{
    /// <summary>A handler this reader does not know, or not a pointer: the walk stops here (the Godot port's "end").</summary>
    End,
    /// <summary><c>if pred(a) cmp b goto c</c>, else fall through. Runs on in the same tick.</summary>
    If,
    /// <summary><c>goto a</c>. Runs on in the same tick.</summary>
    Goto,
    /// <summary>Wait a/70 s; c ≠ 0 freezes the animation. Ends the tick.</summary>
    Wait,
    /// <summary>Start the animation block at a. Ends the tick.</summary>
    Anim,
    /// <summary>Play sound a. Runs on in the same tick.</summary>
    Sound,
    /// <summary>var[a] = b (a is a byte offset into the actor's parameter block). Ends the tick.</summary>
    Set,
    /// <summary>var[a] = pred(b). Ends the tick.</summary>
    SetFn,
    /// <summary>var[a] += b. Ends the tick.</summary>
    Add,
}

/// <summary>The comparison of an <see cref="AisOpKind.If"/> (one handler each).</summary>
public enum AisCompare { None, Le, Lt, Gt, Eq0, Ne0 }

/// <summary>The predicates an IF or SETFN reads (handlers 0x12a6b8.. in v1.00).</summary>
public enum AisPredicate
{
    Unknown,
    /// <summary>1 when the player is NOT perceived.</summary>
    See,
    Distance,
    /// <summary>Bearing to the player, 0..2047.</summary>
    Bearing,
    BearingAbs,
    /// <summary>Random 0..1023.</summary>
    Random,
    AngleToPlayer,
    /// <summary>Animation flags &amp; arg.</summary>
    AnimFlags,
    /// <summary>Actor flags &amp; arg (0x1000 = movement blocked).</summary>
    Flags,
}

/// <summary>
/// One decoded AIS op. <see cref="A"/>, <see cref="B"/>, <see cref="C"/> are the raw operands; for IF
/// the predicate comes from A, for SETFN from B. <see cref="Target"/> is the jump VA (IF/GOTO) and
/// <see cref="AnimVa"/> the animation block (ANIM), both corrected by +0x30000. The next op is Va + 16.
/// </summary>
public sealed record AisOp(uint Va, AisOpKind Kind, uint Handler, int A, int B, int C,
    AisPredicate Predicate, uint PredicateHandler, AisCompare Compare, uint Target, uint AnimVa)
{
    public uint Next => Va + 16;
}

/// <summary>One entry of an animation block: a model frame and its flags (bit 0 = last).</summary>
public readonly record struct AisAnimFrame(ushort Frame, ushort Flags);

/// <summary>
/// An animation block <c>{u32 type, u32 n, u32 fps, u16 flags, (u16 frame, u16 frame_flags)…}</c>; the
/// list ends at the entry with frame_flags &amp; 1 (n is not trusted). Flags: 4 one-shot, 8 loop,
/// 0x100 firing pose. Type 1 is an animation; <see cref="Fps"/> is 0 for other types, as in the Godot port.
/// </summary>
public sealed record AisAnimBlock(uint Va, uint Type, uint Count, uint RawFps, ushort Flags, ImmutableArray<AisAnimFrame> Frames)
{
    public const ushort OneShot = 4, Loop = 8, FiringPose = 0x100;
    public int Fps => Type == 1 ? (int)RawFps : 0;
}

/// <summary>
/// Fire parameters (from the 36-dword block, family-dependent indices): muzzle offset, ammo type,
/// bolt speed (negative = alternate muzzle X, twin barrels), rate, range and the aim gate (an
/// 11-bit bearing for walkers/turrets, cos×65536 for hovers and flyers).
/// </summary>
public readonly record struct AisFire(int MuzzleX, int MuzzleY, int MuzzleZ, int Ammo, int Speed, int Rate, int Range, int AimGate);

/// <summary>A child segment <c>{continue, child_type, ox, oy, oz, pitch, yaw, roll}</c> (32 bytes); the entry with continue = 0 is the last.</summary>
public readonly record struct EnemySegment(int ChildType, int X, int Y, int Z, int Pitch, int Yaw, int Roll);

/// <summary>A death part: debris type and offset (kind 0 = one part at the origin).</summary>
public readonly record struct EnemyDeathPart(int Type, int X, int Y, int Z);

/// <summary>Frame event of a walker or head: on <see cref="Frame"/> play <see cref="Sound"/>.</summary>
public readonly record struct AisFrameEvent(int Frame, int Sound);

/// <summary>
/// One record of the enemy type table (100 × 0x1C bytes) with its parameter block read through the
/// layout of its AI state. Fields the state's layout lacks are null.
/// </summary>
public sealed class EnemyType
{
    public int Index { get; init; }
    /// <summary>The seven raw dwords of the table record.</summary>
    public ImmutableArray<uint> Record { get; init; } = [];
    /// <summary>The .3D name in MDMDENMS.BSA, lower case without ".3d" (as the Godot port keys it); "" when none.</summary>
    public string Name { get; init; } = "";
    public string ModelFile { get; init; } = "";
    /// <summary>A mod robot's own look: the name its ENHANCED replacement (<c>models/LOOK.glb</c>) is
    /// found under instead of <see cref="ModelFile"/>'s; "" for DOS's types.</summary>
    public string Look { get; init; } = "";
    /// <summary>AI state id (+0x08): 2 turret, 6 hover, 7 walker, 8 head, 9 flyer, 10 machine, 11 path vehicle, 12 dormant trap, 13 tank…</summary>
    public int State { get; init; }
    /// <summary>The 36-dword parameter block (+0x14), zeros when the record has none.</summary>
    public ImmutableArray<int> Params { get; init; } = [];
    /// <summary>dw0 clamped to 0..0x7FFF.</summary>
    public int HitPoints { get; init; }
    public int? Speed { get; init; }
    public int? Near { get; init; }
    public int? Turn { get; init; }
    public int? Axis { get; init; }
    public int? AngleMin { get; init; }
    public int? AngleMax { get; init; }
    public int? Range { get; init; }
    public int? Altitude { get; init; }
    public int? VerticalAccel { get; init; }
    public int? AvoidRadius { get; init; }
    public int? ForwardSpeed { get; init; }
    /// <summary>Null when the layout has no fire fields or rate or range is not positive (the type never shoots).</summary>
    public AisFire? Fire { get; init; }
    /// <summary>First op of the AIS script (VA), when the layout has a script that points into the image.</summary>
    public uint? ScriptVa { get; init; }
    public ImmutableArray<AisFrameEvent> Events { get; init; } = [];
    /// <summary>Engine loop sound, when the layout's engine block has type 5.</summary>
    public int? EngineSound { get; init; }
    public ImmutableArray<EnemyDeathPart> Death { get; init; } = [];
    /// <summary>The kind word of the death spec (+0x10) when it points at one: 0 = one part and no
    /// explosion of its own, 1 = a part list plus the explosion. Null when the spec is the bare 0
    /// (nothing) or 1 (the explosion alone) — see <see cref="Record"/>[4].</summary>
    public int? DeathKind { get; init; }
    public ImmutableArray<EnemySegment> Segments { get; init; } = [];
}

/// <summary>One ammo-type record (stride 0x32): family 0 hitscan, 1 gravity, 2 straight bolt.</summary>
public sealed record AmmoType(int Index, uint Callback, int Family, string Model, int ImpactBank, int Damage, int BlastRadius,
    int LifeTicks, int FireSound, int ImpactSound);

/// <summary>
/// SkyNET's enemy AI data, kept in the executable (SKYNET.EXE; it is not a data file): the enemy
/// type table, each type's AIS script as decoded ops, the animation blocks the scripts start, and the
/// ammo table. Ported from the Godot port's generator (tools/gen_enemy_ai.py → enemy_ai_data.gd);
/// the VM is not run here. v1.01 tables sit 0x100 above v1.00's and the op handlers 0x500 higher.
/// <para><see cref="Parse"/> reads the executable (tools/SkyNet.Tables does, once); the game builds
/// it from the shipped tables (SkyNet.Data.GameTables.Ais) through the public constructor.</para>
/// </summary>
public sealed class AisData
{
    /// <summary>Where a build keeps its tables.</summary>
    public sealed record Build(string Version, uint EnemyTable, uint AmmoTable, uint CodeShift);

    public static readonly Build V100 = new("1.00", 0x44D00, 0x40728, 0);
    public static readonly Build V101 = new("1.01", 0x44E00, 0x40828, 0x500);

    /// <summary>Stored data pointers are object-relative; add this for the VA.</summary>
    public const uint PointerBase = 0x30000;
    public const int ParamCount = 36;
    public const int RecordSize = 0x1C;
    public const int AmmoStride = 0x32;
    public const int AmmoCount = 25;
    /// <summary>Dormant traps (state 12, marker sub+2 ≠ 0): the effect bank and the sound of the bang.</summary>
    public const int TrapEffectBank = 367, TrapSound = 0x26;

    // v1.00 handler addresses; a build adds its CodeShift.
    static readonly Dictionary<uint, (AisOpKind, AisCompare)> OpHandlers = new()
    {
        [0x12A572] = (AisOpKind.If, AisCompare.Le), [0x12A586] = (AisOpKind.If, AisCompare.Lt),
        [0x12A59A] = (AisOpKind.If, AisCompare.Gt), [0x12A5AE] = (AisOpKind.If, AisCompare.Eq0),
        [0x12A5C1] = (AisOpKind.If, AisCompare.Ne0), [0x12A5D4] = (AisOpKind.Goto, AisCompare.None),
        [0x12A5D9] = (AisOpKind.Wait, AisCompare.None), [0x12A60C] = (AisOpKind.Anim, AisCompare.None),
        [0x12A640] = (AisOpKind.Sound, AisCompare.None), [0x12A668] = (AisOpKind.Set, AisCompare.None),
        [0x12A682] = (AisOpKind.SetFn, AisCompare.None), [0x12A69E] = (AisOpKind.Add, AisCompare.None),
    };

    static readonly Dictionary<uint, AisPredicate> PredHandlers = new()
    {
        [0x12A6B8] = AisPredicate.See, [0x12A6E9] = AisPredicate.Distance, [0x12A6FD] = AisPredicate.Bearing,
        [0x12A75A] = AisPredicate.BearingAbs, [0x12A7AB] = AisPredicate.Random, [0x12A789] = AisPredicate.AngleToPlayer,
        [0x12A7C4] = AisPredicate.AnimFlags, [0x12A7D0] = AisPredicate.Flags,
    };

    /// <summary>Parameter-block indices per AI state (the Godot generator's LAYOUT).</summary>
    sealed record Layout(int Speed = -1, int Near = -1, int Turn = -1, int Axis = -1, int AMin = -1, int AMax = -1,
        int Range = -1, int Alt = -1, int VAccel = -1, int Avoid = -1, int FSpeed = -1, int[]? Fire = null,
        int Script = -1, int Events = -1, int Engine = -1);

    static readonly Dictionary<int, Layout> Layouts = new()
    {
        [7] = new(Speed: 4, Near: 6, Turn: 7, Fire: [11, 12, 13, 14, 15, 18, 19, 16], Script: 26, Events: 29),
        [6] = new(Speed: 4, Near: 6, Turn: 7, Fire: [12, 13, 14, 15, 16, 19, 20, 17], Script: 21),
        [2] = new(Axis: 4, AMin: 5, AMax: 6, Turn: 7, Range: 8, Fire: [12, 13, 14, 15, 16, 19, 20, 17]),
        [8] = new(Axis: 4, AMin: 5, AMax: 6, Turn: 7, Range: 8, Fire: [12, 13, 14, 15, 16, 19, 20, 17], Script: 24, Events: 27),
        [9] = new(Speed: 4, Turn: 5, Alt: 6, VAccel: 7, Avoid: 8, FSpeed: 9, Fire: [17, 18, 19, 20, 21, 24, 25, 22], Script: 14, Engine: 26),
        [13] = new(Speed: 6, Turn: 5, Script: 12, Engine: 22),
        [10] = new(Script: 6),
    };

    readonly LeExecutable _exe = null!;
    readonly Dictionary<uint, AisOp> _ops = [];
    readonly Dictionary<uint, AisAnimBlock> _anims = [];

    public Build Tables { get; }
    public ImmutableArray<EnemyType> Types { get; }
    public ImmutableArray<AmmoType> Ammo { get; }
    /// <summary>Every op reachable from a type's script, by VA.</summary>
    public IReadOnlyDictionary<uint, AisOp> Ops => _ops;
    /// <summary>Every animation block an ANIM op starts, by VA.</summary>
    public IReadOnlyDictionary<uint, AisAnimBlock> Anims => _anims;

    AisData(LeExecutable exe, Build build)
    {
        _exe = exe;
        Tables = build;
        Types = [.. ReadTypes()];
        Ammo = [.. ReadAmmo()];
    }

    /// <summary>The data as already decoded (the shipped tables): types, ammo, every op and animation block.</summary>
    public AisData(Build build, IEnumerable<EnemyType> types, IEnumerable<AmmoType> ammo, IEnumerable<AisOp> ops, IEnumerable<AisAnimBlock> anims)
    {
        Tables = build;
        Types = [.. types];
        Ammo = [.. ammo];
        foreach (var op in ops)
            _ops[op.Va] = op;
        foreach (var a in anims)
            _anims[a.Va] = a;
    }

    /// <summary>Parse SKYNET.EXE (v1.01, or v1.00); null when it is not a SkyNET LE image with an enemy table.</summary>
    public static AisData? Parse(ReadOnlySpan<byte> exeBytes)
    {
        var exe = LeExecutable.Parse(exeBytes);
        if (exe == null)
            return null;
        Build? build = exe.SkyNetVersion switch { "1.01" => V101, "1.00" => V100, _ => null };
        if (build == null || !exe.Contains(build.EnemyTable, RecordSize))
            return null;
        return new AisData(exe, build);
    }

    static bool IsPointer(long raw) => raw > 0x10000 && raw < 0x40000;

    IEnumerable<EnemyType> ReadTypes()
    {
        for (int i = 0; i < 256; i++)
        {
            uint b = Tables.EnemyTable + (uint)(RecordSize * i);
            if (!_exe.Contains(b, RecordSize))
                yield break;
            var rec = new uint[7];
            for (int k = 0; k < 7; k++)
                rec[k] = _exe.U32(b + (uint)(4 * k));
            if (rec[0] == 0)
                yield break;
            yield return ReadType(i, rec);
        }
    }

    EnemyType ReadType(int index, uint[] rec)
    {
        string model = _exe.CString(rec[0] + PointerBase + 4);
        int state = (int)rec[2];
        var p = new int[ParamCount];
        if (rec[5] != 0)
            for (int k = 0; k < ParamCount; k++)
                p[k] = _exe.I32(rec[5] + PointerBase + (uint)(4 * k));
        var lay = Layouts.GetValueOrDefault(state) ?? new Layout();
        int? F(int i) => i >= 0 ? p[i] : null;

        AisFire? fire = null;
        if (lay.Fire is { } fi && p[fi[5]] > 0 && p[fi[6]] > 0)
            fire = new AisFire(p[fi[0]], p[fi[1]], p[fi[2]], p[fi[3]], p[fi[4]], p[fi[5]], p[fi[6]], p[fi[7]]);

        uint? script = null;
        if (lay.Script >= 0 && IsPointer(p[lay.Script]))
        {
            script = (uint)p[lay.Script] + PointerBase;
            DecodeScript(script.Value);
        }

        var events = ImmutableArray.CreateBuilder<AisFrameEvent>();
        if (lay.Events >= 0 && IsPointer(p[lay.Events]))
        {
            uint eva = (uint)p[lay.Events] + PointerBase;
            for (int k = 0; k < 80; k += 2)
            {
                int fr = _exe.I32(eva + (uint)(4 * k));
                if (fr == -1)
                    break;
                events.Add(new AisFrameEvent(fr, _exe.I32(eva + (uint)(4 * k + 4))));
            }
        }

        int? engine = lay.Engine >= 0 && p[lay.Engine] == 5 ? p[lay.Engine + 1] : null;

        // Death spec (+0x10): 0/1 none, else -> {kind, arg}: kind 0 one debris type, kind 1 a list
        // of 32-byte {continue, type, ox, oy, oz, ...} entries (at most 12 read, as the port does).
        var death = ImmutableArray.CreateBuilder<EnemyDeathPart>();
        int? deathKind = null;
        if (rec[4] > 1)
        {
            uint dva = rec[4] + PointerBase;
            int kind = _exe.I32(dva), arg = _exe.I32(dva + 4);
            deathKind = kind;
            if (kind == 0)
                death.Add(new EnemyDeathPart(arg, 0, 0, 0));
            else if (kind == 1)
            {
                uint l = (uint)arg + PointerBase;
                for (int k = 0; k < 12; k++, l += 32)
                {
                    death.Add(new EnemyDeathPart(_exe.I32(l + 4), _exe.I32(l + 8), _exe.I32(l + 12), _exe.I32(l + 16)));
                    if (_exe.I32(l) == 0)
                        break;
                }
            }
        }

        var segs = ImmutableArray.CreateBuilder<EnemySegment>();
        if (rec[3] != 0)
        {
            uint s = rec[3] + PointerBase;
            for (int k = 0; k < 16 && _exe.Contains(s, 32); k++, s += 32)
            {
                segs.Add(new EnemySegment(_exe.I32(s + 4), _exe.I32(s + 8), _exe.I32(s + 12), _exe.I32(s + 16),
                    _exe.I32(s + 20), _exe.I32(s + 24), _exe.I32(s + 28)));
                if (_exe.I32(s) == 0)
                    break;
            }
        }

        return new EnemyType
        {
            Index = index,
            Record = [.. rec],
            ModelFile = model,
            Name = model.ToLowerInvariant().Replace(".3d", "", StringComparison.Ordinal),
            State = state,
            Params = [.. p],
            HitPoints = Math.Clamp(p[0], 0, 0x7FFF),
            Speed = F(lay.Speed), Near = F(lay.Near), Turn = F(lay.Turn), Axis = F(lay.Axis),
            AngleMin = F(lay.AMin), AngleMax = F(lay.AMax), Range = F(lay.Range), Altitude = F(lay.Alt),
            VerticalAccel = F(lay.VAccel), AvoidRadius = F(lay.Avoid), ForwardSpeed = F(lay.FSpeed),
            Fire = fire,
            ScriptVa = script,
            Events = events.ToImmutable(),
            EngineSound = engine,
            Death = death.ToImmutable(),
            DeathKind = deathKind,
            Segments = segs.ToImmutable(),
        };
    }

    void DecodeScript(uint start)
    {
        var work = new Stack<uint>();
        work.Push(start);
        while (work.Count > 0)
        {
            uint va = work.Pop();
            if (_ops.ContainsKey(va))
                continue;
            if (!_exe.Contains(va, 16))
            {
                _ops[va] = new AisOp(va, AisOpKind.End, 0, 0, 0, 0, AisPredicate.Unknown, 0, AisCompare.None, 0, 0);
                continue;
            }
            uint fn = _exe.U32(va) + PointerBase;
            int a = _exe.I32(va + 4), b = _exe.I32(va + 8), c = _exe.I32(va + 12);
            if (!OpHandlers.TryGetValue(fn - Tables.CodeShift, out var h))
            {
                _ops[va] = new AisOp(va, AisOpKind.End, fn, a, b, c, AisPredicate.Unknown, 0, AisCompare.None, 0, 0);
                continue;
            }
            var (kind, cmp) = h;
            AisOp op = kind switch
            {
                AisOpKind.If => new AisOp(va, kind, fn, a, b, c, Pred(a), (uint)a + PointerBase, cmp, (uint)c + PointerBase, 0),
                AisOpKind.Goto => new AisOp(va, kind, fn, a, b, c, AisPredicate.Unknown, 0, cmp, (uint)a + PointerBase, 0),
                AisOpKind.SetFn => new AisOp(va, kind, fn, a, b, c, Pred(b), (uint)b + PointerBase, cmp, 0, 0),
                AisOpKind.Anim => new AisOp(va, kind, fn, a, b, c, AisPredicate.Unknown, 0, cmp, 0, (uint)a + PointerBase),
                _ => new AisOp(va, kind, fn, a, b, c, AisPredicate.Unknown, 0, cmp, 0, 0),
            };
            _ops[va] = op;
            switch (kind)
            {
                case AisOpKind.If:
                    work.Push(op.Target);
                    work.Push(op.Next);
                    break;
                case AisOpKind.Goto:
                    work.Push(op.Target);
                    break;
                case AisOpKind.Anim:
                    DecodeAnim(op.AnimVa);
                    work.Push(op.Next);
                    break;
                default:
                    work.Push(op.Next);
                    break;
            }
        }
    }

    AisPredicate Pred(int raw) => PredHandlers.GetValueOrDefault((uint)raw + PointerBase - Tables.CodeShift, AisPredicate.Unknown);

    void DecodeAnim(uint va)
    {
        if (_anims.ContainsKey(va) || !_exe.Contains(va, 14))
            return;
        var frames = ImmutableArray.CreateBuilder<AisAnimFrame>();
        uint p = va + 14;
        for (int k = 0; k < 128 && _exe.Contains(p, 4); k++, p += 4)
        {
            var f = new AisAnimFrame(_exe.U16(p), _exe.U16(p + 2));
            frames.Add(f);
            if ((f.Flags & 1) != 0)
                break;
        }
        _anims[va] = new AisAnimBlock(va, _exe.U32(va), _exe.U32(va + 4), _exe.U32(va + 8), _exe.U16(va + 12), frames.ToImmutable());
    }

    IEnumerable<AmmoType> ReadAmmo()
    {
        uint fam1 = 0xF3414 + Tables.CodeShift, fam2 = 0xF344D + Tables.CodeShift;
        for (int i = 0; i < AmmoCount; i++)
        {
            uint b = Tables.AmmoTable + (uint)(AmmoStride * i);
            if (!_exe.Contains(b, AmmoStride))
                yield break;
            uint cb = _exe.U32(b), mdl = _exe.U32(b + 4);
            string model = IsPointer(mdl) ? _exe.CString(mdl + PointerBase + 4).ToUpperInvariant() : "";
            int family = cb == fam1 ? 1 : cb == fam2 ? 2 : 0;
            int bank = _exe.I32(b + 8);
            yield return new AmmoType(i, cb, family, model, bank > 0 ? bank >> 7 : 0, _exe.I32(b + 0xC), _exe.I32(b + 0x10),
                _exe.I32(b + 0x18), _exe.I32(b + 0x1C), _exe.I32(b + 0x20));
        }
    }

    /// <summary>
    /// The ops of a script in a fixed order (breadth first from <paramref name="start"/>; an IF's
    /// fall-through before its target), so two builds whose scripts sit at different VAs compare equal.
    /// </summary>
    public IReadOnlyList<AisOp> ScriptOps(uint start)
    {
        var order = new List<AisOp>();
        var seen = new HashSet<uint> { start };
        var q = new Queue<uint>();
        q.Enqueue(start);
        while (q.Count > 0)
        {
            var op = _ops[q.Dequeue()];
            order.Add(op);
            uint[] next = op.Kind switch
            {
                AisOpKind.End => [],
                AisOpKind.If => [op.Next, op.Target],
                AisOpKind.Goto => [op.Target],
                _ => [op.Next],
            };
            foreach (var n in next)
                if (seen.Add(n))
                    q.Enqueue(n);
        }
        return order;
    }
}
