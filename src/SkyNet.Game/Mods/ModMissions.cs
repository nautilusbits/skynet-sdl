using System.Globalization;
using RetroEng.Content;
using RetroEng.Diagnostics;
using RetroEng.World;
using SkyNet.Formats;
using SkyNet.Game.Campaign;

namespace SkyNet.Game.Mods;

/// <summary>
/// The missions the mods add: <c>missions/MNN.json</c> (docs/formats/mission.md) under a number the
/// game's mission table lacks, of the data set's game. Each becomes a row after the table's
/// (<see cref="MissionTable.Playable"/>): its start instance's map, its vehicle. The game lists them
/// (the console's <c>mission</c>, <c>--mission N</c>, saves) but no campaign reaches them (a DOS
/// mission's <c>next</c> stays). A file that does not read is logged and left out. The briefing a
/// mission file names (its objective counter) is the mods' <c>briefings/NNN.TXT</c> when MDMDBRIF.BSA
/// lacks it. Read when the mods mount (<see cref="ModContent.Mount(VirtualFileSystem?, IReadOnlyList{string}?, bool)"/>).
/// <code>
///   mods/&lt;name&gt;/missions/M20.json      the world: instances, links, start
///                   maps/MAP.720, WLD.720  the maps it adds (ModMaps, MapTerrains)
///                   briefings/720.TXT     [M1]..[M5]: the objectives
/// </code>
/// </summary>
public static class ModMissions
{
    /// <summary>The numbers the editor gives new missions (SkyNET's table runs 1-8, Future Shock's 1-19).</summary>
    public const int FirstNew = 20, LastNew = 99;

    private const string Folder = "missions/";
    private const string BriefingFolder = "briefings/";

    private sealed record Added(GameKind Game, Mission Row, MissionFile File);

    private sealed record Snapshot(VirtualFileSystem? Files, IReadOnlyList<Added> Missions);

    private static Snapshot _mounted = new(null, []);

    /// <summary>The briefing's path in a mod: <c>briefings/NNN.TXT</c>.</summary>
    public static string BriefingPath(string name) => BriefingFolder + name;

    /// <summary>The mods' added missions of <paramref name="game"/>, by number.</summary>
    public static IReadOnlyList<Mission> For(GameKind game) => [.. Volatile.Read(ref _mounted).Missions.Where(m => m.Game == game).Select(m => m.Row)];

    /// <summary>Added mission <paramref name="number"/>'s file; null when no mod adds it.</summary>
    public static MissionFile? File(GameKind game, int number) =>
        Volatile.Read(ref _mounted).Missions.FirstOrDefault(m => m.Game == game && m.Row.Number == number)?.File;

    /// <summary>A briefing the mods carry (<c>briefings/NNN.TXT</c>); null when none has it.</summary>
    public static byte[]? Briefing(string name) => Volatile.Read(ref _mounted).Files?.TryReadAllBytes(BriefingPath(name));

    /// <summary>The mission rows of <paramref name="vfs"/>'s added missions (the files that read), with their files.</summary>
    internal static void Install(VirtualFileSystem? vfs)
    {
        var added = new List<Added>();
        foreach (var path in vfs?.Enumerate(Folder) ?? [])
        {
            if (Read(vfs!, path) is { } m)
            {
                added.Add(m);
            }
        }

        Volatile.Write(ref _mounted, new Snapshot(vfs, [.. added.OrderBy(m => m.Game).ThenBy(m => m.Row.Number)]));
    }

    // One mission file: added when it reads and its number is not the table's ("M01.json" of a mod
    // replaces DOS's mission 1 through the block store instead).
    private static Added? Read(VirtualFileSystem vfs, string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        if (!path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || name.Length < 2 || name[0] is not ('M' or 'm')
            || !int.TryParse(name.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var number))
        {
            return null;
        }

        try
        {
            var file = MissionFile.Parse(vfs.ReadAllBytes(path));
            if (MissionTable.For(file.Game).ByNumber(number) != null)
            {
                return null;
            }

            if (file.Number != number)
            {
                Log.Warn("Mods", $"{path} is mission {file.Number}, not {number}: left out");
                return null;
            }

            var start = file.World.Instance(file.World.Start.Instance)!;
            return new Added(file.Game, new Mission(number, MissionFile.LayerMap(start), file.Mode), file);
        }
        catch (Exception e) when (e is WorldFormatException or IOException or InvalidDataException)
        {
            Log.Warn("Mods", $"{path}: {e.Message}; left out");
            return null;
        }
    }
}
