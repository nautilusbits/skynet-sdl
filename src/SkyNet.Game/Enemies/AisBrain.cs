using RetroEng.Loop;
using SkyNet.Formats;

namespace SkyNet.Game.Enemies;

/// <summary>
/// What an actor perceives in one tick, for the script's predicates: whether the player is seen,
/// his flat distance, his bearing relative to the facing (0..2047, 0 = dead ahead), his absolute
/// DOS angle (0..2047) and whether the last move was blocked. <see cref="Rand"/> pins the random
/// predicate (tests); null rolls the brain's own generator.
/// </summary>
public readonly record struct AisSenses(bool See, float Distance, int Bearing, int Angle, bool Blocked, int? Rand = null)
{
    public static AisSenses None => new(false, 1e9f, 0, 0, false);
}

/// <summary>
/// The AIS script VM of one actor (FUN_0012a500 in v1.00, + the animation stepper 0x135c70 and
/// the frame events FUN_001375b1), ported from the Godot port's enemy_ai.gd. IF, GOTO and SOUND
/// run on in the same tick; WAIT, ANIM, SET, SETFN and ADD end it, as the DOS handlers' carry flag
/// does. Variables are keyed by the SET byte offset into the actor's parameter block (flyer target
/// speed 56, tank speed 48, tank heading 44).
/// <para>Deviation (the Godot port's): DOS runs the script once per rendered frame; this runs it at
/// <see cref="ScriptHz"/> (12 Hz, the frame rate the scripts' random rolls were tuned for on a
/// 1996 machine), and the animation steps every tick.</para>
/// </summary>
public sealed class AisBrain
{
    /// <summary>WAIT counts in 1/70 s.</summary>
    public const float Tick = 1f / 70f;
    public const float ScriptHz = 12f;
    /// <summary>Runtime animation flags (the stepper's BX): the one-shot ended, the loop wrapped.</summary>
    public const int AnimDone = 1, AnimWrapped = 2;
    /// <summary>Actor flag the tank script tests: movement blocked.</summary>
    public const int FlagBlocked = 0x1000;
    /// <summary>Safety cap on ops per evaluation (scripts are small loops).</summary>
    public const int MaxOpsPerTick = 64;

    private readonly AisData _ais;
    private readonly SimRandom _rng;
    private readonly Dictionary<int, int> _vars = [];
    private readonly List<int> _sounds = [];
    private float _scriptAcc;
    private float _cursor;

    public AisBrain(AisData ais, int type, SimRandom? rng = null)
    {
        _ais = ais;
        _rng = rng ?? new SimRandom((ulong)type);
        TypeIndex = type;
        Type = type >= 0 && type < ais.Types.Length ? ais.Types[type] : null;
        State = Type?.State ?? 0;
        Pc = Type?.ScriptVa ?? 0;
        OneShotWalker = State == 7 && Pc != 0 && !ScriptLoops(ais, Pc);
    }

    /// <summary>A brain over a hand-built script (tests): the ops and blocks come from <paramref name="ais"/>'s
    /// tables only through <paramref name="ops"/> / <paramref name="anims"/>.</summary>
    public AisBrain(IReadOnlyDictionary<uint, AisOp> ops, IReadOnlyDictionary<uint, AisAnimBlock> anims, uint start, int state = 7,
        IReadOnlyList<AisFrameEvent>? events = null, SimRandom? rng = null)
    {
        _ais = null!;
        _ops = ops;
        _anims = anims;
        _events = events;
        _rng = rng ?? new SimRandom(1);
        TypeIndex = -1;
        State = state;
        Pc = start;
    }

    private readonly IReadOnlyDictionary<uint, AisOp>? _ops;
    private readonly IReadOnlyDictionary<uint, AisAnimBlock>? _anims;
    private readonly IReadOnlyList<AisFrameEvent>? _events;

    private IReadOnlyDictionary<uint, AisOp> Ops => _ops ?? _ais.Ops;
    private IReadOnlyDictionary<uint, AisAnimBlock> Anims => _anims ?? _ais.Anims;
    private IReadOnlyList<AisFrameEvent> Events => _events ?? (IReadOnlyList<AisFrameEvent>?)Type?.Events ?? [];

    public int TypeIndex { get; }
    public EnemyType? Type { get; }
    /// <summary>The AI state id (2 turret, 6 hover, 7 walker, …).</summary>
    public int State { get; }
    /// <summary>The next op's VA; 0 = no script (or it ended: the pose holds).</summary>
    public uint Pc { get; private set; }
    public float WaitLeft { get; private set; }
    /// <summary>WAIT(n, c ≠ 0): the animation is paused while it waits.</summary>
    public bool FreezeAnim { get; private set; }
    public IReadOnlyDictionary<int, int> Vars => _vars;

    public uint AnimVa { get; private set; }
    public AisAnimBlock? Anim { get; private set; }
    /// <summary>The block's flags with the runtime <see cref="AnimDone"/>/<see cref="AnimWrapped"/> bits.</summary>
    public int AnimFlags { get; private set; }
    /// <summary>The model frame shown now.</summary>
    public int Frame { get; private set; }
    public bool FrameChanged { get; private set; }
    /// <summary>Blocks started so far (a one-shot started again counts): a drawing that plays its
    /// own clip per block restarts it on a change.</summary>
    public int AnimStarts { get; private set; }
    /// <summary>Frame-event and script sounds of the last tick.</summary>
    public IReadOnlyList<int> Sounds => _sounds;

    /// <summary>A walker whose script starts no looping block (the spider bot): its walk cycle is
    /// one-shot firing blocks, and the script waits for each to end (DOS runs the script on the
    /// stepper's flagged frame).</summary>
    public bool OneShotWalker { get; }

    public bool HasScript => Pc != 0;
    public bool FiringPose => (AnimFlags & AisAnimBlock.FiringPose) != 0;
    public bool Looping => (AnimFlags & AisAnimBlock.Loop) != 0;
    public bool OneShotDone => (AnimFlags & AisAnimBlock.OneShot) != 0 && (AnimFlags & AnimDone) != 0;

    public int Var(int offset, int fallback) => _vars.TryGetValue(offset, out var v) ? v : fallback;
    public bool HasVar(int offset) => _vars.ContainsKey(offset);

    /// <summary>True when any block the script can start loops.</summary>
    public static bool ScriptLoops(AisData ais, uint start)
    {
        var seen = new HashSet<uint>();
        var todo = new Stack<uint>();
        todo.Push(start);
        while (todo.Count > 0)
        {
            var p = todo.Pop();
            if (!seen.Add(p) || !ais.Ops.TryGetValue(p, out var op))
            {
                continue;
            }

            switch (op.Kind)
            {
                case AisOpKind.End:
                    continue;
                case AisOpKind.Goto:
                    todo.Push(op.Target);
                    continue;
                case AisOpKind.If:
                    todo.Push(op.Target);
                    break;
                case AisOpKind.Anim:
                    if (ais.Anims.TryGetValue(op.AnimVa, out var b) && (b.Flags & AisAnimBlock.Loop) != 0)
                    {
                        return true;
                    }

                    break;
            }

            todo.Push(op.Next);
        }

        return false;
    }

    /// <summary>Starts a block; a looping block already playing keeps running (PLAY_ANIM's check).</summary>
    public void PlayAnim(uint va)
    {
        if (va == AnimVa && (AnimFlags & AisAnimBlock.Loop) != 0)
        {
            return;
        }

        AnimVa = va;
        AnimStarts++;
        if (!Anims.TryGetValue(va, out var block) || block.Frames.Length == 0)
        {
            Anim = null;
            AnimFlags = 0;
            return;
        }

        Anim = block;
        AnimFlags = block.Flags;
        _cursor = 0f;
        SetFrame(block.Frames[0].Frame);
    }

    private void SetFrame(int f)
    {
        if (f == Frame)
        {
            return;
        }

        Frame = f;
        FrameChanged = true;
        foreach (var ev in Events)
        {
            if (ev.Frame == f)
            {
                _sounds.Add(ev.Sound);
            }
        }
    }

    private void StepAnim(float dt)
    {
        if (Anim is not { } a || a.Fps <= 0 || FreezeAnim)
        {
            return;
        }

        var n = a.Frames.Length;
        if (n == 1)
        {
            SetFrame(a.Frames[0].Frame);
            return;
        }

        _cursor += a.Fps * dt;
        AnimFlags &= ~AnimWrapped;
        if (_cursor >= n)
        {
            if ((AnimFlags & AisAnimBlock.OneShot) != 0)
            {
                _cursor = n - 1;
                AnimFlags |= AnimDone;
            }
            else
            {
                _cursor %= n;
                AnimFlags |= AnimWrapped;
            }
        }

        SetFrame(a.Frames[Math.Clamp((int)_cursor, 0, n - 1)].Frame);
    }

    /// <summary>A predicate's value (handlers 0x12a6b8..).</summary>
    public int Predicate(AisPredicate p, int arg, in AisSenses s) => p switch
    {
        // DOS 0x12a6b8 returns 1 while the "perceived" flag is CLEAR.
        AisPredicate.See => s.See ? 0 : 1,
        AisPredicate.Distance => (int)MathF.Min(s.Distance, int.MaxValue / 2),
        AisPredicate.Bearing => s.Bearing & 0x7FF,
        AisPredicate.BearingAbs => Math.Min(s.Bearing & 0x7FF, 2048 - (s.Bearing & 0x7FF)),
        AisPredicate.Random => s.Rand ?? _rng.Next(1024),
        AisPredicate.AngleToPlayer => s.Angle & 0x7FF,
        AisPredicate.AnimFlags => AnimFlags & arg,
        AisPredicate.Flags => (s.Blocked ? FlagBlocked : 0) & arg,
        _ => 0,
    };

    private bool WaitsOnBlock => OneShotWalker && Anim != null && (AnimFlags & AisAnimBlock.OneShot) != 0 && (AnimFlags & AnimDone) == 0;

    /// <summary>One AI tick: steps the animation and, at <see cref="ScriptHz"/>, the script (dt 0:
    /// the script runs now — the spawn tick). Read <see cref="Frame"/>, <see cref="Sounds"/> and
    /// <see cref="FiringPose"/> afterwards.</summary>
    public void Update(float dt, in AisSenses senses)
    {
        _sounds.Clear();
        FrameChanged = false;
        StepAnim(dt);
        _scriptAcc += dt;
        if (Pc != 0 && (_scriptAcc >= 1f / ScriptHz || dt <= 0f) && !WaitsOnBlock)
        {
            var elapsed = _scriptAcc;
            _scriptAcc = 0f;
            RunScript(elapsed, senses);
        }
    }

    /// <summary>Executes ops until one ends the tick (FUN_0012a500).</summary>
    public void RunScript(float dt, in AisSenses s)
    {
        if (Pc == 0)
        {
            return;
        }

        if (WaitLeft > 0f)
        {
            WaitLeft -= dt;
            if (WaitLeft > 0f)
            {
                return;
            }

            FreezeAnim = false;
        }

        for (var i = 0; i < MaxOpsPerTick; i++)
        {
            if (!Ops.TryGetValue(Pc, out var op) || op.Kind == AisOpKind.End)
            {
                Pc = 0;
                return;
            }

            switch (op.Kind)
            {
                case AisOpKind.If:
                    var v = Predicate(op.Predicate, op.B, s);
                    var jump = op.Compare switch
                    {
                        AisCompare.Le => v <= op.B,
                        AisCompare.Lt => v < op.B,
                        AisCompare.Gt => v > op.B,
                        AisCompare.Eq0 => v == 0,
                        AisCompare.Ne0 => v != 0,
                        _ => false,
                    };
                    Pc = jump ? op.Target : op.Next;
                    break;
                case AisOpKind.Goto:
                    Pc = op.Target;
                    break;
                case AisOpKind.Sound:
                    _sounds.Add(op.A);
                    Pc = op.Next;
                    break;
                case AisOpKind.Wait:
                    WaitLeft = op.A * Tick;
                    FreezeAnim = op.C != 0;
                    Pc = op.Next;
                    return;
                case AisOpKind.Anim:
                    PlayAnim(op.AnimVa);
                    Pc = op.Next;
                    return;
                case AisOpKind.Set:
                    _vars[op.A] = op.B;
                    Pc = op.Next;
                    return;
                case AisOpKind.Add:
                    _vars[op.A] = _vars.GetValueOrDefault(op.A) + op.B;
                    Pc = op.Next;
                    return;
                case AisOpKind.SetFn:
                    _vars[op.A] = Predicate(op.Predicate, 0, s);
                    Pc = op.Next;
                    return;
                default:
                    Pc = op.Next;
                    return;
            }
        }
    }
}
