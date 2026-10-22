using System.Collections.Frozen;

namespace SkyNet.Game.Behaviour;

/// <summary>What an act id makes of the entity that carries it (one handler family of the
/// table at SKYNET.EXE 0x59b00).</summary>
public enum ActKind
{
    None, Light, Destructible, Demolish, Hint, Objective, Fail, Relay, Mover, Water, SoundCue,
    SoundLoop, Voice, ProxGate, Exit, ProxChain, Spawn, Pickup, Inert, Undecoded,
}

/// <summary>When a handler runs: in the tick bit 0 goes up, or every tick it is up.</summary>
public enum FireEdge { Edge, Level }

/// <summary>What a handler does to its own record after it ran.</summary>
public enum OnFire { Clear, Retire, Swap, Force1, None }

/// <summary>The mover families of the handler table.</summary>
public enum MoverFamily { Slide, Swing, Jump, Slide5F, Rot }

/// <summary>One row of the table: the act's kind, the slot's two config words and how it fires.</summary>
public sealed record ActRule(ActKind Kind, int P4 = 0, int P6 = 0, FireEdge Fire = FireEdge.Edge, OnFire OnFire = OnFire.Clear,
    MoverFamily Family = MoverFamily.Slide);

/// <summary>A mover act worked out: family, DOS axis (0 X, 1 Y, 2 Z), span of the travel, direction sign, speed (0 = instant).</summary>
public readonly record struct MoverParams(MoverFamily Family, int Axis, int P4, float Span, float Sign, float Speed)
{
    /// <summary>A continuous rotator (0x39-0x3E): turns while its bit is up, never arrives.</summary>
    public bool Spins => Family == MoverFamily.Rot && Speed > 0f;
}

/// <summary>
/// The SkyNET act rules as data, one row per act id (docs/game-logic/action-handlers.md; the
/// Godot port's rules_skynet.gd, whose rows this follows). Shared by the runtime and the tests.
/// </summary>
public static class ActRules
{
    public const int LightToggle = 0x01, LightFlicker = 0x02, LightStrobe = 0x03;
    public const int LightFadeUpFirst = 0x0D, LightFadeDownLast = 0x12;
    public const int DestructRamp = 0x18, DestructStage = 0x19;
    public const int Demolish = 0x1B;
    public const int HintFirst = 0x1C, HintLast = 0x25;
    public const int ObjectiveFirst = 0x26, ObjectiveLast = 0x2A;
    public const int Fail = 0x2B;
    public const int Relay = 0x2C, RelayAt = 1;
    public const int Voice = 0xED, SoundLoop = 0xEE;
    public const int ProxGate = 0xEF, Exit = 0xF0, ProxChainA = 0xF1, ProxChainB = 0xF2;
    public const int Spawn = 0xF3, Pickup = 0xFD, Unknown1A = 0x1A, SpentFirst = 0xFE;
    /// <summary>State bit 0x10: a walk-on pad (the floor polygon's owner record).</summary>
    public const int PadBit = 0x10;
    /// <summary>State bit 0x08 on a named variant-1 trigger: a wall button, answered by the crosshair.</summary>
    public const int WallButtonBit = 0x08;

    public const float SwingSpeed = 512f;
    public const float SlideSpeedSlow = 70f, SlideSpeedFast = 140f;
    public const float RotSpeed = 153f;
    public const float SlideSpeedScale = 2.2f;

    /// <summary>0xEF: 60 units from the eye (v1.01 0x1386a0), plus the Godot port's 26-unit capsule pad.</summary>
    public const float ProxGateRadius = 60f, PlayerRadius = 26f;
    public const float ProxChainARadius = 256f, ProxChainBRadius = 1024f;
    /// <summary>The port's touch radius of an 0xF0 doorway sprite (2D, feet) and its vertical window:
    /// one storey (128 u), so a doorway does not reach the floors under it (Future Shock MAP.013's
    /// exit @03d84 hangs 42 u over its own floor, 170 and 293 u over the two below).</summary>
    public const float TeleportTouchRadius = 90f, ProxVerticalWindow = 128f;
    /// <summary>How far the crosshair reaches a wall button (Godot USE_RAY).</summary>
    public const float UseRay = 600f;
    /// <summary>One TRANSFRM.PRS stage per qualifying hit or enable: the counter step of handler 0x120833.</summary>
    public const float DestructPerStage = 16f;
    /// <summary>Flicker/strobe lights step at 20 Hz.</summary>
    public const float LightFxTick = 1f / 20f;
    /// <summary>Path vehicles (cargo truck, convoy): enemy types 46-52 driving a marker chain.</summary>
    public const int PathVehicleFirst = 46, PathVehicleLast = 52;

    /// <summary>Mover ids → (family, p4, p6) from the 0x59b00 slot config words.</summary>
    public static readonly FrozenDictionary<int, (MoverFamily Family, int P4, int P6)> MoverTable = BuildMovers();

    /// <summary>One-shot sound cues 0xDB-0xEB → the sound id (slot +4 word).</summary>
    public static readonly FrozenDictionary<int, int> SoundOneShot = new Dictionary<int, int>
    {
        [0xdb] = 40, [0xdc] = 41, [0xdd] = 42, [0xde] = 43, [0xdf] = 44, [0xe0] = 45,
        [0xe1] = 46, [0xe2] = 47, [0xe3] = 77, [0xe4] = 93, [0xe5] = 95, [0xe6] = 109,
        [0xe7] = 110, [0xe8] = 111, [0xe9] = 112, [0xea] = 113, [0xeb] = 114,
    }.ToFrozenDictionary();

    /// <summary>Water movers 0xD6-0xDA → (DOS delta, the act this one turns into; 0 none).
    /// Delta 0: the level goes to the entity's own Y; a negative DOS delta raises the surface.</summary>
    public static readonly FrozenDictionary<int, (int Delta, int Partner)> WaterActs = new Dictionary<int, (int, int)>
    {
        [0xd6] = (0, 0), [0xd7] = (-170, 0), [0xd8] = (140, 0), [0xd9] = (-112, 0xda), [0xda] = (112, 0xd9),
    }.ToFrozenDictionary();

    private static readonly ActRule UndecodedRule = new(ActKind.Undecoded, OnFire: OnFire.None);
    private static readonly ActRule NotALight = new(ActKind.Inert, OnFire: OnFire.None);
    private static readonly FrozenDictionary<int, ActRule> Rules = Build();

    /// <summary>The row of an act id; an undecoded row when the table has none.</summary>
    public static ActRule For(int act) => Rules.TryGetValue(act, out var r) ? r : UndecodedRule;

    /// <summary>The row as a particular record carries it: a light act means a light only on a
    /// variant-2 record (the handlers write that record's intensity and enable words).</summary>
    public static ActRule For(int act, int variant)
    {
        var r = For(act);
        return variant != 2 && r.Kind == ActKind.Light ? NotALight : r;
    }

    public static bool IsMover(int act) => MoverTable.ContainsKey(act);
    public static bool IsDestructible(int act) => act is DestructRamp or DestructStage;
    public static bool IsProxChain(int act) => act is ProxChainA or ProxChainB;
    public static bool IsTrigger(int act) => act is ProxGate or ProxChainA or ProxChainB;

    /// <summary>What a mover act comes to (rules_skynet.gd mover_params).</summary>
    public static MoverParams Mover(int act)
    {
        var (fam, p4, limit) = MoverTable[act];
        if (limit >= 0x8000)
        {
            limit -= 0x10000;
        }

        // Slide/swing step +p6 for odd ids and −p6 for even ones; the others flip on a negative limit.
        var sign = (act & 1) != 0 ? 1f : -1f;
        if (fam != MoverFamily.Rot && fam != MoverFamily.Slide && limit < 0)
        {
            sign = -sign;
        }

        float span = Math.Abs(limit);
        if (fam == MoverFamily.Slide5F)
        {
            span = Math.Abs(limit << 4);
        }
        else if (fam == MoverFamily.Rot && limit == 0)
        {
            span = 2048f;
        }

        var speed = fam switch
        {
            MoverFamily.Slide => span >= 2048f ? SlideSpeedFast : SlideSpeedSlow,
            MoverFamily.Slide5F => p4 * SlideSpeedScale,
            MoverFamily.Jump => 0f,
            // The wall monitors' half turn is instant; only the slots with no angle turn at a rate.
            MoverFamily.Rot => limit == 0 ? RotSpeed : 0f,
            _ => SwingSpeed,
        };
        return new MoverParams(fam, Math.Clamp(p4, 0, 2), p4, span, sign, speed);
    }

    /// <summary>The light op name of a light act (the graph's vocabulary).</summary>
    public static string LightOp(int act) => act switch
    {
        LightToggle => "toggle",
        LightFlicker => "flicker",
        LightStrobe => "strobe",
        >= LightFadeUpFirst and < 0x10 => "fade_up",
        _ => "fade_down",
    };

    private static FrozenDictionary<int, (MoverFamily, int, int)> BuildMovers()
    {
        var m = new Dictionary<int, (MoverFamily, int, int)>();
        void Pairs(int first, MoverFamily f, int p4, params int[] p6s)
        {
            for (var i = 0; i < p6s.Length; i++)
            {
                m[first + i] = (f, p4, p6s[i]);
            }
        }

        Pairs(0x30, MoverFamily.Jump, 0, 512, 65024);
        Pairs(0x32, MoverFamily.Jump, 1, 512, 65024);
        Pairs(0x34, MoverFamily.Jump, 2, 512, 65024);
        Pairs(0x36, MoverFamily.Rot, 0, 1024);
        Pairs(0x37, MoverFamily.Rot, 1, 1024);
        Pairs(0x38, MoverFamily.Rot, 2, 1024);
        Pairs(0x39, MoverFamily.Rot, 0, 0, 0);
        Pairs(0x3b, MoverFamily.Rot, 1, 0, 0);
        Pairs(0x3d, MoverFamily.Rot, 2, 0, 0);
        Pairs(0x3f, MoverFamily.Slide, 0, 64, 64, 128, 128, 256, 256, 512, 512);
        Pairs(0x59, MoverFamily.Slide, 1, 64, 64, 128, 128, 256, 256);
        m[0x5f] = (MoverFamily.Slide5F, 316, 128);
        Pairs(0x61, MoverFamily.Slide, 1, 512, 512, 2048, 2048, 2560, 2560, 24, 24, 768, 768, 480, 480, 378, 378, 1024, 1024, 1520, 1520);
        Pairs(0x73, MoverFamily.Slide, 2, 64, 64, 128, 128, 256, 256, 512, 512, 640, 640, 384, 384);
        Pairs(0x8d, MoverFamily.Swing, 0, 256, 256, 512, 512, 1024, 1024);
        // 0xA5/0xA6 belong to the slide handler: the lift (MAP.233's elevator, 688 units up).
        Pairs(0xa5, MoverFamily.Slide, 1, 688, 688);
        Pairs(0xa7, MoverFamily.Swing, 1, 256, 256, 512, 512, 1024, 1024);
        // Zero slides: the limit word is 0, so they flip parity, clear their bit and never move.
        Pairs(0xbd, MoverFamily.Slide, 0, 0, 0, 0, 0);
        Pairs(0xc1, MoverFamily.Swing, 2, 256, 256, 512, 512, 1024, 1024);
        return m.ToFrozenDictionary();
    }

    private static FrozenDictionary<int, ActRule> Build()
    {
        var r = new Dictionary<int, ActRule>
        {
            [0x00] = new(ActKind.None, OnFire: OnFire.None),
            [LightToggle] = new(ActKind.Light),
            [LightFlicker] = new(ActKind.Light, Fire: FireEdge.Level, OnFire: OnFire.None),
            [LightStrobe] = new(ActKind.Light, Fire: FireEdge.Level, OnFire: OnFire.None),
            [Demolish] = new(ActKind.Demolish),
            [Fail] = new(ActKind.Fail, OnFire: OnFire.Retire),
            [Relay] = new(ActKind.Relay, P4: RelayAt, Fire: FireEdge.Level),
            [0xEC] = UndecodedRule,
            [Voice] = new(ActKind.Voice),
            [SoundLoop] = new(ActKind.SoundLoop, Fire: FireEdge.Level, OnFire: OnFire.None),
            [ProxGate] = new(ActKind.ProxGate, P6: (int)ProxGateRadius, OnFire: OnFire.Force1),
            [Exit] = new(ActKind.Exit),
            [ProxChainA] = new(ActKind.ProxChain, P4: (int)ProxChainARadius),
            [ProxChainB] = new(ActKind.ProxChain, P4: (int)ProxChainBRadius),
            [Spawn] = new(ActKind.Spawn),
            [Pickup] = new(ActKind.Pickup, Fire: FireEdge.Level, OnFire: OnFire.None),
            [Unknown1A] = UndecodedRule,
        };
        for (var a = LightFadeUpFirst; a <= LightFadeDownLast; a++)
        {
            r[a] = new ActRule(ActKind.Light, P6: a);
        }

        foreach (var a in new[] { 0x0A, 0x0C, 0x14, 0x17, 0x2D, 0xFE, 0xFF })
        {
            r[a] = new ActRule(ActKind.Inert, OnFire: OnFire.None);
        }

        r[DestructRamp] = new ActRule(ActKind.Destructible, P4: 4);
        r[DestructStage] = new ActRule(ActKind.Destructible);
        for (var a = HintFirst; a <= HintLast; a++)
        {
            r[a] = new ActRule(ActKind.Hint, P6: a - HintFirst, OnFire: OnFire.Retire);
        }

        for (var a = ObjectiveFirst; a <= ObjectiveLast; a++)
        {
            r[a] = new ActRule(ActKind.Objective, P6: a - ObjectiveFirst, OnFire: OnFire.Retire);
        }

        foreach (var (a, (fam, p4, p6)) in MoverTable)
        {
            r[a] = new ActRule(ActKind.Mover, p4, p6, FireEdge.Level, OnFire.Clear, fam);
        }

        foreach (var (a, (delta, partner)) in WaterActs)
        {
            r[a] = new ActRule(ActKind.Water, delta, partner, OnFire: partner != 0 ? OnFire.Swap : OnFire.Clear);
        }

        foreach (var (a, id) in SoundOneShot)
        {
            r[a] = new ActRule(ActKind.SoundCue, P4: id);
        }

        return r.ToFrozenDictionary();
    }
}
