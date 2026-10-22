using SkyNet.Game.Mods;

namespace SkyNet.Edit;

/// <summary>Where File > Save writes (editor plan step E6): a folder mod <c>mods/&lt;name&gt;/</c>; the
/// system mods and every ZIP mod are read-only.</summary>
public static class SaveTarget
{
    public const string ZipReadOnly = "zip mods are read-only, save as a folder mod";

    /// <summary>The folder of mod <paramref name="name"/> in <paramref name="modsDir"/>; null with the
    /// reason in <paramref name="error"/> when the editor may not write there.</summary>
    public static string? Folder(string modsDir, string name, IEnumerable<string> systemMods, out string? error)
    {
        name = name.Trim();
        error = null;
        if (!ModOrder.IsName(name))
        {
            error = $"\"{name}\" is not a mod name (one folder name, no path)";
            return null;
        }

        if (systemMods.Contains(name, StringComparer.OrdinalIgnoreCase) || File.Exists(Path.Combine(modsDir, name + ".zip")))
        {
            error = $"{name}: {ZipReadOnly}";
            return null;
        }

        return Path.Combine(modsDir, name);
    }

    /// <summary>The mod name a folder picked in Save As's Browse stands for: <paramref name="current"/> when it
    /// is the mods folder itself, the folder's name when it is a folder mod in it; null with the reason in
    /// <paramref name="error"/> for anything else, a system mod included.</summary>
    public static string? Browsed(string modsDir, string picked, string current, IEnumerable<string> systemMods, out string? error)
    {
        error = null;
        var dir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(picked));
        var mods = Path.TrimEndingDirectorySeparator(Path.GetFullPath(modsDir));
        if (string.Equals(dir, mods, StringComparison.OrdinalIgnoreCase))
        {
            return current;
        }

        if (!string.Equals(Path.GetDirectoryName(dir), mods, StringComparison.OrdinalIgnoreCase))
        {
            error = $"{picked}: not a folder mod in {modsDir}";
            return null;
        }

        var name = Path.GetFileName(dir);
        return Folder(modsDir, name, systemMods, out error) != null ? name : null;
    }

    /// <summary>The mod a plain Save goes to: the last (winning) of <paramref name="enabled"/> when it is a
    /// folder mod; null when there is none (Save then asks for a name).</summary>
    public static string? Default(string modsDir, IReadOnlyList<string> enabled, IEnumerable<string> systemMods) =>
        enabled.Count > 0 && Folder(modsDir, enabled[^1], systemMods, out _) != null ? enabled[^1] : null;
}
