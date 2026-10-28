using System.Numerics;
using RetroEng.Net;

namespace SkyNet.Game.Net;

/// <summary>
/// The server's side of the wire (net_game.gd's _srv_* and _s_* halves): admits clients after a
/// hello, checks everything they send through <see cref="DmServer"/>'s gates, relays poses and
/// shots, and sends the rule events out. The host also plays, as player 1: its own pose, shots
/// and hits go straight into the server here, and what happens to it is raised as events (the
/// same ones a <see cref="DmClient"/> raises on a client). A dedicated server (no local name) has no
/// player of its own: only the clients and the bots play.
/// Client peer N plays as player N + 1.
/// </summary>
public sealed class DmHost : IDisposable
{
    private readonly NetEndpoint _ep;
    private readonly NetWriter _w = new();
    private readonly Dictionary<int, double> _helloDeadline = [];
    /// <summary>Peers that said hello to a passworded server: the nonce they must answer, what they said.</summary>
    private readonly Dictionary<int, (byte[] Nonce, DmClass Class, string Name, int Avatar)> _challenged = [];
    /// <summary>When each human client last did something (moved, turned, fired, spoke, used).</summary>
    private readonly Dictionary<int, double> _active = [];
    private readonly Dictionary<int, (Vector3 Pos, float Yaw)> _lastPose = [];
    private DiscoveryResponder? _discovery;
    private RetroEng.Net.Master.MasterAnnouncer? _announcer;
    private RetroEng.Net.Master.InfoResponder? _responder;
    private string _gameId = "skynet";
    /// <summary>The host's own clock (seconds since it started, across arenas).</summary>
    private double _clock;
    private double _poseAccum;
    private bool _muted;

    /// <param name="localName">The host's own player's name; null: a dedicated server, no local player.</param>
    /// <param name="localAvatar">The host's own avatar (1..13; 0 = its class's default).</param>
    public DmHost(NetEndpoint endpoint, DmServer server, string? localName, DmClass localClass, int localAvatar = 0)
    {
        _ep = endpoint;
        _ep.HandlerFailed += (id, peer, e) => RetroEng.Diagnostics.Log.Warn("Net", $"message {id} from peer {peer} dropped: {e.GetType().Name}: {e.Message}");
        Server = server;
        Dedicated = localName == null;
        _ep.Connected += OnConnected;
        _ep.Disconnected += OnDisconnected;
        _ep.On(DmMsg.Hello, OnHello);
        _ep.On(DmMsg.Auth, OnAuth);
        _ep.On(DmMsg.LevelReady, (int peer, ref NetReader r) => { if (Admitted(peer) is { } id) Server.LevelReady(id); });
        _ep.On(DmMsg.Pose, OnPose);
        _ep.On(DmMsg.Fire, OnFire);
        _ep.On(DmMsg.Hit, OnHit);
        _ep.On(DmMsg.Pickup, (int peer, ref NetReader r) => { var key = r.I32(); if (Admitted(peer) is { } id) Server.ClientPickup(id, key); });
        _ep.On(DmMsg.Chat, OnChat);
        _ep.On(DmMsg.SetClass, (int peer, ref NetReader r) =>
        {
            var c = r.U8();
            var avatar = r.U8();
            if (Admitted(peer) is { } id)
            {
                Server.ClientSetClass(id, (DmClass)c, c > 1 ? 0 : DmAvatarTable.FromWire(avatar, (DmClass)c));
            }
        });
        _ep.On(DmMsg.LevelIntent, OnLevelIntent);
        _ep.On(DmMsg.VehicleEnter, (int peer, ref NetReader r) => { var key = r.I32(); if (Admitted(peer, active: true) is { } id) Server.ClientEnterVehicle(id, key); });
        _ep.On(DmMsg.VehicleLeave, (int peer, ref NetReader r) =>
        {
            var pos = r.Vec3();
            var yaw = r.F32();
            if (Admitted(peer, active: true) is { } id)
            {
                Server.ClientLeaveVehicle(id, pos, yaw);
            }
        });

        Wire(server);
        if (localName != null)
        {
            server.AddHuman(DmServer.HostId, localName, localClass, localAvatar);
        }
    }

    /// <summary>The server's rule events turned into messages (again for each arena of a rotation).</summary>
    private void Wire(DmServer server)
    {
        var Server = server;
        Server.PlayerAdded += p => SendPlayerAdd(p);
        Server.Warned += line => Log?.Invoke(line);
        Server.PlayerRemoved += id => All(_w.Begin(DmMsg.PlayerRemove).I32(id), Channel.Reliable);
        Server.HealthChanged += OnHealthChanged;
        Server.Died += OnDied;
        Server.Respawned += (id, sp) =>
        {
            All(_w.Begin(DmMsg.Respawn).I32(id).Vec3(sp.Feet).F32(sp.Yaw), Channel.Reliable);
            if (id == DmServer.HostId)
            {
                LocalRespawned?.Invoke(sp);
            }
        };
        Server.PickupTaken += (key, by) => All(_w.Begin(DmMsg.PickupTaken).I32(key).I32(by), Channel.Reliable);
        Server.PickupSpawned += key => All(_w.Begin(DmMsg.PickupSpawn).I32(key), Channel.Reliable);
        Server.PickupAdded += p =>
        {
            _w.Begin(DmMsg.PickupAdd);
            DmProtocol.WritePickup(_w, p);
            All(_w, Channel.Reliable);
        };
        Server.TimeChanged += sec => All(_w.Begin(DmMsg.Time).I32(sec), Channel.Unreliable);
        Server.MatchOver += reason =>
        {
            _w.Begin(DmMsg.MatchOver).Str(reason, 256);
            DmProtocol.WriteScores(_w, Server.Players.Values.ToList());
            All(_w, Channel.Reliable);
        };
        Server.Restarted += () =>
        {
            All(_w.Begin(DmMsg.Restart), Channel.Reliable);
            // NEW ROUND puts the map back as the arena started (the owner's rule): the server's level
            // first, then every client's copy from the reset update.
            if (Level != null)
            {
                Level.Reset().Write(_w);
                All(_w, Channel.Reliable);
            }
        };
        Server.ClassChanged += (id, cls) => All(_w.Begin(DmMsg.ClassOf).I32(id).U8((byte)cls)
            .U8(DmAvatarTable.ToWire(Server.Players.TryGetValue(id, out var p) ? p.Avatar : DmAvatarTable.ForClass(cls))), Channel.Reliable);
        Server.VehicleChanged += (v, why) =>
        {
            _w.Begin(DmMsg.VehicleState).U8((byte)why);
            DmProtocol.WriteVehicle(_w, v);
            All(_w, Channel.Reliable);
            VehicleChanged?.Invoke(v, why);
        };
    }

    public DmServer Server { get; private set; }

    /// <summary>No player of its own (a dedicated server): only clients and bots play.</summary>
    public bool Dedicated { get; }

    /// <summary>The host's own player (<see cref="DmServer.HostId"/>); 0 on a dedicated server.</summary>
    public int LocalId => Dedicated ? 0 : DmServer.HostId;

    /// <summary>The door rules of an internet server (password, idle drop); the defaults let anyone in.</summary>
    public DmHostPolicy Policy { get; set; } = new();

    public int Port => _ep.Transport.LocalPort;

    /// <summary>A network demo's tap (docs/demos.md): every message a client playing as
    /// <see cref="LocalId"/> would receive from this server (on a dedicated server, one playing as
    /// nobody), as it goes out. The welcome is not among them: the recorder's keyframes stand in for it.</summary>
    public Action<byte[]>? Tap { get; set; }

    /// <summary>A map rotation carried the game to another arena (a recorder's cue for a keyframe).</summary>
    public event Action? ArenaChanged;

    // ---- what happens to the host's own player and what it sees of the others ----

    /// <summary>(player, feet, view yaw, pitch, flags, car yaw).</summary>
    public event Action<int, Vector3, float, float, int, float>? PoseReceived;
    /// <summary>A vehicle's new state (the same the clients get as <see cref="DmMsg.VehicleState"/>).</summary>
    public event Action<DmVehicle, DmVehicleEvent>? VehicleChanged;
    public event Action<int, int, Vector3, Vector3>? Fired;
    /// <summary>(health, armour, attacker).</summary>
    public event Action<float, float, int>? LocalHealthChanged;
    public event Action<DmSpawn>? LocalRespawned;
    public event Action<int, string>? ChatReceived;
    public event Action<string>? Log;
    /// <summary>A client passed the hello (and the password) and was welcomed: its player id.</summary>
    public event Action<int>? Joined;

    /// <summary>Answers LAN probes on <paramref name="port"/>; false (and no listing) when taken.</summary>
    public bool StartDiscovery(int port = DmProtocol.DiscoveryPort, System.Net.IPAddress? bind = null)
    {
        _discovery?.Dispose();
        _discovery = DiscoveryResponder.Start(port, DmProtocol.DiscoveryMagic, bind);
        return _discovery != null;
    }

    public int DiscoveryPort => _discovery?.LocalPort ?? 0;

    /// <summary>The master server's side of a public server (null when not public).</summary>
    public RetroEng.Net.Master.MasterAnnouncer? Master => _announcer;

    /// <summary>Announces this server to the master server at <paramref name="master"/> ("host:port",
    /// docs/multiplayer.md "Master server"): heartbeats from the game socket, the master's challenge
    /// echoed, pings answered, NAT introductions taken part in. False (not listed) on a transport
    /// without UDP or an address that cannot be read.</summary>
    public bool StartMaster(string master, string gameId, System.Net.IPAddress? bind = null)
    {
        if (_ep.Transport is not UdpTransport udp
            || !RetroEng.Net.Master.MasterProtocol.TryParseAddress(master, RetroEng.Net.Master.MasterProtocol.DefaultPort, out var host, out var port))
        {
            return false;
        }

        _gameId = gameId;
        _announcer = new RetroEng.Net.Master.MasterAnnouncer(udp, host, port, MasterInfo, bind);
        _announcer.Log += line => Log?.Invoke(line);
        _responder = new RetroEng.Net.Master.InfoResponder(udp, MasterInfo);
        udp.Unconnected = (from, data) =>
        {
            if (!_announcer.Handle(from, data))
            {
                _responder.Handle(from, data, _clock);
            }
        };
        return true;
    }

    private RetroEng.Net.Master.ServerInfo MasterInfo() =>
        DmProtocol.MasterInfo(_gameId, Server.Settings, Server.Humans, Server.BotCount, Policy.PasswordKey != null);

    /// <summary>One frame: the network, then the rules' clock, then the stale peers.</summary>
    public void Update(double dt)
    {
        _clock += dt;
        _ep.Poll();
        _announcer?.Poll(_clock);
        Server.Update(dt);
        Bots?.Step(dt);
        SendLevel();
        _discovery?.Poll(Server.Now, () => DmProtocol.DiscoveryInfo(Server.Settings, Server.Humans, Server.BotCount, Port, Policy.PasswordKey != null));
        foreach (var (peer, deadline) in _helloDeadline)
        {
            if (Server.Now >= deadline)
            {
                _helloDeadline.Remove(peer);
                Kick(peer, _challenged.ContainsKey(peer) ? "No password answer from this client." : "No hello from this client.");
            }
        }

        if (Policy.IdleSeconds > 0)
        {
            foreach (var (id, at) in _active.ToList())
            {
                if (Server.Now - at >= Policy.IdleSeconds && Server.Players.ContainsKey(id))
                {
                    Kick(PeerOf(id), string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Idle for {Policy.IdleSeconds:0} seconds."));
                }
            }
        }
    }

    /// <summary>Where human player <paramref name="id"/> connects from (null: the host's own, a bot, loopback).</summary>
    public System.Net.IPEndPoint? AddressOf(int id) => id > DmServer.HostId ? _ep.Transport.RemoteEndPoint(PeerOf(id)) : null;

    /// <summary>Seconds since human player <paramref name="id"/> last did something (0 when unknown).</summary>
    public double IdleOf(int id) => _active.TryGetValue(id, out var at) ? Server.Now - at : 0;

    /// <summary>Peers connected but not yet playing (no hello, or no password answer yet).</summary>
    public int Pending => _helloDeadline.Count;

    /// <summary>Closes player <paramref name="id"/>'s connection with <paramref name="reason"/>; false
    /// for the host's own player, a bot or nobody.</summary>
    public bool KickPlayer(int id, string reason)
    {
        if (id <= DmServer.HostId || !Server.Players.ContainsKey(id))
        {
            return false;
        }

        Kick(PeerOf(id), reason);
        return true;
    }

    /// <summary>
    /// A map rotation: <paramref name="next"/> (a server on the next arena, its arena set) takes over.
    /// Every connected client is carried over under the same id, name and class and welcomed again,
    /// which loads the new arena on its machine as a join does; its scores start afresh. The bots are
    /// the caller's to start on the new server (<see cref="DmBots.Start"/>).
    /// </summary>
    public void ChangeArena(DmServer next, DmLevelServer? level)
    {
        var carried = Server.Players.Values.Where(p => !p.Bot && p.Id > DmServer.HostId).ToList();
        var local = Dedicated ? null : Server.Players.GetValueOrDefault(DmServer.HostId);
        Bots = null;
        Level = level;
        Server = next;
        Wire(next);
        _announcer?.Changed();
        _muted = true;
        try
        {
            if (local != null)
            {
                next.AddHuman(DmServer.HostId, local.Name, local.Class, local.Avatar);
            }

            foreach (var p in carried)
            {
                next.AddHuman(p.Id, p.Name, p.Class, p.Avatar);
            }
        }
        finally
        {
            _muted = false;
        }

        foreach (var p in carried)
        {
            if (next.Players.ContainsKey(p.Id))
            {
                _active[p.Id] = next.Now;
                Welcome(PeerOf(p.Id), p.Id);
            }
            else
            {
                Kick(PeerOf(p.Id), "The next map has no room for you.");
            }
        }

        ArenaChanged?.Invoke();
    }

    /// <summary>The host's own pose, sent on at <see cref="DmProtocol.PoseHz"/>.</summary>
    public void SendPose(Vector3 pos, float yaw, float pitch, int flags, double dt, float carYaw = float.NaN)
    {
        _poseAccum += dt;
        if (_poseAccum < 1.0 / DmProtocol.PoseHz)
        {
            return;
        }

        _poseAccum = 0;
        Pose(LocalId, pos, yaw, pitch, flags, carYaw);
    }

    /// <summary>The host's use key on vehicle <paramref name="key"/> (the server's rules, as a client's).</summary>
    public bool RequestVehicle(int key) => Server.EnterVehicle(LocalId, key);

    /// <summary>The host climbs out at <paramref name="pos"/>, the vehicle heading <paramref name="yaw"/>.</summary>
    public bool LeaveVehicle(Vector3 pos, float yaw) => Server.LeaveVehicle(LocalId, pos, yaw);

    /// <summary>A bot's pose (no throttle: the bot brain sends at its own rate).</summary>
    public void BotPose(int id, Vector3 pos, float yaw, float pitch, int flags) => Pose(id, pos, yaw, pitch, flags);

    /// <summary>A driving bot's pose: <paramref name="carYaw"/> is its vehicle's heading.</summary>
    internal void BotPose(int id, Vector3 pos, float yaw, float pitch, int flags, float carYaw) => Pose(id, pos, yaw, pitch, flags, carYaw);

    public void SendFire(int weapon, Vector3 from, Vector3 dir) => Fire(LocalId, weapon, from, dir);

    /// <summary>A bot's shot, drawn by everyone else as a human's is.</summary>
    public void BotFire(int id, int weapon, Vector3 from, Vector3 dir) => Fire(id, weapon, from, dir);

    /// <summary>The server's level (step 8): its changes go out every <see cref="Update"/>, a joiner
    /// gets its overlay after the welcome, the clients' intents run through it. Null: no level (the
    /// headless runner).</summary>
    public DmLevelServer? Level { get; set; }

    /// <summary>The server's bots (step 7), stepped by <see cref="Update"/>; null without bots.</summary>
    public DmBots? Bots { get; set; }

    /// <summary>The host's player (or a bot) hit <paramref name="victim"/>.</summary>
    public void Hit(int victim, float damage, int weapon, int attacker = DmServer.HostId) => Server.Hit(victim, damage, attacker, weapon);

    public void RequestPickup(int key, int by = DmServer.HostId) => Server.Pickup(key, by);

    public void SendChat(string text)
    {
        var t = DmProtocol.CleanText(text, DmProtocol.MaxChatLength);
        if (t.Length > 0)
        {
            Chat(LocalId, t);
        }
    }

    /// <summary>The host has the arena up: its spawn and every bot's.</summary>
    public void ReportLevelReady()
    {
        if (!Dedicated)
        {
            Server.LevelReady(LocalId);
        }

        foreach (var id in Server.Players.Keys.Where(Server.IsBot).ToList())
        {
            Server.LevelReady(id);
        }
    }

    public void Kick(int peer, string reason)
    {
        Log?.Invoke($"kicking peer {peer}: {reason}");
        _helloDeadline.Remove(peer);
        _challenged.Remove(peer);
        _active.Remove(PlayerOf(peer));
        _lastPose.Remove(PlayerOf(peer));
        _ep.Disconnect(peer, reason);
        if (Server.Players.ContainsKey(PlayerOf(peer)))
        {
            Server.RemovePlayer(PlayerOf(peer));
        }
    }

    /// <summary>Everyone told, then the connections closed.</summary>
    public void Shutdown()
    {
        foreach (var id in Server.Players.Keys.Where(id => id > DmServer.HostId).ToList())
        {
            _ep.Disconnect(PeerOf(id), "The server closed the game.");
        }

        _helloDeadline.Clear();
        _challenged.Clear();
    }

    public void Dispose()
    {
        _discovery?.Dispose();
        _announcer?.Goodbye();
        _announcer = null;
        _ep.Dispose();
    }

    public static int PlayerOf(int peer) => peer + 1;

    public static int PeerOf(int player) => player - 1;

    // ---- client messages ----

    private void OnConnected(int peer)
    {
        Log?.Invoke($"peer {peer} connected");
        _helloDeadline[peer] = Server.Now + DmProtocol.HelloTimeout;
    }

    private void OnDisconnected(int peer, DisconnectCause cause, string reason)
    {
        _helloDeadline.Remove(peer);
        _challenged.Remove(peer);
        var id = PlayerOf(peer);
        _active.Remove(id);
        _lastPose.Remove(id);
        if (Server.Players.TryGetValue(id, out var p))
        {
            Log?.Invoke($"{p.Name} left ({cause})");
            Server.RemovePlayer(id);
        }
    }

    private void OnHello(int peer, ref NetReader r)
    {
        var id = PlayerOf(peer);
        if (!_helloDeadline.ContainsKey(peer) || Server.Players.ContainsKey(id))
        {
            return; // a replayed hello: no second admission
        }

        var version = r.I32();
        if (version != DmProtocol.Version)
        {
            Kick(peer, $"Version mismatch: the server speaks network protocol {DmProtocol.Version}, this game {version}. Both need the same build.");
            return;
        }

        var cls = (DmClass)(r.U8() & 1);
        var name = r.Str(64);
        // Protocol 7: the avatar's index, kept only when it is one of the class's (else its default).
        var avatar = DmAvatarTable.FromWire(r.U8(), cls);
        if (Policy.PasswordKey != null)
        {
            if (_challenged.ContainsKey(peer))
            {
                return;
            }

            // Protocol 5: prove the password on a fresh nonce; it never travels itself.
            var nonce = System.Security.Cryptography.RandomNumberGenerator.GetBytes(DmProtocol.NonceLength);
            _challenged[peer] = (nonce, cls, name, avatar);
            _ep.Send(peer, _w.Begin(DmMsg.Challenge).Bytes(nonce), Channel.Reliable);
            return;
        }

        Admit(peer, cls, name, avatar);
    }

    private void OnAuth(int peer, ref NetReader r)
    {
        if (Policy.PasswordKey is not { } key || !_challenged.Remove(peer, out var c))
        {
            return;
        }

        var has = r.Bool();
        if (!has)
        {
            Kick(peer, "This server needs a password.");
            return;
        }

        if (!DmProtocol.ProofMatches(key, c.Nonce, r.Bytes(32)))
        {
            Log?.Invoke($"peer {peer} ({DmProtocol.CleanText(c.Name, DmProtocol.MaxNameLength)}) gave a wrong password");
            Kick(peer, "Wrong password.");
            return;
        }

        Admit(peer, c.Class, c.Name, c.Avatar);
    }

    /// <summary>The hello (and password) passed: the joiner gets its player and the welcome.</summary>
    private void Admit(int peer, DmClass cls, string name, int avatar)
    {
        var id = PlayerOf(peer);
        _helloDeadline.Remove(peer);
        if (Server.AddHuman(id, name, cls, avatar) is not { } p)
        {
            Kick(peer, "Server is full.");
            return;
        }

        _active[id] = Server.Now;
        Welcome(peer, id);
        Log?.Invoke($"{p.Name} joined as player {id}{(AddressOf(id) is { } a ? $" from {a.Address}" : "")}");
        Joined?.Invoke(id);
    }

    /// <summary>The welcome: the joiner's id, the game, the roster, the items, the clock, the
    /// vehicles; then where everyone stands and the map's state.</summary>
    private void Welcome(int peer, int id)
    {
        DmProtocol.WriteWelcome(_w, id, Server.Settings, Server.Players.Values, Server.Pickups.Values, Server.TimeLeft, Server.MatchRunning, Server.Vehicles.Values);
        _ep.Send(peer, _w, Channel.Reliable);
        // Where everyone alive stands now (poses only flow while people move).
        foreach (var other in Server.Players.Values)
        {
            if (other.Id != id && other.Alive)
            {
                _ep.Send(peer, _w.Begin(DmMsg.PoseOf).I32(other.Id).Vec3(other.Position).F32(other.Yaw).F32(other.Pitch)
                    .I32(Server.ServerFlags(other.Id, DmProtocol.WeaponBits(other.Weapon))).F32(Server.VehicleOf(other.Id)?.Yaw ?? other.Yaw), Channel.Unreliable);
            }
        }

        // The map as the server has it now: a late joiner walks into the doors already open.
        if (Level != null)
        {
            Level.Overlay().Write(_w);
            _ep.Send(peer, _w, Channel.Reliable);
        }
    }

    private void OnPose(int peer, ref NetReader r)
    {
        var pos = r.Vec3();
        var yaw = r.F32();
        var pitch = r.F32();
        var flags = r.I32();
        var carYaw = r.F32();
        if (Admitted(peer) is not { } id)
        {
            return;
        }

        // Moving or turning counts as being there; a pose that stands still does not.
        if (!_lastPose.TryGetValue(id, out var last) || Vector3.DistanceSquared(last.Pos, pos) > 4f || MathF.Abs(last.Yaw - yaw) > 0.01f)
        {
            _active[id] = Server.Now;
        }

        _lastPose[id] = (pos, yaw);
        if (Server.ClientPose(id, pos, yaw, pitch, flags, carYaw, out var accepted))
        {
            Pose(id, accepted, yaw, pitch, flags, carYaw);
        }
    }

    private void OnFire(int peer, ref NetReader r)
    {
        var weapon = r.I32();
        var from = r.Vec3();
        var dir = r.Vec3();
        if (Admitted(peer, active: true) is { } id && Server.ClientFire(id, weapon, from, dir))
        {
            Fire(id, weapon, from, Vector3.Normalize(dir));
        }
    }

    private void OnHit(int peer, ref NetReader r)
    {
        var victim = r.I32();
        var damage = r.F32();
        var weapon = r.I32();
        if (Admitted(peer, active: true) is { } id)
        {
            Server.ClientHit(id, victim, damage, weapon);
        }
    }

    private void OnChat(int peer, ref NetReader r)
    {
        var text = r.Str(DmProtocol.MaxChatLength * 4);
        if (Admitted(peer, active: true) is { } id && Server.ClientChat(id, text) is { } clean)
        {
            Chat(id, clean);
        }
    }

    private void OnLevelIntent(int peer, ref NetReader r)
    {
        var kind = (Behaviour.LevelIntentKind)r.U8();
        var record = r.I32();
        var eye = r.Vec3();
        var damage = r.F32();
        if (Admitted(peer, active: true) is not { } id || Level == null)
        {
            return;
        }

        switch (kind)
        {
            case Behaviour.LevelIntentKind.Use:
                Level.ClientUse(Server, id, eye, record);
                break;
            case Behaviour.LevelIntentKind.Hit:
                Level.ClientHit(Server, id, record, damage);
                break;
        }
    }

    /// <summary>The level's walk-in sweep for the other bodies, then what changed, to everybody.</summary>
    private void SendLevel()
    {
        if (Level == null)
        {
            return;
        }

        Level.Sweep(Server);
        if (Level.Delta() is { } delta)
        {
            delta.Write(_w);
            All(_w, Channel.Reliable);
        }
    }

    private int? Admitted(int peer, bool active = false)
    {
        var id = PlayerOf(peer);
        if (!Server.Players.ContainsKey(id))
        {
            return null;
        }

        if (active)
        {
            _active[id] = Server.Now;
        }

        return id;
    }

    // ---- out ----

    private void Pose(int id, Vector3 pos, float yaw, float pitch, int flags, float carYaw = float.NaN)
    {
        flags = Server.Pose(id, pos, yaw, pitch, flags, carYaw);
        carYaw = Server.VehicleOf(id)?.Yaw ?? yaw;
        _w.Begin(DmMsg.PoseOf).I32(id).Vec3(pos).F32(yaw).F32(pitch).I32(flags).F32(carYaw);
        All(_w, Channel.Unreliable, except: id);
        if (id != LocalId)
        {
            PoseReceived?.Invoke(id, pos, yaw, pitch, flags, carYaw);
        }
    }

    private void Fire(int id, int weapon, Vector3 from, Vector3 dir)
    {
        if (Server.Players.TryGetValue(id, out var p))
        {
            p.Weapon = weapon;
        }

        All(_w.Begin(DmMsg.FireOf).I32(id).I32(weapon).Vec3(from).Vec3(dir), Channel.Reliable, except: id);
        if (id != LocalId)
        {
            Fired?.Invoke(id, weapon, from, dir);
        }
    }

    /// <summary>A line from the server itself (a dedicated server's <c>say</c>): player 0, shown as SERVER.</summary>
    public void ServerChat(string text)
    {
        var t = DmProtocol.CleanText(text, DmProtocol.MaxChatLength);
        if (t.Length > 0)
        {
            Chat(0, t);
        }
    }

    /// <summary>A server line to one client only (the message of the day).</summary>
    public void ServerChatTo(int id, string text)
    {
        var t = DmProtocol.CleanText(text, DmProtocol.MaxChatLength);
        if (t.Length > 0 && id > DmServer.HostId && Server.Players.ContainsKey(id))
        {
            _ep.Send(PeerOf(id), _w.Begin(DmMsg.ChatOf).I32(0).Str(t, DmProtocol.MaxChatLength * 4), Channel.Reliable);
        }
    }

    private void Chat(int from, string text)
    {
        All(_w.Begin(DmMsg.ChatOf).I32(from).Str(text, DmProtocol.MaxChatLength * 4), Channel.Reliable);
        ChatReceived?.Invoke(from, text);
    }

    private void SendPlayerAdd(DmPlayer p)
    {
        _w.Begin(DmMsg.PlayerAdd);
        DmProtocol.WritePlayer(_w, p);
        All(_w, Channel.Reliable, except: p.Id);
    }

    private void OnHealthChanged(int id, int attacker)
    {
        var p = Server.Players[id];
        if (id == LocalId)
        {
            Tap?.Invoke(_w.Begin(DmMsg.Health).F32(p.Health).F32(p.Armour).I32(attacker).ToArray());
            LocalHealthChanged?.Invoke(p.Health, p.Armour, attacker);
        }
        else if (id > LocalId)
        {
            _ep.Send(PeerOf(id), _w.Begin(DmMsg.Health).F32(p.Health).F32(p.Armour).I32(attacker), Channel.Reliable);
        }
    }

    private void OnDied(int victim, int killer, int weapon)
    {
        _w.Begin(DmMsg.Died).I32(victim).I32(killer).I32(weapon);
        DmProtocol.WriteScores(_w, Server.Players.Values.ToList());
        All(_w, Channel.Reliable);
        if (Server.Players.TryGetValue(victim, out var v))
        {
            Log?.Invoke($"{v.Name} died (killer {killer}, weapon {weapon})");
        }
    }

    /// <summary>To every admitted human client except <paramref name="except"/>'s.</summary>
    private void All(NetWriter w, Channel ch, int except = 0)
    {
        if (_muted)
        {
            return;
        }

        if (Tap != null && (except == 0 || except != LocalId))
        {
            Tap(w.ToArray());
        }

        foreach (var id in Server.Players.Keys)
        {
            if (id > DmServer.HostId && id != except)
            {
                _ep.Send(PeerOf(id), w, ch);
            }
        }
    }
}

/// <summary>A server's door rules (the dedicated server's config): a password and the idle drop.
/// The address rules (bans, connections per address) are checked before a connection is accepted
/// (<see cref="RetroEng.Net.UdpTransport.Screen"/>).</summary>
public sealed record DmHostPolicy
{
    /// <summary><see cref="DmProtocol.PasswordKey"/> of the password; null: no password.</summary>
    public byte[]? PasswordKey { get; init; }

    /// <summary>A client that neither moves, turns, fires, speaks nor uses anything for this long is
    /// dropped (0: never).</summary>
    public double IdleSeconds { get; init; }
}
