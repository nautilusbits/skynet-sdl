using System.Security.Cryptography;
using SkyNet.Formats;

namespace SkyNet.Game.Storage;

/// <summary>One data archive as found: its name (upper case), size and SHA-256 (lower-case hex).</summary>
public sealed record DataFile(string Name, long Size, string Sha256);

/// <summary>A data folder checked against the known releases: its archives and the names that
/// differ from the nearest release (none: a known version).</summary>
public sealed record DataCheck(GameKind Game, IReadOnlyList<DataFile> Files, IReadOnlyList<string> Differs)
{
    public bool Known => Differs.Count == 0;

    /// <summary>Archives not hashed: over <see cref="DataVersions.MaxArchiveSize"/> or past
    /// <see cref="DataVersions.MaxArchives"/>. They count as differing.</summary>
    public IReadOnlyList<string> Skipped { get; init; } = [];
}

/// <summary>
/// Which release a data folder holds, by its MDMD*.BSA archives' SHA-256 (issue #20: another
/// Future Shock release drew wrong). Hashes are cached per game, name, size and last-write time,
/// so a start rehashes only a changed file.
/// <code>
///   folder ─► MDMD*.BSA ─► cached? ── yes ─► hash
///                             └─ no ─► SHA-256 ─► cache
///   hashes ─► nearest known release ─► names that differ (none: known)
/// </code>
/// </summary>
public static class DataVersions
{
    /// <summary>The cache file's name in the game's cache folder.</summary>
    public const string CacheName = "datahash.txt";

    /// <summary>The most archives a folder's check looks at (a release has 7 or 8).</summary>
    public const int MaxArchives = 32;

    /// <summary>The largest archive hashed (the largest known is about 13 MB).</summary>
    public const long MaxArchiveSize = 64L * 1024 * 1024;

    /// <summary>Archives a release may lack: MDMDHRES.BSA, the 640 x 480 HUD art (the game plays low-res).</summary>
    private static readonly HashSet<string> Optional = new(StringComparer.Ordinal) { "MDMDHRES.BSA" };

    private const string Prefix = "MDMD";
    private const string Extension = ".BSA";

    /// <summary>Fingerprints <paramref name="dir"/> (hashing only what <paramref name="cacheFile"/>
    /// lacks) and compares it with <paramref name="game"/>'s known releases.</summary>
    public static DataCheck Check(string dir, GameKind game, string cacheFile)
    {
        var cache = HashCache.Load(cacheFile, game);
        var files = new List<DataFile>();
        var skipped = new List<string>();
        foreach (var (name, path) in Archives(dir))
        {
            var info = new FileInfo(path);
            if (files.Count + skipped.Count >= MaxArchives || info.Length > MaxArchiveSize)
            {
                skipped.Add(name);
                continue;
            }

            files.Add(Fingerprint(name, info, cache));
        }

        cache.Save(cacheFile);
        var differs = Differs(files, KnownData.Of(game)).Union(skipped).Order(StringComparer.Ordinal).ToList();
        return new DataCheck(game, files, differs) { Skipped = skipped };
    }

    /// <summary>The fingerprint's length (hex digits: 64 bits).</summary>
    public const int FingerprintLength = 16;

    /// <summary>A short name for the data release <paramref name="files"/> hold: the first
    /// <see cref="FingerprintLength"/> hex digits of the SHA-256 of their names and hashes, by name,
    /// the optional archives left out (one release with or without them is the same data).
    /// Keys what is baked from the data (<see cref="Enhanced.IrradianceFiles"/>).</summary>
    public static string Fingerprint(IEnumerable<DataFile> files)
    {
        var lines = files.Where(f => !Optional.Contains(f.Name)).OrderBy(f => f.Name, StringComparer.Ordinal)
            .Select(f => $"{f.Name}\t{f.Sha256}\n");
        var hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Concat(lines)));
        return Convert.ToHexStringLower(hash)[..FingerprintLength];
    }

    /// <summary>The names that differ from the release with the fewest differences: changed,
    /// missing or extra, sorted. Every name when no release is known.</summary>
    internal static IReadOnlyList<string> Differs(IReadOnlyList<DataFile> files, IReadOnlyList<IReadOnlyList<DataFile>> releases)
    {
        if (releases.Count == 0)
        {
            return [.. files.Select(f => f.Name).Order(StringComparer.Ordinal)];
        }

        return releases.Select(r => Diff(files, r)).MinBy(d => d.Count)!;
    }

    // Names whose hash differs between the two sets, or that only one has (an optional one may be missing).
    private static List<string> Diff(IReadOnlyList<DataFile> files, IReadOnlyList<DataFile> release)
    {
        var found = files.ToDictionary(f => f.Name, f => f.Sha256);
        var known = release.ToDictionary(f => f.Name, f => f.Sha256);
        return [.. found.Keys.Union(known.Keys)
            .Where(n => !(Optional.Contains(n) && !found.ContainsKey(n)))
            .Where(n => !found.TryGetValue(n, out var a) || !known.TryGetValue(n, out var b) || a != b)
            .Order(StringComparer.Ordinal)];
    }

    // One listing of the folder: MDMD*.BSA, upper-case name → path, sorted by name. When two
    // spellings exist the upper-case DOS one wins (as GameDataLocator.FindFile).
    private static SortedDictionary<string, string> Archives(string dir)
    {
        var archives = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(dir))
        {
            var file = Path.GetFileName(path);
            var name = file.ToUpperInvariant();
            if (!name.StartsWith(Prefix, StringComparison.Ordinal) || !name.EndsWith(Extension, StringComparison.Ordinal))
            {
                continue;
            }

            if (!archives.ContainsKey(name) || file == name)
            {
                archives[name] = path;
            }
        }

        return archives;
    }

    // One archive's size and hash: the cache's when the file is unchanged, else read once.
    private static DataFile Fingerprint(string name, FileInfo info, HashCache cache)
    {
        var key = new HashCache.Key(name, info.Length, info.LastWriteTimeUtc.Ticks);
        if (cache.Find(key) is not { } hash)
        {
            using var stream = info.OpenRead();
            hash = Convert.ToHexStringLower(SHA256.HashData(stream));
        }

        cache.Use(key, hash);
        return new DataFile(name, info.Length, hash);
    }
}

/// <summary>
/// The hashes already made: one line per archive, <c>game TAB name TAB size TAB ticks TAB sha256</c>
/// (no paths: the file may travel with a player's report). One game's entries are read; on save
/// they are replaced by the ones used this run, the other games' lines kept as they were. Bad
/// lines are dropped, and a cache that is too large, unreadable or unwritable only costs a rehash.
/// </summary>
internal sealed class HashCache
{
    internal readonly record struct Key(string Name, long Size, long Ticks);

    private const char Tab = '\t';
    private const int Fields = 5;
    private const int HashLength = 64;
    private const long MaxFileSize = 64 * 1024;

    private readonly string _game;
    private readonly List<string> _otherGames = [];
    private readonly Dictionary<Key, string> _loaded = [];
    private readonly Dictionary<Key, string> _used = [];

    private HashCache(GameKind game) => _game = game.ToString();

    public static HashCache Load(string file, GameKind game)
    {
        var cache = new HashCache(game);
        string[] lines;
        try
        {
            var info = new FileInfo(file);
            lines = info.Exists && info.Length <= MaxFileSize ? File.ReadAllLines(file) : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return cache;
        }

        foreach (var line in lines)
        {
            cache.Read(line);
        }

        return cache;
    }

    // One line: this game's into the lookup, another game's kept for the save.
    private void Read(string line)
    {
        var f = line.Split(Tab);
        if (f.Length != Fields || !long.TryParse(f[2], out var size) || !long.TryParse(f[3], out var ticks) || !IsHash(f[4]))
        {
            return;
        }

        if (f[0] != _game)
        {
            _otherGames.Add(line);
            return;
        }

        _loaded[new Key(f[1], size, ticks)] = f[4];
    }

    // Exactly 64 lower-case hex digits.
    private static bool IsHash(string s) => s.Length == HashLength && s.All(char.IsAsciiHexDigitLower);

    public string? Find(Key key) => _loaded.GetValueOrDefault(key);

    /// <summary>A hash used this run: kept on save.</summary>
    public void Use(Key key, string hash) => _used[key] = hash;

    /// <summary>Writes the cache when this game's entries changed (a temporary file, then moved over).</summary>
    public void Save(string file)
    {
        if (_used.Count == _loaded.Count && _used.All(e => _loaded.TryGetValue(e.Key, out var h) && h == e.Value))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
            var temp = file + ".tmp";
            var ours = _used.Select(e => $"{_game}{Tab}{e.Key.Name}{Tab}{e.Key.Size}{Tab}{e.Key.Ticks}{Tab}{e.Value}");
            File.WriteAllLines(temp, _otherGames.Concat(ours));
            File.Move(temp, file, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Not cached: the next start hashes again.
        }
    }
}
