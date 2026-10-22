using System.Globalization;

namespace SkyNet.Game.Mods;

/// <summary>One file of a mirrored mods tree: its path under the tree ('/'-separated), its size
/// and its last change (ms since 1970; 0 when the source does not say).</summary>
public readonly record struct MirrorFile(string Path, long Size, long Modified);

/// <summary>What a sync does: the files to copy (new or changed), the paths to delete (gone from
/// the source) and the files that stay as they are.</summary>
public sealed record MirrorPlan(IReadOnlyList<MirrorFile> Copy, IReadOnlyList<string> Delete, IReadOnlyList<MirrorFile> Keep);

/// <summary>
/// Mirrors a player's mods folder into the game's mods folder (Android: the folder picked with the
/// system's folder picker, copied into the app's storage). The game's mods folder also holds what
/// the game makes itself (the system mods' baked maps, <c>skynet.zip</c> / <c>shock.zip</c>, or a
/// generated hi-res mod), so the mirror only ever deletes what it copied: a manifest lists those
/// files with the source's size and time when copied.
/// <code>
///   source tree ──list──▶ Plan(source, manifest, exists) ──▶ copy new/changed, delete gone
///                                    ▲                                  │
///                                    └──────── manifest (owned) ◀───────┘
/// </code>
/// A source path under a system mod's name (a desktop folder carrying its own <c>skynet.zip</c>)
/// is never copied, and a system mod's path is never deleted.
/// </summary>
public static class ModMirror
{
    private const char Separator = '/';
    private const char Field = '\t';
    private static readonly string[] SystemSuffixes = ["", ".zip", ".pak"];

    /// <summary>The sync for <paramref name="source"/> over the files the last sync left
    /// (<paramref name="owned"/>); <paramref name="exists"/> says whether a path is in the game's
    /// mods folder (a copied file someone deleted is copied again).</summary>
    public static MirrorPlan Plan(IEnumerable<MirrorFile> source, IEnumerable<MirrorFile> owned,
        Func<string, bool> exists, IEnumerable<string> systemMods)
    {
        var system = systemMods.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var last = owned.Where(f => Valid(f.Path) && !Reserved(f.Path, system))
            .GroupBy(f => f.Path, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);
        var want = source.Where(f => Valid(f.Path) && !Reserved(f.Path, system))
            .GroupBy(f => f.Path, StringComparer.Ordinal)
            .Select(g => g.Last())
            .ToArray();

        // Unchanged: copied before with the same size and time, and still there.
        var copy = new List<MirrorFile>();
        var keep = new List<MirrorFile>();
        foreach (var file in want)
        {
            var same = last.TryGetValue(file.Path, out var was) && was == file && exists(file.Path);
            (same ? keep : copy).Add(file);
        }

        // Gone from the source: only files the mirror copied.
        var paths = want.Select(f => f.Path).ToHashSet(StringComparer.Ordinal);
        var delete = last.Keys.Where(p => !paths.Contains(p)).Order(StringComparer.Ordinal).ToArray();
        return new MirrorPlan(copy, delete, keep);
    }

    /// <summary>True for a path under a system mod: <c>skynet/...</c>, <c>skynet.zip</c>, <c>skynet.pak</c>.</summary>
    public static bool Reserved(string path, IReadOnlySet<string> systemMods)
    {
        var top = path.Split(Separator)[0];
        return systemMods.Any(m => SystemSuffixes.Any(s => top.Equals(m + s, StringComparison.OrdinalIgnoreCase)));
    }

    // A relative path with no empty, "." or ".." part (nothing outside the mods folder) and no
    // character the manifest's lines use.
    private static bool Valid(string path) =>
        path.Length > 0 && path.IndexOfAny(['\\', Field, '\n', '\r']) < 0
        && path.Split(Separator).All(p => p is not ("" or "." or ".."));

    /// <summary>The manifest's text: one "size, time, path" line per file, tab-separated.</summary>
    public static string Write(IEnumerable<MirrorFile> files) =>
        string.Concat(files.Select(f => string.Create(CultureInfo.InvariantCulture, $"{f.Size}{Field}{f.Modified}{Field}{f.Path}\n")));

    /// <summary>The files a manifest lists; a line that does not read is skipped (its file is then
    /// not the mirror's: kept, never deleted).</summary>
    public static IReadOnlyList<MirrorFile> Read(string text)
    {
        var files = new List<MirrorFile>();
        foreach (var line in text.Split('\n'))
        {
            var parts = line.Split(Field, 3);
            if (parts.Length != 3
                || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var size)
                || !long.TryParse(parts[1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var modified))
            {
                continue;
            }

            files.Add(new MirrorFile(parts[2], size, modified));
        }

        return files;
    }
}
