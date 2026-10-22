using System.Globalization;
using System.Net;
using System.Numerics;
using System.Text;
using RetroEng.Diagnostics;
using RetroEng.Net;
using SkyNet.Formats;
using SkyNet.Game.Campaign;

namespace SkyNet.Game.Net;

/// <summary>
/// A dedicated server (docs/multiplayer.md "Dedicated server"): the deathmatch's server with no player
/// of its own, its arena's level run by a <see cref="GameSession"/> that has no window, no renderer and
/// no sound (the collision, the doors and the destructibles, the bots), its map rotation, its door rules
/// (password, bans, connections per address, idle drop) and its console. No clock of its own: whoever
/// owns it calls <see cref="Update"/> (the command-line runner <see cref="DmDedicatedRunner"/>, or the
/// menu's status screen in the game's own loop).
/// </summary>
public sealed class DmDedicated : IDisposable
{
    /// <summary>Log lines kept for the status screen and <c>status</c>.</summary>
    public const int LogLines = 200;

    private readonly GameData _data;
    private readonly GameSession _session;
    private readonly UdpTransport _transport;
    private readonly List<string> _log = [];
    private readonly Enemies.EnemyModels _models;
    private ParkedVehicleColliders? _parked;
    private double _tickAccum;
    private double? _intermissionUntil;
    private int _index;
    private bool _disposed;
    /// <summary>The round being recorded (<see cref="ServerConfig.RecordDemos"/>).</summary>
    private Demos.NetDemoRecorder? _demo;
    private string? _roundOver;

    private DmDedicated(GameData data, ServerConfig config, BanList bans, UdpTransport transport)
    {
        _data = data;
        Config = config;
        Bans = bans;
        _transport = transport;
        _session = new GameSession(data);
        _models = new Enemies.EnemyModels(data);
        _transport.Screen = Screen;
    }

    public ServerConfig Config { get; }

    public BanList Bans { get; }

    public DmHost Host { get; private set; } = null!;

    public DmServer Server => Host.Server;

    public int Port => _transport.LocalPort;

    /// <summary>Where the rotation stands (0-based).</summary>
    public int RotationIndex => _index;

    /// <summary>Rounds finished since the start.</summary>
    public int Rounds { get; private set; }

    /// <summary>Maps changed since the start.</summary>
    public int Rotations { get; private set; }

    /// <summary>Humans that joined since the start.</summary>
    public int Joins { get; private set; }

    /// <summary>Seconds since the start (the server's own clock, across maps).</summary>
    public double Uptime { get; private set; }

    /// <summary>The arena's name (NETLEVEL.PRS).</summary>
    public string Arena { get; private set; } = "";

    /// <summary>Seconds until the next map while a round's end is up; null while a round runs.</summary>
    public double? Intermission => _intermissionUntil is { } t ? Math.Max(0, t - Uptime) : null;

    /// <summary>The server asked to stop (<c>quit</c>); its owner disposes it.</summary>
    public bool Stopped { get; private set; }

    /// <summary>The latest log lines, oldest first.</summary>
    public IReadOnlyList<string> Lines => _log;

    /// <summary>Every log line as it is written (the runner prints them).</summary>
    public event Action<string>? Logged;

    /// <summary>Starts serving <paramref name="config"/>'s first map; null (and why) when the port is
    /// taken, the ban file is broken or the map does not load.</summary>
    public static DmDedicated? Start(GameData data, ServerConfig config, out string? error)
    {
        error = null;
        BanList bans;
        try
        {
            bans = BanList.Load(config.BanFile.Length > 0 ? config.Resolve(config.BanFile) : null);
        }
        catch (Exception e) when (e is RetroEng.Json.JsonDataException or IOException or UnauthorizedAccessException)
        {
            error = $"ban file {config.BanFile}: {e.Message}";
            return null;
        }

        var cap = Math.Clamp(config.Rotation.Max(r => r.MaxPlayers ?? config.MaxPlayers), 1, DmProtocol.MaxPeers);
        // A few more connections than players: a full server still says "Server is full." itself.
        if (UdpTransport.Listen(config.Port, DmProtocol.AppKey, cap + 4, config.Bind, (int)(config.TimeoutSeconds * 1000)) is not { } transport)
        {
            error = $"cannot serve on port {config.Port} (taken?)";
            return null;
        }

        var d = new DmDedicated(data, config, bans, transport);
        if (!d.Load(0, out error))
        {
            d.Dispose();
            return null;
        }

        if (config.Discovery && !d.Host.StartDiscovery(DmProtocol.DiscoveryPort, config.Bind))
        {
            d.Write($"discovery port {DmProtocol.DiscoveryPort} busy: this server will not show in LAN lists");
        }

        d.Write($"'{config.Name}' listening on port {d.Port}{(config.Bind != null ? $" ({config.Bind})" : "")}, " +
            $"{config.Rotation.Length} map(s) in the rotation, classes {DmProtocol.RulesName(config.Classes)}, password {(config.PasswordKey != null ? "on" : "off")}, " +
            $"{bans.Entries.Count} ban(s){(config.Public ? $", public (master server {config.Master})" : "")}");
        if (config.Public && !d.Host.StartMaster(config.Master, DmProtocol.GameId(data.Game), config.Bind))
        {
            d.Write($"public, but the master server's address '{config.Master}' cannot be read ('master' in the config): not listed");
        }

        return d;
    }

    /// <summary>One frame of <paramref name="dt"/> seconds: the wire, the rules, the level, the bots,
    /// the round's end and the rotation.</summary>
    public void Update(double dt)
    {
        if (Stopped)
        {
            return;
        }

        dt = Math.Clamp(dt, 0, 0.25);
        Uptime += dt;
        if (_demo != null)
        {
            _demo.Clock = Uptime;
        }

        Host.Update(dt);
        if (_demo is { KeyframeDue: true })
        {
            _demo.Keyframe(Keyframe());
        }

        _tickAccum = Math.Min(_tickAccum + dt, 0.25);
        while (_tickAccum >= GameSession.Dt)
        {
            _tickAccum -= GameSession.Dt;
            _session.TickServer();
        }

        if (_intermissionUntil is { } until && Uptime >= until)
        {
            _intermissionUntil = null;
            Next();
        }
    }

    /// <summary>The next map of the rotation now (a one-map rotation starts a new round on it).</summary>
    public void Next()
    {
        if (Config.Rotation.Length == 1 && Server.Settings.Map == Config.Rotation[0].Map)
        {
            Write("new round");
            Server.Restart();
            BeginDemo();
            return;
        }

        var next = (_index + 1) % Config.Rotation.Length;
        if (!Load(next, out var error))
        {
            Write($"rotation: {error}; staying on MAP.{Server.Settings.Map:000}");
            Server.Restart();
            BeginDemo();
        }
    }

    /// <summary>The round's demo starts (the last one is written): a keyframe of the round as a client
    /// playing as nobody would be told it.</summary>
    private void BeginDemo()
    {
        EndDemo();
        _roundOver = null;
        if (!Config.RecordDemos)
        {
            return;
        }

        var c = CultureInfo.InvariantCulture;
        var name = string.Create(c, $"round-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Rounds + 1:000}-MAP.{Server.Settings.Map:000}{Demos.CampaignDemo.Extension}");
        var path = Path.Combine(Config.Resolve(Config.DemoFolder), name);
        _demo = new Demos.NetDemoRecorder(Demos.NetDemo.Create(_data, Demos.NetDemoRole.Dedicated, 0, "", Server.Settings), path) { Clock = Uptime };
        _demo.Keyframe(Keyframe());
        Write($"recording the round to {path}");
    }

    /// <summary>The round's demo written (at the next round, a map change or the stop).</summary>
    private void EndDemo()
    {
        if (_demo is not { } d)
        {
            return;
        }

        _demo = null;
        try
        {
            Write(string.Create(CultureInfo.InvariantCulture, $"demo written: {d.Save()} ({d.Seconds:0} s)"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Write($"the round's demo was not written: {e.Message}");
        }
    }

    private byte[] Keyframe() => Demos.NetDemo.Keyframe(0, Server.Settings, Server.Players.Values, Server.Pickups.Values, Server.TimeLeft,
        Server.MatchRunning, Server.Vehicles.Values,
        p => (p.Position, p.Yaw, p.Pitch, Server.ServerFlags(p.Id, 0), Server.VehicleOf(p.Id)?.Yaw ?? p.Yaw),
        Host.Level?.Overlay(), _roundOver, null, null);

    /// <summary>Switches to <paramref name="map"/> now (out of the rotation's order: the rotation goes
    /// on from the entry with that map, else from where it was).</summary>
    public bool ChangeMap(int map, out string? error)
    {
        var at = Config.Rotation.Select((r, i) => (r, i)).FirstOrDefault(x => x.r.Map == map, (null!, -1)).Item2;
        if (at >= 0)
        {
            return Load(at, out error);
        }

        return Load(_index, out error, map);
    }

    /// <summary>Loads rotation entry <paramref name="index"/> (or <paramref name="mapOverride"/> with
    /// that entry's rules) and moves every client there.</summary>
    private bool Load(int index, out string? error, int? mapOverride = null)
    {
        error = null;
        var opts = Config.OptionsFor(index);
        var map = mapOverride ?? opts.HostMap ?? 605;
        var level = NetLevelList.For(NetLevelList.Load(_data), map);
        if (DmArena.Load(_data, map) is not { } arena)
        {
            error = $"{level.MapName} is not in the data";
            return false;
        }

        if (!_session.StartDeathmatch(map))
        {
            error = $"{level.MapName} does not load";
            return false;
        }

        var settings = Config.SettingsFor(index, level) with { Map = map, Arena = level.Name };
        var server = new DmServer(settings);
        var startLoops = new Dictionary<int, (int Id, Vector3 At)>(_session.Loops);
        var levelServer = new DmLevelServer(_session.Behaviour!, snap => _session.ResetLevel(snap, startLoops));
        _session.DeathmatchDrop = (sprite, at) => Host.Server.Drop(sprite, at);
        DmGame.OwnLevelBlasts(_session, levelServer, server);
        server.SetArena(new DmArena
        {
            Map = arena.Map,
            Spawns = [.. arena.Spawns.Select(sp => sp with { Feet = Player.PlayerStart.Clear(_session.World, _session.Player.Body, sp.Feet) })],
            ItemSpots = arena.ItemSpots,
            JeepSpots = [.. arena.JeepSpots.Select(Floor)],
            HkSpots = [.. arena.HkSpots.Select(Floor)],
        });
        server.MatchOver += OnMatchOver;
        if (Host == null)
        {
            Host = new DmHost(new NetEndpoint(_transport), server, null, DmClass.Human)
            {
                Level = levelServer,
                Policy = new DmHostPolicy { PasswordKey = Config.PasswordKey, IdleSeconds = Config.IdleKickSeconds },
            };
            Host.Log += Write;
            Host.Joined += OnJoined;
            Host.Tap = bytes => _demo?.Message(bytes);
            Host.ChatReceived += (from, text) => Write($"{NameOf(from)}: {text}");
        }
        else
        {
            Host.ChangeArena(server, levelServer);
            Rotations++;
        }

        // The arena's parked vehicles stop bodies and shots (the session's world is the same across
        // maps, so once; it asks the server of the day).
        if (_parked == null)
        {
            _parked = new ParkedVehicleColliders(() => Host.Server.Vehicles.Values, _models);
            _session.World.Add(_parked);
        }
        Host.ReportLevelReady();
        if (settings.Bots > 0)
        {
            DmBots.Start(Host, _session.World, _session.Weapons.Table, seed: null, nav: DmNav.For(_session), rides: DmNav.Rides(_session, Host.Server.Vehicles.Values));
        }

        _index = index;
        _intermissionUntil = null;
        Arena = level.Name;
        BeginDemo();
        Write($"map MAP.{map:000} '{level.Name}': goal {settings.Goal.ToString().ToUpperInvariant()} {settings.GoalLimit}, " +
            $"time {settings.TimeLimitMinutes} min, {server.Spawns.Count} spawns, {server.Pickups.Count} items, {server.Vehicles.Count} vehicles, " +
            $"{settings.Bots} bot(s) at skill {settings.BotSkill}, {server.Humans} player(s) carried over");
        return true;

        Vector3 Floor(Vector3 marker) =>
            _session.World.Raycast(marker + new Vector3(0f, 40f, 0f), -Vector3.UnitY, 4000f, out var hit) ? hit.Point : marker;
    }

    private void OnMatchOver(string reason)
    {
        Rounds++;
        var board = DmServer.Rank(Server.Players.Values, Server.Settings.Goal);
        Write($"round over: {reason}; " + string.Join(", ", board.Take(8).Select(p =>
            string.Create(CultureInfo.InvariantCulture, $"{p.Name} {p.Frags}/{p.Deaths}/{p.Points}"))));
        _intermissionUntil = Uptime + Config.IntermissionSeconds;
        _roundOver = reason;
    }

    private void OnJoined(int id)
    {
        Joins++;
        if (Config.Motd.Length > 0)
        {
            Host.ServerChatTo(id, Config.Motd);
        }
    }

    /// <summary>The door check before a connection is accepted: a banned address, or one with too
    /// many connections already, is turned away with the reason.</summary>
    private string? Screen(IPEndPoint from)
    {
        if (Bans.Find(from.Address) is { } ban)
        {
            Write($"refused {from.Address}: banned{(ban.Reason.Length > 0 ? $" ({ban.Reason})" : "")}");
            return ban.Reason.Length > 0 ? $"You are banned from this server: {ban.Reason}" : "You are banned from this server.";
        }

        if (Config.MaxPerAddress > 0 && ConnectionsFrom(from.Address) >= Config.MaxPerAddress)
        {
            Write($"refused {from.Address}: {Config.MaxPerAddress} connection(s) from it already");
            return $"Too many connections from your address (at most {Config.MaxPerAddress}).";
        }

        return null;
    }

    private int ConnectionsFrom(IPAddress address)
    {
        var n = 0;
        for (var peer = 1; peer <= DmProtocol.MaxPeers + 8; peer++)
        {
            if (_transport.RemoteEndPoint(peer) is { } ep && Same(ep.Address, address))
            {
                n++;
            }
        }

        return n;
    }

    private static bool Same(IPAddress a, IPAddress b) =>
        (a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a).Equals(b.IsIPv4MappedToIPv6 ? b.MapToIPv4() : b);

    // ---- console ----

    public static readonly string[] Commands =
    [
        "status", "players", "kick <id> [reason]", "ban <id|address> [reason]", "unban <address>", "bans",
        "map <MAP.NNN>", "next", "say <text>", "quit", "help",
    ];

    /// <summary>Runs one console line (stdin, the status screen); what it says back.</summary>
    public string Execute(string line)
    {
        var text = line.Trim();
        if (text.Length == 0)
        {
            return "";
        }

        var sp = text.IndexOf(' ');
        var cmd = (sp < 0 ? text : text[..sp]).ToLowerInvariant();
        var rest = sp < 0 ? "" : text[(sp + 1)..].Trim();
        switch (cmd)
        {
            case "help":
                return "commands: " + string.Join(", ", Commands);
            case "status":
                return Status();
            case "players":
                return Players();
            case "say":
                if (rest.Length == 0)
                {
                    return "say <text>";
                }

                Host.ServerChat(rest);
                return "";
            case "kick":
            {
                var (id, reason) = Target(rest);
                if (id is not { } who)
                {
                    return "kick <id> [reason]  (the id from 'players')";
                }

                var name = Server.Players.GetValueOrDefault(who)?.Name;
                return Host.KickPlayer(who, reason.Length > 0 ? $"Kicked: {reason}" : "Kicked by the server.")
                    ? $"kicked {name} ({who})" : $"no human player {who}";
            }

            case "ban":
            {
                var (id, reason) = Target(rest);
                if (id is { } who)
                {
                    if (Host.AddressOf(who) is not { } ep)
                    {
                        return $"no human player {who} with an address";
                    }

                    var name = Server.Players[who].Name;
                    Bans.Add(ep.Address, name, reason);
                    Host.KickPlayer(who, reason.Length > 0 ? $"Banned: {reason}" : "Banned from this server.");
                    KickAddress(ep.Address, reason);
                    return $"banned {name} ({ep.Address})";
                }

                var word = rest.Split(' ', 2);
                if (word.Length == 0 || !IPAddress.TryParse(word[0], out var address))
                {
                    return "ban <id|address> [reason]";
                }

                var r = word.Length > 1 ? word[1].Trim() : "";
                Bans.Add(address, "", r);
                KickAddress(address, r);
                return $"banned {address}";
            }

            case "unban":
                return IPAddress.TryParse(rest, out var un) ? Bans.Remove(un) ? $"unbanned {un}" : $"{un} was not banned" : "unban <address>";
            case "bans":
                return Bans.Entries.Count == 0 ? "no bans"
                    : string.Join("\n", Bans.Entries.Select(b => $"{b.Address}  {b.Name}  {b.Reason}  {b.When}"));
            case "map":
                if (NetLevelList.ParseMapName(rest) is not { } map)
                {
                    return "map <MAP.NNN>";
                }

                return ChangeMap(map, out var error) ? $"now on MAP.{map:000}" : error ?? "failed";
            case "next":
                Next();
                return $"now on MAP.{Server.Settings.Map:000}";
            case "quit" or "stop" or "exit":
                Stop("The server is shutting down.");
                return "stopping";
            default:
                return $"unknown command '{cmd}' (help lists them)";
        }
    }

    private void KickAddress(IPAddress address, string reason)
    {
        foreach (var p in Server.Players.Values.Where(p => !p.Bot).ToList())
        {
            if (Host.AddressOf(p.Id) is { } ep && Same(ep.Address, address))
            {
                Host.KickPlayer(p.Id, reason.Length > 0 ? $"Banned: {reason}" : "Banned from this server.");
            }
        }
    }

    private static (int? Id, string Reason) Target(string rest)
    {
        var parts = rest.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 0 && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)
            ? (id, parts.Length > 1 ? parts[1].Trim() : "") : (null, "");
    }

    /// <summary>One line of where the server stands.</summary>
    public string Status()
    {
        var s = Server.Settings;
        var time = s.RoundSeconds > 0 ? $"{(int)Server.TimeLeft / 60}:{(int)Server.TimeLeft % 60:00} left" : "no time limit";
        var state = Intermission is { } wait ? $"round over, next map in {wait:0} s" : time;
        return string.Create(CultureInfo.InvariantCulture,
            $"'{Config.Name}' port {Port}, MAP.{s.Map:000} '{Arena}' ({_index + 1}/{Config.Rotation.Length}), {state}, " +
            $"{Server.Humans}/{Host.Server.Settings.MaxPlayers} players + {Server.BotCount} bots, up {TimeSpan.FromSeconds(Uptime):hh\\:mm\\:ss}, " +
            $"{Rounds} round(s), {Joins} join(s)");
    }

    /// <summary>The roster: id, name, score, address, idle time.</summary>
    public string Players()
    {
        var board = DmServer.Rank(Server.Players.Values, Server.Settings.Goal);
        if (board.Count == 0)
        {
            return "no players";
        }

        var sb = new StringBuilder();
        foreach (var p in board)
        {
            var where = p.Bot ? "bot" : Host.AddressOf(p.Id)?.Address.ToString() ?? "local";
            sb.Append(CultureInfo.InvariantCulture, $"{p.Id,4}  {p.Name,-16} {p.Frags,4} kills {p.Deaths,4} deaths {p.Points,6} points  {where}");
            if (!p.Bot)
            {
                sb.Append(CultureInfo.InvariantCulture, $"  idle {Host.IdleOf(p.Id):0} s");
            }

            sb.Append('\n');
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>Everyone told <paramref name="reason"/>, the connections closed; <see cref="Stopped"/>.</summary>
    public void Stop(string reason)
    {
        if (Stopped)
        {
            return;
        }

        Write($"stopping: {reason}");
        foreach (var p in Server.Players.Values.Where(p => !p.Bot).ToList())
        {
            Host.KickPlayer(p.Id, reason);
        }

        Host.Update(0);
        EndDemo();
        Stopped = true;
    }

    public void Dispose()
    {
        // Twice is once (SERVER STATUS's QUIT and its owner may both close it).
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (Host != null)
        {
            if (!Stopped)
            {
                Stop("The server closed.");
            }

            Host.Dispose();
        }
        else
        {
            _transport.Dispose();
        }

        _session.EndDeathmatch();
    }

    private string NameOf(int id) => id == 0 ? "SERVER" : Server.Players.GetValueOrDefault(id)?.Name ?? $"PLAYER {id}";

    private void Write(string line)
    {
        _log.Add(line);
        if (_log.Count > LogLines)
        {
            _log.RemoveRange(0, _log.Count - LogLines);
        }

        Log.Info("Server", line);
        Logged?.Invoke(line);
    }
}
