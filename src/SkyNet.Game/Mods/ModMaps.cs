using System.Globalization;
using RetroEng.Content;
using SkyNet.Formats;

namespace SkyNet.Game.Mods;

/// <summary>
/// The maps the mods add: <c>maps/MAP.NNN</c>, a MAP in the DOS format under a number the map
/// archive lacks (the highest mod that has it wins), behind <see cref="GameData.LoadMap"/>
/// (<see cref="GameData.MapResolver"/>, set when the mods mount) and the bake's <c>MAP.NNN</c>
/// input. A DOS map is never replaced: its layer edits it. Its ground is a mod's <c>maps/WLD.NNN</c>
/// (<see cref="MapTerrains"/>); its briefing <c>briefings/NNN.TXT</c> (<see cref="ModMissions"/>).
/// <para>New numbers: <see cref="FirstNew"/>..<see cref="LastNew"/> are free in both data sets
/// (SkyNET's maps run 200-696, Future Shock's 001-719 and 803, 900, 957).</para>
/// </summary>
public static class ModMaps
{
    /// <summary>The range the editor gives new maps: no SkyNET or Future Shock map has these numbers.</summary>
    public const int FirstNew = 720, LastNew = 799;

    private const string Folder = "maps/";
    private const string Prefix = "MAP.";
    private const int NameLength = 7;

    /// <summary>The map's path in a mod: <c>maps/MAP.NNN</c>.</summary>
    public static string PathOf(int map) => Folder + Campaign.MissionFile.MapName(map);

    /// <summary>The map numbers the mods in <paramref name="vfs"/> carry, ascending.</summary>
    public static IReadOnlyList<int> Numbers(VirtualFileSystem? vfs) =>
        vfs == null ? [] : [.. vfs.Enumerate(Folder).Select(p => MapOf(Path.GetFileName(p))).Where(n => n >= 0).Distinct().Order()];

    /// <summary>MAP.<paramref name="map"/> in <paramref name="vfs"/>; null when no mod has one that parses (logged).</summary>
    public static byte[]? Find(VirtualFileSystem? vfs, int map)
    {
        if (vfs?.TryReadAllBytes(PathOf(map)) is not { } bytes)
        {
            return null;
        }

        if (MapFile.Parse(bytes) == null)
        {
            RetroEng.Diagnostics.Log.Warn("Mods", $"{PathOf(map)} is not a MAP the game reads; left out");
            return null;
        }

        return bytes;
    }

    /// <summary>The number of a file name "MAP.NNN"; −1 for anything else ("MAP.210.layer.json" too).</summary>
    public static int MapOf(string name) =>
        name.Length == NameLength && name.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)
        && int.TryParse(name.AsSpan(Prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : -1;

    /// <summary>Installs the resolver for <paramref name="vfs"/> (none when no mod adds a map).</summary>
    internal static void Install(VirtualFileSystem? vfs)
    {
        GameData.MapResolver = Numbers(vfs).Count > 0 ? n => Find(vfs, n) : null;
    }
}
