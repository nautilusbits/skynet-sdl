using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using RetroEng.Json;
using RetroEng.Net.Master;

namespace SkyNet.Master;

/// <summary>
/// master.json (docs/multiplayer.md "Master server"): every key optional, comments and trailing
/// commas allowed; an unknown key or a wrong value stops the master with the JSON path, line and
/// column.
/// </summary>
public static class MasterSettings
{
    public static readonly ImmutableArray<string> Keys =
    [
        "bind", "port", "max_servers", "max_servers_per_address", "heartbeat_seconds", "missed_beats", "challenge_seconds",
        "query_rate", "query_burst", "heartbeat_rate", "heartbeat_burst", "introduce_rate", "introduce_burst", "games", "log_file",
    ];

    /// <summary>The config and its log file ("" none; relative to the config's folder).</summary>
    public static (MasterConfig Config, string LogFile) Load(string path)
    {
        var (c, log) = Parse(File.ReadAllBytes(path));
        if (log.Length > 0 && !Path.IsPathRooted(log))
        {
            log = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".", log));
        }

        return (c, log);
    }

    public static (MasterConfig Config, string LogFile) Parse(ReadOnlySpan<byte> json)
    {
        var root = JsonItem.Parse(json, lenient: true).AsObject();
        foreach (var (key, value) in root.Members)
        {
            if (!Keys.Contains(key))
            {
                throw value.Error($"unknown key \"{key}\"");
            }
        }

        var c = new MasterConfig();
        if (root.Opt("bind") is { } bind)
        {
            var text = bind.AsString().Trim();
            c = c with { Bind = text.Length == 0 ? null : IPAddress.TryParse(text, out var a) ? a : throw bind.Error($"expected an IP address, found \"{text}\"") };
        }

        var games = c.Games;
        if (root.Opt("games") is { } g)
        {
            games = [.. g.AsArray().Items.Select(i => MasterProtocol.ValidGameId(i.AsString()) ? i.AsString() : throw i.Error("a game id is lower-case letters, digits, '-' and '_' (at most 16)"))];
        }

        c = c with
        {
            Port = root.Opt("port")?.AsInt(0, 65535) ?? c.Port,
            MaxServers = root.Opt("max_servers")?.AsInt(1, 1_000_000) ?? c.MaxServers,
            MaxServersPerAddress = root.Opt("max_servers_per_address")?.AsInt(1, 10_000) ?? c.MaxServersPerAddress,
            HeartbeatSeconds = Num(root, "heartbeat_seconds", 10, 3600) ?? c.HeartbeatSeconds,
            MissedBeats = root.Opt("missed_beats")?.AsInt(1, 100) ?? c.MissedBeats,
            ChallengeSeconds = Num(root, "challenge_seconds", 2, 600) ?? c.ChallengeSeconds,
            QueryRate = Num(root, "query_rate", 0.01, 10_000) ?? c.QueryRate,
            QueryBurst = Num(root, "query_burst", 1, 10_000) ?? c.QueryBurst,
            HeartbeatRate = Num(root, "heartbeat_rate", 0.01, 10_000) ?? c.HeartbeatRate,
            HeartbeatBurst = Num(root, "heartbeat_burst", 1, 10_000) ?? c.HeartbeatBurst,
            IntroduceRate = Num(root, "introduce_rate", 0.01, 10_000) ?? c.IntroduceRate,
            IntroduceBurst = Num(root, "introduce_burst", 1, 10_000) ?? c.IntroduceBurst,
            Games = games,
        };
        return (c, root.Opt("log_file")?.AsString().Trim() ?? "");
    }

    private static double? Num(JsonItem root, string key, double min, double max)
    {
        if (root.Opt(key) is not { } e)
        {
            return null;
        }

        var v = e.AsDouble();
        return double.IsFinite(v) && v >= min && v <= max ? v : throw e.Error(string.Create(CultureInfo.InvariantCulture, $"expected {min}-{max}, found {v}"));
    }
}
