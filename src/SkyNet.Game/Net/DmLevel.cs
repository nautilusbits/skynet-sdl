using System.Numerics;
using RetroEng.Net;
using SkyNet.Game.Behaviour;
using SkyNet.Game.Player;

namespace SkyNet.Game.Net;

/// <summary>
/// The level's state on the wire (<see cref="DmMsg.LevelState"/>): the server's rows of the records
/// that changed (<see cref="Whole"/> false, a delta) or of every record that differs from the map
/// as it starts (true, a late joiner's overlay), and the cues its chains ran. Rows are sorted by
/// offset, so one state always gives the same bytes. <see cref="Reset"/> (a NEW ROUND, always
/// whole): the client puts its level back to how the arena started before it lays the rows down.
/// </summary>
public sealed record LevelUpdate(bool Whole, int Signature, LevelNetRecord[] Records, int[] Fired, bool Reset = false)
{
    /// <summary>A message holds at most this many rows (the overlay of the biggest map is far less).</summary>
    public const int MaxRecords = 16384;

    public void Write(NetWriter w)
    {
        w.Begin(DmMsg.LevelState).U8((byte)(Reset ? 2 : Whole ? 1 : 0)).I32(Signature).U16((ushort)Records.Length);
        foreach (var r in Records)
        {
            w.I32(r.Offset).U8(r.State).U8(r.Act).I32(r.Link).U16((ushort)r.Flags);
            if (r.Has(NetRecordFlags.HasHp))
            {
                w.F32(r.Hp);
            }

            if (r.Has(NetRecordFlags.Wreck))
            {
                w.U8(r.Stage).F32(r.Accum);
            }

            if (r.Has(NetRecordFlags.Mover))
            {
                w.F32(r.Progress).U8(unchecked((byte)r.Dir)).Bool(r.Running);
            }

            if (r.Has(NetRecordFlags.Light))
            {
                w.I32(r.LightEnable).I32(r.LightIntensity);
            }
        }

        w.U16((ushort)Fired.Length);
        foreach (var off in Fired)
        {
            w.I32(off);
        }
    }

    /// <summary>The message's body read back (after its id); throws <see cref="NetReadException"/>
    /// on a short or bad one, as every handler's read does.</summary>
    public static LevelUpdate Read(ref NetReader r)
    {
        var kind = r.U8();
        if (kind > 2)
        {
            throw new NetReadException("bad level update kind");
        }

        var whole = kind != 0;
        var signature = r.I32();
        int n = r.U16();
        var rows = new LevelNetRecord[n];
        for (var i = 0; i < n; i++)
        {
            var off = r.I32();
            var state = r.U8();
            var act = r.U8();
            var link = r.I32();
            var flags = (NetRecordFlags)r.U16();
            var row = new LevelNetRecord(off, state, act, link, flags);
            if (row.Has(NetRecordFlags.HasHp))
            {
                row = row with { Hp = Finite(r.F32()) };
            }

            if (row.Has(NetRecordFlags.Wreck))
            {
                row = row with { Stage = r.U8(), Accum = Finite(r.F32()) };
            }

            if (row.Has(NetRecordFlags.Mover))
            {
                row = row with { Progress = Finite(r.F32()), Dir = unchecked((sbyte)r.U8()) < 0 ? (sbyte)-1 : (sbyte)1, Running = r.Bool() };
            }

            if (row.Has(NetRecordFlags.Light))
            {
                row = row with { LightEnable = r.I32(), LightIntensity = r.I32() };
            }

            if (off < 0)
            {
                throw new NetReadException("negative record offset");
            }

            rows[i] = row;
        }

        int f = r.U16();
        var fired = new int[f];
        for (var i = 0; i < f; i++)
        {
            fired[i] = r.I32();
        }

        return new LevelUpdate(whole, signature, rows, fired, kind == 2);
    }

    private static float Finite(float v) => float.IsFinite(v) ? v : throw new NetReadException("not a number");
}

/// <summary>
/// The server's level in a network game (the Godot port's M5: dm_game.gd's trigger half and
/// net_game.gd's _srv_client_trigger): the host's own <see cref="LevelBehaviour"/> plays as in the
/// campaign, in <see cref="LevelRole.Server"/>. Each frame what changed goes out as a delta; a
/// joiner gets the whole overlay after its welcome; the other players' use keys and shots on records
/// come in as intents, are checked like every client input, and run through the level's own entry
/// points; their bodies (and the bots') are swept for the walk-in triggers from their poses.
/// </summary>
public sealed class DmLevelServer
{
    /// <summary>How far an intent's eye, or the record it names, may stand from the server's last
    /// pose of the sender (net_game.gd TRIGGER_REACH: the pickup reasoning at vehicle range).</summary>
    public const float TriggerReach = 1400f;

    private readonly int[] _offsets;
    private readonly Dictionary<int, LevelNetRecord> _start = [];
    private readonly Dictionary<int, LevelNetRecord> _sent = [];
    private readonly HashSet<int> _awake = [];
    private readonly BehaviourSnapshot _startState;
    private readonly Action<BehaviourSnapshot> _reset;

    /// <param name="reset">Puts the level back to the arena's start on a NEW ROUND (the host's
    /// session also re-arms its breakables and loops, <see cref="Campaign.GameSession.ResetLevel"/>);
    /// null: the behaviour alone.</param>
    public DmLevelServer(LevelBehaviour behaviour, Action<BehaviourSnapshot>? reset = null)
    {
        Behaviour = behaviour;
        behaviour.Role = LevelRole.Server;
        behaviour.LocalActor = DmServer.HostId;
        _startState = behaviour.Snapshot();
        _reset = reset ?? behaviour.ResetTo;
        Signature = behaviour.MapSignature();
        _offsets = [.. behaviour.Records];
        foreach (var off in _offsets)
        {
            var row = behaviour.Capture(off);
            _start[off] = row;
            _sent[off] = row;
        }

        behaviour.TakeFired();
    }

    public LevelBehaviour Behaviour { get; }

    public int Signature { get; }

    /// <summary>Intents run (for tests and the log).</summary>
    public int IntentsRun { get; private set; }

    /// <summary>Every record that differs from the map as it started: a joiner's overlay.</summary>
    public LevelUpdate Overlay()
    {
        var rows = new List<LevelNetRecord>();
        foreach (var off in _offsets)
        {
            var now = Behaviour.Capture(off);
            if (now != _start[off])
            {
                rows.Add(now);
            }
        }

        return new LevelUpdate(true, Signature, [.. rows], []);
    }

    /// <summary>What changed since the last call, and the cues run; null when nothing did (a quiet
    /// arena sends nothing at all).</summary>
    public LevelUpdate? Delta()
    {
        List<LevelNetRecord>? rows = null;
        foreach (var off in _offsets)
        {
            var now = Behaviour.Capture(off);
            if (!now.SameAs(_sent[off]))
            {
                _sent[off] = now;
                (rows ??= []).Add(now);
            }
        }

        var fired = Behaviour.TakeFired();
        return rows == null && fired.Length == 0 ? null : new LevelUpdate(false, Signature, rows?.ToArray() ?? [], fired);
    }

    /// <summary>
    /// A client's use key: an admitted sender within its budget, alive, its eye a real place near
    /// where the server last saw it, the record it aims at (if any) one the map has, within reach.
    /// What the record itself allows (its measure, its state bits) is the level's.
    /// </summary>
    public bool ClientUse(DmServer server, int id, Vector3 eye, int aimed)
    {
        if (!server.IsAlive(id) || !DmProtocol.SanePos(eye) || !server.Gate(id, DmProtocol.Limit.Trigger))
        {
            return false;
        }

        var known = server.Players[id].Position;
        if (Vector3.Distance(eye, known) > TriggerReach)
        {
            return false;
        }

        if (aimed >= 0 && (!Behaviour.TryPositionOf(aimed, out var at) || Vector3.Distance(at, known) > TriggerReach))
        {
            aimed = -1;
        }

        IntentsRun++;
        return Behaviour.RemoteUse(eye, aimed);
    }

    /// <summary>A client's shot on record <paramref name="off"/>: alive (or just killed with a shot
    /// in the air), within budget, a record the map has within ray range of the sender, a real
    /// positive amount clamped like a hit on a player (<see cref="DmProtocol.MaxHitDamage"/>).</summary>
    public bool ClientHit(DmServer server, int id, int off, float damage)
    {
        if (!server.Players.TryGetValue(id, out var p) || !float.IsFinite(damage) || damage <= 0f
            || !server.Gate(id, DmProtocol.Limit.Trigger))
        {
            return false;
        }

        if (!p.Alive && !server.InShotGrace(id))
        {
            return false;
        }

        if (!Behaviour.TryPositionOf(off, out var at) || Vector3.Distance(at, p.Position) > DmProtocol.MaxHitRange
            || !Behaviour.IsDamageable(off))
        {
            return false;
        }

        IntentsRun++;
        Behaviour.RunAs(id, () => Behaviour.Hit(off, MathF.Min(damage, DmProtocol.MaxHitDamage)));
        return true;
    }

    /// <summary>
    /// NEW ROUND (docs/multiplayer.md, the owner's rule): the level back to how the arena started —
    /// doors shut, switches back, breakables whole — and the update every client gets for it: a
    /// <see cref="LevelUpdate.Reset"/> (each client puts its own copy back to its start) with the
    /// overlay over that start, which is empty unless the start itself differs.
    /// </summary>
    public LevelUpdate Reset()
    {
        _reset(_startState);
        _awake.Clear();
        Behaviour.TakeFired();
        var rows = new List<LevelNetRecord>();
        foreach (var off in _offsets)
        {
            var now = Behaviour.Capture(off);
            _sent[off] = now;
            if (now != _start[off])
            {
                rows.Add(now);
            }
        }

        return new LevelUpdate(true, Signature, [.. rows], [], Reset: true);
    }

    /// <summary>
    /// A broken record's radial blast on the bodies (the owner's rule: the server owns level blasts):
    /// every living player whose middle (a driver's vehicle's) lies within the blast's reach and in a
    /// clear line takes DOS's falloff (<see cref="Weapons.Blast.Damage"/>), credited to whoever broke
    /// <paramref name="source"/> (<see cref="LevelBehaviour.BreakerOf"/>; 0 = the world), with the
    /// weapon <see cref="DmProtocol.LevelBlastWeapon"/>. Returns who was struck and for how much.
    /// </summary>
    public List<(int Victim, float Points)> Blast(DmServer server, int source, Vector3 at, float strength, Func<Vector3, Vector3, bool>? clear = null)
    {
        var culprit = Behaviour.BreakerOf(source);
        var reach = Weapons.Blast.Reach(strength);
        var struck = new List<(int, float)>();
        foreach (var p in server.Players.Values.ToList())
        {
            if (!p.Alive)
            {
                continue;
            }

            var centre = server.VehicleOf(p.Id) is { } v
                ? p.Position + new Vector3(0f, DmAvatar.VehicleRadius(v.Kind), 0f)
                : p.Position + new Vector3(0f, DmAvatar.Height / 2f, 0f);
            var d = Vector3.Distance(centre, at);
            var points = d <= reach ? Weapons.Blast.Damage(strength, d) : 0f;
            if (points <= 0f || clear?.Invoke(at, centre) == false)
            {
                continue;
            }

            if (server.Hit(p.Id, points, culprit, DmProtocol.LevelBlastWeapon))
            {
                struck.Add((p.Id, points));
            }
        }

        return struck;
    }

    /// <summary>The walk-in sweep for every living body but the host's own (which its tick sweeps):
    /// the clients from their last pose, the bots from theirs. A body that has just come to life is
    /// pre-latched where it stands, as a spawn is.</summary>
    public void Sweep(DmServer server)
    {
        foreach (var p in server.Players.Values)
        {
            if (p.Id == DmServer.HostId)
            {
                continue;
            }

            var eye = p.Position + new Vector3(0f, PlayerController.EyeHeight, 0f);
            if (!p.Alive)
            {
                _awake.Remove(p.Id);
                continue;
            }

            if (_awake.Add(p.Id))
            {
                Behaviour.ArmBody(p.Id, eye);
            }
            else
            {
                Behaviour.BodyProx(p.Id, eye);
            }
        }

        foreach (var id in _awake.Where(id => !server.Players.ContainsKey(id)).ToList())
        {
            _awake.Remove(id);
            Behaviour.ForgetBody(id);
        }
    }
}

/// <summary>A client's level in a network game: <see cref="LevelRole.Client"/>, the server's overlay
/// laid down once, its deltas shown as they come. An overlay of another map file is refused, and so
/// is everything after it: this peer's map then stays as its own files have it.</summary>
public sealed class DmLevelClient
{
    private readonly int _signature;
    private readonly BehaviourSnapshot _startState;
    private readonly Action<BehaviourSnapshot> _reset;
    private readonly Dictionary<int, LevelNetRecord> _start = [];

    /// <param name="reset">Puts this copy back to the arena's start on the server's NEW ROUND (the
    /// session re-arms its breakables and loops too); null: the behaviour alone.</param>
    public DmLevelClient(LevelBehaviour behaviour, Action<LevelIntent> send, Action<BehaviourSnapshot>? reset = null)
    {
        Behaviour = behaviour;
        _signature = behaviour.MapSignature();
        _startState = behaviour.Snapshot();
        _reset = reset ?? behaviour.ResetTo;
        foreach (var off in behaviour.Records)
        {
            _start[off] = behaviour.Capture(off);
        }

        behaviour.Role = LevelRole.Client;
        behaviour.Intent = send;
    }

    /// <summary>Every record that differs from the map as it started here, as the server's overlay
    /// would carry it (a network demo's keyframe of a client's recording).</summary>
    public LevelUpdate Overlay()
    {
        var rows = new List<LevelNetRecord>();
        foreach (var (off, start) in _start.OrderBy(kv => kv.Key))
        {
            var now = Behaviour.Capture(off);
            if (now != start)
            {
                rows.Add(now);
            }
        }

        return new LevelUpdate(true, _signature, [.. rows], []);
    }

    public LevelBehaviour Behaviour { get; }

    /// <summary>False once the server's rows turned out to be of other map data.</summary>
    public bool InStep { get; private set; } = true;

    /// <summary>NEW ROUND resets laid down so far.</summary>
    public int Resets { get; private set; }

    /// <summary>Rows applied so far.</summary>
    public int Applied { get; private set; }

    /// <summary>Lays one update over the level; false when it is refused (other map data).</summary>
    public bool Apply(LevelUpdate u)
    {
        if (!InStep)
        {
            return false;
        }

        if (u.Signature != _signature)
        {
            InStep = false;
            return false;
        }

        if (u.Reset)
        {
            _reset(_startState);
            Resets++;
        }

        foreach (var row in u.Records)
        {
            // A joiner's overlay is the state it walks into, not news: no blasts for the crates broken
            // before it came, no stage bangs.
            Behaviour.ApplyNet(row, present: !u.Whole);
            Applied++;
        }

        foreach (var off in u.Fired)
        {
            Behaviour.PresentFire(off);
        }

        return true;
    }
}
