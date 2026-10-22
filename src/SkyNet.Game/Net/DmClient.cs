using System.Numerics;
using RetroEng.Net;

namespace SkyNet.Game.Net;

/// <summary>
/// A client's side of the wire (net_game.gd's client half): says hello once connected, keeps a
/// copy of the roster, the items and the clock from what the server sends, and raises it as
/// events. It simulates nothing but its own player: its pose, shots and the hits it scores go to
/// the server, which answers with health, deaths and respawns.
/// </summary>
public sealed class DmClient : IDisposable
{
    private readonly NetEndpoint _ep;
    private readonly NetWriter _w = new();
    private readonly Dictionary<int, DmPlayer> _players = [];
    private readonly Dictionary<int, DmPickup> _pickups = [];
    private readonly Dictionary<int, DmVehicle> _vehicles = [];
    private double _poseAccum;

    /// <param name="password">The server's password, if it has one (proved on its nonce, never sent).</param>
    /// <param name="avatar">The avatar to wear (1..13; 0 = the class's default).</param>
    public DmClient(NetEndpoint endpoint, string name, DmClass cls, string? password = null, int avatar = 0)
    {
        _ep = endpoint;
        _ep.HandlerFailed += (id, peer, e) => RetroEng.Diagnostics.Log.Warn("Net", $"message {id} from peer {peer} dropped: {e.GetType().Name}: {e.Message}");
        Name = DmProtocol.CleanText(name, DmProtocol.MaxNameLength) is { Length: > 0 } n ? n : "PLAYER";
        Class = cls;
        Avatar = DmAvatarTable.Resolve(avatar, cls);
        _ep.Connected += _ => _ep.Send(INetTransport.ServerPeer, DmProtocol.WriteHello(_w, Class, Name, Avatar), Channel.Reliable);
        _ep.Disconnected += (_, cause, reason) =>
        {
            Active = false;
            Disconnected?.Invoke(reason.Length > 0 ? reason : cause switch
            {
                DisconnectCause.ConnectionFailed => "Could not reach the server.",
                DisconnectCause.Rejected => "The server turned the connection down.",
                DisconnectCause.Timeout => "The server stopped answering.",
                DisconnectCause.Local => "Left the game.",
                _ => "The server closed the game.",
            });
        };
        _ep.On(DmMsg.Welcome, OnWelcome);
        _ep.On(DmMsg.Challenge, (int _, ref NetReader r) =>
        {
            var nonce = r.Bytes(DmProtocol.NonceLength);
            _w.Begin(DmMsg.Auth).Bool(!string.IsNullOrEmpty(password));
            if (!string.IsNullOrEmpty(password))
            {
                _w.Bytes(DmProtocol.PasswordProof(DmProtocol.PasswordKey(password), nonce));
            }

            Send(_w, Channel.Reliable);
        });
        _ep.On(DmMsg.PlayerAdd, (int _, ref NetReader r) =>
        {
            var p = DmProtocol.ReadPlayer(ref r);
            _players[p.Id] = p;
            PlayerJoined?.Invoke(p.Id);
        });
        _ep.On(DmMsg.PlayerRemove, (int _, ref NetReader r) =>
        {
            var id = r.I32();
            if (_players.Remove(id))
            {
                PlayerLeft?.Invoke(id);
            }
        });
        _ep.On(DmMsg.PoseOf, OnPose);
        _ep.On(DmMsg.FireOf, (int _, ref NetReader r) =>
        {
            var id = r.I32();
            var weapon = r.I32();
            var from = r.Vec3();
            var dir = r.Vec3();
            if (_players.TryGetValue(id, out var p))
            {
                p.Weapon = weapon;
            }

            Fired?.Invoke(id, weapon, from, dir);
        });
        _ep.On(DmMsg.Health, (int _, ref NetReader r) =>
        {
            var hp = r.F32();
            var armour = r.F32();
            var attacker = r.I32();
            if (_players.TryGetValue(LocalId, out var me))
            {
                me.Health = hp;
                me.Armour = armour;
            }

            LocalHealthChanged?.Invoke(hp, armour, attacker);
        });
        _ep.On(DmMsg.Died, (int _, ref NetReader r) =>
        {
            var victim = r.I32();
            var killer = r.I32();
            var weapon = r.I32();
            DmProtocol.ReadScores(ref r, _players);
            if (_players.TryGetValue(victim, out var v))
            {
                v.Alive = false;
                v.Health = 0f;
            }

            Died?.Invoke(victim, killer, weapon);
        });
        _ep.On(DmMsg.Respawn, (int _, ref NetReader r) =>
        {
            var id = r.I32();
            var sp = new DmSpawn(r.Vec3(), r.F32());
            if (_players.TryGetValue(id, out var p))
            {
                p.Alive = true;
                p.Health = DmProtocol.DosHealth;
                p.Armour = 0f;
                p.Position = sp.Feet;
                p.Yaw = sp.Yaw;
            }

            Respawned?.Invoke(id, sp);
        });
        _ep.On(DmMsg.PickupTaken, (int _, ref NetReader r) =>
        {
            var key = r.I32();
            var by = r.I32();
            if (_pickups.TryGetValue(key, out var p))
            {
                p.Taken = true;
            }

            PickupTaken?.Invoke(key, by);
        });
        _ep.On(DmMsg.PickupSpawn, (int _, ref NetReader r) =>
        {
            var key = r.I32();
            if (_pickups.TryGetValue(key, out var p))
            {
                p.Taken = false;
            }

            PickupSpawned?.Invoke(key);
        });
        _ep.On(DmMsg.PickupAdd, (int _, ref NetReader r) =>
        {
            var p = DmProtocol.ReadPickup(ref r);
            _pickups[p.Key] = p;
            PickupSpawned?.Invoke(p.Key);
        });
        _ep.On(DmMsg.ChatOf, (int _, ref NetReader r) =>
        {
            var from = r.I32();
            var text = DmProtocol.CleanText(r.Str(DmProtocol.MaxChatLength * 4), DmProtocol.MaxChatLength);
            ChatReceived?.Invoke(from, text);
        });
        _ep.On(DmMsg.Time, (int _, ref NetReader r) =>
        {
            TimeLeft = r.I32();
            TimeChanged?.Invoke((int)TimeLeft);
        });
        _ep.On(DmMsg.MatchOver, (int _, ref NetReader r) =>
        {
            var reason = r.Str(256);
            DmProtocol.ReadScores(ref r, _players);
            MatchRunning = false;
            MatchOver?.Invoke(reason);
        });
        _ep.On(DmMsg.Restart, (int _, ref NetReader r) =>
        {
            MatchRunning = true;
            foreach (var p in _players.Values)
            {
                p.Frags = p.Deaths = p.Points = 0;
            }

            foreach (var k in _pickups.Where(kv => kv.Value.Drop).Select(kv => kv.Key).ToList())
            {
                _pickups.Remove(k);
            }

            foreach (var p in _pickups.Values)
            {
                p.Taken = false;
            }

            Restarted?.Invoke();
        });
        _ep.On(DmMsg.ClassOf, (int _, ref NetReader r) =>
        {
            var id = r.I32();
            var cls = (DmClass)(r.U8() & 1);
            var avatar = DmAvatarTable.FromWire(r.U8(), cls);
            if (_players.TryGetValue(id, out var p))
            {
                (p.Class, p.Avatar) = (cls, avatar);
            }

            ClassChanged?.Invoke(id, cls);
        });
        _ep.On(DmMsg.LevelState, (int _, ref NetReader r) => LevelState?.Invoke(LevelUpdate.Read(ref r)));
        _ep.On(DmMsg.VehicleState, (int _, ref NetReader r) =>
        {
            var why = (DmVehicleEvent)r.U8();
            var v = DmProtocol.ReadVehicle(ref r);
            _vehicles[v.Key] = v;
            VehicleChanged?.Invoke(v, why);
        });
    }

    public string Name { get; }

    /// <summary>The endpoint (a network demo's recorder taps what it receives).</summary>
    public NetEndpoint Endpoint => _ep;

    public DmClass Class { get; private set; }
    /// <summary>The avatar asked for (1..13, one of <see cref="Class"/>).</summary>
    public int Avatar { get; private set; }

    /// <summary>True from the welcome until the connection ends.</summary>
    public bool Active { get; private set; }

    /// <summary>This client's player id (from the welcome; 0 before).</summary>
    public int LocalId { get; private set; }

    public DmSettings? Settings { get; private set; }

    public IReadOnlyDictionary<int, DmPlayer> Players => _players;

    public IReadOnlyDictionary<int, DmPickup> Pickups => _pickups;

    /// <summary>The arena's vehicles as the server last told them.</summary>
    public IReadOnlyDictionary<int, DmVehicle> Vehicles => _vehicles;

    public double TimeLeft { get; private set; }

    public bool MatchRunning { get; private set; }

    public event Action? Welcomed;
    public event Action<string>? Disconnected;
    public event Action<int>? PlayerJoined;
    public event Action<int>? PlayerLeft;
    /// <summary>(player, feet, view yaw, pitch, flags, car yaw).</summary>
    public event Action<int, Vector3, float, float, int, float>? PoseReceived;
    public event Action<DmVehicle, DmVehicleEvent>? VehicleChanged;
    public event Action<int, int, Vector3, Vector3>? Fired;
    public event Action<float, float, int>? LocalHealthChanged;
    public event Action<int, int, int>? Died;
    public event Action<int, DmSpawn>? Respawned;
    public event Action<int, int>? PickupTaken;
    public event Action<int>? PickupSpawned;
    public event Action<int, string>? ChatReceived;
    public event Action<int>? TimeChanged;
    public event Action<string>? MatchOver;
    public event Action? Restarted;
    public event Action<int, DmClass>? ClassChanged;
    /// <summary>The server's level state: the overlay after the welcome, then the deltas.</summary>
    public event Action<LevelUpdate>? LevelState;

    /// <summary>One frame: the network, and the round's clock between the server's ticks.</summary>
    public void Update(double dt)
    {
        _ep.Poll();
        if (MatchRunning && TimeLeft > 0)
        {
            TimeLeft = Math.Max(TimeLeft - dt, 0);
        }
    }

    /// <summary>The arena is loaded here: asks the server for the first spawn.</summary>
    public void ReportLevelReady() => Send(_w.Begin(DmMsg.LevelReady), Channel.Reliable);

    /// <summary>The own pose, sent on at <see cref="DmProtocol.PoseHz"/>.</summary>
    public void SendPose(Vector3 pos, float yaw, float pitch, int flags, double dt, float carYaw = float.NaN)
    {
        _poseAccum += dt;
        if (!Active || _poseAccum < 1.0 / DmProtocol.PoseHz)
        {
            return;
        }

        _poseAccum = 0;
        if (_players.TryGetValue(LocalId, out var me))
        {
            me.Position = pos;
        }

        Send(_w.Begin(DmMsg.Pose).Vec3(pos).F32(yaw).F32(pitch).I32(flags).F32(float.IsFinite(carYaw) ? carYaw : yaw), Channel.Unreliable);
    }

    /// <summary>The use key on parked vehicle <paramref name="key"/>: the server gives the seat or not.</summary>
    public void RequestVehicle(int key)
    {
        if (Active && _vehicles.TryGetValue(key, out var v) && v.Driver == 0 && !v.Wrecked)
        {
            Send(_w.Begin(DmMsg.VehicleEnter).I32(key), Channel.Reliable);
        }
    }

    /// <summary>Climbs out: the vehicle stays at <paramref name="pos"/>, heading <paramref name="yaw"/>.</summary>
    public void LeaveVehicle(Vector3 pos, float yaw)
    {
        if (Active)
        {
            Send(_w.Begin(DmMsg.VehicleLeave).Vec3(pos).F32(yaw), Channel.Reliable);
        }
    }

    public void SendFire(int weapon, Vector3 from, Vector3 dir) => Send(_w.Begin(DmMsg.Fire).I32(weapon).Vec3(from).Vec3(dir), Channel.Reliable);

    /// <summary>This client hit <paramref name="victim"/> (or itself: drowning, radiation).</summary>
    public void SendHit(int victim, float damage, int weapon)
    {
        if (float.IsFinite(damage) && damage > 0f)
        {
            Send(_w.Begin(DmMsg.Hit).I32(victim).F32(damage).I32(weapon), Channel.Reliable);
        }
    }

    public void RequestPickup(int key)
    {
        if (_pickups.TryGetValue(key, out var p) && !p.Taken)
        {
            Send(_w.Begin(DmMsg.Pickup).I32(key), Channel.Reliable);
        }
    }

    public void SendChat(string text)
    {
        var t = DmProtocol.CleanText(text, DmProtocol.MaxChatLength);
        if (t.Length > 0)
        {
            Send(_w.Begin(DmMsg.Chat).Str(t, DmProtocol.MaxChatLength * 4), Channel.Reliable);
        }
    }

    /// <summary>A class (and avatar: 0 keeps the current one if it is of the class) for the next life.</summary>
    public void SetClass(DmClass cls, int avatar = 0)
    {
        Avatar = DmAvatarTable.Resolve(avatar != 0 ? avatar : Avatar, cls);
        Class = cls;
        Send(_w.Begin(DmMsg.SetClass).U8((byte)cls).U8(DmAvatarTable.ToWire(Avatar)), Channel.Reliable);
    }

    /// <summary>The use key or a shot on a level record, for the server's level to run.</summary>
    public void SendLevelIntent(in Behaviour.LevelIntent i)
    {
        if (Active)
        {
            Send(_w.Begin(DmMsg.LevelIntent).U8((byte)i.Kind).I32(i.Record).Vec3(i.Eye).F32(i.Damage), Channel.Reliable);
        }
    }

    /// <summary>Leaves the game (the server sees the peer go).</summary>
    public void Leave() => _ep.Disconnect(INetTransport.ServerPeer, "");

    public void Dispose() => _ep.Dispose();

    private void Send(NetWriter w, Channel ch) => _ep.Send(INetTransport.ServerPeer, w, ch);

    private void OnWelcome(int peer, ref NetReader r)
    {
        LocalId = r.I32();
        Settings = DmProtocol.ReadSettings(ref r);
        _players.Clear();
        int n = r.U16();
        for (var i = 0; i < n; i++)
        {
            var p = DmProtocol.ReadPlayer(ref r);
            _players[p.Id] = p;
        }

        _pickups.Clear();
        n = r.U16();
        for (var i = 0; i < n; i++)
        {
            var p = DmProtocol.ReadPickup(ref r);
            _pickups[p.Key] = p;
        }

        TimeLeft = r.I32();
        MatchRunning = r.Bool();
        _vehicles.Clear();
        n = r.U16();
        for (var i = 0; i < n; i++)
        {
            var v = DmProtocol.ReadVehicle(ref r);
            _vehicles[v.Key] = v;
        }

        Active = true;
        Welcomed?.Invoke();
    }

    private void OnPose(int peer, ref NetReader r)
    {
        var id = r.I32();
        var pos = r.Vec3();
        var yaw = r.F32();
        var pitch = r.F32();
        var flags = r.I32();
        var carYaw = r.F32();
        if (_players.TryGetValue(id, out var p))
        {
            p.Position = pos;
            p.Yaw = yaw;
            p.Pitch = pitch;
        }

        PoseReceived?.Invoke(id, pos, yaw, pitch, flags, carYaw);
    }
}
