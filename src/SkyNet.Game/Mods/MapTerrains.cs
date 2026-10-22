using RetroEng.Content;
using RetroEng.Diagnostics;
using SkyNet.Formats;

namespace SkyNet.Game.Mods;

/// <summary>
/// The mods' terrains: <c>maps/WLD.NNN</c>, a whole WLD in the DOS format standing in for the data
/// folder's (the highest mod that has it wins), behind <see cref="GameData.LoadWld"/>
/// (<see cref="GameData.WldResolver"/>, set when the mods mount) and the bake's <c>WLD.NNN</c> input
/// (<see cref="Baking.BakeInputs"/>), so the drawn ground, collision, spawns and the editor read it.
/// A file that does not read as a WLD is logged under [Mods] and the DOS one stays.
/// </summary>
public static class MapTerrains
{
    private const string Folder = "maps/";
    private const string Prefix = "WLD.";

    /// <summary>The terrain's path in a mod: <c>maps/WLD.NNN</c>.</summary>
    public static string PathOf(int map) => $"{Folder}{FileName(map)}";

    /// <summary>The DOS file name, <c>WLD.NNN</c>.</summary>
    public static string FileName(int map) => $"{Prefix}{map:D3}";

    /// <summary>MAP.<paramref name="map"/>'s terrain in <paramref name="vfs"/>; null when no mod has a valid one.</summary>
    public static byte[]? Find(VirtualFileSystem? vfs, int map)
    {
        if (vfs?.TryReadAllBytes(PathOf(map)) is not { } bytes)
        {
            return null;
        }

        if (WldTerrain.Parse(bytes, smoothMaterials: false) == null)
        {
            Log.Warn("Mods", $"{PathOf(map)} is not a WLD the game reads; the data folder's stays");
            return null;
        }

        return bytes;
    }

    /// <summary>The map number of an input name "WLD.NNN"; −1 for anything else.</summary>
    public static int MapOf(string name) =>
        name.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) && int.TryParse(name.AsSpan(Prefix.Length), out var n) && n >= 0 ? n : -1;

    /// <summary>Installs the resolver for <paramref name="vfs"/> (none when no mod has a terrain).</summary>
    internal static void Install(VirtualFileSystem? vfs)
    {
        var any = vfs?.Enumerate(Folder).Any(p => MapOf(Path.GetFileName(p)) >= 0) == true;
        GameData.WldResolver = any ? n => Find(vfs, n) : null;
    }
}
