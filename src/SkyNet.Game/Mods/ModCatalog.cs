using RetroEng.Content;
using RetroEng.Json;

namespace SkyNet.Game.Mods;

/// <summary>
/// A mod's optional <c>mod.json</c> (docs/modding.md): what the MODS screen says about it. Every
/// key is optional and a string: <c>name</c>, <c>version</c>, <c>author</c>, <c>description</c>;
/// <c>comment</c> is free text that is not shown. An unknown key, a value that is not a string or
/// one longer than its limit is an error: the screen shows it in the mod's row and the mod still
/// loads (the file is only its label).
/// </summary>
public sealed record ModInfo(string? Name, string? Version, string? Author, string? Description)
{
    public const string FileName = "mod.json";
    public const int MaxBytes = 64 * 1024;
    public const int MaxName = 40;
    public const int MaxVersion = 16;
    public const int MaxAuthor = 40;
    public const int MaxDescription = 400;

    /// <summary>Reads a <c>mod.json</c>; <see cref="JsonDataException"/> (with the JSON path, the line and
    /// the column) when it is not JSON or not this format.</summary>
    public static ModInfo Parse(ReadOnlySpan<byte> json)
    {
        if (json.Length > MaxBytes)
        {
            throw new JsonDataException($"larger than {MaxBytes / 1024} KB", "$", 0, 0);
        }

        var root = JsonItem.Parse(json, lenient: true).AsObject();
        string? name = null, version = null, author = null, description = null;
        foreach (var (key, value) in root.Members)
        {
            switch (key)
            {
                case "name": name = Text(value, MaxName); break;
                case "version": version = Text(value, MaxVersion); break;
                case "author": author = Text(value, MaxAuthor); break;
                case "description": description = Text(value, MaxDescription); break;
                case "comment": value.AsString(); break;
                default: throw value.Error($"unknown key \"{key}\" (name, version, author, description, comment)");
            }
        }

        return new ModInfo(name, version, author, description);
    }

    /// <summary>A string value, line breaks and tabs made spaces, trimmed; null when empty.</summary>
    private static string? Text(JsonItem value, int max)
    {
        var s = value.AsString();
        if (s.Length > max)
        {
            throw value.Error($"longer than {max} characters");
        }

        s = string.Concat(s.Select(c => char.IsControl(c) ? ' ' : c)).Trim();
        return s.Length > 0 ? s : null;
    }
}

/// <summary>What a mod carries that the game reads.</summary>
[Flags]
public enum ModCarries
{
    None = 0,
    Textures = 1,
    Models = 2,
    Tables = 4,
    /// <summary>Weather (<c>weather/*.json</c>: a storm, rain).</summary>
    Weather = 8,
}

/// <summary>One mod of the mods folder as the MODS screen lists it.</summary>
/// <param name="Name">The mod's name (its folder or ZIP without <c>.zip</c>): what the settings and <c>--mod</c> name.</param>
/// <param name="System">A system mod (the baked maps of a data set): always on, below every user mod, locked.</param>
/// <param name="Folder">It has a folder <c>mods/NAME/</c>.</param>
/// <param name="Zip">It has a ZIP <c>mods/NAME.zip</c>.</param>
/// <param name="Info">Its <c>mod.json</c>; null when it has none or it does not read.</param>
/// <param name="Problem">Why its <c>mod.json</c> was not read, or why the mod does not open; null: none.</param>
public sealed record ModEntry(string Name, bool System, bool Folder, bool Zip, ModInfo? Info, string? Problem, ModCarries Carries)
{
    /// <summary>The name to show: mod.json's, else the folder's.</summary>
    public string Title => Info?.Name ?? Name;
}

/// <summary>The mods folder read for the MODS screen: every mod with its <c>mod.json</c> and what it carries.</summary>
public static class ModCatalog
{
    /// <summary>Every mod in <paramref name="modsDirectory"/>, by name; <paramref name="systemMods"/>
    /// are marked as system mods (not opened). A user mod that does not open is listed with the reason.</summary>
    public static IReadOnlyList<ModEntry> Scan(string modsDirectory, IEnumerable<string> systemMods)
    {
        var system = systemMods.ToHashSet(VfsPath.Comparer);
        IReadOnlyList<ModSource> found;
        try
        {
            found = new ContentRoot(modsDirectory).Discover();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        return found.Select(m => system.Contains(m.Name)
            ? new ModEntry(m.Name, true, m.Folder != null, m.Archives.Count > 0, null, null, ModCarries.None)
            : Read(m)).ToArray();
    }

    /// <summary>One user mod: opened, its <c>mod.json</c> read and its folders looked at.</summary>
    public static ModEntry Read(ModSource mod)
    {
        var (folder, zip) = (mod.Folder != null, mod.Archives.Count > 0);
        try
        {
            using var vfs = new VirtualFileSystem();
            vfs.Mount(ContentRoot.OpenMod(mod), 0, mod.Name);
            var carries = ModCarries.None;
            if (vfs.Enumerate("textures/").Count > 0)
            {
                carries |= ModCarries.Textures;
            }

            if (vfs.Enumerate("models/").Count > 0)
            {
                carries |= ModCarries.Models;
            }

            if (vfs.Enumerate("tables/").Any(p => p.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
            {
                carries |= ModCarries.Tables;
            }

            if (SkyNet.Game.Enhanced.Weather.Carried(vfs))
            {
                carries |= ModCarries.Weather;
            }

            ModInfo? info = null;
            string? problem = null;
            if (vfs.TryReadAllBytes(ModInfo.FileName) is { } bytes)
            {
                try
                {
                    info = ModInfo.Parse(bytes);
                }
                catch (JsonDataException e)
                {
                    problem = $"{ModInfo.FileName}: {e.Message}";
                }
            }

            return new ModEntry(mod.Name, false, folder, zip, info, problem, carries);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return new ModEntry(mod.Name, false, folder, zip, null, $"does not open: {e.Message}", ModCarries.None);
        }
    }
}

/// <summary>
/// The rules of the enabled-mods list (docs/modding.md, "The MODS screen"): the settings keep the
/// enabled user mods in load order (later wins); <c>--mod</c> replaces that list for one run; system
/// mods are never in it. The MODS screen shows the enabled mods highest first, then the others by
/// name, then the system mods.
/// </summary>
public static class ModOrder
{
    /// <summary>The most mods a list keeps.</summary>
    public const int MaxMods = 64;

    /// <summary>A mod name as the settings and <c>--mod</c> take it: one folder or ZIP name, not a path.</summary>
    public static bool IsName(string name) =>
        name.Length is > 0 and <= 128 && !name.StartsWith('.') && name.IndexOfAny(['/', '\\', ':', '*', '?', '"', '<', '>', '|']) < 0 &&
        !name.Any(char.IsControl);

    /// <summary>The names trimmed, those that are not names dropped, the first of each kept (ignoring case).</summary>
    public static List<string> Clean(IEnumerable<string> names)
    {
        var seen = new HashSet<string>(VfsPath.Comparer);
        return names.Select(n => n.Trim()).Where(n => IsName(n) && seen.Add(n)).Take(MaxMods).ToList();
    }

    /// <summary><c>--mod a,b</c>'s list.</summary>
    public static List<string> Parse(string commandLine) => Clean(commandLine.Split(','));

    /// <summary>The mods to mount, in load order: <paramref name="commandLine"/> (<c>--mod</c>) when
    /// given, else the settings' list; system mods are left out (they are always mounted, below).</summary>
    public static string[] Effective(IReadOnlyList<string> saved, IReadOnlyList<string>? commandLine, IEnumerable<string> systemMods)
    {
        var system = systemMods.ToHashSet(VfsPath.Comparer);
        return [.. Clean(commandLine ?? saved).Where(n => !system.Contains(n))];
    }

    /// <summary>Turns <paramref name="name"/> on (it goes last in the load order: it wins over every
    /// other) or off (it leaves the list).</summary>
    public static void Set(List<string> enabled, string name, bool on)
    {
        var at = enabled.FindIndex(n => VfsPath.Comparer.Equals(n, name));
        if (on && at < 0)
        {
            enabled.Add(name);
        }
        else if (!on && at >= 0)
        {
            enabled.RemoveAt(at);
        }
    }

    /// <summary>Moves an enabled mod one place: <paramref name="up"/> = higher on the screen = later in
    /// the load order (it wins over the one it passes). It swaps with the next enabled mod that is
    /// <paramref name="present"/> that way; names whose mod is gone stay where they are. False when it
    /// cannot move.</summary>
    public static bool Move(List<string> enabled, string name, bool up, Func<string, bool> present)
    {
        var at = enabled.FindIndex(n => VfsPath.Comparer.Equals(n, name));
        if (at < 0)
        {
            return false;
        }

        var step = up ? 1 : -1;
        for (var i = at + step; i >= 0 && i < enabled.Count; i += step)
        {
            if (present(enabled[i]))
            {
                (enabled[at], enabled[i]) = (enabled[i], enabled[at]);
                return true;
            }
        }

        return false;
    }

    /// <summary>The screen's rows: the enabled mods found, the last-loaded (winning) first; then the
    /// other user mods by name; then the system mods.</summary>
    public static IReadOnlyList<ModEntry> Rows(IReadOnlyList<string> enabled, IReadOnlyList<ModEntry> found)
    {
        var byName = found.ToDictionary(m => m.Name, VfsPath.Comparer);
        var on = enabled.Reverse().Select(n => byName.GetValueOrDefault(n)).OfType<ModEntry>().Where(m => !m.System).ToList();
        var onNames = on.Select(m => m.Name).ToHashSet(VfsPath.Comparer);
        var off = found.Where(m => !m.System && !onNames.Contains(m.Name)).OrderBy(m => m.Name, VfsPath.Comparer);
        return [.. on, .. off, .. found.Where(m => m.System).OrderBy(m => m.Name, VfsPath.Comparer)];
    }
}
