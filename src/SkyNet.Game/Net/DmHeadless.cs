using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Numerics;
using RetroEng.Diagnostics;
using RetroEng.Net;
using SkyNet.Formats;

namespace SkyNet.Game.Net;

/// <summary>Options of a headless deathmatch run (<see cref="DmHeadless"/>).</summary>
public sealed record DmRunOptions
{
    /// <summary>Host this map (MAP.605, 605); null = join.</summary>
    public int? HostMap { get; init; }
    public string? JoinHost { get; init; }
    public int Port { get; init; } = DmProtocol.DefaultPort;
    public string Name { get; init; } = "PLAYER";
    public DmClass Class { get; init; }
    /// <summary>Which of DOS's 13 avatars to wear (<c>--avatar=1..13</c>, the host and join screens);
    /// 0 or one not of <see cref="Class"/>: the class's default.</summary>
    public int Avatar { get; init; }
    /// <summary>The host's CLASSES (<c>--classes=dos|balanced</c>, the menu's CLASSES, server.json's
    /// <c>classes</c>).</summary>
    public DmClassRules Classes { get; init; } = DmClassRules.Balanced;
    /// <summary>TERMINATOR VISION (<c>--vision=normal|pointcloud</c>, the network screens' row): a
    /// TERMINATOR's machine vision drawn as a point cloud. This machine's own, never sent.</summary>
    public bool PointCloudVision { get; init; }
    /// <summary>Bind the server to this address (the smoke test: 127.0.0.1).</summary>
    public IPAddress? Bind { get; init; }
    public bool Discovery { get; init; } = true;
    public int Bots { get; init; }
    /// <summary>Bot skill 0-2 (bot_brain.gd SKILL).</summary>
    public int BotSkill { get; init; } = 1;
    public int TimeLimitMinutes { get; init; }
    /// <summary>The round's goal (<c>--goal</c>, the menu's GOAL); null: NETLEVEL.PRS's.</summary>
    public DmGoal? Goal { get; init; }
    /// <summary>The goal's limit (<c>--goal-limit</c> / <c>--frags</c>, the menu's box after GOAL;
    /// 0 none); null: DOS's default for the goal (<see cref="DmSettings.DefaultLimit"/>).</summary>
    public int? GoalLimit { get; init; }
    /// <summary>The console's cheats work in this game (<c>--cheats</c>, the menu's CHEATS).</summary>
    public bool Cheats { get; init; }
    /// <summary>Seconds before the run gives up (0 = runs until stopped).</summary>
    public double QuitAfter { get; init; }
    /// <summary>The two-process check: see each other, trade one hit, leave.</summary>
    public bool Smoke { get; init; }
    /// <summary>The dedicated server's check (<c>--smoke=dedicated</c>, a client): the lower of two
    /// players shoots the other until the round ends, and both pass once the server has moved them to
    /// the next map.</summary>
    public bool SmokeDedicated { get; init; }
    public string? Data { get; init; }
    /// <summary>The host's MAX # PLAYERS (the network menu); null: NETLEVEL.PRS's.</summary>
    public int? MaxPlayers { get; init; }
    /// <summary>The host's REPLENISH (the network menu); null: NETLEVEL.PRS's.</summary>
    public bool? Replenish { get; init; }
    /// <summary>The host's item counts by NETLEVEL key (the network menu); null: NETLEVEL.PRS's.</summary>
    public System.Collections.Immutable.ImmutableDictionary<string, int>? Items { get; init; }
    /// <summary>The server's password (<c>--password</c>): a joiner proves it, a dedicated server from
    /// the menu asks for it.</summary>
    public string? Password { get; init; }
    /// <summary>No local player: the menu's DEDICATED (the server runs behind the status screen).</summary>
    public bool Dedicated { get; init; }
    /// <summary>The master server ("host:port"): a public host announces itself there, a joiner
    /// asks it for an introduction (<c>--master</c>, the settings' <c>net.master</c>).</summary>
    public string? Master { get; init; }
    /// <summary>The host announces itself to <see cref="Master"/> (<c>--public</c>, the menu's PUBLIC).</summary>
    public bool Public { get; init; }
    /// <summary>Join through <see cref="Master"/>'s NAT introduction (an INTERNET list entry; a direct
    /// connection when no introduction comes).</summary>
    public bool Introduce { get; init; }
    /// <summary>A client finds the server of this name in <see cref="Master"/>'s list and joins it
    /// through the introduction (<c>--find</c>: tools/net-smoke.ps1 -Master).</summary>
    public string? Find { get; init; }

    /// <summary>The server's settings for <paramref name="level"/>: NETLEVEL.PRS's, with these
    /// options' limits, bots and overrides.</summary>
    public DmSettings SettingsFor(NetLevel level)
    {
        var goal = Goal ?? DmSettings.ParseGoal(level.Goal);
        return DmSettings.From(level, $"{DmProtocol.CleanText(Name, DmProtocol.MaxNameLength)}'s game") with
        {
            TimeLimitMinutes = TimeLimitMinutes, Goal = goal, GoalLimit = GoalLimit ?? DmSettings.DefaultLimit(goal), Cheats = Cheats,
            Bots = Bots, BotSkill = BotSkill, MaxPlayers = MaxPlayers ?? level.MaxPlayers, Replenish = Replenish ?? level.Replenish,
            Items = Items ?? level.Items, Classes = Classes,
        };
    }

    /// <summary>A host's door rules: <see cref="Password"/>'s key (the menu's PASSWORD, <c>--password</c>
    /// on <c>--host</c>); only the key is kept.</summary>
    public DmHostPolicy HostPolicy() => new() { PasswordKey = string.IsNullOrEmpty(Password) ? null : DmProtocol.PasswordKey(Password) };

    /// <summary>The transport's peer cap for <paramref name="settings"/>.</summary>
    public static int Cap(DmSettings settings) =>
        settings.MaxPlayers > 0 ? Math.Min(settings.MaxPlayers, DmProtocol.MaxPeers) : DmProtocol.MaxPeers;
}

/// <summary>
/// A deathmatch with no window (step 1 of docs/multiplayer.md): <c>--host=MAP.605</c> serves the
/// arena with the host standing at a spawn, <c>--join=ip[:port]</c> joins one; both keep sending
/// poses at 20 Hz. With <c>--smoke</c> they check the wire end to end: each sees the other's pose,
/// the client hits the host, the host hits back, the client leaves, and both exit 0 (1 when a
/// step did not happen before <c>--quit-after</c>). The host prints "listening on port N" first,
/// so <c>--port=0</c> (any free port) works for scripts.
/// </summary>
public static class DmHeadless
{
    public const double TickSeconds = 1.0 / 60.0;

    /// <summary>The command line asks for a network game.</summary>
    public static bool Wants(string[] args) =>
        args.Any(a => a.StartsWith("--host", StringComparison.Ordinal) || a.StartsWith("--join", StringComparison.Ordinal) || a.StartsWith("--find", StringComparison.Ordinal));

    /// <summary>A network game without the game: <c>--smoke</c> (tools/net-smoke.ps1) or
    /// <c>--headless</c>. Otherwise <c>--host</c> / <c>--join</c> start the game in its window.</summary>
    public static bool Headless(string[] args) => args.Any(a => a.StartsWith("--smoke", StringComparison.Ordinal)) || args.Contains("--headless");

    /// <summary>The network options' names; a value after a space belongs to them (--port 27015).</summary>
    private static readonly string[] NetKeys =
        ["--host", "--join", "--port", "--name", "--class", "--bind", "--bots", "--bot-skill", "--time", "--frags", "--goal", "--goal-limit", "--quit-after", "--password", "--master", "--find", "--classes", "--avatar", "--vision"];
    private static readonly string[] NetFlags = ["--no-discovery", "--smoke", "--headless", "--cheats", "--public"];
    /// <summary>Options both runs read (the game's own parser too).</summary>
    private static readonly string[] SharedKeys = ["--data"];
    private static readonly string[] SharedFlags = ["--offscreen"];

    /// <summary>The command line split for the windowed game: the network options (for
    /// <see cref="Parse"/>) and the game's own (its usual parser), <c>--data</c> and
    /// <c>--offscreen</c> in both.</summary>
    public static (string[] Net, string[] Game) Split(string[] args)
    {
        var net = new List<string>();
        var game = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            var eq = a.IndexOf('=');
            var key = eq > 0 ? a[..eq] : a;
            var takesNext = eq <= 0 && i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal);
            if (NetKeys.Contains(key) || SharedKeys.Contains(key))
            {
                var part = takesNext ? new[] { a, args[++i] } : [a];
                net.AddRange(part);
                if (SharedKeys.Contains(key))
                {
                    game.AddRange(part);
                }
            }
            else if (NetFlags.Contains(a))
            {
                net.Add(a);
            }
            else
            {
                game.Add(a);
                if (SharedFlags.Contains(a))
                {
                    net.Add(a);
                }
            }
        }

        return ([.. net], [.. game]);
    }

    /// <summary>Parses the arguments, finds the data, runs; the process exit code.</summary>
    public static int Run(string[] args)
    {
        TextLogSink.AttachParentConsole();
        var sink = TextLogSink.Console(plain: true);
        Log.AddSink(sink, LogLevel.Info);
        try
        {
            if (Parse(args, out var error) is not { } o)
            {
                Log.Error("Net", error ?? "bad command line");
                return 2;
            }

            var dir = GameDataLocator.Locate(o.Data ?? Environment.GetEnvironmentVariable("SKYNET_DATA"), null,
                [AppContext.BaseDirectory, Environment.CurrentDirectory]);
            var data = dir != null ? GameData.Open(dir) : null;
            if (data == null)
            {
                Log.Error("Net", "No game data found: pass --data <dir> or set SKYNET_DATA.");
                return 2;
            }

            return o.HostMap is { } map ? RunHost(data, o, map) : RunClient(data, o);
        }
        finally
        {
            Log.RemoveSink(sink);
        }
    }

    public static DmRunOptions? Parse(string[] args, out string? error)
    {
        error = null;
        var o = new DmRunOptions();
        var c = CultureInfo.InvariantCulture;
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            string key, value;
            var eq = a.IndexOf('=');
            if (eq > 0)
            {
                key = a[..eq];
                value = a[(eq + 1)..];
            }
            else
            {
                key = a;
                value = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : "";
            }

            bool Int(out int n) => int.TryParse(value, NumberStyles.Integer, c, out n);

            switch (key)
            {
                case "--host":
                    if (NetLevelList.ParseMapName(value.Length > 0 ? value : "MAP.605") is not { } m)
                    {
                        error = $"--host takes a map like MAP.605, not '{value}'";
                        return null;
                    }

                    o = o with { HostMap = m };
                    break;
                case "--join":
                    var host = value.Length > 0 ? value : "127.0.0.1";
                    var colon = host.LastIndexOf(':');
                    if (colon > 0 && int.TryParse(host[(colon + 1)..], NumberStyles.None, c, out var jp))
                    {
                        o = o with { JoinHost = host[..colon], Port = jp };
                    }
                    else
                    {
                        o = o with { JoinHost = host };
                    }

                    break;
                case "--port":
                    if (!Int(out var port) || port is < 0 or > 65535)
                    {
                        error = $"--port takes a port 0-65535, not '{value}'";
                        return null;
                    }

                    o = o with { Port = port };
                    break;
                case "--name": o = o with { Name = value }; break;
                case "--master": o = o with { Master = value }; break;
                case "--public": o = o with { Public = true }; break;
                case "--find": o = o with { Find = value }; break;
                case "--password": o = o with { Password = value }; break;
                case "--classes":
                    if (DmProtocol.ParseRules(value) is not { } rules)
                    {
                        error = $"--classes takes dos or balanced, not '{value}'";
                        return null;
                    }

                    o = o with { Classes = rules };
                    break;
                case "--vision":
                    if (value.ToLowerInvariant() is not ("normal" or "pointcloud"))
                    {
                        error = $"--vision takes normal or pointcloud, not '{value}'";
                        return null;
                    }

                    o = o with { PointCloudVision = value.Equals("pointcloud", StringComparison.OrdinalIgnoreCase) };
                    break;
                case "--class": o = o with { Class = value.Equals("terminator", StringComparison.OrdinalIgnoreCase) ? DmClass.Terminator : DmClass.Human }; break;
                case "--avatar":
                    if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var avatar) || avatar is < 1 or > DmAvatarTable.Count)
                    {
                        error = $"--avatar takes 1 to {DmAvatarTable.Count}, not '{value}'";
                        return null;
                    }

                    // The avatar names its class (7 and 13 are the machines).
                    o = o with { Avatar = avatar, Class = DmAvatarTable.Get(avatar).Class };
                    break;
                case "--bind":
                    if (!IPAddress.TryParse(value, out var bind))
                    {
                        error = $"--bind takes an IP address, not '{value}'";
                        return null;
                    }

                    o = o with { Bind = bind };
                    break;
                case "--no-discovery": o = o with { Discovery = false }; break;
                case "--cheats": o = o with { Cheats = true }; break;
                case "--goal":
                    if (value.ToLowerInvariant() is not ("points" or "kills" or "lives" or "time"))
                    {
                        error = $"--goal takes points, kills, lives or time, not '{value}'";
                        return null;
                    }

                    o = o with { Goal = DmSettings.ParseGoal(value) };
                    break;
                case "--bots" or "--bot-skill" or "--time" or "--frags" or "--goal-limit":
                    if (!Int(out var n))
                    {
                        error = $"{key} takes a whole number, not '{value}'";
                        return null;
                    }

                    o = key switch
                    {
                        "--bots" => o with { Bots = Math.Clamp(n, 0, 12) },
                        "--bot-skill" => o with { BotSkill = Math.Clamp(n, 0, 2) },
                        "--time" => o with { TimeLimitMinutes = Math.Clamp(n, 0, 180) },
                        _ => o with { GoalLimit = Math.Clamp(n, 0, 99999) },
                    };
                    break;
                case "--quit-after":
                    if (!double.TryParse(value, NumberStyles.Float, c, out var after) || !double.IsFinite(after) || after < 0)
                    {
                        error = $"--quit-after takes seconds, not '{value}'";
                        return null;
                    }

                    o = o with { QuitAfter = after };
                    break;
                case "--smoke": o = value == "dedicated" ? o with { Smoke = true, SmokeDedicated = true } : o with { Smoke = true }; break;
                case "--headless": break; // Headless decided it
                case "--data": o = o with { Data = value }; break;
                case "--offscreen": break; // the windowed game's; this run has no window
                default:
                    error = $"Unknown option {key} for a network run";
                    return null;
            }
        }

        if (o.HostMap == null && o.JoinHost == null && o.Find == null)
        {
            error = "--host, --join or --find is needed";
            return null;
        }

        if ((o.Find != null || o.Public) && !RetroEng.Net.Master.MasterProtocol.TryParseAddress(o.Master, RetroEng.Net.Master.MasterProtocol.DefaultPort, out _, out _))
        {
            error = "--find and --public need --master=HOST[:PORT]";
            return null;
        }

        if (o.Port is < 0 or > 65535 || (o.JoinHost != null && o.Port == 0))
        {
            error = "--port must be 1-65535 (0 = any free port, hosting only)";
            return null;
        }

        return o.Smoke && o.QuitAfter <= 0 ? o with { QuitAfter = 30 } : o;
    }

    // ---- host ----

    private static int RunHost(GameData data, DmRunOptions o, int mapNumber)
    {
        var level = NetLevelList.For(NetLevelList.Load(data), mapNumber);
        var arena = DmArena.Load(data, mapNumber);
        if (arena == null)
        {
            Log.Error("Net", $"{level.MapName} is not in the data");
            return 1;
        }

        Log.Info("Net", $"arena {level.MapName} '{level.Name}': {arena.Spawns.Count} spawns, {arena.ItemSpots.Count} item spots");
        var settings = o.SettingsFor(level);
        var cap = DmRunOptions.Cap(settings);
        var transport = UdpTransport.Listen(o.Port, DmProtocol.AppKey, cap, o.Bind);
        if (transport == null)
        {
            Log.Error("Net", $"cannot host on port {o.Port} (taken?)");
            return 1;
        }

        using var host = new DmHost(new NetEndpoint(transport), new DmServer(settings), o.Name, o.Class, o.Avatar) { Policy = o.HostPolicy() };
        host.Log += s => Log.Info("Net", s);
        Log.Info("Net", $"classes {DmProtocol.RulesName(settings.Classes)}, password {(host.Policy.PasswordKey != null ? "on" : "off")}");
        // Bots walk the level's collision: the campaign's session builds it (no window, no sound),
        // and its clear-spawn nudge moves the spawns as the windowed host does.
        var session = o.Bots > 0 ? new Campaign.GameSession(data) : null;
        if (session != null && session.StartDeathmatch(mapNumber))
        {
            arena = DmBots.Cleared(arena, session);
        }

        host.Server.SetArena(arena);
        Log.Info("Net", $"listening on port {host.Port}");
        Log.Info("Net", $"placed {host.Server.Pickups.Count} pickups");
        if (o.Discovery && !host.StartDiscovery(DmProtocol.DiscoveryPort, o.Bind))
        {
            Log.Warn("Net", $"discovery port {DmProtocol.DiscoveryPort} busy: this game will not show in LAN lists");
        }

        host.ReportLevelReady();
        if (session != null)
        {
            session.World.Add(new ParkedVehicleColliders(() => host.Server.Vehicles.Values, new Enemies.EnemyModels(data)));
            DmBots.Start(host, session.World, session.Weapons.Table, seed: null, nav: DmNav.For(session), rides: DmNav.Rides(session, host.Server.Vehicles.Values));
            Log.Info("Net", $"{o.Bots} bots at skill {o.BotSkill}");
        }

        var me = host.Server.Players[host.LocalId];
        Log.Info("Net", $"host spawned at {Fmt(me.Position)}");

        bool sawPose = false, wasHit = false, hitBack = false, clientLeft = false;
        int client = 0;
        host.PoseReceived += (id, pos, _, _, _, _) =>
        {
            if (!sawPose)
            {
                Log.Info("Net", $"host sees player {id} at {Fmt(pos)}");
            }

            sawPose = true;
            client = id;
        };
        host.LocalHealthChanged += (hp, _, attacker) =>
        {
            Log.Info("Net", $"host hit by {attacker}: health {hp:0}");
            wasHit |= attacker > DmServer.HostId;
        };
        host.Server.PlayerRemoved += id => clientLeft |= id == client && client != 0;

        var clock = Stopwatch.StartNew();
        var last = 0.0;
        while (true)
        {
            var now = clock.Elapsed.TotalSeconds;
            var dt = now - last;
            last = now;
            host.Update(dt);
            host.SendPose(me.Position, me.Yaw + (float)now * 0.5f, 0f, 0, dt);
            if (o.Smoke)
            {
                if (sawPose && wasHit && !hitBack && host.Server.IsAlive(client))
                {
                    hitBack = true;
                    host.Hit(client, 100f, 1);
                    Log.Info("Net", $"host hits player {client} back");
                }

                if (hitBack && clientLeft)
                {
                    Log.Info("Net", "SMOKE host: PASS (saw the client's pose, was hit, hit back, the client left)");
                    host.Shutdown();
                    return 0;
                }
            }

            if (o.QuitAfter > 0 && now >= o.QuitAfter)
            {
                host.Shutdown();
                if (o.Smoke)
                {
                    Log.Error("Net", $"SMOKE host: FAIL (saw pose {sawPose}, was hit {wasHit}, hit back {hitBack}, client left {clientLeft})");
                    return 1;
                }

                return 0;
            }

            Thread.Sleep(TimeSpan.FromSeconds(TickSeconds));
        }
    }

    // ---- client ----

    /// <summary>The client's connection for <paramref name="o"/>: through the master's NAT
    /// introduction (an INTERNET list entry), else straight to the address. Null when it cannot open.</summary>
    public static UdpTransport? ClientTransport(DmRunOptions o)
    {
        if (o.Introduce && IPAddress.TryParse(o.JoinHost, out var ip)
            && RetroEng.Net.Master.MasterProtocol.TryParseAddress(o.Master, RetroEng.Net.Master.MasterProtocol.DefaultPort, out var mh, out var mp))
        {
            var t = UdpTransport.ConnectIntroduced(new IPEndPoint(ip, o.Port), mh, mp, DmProtocol.AppKey);
            if (t != null)
            {
                var told = false;
                t.NatIntroduced += from =>
                {
                    if (!told)
                    {
                        told = true;
                        Log.Info("Net", $"the master server introduced this client: the server's punch came from {from}");
                    }
                };
            }

            return t;
        }

        return UdpTransport.Connect(o.JoinHost!, o.Port, DmProtocol.AppKey);
    }

    /// <summary><c>--find</c>: the master's list until the named server shows (or the time is up).</summary>
    private static DmRunOptions? FindInMaster(GameData data, DmRunOptions o)
    {
        using var browser = RetroEng.Net.Master.MasterBrowser.Start(o.Master!, DmProtocol.GameId(data.Game), DmProtocol.Version);
        if (browser == null)
        {
            Log.Error("Net", $"cannot read the master address '{o.Master}'");
            return null;
        }

        Log.Info("Net", $"asking the master server {browser.MasterName} for '{o.Find}'");
        var clock = Stopwatch.StartNew();
        var limit = o.QuitAfter > 0 ? o.QuitAfter : 30;
        var refreshed = 0.0;
        while (clock.Elapsed.TotalSeconds < limit)
        {
            var now = clock.Elapsed.TotalSeconds;
            browser.Poll(now);
            if (browser.State == RetroEng.Net.Master.MasterBrowserState.Unreachable)
            {
                break;
            }

            if (browser.Servers.FirstOrDefault(s => s.Info.Name == o.Find) is { } found && (found.PingMs != null || now - refreshed > 3))
            {
                Log.Info("Net", $"found '{found.Info.Name}' at {found.Address} in the master's list ({found.Info.Map}, " +
                    $"{found.Info.Players}/{found.Info.MaxPlayers} players, ping {(found.PingMs is { } ms ? $"{ms} ms" : "none")}, " +
                    $"{(found.Nat ? "behind a NAT" : "direct")}); joining through the introduction");
                return o with { JoinHost = found.Address.Address.ToString(), Port = found.Address.Port, Introduce = true };
            }

            if (browser.State is RetroEng.Net.Master.MasterBrowserState.Listed or RetroEng.Net.Master.MasterBrowserState.NoAnswer && now - refreshed > 1)
            {
                refreshed = now;
                browser.Refresh();
            }

            Thread.Sleep(20);
        }

        Log.Error("Net", $"'{o.Find}' is not in the master's list ({browser.State}, {browser.Servers.Count} servers)");
        return null;
    }

    private static int RunClient(GameData data, DmRunOptions o)
    {
        if (o.Find != null)
        {
            if (FindInMaster(data, o) is not { } found)
            {
                return 1;
            }

            o = found;
        }

        var transport = ClientTransport(o);
        if (transport == null)
        {
            Log.Error("Net", $"cannot connect to {o.JoinHost}:{o.Port}");
            return 1;
        }

        using var client = new DmClient(new NetEndpoint(transport), o.Name, o.Class, o.Password, o.Avatar);
        Log.Info("Net", $"connecting to {o.JoinHost}:{o.Port} as {client.Name}");
        string? gone = null;
        bool sawHost = false, hitSent = false, wasHit = false, left = false;
        var maps = new List<int>();
        var nextShot = 0.0;
        client.Disconnected += reason => gone = reason;
        client.Welcomed += () =>
        {
            var s = client.Settings!;
            maps.Add(s.Map);
            Log.Info("Net", $"welcome: player {client.LocalId}, MAP.{s.Map:000} '{s.Arena}', {client.Players.Count} players, {client.Pickups.Count} pickups");
            // The arena is loaded the way the windowed game will (here only to check it is there).
            if (DmArena.Load(data, s.Map) is { } arena)
            {
                Log.Info("Net", $"arena loaded: {arena.Spawns.Count} spawns");
                client.ReportLevelReady();
            }
            else
            {
                Log.Error("Net", $"MAP.{s.Map:000} is not in this game's data");
                client.Leave();
            }
        };
        client.Respawned += (id, sp) =>
        {
            if (id == client.LocalId)
            {
                Log.Info("Net", $"spawned at {Fmt(sp.Feet)}");
            }
        };
        client.PoseReceived += (id, pos, _, _, _, _) =>
        {
            if (id == DmServer.HostId && !sawHost)
            {
                sawHost = true;
                Log.Info("Net", $"client sees the host at {Fmt(pos)}");
            }
        };
        client.LocalHealthChanged += (hp, _, attacker) =>
        {
            Log.Info("Net", $"client hit by {attacker}: health {hp:0}");
            wasHit |= attacker == DmServer.HostId;
        };

        var clock = Stopwatch.StartNew();
        var last = 0.0;
        while (true)
        {
            var now = clock.Elapsed.TotalSeconds;
            var dt = now - last;
            last = now;
            client.Update(dt);
            if (left)
            {
                return 0; // the poll after Leave let the disconnect go out
            }

            if (gone != null)
            {
                Log.Error("Net", $"disconnected: {gone}");
                return o.Smoke ? 1 : 0;
            }

            if (o.SmokeDedicated)
            {
                if (client.Players.TryGetValue(client.LocalId, out var self) && self.Alive)
                {
                    client.SendPose(self.Position, self.Yaw + (float)now * 0.3f, 0f, 0, dt);
                    var other = client.Players.Values.Where(p => !p.Bot && p.Id != client.LocalId && p.Alive).OrderBy(p => p.Id).FirstOrDefault();
                    if (other != null && client.LocalId < other.Id && client.MatchRunning && now >= nextShot)
                    {
                        nextShot = now + 0.4;
                        client.SendHit(other.Id, 100f, 1);
                    }
                }

                if (maps.Count >= 2)
                {
                    Log.Info("Net", $"SMOKE client: PASS (welcomed on MAP.{maps[0]:000}, then moved to MAP.{maps[^1]:000})");
                    client.Leave();
                    left = true;
                }
            }
            else if (client.Players.TryGetValue(client.LocalId, out var me) && me.Alive)
            {
                client.SendPose(me.Position, me.Yaw, 0f, 0, dt);
                if (o.Smoke && sawHost && !hitSent)
                {
                    hitSent = true;
                    client.SendHit(DmServer.HostId, 100f, 1);
                    Log.Info("Net", "client hits the host");
                }

                if (o.Smoke && wasHit)
                {
                    Log.Info("Net", "SMOKE client: PASS (welcomed, spawned, saw the host's pose, hit it, was hit back)");
                    client.Leave();
                    left = true;
                }
            }

            if (!left && o.QuitAfter > 0 && now >= o.QuitAfter)
            {
                client.Leave();
                if (o.Smoke)
                {
                    Log.Error("Net", o.SmokeDedicated
                        ? $"SMOKE client: FAIL (welcomed on {string.Join(", ", maps.Select(m => $"MAP.{m:000}"))})"
                        : $"SMOKE client: FAIL (welcomed {client.Active}, saw host {sawHost}, hit sent {hitSent}, was hit {wasHit})");
                    return 1;
                }

                return 0;
            }

            Thread.Sleep(TimeSpan.FromSeconds(TickSeconds));
        }
    }

    private static string Fmt(Vector3 v) => string.Create(CultureInfo.InvariantCulture, $"{v.X:0},{v.Y:0},{v.Z:0}");
}
