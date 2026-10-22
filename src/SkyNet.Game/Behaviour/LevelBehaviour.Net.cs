using System.Numerics;
using SkyNet.Formats;

namespace SkyNet.Game.Behaviour;

/// <summary>Who plays a level (the Godot port's Behaviour.net_role). The campaign is always
/// <see cref="Local"/>.</summary>
public enum LevelRole
{
    /// <summary>Nobody else watches: the level runs and writes as it always has.</summary>
    Local,
    /// <summary>A network game's server: the same, plus the cues it fires are kept for the wire
    /// and the other players' bodies are swept by <see cref="LevelBehaviour.BodyProx"/>.</summary>
    Server,
    /// <summary>A network game's client: the level shows the server's state (movers animate, lights
    /// flicker) and writes nothing of its own; the use key and the shots on records become
    /// <see cref="LevelIntent"/>s for the server.</summary>
    Client,
}

public enum LevelIntentKind : byte
{
    /// <summary>The use key went down at <see cref="LevelIntent.Eye"/> with the crosshair on
    /// <see cref="LevelIntent.Record"/> (−1 none).</summary>
    Use = 0,
    /// <summary>A weapon struck <see cref="LevelIntent.Record"/> for <see cref="LevelIntent.Damage"/>.</summary>
    Hit = 1,
}

/// <summary>A client's hand on the level (net_game.gd _c_trigger): an intent, never a result.</summary>
public readonly record struct LevelIntent(LevelIntentKind Kind, int Record, Vector3 Eye, float Damage);

/// <summary>What <see cref="LevelNetRecord.Flags"/> holds.</summary>
[Flags]
public enum NetRecordFlags : ushort
{
    None = 0,
    Spent = 1,
    CueSpent = 2,
    Hidden = 4,
    NoCollide = 8,
    Turned = 16,
    HasHp = 32,
    Wreck = 64,
    Mover = 128,
    Light = 256,
}

/// <summary>
/// One record's whole live state as the wire carries it (the Godot port's trigger delta, one row
/// per record): the state, act and link bytes, ObjHit's pool, what is spent, hidden, out of the
/// collision or turned, a wreck's stage, a mover's travel and next direction, a light's enable word
/// and intensity. A mover's travel rides along but is not news by itself (<see cref="SameAs"/>):
/// every peer animates it from the bit, as the Godot port's movers do.
/// </summary>
public readonly record struct LevelNetRecord(
    int Offset,
    byte State,
    byte Act,
    int Link,
    NetRecordFlags Flags,
    float Hp = 0f,
    byte Stage = 0,
    float Accum = 0f,
    float Progress = 0f,
    sbyte Dir = 0,
    bool Running = false,
    int LightEnable = 0,
    int LightIntensity = 0)
{
    public bool Has(NetRecordFlags f) => (Flags & f) != 0;

    /// <summary>The same state for the wire: everything but a mover's travel and running flag,
    /// which every peer steps itself between the bit's changes.</summary>
    public bool SameAs(in LevelNetRecord o) =>
        Offset == o.Offset && State == o.State && Act == o.Act && Link == o.Link && Flags == o.Flags
        && Hp.Equals(o.Hp) && Stage == o.Stage && Accum.Equals(o.Accum) && Dir == o.Dir
        && LightEnable == o.LightEnable && LightIntensity == o.LightIntensity;
}

/// <summary>The network side of the level (docs/multiplayer.md step 8; the Godot port's M5 trigger
/// state): the record rows the wire carries, a client's application of them through the same
/// listener a local change speaks to, and the server's entry points for the other players.</summary>
public sealed partial class LevelBehaviour
{
    private readonly List<int> _fired = [];
    private readonly Dictionary<int, HashSet<int>> _bodyLatched = [];
    private readonly Dictionary<int, int> _hitBy = [];
    private int? _actor;

    /// <summary>A server's own player (the host's id): whoever strikes a record when no other player's
    /// intent or blast is being run (<see cref="CurrentActor"/>). 0 = nobody (the world).</summary>
    public int LocalActor { get; set; }

    /// <summary>Who is striking records now: the player whose intent or whose broken record's blast is
    /// running (<see cref="RunAs"/>), else <see cref="LocalActor"/>.</summary>
    public int CurrentActor => _actor ?? LocalActor;

    /// <summary>Runs <paramref name="action"/> with <paramref name="actor"/> as the one striking records:
    /// a client's hit intent, or a broken record's blast (credited to whoever broke it).</summary>
    public void RunAs(int actor, Action action)
    {
        var was = _actor;
        _actor = actor;
        try
        {
            action();
        }
        finally
        {
            _actor = was;
        }
    }

    /// <summary>The player who last struck record <paramref name="off"/> on a server (0 = none, the
    /// world): a level blast is theirs, so a death in it is their kill (docs/multiplayer.md).</summary>
    public int BreakerOf(int off) => _hitBy.GetValueOrDefault(off);

    private void NoteHitter(int off)
    {
        if (Role == LevelRole.Server)
        {
            _hitBy[off] = CurrentActor;
        }
    }

    /// <summary>A network game's NEW ROUND: the level back to <paramref name="start"/> (the
    /// <see cref="Snapshot"/> taken when the arena started), the hitters forgotten.</summary>
    public void ResetTo(BehaviourSnapshot start)
    {
        Restore(start);
        _hitBy.Clear();
        _fired.Clear();
        foreach (var latched in _bodyLatched.Values)
        {
            latched.Clear();
        }
    }

    /// <summary>Who plays this level; <see cref="LevelRole.Local"/> unless a network game says so.</summary>
    public LevelRole Role { get; set; }

    /// <summary>A client's way to the server (<see cref="LevelRole.Client"/> only).</summary>
    public Action<LevelIntent>? Intent { get; set; }

    /// <summary>Every record the behaviour keeps state for, in file order.</summary>
    public IEnumerable<int> Records => _state.Keys.Order();

    /// <summary>Where record <paramref name="off"/> stands; false when the map has no such record.</summary>
    public bool TryPositionOf(int off, out Vector3 at)
    {
        at = default;
        if (!_map.TryGetEntityAt(off, out var e))
        {
            return false;
        }

        at = BehaviourModel.Position(e);
        return true;
    }

    /// <summary>A fingerprint of the map file the records come from (offsets and their authored
    /// bytes): a server's rows are refused whole by a client that loaded other data (graph_sha).</summary>
    public int MapSignature()
    {
        var h = 2166136261u;
        void Mix(int v)
        {
            for (var i = 0; i < 4; i++)
            {
                h = (h ^ (byte)(v >> (8 * i))) * 16777619u;
            }
        }

        Mix(_map.Entities.Count());
        foreach (var e in _map.Entities)
        {
            Mix(e.FileOffset);
            Mix(e.StateByte);
            Mix(e.ActType);
            Mix(e.LinkNext);
            Mix((int)e.Variant);
        }

        return unchecked((int)h);
    }

    /// <summary>Record <paramref name="off"/>'s live state as a wire row.</summary>
    public LevelNetRecord Capture(int off)
    {
        var f = NetRecordFlags.None;
        if (_spent.Contains(off)) f |= NetRecordFlags.Spent;
        if (_cueSpent.Contains(off)) f |= NetRecordFlags.CueSpent;
        if (_hidden.Contains(off)) f |= NetRecordFlags.Hidden;
        if (_noCollide.Contains(off)) f |= NetRecordFlags.NoCollide;
        if (_turned.Contains(off)) f |= NetRecordFlags.Turned;
        var hp = 0f;
        if (_hp.TryGetValue(off, out var h))
        {
            f |= NetRecordFlags.HasHp;
            hp = h;
        }

        byte stage = 0;
        var accum = 0f;
        if (_wrecks.TryGetValue(off, out var w))
        {
            f |= NetRecordFlags.Wreck;
            stage = (byte)Math.Clamp(w.Stage, 0, 255);
            accum = w.Accum;
        }

        var progress = 0f;
        sbyte dir = 0;
        var running = false;
        if (_movers.TryGetValue(off, out var m))
        {
            f |= NetRecordFlags.Mover;
            (progress, dir, running) = (m.Progress, m.Dir < 0f ? (sbyte)-1 : (sbyte)1, m.Running);
        }

        int enable = 0, intensity = 0;
        if (_lights.TryGetValue(off, out var l))
        {
            f |= NetRecordFlags.Light;
            (enable, intensity) = (l.Enable, l.Intensity);
        }

        return new LevelNetRecord(off, (byte)State(off), (byte)Act(off), Link(off), f, hp, stage, accum, progress, dir, running, enable, intensity);
    }

    /// <summary>The cues the server's chains ran since the last call (sounds, voices, loops): a
    /// one-shot's bit goes up and down inside one tick, so no row could tell a client about it.</summary>
    public int[] TakeFired()
    {
        var fired = _fired.ToArray();
        _fired.Clear();
        return fired;
    }

    // ---------------------------------------------------------------- client

    /// <summary>A client's tick: what only shows the server's state (the flickering lights, the
    /// movers stepping from their bit) and the use key as an intent; no sweep, no chain, no exit.</summary>
    private Vector3 ClientTick(float dt, in PlayerProbe player)
    {
        if (player.UsePressed)
        {
            Intent?.Invoke(new LevelIntent(LevelIntentKind.Use, player.AimedOwner, player.Eye, 0f));
        }

        LightTick(dt);
        var carry = MoverTick(dt, player.Feet, player.FloorOwner);
        _armed.Clear();
        return carry;
    }

    /// <summary>
    /// A server's row laid over this level, shown through the listener by the paths a local change
    /// takes: a wreck's stage, a destruction (its blast, sound and removal), a light switched or
    /// faded, an ambient loop whose bit went down. A mover takes the travel it had on the server and
    /// steps on from its bit itself. Records the map does not have are ignored.
    /// </summary>
    public void ApplyNet(in LevelNetRecord r, bool present = true)
    {
        var off = r.Offset;
        if (!_state.ContainsKey(off))
        {
            return;
        }

        var wasOn = Enabled(off);
        var wasSpent = _spent.Contains(off);
        _state[off] = r.State;
        _act[off] = r.Act;
        _link[off] = r.Link;
        if (r.Has(NetRecordFlags.HasHp))
        {
            _hp[off] = r.Hp;
        }
        else
        {
            _hp.Remove(off);
        }

        Put(_spent, off, r.Has(NetRecordFlags.Spent));
        Put(_cueSpent, off, r.Has(NetRecordFlags.CueSpent));
        Put(_hidden, off, r.Has(NetRecordFlags.Hidden));
        Put(_noCollide, off, r.Has(NetRecordFlags.NoCollide));
        Put(_turned, off, r.Has(NetRecordFlags.Turned));
        if (r.Has(NetRecordFlags.Mover) && _movers.TryGetValue(off, out var m))
        {
            m.Progress = r.Progress;
            m.Dir = r.Dir < 0 ? -1f : 1f;
            Apply(m);
        }

        if (r.Has(NetRecordFlags.Wreck) && _wrecks.TryGetValue(off, out var w))
        {
            var stage = Math.Min((int)r.Stage, w.Stages - 1);
            w.Accum = r.Accum;
            if (stage != w.Stage)
            {
                w.Stage = stage;
                if (present)
                {
                    _out.Stage(off, stage, w.Stages);
                }
            }
        }

        if (r.Has(NetRecordFlags.Light) && _lights.TryGetValue(off, out var l) && (l.Enable != r.LightEnable || l.Intensity != r.LightIntensity))
        {
            (l.Enable, l.Intensity) = (r.LightEnable, r.LightIntensity);
            _out.Light(off, ActRules.LightOp(Act(off)), LightOn(off), l.Intensity, true);
        }

        // A loop the server has switched off: nothing else would stop it (present_silence).
        if (wasOn && !Enabled(off) && _kind.GetValueOrDefault(off) == NodeKind.SoundLoop)
        {
            _out.LoopStop(off);
        }

        if (present && !wasSpent && _spent.Contains(off) && _map.TryGetEntityAt(off, out var e))
        {
            _out.Destroyed(new DestroyEvent(off, BehaviourModel.Position(e), e.Link?.DestroyType ?? 0, e.Link?.DestroyParam ?? 0,
                _hidden.Contains(off)));
        }
    }

    private static void Put(HashSet<int> set, int off, bool on)
    {
        if (on)
        {
            set.Add(off);
        }
        else
        {
            set.Remove(off);
        }
    }

    /// <summary>A cue the server's chain ran (<see cref="TakeFired"/>), played here as the record's
    /// own fire plays it, with no state of its own.</summary>
    public void PresentFire(int off)
    {
        if (!_kind.TryGetValue(off, out var kind) || !_map.TryGetEntityAt(off, out var e))
        {
            return;
        }

        var at = BehaviourModel.Position(e);
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
        }
    }

    // ---------------------------------------------------------------- server

    /// <summary>
    /// Another player's use key, on the server: what <see cref="Use"/> and the key's edge in the
    /// proximity sweep do for the own player, measured from <paramref name="eye"/> — the record under
    /// that player's crosshair (a wall button or a prop whose state bit 1 answers), else the nearest
    /// wall button in its measure, then every 0xEF gate whose measure holds the eye. A gate that
    /// leads to a doorway is left alone: an arena is one map and a client asks for no map change.
    /// </summary>
    public bool RemoteUse(Vector3 eye, int aimed)
    {
        if (Role != LevelRole.Server)
        {
            return false;
        }

        var done = false;
        if (aimed >= 0 && !_spent.Contains(aimed) && _map.TryGetEntityAt(aimed, out _))
        {
            done = _triggers.ContainsKey(aimed)
                ? ChainExit(aimed) < 0 && ProxUse(aimed, eye, aimed: true)
                : ActivateProp(aimed);
        }

        if (!done)
        {
            var best = -1;
            var bestD = float.MaxValue;
            foreach (var off in _triggers.Keys)
            {
                if (_spent.Contains(off) || !IsWallButton(off) || ChainExit(off) >= 0)
                {
                    continue;
                }

                var d = Vector3.Distance(PositionOf(off), eye);
                if (d <= Measure(off) && d < bestD)
                {
                    (best, bestD) = (off, d);
                }
            }

            if (best >= 0)
            {
                done = ProxUse(best, eye, aimed: false);
            }
        }

        foreach (var off in _prox)
        {
            if (IsWallButton(off) || _spent.Contains(off) || Act(off) != ActRules.ProxGate || ChainExit(off) >= 0)
            {
                continue;
            }

            var t = _triggers[off];
            if (!t.EdgeDone && Inside(off, eye))
            {
                Flip(off);
                TurnSwitch(off);
                t.EdgeDone = true;
                done = true;
            }
        }

        return done;
    }

    /// <summary>
    /// The walk-in sweep for a body that is not this machine's player (a client's, from its pose; a
    /// bot's), on the server: the 0xF1/0xF2 chain triggers measure one eye in DOS (0x138223); here
    /// each body has its own latch, so one standing inside a radius does not hold it shut for
    /// another (the Godot port's Behaviour.prox_body).
    /// </summary>
    public void BodyProx(int body, Vector3 eye)
    {
        if (Role != LevelRole.Server)
        {
            return;
        }

        if (!_bodyLatched.TryGetValue(body, out var latched))
        {
            _bodyLatched[body] = latched = [];
        }

        foreach (var off in _prox)
        {
            var a = Act(off);
            if (IsWallButton(off) || _spent.Contains(off) || a == ActRules.ProxGate)
            {
                continue;
            }

            var chain = ActRules.IsProxChain(a);
            if (chain && !Enabled(off))
            {
                continue;
            }

            var here = Inside(off, eye);
            if (here && latched.Add(off))
            {
                Flip(off);
                if (chain)
                {
                    ClearEnable(off);
                }
            }
            else if (!here)
            {
                latched.Remove(off);
            }
        }
    }

    /// <summary>A body put down (a spawn): its latches start from where it stands (prox_body_arm).</summary>
    public void ArmBody(int body, Vector3 eye)
    {
        var latched = new HashSet<int>();
        foreach (var off in _prox)
        {
            if (Inside(off, eye))
            {
                latched.Add(off);
            }
        }

        _bodyLatched[body] = latched;
    }

    /// <summary>A body gone from the game.</summary>
    public void ForgetBody(int body) => _bodyLatched.Remove(body);
}
