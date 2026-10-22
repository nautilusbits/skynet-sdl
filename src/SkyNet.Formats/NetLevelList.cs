using System.Collections.Immutable;
using System.Globalization;

namespace SkyNet.Formats;

/// <summary>
/// One deathmatch arena of NETLEVEL.PRS: its description, map, player cap (0 = unlimited), time of
/// day, goal (time, points, kills or lives), the DOS scoring (points per kill and per hit, points
/// lost per death), the item counts the host scatters over the map's item markers, and whether
/// taken items come back (replenish).
/// </summary>
public sealed record NetLevel(
    string Name,
    int Map,
    int MaxPlayers,
    string TimeSetting,
    string Goal,
    int ScoringKill,
    int ScoringDeath,
    int ScoringHit,
    ImmutableDictionary<string, int> Items,
    bool Replenish)
{
    /// <summary>The map's file name, MAP.NNN.</summary>
    public string MapName => string.Create(CultureInfo.InvariantCulture, $"MAP.{Map:000}");

    public int Count(string item) => Items.GetValueOrDefault(item);
}

/// <summary>
/// NETLEVEL.PRS, the DOS network level list (a loose file next to the BSAs): one [levelN] section
/// per arena, <c>key = value</c> lines, ';' comments (also after a value). Ported from the Godot
/// port's net_levels.gd; a missing file gives its built-in MAP.600-609 list with 4 of each item.
/// </summary>
public static class NetLevelList
{
    public const string FileName = "NETLEVEL.PRS";

    /// <summary>The item keys, in the order the host places them.</summary>
    public static readonly ImmutableArray<string> ItemKeys =
        ["jeeps", "hks", "bullets", "energy", "armor", "health", "slugthrowers", "lasers", "plasmas", "launchers", "grenades", "rockets"];

    /// <summary>The weapon categories (placed on the weapon spots, marker 101/102).</summary>
    public static readonly ImmutableArray<string> WeaponKeys = ["slugthrowers", "lasers", "plasmas", "launchers"];

    /// <summary>The Godot port's list when the file is missing.</summary>
    public static readonly ImmutableArray<int> BuiltInMaps = [605, 601, 602, 603, 604, 600, 606, 609, 607, 608];

    public static ImmutableArray<NetLevel> Parse(ReadOnlySpan<byte> bytes)
    {
        var list = ImmutableArray.CreateBuilder<NetLevel>();
        Builder? cur = null;
        foreach (var raw in TextLines.Split(bytes))
        {
            var line = raw;
            var sc = line.IndexOf(';');
            if (sc >= 0)
            {
                line = TextLines.Strip(line[..sc]);
            }

            if (line.Length == 0)
            {
                continue;
            }

            if (line[0] == '[')
            {
                if (cur?.Build() is { } done)
                {
                    list.Add(done);
                }

                cur = new Builder();
                continue;
            }

            var eq = line.IndexOf('=');
            if (eq <= 0 || cur == null)
            {
                continue;
            }

            cur.Set(TextLines.Strip(line[..eq]).ToLowerInvariant(), TextLines.Strip(line[(eq + 1)..]));
        }

        if (cur?.Build() is { } last)
        {
            list.Add(last);
        }

        return list.ToImmutable();
    }

    /// <summary>The list from the data folder, or the built-in one without the file.</summary>
    public static ImmutableArray<NetLevel> Load(GameData data) =>
        data.ReadLoose(FileName) is { } b && Parse(b) is { Length: > 0 } list ? list : BuiltIn();

    public static ImmutableArray<NetLevel> BuiltIn() => [.. BuiltInMaps.Select(Default)];

    /// <summary>The Godot port's defaults for a map NETLEVEL.PRS does not list.</summary>
    public static NetLevel Default(int map) => new(
        string.Create(CultureInfo.InvariantCulture, $"MAP.{map:000}"), map, 0, "night", "kills", 500, 400, 100,
        ItemKeys.ToImmutableDictionary(k => k, _ => 4), true);

    /// <summary>The entry for <paramref name="map"/>, or the defaults.</summary>
    public static NetLevel For(IEnumerable<NetLevel> levels, int map) => levels.FirstOrDefault(l => l.Map == map) ?? Default(map);

    /// <summary>"MAP.605", "605" or "map.605" → 605; null otherwise.</summary>
    public static int? ParseMapName(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var t = text.Trim();
        if (t.StartsWith("MAP.", StringComparison.OrdinalIgnoreCase))
        {
            t = t[4..];
        }

        return int.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n is >= 0 and <= 999 ? n : null;
    }

    private sealed class Builder
    {
        // A key the section leaves out keeps the Godot port's default, 4.
        private readonly Dictionary<string, int> _items = ItemKeys.ToDictionary(k => k, _ => 4);
        private string _name = "";
        private int _map = -1;
        private int _maxPlayers;
        private string _time = "night";
        private string _goal = "kills";
        private int _kill = 500, _death = 400, _hit = 100;
        private bool _replenish = true;

        public void Set(string key, string value)
        {
            switch (key)
            {
                case "name": _name = value; break;
                case "map": _map = Int(value, -1); break;
                case "maxplayers": _maxPlayers = Int(value, 0); break;
                case "timesetting": _time = value.ToLowerInvariant(); break;
                case "goal": _goal = value.ToLowerInvariant(); break;
                case "scoring_kill": _kill = Int(value, _kill); break;
                case "scoring_death": _death = Int(value, _death); break;
                case "scoring_hit": _hit = Int(value, _hit); break;
                case "replenish": _replenish = value.ToLowerInvariant() is "yes" or "1" or "true"; break;
                default:
                    if (_items.ContainsKey(key))
                    {
                        _items[key] = Math.Max(0, Int(value, 0));
                    }

                    break;
            }
        }

        public NetLevel? Build() => _map < 0 ? null : new(
            _name.Length > 0 ? _name : string.Create(CultureInfo.InvariantCulture, $"MAP.{_map:000}"),
            _map, Math.Max(0, _maxPlayers), _time, _goal, _kill, _death, _hit, _items.ToImmutableDictionary(), _replenish);

        private static int Int(string v, int fallback) =>
            int.TryParse(v, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n) ? n : fallback;
    }
}
