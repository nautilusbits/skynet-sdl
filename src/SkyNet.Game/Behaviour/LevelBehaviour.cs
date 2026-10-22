using RetroEng.Loop;
using System.Numerics;
using RetroEng.Diagnostics;
using RetroEng.Physics;
using SkyNet.Formats;
using SkyNet.Game.Collision;
using SkyNet.Game.DevTools;
using SkyNet.Game.Enemies;

namespace SkyNet.Game.Behaviour;

/// <summary>What the behaviour needs of the player each tick, in level axes.</summary>
/// <param name="Feet">The feet (<c>PlayerController.Position</c>).</param>
/// <param name="Eye">The eye: gates and proximity triggers measure from it, true 3D.</param>
/// <param name="FloorOwner">The record the feet stand on (<c>PlayerController.FloorOwner</c>), −1 none: walk-on pads and lifts.</param>
/// <param name="UsePressed">The use key went down this tick.</param>
/// <param name="AimedOwner">The record the crosshair ray hit within <see cref="ActRules.UseRay"/> when the key went down, −1 none.</param>
public readonly record struct PlayerProbe(Vector3 Feet, Vector3 Eye, int FloorOwner = -1, bool UsePressed = false, int AimedOwner = -1);

/// <summary>
/// The entity logic of one level: every MAP record's live state, act and link bytes, the action
/// chains walked as DOS ObjFlipLink walks them, and the handlers of the 0x59b00 table — movers,
/// gates and proximity triggers, walk-on pads, exits, lights, destructibles, demolition,
/// messages and the objective counter. Ported from the Godot port (trigger_runtime.gd,
/// behaviour.gd and the node scripts of scripts/level/), whose rules it follows, deviations
/// included (docs/game-logic/action-handlers.md).
/// <para>Deterministic and headless: ticked by the fixed loop, no rendering; what it does goes
/// out through <see cref="IBehaviourListener"/>, and the level draws and collides movable
/// entities through <see cref="IEntityPoses"/>.</para>
/// </summary>
public sealed partial class LevelBehaviour : IEntityPoses
{
    private sealed class MoverState(MapEntity e, MoverParams p)
    {
        public readonly MapEntity Entity = e;
        public readonly MoverParams P = p;
        public readonly Vector3 Base = BehaviourModel.Position(e);
        public readonly Vector3 Euler = BehaviourModel.Euler(e);
        public float Progress;
        public float Dir = 1f;
        public bool Running;
        public Matrix4x4 Delta = Matrix4x4.Identity;
        /// <summary>The progress at the previous tick's end (render interpolation; never simulated).</summary>
        public float Previous;
    }

    private sealed class TriggerState
    {
        public bool Latched, EdgeDone, ExitWalked;
    }

    private sealed class WreckState(int stages)
    {
        public readonly int Stages = stages;
        public int Stage;
        public float Accum;
    }

    private sealed class LightState(MapEntity e)
    {
        public int Enable = e.LightEnable;
        public int Intensity = e.LightIntensity;
        public bool FxShowing, FxOn = true, Announced;
    }

    private readonly MapFile _map;
    private readonly TransfrmPrs _transfrm;
    private readonly MissionProgress _mission;
    private readonly IBehaviourListener _out;
    private readonly SimRandom _random;

    private readonly Dictionary<int, int> _state = [];
    private readonly Dictionary<int, int> _act = [];
    private readonly Dictionary<int, int> _link = [];
    private readonly Dictionary<int, float> _hp = [];
    private readonly HashSet<int> _spent = [];
    private readonly HashSet<int> _armed = [];

    private readonly Dictionary<int, NodeKind> _kind = [];
    private readonly Dictionary<int, MoverState> _movers = [];
    private readonly Dictionary<int, TriggerState> _triggers = [];
    private readonly List<int> _prox = [];
    private readonly List<int> _exits = [];
    private readonly Dictionary<int, bool> _touching = [];
    private readonly Dictionary<int, WreckState> _wrecks = [];
    private readonly Dictionary<int, LightState> _lights = [];
    private readonly List<int> _relays = [], _water = [], _spawns = [], _rams = [], _demolishers = [];
    private readonly HashSet<int> _cueSpent = [];
    private readonly HashSet<int> _vehicles = [];
    private readonly HashSet<int> _hidden = [], _noCollide = [];
    /// <summary>The wall switches showing their back (ON) face: turned half round by a press.</summary>
    private readonly HashSet<int> _turned = [];
    private bool _useEdge;
    private float _lightClock;

    public LevelBehaviour(MapFile map, TransfrmPrs transfrm, MissionProgress mission, IBehaviourListener? listener = null, int seed = 1, SimRandom? random = null)
    {
        _map = map;
        _transfrm = transfrm;
        _mission = mission;
        _out = listener ?? new EffectLog();
        _random = random ?? new SimRandom((ulong)seed);
        var targeted = BehaviourModel.ChainTargets(map);
        foreach (var e in map.Entities)
        {
            var off = e.FileOffset;
            _state[off] = e.StateByte;
            _act[off] = e.ActType;
            _link[off] = e.LinkNext;
            if (e.Variant == MapEntityVariant.Mesh && e.HitPoints > 0)
            {
                _hp[off] = e.HitPoints;
            }

            if (e.MarkerType == 2 && e.EnemyType is >= ActRules.PathVehicleFirst and <= ActRules.PathVehicleLast)
            {
                _vehicles.Add(off);
            }

            var kind = BehaviourModel.KindOf(map, e, transfrm, targeted);
            if (kind == NodeKind.None)
            {
                continue;
            }

            _kind[off] = kind;
            var variant = (int)e.Variant;
            switch (kind)
            {
                case NodeKind.Mover:
                    _movers[off] = new MoverState(e, ActRules.Mover(e.ActType));
                    break;
                case NodeKind.Trigger:
                    _triggers[off] = new TriggerState();
                    if (ActRules.IsProxChain(e.ActType) || (e.ActType == ActRules.ProxGate && (e.StateByte & 6) is 0 or 6))
                    {
                        _prox.Add(off);
                    }

                    break;
                case NodeKind.Exit:
                    _exits.Add(off);
                    _touching[off] = false;
                    break;
                case NodeKind.Destructible:
                    var stages = transfrm.StagesOf(map.EntityName(e)).Length;
                    if (stages > 0)
                    {
                        _wrecks[off] = new WreckState(stages);
                    }

                    break;
                case NodeKind.Raw:
                    switch (ActRules.For(e.ActType, variant).Kind)
                    {
                        case ActKind.Relay: _relays.Add(off); break;
                        case ActKind.Water: _water.Add(off); break;
                        case ActKind.Light: _lights[off] = new LightState(e); break;
                    }

                    if (e.ActType == ActRules.Spawn)
                    {
                        _spawns.Add(off);
                    }

                    break;
            }

            if (ActRules.IsDestructible(e.ActType))
            {
                _rams.Add(off);
            }
            else if (e.ActType == ActRules.Demolish)
            {
                _demolishers.Add(off);
            }
        }

        Paths = new PathVehicles(this, map.Entities.Where(e => _vehicles.Contains(e.FileOffset)));
    }

    /// <summary>The marker-path vehicles (AI state 11): ticked here, after the spawns.</summary>
    public PathVehicles Paths { get; private set; } = null!;

    /// <summary>Builds the behaviour of a parsed map with the data's TRANSFRM.PRS.</summary>
    public static LevelBehaviour Create(GameData data, MapFile map, MissionProgress mission, IBehaviourListener? listener = null) =>
        new(map, BehaviourModel.LoadTransfrm(data), mission, listener);

    public MapFile Map => _map;
    public TransfrmPrs Transfrm => _transfrm;
    public MissionProgress Mission => _mission;
    /// <summary>An exit was taken: the level asks for its map change once.</summary>
    public bool ExitTaken { get; private set; }
    public ExitRequest? PendingExit { get; private set; }
    public IReadOnlyCollection<int> Movers => _movers.Keys;
    public IReadOnlyCollection<int> Exits => _exits;
    public IReadOnlyCollection<int> Triggers => _triggers.Keys;

    // ---------------------------------------------------------------- reading

    public int State(int off) => _state.GetValueOrDefault(off);
    public int Act(int off) => _act.GetValueOrDefault(off);
    public int Link(int off) => _link.GetValueOrDefault(off);
    public bool Enabled(int off) => (State(off) & 1) != 0;
    /// <summary>Enabled now, or armed by a chain earlier in this tick.</summary>
    public bool Fires(int off) => Enabled(off) || _armed.Contains(off);
    public bool Spent(int off) => _spent.Contains(off);
    public bool HasHp(int off) => _hp.ContainsKey(off);
    public float Hp(int off) => _hp.GetValueOrDefault(off);
    public NodeKind KindOf(int off) => _kind.GetValueOrDefault(off);
    public int StageOf(int off) => _wrecks.TryGetValue(off, out var w) ? w.Stage : 0;
    /// <summary>A mover's travel so far (units or 11-bit angle units, 0 .. span) and its next direction.</summary>
    public (float Progress, float Span, float Dir) MoverProgress(int off) =>
        _movers.TryGetValue(off, out var m) ? (m.Progress, m.P.Span, m.Dir) : (0f, 0f, 0f);
    public bool MoverRunning(int off) => _movers.TryGetValue(off, out var m) && m.Running;
    /// <summary>Is a light on (variant-2 enable word positive, flicker phase on)?</summary>
    public bool LightOn(int off) => _lights.TryGetValue(off, out var l) && l.Enable > 0 && (!l.FxShowing || l.FxOn);
    public int LightIntensity(int off) => _lights.TryGetValue(off, out var l) ? l.Intensity : -1;
    /// <summary>Does the behaviour run this light (a variant-2 record with a light act)?</summary>
    public bool HasLight(int off) => _lights.ContainsKey(off);

    /// <summary>Can a weapon still damage this record?</summary>
    public bool IsDamageable(int off) => !_spent.Contains(off) && (_hp.ContainsKey(off) || _wrecks.ContainsKey(off));

    /// <summary>Is this wall switch turned to its ON face (<see cref="BehaviourModel.IsSwitch"/>)?</summary>
    public bool SwitchOn(int off) => _turned.Contains(off);

    public bool TryGetPose(int offset, out EntityPose pose) => TryGetPose(offset, 1f, out pose);

    /// <summary>The pose drawn <paramref name="alpha"/> of the way from the previous tick: a mover between
    /// its last two progresses (<see cref="Interpolation"/>; an instant one and a jump at the latest).</summary>
    public bool TryGetPose(int offset, float alpha, out EntityPose pose)
    {
        var delta = _movers.TryGetValue(offset, out var m) ? DrawnDelta(m, alpha)
            : _turned.Contains(offset) && _map.TryGetEntityAt(offset, out var e) ? SwitchDelta(e) : Matrix4x4.Identity;
        var stage = _wrecks.TryGetValue(offset, out var w) ? w.Stage : 0;
        pose = new EntityPose(delta, stage, !_hidden.Contains(offset), !_noCollide.Contains(offset));
        return true;
    }

    private static Matrix4x4 DrawnDelta(MoverState m, float alpha)
    {
        if (alpha >= 1f || m.Previous == m.Progress || m.P.Speed <= 0f || m.P.Family == MoverFamily.Jump)
        {
            return m.Delta;
        }

        var progress = m.P.Spins ? Interpolation.LerpAngle(m.Previous, m.Progress, alpha, 2048f) : float.Lerp(m.Previous, m.Progress, alpha);
        return DeltaAt(m, progress);
    }

    /// <summary>A tick's start: every mover's progress becomes the one the frames glide from.</summary>
    public void KeepPoses()
    {
        foreach (var m in _movers.Values)
        {
            m.Previous = m.Progress;
        }
    }

    // ---------------------------------------------------------------- save and load

    /// <summary>The level's whole live state, for a save (<see cref="BehaviourSnapshot"/>). Taken
    /// between ticks: nothing armed, no use edge pending.</summary>
    public BehaviourSnapshot Snapshot()
    {
        static int[] Sorted(IEnumerable<int> set) => [.. set.Order()];
        return new BehaviourSnapshot(
            [.. _state.Keys.Order().Select(off => new RecordBytes(off, State(off), Act(off), Link(off)))],
            [.. _hp.OrderBy(p => p.Key).Select(p => (p.Key, p.Value))],
            Sorted(_spent),
            Sorted(_cueSpent),
            Sorted(_hidden),
            Sorted(_noCollide),
            [.. _movers.OrderBy(p => p.Key).Select(p => new MoverSnapshot(p.Key, p.Value.Progress, p.Value.Dir, p.Value.Running))],
            [.. _triggers.OrderBy(p => p.Key).Select(p => new TriggerSnapshot(p.Key, p.Value.Latched, p.Value.EdgeDone, p.Value.ExitWalked))],
            [.. _touching.OrderBy(p => p.Key).Select(p => (p.Key, p.Value))],
            [.. _wrecks.OrderBy(p => p.Key).Select(p => new WreckSnapshot(p.Key, p.Value.Stage, p.Value.Accum))],
            [.. _lights.OrderBy(p => p.Key).Select(p => new LightSnapshot(p.Key, p.Value.Enable, p.Value.Intensity, p.Value.FxShowing,
                p.Value.FxOn, p.Value.Announced))],
            _lightClock,
            Paths.Snapshot(),
            Sorted(_turned));
    }

    /// <summary>The behaviour's whole state folded into <paramref name="h"/> for the per-tick state
    /// hash (docs/demos.md): what <see cref="Snapshot"/> keeps and the armed records, each table
    /// hashed as an unordered set, without allocating.</summary>
    public void HashInto(ref StateHash h)
    {
        ulong sum = 0;
        foreach (var (off, v) in _state)
        {
            sum += StateHash.Entry(off, (ulong)(uint)v | ((ulong)(uint)Act(off) << 32) ^ ((ulong)(uint)Link(off) * 0x9E3779B97F4A7C15UL));
        }

        h.AddUnordered(sum);
        h.AddUnordered(Sum(_hp));
        h.AddUnordered(Sum(_spent, 1));
        h.AddUnordered(Sum(_cueSpent, 2));
        h.AddUnordered(Sum(_hidden, 3));
        h.AddUnordered(Sum(_noCollide, 4));
        h.AddUnordered(Sum(_armed, 5));
        h.AddUnordered(Sum(_turned, 6));
        sum = 0;
        foreach (var (off, m) in _movers)
        {
            var e = new StateHash();
            e.Add(m.Progress);
            e.Add(m.Dir);
            e.Add(m.Running);
            sum += StateHash.Entry(off, e);
        }

        h.AddUnordered(sum);
        sum = 0;
        foreach (var (off, t) in _triggers)
        {
            sum += StateHash.Entry(off, (t.Latched ? 1UL : 0) | (t.EdgeDone ? 2UL : 0) | (t.ExitWalked ? 4UL : 0));
        }

        h.AddUnordered(sum);
        sum = 0;
        foreach (var (off, touching) in _touching)
        {
            sum += StateHash.Entry(off, touching ? 1UL : 2UL);
        }

        h.AddUnordered(sum);
        sum = 0;
        foreach (var (off, w) in _wrecks)
        {
            var e = new StateHash();
            e.Add(w.Stage);
            e.Add(w.Accum);
            sum += StateHash.Entry(off, e);
        }

        h.AddUnordered(sum);
        sum = 0;
        foreach (var (off, l) in _lights)
        {
            var e = new StateHash();
            e.Add(l.Enable);
            e.Add(l.Intensity);
            e.Add(l.FxShowing);
            e.Add(l.FxOn);
            e.Add(l.Announced);
            sum += StateHash.Entry(off, e);
        }

        h.AddUnordered(sum);
        h.Add(_lightClock);
        h.Add(_useEdge);
        h.Add(ExitTaken);
        Paths.HashInto(ref h);
    }

    private static ulong Sum(Dictionary<int, float> table)
    {
        ulong sum = 0;
        foreach (var (k, v) in table)
        {
            var e = new StateHash();
            e.Add(v);
            sum += StateHash.Entry(k, e);
        }

        return sum;
    }

    private static ulong Sum(HashSet<int> set, ulong salt)
    {
        ulong sum = 0;
        foreach (var k in set)
        {
            sum += StateHash.Entry(k, salt);
        }

        return sum;
    }

    /// <summary>
    /// Puts a <see cref="Snapshot"/> of the same map back onto a freshly built behaviour (one that
    /// has not been <see cref="Start"/>ed: the saved state already holds what the start did). Records
    /// the map does not have are ignored; the movers' poses follow their travel; no cue runs.
    /// </summary>
    public void Restore(BehaviourSnapshot s)
    {
        foreach (var r in s.Records)
        {
            if (_state.ContainsKey(r.Offset))
            {
                _state[r.Offset] = r.State & 0xFF;
                _act[r.Offset] = r.Act;
                _link[r.Offset] = r.Link;
            }
        }

        _hp.Clear();
        foreach (var (off, hp) in s.HitPoints)
        {
            _hp[off] = hp;
        }

        Refill(_spent, s.Spent);
        Refill(_cueSpent, s.CueSpent);
        Refill(_hidden, s.Hidden);
        Refill(_noCollide, s.NoCollide);
        Refill(_turned, s.Turned ?? []);
        foreach (var m in s.Movers)
        {
            if (_movers.TryGetValue(m.Offset, out var mover))
            {
                mover.Progress = mover.Previous = m.Progress;
                mover.Dir = m.Dir;
                mover.Running = m.Running;
                Apply(mover);
            }
        }

        foreach (var t in s.Triggers)
        {
            if (_triggers.TryGetValue(t.Offset, out var trigger))
            {
                (trigger.Latched, trigger.EdgeDone, trigger.ExitWalked) = (t.Latched, t.EdgeDone, t.ExitWalked);
            }
        }

        foreach (var (off, touching) in s.Doorways)
        {
            if (_touching.ContainsKey(off))
            {
                _touching[off] = touching;
            }
        }

        foreach (var w in s.Wrecks)
        {
            if (_wrecks.TryGetValue(w.Offset, out var wreck))
            {
                (wreck.Stage, wreck.Accum) = (w.Stage, w.Accum);
            }
        }

        foreach (var l in s.Lights)
        {
            if (_lights.TryGetValue(l.Offset, out var light))
            {
                (light.Enable, light.Intensity, light.FxShowing, light.FxOn, light.Announced) = (l.Enable, l.Intensity, l.FxShowing, l.FxOn, l.Announced);
            }
        }

        _lightClock = s.LightClock;
        _armed.Clear();
        _useEdge = false;
        ExitTaken = false;
        PendingExit = null;
        Paths.Restore(s.Paths);
    }

    private static void Refill(HashSet<int> set, IEnumerable<int> values)
    {
        set.Clear();
        set.UnionWith(values);
    }

    // ---------------------------------------------------------------- start

    /// <summary>The level comes up (Behaviour._ready): an exit laid down armed is disarmed, every
    /// other node whose bit is up runs its cue once (ambient loops start). Then the proximity
    /// triggers and doorways around the spawn point are pre-latched, so the player does not bounce
    /// straight back through the gate he arrived by.</summary>
    public void Start(Vector3 feet, Vector3 eye)
    {
        foreach (var (off, kind) in _kind)
        {
            if (kind == NodeKind.Exit)
            {
                if (Enabled(off))
                {
                    ClearEnable(off);
                }

                continue;
            }

            if (Enabled(off))
            {
                Fire(off);
            }
        }

        Arm(feet, eye);
    }

    /// <summary>Pre-latch: the triggers whose measure holds the eye and the doorways the feet touch
    /// count as already entered (Behaviour.arm_proximity).</summary>
    public void Arm(Vector3 feet, Vector3 eye)
    {
        foreach (var off in _prox)
        {
            if (Inside(off, eye))
            {
                _triggers[off].Latched = true;
            }
        }

        foreach (var off in _exits)
        {
            _touching[off] = Within(off, feet);
        }
    }

    // ---------------------------------------------------------------- the tick

    /// <summary>One fixed tick. Returns how far a lift or a moving floor under the feet carried the
    /// player this tick (add it to the position).</summary>
    public Vector3 Tick(float dt, in PlayerProbe player)
    {
        if (Role == LevelRole.Client)
        {
            return ClientTick(dt, player);
        }

        if (player.FloorOwner >= MapFile.GridOffset)
        {
            WalkOn(player.FloorOwner);
        }

        if (player.UsePressed)
        {
            Use(player);
        }

        LightTick(dt);
        var carry = MoverTick(dt, player.Feet, player.FloorOwner);
        ProxTick(player.Eye);
        DestructTick();
        DemolishTick();
        RelayTick();
        SpawnTick();
        using (Prof.Zone(GameZones.PathVehicles))
        {
            Paths.Tick(dt, player.Feet);
        }

        WaterTick();
        ExitTick(player.Feet, player.UsePressed);
        _armed.Clear();
        return carry;
    }

    /// <summary>The use key went down: the record under the crosshair (a wall button, or a prop
    /// whose state bit 1 answers), else the nearest wall button in its measure; the 0xEF gates in
    /// reach fire in the proximity sweep of the same tick.</summary>
    private void Use(in PlayerProbe player)
    {
        var done = false;
        if (player.AimedOwner >= 0 && !_spent.Contains(player.AimedOwner) && _map.TryGetEntityAt(player.AimedOwner, out _))
        {
            done = _triggers.ContainsKey(player.AimedOwner)
                ? ProxUse(player.AimedOwner, player.Eye, aimed: true)
                : ActivateProp(player.AimedOwner);
        }

        if (!done)
        {
            UseNearby(player.Eye);
        }

        _useEdge = true;
    }

    /// <summary>A prop with state bit 1 answers the key (Behaviour.on_player_activate).</summary>
    private bool ActivateProp(int off)
    {
        if ((State(off) & 2) == 0)
        {
            return false;
        }

        Flip(off);
        return true;
    }

    private void UseNearby(Vector3 eye)
    {
        var best = -1;
        var bestD = float.MaxValue;
        foreach (var off in _triggers.Keys)
        {
            if (_spent.Contains(off) || !IsWallButton(off))
            {
                continue;
            }

            var d = Vector3.Distance(PositionOf(off), eye);
            if (d > Measure(off) || d >= bestD)
            {
                continue;
            }

            bestD = d;
            best = off;
        }

        if (best >= 0)
        {
            ProxUse(best, eye, aimed: false);
        }
    }

    /// <summary>Activates a trigger by the key (trigger.gd prox_use). A wall button is reached by
    /// the crosshair and needs no distance; any other measured trigger needs the eye inside it.</summary>
    public bool ProxUse(int off, Vector3 eye, bool aimed = false)
    {
        if (!_triggers.TryGetValue(off, out var t))
        {
            return false;
        }

        var a = Act(off);
        var measured = Runs(off);
        if (!IsWallButton(off) && measured && !Inside(off, eye))
        {
            return false;
        }

        if ((State(off) & 2) == 0 && !measured)
        {
            return false;
        }

        var chain = ActRules.IsProxChain(a);
        if (chain && !Enabled(off))
        {
            return false;
        }

        if (a == ActRules.ProxGate)
        {
            TurnSwitch(off);
            t.EdgeDone = true;
            if (ChainExit(off) >= 0)
            {
                return UseExitThrough(off);
            }

        }

        Flip(off);
        if (chain)
        {
            ClearEnable(off);
        }

        return true;
    }

    /// <summary>A record whose state bit 0x10 owns the floor under the feet: clear the bit and walk
    /// its chain, once (FUN_00139d5e).</summary>
    public void WalkOn(int off)
    {
        var st = State(off);
        if ((st & ActRules.PadBit) == 0 || _spent.Contains(off) || !_state.ContainsKey(off))
        {
            return;
        }

        SetState(off, st & ~ActRules.PadBit);
        _out.Pad(off);
        Flip(off);
    }

    private void ProxTick(Vector3 eye)
    {
        if (_useEdge && !ExitTaken && GateToExit(eye) is var gate and >= 0)
        {
            if (!_triggers[gate].EdgeDone)
            {
                TurnSwitch(gate);           // not twice in a tick: the key's own press already turned it
            }

            UseExitThrough(gate);
        }

        foreach (var off in _prox)
        {
            if (IsWallButton(off) || _spent.Contains(off))
            {
                continue;
            }

            ProxWatch(off, eye);
        }

        _useEdge = false;
        foreach (var t in _triggers.Values)
        {
            t.EdgeDone = false;
        }
    }

    private void ProxWatch(int off, Vector3 eye)
    {
        var t = _triggers[off];
        var here = Inside(off, eye);
        var a = Act(off);
        if (a == ActRules.ProxGate)
        {
            if (_useEdge && here && !t.EdgeDone && ChainExit(off) < 0)
            {
                Flip(off);
                TurnSwitch(off);
            }

            return;
        }

        var chain = ActRules.IsProxChain(a);
        if (chain && !Enabled(off))
        {
            return;
        }

        if (here && !t.Latched)
        {
            t.Latched = true;
            Flip(off);
            if (chain)
            {
                ClearEnable(off);
            }
        }
        else if (!here && t.Latched)
        {
            t.Latched = false;
        }
    }

    /// <summary>The first 0xEF gate in reach of the eye whose chain ends in a doorway.</summary>
    private int GateToExit(Vector3 eye)
    {
        foreach (var off in _prox)
        {
            if (Act(off) == ActRules.ProxGate && !_spent.Contains(off) && Inside(off, eye) && ChainExit(off) >= 0)
            {
                return off;
            }
        }

        return -1;
    }

    /// <summary>A gate whose chain ends in a doorway: walk the chain once, then take the doorway
    /// if the walk did not (Behaviour.use_exit_through).</summary>
    private bool UseExitThrough(int gate)
    {
        if (ExitTaken)
        {
            return false;
        }

        var exit = ChainExit(gate);
        if (exit < 0 || !_exits.Contains(exit))
        {
            return false;
        }

        var t = _triggers[gate];
        if (!t.ExitWalked)
        {
            t.ExitWalked = true;
            Flip(gate);
        }

        if (ExitTaken)
        {
            return true;
        }

        SetState(exit, State(exit) | 1);
        return FireExit(exit);
    }

    /// <summary>The 0xF0 record a chain from <paramref name="off"/> reaches, −1 none.</summary>
    public int ChainExit(int off)
    {
        var seen = new HashSet<int>();
        for (var cur = off; cur > 0 && seen.Add(cur); cur = Link(cur))
        {
            if (Act(cur) == ActRules.Exit)
            {
                return cur;
            }
        }

        return -1;
    }

    private bool Runs(int off)
    {
        var a = Act(off);
        if (ActRules.IsProxChain(a))
        {
            return true;
        }

        return a == ActRules.ProxGate && (State(off) & 6) is 0 or 6;
    }

    /// <summary>The tail of the 0xEF handler (v1.01 0x13870b, after its ObjFlipLink): a variant-1
    /// gate with state bit 3 turns its yaw half round, every press (<see cref="BehaviourModel.IsSwitch"/>).
    /// Only the use-key handler does it: a walk-on pad or a chain reaching the gate does not.</summary>
    private void TurnSwitch(int off)
    {
        if ((State(off) & ActRules.WallButtonBit) == 0 || !_map.TryGetEntityAt(off, out var e) || !BehaviourModel.IsSwitch(e))
        {
            return;
        }

        if (!_turned.Remove(off))
        {
            _turned.Add(off);
        }
    }

    /// <summary>A turned switch's move from where the map placed it: the yaw plus 0x400.</summary>
    private static Matrix4x4 SwitchDelta(MapEntity e)
    {
        var euler = BehaviourModel.Euler(e);
        var at = BehaviourModel.Position(e);
        var placed = BehaviourModel.Placement(euler, at);
        var turned = BehaviourModel.Placement(euler with { Y = (euler.Y + BehaviourModel.SwitchTurn) % 2048f }, at);
        return Matrix4x4.Invert(placed, out var inv) ? inv * turned : Matrix4x4.Identity;
    }

    private bool IsWallButton(int off) =>
        (State(off) & ActRules.WallButtonBit) != 0 && _map.TryGetEntityAt(off, out var e)
        && e.Variant == MapEntityVariant.Mesh && e.NameIndex >= 0;

    private float Measure(int off) => Act(off) switch
    {
        ActRules.ProxChainA => ActRules.ProxChainARadius,
        ActRules.ProxChainB => ActRules.ProxChainBRadius,
        _ => ActRules.ProxGateRadius + ActRules.PlayerRadius,
    };

    private bool Inside(int off, Vector3 eye) => Vector3.Distance(PositionOf(off), eye) <= Measure(off);

    private Vector3 PositionOf(int off) => _map.TryGetEntityAt(off, out var e) ? BehaviourModel.Position(e) : default;

    // ---------------------------------------------------------------- exits

    private bool Within(int off, Vector3 feet)
    {
        var p = PositionOf(off);
        if (MathF.Abs(feet.Y - p.Y) > ActRules.ProxVerticalWindow)
        {
            return false;
        }

        return new Vector2(feet.X - p.X, feet.Z - p.Z).Length() <= ActRules.TeleportTouchRadius;
    }

    private void ExitTick(Vector3 feet, bool use)
    {
        foreach (var off in _exits)
        {
            var here = Within(off, feet);
            if (here && !_touching[off])
            {
                SetState(off, State(off) | 1);       // touching the doorway arms it (port rule)
            }

            _touching[off] = here;
            if (_armed.Contains(off))
            {
                SetState(off, State(off) | 1);
                FireExit(off);
            }
        }

        // The use key takes an armed doorway the feet touch (activate_teleport).
        if (use && !ExitTaken)
        {
            foreach (var off in _exits)
            {
                if (Enabled(off) && Within(off, feet) && FireExit(off))
                {
                    break;
                }
            }
        }
    }

    private bool FireExit(int off)
    {
        if (ExitTaken || !Enabled(off) || !_map.TryGetEntityAt(off, out var e))
        {
            return false;
        }

        ClearEnable(off);                          // one-shot (0x138081)
        ExitTaken = true;
        var request = new ExitRequest(off, e.Param16, e.Param8);
        PendingExit = request;
        _out.Exit(request);
        return true;
    }

    /// <summary>The map switch was refused: the level may ask again.</summary>
    public void ExitRefused()
    {
        ExitTaken = false;
        PendingExit = null;
    }

    // ---------------------------------------------------------------- ObjFlipLink

    /// <summary>
    /// ObjFlipLink (FUN_001394aa): walk the chain from <paramref name="start"/>, the start
    /// included, toggling bit 0 of each record (an 0xEF gate stays live) and running the cue of
    /// each one that goes up as it is reached; the walk stops after an actor. What went up is
    /// remembered for the rest of the tick, so several flips of one chain in one tick cannot cancel
    /// a one-shot (the Godot port's rule).
    /// </summary>
    public void Flip(int start)
    {
        var visited = new HashSet<int>();
        var byProx = ActRules.IsProxChain(Act(start));
        var stack = new Stack<int>();
        stack.Push(start);
        while (stack.Count > 0)
        {
            var off = stack.Pop();
            if (!visited.Add(off) || !_state.ContainsKey(off))
            {
                continue;
            }

            var a = Act(off);
            var s = State(off) ^ 1;
            var touchedExit = false;
            if (a == ActRules.ProxGate)
            {
                s |= 1;
            }
            else if (byProx && a == ActRules.Exit && Enabled(off))
            {
                // A doorway armed by the port's touch when a proximity chain reaches it: the chain is
                // the rise DOS would have fired on (Godot trigger_runtime.flip).
                s |= 1;
                touchedExit = true;
            }

            SetState(off, s);
            if ((s & 1) != 0)
            {
                _armed.Add(off);
                Fire(off);
                if (touchedExit && Enabled(off))
                {
                    _armed.Remove(off);
                    SetState(off, State(off) & ~1);
                }
            }
            else if (_kind.GetValueOrDefault(off) == NodeKind.SoundLoop)
            {
                _out.LoopStop(off);
            }

            if (_map.TryGetEntityAt(off, out var rec) && rec.IsActor)
            {
                break;
            }

            var next = Link(off);
            if (next > 0)
            {
                stack.Push(next);
            }
        }
    }

    /// <summary>One record's own one-shot cue (runtime.fire): the cue runs, its bit clears the way
    /// its handler clears it (the rule of the act the map authored), a hint or objective retires.</summary>
    private void Fire(int off)
    {
        if (_vehicles.Contains(off))
        {
            _out.PathStart(off);
            return;
        }

        if (!_kind.TryGetValue(off, out var kind) || !_map.TryGetEntityAt(off, out var e))
        {
            return;
        }

        if (Role == LevelRole.Server && kind is NodeKind.SoundCue or NodeKind.Voice or NodeKind.SoundLoop)
        {
            _fired.Add(off);                       // a cue no state byte can tell a client about
        }

        var at = BehaviourModel.Position(e);
        var retire = false;
        switch (kind)
        {
            case NodeKind.SoundCue:
                _out.Sound(off, ActRules.SoundOneShot.GetValueOrDefault(e.ActType, -1), at);
                break;
            case NodeKind.Voice:
                _out.Voice(off, e.Param16, at);
                break;
            case NodeKind.SoundLoop:
                _out.LoopStart(off, e.Variant == MapEntityVariant.Sprite ? e.Param16 : -1, at);
                break;
            case NodeKind.Message:
                if (_cueSpent.Add(off))
                {
                    var index = e.ActType - ActRules.HintFirst;
                    _out.Hint(off, index, _mission.HintText(index));
                }

                retire = true;
                break;
            case NodeKind.Objective:
                if (_cueSpent.Add(off))
                {
                    if (e.ActType == ActRules.Fail)
                    {
                        _mission.Fail();
                        _out.MissionFailed(off);
                    }
                    else
                    {
                        var index = e.ActType - ActRules.ObjectiveFirst;
                        var wasComplete = _mission.Complete;
                        var text = _mission.Take(index);
                        _out.Objective(off, index, _mission.Left, text);
                        if (_mission.Complete && !wasComplete)
                        {
                            _out.MissionComplete();
                        }
                    }
                }

                retire = true;
                break;
            case NodeKind.Exit:
                FireExit(off);
                break;
            default:
                return;                            // no cue of its own: a level kind or a sweep's
        }

        if (ActRules.For(e.ActType).OnFire != OnFire.None)
        {
            ClearEnable(off);
        }

        if (retire)
        {
            _act[off] = 0xFF;
        }
    }

    private void SetState(int off, int value) => _state[off] = value & 0xFF;

    private void ClearEnable(int off) => _state[off] = State(off) & ~1;

    internal void ClearEnableOf(int off)
    {
        if (_state.ContainsKey(off))
        {
            ClearEnable(off);
        }
    }

    /// <summary>A path vehicle ran its path out at <paramref name="last"/>, whose link reaches
    /// <paramref name="end"/>, a record that is not a marker: the path has run (the graph's
    /// <c>path@vehicle</c>), the end's chain is walked once and the link to it is cut (0x1274b2).</summary>
    internal void PathEnded(int vehicle, int last, int end)
    {
        _out.PathEnd(vehicle);
        Flip(end);
        _link[last] = 0;
    }

    // ---------------------------------------------------------------- movers

    private Vector3 MoverTick(float dt, Vector3 feet, int floorOwner)
    {
        var carry = Vector3.Zero;
        foreach (var (off, m) in _movers)
        {
            var before = m.Delta;
            MoverWatch(off, m, dt);
            if (off == floorOwner && before != m.Delta && Matrix4x4.Invert(before, out var inv))
            {
                carry = Vector3.Transform(feet, inv * m.Delta) - feet;
            }
        }

        return carry;
    }

    /// <summary>mover.gd mover_watch: while the bit is up, travel to the far end (or back),
    /// then clear the bit and reverse; a continuous rotator turns for as long as the bit is up.</summary>
    private void MoverWatch(int off, MoverState m, float dt)
    {
        if (!Enabled(off))
        {
            if (m.Running && !m.P.Spins)
            {
                _out.MoverStop(off);
            }

            m.Running = false;
            return;
        }

        if (m.P.Spins)
        {
            if (!m.Running)
            {
                m.Running = true;
                _out.Spin(off);
            }

            m.Progress = (m.Progress + m.P.Speed * dt) % 2048f;
            Apply(m);
            return;
        }

        if (m.P.Span == 0f)
        {
            // A zero slide: flips its parity, clears its bit, never moves.
            _out.MoverStart(off, m.P.Family, m.P.Axis, 0f, 0f);
            ClearEnable(off);
            m.Dir = -m.Dir;
            return;
        }

        var target = m.Dir > 0f ? m.P.Span : 0f;
        if (!m.Running)
        {
            m.Running = true;
            _out.MoverStart(off, m.P.Family, m.P.Axis, (target - m.Progress) * m.P.Sign, m.P.Speed);
        }

        var step = m.P.Speed > 0f ? m.P.Speed * dt : float.MaxValue;
        m.Progress = MathF.Abs(target - m.Progress) <= step ? target : m.Progress + MathF.Sign(target - m.Progress) * step;
        Apply(m);
        if (m.Progress == target)
        {
            ClearEnable(off);                      // arrived: self-disable
            m.Dir = -m.Dir;                        // the next activation reverses
            m.Running = false;
            _out.MoverStop(off);
        }
    }

    private static void Apply(MoverState m) => m.Delta = DeltaAt(m, m.Progress);

    /// <summary>The mover's delta at <paramref name="progress"/>.</summary>
    private static Matrix4x4 DeltaAt(MoverState m, float progress)
    {
        var d = progress * m.P.Sign;
        var placed = BehaviourModel.Placement(m.Euler, m.Base);
        switch (m.P.Family)
        {
            case MoverFamily.Slide:
            case MoverFamily.Jump:
                return Matrix4x4.CreateTranslation(DosAxis(m.P.Axis) * d);
            case MoverFamily.Slide5F:
                // Along the record's own −Y (mover.gd: base.translated_local(0, −progress·sign, 0)).
                return Matrix4x4.CreateTranslation(Vector3.TransformNormal(new Vector3(0f, -d, 0f), placed));
        }

        var euler = m.Euler;
        switch (m.P.Axis)
        {
            case 0: euler.X += d; break;
            case 1: euler.Y += d; break;
            default: euler.Z += d; break;
        }

        var now = BehaviourModel.Placement(euler, m.Base);
        return Matrix4x4.Invert(placed, out var inv) ? inv * now : Matrix4x4.Identity;
    }

    /// <summary>DOS axis → level axes: X → +X, Y (down) → −Y, Z → −Z.</summary>
    private static Vector3 DosAxis(int axis) => axis switch
    {
        1 => -Vector3.UnitY,
        2 => -Vector3.UnitZ,
        _ => Vector3.UnitX,
    };

    // ---------------------------------------------------------------- lights

    private void LightTick(float dt)
    {
        if (_lights.Count == 0)
        {
            return;
        }

        _lightClock += dt;
        var fx = _lightClock >= ActRules.LightFxTick;
        if (fx)
        {
            _lightClock = 0f;
        }

        foreach (var (off, l) in _lights)
        {
            LightWatch(off, l, fx);
        }
    }

    private void LightWatch(int off, LightState l, bool fx)
    {
        if (!Fires(off))
        {
            l.Announced = false;
            if (l.FxShowing)
            {
                l.FxShowing = false;
                l.FxOn = true;
                _out.Light(off, "steady", LightOn(off), l.Intensity, false);
            }

            return;
        }

        var a = Act(off);
        var op = ActRules.LightOp(a);
        var announce = !l.Announced;
        l.Announced = true;

        if (Role == LevelRole.Client && a is not (ActRules.LightFlicker or ActRules.LightStrobe))
        {
            return;                                // a switch or a fade: the server's, shown as it arrives
        }

        if (a == ActRules.LightToggle)
        {
            l.Enable = l.Enable != 0 ? -l.Enable : 1;
            ClearEnable(off);
        }
        else if (a is ActRules.LightFlicker or ActRules.LightStrobe)
        {
            if (!fx)
            {
                return;
            }

            l.FxShowing = true;
            if (a == ActRules.LightStrobe || _random.NextDouble() < 0.5)
            {
                l.FxOn = !l.FxOn;
            }
        }
        else
        {
            var f = (a - 0x0C) / 4f;
            if (a >= 0x10)
            {
                f = -f;
            }

            l.Intensity = Math.Max((int)(l.Intensity * (1f + f)), 0);
            ClearEnable(off);
        }

        _out.Light(off, op, LightOn(off), l.Intensity, announce);
    }

    // ---------------------------------------------------------------- relays, spawns, water

    private void RelayTick()
    {
        foreach (var off in _relays)
        {
            var left = _mission.Left;
            if (!Enabled(off) || left <= 0 || left != ActRules.RelayAt)
            {
                continue;
            }

            Flip(off);
            ClearEnable(off);
        }
    }

    private void SpawnTick()
    {
        foreach (var off in _spawns)
        {
            if (!Fires(off))
            {
                continue;
            }

            ClearEnable(off);
            _out.Spawn(off, _map.TryGetEntityAt(off, out var e) ? e.Param16 : 0);
        }
    }

    private void WaterTick()
    {
        foreach (var off in _water)
        {
            if (!Fires(off) || !ActRules.WaterActs.TryGetValue(Act(off), out var cfg))
            {
                continue;
            }

            ClearEnable(off);
            if (cfg.Partner != 0)
            {
                _act[off] = cfg.Partner;            // next time it goes the other way
            }

            if (cfg.Delta == 0)
            {
                _out.Water(off, PositionOf(off).Y, true);
            }
            else
            {
                _out.Water(off, -cfg.Delta, false);
            }
        }
    }

    // ---------------------------------------------------------------- damage

    /// <summary>
    /// ObjHit (v1.01 0x139819, v1.00 0x139019): a weapon or a blast hits record
    /// <paramref name="off"/> for <paramref name="damage"/> points. A TRANSFRM.PRS wreck with state
    /// bits 1/2 advances one stage (handler 0x120833, stage counter += 16); otherwise bit 1 walks the
    /// chain on every hit and bit 2 once when the points run out. The points drain whatever the bits,
    /// a wreck at its last stage included; at zero the record leaves the world (0x139778 unlinks it
    /// from its cell, wrecks too: CARHIP and COPCAR cars vanish in their blast) and is destroyed
    /// (0x124793). True when it answered.
    /// </summary>
    public bool Hit(int off, float damage)
    {
        if (!_map.TryGetEntityAt(off, out var e))
        {
            return false;
        }

        if (Role == LevelRole.Client)
        {
            // The shooter detects the hit, the server owns what it comes to (net_game.gd): an
            // intent, and nothing changes here until the server's delta says so.
            if (!_spent.Contains(off) && float.IsFinite(damage) && damage > 0f)
            {
                Intent?.Invoke(new LevelIntent(LevelIntentKind.Hit, off, default, damage));
            }

            return false;
        }

        NoteHitter(off);
        var wasSpent = _spent.Contains(off);
        var hasHp = _hp.ContainsKey(off) && e.Variant == MapEntityVariant.Mesh && !wasSpent;
        var depleted = hasHp && _hp[off] - damage <= 0f;
        var acted = false;
        var st = State(off);
        if ((st & 6) != 0)
        {
            if (_wrecks.ContainsKey(off))
            {
                acted = Advance(off);
            }
            else if (!wasSpent && ((st & 2) != 0 || ((st & 4) != 0 && depleted)))
            {
                Flip(off);
                acted = true;
            }
        }

        if (hasHp)
        {
            _hp[off] -= damage;
            acted = true;
            if (depleted)
            {
                _spent.Add(off);
                Destroy(off);
            }
        }

        return acted;
    }

    /// <summary>
    /// A destroyed robot's own act (the start marker's sub+4, handed to its actor by
    /// EnemiesStartMarked 0x12a439 and run by ObjHit when the last hit point goes): hints print,
    /// objectives count down (MAP.060's camp defences), 0x2B fails the mission; any other act is not
    /// run (the Godot port's behaviour.gd enemy_act). <paramref name="marker"/> is the marker's offset.
    /// </summary>
    public void RobotAct(int act, int marker)
    {
        if (act is >= ActRules.ObjectiveFirst and <= ActRules.ObjectiveLast)
        {
            var index = act - ActRules.ObjectiveFirst;
            var wasComplete = _mission.Complete;
            var text = _mission.Take(index);
            _out.Objective(marker, index, _mission.Left, text);
            if (_mission.Complete && !wasComplete)
            {
                _out.MissionComplete();
            }
        }
        else if (act is >= ActRules.HintFirst and <= ActRules.HintLast)
        {
            var index = act - ActRules.HintFirst;
            _out.Hint(marker, index, _mission.HintText(index));
        }
        else if (act == ActRules.Fail)
        {
            _mission.Fail();
            _out.MissionFailed(marker);
        }
    }

    /// <summary>Act 0x1B: hp = max(hp, 1), then ObjHit with hp + 1 (handler 0x1380bf).</summary>
    public void Demolish(int off)
    {
        if (!_map.TryGetEntityAt(off, out var e) || _spent.Contains(off) || e.Variant != MapEntityVariant.Mesh)
        {
            return;
        }

        if (!_hp.TryGetValue(off, out var hp) || hp <= 0f)
        {
            _hp[off] = hp = 1f;
        }

        _out.Demolish(off, hp);
        Hit(off, hp + 1f);
    }

    private void DestructTick()
    {
        foreach (var off in _rams)
        {
            if (!Fires(off))
            {
                continue;
            }

            ClearEnable(off);
            if (_wrecks.ContainsKey(off))
            {
                BreakDown(off);
            }
        }
    }

    private void DemolishTick()
    {
        foreach (var off in _demolishers)
        {
            if (!Fires(off))
            {
                continue;
            }

            ClearEnable(off);
            Demolish(off);
        }
    }

    /// <summary>One stage on (handler 0x120833): the counter takes 16, the stage is counter / 16,
    /// up to the last TRANSFRM.PRS frame, where it stays (the record is not spent by it: its hit
    /// points still drain). False when the wreck is already at its last stage.</summary>
    private bool Advance(int off)
    {
        var w = _wrecks[off];
        var last = w.Stages - 1;
        if (w.Stage >= last)
        {
            return false;
        }

        w.Accum += ActRules.DestructPerStage;
        var stage = Math.Min((int)(w.Accum / ActRules.DestructPerStage), last);
        if (stage != w.Stage)
        {
            w.Stage = stage;
            _out.Stage(off, stage, w.Stages);
        }

        return true;
    }

    /// <summary>A chain strikes a wreck (destructible.gd break_down): one stage; reaching the last
    /// one spends it, its final mesh left standing.</summary>
    private void BreakDown(int off)
    {
        if (_spent.Contains(off) || !Advance(off) || _wrecks[off].Stage < _wrecks[off].Stages - 1)
        {
            return;
        }

        // The last stage stands (MAP.248's wall keeps its hole): spent, but not removed.
        _spent.Add(off);
        _hp[off] = 0f;
        Destroy(off, removed: false);
        _noCollide.Add(off);
    }

    /// <summary>The hit points ran out (or a chain broke a wreck down): the destruction event, and
    /// with <paramref name="removed"/> the record leaves the world.</summary>
    private void Destroy(int off, bool removed = true)
    {
        if (!_map.TryGetEntityAt(off, out var e))
        {
            return;
        }

        if (removed)
        {
            _hidden.Add(off);
            _noCollide.Add(off);
        }

        _out.Destroyed(new DestroyEvent(off, BehaviourModel.Position(e), e.Link?.DestroyType ?? 0, e.Link?.DestroyParam ?? 0, removed));
    }

    // ---------------------------------------------------------------- colliders

    /// <summary>The records the static collision grid must leave out
    /// (<c>LevelCollision.Build(data, map, wld, behaviour.IsDynamic)</c>).</summary>
    public bool IsDynamic(MapEntity e) => BehaviourModel.IsDynamic(_map, e, _transfrm);

    /// <summary>
    /// The colliders of the dynamic meshes (<see cref="BehaviourModel.IsDynamic"/>), one body per
    /// record with a triangle list per stage, gathered through this behaviour's poses. Add it to
    /// the level's <see cref="CollisionWorld"/> built with those records skipped.
    /// </summary>
    public DynamicColliders BuildColliders(GameData data)
    {
        var colliders = new DynamicColliders(this);
        var cache = new Dictionary<string, Vector3[][]?>(StringComparer.OrdinalIgnoreCase);
        var objects = data.Exists("MDMDOBJS.BSA") ? data.OpenArchive("MDMDOBJS.BSA") : null;
        var enemies = data.Exists("MDMDENMS.BSA") ? data.OpenArchive("MDMDENMS.BSA") : null;
        try
        {
            Vector3[][]? Faces(string name)
            {
                if (cache.TryGetValue(name, out var f))
                {
                    return f;
                }

                var file = name + ".3D";
                var bytes = objects?.Read(file) is { Length: > 0 } b ? b : enemies?.Read(file);
                var model = bytes is { Length: > 0 } ? Model3D.Parse(bytes) : null;
                return cache[name] = model != null ? LevelCollision.Faces(model) : null;
            }

            foreach (var e in _map.Entities)
            {
                if (!BehaviourModel.IsDynamic(_map, e, _transfrm) || string.IsNullOrEmpty(_map.EntityName(e)))
                {
                    continue;
                }

                var placed = BehaviourModel.Placement(BehaviourModel.Euler(e), BehaviourModel.Position(e));
                var stages = BehaviourModel.StageMeshes(_map, e, _transfrm)
                    .Select(n => Faces(n) is { } faces
                        ? faces.Select(t => new Triangle(Vector3.Transform(t[0], placed), Vector3.Transform(t[1], placed),
                            Vector3.Transform(t[2], placed), e.FileOffset)).ToArray()
                        : [])
                    .ToArray();
                if (stages.Any(s => s.Length > 0))
                {
                    colliders.Add(e.FileOffset, stages);
                }
            }
        }
        finally
        {
            objects?.Dispose();
            enemies?.Dispose();
        }

        return colliders;
    }
}
