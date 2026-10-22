using System.Globalization;
using RetroEng.Audio;
using RetroEng.Content;
using RetroEng.Json;
using SkyNet.Game.Audio;
using SkyNet.Game.Mods;

namespace SkyNet.Game.Enhanced;

/// <summary>What a weather file brings.</summary>
public enum WeatherKind { Storm, Rain, Clouds, Haze, Dust, Wind }

/// <summary>
/// A mod's weather over the outdoor maps in the enhanced look: every <c>weather/*.json</c> of the
/// mounted mods, each one entry of a <c>"kind"</c> (left out: the file's name, so <c>storm.json</c> is a
/// storm). Kinds combine (rain and a storm at once); a second file of a kind already read is left
/// out (logged). <c>weather/thunder.wav</c>, <c>weather/rain.wav</c> and <c>weather/wind.wav</c>
/// replace the sounds made in code. Rain brings an overcast deck (no star shows through it).
/// <code>
///   weather/storm.json  ─► StormTable   ─► Storm (flash, bolt, thunder)
///   weather/rain.json   ─► RainTable    ─► Rain  (streaks, hiss)
///   weather/clouds.json ─► CloudTable   ─► WeatherSky (the deck, the moon and stars hidden)
///   weather/haze.json   ─► HazeAirTable ─► WeatherSky (the depth cue's colour and reach, the horizon)
///   weather/dust.json   ─► DustTable    ─► Dust  (sheets, grit)
///   weather/wind.json   ─► WindTable    ─► Wind  (gusts: rain, dust, clouds, smoke; the howl)
/// </code>
/// Presentation only: nothing here touches the simulation.
/// </summary>
public sealed record Weather(StormTable? Storm, RainTable? Rain)
{
    /// <summary>The mods' folder of weather files.</summary>
    public const string Folder = "weather/";
    /// <summary>The mods' own sounds, in place of the ones made in code.</summary>
    public const string ThunderFile = "weather/thunder.wav", RainFile = "weather/rain.wav", WindFile = "weather/wind.wav";

    /// <summary>No weather.</summary>
    public static Weather None { get; } = new(null, null);

    /// <summary>The cloud deck (with rain: overcast).</summary>
    public CloudTable? Clouds { get; init; }

    /// <summary>The haze.</summary>
    public HazeAirTable? Haze { get; init; }

    /// <summary>The dust.</summary>
    public DustTable? Dust { get; init; }

    /// <summary>The wind.</summary>
    public WindTable? Wind { get; init; }

    /// <summary>Whether any kind is on.</summary>
    public bool Any => Storm != null || Rain != null || Clouds != null || Haze != null || Dust != null || Wind != null;

    /// <summary>The weather of <paramref name="files"/> (path, bytes; in path order): each read, the
    /// ones turned off left out, a file that does not read or repeats a kind reported to
    /// <paramref name="warn"/> and left out.</summary>
    public static Weather Parse(IEnumerable<(string Path, byte[] Bytes)> files, Action<string>? warn = null)
    {
        var weather = None;
        HashSet<WeatherKind> read = [];
        foreach (var (path, bytes) in files)
        {
            try
            {
                var o = JsonItem.Parse(bytes).AsObject();
                var kind = KindOf(o, path);
                if (!read.Add(kind))
                {
                    warn?.Invoke($"{path}: a second {Name(kind)}; left out");
                    continue;
                }

                weather = kind switch
                {
                    WeatherKind.Storm => weather with { Storm = StormTable.From(o) is { On: true } s ? s : null },
                    WeatherKind.Rain => weather with { Rain = RainTable.From(o) is { On: true } r ? r : null },
                    WeatherKind.Clouds => weather with { Clouds = CloudTable.From(o) is { On: true } c ? c : null },
                    WeatherKind.Haze => weather with { Haze = HazeAirTable.From(o) is { On: true } h ? h : null },
                    WeatherKind.Dust => weather with { Dust = DustTable.From(o) is { On: true } d ? d : null },
                    _ => weather with { Wind = WindTable.From(o) is { On: true } w ? w : null },
                };
            }
            catch (Exception e) when (e is JsonDataException or System.Text.Json.JsonException)
            {
                warn?.Invoke($"{path}: {e.Message}; left out");
            }
        }

        // Rain falls from an overcast deck: no star shows through it.
        if (weather.Rain != null)
        {
            weather = weather with { Clouds = (weather.Clouds ?? CloudTable.Defaults) with { Coverage = CloudTable.RainCoverage } };
        }

        return weather;
    }

    /// <summary>A file's kind: its <c>"kind"</c>, else its name's (<c>rain.json</c>: rain).</summary>
    public static WeatherKind KindOf(JsonItem o, string path)
    {
        if (o.Opt("kind") is { } k)
        {
            return Of(k.AsString()) ?? throw k.Error($"\"kind\" is one of {Kinds}");
        }

        return Of(Path.GetFileNameWithoutExtension(path)) ?? throw o.Error($"\"kind\" is missing (one of {Kinds})");
    }

    private const string Kinds = "\"storm\", \"rain\", \"clouds\", \"haze\", \"dust\", \"wind\"";

    private static WeatherKind? Of(string name) => name.ToLowerInvariant() switch
    {
        "storm" => WeatherKind.Storm,
        "rain" => WeatherKind.Rain,
        "clouds" => WeatherKind.Clouds,
        "haze" => WeatherKind.Haze,
        "dust" => WeatherKind.Dust,
        "wind" => WeatherKind.Wind,
        _ => null,
    };

    private static string Name(WeatherKind kind) => kind.ToString().ToLowerInvariant();

    /// <summary>A colour key: [r, g, b], each 0-1 (sRGB); null when left out.</summary>
    internal static System.Numerics.Vector3? Colour(JsonItem o, string name)
    {
        if (o.Opt(name) is not { } v)
        {
            return null;
        }

        var items = v.Items;
        if (items.Count != 3)
        {
            throw v.Error($"\"{name}\" is [r, g, b], each 0 to 1");
        }

        var c = new System.Numerics.Vector3((float)items[0].AsDouble(), (float)items[1].AsDouble(), (float)items[2].AsDouble());
        return c == System.Numerics.Vector3.Clamp(c, System.Numerics.Vector3.Zero, System.Numerics.Vector3.One)
            ? c : throw v.Error($"\"{name}\" is [r, g, b], each 0 to 1");
    }

    /// <summary>A number key within [min, max]; null when left out.</summary>
    internal static float? Ranged(JsonItem o, string name, float min, float max)
    {
        if (o.Opt(name) is not { } v)
        {
            return null;
        }

        var f = (float)v.AsDouble();
        return f >= min && f <= max ? f : throw v.Error(string.Create(CultureInfo.InvariantCulture, $"\"{name}\" must be {min} to {max}"));
    }

    // The mods read and what they gave, as one immutable reference: read from any thread.
    private sealed record Loaded(Weather Value, VirtualFileSystem? From);

    private static Loaded _box = new(None, null);

    /// <summary>The mounted mods' weather (the highest mod with a file wins it); <see cref="None"/>
    /// without files.</summary>
    public static Weather Current
    {
        get
        {
            var vfs = ModContent.Files;
            var cached = Volatile.Read(ref _box);
            if (!ReferenceEquals(vfs, cached.From))
            {
                cached = new Loaded(Load(vfs), vfs);
                Volatile.Write(ref _box, cached);
            }

            return cached.Value;
        }
    }

    /// <summary>Whether <paramref name="vfs"/> has any weather file.</summary>
    public static bool Carried(VirtualFileSystem vfs) => Files(vfs).Any();

    private static IEnumerable<string> Files(VirtualFileSystem vfs) =>
        vfs.Enumerate(Folder).Where(p => p.EndsWith(".json", StringComparison.OrdinalIgnoreCase));

    private static Weather Load(VirtualFileSystem? vfs)
    {
        if (vfs == null)
        {
            return None;
        }

        var files = Files(vfs).Select(p => (p, vfs.TryReadAllBytes(p) ?? [])).ToList();
        if (files.Count == 0)
        {
            return None;
        }

        var weather = Parse(files, w => RetroEng.Diagnostics.Log.Warn("Mods", w));
        if (weather.Storm != null && Recorded(vfs, ThunderFile, Looping.Once) is { } thunder)
        {
            weather = weather with { Storm = weather.Storm with { Recorded = thunder } };
        }

        if (weather.Rain != null && Recorded(vfs, RainFile, Looping.Forever) is { } hiss)
        {
            weather = weather with { Rain = weather.Rain with { Recorded = hiss } };
        }

        if (weather.Wind != null && Recorded(vfs, WindFile, Looping.Forever) is { } howl)
        {
            weather = weather with { Wind = weather.Wind with { Recorded = howl } };
        }

        RetroEng.Diagnostics.Log.Info("Mods", Describe(weather));
        return weather;
    }

    private static Sound? Recorded(VirtualFileSystem vfs, string path, Looping looping)
    {
        if (vfs.TryReadAllBytes(path) is not { } bytes)
        {
            return null;
        }

        var sound = SoundBank.DecodeWav(bytes, looping);
        if (sound == null)
        {
            RetroEng.Diagnostics.Log.Warn("Mods", $"{path}: not a PCM WAV (8 or 16 bits); the sound made in code plays");
        }

        return sound;
    }

    /// <summary>One line on what is on (the log, the console's <c>weather</c>).</summary>
    public static string Describe(Weather w)
    {
        var c = CultureInfo.InvariantCulture;
        List<string> parts = [];
        if (w.Storm is { } s)
        {
            parts.Add(string.Create(c, $"storm: a strike every {s.Interval} s on average, {s.NearMetres}-{s.FarMetres} m{(s.Recorded != null ? ", the mod's thunder" : "")}"));
        }

        if (w.Rain is { } r)
        {
            parts.Add(string.Create(c, $"rain: intensity {r.Intensity}, wind {r.WindSpeed} m/s towards {r.WindDirection} degrees{(r.Recorded != null ? ", the mod's hiss" : "")}"));
        }

        if (w.Clouds is { } k)
        {
            parts.Add(string.Create(c, $"clouds: coverage {k.Coverage}, {k.HeightMetres} m up"));
        }

        if (w.Haze is { } h)
        {
            parts.Add(string.Create(c, $"haze: visibility {h.VisibilityMetres} m"));
        }

        if (w.Dust is { } d)
        {
            parts.Add(string.Create(c, $"dust: amount {d.Amount}, streaks {d.Streaks}"));
        }

        if (w.Wind is { } i)
        {
            parts.Add(string.Create(c, $"wind: {i.Speed} m/s towards {i.Direction} degrees, gusts {i.Gusts}{(i.Recorded != null ? ", the mod's howl" : "")}"));
        }

        return parts.Count == 0 ? "weather: none" : "weather: " + string.Join("; ", parts);
    }
}
