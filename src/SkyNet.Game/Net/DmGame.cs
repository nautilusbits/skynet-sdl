using System.Globalization;
using System.Net;
using System.Numerics;
using RetroEng.Commands;
using RetroEng.Diagnostics;
using RetroEng.Net;
using SkyNet.Formats;
using SkyNet.Game.Audio;
using SkyNet.Game.Campaign;
using SkyNet.Game.Demos;
using SkyNet.Game.Enemies;
using SkyNet.Game.Player;
using SkyNet.Game.Vehicles;
using SkyNet.Game.Weapons;

namespace SkyNet.Game.Net;

/// <summary>
/// A deathmatch in the game's window (steps 2 and 3 of docs/multiplayer.md; the Godot port's
/// dm_game.gd): the campaign's <see cref="GameSession"/> plays the arena in
/// <see cref="GameSession.Deathmatch"/> mode with its player controller and weapons; this joins it
/// to the wire. The own pose goes out at 20 Hz; the shots the weapons fire go out to be drawn by
/// the others; what the weapons strike on another player's body (<see cref="DmAvatar"/>, a sphere
/// target) is reported as a hit; the soldier's own hurts (his blast, a fall, the water) go to the
/// server as self-hits (<see cref="Soldier.Remote"/>). The server's word sets the bar, kills and
/// respawns. The others are drawn by <see cref="AvatarRenderer"/>, their shots by the weapons'
/// <see cref="WeaponSystem.RemoteFire"/>. The host is the server and plays as player 1; its
/// spawns get the campaign's clear-spawn nudge (<see cref="PlayerStart.Clear"/>) on its own level.
/// Step 4: the arena's items are the server's list (<see cref="Items"/>, drawn as the map's
/// pickups are), asked for when the soldier stands in DOS's grab box and taken on the server's
/// word; the taker gets the ammo and the weapon here, health and armour come from the server.
/// Step 5: what <see cref="DmHud"/> shows (frags, clock, kill feed, chat, the centre line, the
/// scoreboard, the end of the round), the chat line, the host's new round, the class for the
/// next life, the death view (dm_game.gd and fly_camera.gd begin_death_view).
/// Step 9: the arena's jeeps and HKs (<see cref="Vehicles"/>): the use key boards the first free one
/// within DOS's reach or climbs out, both on the server's word; this machine's ride is the campaign's
/// <see cref="VehicleRig"/> (the session's <see cref="GameSession.Ride"/>) with its hull the server's.
/// </summary>
public sealed class DmGame : IDisposable
{
    /// <summary>The firing flag stays in the pose this long after a shot.</summary>
    public const float FiringHold = 0.25f;

    /// <summary>dm_game.gd: six chat lines for ten seconds.</summary>
    public const int ChatLines = 6;
    public const double ChatSeconds = 10.0;

    /// <summary>pickup.gd: a pickup asks the server again after half a second.</summary>
    public const double PickupAskEvery = 0.5;

    /// <summary>fly_camera.gd's death view: the eye sinks to 14 in half a second, the head tips 0.35
    /// rad and looks 0.22 rad down.</summary>
    public const float DeathEye = 14f, DeathFall = 0.5f, DeathRoll = 0.35f, DeathPitch = -0.22f;

    /// <summary>Commands that would leave the arena or change the session under the wire (the
    /// Godot port's NET_REFUSED, its session-changing part).</summary>
    public static readonly string[] Refused = ["map", "mission", "restart", "nextlevel", "win", "save", "load"];

    /// <summary>The cheats (the Godot port's NET_REFUSED, its cheat part; DOS skips its flagged
    /// CHEAT.PRS codes in a network game, FUN_00142658: god, every weapon, the super UZI, ammo,
    /// health and armour...): refused in a network game unless the host allowed them
    /// (<see cref="DmSettings.Cheats"/>).</summary>
    public static readonly string[] Cheats =
        ["tp", "god", "noclip", "give", "arnold", "superuzi", "slugs", "surgery", "health", "armor", "weapon", "killall"];

    /// <summary>A line of the chat, and when it goes.</summary>
    public readonly record struct Line(string Text, Vector4 Colour, double Until);

    private readonly GameSession _session;
    private readonly VehicleRig? _rig;
    /// <summary>The vehicle this machine's player drives (its key), 0 on foot.</summary>
    private int _driving;
    private readonly DmHost? _host;
    private readonly DmClient? _client;
    private readonly Dictionary<int, DmAvatar> _avatars = [];
    private readonly EnemyModels _models;
    private readonly ParkedVehicleColliders _parked;
    private readonly Action<int, Vector3, Vector3> _onShot;
    private readonly Dictionary<int, string> _names = [];
    /// <summary>Each vehicle's driver as last seen here (0 none), for <see cref="SeatLine"/>.</summary>
    private readonly Dictionary<int, int> _seats = [];
    /// <summary>The engine loop each other driver is heard with (<see cref="Engines"/>).</summary>
    private readonly Dictionary<int, (VehicleMode Mode, Audio.SoundBank.LoopHandle Loop)> _engines = [];
    private readonly Dictionary<int, double> _asked = [];
    private readonly List<DroppedSprite> _items = [];
    private readonly List<Line> _chat = [];
    /// <summary>Picks DOS's PlayerDiedN / PlayerSuicideN line (rand &amp; 7, rand &amp; 3, each machine its own).</summary>
    private readonly Random _lineRng = new();
    private double _restartedAt = double.NegativeInfinity;
    private AvatarRenderer? _view;
    /// <summary>The frame <see cref="Draw"/> captures into (a draw on the simulation's thread).</summary>
    private readonly DmFrame _frame = new();
    /// <summary>Console <c>navmesh on</c>: the bots' mesh and paths drawn; the outline built once per mesh.</summary>
    private bool _showNav;
    private (Navigation.NavMesh? Mesh, RetroEng.Render.Vertex[]? Lines) _navOutline;
    private DmLevelClient? _level;
    private readonly List<LevelUpdate> _levelEarly = [];
    private float _firing;
    private readonly ScannerSweep _sweep = new();
    private readonly ScannerBeeps _beeps = new();
    private bool _levelUp;
    private bool _itemsDirty = true;
    private double _clock;
    private double? _diedAt;
    private (string Text, double Until) _centre = ("", 0);
    /// <summary>A network demo being recorded here (docs/demos.md "Network demos").</summary>
    private NetDemoRecorder? _recorder;
    /// <summary>A network demo playing: the client is fed from it and nothing is sent anywhere.</summary>
    private readonly NetDemoPlayer? _replay;
    private readonly NetWriter _dw = new();
    /// <summary>Playback: the recording player's body, drawn when the camera is not its own eye.</summary>
    private DmAvatar? _self;

    private DmGame(GameSession session, DmHost? host, DmClient? client, NetDemoPlayer? replay = null)
    {
        _replay = replay;
        _session = session;
        _host = host;
        _client = client;
        _rig = session.Ride as VehicleRig;
        session.DeathmatchUse = UseVehicle;
        // The other players' bodies block the soldier as his blocks theirs (on foot; a vehicle is
        // its own business).
        session.DeathmatchBlock = (before, after) => _driving != 0 ? after
            : BodyBlock.Separate(before, after, _avatars.Values.Where(a => a.Alive && a.Visible)
                .Select(a => (a.Position, a.Vehicle == VehicleMode.Foot ? BodyBlock.Radius : DmAvatar.VehicleRadius(a.Vehicle))),
                session.World, session.Player.Body);
        if (_rig != null)
        {
            _rig.Hull.Remote = SelfHit;
        }

        _models = new EnemyModels(session.Data);
        // The parked vehicles stop bodies and shots as DOS's vehicle records do.
        _parked = new ParkedVehicleColliders(() => (IEnumerable<DmVehicle>?)_host?.Server.Vehicles.Values ?? _client?.Vehicles.Values ?? [], _models);
        session.World.Add(_parked);
        _onShot = (record, from, dir) =>
        {
            _firing = FiringHold;
            if (Spawned && _recorder != null)
            {
                _recorder.Local(NetDemoLocal.WriteFire(_dw, record, from, dir));
            }

            if (Spawned)
            {
                if (_host != null)
                {
                    _host.SendFire(record, from, dir);
                }
                else
                {
                    _client!.SendFire(record, from, dir);
                }
            }
        };
        session.Weapons.Shot += _onShot;
        session.Soldier.Remote = SelfHit;
        if (host != null)
        {
            host.PoseReceived += OnPose;
            host.Fired += OnFired;
            host.LocalHealthChanged += OnLocalHealth;
            host.LocalRespawned += sp => OnRespawned(LocalId, sp);
            host.Server.Respawned += (id, sp) =>
            {
                if (id != LocalId)
                {
                    OnRespawned(id, sp);
                }
            };
            host.Server.Died += OnDied;
            host.Server.PlayerAdded += p => Joined(p.Id);
            host.Server.PlayerRemoved += Left;
            host.Server.PickupTaken += OnPickupTaken;
            host.Server.PickupSpawned += _ => _itemsDirty = true;
            host.Server.PickupAdded += _ => _itemsDirty = true;
            host.Server.MatchOver += OnMatchOver;
            host.Server.Restarted += OnRestarted;
            host.Server.ClassChanged += OnClassChanged;
            host.ChatReceived += OnChat;
            host.VehicleChanged += OnVehicle;
            host.Log += s => Log.Info("Net", s);
            host.Tap = bytes => _recorder?.Message(bytes);
        }
        else
        {
            client!.Welcomed += OnWelcome;
            client.Disconnected += reason =>
            {
                Log.Warn("Net", $"disconnected: {reason}");
                Ended?.Invoke(reason);
            };
            client.PlayerJoined += Joined;
            client.PlayerLeft += Left;
            client.PoseReceived += OnPose;
            client.Fired += OnFired;
            client.LocalHealthChanged += OnLocalHealth;
            client.Died += OnDied;
            client.Respawned += OnRespawned;
            client.PickupTaken += OnPickupTaken;
            client.PickupSpawned += _ => _itemsDirty = true;
            client.MatchOver += OnMatchOver;
            client.Restarted += OnRestarted;
            client.ClassChanged += OnClassChanged;
            client.ChatReceived += OnChat;
            client.LevelState += OnLevelState;
            client.VehicleChanged += OnVehicle;
            client.Endpoint.Tap = (_, data, _) => _recorder?.Message(data);
        }
    }

    public bool IsHost => _host != null;

    public DmHost? Host => _host;

    public DmClient? Client => _client;

    public int LocalId => _host?.LocalId ?? _client!.LocalId;

    /// <summary>The arena is loaded (the host's at once, a client's on the welcome).</summary>
    public bool LevelUp => _levelUp;

    /// <summary>The server's spawn put the own player in the arena (poses flow from then on).</summary>
    public bool Spawned { get; private set; }

    public IReadOnlyDictionary<int, DmAvatar> Avatars => _avatars;

    public IReadOnlyDictionary<int, DmPlayer> Players => _host != null ? _host.Server.Players : _client!.Players;

    /// <summary>The arena's items as the server lists them (taken ones too).</summary>
    public IReadOnlyDictionary<int, DmPickup> Pickups => _host != null ? _host.Server.Pickups : _client!.Pickups;

    /// <summary>The arena's vehicles as the server has them (a client's copy).</summary>
    public IReadOnlyDictionary<int, DmVehicle> Vehicles => _host != null ? _host.Server.Vehicles : _client!.Vehicles;

    /// <summary>The vehicle this machine's player drives (its key), 0 on foot.</summary>
    public int Driving => _driving;

    /// <summary>The items lying in the arena now, as sprites on their foot (drawn as the map's drops are).</summary>
    public IReadOnlyList<DroppedSprite> Items
    {
        get
        {
            if (_itemsDirty)
            {
                _itemsDirty = false;
                _items.Clear();
                foreach (var p in Pickups.Values.OrderBy(p => p.Key))
                {
                    if (!p.Taken)
                    {
                        _items.Add(new DroppedSprite(p.Key, p.Sprite, p.Position));
                    }
                }
            }

            return _items;
        }
    }

    public DmSettings Settings => _host != null ? _host.Server.Settings : _client!.Settings ?? new DmSettings();

    /// <summary>Seconds left of a timed round (0 without a time limit).</summary>
    public double TimeLeft => _host != null ? _host.Server.TimeLeft : _client!.TimeLeft;

    /// <summary>Why the round ended (the end-of-round screen is up); null while it runs.</summary>
    public string? RoundOver { get; private set; }

    /// <summary>The class the server has this machine's player in (worn since the last spawn).</summary>
    public DmClass LocalClass => Players.TryGetValue(LocalId, out var me) ? me.Class : DmClass.Human;

    /// <summary>The HUMAN's motion detector reads (fly_camera.gd detector_active): record 13 in
    /// hand, on foot, alive, and the soldier in the arena.</summary>
    public bool DetectorActive => Spawned && _driving == 0 && _session.Soldier.Alive && DeathView == 0f
        && _session.Weapons.Inventory.Primary == WeaponTable.MotionDetector;

    /// <summary>The motion detector's plot of the others around this machine's player
    /// (<see cref="MotionDetector.Scan"/>); read while <see cref="DetectorActive"/>.</summary>
    public ScannerPlot Scanner() => MotionDetector.Scan(_avatars.Values, _session.Player.Position, _session.Player.Yaw);

    /// <summary>The TERMINATOR's machine vision is on (SKYNET.EXE v1.01 0x134a39, every frame): a
    /// machine avatar in the arena, alive (not [0x30a50] bit 0x400000) and the head not under water
    /// (bit 0x4000000: the water's palette wins). On foot or driving alike.</summary>
    public bool MachineVision => LocalClass == DmClass.Terminator && Spawned && _session.Soldier.Alive && DeathView == 0f
        && !_session.Breath.IsUnder;

    /// <summary>TERMINATOR VISION is chosen (the network screens' row, <c>--vision=pointcloud</c>, the
    /// console's <c>vision</c>): this machine's own, never sent.</summary>
    public bool PointCloudVision { get; set; }

    /// <summary>The main view is drawn as TERMINATOR VISION's point cloud: chosen and the machine vision
    /// on (<see cref="MachineVision"/>: a TERMINATOR, alive, the head above water). The marks and the
    /// machine palette (<see cref="DmHud"/>) go over it as over the plain view.</summary>
    public bool PointCloudView => PointCloudVision && MachineVision;

    /// <summary>Seconds this game has run (the HUD's scrolling and flashing keep to it).</summary>
    public double Clock => _clock;

    /// <summary>The local bar's top: DOS's 150 points for every class (<see cref="DmProtocol.DosHealth"/>).</summary>
    public float MaxHealth => DmProtocol.DosHealth;

    /// <summary>The scoreboard stays up (the console's <c>scores</c>; else it shows while TAB is held).</summary>
    public bool ShowScores { get; set; }

    /// <summary>The chat line is being typed (T or ENTER opened it): the keys are the line's.</summary>
    public bool ChatOpen { get; private set; }

    /// <summary>The chat line as typed so far.</summary>
    public string ChatText { get; private set; } = "";

    /// <summary>The keys do nothing to the soldier: not spawned yet, typing, or the round over.</summary>
    public bool InputBlocked => _replay != null ? Camera != DemoCamera.Free : !Spawned || ChatOpen || RoundOver != null;

    /// <summary>The chat log, oldest first (aged in <see cref="Frame"/>). The network events go to the
    /// status lines (<see cref="Status"/>), as DOS prints them.</summary>
    public IReadOnlyList<Line> Chat => _chat;

    /// <summary>The centre line ("KILLED BY …", "NEW ROUND", "NEXT LIFE: …"); null when none.</summary>
    public string? Centre => _centre.Text.Length > 0 && _clock < _centre.Until ? _centre.Text : null;

    /// <summary>The red wash of a hit (0..1), fading at 2 a second as dm_game.gd's.</summary>
    public float HitFlash { get; private set; }

    /// <summary>The death view's progress (0 alive, up to 1 lying on the floor), eased as
    /// fly_camera.gd's ease(t / DEATH_FALL, 0.45).</summary>
    public float DeathView => _diedAt is { } at && OwnView ? 1f - MathF.Pow(1f - Math.Clamp((float)(_clock - at) / DeathFall, 0f, 1f), 1f / 0.45f) : 0f;

    /// <summary>The scoreboard's rows in DOS's order for the round's goal (<see cref="DmServer.Rank"/>).</summary>
    public List<DmPlayer> Scoreboard() => DmServer.Rank(Players.Values, Settings.Goal);

    /// <summary>The death view over the soldier's own: the eye above the feet, the pitch and the roll.</summary>
    public (float Eye, float Pitch, float Roll) DeathPose(float eye, float pitch)
    {
        var k = DeathView;
        return (float.Lerp(eye, DeathEye, k), float.Lerp(pitch, DeathPitch, k), DeathRoll * k);
    }

    /// <summary>A line for the HUD's status line ("KILLED BY …") in its palette colour (DOS's
    /// network events are 0x0C, <see cref="Hud.StatusQueue.NetworkColour"/>).</summary>
    public event Action<string, byte>? Status;

    /// <summary>The game is over for this machine (the server went, or the connection failed).</summary>
    public event Action<string>? Ended;

    // ---- start ----

    /// <summary>Hosts <paramref name="o"/>'s arena: the server on its port, the session on the map,
    /// the spawns cleared on the host's level, the host's first spawn. Null (and why in
    /// <paramref name="error"/>) when the map is missing or the port is taken.</summary>
    /// <param name="transport">The server's transport (tests: a loopback one); null listens on UDP as
    /// <paramref name="o"/> says.</param>
    public static DmGame? StartHost(GameSession session, DmRunOptions o, out string? error, INetTransport? transport = null)
    {
        error = null;
        var mapNumber = o.HostMap ?? 605;
        var level = NetLevelList.For(NetLevelList.Load(session.Data), mapNumber);
        if (DmArena.Load(session.Data, mapNumber) is not { } arena)
        {
            error = $"{level.MapName} is not in the data";
            return null;
        }

        var settings = o.SettingsFor(level);
        var cap = DmRunOptions.Cap(settings);
        if ((transport ?? UdpTransport.Listen(o.Port, DmProtocol.AppKey, cap, o.Bind)) is not { } listening)
        {
            error = $"cannot host on port {o.Port} (taken?)";
            return null;
        }

        var host = new DmHost(new NetEndpoint(listening), new DmServer(settings), o.Name, o.Class, o.Avatar) { Policy = o.HostPolicy() };
        Log.Info("Net", $"classes {DmProtocol.RulesName(settings.Classes)}, password {(host.Policy.PasswordKey != null ? "on" : "off")}");
        var game = new DmGame(session, host, null);
        if (!session.StartDeathmatch(mapNumber))
        {
            game.Dispose();
            error = $"{level.MapName} does not load";
            return null;
        }

        game._levelUp = true;
        // Step 8: the map's own state is the server's (the host's level, in the server role); a
        // broken crate's item becomes a server pickup. NEW ROUND puts the level back as it starts
        // now (its loops too).
        var startLoops = new Dictionary<int, (int Id, Vector3 At)>(session.Loops);
        var levelServer = new DmLevelServer(session.Behaviour!, snap => session.ResetLevel(snap, startLoops));
        host.Level = levelServer;
        session.DeathmatchDrop = (sprite, at) => host.Server.Drop(sprite, at);
        OwnLevelBlasts(session, levelServer, host.Server);
        // The campaign's clear-spawn nudge on the host's own level: a spawn marker inside a prop
        // moves to the nearest free spot (the Godot port's _find_clear_spawn on every spawn).
        var cleared = arena.Spawns.Select(sp => sp with { Feet = PlayerStart.Clear(session.World, session.Player.Body, sp.Feet) }).ToList();
        host.Server.SetArena(new DmArena
        {
            Map = arena.Map, Spawns = cleared, ItemSpots = arena.ItemSpots,
            JeepSpots = [.. arena.JeepSpots.Select(Floor)], HkSpots = [.. arena.HkSpots.Select(Floor)],
        });
        Log.Info("Net", $"arena {level.MapName} '{level.Name}': {cleared.Count} spawns ({cleared.Zip(arena.Spawns).Count(p => p.First.Feet != p.Second.Feet)} nudged clear), " +
            $"{arena.ItemSpots.Count} item spots, {host.Server.Pickups.Count} pickups placed, " +
            $"{host.Server.Vehicles.Values.Count(v => v.Kind == VehicleMode.Jeep)} jeeps and {host.Server.Vehicles.Values.Count(v => v.Kind == VehicleMode.Hk)} HKs parked");
        Log.Info("Net", $"listening on port {host.Port}");
        if (o.Discovery && !host.StartDiscovery(DmProtocol.DiscoveryPort, o.Bind))
        {
            Log.Warn("Net", $"discovery port {DmProtocol.DiscoveryPort} busy: this game will not show in LAN lists");
        }

        if (o.Public)
        {
            host.Log += line => Log.Info("Net", line);
            if (!host.StartMaster(o.Master ?? "", DmProtocol.GameId(session.Data.Game), o.Bind))
            {
                Log.Warn("Net", $"PUBLIC: the master server's address '{o.Master}' cannot be read: this game is not listed");
            }
        }

        host.ReportLevelReady();
        foreach (var v in host.Server.Vehicles.Values)
        {
            Log.Info("Net", string.Create(CultureInfo.InvariantCulture, $"vehicle {v.Key} ({v.Kind}) at {v.Position.X:0},{v.Position.Y:0},{v.Position.Z:0}"));
        }

        if (o.Bots > 0)
        {
            DmBots.Start(host, session.World, session.Weapons.Table, seed: null, nav: DmNav.For(session), rides: DmNav.Rides(session, host.Server.Vehicles.Values));
        }

        return game;

        // DOS drops each parked vehicle on the floor under its marker (0x131002): a deck or a bridge
        // over the terrain holds it.
        Vector3 Floor(Vector3 marker) =>
            session.World.Raycast(marker + new Vector3(0f, 40f, 0f), -Vector3.UnitY, 4000f, out var hit) ? hit.Point : marker;
    }

    /// <summary>Joins the game at <paramref name="o"/>'s address; the arena loads when the welcome
    /// comes. Null (and why) when the address does not resolve.</summary>
    public static DmGame? StartClient(GameSession session, DmRunOptions o, out string? error, INetTransport? transport = null)
    {
        error = null;
        if (transport != null)
        {
            return new DmGame(session, null, new DmClient(new NetEndpoint(transport), o.Name, o.Class, o.Password, o.Avatar));
        }

        if (o.JoinHost == null)
        {
            error = "--find works headless only (--headless or --smoke)";
            return null;
        }

        if (DmHeadless.ClientTransport(o) is not { } joined)
        {
            error = $"cannot connect to {o.JoinHost}:{o.Port}";
            return null;
        }

        var client = new DmClient(new NetEndpoint(joined), o.Name, o.Class, o.Password, o.Avatar);
        Log.Info("Net", $"connecting to {o.JoinHost}:{o.Port} as {client.Name}{(o.Introduce ? $" through the master server {o.Master}" : "")}");
        return new DmGame(session, null, client);
    }

    // ---- every frame ----

    /// <summary>One frame (also under a menu: the wire and the server's clock go on): the network,
    /// the others' bodies, the own pose.</summary>
    public void Frame(float seconds)
    {
        _clock += seconds;
        if (_recorder != null)
        {
            _recorder.Clock = _clock;
        }

        _replay?.Advance(seconds);
        if (_host != null)
        {
            _host.Update(seconds);
        }
        else
        {
            _client!.Update(seconds);
        }

        if (_replay != null)
        {
            ReplayLocal();
        }

        foreach (var a in _avatars.Values)
        {
            a.Step(seconds);
        }

        foreach (var v in Vehicles.Values)
        {
            _seats.TryAdd(v.Key, v.Driver);
        }

        Engines();
        _chat.RemoveAll(l => _clock >= l.Until);
        HitFlash = MathF.Max(0f, HitFlash - seconds * 2f);
        _firing = MathF.Max(0f, _firing - seconds);
        Sweep(seconds);
        if (_replay != null)
        {
            ReplayCamera(seconds);
            return;
        }

        if (!Spawned)
        {
            RecordKeyframe();
            return;
        }

        AskForItems();
        var p = _session.Player;
        var flags = DmProtocol.WeaponBits(WeaponInHand);
        if (new Vector2(p.Velocity.X, p.Velocity.Z).Length() > 1f)
        {
            flags |= DmProtocol.FlagMoving;
        }

        if (_firing > 0f)
        {
            flags |= DmProtocol.FlagFiring;
        }

        if (DetectorActive)
        {
            flags |= DmProtocol.FlagScanner;
        }

        if (!_session.Soldier.Alive)
        {
            flags |= DmProtocol.FlagDead;
        }

        // In a vehicle the heading rides along (the jeep's view is its turret).
        var carYaw = _driving != 0 && _rig is { Riding: true } r ? r.Controller.CarYaw : p.Yaw;
        if (_host != null)
        {
            _host.SendPose(p.Position, p.Yaw, p.Pitch, flags, seconds, carYaw);
        }
        else
        {
            _client!.SendPose(p.Position, p.Yaw, p.Pitch, flags, seconds, carYaw);
        }

        if (_recorder is { PoseDue: true })
        {
            _recorder.Local(LocalPose(flags, carYaw));
        }

        RecordKeyframe();
    }

    /// <summary>The scanner in hand beeps once a sweep (MOTLOOP2.RAW, FUN_0011ff00), and so do the
    /// others' where they stand (DOS sends it, FUN_00122e41; here their pose's
    /// <see cref="DmProtocol.FlagScanner"/>).</summary>
    private void Sweep(float seconds)
    {
        foreach (var a in _beeps.Tick(_avatars.Values, seconds))
        {
            _session.Sounds?.Play3D(MotionDetector.SweepSound, a.Centre);
        }

        if (!DetectorActive)
        {
            _sweep.Reset();
            return;
        }

        if (_sweep.Tick(seconds))
        {
            _session.Sounds?.Play2D(MotionDetector.SweepSound);
        }
    }

    /// <summary>Queues the others' bodies on <paramref name="level"/> (the level being shown).</summary>
    public void Draw(Level level, RetroEng.Render.Renderer renderer, long nowMs)
    {
        Capture(level, renderer, _frame);
        _frame.View!.Draw(_frame, nowMs);
    }

    /// <summary>The others' bodies on <paramref name="level"/> as they are now, and the level's avatar
    /// drawer, for a draw on another thread (<see cref="AvatarRenderer.Draw(DmFrame, long)"/>).</summary>
    public void Capture(Level level, RetroEng.Render.Renderer renderer, DmFrame frame)
    {
        frame.View = View(level, renderer);
        AvatarRenderer.Capture(_replay == null ? _avatars.Values
            : _avatars.Values.Where(a => a.Id != Hidden).Concat(_self != null && !OwnView ? new[] { _self } : Array.Empty<DmAvatar>()),
            Vehicles.Values, frame);
        CaptureNav(frame);
    }

    /// <summary>The bots' mesh and paths into <paramref name="frame"/> while <c>navmesh on</c> (the host's bots only).</summary>
    private void CaptureNav(DmFrame frame)
    {
        frame.NavPaths.Clear();
        frame.NavMesh = null;
        if (!_showNav || _host?.Bots is not { Nav: { } nav } bots)
        {
            return;
        }

        if (!ReferenceEquals(_navOutline.Mesh, nav))
        {
            // The jeep's mesh (bots driving) over the soldier's.
            RetroEng.Render.Vertex[] jeep = bots.Rides.Jeep is { } j ? DmNavLines.Lifted(j, DmNavLines.JeepLift) : [];
            _navOutline = (nav, [.. DmNavLines.Outline(nav), .. jeep]);
        }

        frame.NavMesh = _navOutline.Lines;
        DmNavLines.Paths(bots.Brains.Values, frame.NavPaths);
    }

    /// <summary>Builds <paramref name="level"/>'s avatar drawer and every mesh it can draw (<see cref="AvatarRenderer.Prepare"/>), at the map's load;
    /// a mesh is a step of <paramref name="progress"/>.</summary>
    public void Prepare(Level level, RetroEng.Render.Renderer renderer, LoadProgress? progress = null)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var view = View(level, renderer);
        view.Prepare(Players.Values.Select(p => DmAvatarTable.Get(DmAvatarTable.Resolve(p.Avatar, p.Class))).Distinct().ToArray(), progress);
        Log.Info("Net", $"avatar meshes: {view.MeshesBuilt} in {clock.ElapsedMilliseconds} ms");
    }

    private AvatarRenderer View(Level level, RetroEng.Render.Renderer renderer)
    {
        if (_view == null || !ReferenceEquals(_view.Level, level))
        {
            _view = new AvatarRenderer(renderer, _models, new TextureStore(renderer, _session.Data), level);
        }

        return _view;
    }

    /// <summary>The avatar meshes built so far (the warm-up's list).</summary>
    public IEnumerable<RetroEng.Render.Material> Materials() => _view?.Materials() ?? [];

    // The id of the nearest living body to the soldier, one with no wall between first; int.MinValue: none.
    private int Nearest()
    {
        var eye = _session.Player.EyePosition;
        return _avatars.Where(kv => kv.Value.Alive)
            .OrderBy(kv => LineClear(_session.World, eye, kv.Value.Centre, int.MinValue) ? 0 : 1)
            .ThenBy(kv => Vector3.DistanceSquared(kv.Value.Centre, eye))
            .Select(kv => kv.Key).DefaultIfEmpty(int.MinValue).First();
    }

    /// <summary>The console's network commands: the roster, and turning to face a player.</summary>
    public void Register(CommandRegistry commands)
    {
        var c = CultureInfo.InvariantCulture;
        commands.Add("players", "players", _ => Logged(string.Join('\n', Players.Values.OrderBy(p => p.Id).Select(p =>
        {
            var at = p.Id == LocalId ? _session.Player.Position : _avatars.TryGetValue(p.Id, out var a) ? a.Position : p.Position;
            return string.Create(c, $"{p.Id} {p.Name}{(p.Id == LocalId ? " (you)" : "")}: {(p.Alive ? "alive" : "dead")} at {at.X:0},{at.Y:0},{at.Z:0}, " +
                $"{p.Frags} kills, {p.Deaths} deaths, {p.Points} points");
        }))), "the deathmatch's players");
        commands.Add("face", "face [id]", args =>
        {
            // No id: the nearest living player, one in plain sight first (a scripted shot follows the fight).
            var id = args.Count == 0 ? Nearest() : args.Int(0, int.MinValue, int.MaxValue);
            if (!_avatars.TryGetValue(id, out var a) || !a.Visible)
            {
                return Logged($"No player {id} in sight");
            }

            var p = _session.Player;
            var to = a.Centre - p.EyePosition;
            p.Yaw = MathF.Atan2(-to.X, -to.Z);
            p.Pitch = MathF.Asin(Math.Clamp(to.Y / MathF.Max(to.Length(), 1f), -1f, 1f));
            return Logged(string.Create(c, $"facing {a.Name} at {a.Position.X:0},{a.Position.Y:0},{a.Position.Z:0}, {to.Length():0} units away"));
        }, "turns the view to another player's body (no id: the nearest living one, in sight first)");
        commands.Add(new Command("class", "class [human|terminator]", args =>
        {
            if (args.OptionalWord(0) is not { } word)
            {
                return $"class: {ClassName(LocalClass)} (human | terminator)";
            }

            var cls = word.StartsWith('t') || word.StartsWith('T') ? DmClass.Terminator
                : word.StartsWith('h') || word.StartsWith('H') ? DmClass.Human : throw new CommandUsageException();
            SetClass(cls);
            return $"{ClassName(cls)} from the next spawn";
        })
        { Help = "HUMAN (motion detector) or TERMINATOR (machine vision, no detector; slower under BALANCED) from the next spawn", Arguments = i => i == 0 ? ["human", "terminator"] : [] });
        commands.Add(new Command("vision", "vision [normal|pointcloud]", args =>
        {
            if (args.OptionalWord(0) is { } word)
            {
                PointCloudVision = word.StartsWith('p') || word.StartsWith('P') ? true
                    : word.StartsWith('n') || word.StartsWith('N') ? false : throw new CommandUsageException();
            }

            return $"terminator vision: {(PointCloudVision ? "pointcloud" : "normal")} (normal | pointcloud; this match)";
        })
        { Help = "TERMINATOR VISION: a TERMINATOR's machine vision as the plain view or as a point cloud (this machine only)", Arguments = i => i == 0 ? ["normal", "pointcloud"] : [] });
        commands.Add(new Command("navmesh", "navmesh [on|off]", args =>
        {
            if (args.OptionalWord(0) is { } word)
            {
                _showNav = word.Equals("on", StringComparison.OrdinalIgnoreCase) ? true
                    : word.Equals("off", StringComparison.OrdinalIgnoreCase) ? false : throw new CommandUsageException();
            }

            var bots = _host?.Bots;
            var mesh = bots == null ? "no bots here" : bots.Nav == null ? "the bots have no mesh" : bots.Rides.Jeep != null ? "the bots' and the jeep's meshes and paths" : "the bots' mesh and paths";
            return $"navmesh: {(_showNav ? "on" : "off")} ({mesh})";
        })
        { Help = "draws the bots' nav mesh and their paths (the host's bots)", Arguments = i => i == 0 ? ["on", "off"] : [] });
        commands.Add("say", "say <text>", args =>
        {
            SendChat(args.Rest().Length != 0 ? args.Rest() : throw new CommandUsageException());
            return "";
        }, "a chat line to everybody (T or ENTER in the game)");
        commands.Add("newround", "newround", _ => Restart() ? "New round" : "Only the host starts a new round",
            "the host starts the round again (ENTER on the end-of-round screen)");
        commands.Add(new Command("scores", "scores [on|off]", args =>
        {
            ShowScores = args.Switch(0, ShowScores);
            return ShowScores ? "scoreboard shown" : "scoreboard on TAB";
        })
        { Help = "keeps the scoreboard up (else it shows while TAB is held)", Arguments = i => i == 0 ? ["on", "off"] : [] });
        commands.Add("level", "level", _ =>
        {
            if (_session.Behaviour is not { } b)
            {
                return Logged("no arena loaded");
            }

            var role = _host != null ? "server" : _level is { InStep: false } ? "client (other map data: not in step)" : "client";
            var movers = b.Movers.Order().Select(m =>
            {
                var (progress, span, _) = b.MoverProgress(m);
                return string.Create(c, $"@{m:x5} {progress:0}/{span:0}{(b.MoverRunning(m) ? " moving" : "")}{(b.Enabled(m) ? " on" : "")}");
            });
            var broken = b.Records.Count(b.Spent);
            return Logged(string.Create(c, $"level ({role}): {broken} records broken; movers: {string.Join(", ", movers.DefaultIfEmpty("none"))}"));
        }, "the arena's own state as this machine has it (step 8)");
        commands.Add("vehicles", "vehicles", _ => Logged(string.Join('\n', Vehicles.Values.OrderBy(v => v.Key).Select(v =>
            string.Create(c, $"{v.Key}: {(v.Kind == VehicleMode.Hk ? "HK" : "jeep")} at {v.Position.X:0},{v.Position.Y:0},{v.Position.Z:0} heading {v.Yaw * 180f / MathF.PI:0}, hull {v.Hull:0}" +
                $"{(v.Wrecked ? " (wreck)" : v.Driver != 0 ? $" driven by {NameOf(v.Driver)}" : "")}{(v.Key == _driving ? " (you)" : "")}"))
            .DefaultIfEmpty("no vehicles"))), "the arena's jeeps and HKs as the server has them (step 9)");
        commands.Add("pickups", "pickups", _ => Logged(string.Join('\n', Pickups.Values.OrderBy(p => p.Key).Select(p =>
            string.Create(c, $"{p.Key}: {p.Sprite >> 7}/{p.Sprite & 0x7F} at {p.Position.X:0},{p.Position.Y:0},{p.Position.Z:0}{(p.Taken ? " (taken)" : "")}"))
            .DefaultIfEmpty("no items"))), "the arena's items as the server lists them");

        static string Logged(string text)
        {
            Log.Info("Net", text);
            return text;
        }
    }

    public void Dispose()
    {
        foreach (var e in _engines.Values)
        {
            e.Loop.Stop();
        }

        _engines.Clear();
        _session.Weapons.Shot -= _onShot;
        _session.Soldier.Remote = null;
        _session.DeathmatchUse = null;
        _session.DeathmatchBlock = null;
        if (_rig != null)
        {
            _rig.Hull.Remote = null;
            if (_rig.Riding)
            {
                _rig.Board(VehicleMode.Foot);
            }
        }

        _session.World.Remove(_parked);
        _session.DeathmatchDrop = null;
        _session.LevelBlast = null;
        _session.EndDeathmatch();
        _session.Player.ClassSpeed = 1f;
        _session.Weapons.Inventory.Take(WeaponTable.MotionDetector); // the class's kit stays in the arena
        foreach (var a in _avatars.Values)
        {
            _session.Weapons.Targets.Remove(a);
        }

        _avatars.Clear();
        if (_host != null)
        {
            _host.Shutdown();
            _host.Update(0);
            _host.Dispose();
        }
        else
        {
            _client!.Leave();
            _client.Update(0);
            _client.Dispose();
        }
    }

    // ---- network demos (docs/demos.md "Network demos") ----

    /// <summary>A network demo's camera: a player's own eye, behind a player, or flying free.</summary>
    public enum DemoCamera { FirstPerson, Chase, Free }

    /// <summary>Plays the network demo <paramref name="player"/> feeds: a client on its recorded
    /// messages (nothing sent, nothing simulated but the view), the arena loading on its first keyframe.</summary>
    public static DmGame StartReplay(GameSession session, NetDemoPlayer player)
    {
        var client = new DmClient(new NetEndpoint(player.Transport), "DEMO", DmClass.Human);
        var game = new DmGame(session, null, client, player)
        {
            Follow = player.LocalId,
            Camera = player.LocalId != 0 ? DemoCamera.FirstPerson : DemoCamera.Chase,
        };
        session.Player.Noclip = true;
        return game;
    }

    /// <summary>The demo playing (null in a live game).</summary>
    public NetDemoPlayer? Replay => _replay;

    /// <summary>The recording under way (null when none).</summary>
    public NetDemoRecorder? Recorder => _recorder;

    /// <summary>Playback: how the view follows.</summary>
    public DemoCamera Camera { get; set; }

    /// <summary>Playback: the player the camera follows (the recording player's id: its own recorded view).</summary>
    public int Follow { get; private set; }

    /// <summary>The view is this machine's own player's (always in a live game).</summary>
    public bool OwnView => _replay == null || (Camera == DemoCamera.FirstPerson && Follow == LocalId && LocalId != 0);

    /// <summary>Playback: the body not drawn because the camera is in its head.</summary>
    private int Hidden => _replay != null && Camera == DemoCamera.FirstPerson && Follow != LocalId ? Follow : int.MinValue;

    /// <summary>Playback: the next (or, <paramref name="step"/> -1, the previous) player to follow: the
    /// recording player and everybody in the arena, in id order. Returns the name.</summary>
    public string FollowNext(int step)
    {
        var ids = Players.Keys.Where(id => id == LocalId ? LocalId != 0 : _avatars.ContainsKey(id)).Order().ToList();
        if (ids.Count == 0)
        {
            return "nobody to follow";
        }

        var at = ids.IndexOf(Follow);
        Follow = ids[((at < 0 ? (step > 0 ? -1 : 0) : at) + step + ids.Count) % ids.Count];
        if (Camera == DemoCamera.Free)
        {
            Camera = DemoCamera.Chase;
        }

        return NameOf(Follow);
    }

    /// <summary>Playback: follows player <paramref name="id"/>; false when nobody of that id plays.</summary>
    public bool FollowPlayer(int id)
    {
        if (!(id == LocalId && LocalId != 0) && !_avatars.ContainsKey(id))
        {
            return false;
        }

        Follow = id;
        return true;
    }

    /// <summary>Starts recording a network demo to <paramref name="path"/>; null, or why not. It
    /// begins with a keyframe as soon as the arena is up.</summary>
    public string? StartRecording(string path, GameData? data)
    {
        if (_replay != null)
        {
            return "A network demo is playing";
        }

        if (_recorder != null)
        {
            return $"Already recording {_recorder.Path}";
        }

        _recorder = new NetDemoRecorder(NetDemo.Create(data, _host == null ? NetDemoRole.Client : NetDemoRole.Host, LocalId, NameOf(LocalId), Settings), path)
        {
            Clock = _clock,
        };
        return null;
    }

    /// <summary>Ends the recording (the caller writes it); null when none ran.</summary>
    public NetDemoRecorder? StopRecording()
    {
        var r = _recorder;
        _recorder = null;
        return r;
    }

    /// <summary>A keyframe when one is due (the first as soon as the arena is up).</summary>
    private void RecordKeyframe()
    {
        if (_recorder is not { KeyframeDue: true } r || !_levelUp)
        {
            return;
        }

        if (!r.Started)
        {
            r.Demo.SetField(NetDemo.KeyLocalId, LocalId.ToString(CultureInfo.InvariantCulture));
            r.Demo.SetField(NetDemo.KeyLocalName, NameOf(LocalId));
            NetDemo.SetRules(r.Demo, Settings);
            Log.Info("Demo", $"recording {r.Path}: player {LocalId} on MAP.{Settings.Map:000}");
        }

        r.Keyframe(Keyframe());
    }

    /// <summary>What a client joining now as this machine's player would be told, and the player's own state.</summary>
    private byte[] Keyframe()
    {
        var running = _host?.Server.MatchRunning ?? _client!.MatchRunning;
        var overlay = _host != null ? _host.Level?.Overlay() : _level?.Overlay();
        var soldier = _session.Soldier;
        var p = _session.Player;
        var state = NetDemoLocal.WriteState(_dw, Spawned, soldier.Health, soldier.Armour, _diedAt != null, RoundOver);
        var carYaw = _driving != 0 && _rig is { Riding: true } r ? r.Controller.CarYaw : p.Yaw;
        var pose = Spawned ? LocalPose(soldier.Alive ? 0 : DmProtocol.FlagDead, carYaw) : null;
        return NetDemo.Keyframe(LocalId, Settings, Players.Values, Pickups.Values, TimeLeft, running, Vehicles.Values, PoseOf, overlay, RoundOver, state, pose);

        (Vector3, float, float, int, float) PoseOf(DmPlayer pl)
        {
            var flags = _avatars.TryGetValue(pl.Id, out var a) ? a.Flags & ~DmProtocol.FlagFiring : 0;
            var car = Vehicles.Values.FirstOrDefault(v => v.Driver == pl.Id) is { } v ? v.Yaw : pl.Yaw;
            return (pl.Position, pl.Yaw, pl.Pitch, flags, car);
        }
    }

    private byte[] LocalPose(int flags, float carYaw)
    {
        var p = _session.Player;
        return NetDemoLocal.WritePose(_dw, p.Position, p.Yaw, p.Pitch, p.Eye, flags, carYaw, WeaponInHand);
    }

    /// <summary>Playback: the own records that came due (after this step's messages).</summary>
    private void ReplayLocal()
    {
        foreach (var d in _replay!.DueLocal)
        {
            var r = new NetReader(d.AsSpan(1));
            switch (d[0])
            {
                case NetDemoLocal.State:
                {
                    var spawned = r.Bool();
                    var health = r.F32();
                    var armour = r.F32();
                    var dead = r.Bool();
                    Spawned = spawned;
                    (_session.Soldier.Health, _session.Soldier.Armour) = (dead ? 0f : health, armour);
                    _diedAt = dead ? _clock - DeathFall : null;
                    break;
                }
                case NetDemoLocal.Fire:
                {
                    var weapon = r.I32();
                    var from = r.Vec3();
                    var dir = r.Vec3();
                    _firing = FiringHold;
                    _self?.Fired();
                    _session.Weapons.RemoteFire(weapon, from, dir, _self);
                    break;
                }
            }
        }

        _replay.DueLocal.Clear();
    }

    /// <summary>Playback: the recording player's body and weapon from its poses, then the camera (the
    /// session's player is the camera, flying with no collision).</summary>
    private void ReplayCamera(float seconds)
    {
        var p = _session.Player;
        p.Noclip = true;
        var pose = LocalId != 0 && Spawned ? _replay!.PoseAt(_replay.Time) : null;
        if (pose is { } own)
        {
            if (_session.Soldier.Alive)
            {
                _self ??= new DmAvatar(LocalId, NameOf(LocalId), LocalClass);
                _self.Class = LocalClass;
                _self.Avatar = Players.TryGetValue(LocalId, out var me) ? me.Avatar : DmAvatarTable.ForClass(LocalClass);
                if (!_self.Alive)
                {
                    _self.SpawnAt(own.Feet, own.Yaw);
                }

                _self.ApplyPose(own.Feet, own.Yaw, own.Pitch, own.Flags, own.CarYaw);
            }
            else
            {
                _self?.Die();
            }

            var inv = _session.Weapons.Inventory;
            if (own.Weapon > 0 && own.Weapon < _session.Weapons.Table.Weapons.Length && own.Weapon != inv.Primary && _driving == 0)
            {
                inv.Grant(own.Weapon);
                inv.Select(own.Weapon);
            }
        }

        _self?.Step(seconds);
        if (Camera == DemoCamera.Free)
        {
            return;
        }

        (Vector3 Feet, float Yaw, float Pitch, float Eye)? target = null;
        if (Follow == LocalId && LocalId != 0)
        {
            if (pose is { } o)
            {
                target = (o.Feet, o.Yaw, o.Pitch, o.Eye);
            }
        }
        else if (_avatars.TryGetValue(Follow, out var a) && a.Visible)
        {
            target = (a.Position, a.Yaw, a.Pitch, a.Vehicle == VehicleMode.Foot ? PlayerController.EyeHeight : DmAvatar.VehicleRadius(a.Vehicle) * 1.5f);
        }

        if (target is not { } t)
        {
            return;
        }

        if (Camera == DemoCamera.FirstPerson)
        {
            p.Place(t.Feet, t.Yaw, t.Pitch, t.Eye);
            return;
        }

        // Behind and above the body, pulled in where a wall stands between.
        const float ChasePitch = -0.35f, ChaseDistance = 220f;
        var head = t.Feet + new Vector3(0f, t.Eye, 0f);
        var back = -PlayerController.Direction(t.Yaw, ChasePitch);
        var reach = ChaseDistance;
        if (_session.World.Raycast(head, back, ChaseDistance, out var hit))
        {
            reach = MathF.Max(hit.Time - 12f, 20f);
        }

        var camera = head + back * reach;
        p.Place(camera - new Vector3(0f, t.Eye, 0f), t.Yaw, ChasePitch, t.Eye);
    }

    // ---- the server's word ----

    private void OnWelcome()
    {
        if (_replay != null && _levelUp && _client!.Settings!.Map == _session.MapNumber)
        {
            // A network demo's keyframe on the arena already up (a seek): the view starts afresh from
            // the welcome; the keyframe's level state puts the map back and lays the overlay down.
            StepOut();
            foreach (var e in _engines.Values)
            {
                e.Loop.Stop();
            }

            _engines.Clear();
            foreach (var a in _avatars.Values)
            {
                _session.Weapons.Targets.Remove(a);
            }

            _avatars.Clear();
            _seats.Clear();
            _self = null;
            Spawned = false;
            _diedAt = null;
            RoundOver = null;
            _itemsDirty = true;
            _centre = ("", 0);
            foreach (var p in _client.Players.Values)
            {
                if (p.Id != _client.LocalId)
                {
                    Ensure(p.Id).SetAlive(p.Alive, p.Position, p.Yaw);
                }
            }

            return;
        }

        if (_levelUp)
        {
            // Welcomed again: a dedicated server's map rotation. What belonged to the old arena goes
            // (the seat, the bodies, the engines, the level mirror); the new one loads as on a join.
            StepOut();
            foreach (var e in _engines.Values)
            {
                e.Loop.Stop();
            }

            _engines.Clear();
            foreach (var a in _avatars.Values)
            {
                _session.Weapons.Targets.Remove(a);
            }

            _avatars.Clear();
            _seats.Clear();
            _asked.Clear();
            _level = null;
            _levelUp = false;
            Spawned = false;
            _diedAt = null;
            RoundOver = null;
            _itemsDirty = true;
            ShowCentre("NEXT MAP", 3.0);
        }

        var s = _client!.Settings!;
        Log.Info("Net", $"welcome: player {_client.LocalId}, MAP.{s.Map:000} '{s.Arena}', {_client.Players.Count} players");
        if (!_session.StartDeathmatch(s.Map))
        {
            Log.Error("Net", $"MAP.{s.Map:000} is not in this game's data");
            _client.Leave();
            Ended?.Invoke($"MAP.{s.Map:000} IS NOT IN THIS GAME'S DATA");
            return;
        }

        _levelUp = true;
        // Step 8: the level shows the server's state and sends the use key and the shots on records.
        var startLoops = new Dictionary<int, (int Id, Vector3 At)>(_session.Loops);
        _level = new DmLevelClient(_session.Behaviour!, i => _client.SendLevelIntent(i), snap => _session.ResetLevel(snap, startLoops));
        // Level blasts are the server's: here they are only seen and heard, they strike nothing.
        _session.LevelBlast = (_, _, _) => { };
        foreach (var early in _levelEarly)
        {
            OnLevelState(early);
        }

        _levelEarly.Clear();
        foreach (var p in _client.Players.Values)
        {
            if (p.Id != _client.LocalId)
            {
                Ensure(p.Id).SetAlive(p.Alive, p.Position, p.Yaw);
            }
        }

        _client.ReportLevelReady();
    }

    /// <summary>Client: the server's level state, laid over the arena (held until it is loaded).</summary>
    private void OnLevelState(LevelUpdate u)
    {
        if (_level == null)
        {
            _levelEarly.Add(u);
            return;
        }

        var inStep = _level.InStep;
        if (!_level.Apply(u) && inStep)
        {
            Log.Warn("Net", "the server's level is of other map data: its state is not applied");
            ShowCentre("MAP DATA DIFFERS FROM THE SERVER", 6.0);
        }
    }

    /// <summary>The arena's level as the wire sees it: the server's (host) or the mirror (client).</summary>
    public DmLevelServer? LevelServer => _host?.Level;

    public DmLevelClient? LevelClient => _level;

    private DmAvatar Ensure(int id)
    {
        if (_avatars.TryGetValue(id, out var a) || id == LocalId)
        {
            return a ?? new DmAvatar(id, "", DmClass.Human);
        }

        var p = Players.GetValueOrDefault(id);
        a = new DmAvatar(id, p?.Name ?? "", p?.Class ?? DmClass.Human) { Hit = OnAvatarHit };
        a.Avatar = p?.Avatar ?? DmAvatarTable.ForClass(a.Class);
        _avatars[id] = a;
        _session.Weapons.Targets.Add(a);
        return a;
    }

    private void Remove(int id)
    {
        if (_avatars.Remove(id, out var a))
        {
            _session.Weapons.Targets.Remove(a);
        }
    }

    private void Joined(int id)
    {
        Ensure(id);
        if (id != LocalId)
        {
            NetLine("NewPlayer", id);
        }
    }

    private void Left(int id)
    {
        NetLine("PlayerLeft", id);
        Remove(id);
    }

    /// <summary>
    /// A network event on the top-left status lines as SKYNET.EXE v1.01 prints it: the STRINGS.PRS
    /// line (FUN_001442e4 → FUN_0012daa8, #P the player concerned, #K the killer) queued in colour
    /// 0x0C (FUN_0012f8f7): NewPlayer 0x1191e0, PlayerLeft 0x118c7c, PlayerRestart 0x118f74,
    /// PlayerDied1-8 / PlayerSuicide1-4 (0x1192c4), LocalDied 0x132279, LocalSuicide 0x122c72.
    /// </summary>
    private void NetLine(string key, int player, int killer = 0)
    {
        var text = _session.Text(key, NetLineFallback(key)).Replace("#P", NameOf(player), StringComparison.Ordinal)
            .Replace("#K", NameOf(killer), StringComparison.Ordinal);
        Log.Info("Net", text);
        Status?.Invoke(text, Hud.StatusQueue.NetworkColour);
    }

    /// <summary>The STRINGS.PRS [english] texts, for data without the file.</summary>
    public static string NetLineFallback(string key) => key switch
    {
        "NewPlayer" => "#P HAS ENTERED THE GAME.",
        "PlayerLeft" => "#P LEFT THE GAME.",
        "PlayerRestart" => "#P RETURNS FROM DEATH.",
        "PlayerDied1" => "#K KILLS #P.",
        "PlayerDied2" => "#K BLOWS AWAY #P.",
        "PlayerDied3" => "#K PUTS #P AWAY.",
        "PlayerDied4" => "#K SAYS GOODBYE TO #P.",
        "PlayerDied5" => "#K OBLITERATES #P.",
        "PlayerDied6" => "#K PUTS AN END TO #P.",
        "PlayerDied7" => "#K TERMINATES #P.",
        "PlayerDied8" => "#K DOWNS #P.",
        "PlayerSuicide1" => "#P COMMITTED SUICIDE.",
        "PlayerSuicide2" => "#P DECIDES LIFE IS DULL.",
        "PlayerSuicide3" => "#P TAKES A WRONG STEP.",
        "PlayerSuicide4" => "#P GOES INTO THE GREAT BEYOND.",
        "LocalDied" => "#K BLOWS YOU AWAY.",
        "LocalSuicide" => "YOU COMMITTED SUICIDE.",
        _ => "",
    };

    private void OnPose(int id, Vector3 pos, float yaw, float pitch, int flags, float carYaw)
    {
        if (id == LocalId || !_levelUp)
        {
            return;
        }

        var a = Ensure(id);
        if (!a.Alive && (flags & DmProtocol.FlagDead) == 0 && Players.TryGetValue(id, out var p) && p.Alive)
        {
            a.SpawnAt(pos, yaw); // a joiner's first sight of someone already in the arena
        }

        a.ApplyPose(pos, yaw, pitch, flags, carYaw);
    }

    private void OnFired(int id, int weapon, Vector3 from, Vector3 dir)
    {
        if (id == LocalId || !_levelUp)
        {
            return;
        }

        var a = _avatars.GetValueOrDefault(id);
        a?.Fired();
        _session.Weapons.RemoteFire(weapon, from, dir, a);
    }

    private void OnLocalHealth(float health, float armour, int attacker)
    {
        var soldier = _session.Soldier;
        var before = soldier.Health;
        (soldier.Health, soldier.Armour) = (health, armour);
        Log.Info("Net", string.Create(CultureInfo.InvariantCulture, $"hit by {NameOf(attacker)}: health {health:0}, armour {armour * 100f:0} %"));
        if (health < before && health > 0f)
        {
            _session.Sounds?.Play2D("HIT2.RAW", -3f);
            // DOS's pain grunt (FUN_00122b52), as Soldier.Pained plays it in the campaign.
            _session.Sounds?.PlayDos2D(PlayerSounds.PainGrunt(_session.Cosmetic.Next(16)), SoundBank.DosOverlap.Skip);
            soldier.Wounded?.Invoke(before - health);
            HitFlash = 0.35f;
        }
    }

    private void OnDied(int victim, int killer, int weapon)
    {
        var noKiller = killer == victim || killer == 0 || !Players.ContainsKey(killer);
        var name = weapon == DmProtocol.LevelBlastWeapon ? "a level blast" : weapon >= 0 && weapon < WeaponTable.Names.Count ? WeaponTable.Names[weapon] : "?";
        Log.Info("Net", noKiller ? $"{NameOf(victim)} died ({name})" : $"{NameOf(killer)} killed {NameOf(victim)} ({name})");
        if (victim == LocalId)
        {
            // The victim's own line (LocalDied / LocalSuicide); the others get PlayerDiedN / PlayerSuicideN.
            NetLine(noKiller ? "LocalSuicide" : "LocalDied", victim, killer);
            StepOut(); // the server took the seat with the death
            _session.Soldier.Health = 0f;
            _session.Player.Velocity = Vector3.Zero;
            _session.Sounds?.Play2D("EXPLO2.RAW", -2f);
            _session.Sounds?.PlayDos2D(PlayerSounds.DeathGrunt); // DOS's grunt9 (FUN_00122b52)
            _diedAt ??= _clock;
            ShowCentre(noKiller ? "YOU DIED" : $"KILLED BY {NameOf(killer)}", DmProtocol.RespawnDelay + 1.0);
            return;
        }

        NetLine(noKiller ? $"PlayerSuicide{1 + (_lineRng.Next() & 3)}" : $"PlayerDied{1 + (_lineRng.Next() & 7)}", victim, killer);

        if (_avatars.TryGetValue(victim, out var a) && a.Die())
        {
            var at = a.Position + new Vector3(0f, 50f, 0f);
            _session.Sounds?.Play3D("EXPLO2.RAW", at, -4f);
            _session.Sounds?.PlayDos3D(PlayerSounds.DeathGrunt, at); // DOS sends the victim's grunt9 to the others
            _session.Weapons.Explosion(at, Enemy.DeathEffectBank, 90f);
        }

    }

    private void OnRespawned(int id, DmSpawn sp)
    {
        if (id != LocalId)
        {
            if (_levelUp)
            {
                Ensure(id).SpawnAt(sp.Feet, sp.Yaw);
            }

            // DOS's line when another player's state turns alive again (a new round's respawns excepted).
            if (_clock > _restartedAt && Players.ContainsKey(id))
            {
                NetLine("PlayerRestart", id);
            }

            return;
        }

        StepOut();
        var p = _session.Player;
        p.Spawn(sp.Feet, sp.Yaw);
        p.Pitch = 0f;
        p.Noclip = false;
        p.Velocity = Vector3.Zero;
        var cls = LocalClass;
        (_session.Soldier.Health, _session.Soldier.Armour) = (DmProtocol.DosHealth, 0f);
        p.ClassSpeed = DmProtocol.SpeedFactor(cls, Settings.Classes);
        // dm_game.gd _apply_local_class: a HUMAN carries the motion detector (weapon record 13) from
        // every spawn; a TERMINATOR has none (SKYNET.EXE v1.01 0x134a9c clears record 13's owned bit
        // every frame a machine avatar plays) and sees the others through the machine vision.
        if (cls == DmClass.Human)
        {
            _session.Weapons.Inventory.Grant(WeaponTable.MotionDetector);
        }
        else
        {
            _session.Weapons.Inventory.Take(WeaponTable.MotionDetector);
        }

        if (_diedAt != null)
        {
            _diedAt = null;
            _centre = ("", 0);
        }

        Spawned = true;
        Log.Info("Net", string.Create(CultureInfo.InvariantCulture, $"spawned at {sp.Feet.X:0},{sp.Feet.Y:0},{sp.Feet.Z:0}"));
    }

    // ---- what this machine's weapons did ----

    private void OnAvatarHit(DmAvatar a, float points)
    {
        var weapon = WeaponInHand;
        _session.Sounds?.Play3D("HIT2.RAW", a.Centre, -4f);
        _recorder?.Local(NetDemoLocal.WriteHit(_dw, a.Id, points, weapon));
        if (_host != null)
        {
            _host.Hit(a.Id, points, weapon);
        }
        else
        {
            _client!.SendHit(a.Id, points, weapon);
        }
    }

    /// <summary>The soldier's own hurt (his blast, a fall, the water): a self-hit for the server.</summary>
    private void SelfHit(float points)
    {
        if (!Spawned)
        {
            return;
        }

        var weapon = WeaponInHand;
        _recorder?.Local(NetDemoLocal.WriteHit(_dw, LocalId, points, weapon));
        if (_host != null)
        {
            _host.Hit(LocalId, points, weapon, LocalId);
        }
        else
        {
            _client!.SendHit(LocalId, points, weapon);
        }
    }

    /// <summary>The record a hit is credited to: the vehicle's gun while riding, else the one in hand.</summary>
    private int WeaponInHand => _driving != 0 && _rig?.Guns is { } guns ? guns.Gun : _session.Weapons.Inventory.Primary;

    // ---- step 9: vehicles ----

    /// <summary>The use key in a deathmatch, before the level's (SKYNET.EXE v1.01 0x130c80): in a
    /// vehicle it climbs out where the vehicle stands; on foot it asks for the first free whole
    /// vehicle within <see cref="DmProtocol.VehicleBoardReach"/> of the feet. True when it took the
    /// press (a free vehicle in reach, or the seat), else the level's switches get it.</summary>
    public bool UseVehicle()
    {
        if (!Spawned || !_session.Soldier.Alive || _rig == null || _replay != null)
        {
            return false;
        }

        if (_driving != 0)
        {
            var at = _session.Player.Position;
            var yaw = _rig.Controller.CarYaw;
            if (_host != null)
            {
                _host.LeaveVehicle(at, yaw);
            }
            else
            {
                _client!.LeaveVehicle(at, yaw);
            }

            return true;
        }

        var feet = _session.Player.Position;
        foreach (var v in Vehicles.Values.OrderBy(v => v.Key))
        {
            if (v.Driver != 0 || v.Wrecked || Vector3.Distance(v.Position, feet) > DmProtocol.VehicleBoardReach)
            {
                continue;
            }

            if (_host != null)
            {
                _host.RequestVehicle(v.Key);
            }
            else
            {
                _client!.RequestVehicle(v.Key);
            }

            return true;
        }

        return false;
    }

    /// <summary>The server's word on a vehicle: this machine's player boards it or is put out of it,
    /// its hull is set; a wreck explodes where it stands; another player's boarding or leaving is a
    /// status line (<see cref="SeatLine"/>).</summary>
    private void OnVehicle(DmVehicle v, DmVehicleEvent why)
    {
        SeatLine(v, why);
        if (why == DmVehicleEvent.Wrecked && _levelUp)
        {
            var at = v.Position + new Vector3(0f, Hull.JeepCentreHeight, 0f);
            _session.Sounds?.Play3D("EXPLO2.RAW", at, -2f);
            _session.Weapons.Explosion(at, Enemy.DeathEffectBank, v.Kind == VehicleMode.Hk ? 260f : 180f);
            Status?.Invoke(v.Kind == VehicleMode.Hk ? "AN HK BLEW UP" : "A JEEP BLEW UP", Hud.StatusQueue.NetworkColour);
        }

        if (v.Driver == LocalId && _driving != v.Key && _rig != null && _session.Soldier.Alive)
        {
            Board(v);
            return;
        }

        if (_driving == v.Key)
        {
            if (v.Driver != LocalId)
            {
                StepOut(v);
                return;
            }

            if (_rig != null && v.Hull < _rig.Hull.Points)
            {
                _session.Sounds?.Play2D(VehicleRig.HitSound, -3f);
                HitFlash = 0.35f;
            }

            if (_rig != null)
            {
                _rig.Hull.Points = v.Hull;
            }
        }
    }

    /// <summary>Into the seat: the campaign's ride (the controller, the guns, the hull as the target)
    /// where the vehicle stands, facing its heading, its hull as the server has it.</summary>
    private void Board(DmVehicle v)
    {
        var p = _session.Player;
        p.Position = v.Position;
        p.Yaw = v.Yaw;
        p.Pitch = 0f;
        p.Velocity = Vector3.Zero;
        _rig!.Board(v.Kind);
        _rig.Controller.Resume(v.Yaw, 0f, 0f, 0f);
        _rig.Hull.Points = v.Hull;
        _driving = v.Key;
        Log.Info("Net", string.Create(CultureInfo.InvariantCulture, $"boarded vehicle {v.Key} ({v.Kind}) at {v.Position.X:0},{v.Position.Y:0},{v.Position.Z:0}"));
    }

    /// <summary>STRINGS.PRS's line when another player boards or leaves a vehicle, as SKYNET.EXE v1.01
    /// prints it on a peer's update when its avatar turns into or out of the network jeep / HK
    /// (0x118f2a: PlayerInJeep / PlayerOutJeep / PlayerInHK / PlayerOutHK, #P the player's name,
    /// queued in colour 0x0C on the top-left status lines). The driver gets no line of his own (0x130c80
    /// prints none). A new round's return to the markers prints nothing; a seat first seen (the welcome)
    /// neither.</summary>
    private void SeatLine(DmVehicle v, DmVehicleEvent why)
    {
        var known = _seats.TryGetValue(v.Key, out var was);
        _seats[v.Key] = v.Driver;
        if (!known || was == v.Driver || why == DmVehicleEvent.Returned)
        {
            return;
        }

        var jeep = v.Kind == VehicleMode.Jeep;
        if (was != 0 && was != LocalId)
        {
            Status?.Invoke(SeatText(jeep ? "PlayerOutJeep" : "PlayerOutHK", was), Hud.StatusQueue.NetworkColour);
        }

        if (v.Driver != 0 && v.Driver != LocalId)
        {
            Status?.Invoke(SeatText(jeep ? "PlayerInJeep" : "PlayerInHK", v.Driver), Hud.StatusQueue.NetworkColour);
        }
    }

    /// <summary>Another driver's engine, heard where his vehicle is: the loop the campaign plays for the
    /// player's own ride (<see cref="VehicleController.EngineSound"/>: careng1 for the jeep, hk2 for
    /// the HK) at the vehicle, started when he boards and stopped when he leaves, dies or goes.</summary>
    private void Engines()
    {
        foreach (var (id, a) in _avatars)
        {
            var mode = _levelUp && a.Alive ? a.Vehicle : VehicleMode.Foot;
            if (_engines.TryGetValue(id, out var e) && e.Mode != mode)
            {
                e.Loop.Stop();
                _engines.Remove(id);
            }

            if (!_engines.ContainsKey(id) && VehicleController.EngineSound(mode) is >= 0 and var sound
                && _session.Sounds?.PlayLoop3D(sound, a.Centre) is { } loop)
            {
                _engines[id] = (mode, loop);
            }

            if (_engines.TryGetValue(id, out e))
            {
                e.Loop.Position = a.Centre;
            }
        }

        if (_engines.Count > 0)
        {
            foreach (var id in _engines.Keys)
            {
                if (!_avatars.ContainsKey(id))
                {
                    _engines[id].Loop.Stop();
                    _engines.Remove(id);
                }
            }
        }
    }

    /// <summary>The STRINGS.PRS [english] texts (the fallbacks are theirs, for data without the file).</summary>
    private string SeatText(string key, int id)
    {
        var fallback = key switch
        {
            "PlayerInJeep" => "#P JUMPED INTO A JEEP.",
            "PlayerOutJeep" => "#P GOT OUT OF THE JEEP.",
            "PlayerInHK" => "#P FOUND AN HK.",
            _ => "#P DUMPED THE HK.",
        };
        return _session.Text(key, fallback).Replace("#P", NameOf(id), StringComparison.Ordinal);
    }

    /// <summary>Climbing out: this far right of the hull's middle, and up.</summary>
    private const float StepAside = 90f, StepUp = 40f;

    /// <summary>Out of the seat on foot: beside the vehicle, 40 up (then he falls), the campaign's
    /// clear-spawn nudge on top. DOS steps the player 20 across (0x130e36); here that lies inside
    /// the parked hull's collider, which the nudge cannot see from inside, so he steps out
    /// <see cref="StepAside"/> to the right of the hull's middle (past the jeep's 57, the HK's 50).</summary>
    private void StepOut(DmVehicle? v = null)
    {
        if (_driving == 0)
        {
            return;
        }

        _driving = 0;
        if (_rig is not { Riding: true })
        {
            return;
        }

        var p = _session.Player;
        var heading = v?.Yaw ?? (_rig.Mode == VehicleMode.Jeep ? _rig.Controller.CarYaw : p.Yaw);
        var right = new Vector3(MathF.Cos(heading), 0f, -MathF.Sin(heading));
        var at = (v?.Position ?? p.Position) + right * StepAside + new Vector3(0f, StepUp, 0f);
        _rig.Board(VehicleMode.Foot);
        p.Position = PlayerStart.Clear(_session.World, p.Body, at);
        p.Velocity = Vector3.Zero;
        Log.Info("Net", "climbed out");
    }

    private string NameOf(int id)
    {
        if (Players.TryGetValue(id, out var p) && p.Name.Length > 0)
        {
            _names[id] = p.Name;
            return p.Name;
        }

        return _names.TryGetValue(id, out var known) ? known : $"PLAYER {id}";
    }

    public static string ClassName(DmClass c) => c == DmClass.Terminator ? "TERMINATOR" : "HUMAN";

    // ---- step 4: the arena's items ----

    /// <summary>pickup.gd under Net: an item whose DOS grab box (80 across, 160 up or down) holds
    /// the soldier's feet is asked for, at most every half second; the server's word takes it.</summary>
    private void AskForItems()
    {
        if (!_session.Soldier.Alive || RoundOver != null)
        {
            return;
        }

        var feet = _session.Player.Position;
        foreach (var item in Pickups.Values)
        {
            if (item.Taken)
            {
                continue;
            }

            var d = feet - item.Position;
            if (new Vector2(d.X, d.Z).Length() > PickupTable.GrabAcross || MathF.Abs(d.Y) > PickupTable.GrabVertical
                || _clock < _asked.GetValueOrDefault(item.Key, double.NegativeInfinity))
            {
                continue;
            }

            _asked[item.Key] = _clock + PickupAskEvery;
            if (_host != null)
            {
                _host.RequestPickup(item.Key);
            }
            else
            {
                _client!.RequestPickup(item.Key);
            }
        }
    }

    /// <summary>The server says <paramref name="by"/> took <paramref name="key"/>: this machine's
    /// taker gets the ammo and the weapon (handler 0x11d670's line and sound; health and armour are
    /// the server's and come as a health message); somebody else's is heard where it lay.</summary>
    private void OnPickupTaken(int key, int by)
    {
        _itemsDirty = true;
        if (!Pickups.TryGetValue(key, out var item))
        {
            return;
        }

        if (by == LocalId)
        {
            if (PickupTable.Items.TryGetValue(item.Sprite, out var fx))
            {
                _session.Apply(fx with { HealPercent = 0, Armour = 0 });
            }
        }
        else if (_levelUp)
        {
            _session.Sounds?.Play3D(PickupTable.Sound, item.Position, -10f);
        }
    }

    // ---- step 5: chat, the round, the class ----

    public void OpenChat()
    {
        ChatOpen = true;
        ChatText = "";
    }

    public void CloseChat()
    {
        ChatOpen = false;
        ChatText = "";
    }

    /// <summary>Typed text into the open line (capped at <see cref="DmProtocol.MaxChatLength"/>).</summary>
    public void TypeChat(string typed)
    {
        if (!ChatOpen || typed.Length == 0)
        {
            return;
        }

        var t = ChatText + typed;
        ChatText = t.Length > DmProtocol.MaxChatLength ? t[..DmProtocol.MaxChatLength] : t;
    }

    public void EraseChat()
    {
        if (ChatOpen && ChatText.Length > 0)
        {
            ChatText = ChatText[..^1];
        }
    }

    /// <summary>ENTER on the open line: sent (an empty one only closes it).</summary>
    public void SubmitChat()
    {
        var text = ChatText;
        CloseChat();
        SendChat(text);
    }

    public void SendChat(string text)
    {
        if (_host != null)
        {
            _host.SendChat(text);
        }
        else
        {
            _client!.SendChat(text);
        }
    }

    /// <summary>The host's NEW ROUND: scores wiped, items back, everybody respawned. False on a client.</summary>
    public bool Restart()
    {
        if (_host == null)
        {
            return false;
        }

        _host.Server.Restart();
        return true;
    }

    /// <summary>A class for the next life (net_game.gd set_class): the server keeps it until the respawn.</summary>
    public void SetClass(DmClass cls)
    {
        if (_host != null)
        {
            _host.Server.ClientSetClass(LocalId, cls);
        }
        else
        {
            _client!.SetClass(cls);
        }

        // A HUMAN has no machine vision: TERMINATOR VISION goes off with the choice.
        PointCloudVision &= cls == DmClass.Terminator;
        ShowCentre($"NEXT LIFE: {ClassName(cls)}", 2.0);
    }

    private void OnChat(int from, string text) => AddLine(_chat, ChatLines, $"{(from == 0 ? "SERVER" : NameOf(from))}: {text}", Vector4.One, ChatSeconds);

    private void OnMatchOver(string reason)
    {
        RoundOver = reason.Length > 0 ? reason : "ROUND OVER";
        CloseChat();
        Log.Info("Net", $"round over: {RoundOver}");
    }

    private void OnRestarted()
    {
        StepOut();
        _restartedAt = _clock;
        RoundOver = null;
        _itemsDirty = true;
        _asked.Clear();
        ShowCentre("NEW ROUND", 2.0);
        Log.Info("Net", "new round");
    }

    private void OnClassChanged(int id, DmClass cls)
    {
        if (_avatars.TryGetValue(id, out var a))
        {
            a.Class = cls;
            a.Avatar = Players.TryGetValue(id, out var p) ? p.Avatar : DmAvatarTable.ForClass(cls);
        }
    }

    private void AddLine(List<Line> list, int max, string text, Vector4 colour, double seconds)
    {
        list.Add(new Line(text, colour, _clock + seconds));
        while (list.Count > max)
        {
            list.RemoveAt(0);
        }

        Log.Info("Net", text);
    }

    private void ShowCentre(string text, double seconds) => _centre = (text, _clock + seconds);

    /// <summary>
    /// A server's level blasts (the owner's rule): a destroyed record's radial blast strikes the
    /// records as the campaign's does (a chain reaction is the breaker's too), and every body through
    /// the server's rules (<see cref="DmLevelServer.Blast"/>), credited to whoever broke the record;
    /// the avatars and this machine's soldier are left to the server's hits.
    /// </summary>
    public static void OwnLevelBlasts(GameSession session, DmLevelServer level, DmServer server) =>
        session.LevelBlast = (source, at, strength) =>
        {
            var b = level.Behaviour;
            var culprit = b.BreakerOf(source);
            b.RunAs(culprit, () => session.Weapons.BlastAt(at, strength, spare: t => t is DmAvatar || t == session.Weapons.Player));
            foreach (var (victim, points) in level.Blast(server, source, at, strength, (from, to) => LineClear(session.World, from, to, source)))
            {
                Log.Info("Net", string.Create(CultureInfo.InvariantCulture, $"level blast of @{source:x5} (broken by {culprit}) hit player {victim} for {points:0}"));
            }
        };

    /// <summary>No level geometry between a blast and a body (the broken record's own triangles excepted).</summary>
    public static bool LineClear(RetroEng.Physics.CollisionWorld world, Vector3 from, Vector3 to, int source)
    {
        var d = to - from;
        var len = d.Length();
        return len < 1f || !world.Raycast(from, d / len, len - 1f, out var hit) || hit.Owner == source;
    }

    /// <summary>Wraps the session-changing console commands (<see cref="Refused"/>): while
    /// <paramref name="active"/> holds they refuse, as the Godot port's console does in a network game;
    /// and the <see cref="Cheats"/> too while <paramref name="cheatsAllowed"/> does not hold (the
    /// host's <c>--cheats</c> or the menu's CHEATS; the scripted runs pass the flag).</summary>
    public static void Guard(CommandRegistry commands, Func<bool> active, Func<bool>? cheatsAllowed = null)
    {
        foreach (var name in Refused)
        {
            Wrap(name, () => active() ? $"'{name}' is not allowed in a network game" : null);
        }

        foreach (var name in Cheats)
        {
            Wrap(name, () => active() && cheatsAllowed?.Invoke() != true ? $"'{name}': the host has not allowed cheats in this network game" : null);
        }

        void Wrap(string name, Func<string?> refusal)
        {
            if (commands.TryGet(name, out var command) && string.Equals(command.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                var run = command.Run;
                commands.Add(command with { Run = args => refusal() ?? run(args) });
            }
        }
    }
}
