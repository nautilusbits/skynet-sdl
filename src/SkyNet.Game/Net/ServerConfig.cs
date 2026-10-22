using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using RetroEng.Json;
using SkyNet.Formats;

namespace SkyNet.Game.Net;

/// <summary>One arena of a dedicated server's rotation, with what it changes of the server's defaults
/// (null: the default, which is itself NETLEVEL.PRS's when the server sets nothing).</summary>
public sealed record RotationEntry
{
    public int Map { get; init; } = 605;
    public DmGoal? Goal { get; init; }
    public int? GoalLimit { get; init; }
    public int? TimeLimitMinutes { get; init; }
    public int? Bots { get; init; }
    public int? BotSkill { get; init; }
    public int? MaxPlayers { get; init; }
    public bool? Replenish { get; init; }
    public ImmutableDictionary<string, int>? Items { get; init; }
}

/// <summary>
/// A dedicated server's configuration (<c>SkyNet --dedicated --config server.json</c>, docs/multiplayer.md
/// "Dedicated server"), read with <see cref="JsonItem"/> (comments and trailing commas allowed). Every
/// key is optional; an unknown key or a wrong value fails with the file, the JSON path, the line and
/// the column. The menu's DEDICATED builds one from the host screen (<see cref="FromOptions"/>).
/// </summary>
public sealed record ServerConfig
{
    public string Name { get; init; } = "SKYNET DEDICATED";
    public int Port { get; init; } = DmProtocol.DefaultPort;
    /// <summary>The address to serve on; null: every interface.</summary>
    public IPAddress? Bind { get; init; }
    /// <summary>Human players at most (bots do not count), 1-16.</summary>
    public int MaxPlayers { get; init; } = 8;
    /// <summary>Connections from one address at most (0: no limit).</summary>
    public int MaxPerAddress { get; init; } = 2;
    /// <summary><see cref="DmProtocol.PasswordKey"/> of the password (from <c>password</c> or
    /// <c>password_sha256</c>); null: none.</summary>
    public byte[]? PasswordKey { get; init; }
    public bool Cheats { get; init; }
    /// <summary>CLASSES: DOS (equal classes) or BALANCED (the machine at 0.9 of the pace; the default).</summary>
    public DmClassRules Classes { get; init; } = DmClassRules.Balanced;
    /// <summary>Announce to the master server (<see cref="Master"/>; docs/multiplayer.md "Master server").</summary>
    public bool Public { get; init; }
    /// <summary>The master server's address (host:port), for <see cref="Public"/>.</summary>
    public string Master { get; init; } = "";
    /// <summary>Answer LAN browsers on <see cref="DmProtocol.DiscoveryPort"/>.</summary>
    public bool Discovery { get; init; } = true;
    /// <summary>Sent to every joiner as a chat line from the server ("" none).</summary>
    public string Motd { get; init; } = "";
    /// <summary>The log file ("" none); a relative path is beside the config file.</summary>
    public string LogFile { get; init; } = "logs/server.log";
    /// <summary>The ban list ("" none: bans last until the server stops); relative to the config.</summary>
    public string BanFile { get; init; } = "bans.json";
    /// <summary>Record every round as a network demo (docs/demos.md "Network demos") into <see cref="DemoFolder"/>.</summary>
    public bool RecordDemos { get; init; }
    /// <summary>Where the round demos go; a relative path is beside the config file.</summary>
    public string DemoFolder { get; init; } = "demos";
    /// <summary>Seconds without movement or action before a client is dropped (0: never).</summary>
    public double IdleKickSeconds { get; init; } = 300;
    /// <summary>Seconds without a packet before a connection is given up.</summary>
    public double TimeoutSeconds { get; init; } = 10;
    /// <summary>Seconds the round's end stays up before the next map.</summary>
    public double IntermissionSeconds { get; init; } = 10;
    public int Bots { get; init; }
    public int BotSkill { get; init; } = 1;
    /// <summary>Defaults for every rotation entry.</summary>
    public DmGoal? Goal { get; init; }
    public int? GoalLimit { get; init; }
    public int TimeLimitMinutes { get; init; } = 10;
    public ImmutableArray<RotationEntry> Rotation { get; init; } = [new RotationEntry()];
    /// <summary>The folder relative paths are taken from (the config file's).</summary>
    public string BaseFolder { get; init; } = Environment.CurrentDirectory;

    public static readonly ImmutableArray<string> Keys =
    [
        "name", "port", "bind", "max_players", "max_per_address", "password", "password_sha256", "cheats", "classes", "public", "master",
        "discovery", "motd", "log_file", "ban_file", "record_demos", "demo_dir", "idle_kick_seconds", "timeout_seconds", "intermission_seconds", "bots",
        "bot_skill", "goal", "goal_limit", "time", "rotation",
    ];

    public static readonly ImmutableArray<string> EntryKeys =
        ["map", "goal", "goal_limit", "time", "bots", "bot_skill", "max_players", "replenish", "items"];

    /// <summary>Reads <paramref name="path"/>; <see cref="JsonDataException"/> (or an IO exception)
    /// when it cannot. <see cref="Describe"/> words either for a person.</summary>
    public static ServerConfig Load(string path)
    {
        var bytes = File.ReadAllBytes(path);
        return Parse(bytes) with { BaseFolder = Path.GetDirectoryName(Path.GetFullPath(path)) ?? Environment.CurrentDirectory };
    }

    /// <summary>A load failure as one line: the file, then where in it and what is wrong.</summary>
    public static string Describe(string path, Exception e) => e switch
    {
        JsonDataException j => $"{path}: {j.Message}",
        FileNotFoundException or DirectoryNotFoundException => $"{path}: no such file",
        _ => $"{path}: {e.Message}",
    };

    public static ServerConfig Parse(ReadOnlySpan<byte> json)
    {
        var root = JsonItem.Parse(json, lenient: true).AsObject();
        Unknown(root, Keys);
        var c = new ServerConfig();
        if (root.Opt("name") is { } name)
        {
            c = c with { Name = Text(name, DmProtocol.MaxServerNameLength) };
        }

        if (root.Opt("port") is { } port)
        {
            c = c with { Port = port.AsInt(0, 65535) };
        }

        if (root.Opt("bind") is { } bind)
        {
            var text = bind.AsString().Trim();
            c = c with { Bind = text.Length == 0 ? null : IPAddress.TryParse(text, out var a) ? a : throw bind.Error($"expected an IP address, found \"{text}\"") };
        }

        if (root.Opt("max_players") is { } mp)
        {
            c = c with { MaxPlayers = mp.AsInt(1, DmProtocol.MaxPeers) };
        }

        if (root.Opt("max_per_address") is { } mpa)
        {
            c = c with { MaxPerAddress = mpa.AsInt(0, DmProtocol.MaxPeers) };
        }

        if (root.Opt("password") is { } pw && root.Opt("password_sha256") is not null)
        {
            throw pw.Error("give \"password\" or \"password_sha256\", not both");
        }

        if (root.Opt("password") is { } password)
        {
            var text = password.AsString();
            c = c with { PasswordKey = text.Length > 0 ? DmProtocol.PasswordKey(text) : null };
        }

        if (root.Opt("password_sha256") is { } hash)
        {
            var text = hash.AsString().Trim();
            byte[] key;
            try
            {
                key = Convert.FromHexString(text);
            }
            catch (FormatException)
            {
                throw hash.Error("expected 64 hex digits (SkyNet --password-hash TEXT prints them)");
            }

            c = c with { PasswordKey = key.Length == 32 ? key : throw hash.Error("expected 64 hex digits (SkyNet --password-hash TEXT prints them)") };
        }

        c = c with
        {
            Cheats = root.Opt("cheats")?.AsBool() ?? c.Cheats,
            Classes = root.Opt("classes") is { } classes ? RulesOf(classes) : c.Classes,
            Public = root.Opt("public")?.AsBool() ?? c.Public,
            Master = root.Opt("master")?.AsString().Trim() ?? c.Master,
            Discovery = root.Opt("discovery")?.AsBool() ?? c.Discovery,
            Motd = root.Opt("motd") is { } motd ? Text(motd, DmProtocol.MaxChatLength) : c.Motd,
            LogFile = root.Opt("log_file")?.AsString().Trim() ?? c.LogFile,
            BanFile = root.Opt("ban_file")?.AsString().Trim() ?? c.BanFile,
            RecordDemos = root.Opt("record_demos")?.AsBool() ?? c.RecordDemos,
            DemoFolder = root.Opt("demo_dir") is { } dir ? (dir.AsString().Trim() is { Length: > 0 } d ? d : throw dir.Error("expected a folder")) : c.DemoFolder,
            IdleKickSeconds = root.Opt("idle_kick_seconds") is { } idle ? Seconds(idle, 0, 86400) : c.IdleKickSeconds,
            TimeoutSeconds = root.Opt("timeout_seconds") is { } to ? Seconds(to, 2, 120) : c.TimeoutSeconds,
            IntermissionSeconds = root.Opt("intermission_seconds") is { } im ? Seconds(im, 0, 600) : c.IntermissionSeconds,
            Bots = root.Opt("bots")?.AsInt(0, 12) ?? c.Bots,
            BotSkill = root.Opt("bot_skill")?.AsInt(0, 2) ?? c.BotSkill,
            Goal = root.Opt("goal") is { } goal ? GoalOf(goal) : c.Goal,
            GoalLimit = root.Opt("goal_limit")?.AsInt(0, 99999) ?? c.GoalLimit,
            TimeLimitMinutes = root.Opt("time")?.AsInt(0, 180) ?? c.TimeLimitMinutes,
        };
        if (root.Opt("rotation") is { } rotation)
        {
            var list = rotation.AsArray().Items.Select(Entry).ToImmutableArray();
            c = c with { Rotation = list.Length > 0 ? list : throw rotation.Error("the rotation needs at least one map") };
        }

        return c;
    }

    private static RotationEntry Entry(JsonItem e)
    {
        if (e.Kind == JsonKind.String)
        {
            return new RotationEntry { Map = MapOf(e) };
        }

        e.AsObject();
        Unknown(e, EntryKeys);
        var items = e.Opt("items") is { } it ? Items(it) : null;
        return new RotationEntry
        {
            Map = MapOf(e.Member("map")),
            Goal = e.Opt("goal") is { } g ? GoalOf(g) : null,
            GoalLimit = e.Opt("goal_limit")?.AsInt(0, 99999),
            TimeLimitMinutes = e.Opt("time")?.AsInt(0, 180),
            Bots = e.Opt("bots")?.AsInt(0, 12),
            BotSkill = e.Opt("bot_skill")?.AsInt(0, 2),
            MaxPlayers = e.Opt("max_players")?.AsInt(1, DmProtocol.MaxPeers),
            Replenish = e.Opt("replenish")?.AsBool(),
            Items = items,
        };
    }

    private static ImmutableDictionary<string, int> Items(JsonItem it)
    {
        var b = ImmutableDictionary.CreateBuilder<string, int>(StringComparer.Ordinal);
        foreach (var (key, value) in it.AsObject().Members)
        {
            if (!NetLevelList.ItemKeys.Contains(key))
            {
                throw value.Error($"unknown item \"{key}\" (one of {string.Join(", ", NetLevelList.ItemKeys)})");
            }

            b[key] = value.AsInt(0, 999);
        }

        return b.ToImmutable();
    }

    private static int MapOf(JsonItem e) =>
        e.Kind == JsonKind.Number ? NetLevelList.ParseMapName(e.AsInt().ToString(CultureInfo.InvariantCulture)) ?? throw e.Error("expected a map like \"MAP.605\"")
        : NetLevelList.ParseMapName(e.AsString()) ?? throw e.Error($"expected a map like \"MAP.605\", found \"{e.AsString()}\"");

    private static DmGoal GoalOf(JsonItem e)
    {
        var word = e.AsString().Trim().ToLowerInvariant();
        return word is "points" or "kills" or "lives" or "time" ? DmSettings.ParseGoal(word) : throw e.Error($"expected points, kills, lives or time, found \"{word}\"");
    }

    private static DmClassRules RulesOf(JsonItem e) =>
        DmProtocol.ParseRules(e.AsString()) ?? throw e.Error($"expected dos or balanced, found \"{e.AsString().Trim()}\"");

    private static double Seconds(JsonItem e, double min, double max)
    {
        var v = e.AsDouble();
        return double.IsFinite(v) && v >= min && v <= max ? v : throw e.Error(string.Create(CultureInfo.InvariantCulture, $"expected seconds {min}-{max}, found {v}"));
    }

    private static string Text(JsonItem e, int max) => DmProtocol.CleanText(e.AsString(), max);

    private static void Unknown(JsonItem obj, ImmutableArray<string> known)
    {
        foreach (var (key, value) in obj.Members)
        {
            if (!known.Contains(key))
            {
                throw value.Error($"unknown key \"{key}\"");
            }
        }
    }

    /// <summary>A path of this config (the log, the bans) made absolute from its folder.</summary>
    public string Resolve(string path) => Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(BaseFolder, path));

    /// <summary>The run options (and settings) of rotation entry <paramref name="index"/>.</summary>
    public DmRunOptions OptionsFor(int index)
    {
        var e = Rotation[((index % Rotation.Length) + Rotation.Length) % Rotation.Length];
        return new DmRunOptions
        {
            HostMap = e.Map, Port = Port, Name = Name, Bind = Bind, Discovery = Discovery,
            Bots = e.Bots ?? Bots, BotSkill = e.BotSkill ?? BotSkill, TimeLimitMinutes = e.TimeLimitMinutes ?? TimeLimitMinutes,
            Goal = e.Goal ?? Goal, GoalLimit = e.GoalLimit ?? GoalLimit, Cheats = Cheats, Classes = Classes,
            MaxPlayers = e.MaxPlayers ?? MaxPlayers, Replenish = e.Replenish, Items = e.Items,
        };
    }

    /// <summary>The server's settings for rotation entry <paramref name="index"/> on <paramref name="level"/>.</summary>
    public DmSettings SettingsFor(int index, NetLevel level)
    {
        var o = OptionsFor(index);
        var items = level.Items;
        if (o.Items != null)
        {
            // The entry's counts over the arena's own: a category it leaves out keeps NETLEVEL's.
            items = items.SetItems(o.Items);
        }

        return (o with { Items = items }).SettingsFor(level) with { ServerName = Name };
    }

    /// <summary>The host screen's choices as a one-map server (the menu's DEDICATED).</summary>
    public static ServerConfig FromOptions(DmRunOptions o, string baseFolder) => new()
    {
        Name = DmProtocol.CleanText($"{o.Name}'s server", DmProtocol.MaxServerNameLength),
        Port = o.Port, Bind = o.Bind, Discovery = o.Discovery, Cheats = o.Cheats, Classes = o.Classes,
        MaxPlayers = Math.Clamp(o.MaxPlayers is int m and > 0 ? m : 8, 1, DmProtocol.MaxPeers),
        Bots = o.Bots, BotSkill = o.BotSkill, Goal = o.Goal, GoalLimit = o.GoalLimit, TimeLimitMinutes = o.TimeLimitMinutes,
        PasswordKey = string.IsNullOrEmpty(o.Password) ? null : DmProtocol.PasswordKey(o.Password),
        Public = o.Public, Master = o.Master ?? "",
        Rotation = [new RotationEntry { Map = o.HostMap ?? 605, Replenish = o.Replenish, Items = o.Items }],
        LogFile = "", BanFile = "", IdleKickSeconds = 0, BaseFolder = baseFolder,
    };
}
