using System.Globalization;
using System.Text;
using RetroEng.Content;
using RetroEng.World;
using SkyNet.Formats;
using SkyNet.Game.Audio;
using SkyNet.Game.Campaign;
using SkyNet.Game.Mods;

namespace SkyNet.Edit;

/// <summary>File > New Mission's choices (editor plan step T7): its number, its maps in play order and
/// the objective its briefing states.</summary>
public sealed record NewMissionSpec(int Number, IReadOnlyList<NewMapSpec> Maps, string Objective = NewMissions.DefaultObjective);

/// <summary>
/// Makes a mission the game lists and can finish (<see cref="ModMissions"/>): each map made by
/// <see cref="NewMaps"/>, every map but the last ending in a doorway to the next (arrival set 0), the
/// last in the objective pad; the mission file <c>missions/MNN.json</c> (one instance per map, the
/// doorways as links, play on foot from the first map's start) and the briefing
/// <c>briefings/NNN.TXT</c> whose [M1] entry is the one objective the pad counts.
/// <code>
///   MAP.720 start → doorway ─link→ MAP.721 (set 0) → … → MAP.72N pad: objective 1 of 1 → MISSION COMPLETE
/// </code>
/// </summary>
public static class NewMissions
{
    public const string DefaultObjective = "Reach the exit.";
    private const string DoneText = "Objective reached.";
    private const int Arrival = 0;

    /// <summary>The mission numbers in use: the data set's table and every mod's mission file.</summary>
    public static SortedSet<int> Taken(GameKind game, VirtualFileSystem? mods)
    {
        var taken = new SortedSet<int>(MissionTable.For(game).Missions.Select(m => m.Number));
        foreach (var path in mods?.Enumerate("missions/") ?? [])
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (name.Length > 1 && int.TryParse(name.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var n))
            {
                taken.Add(n);
            }
        }

        return taken;
    }

    /// <summary>The first mission number of the mod range not in <paramref name="taken"/>; −1 when full.</summary>
    public static int FirstFree(IReadOnlySet<int> taken) =>
        Enumerable.Range(ModMissions.FirstNew, ModMissions.LastNew - ModMissions.FirstNew + 1).FirstOrDefault(n => !taken.Contains(n), -1);

    /// <summary>Why <paramref name="spec"/> cannot be made (null: it can).</summary>
    public static string? Refuse(NewMissionSpec spec, GameData data, IReadOnlySet<int> takenMissions, IReadOnlySet<int> takenMaps)
    {
        if (spec.Number is < ModMissions.FirstNew or > ModMissions.LastNew)
        {
            return $"mission {spec.Number}: new missions take {ModMissions.FirstNew}-{ModMissions.LastNew}";
        }

        if (takenMissions.Contains(spec.Number))
        {
            return $"mission {spec.Number} exists (the table or a mod has it)";
        }

        if (spec.Maps.Count == 0)
        {
            return "a mission needs a map";
        }

        if (spec.Maps.Select(m => m.Number).Distinct().Count() != spec.Maps.Count)
        {
            return "two maps share a number";
        }

        return spec.Maps.Select(m => NewMaps.Refuse(m, data, takenMaps)).FirstOrDefault(r => r != null);
    }

    /// <summary>Writes the mission's maps, file and briefing into the folder mod <paramref name="modFolder"/>;
    /// the paths written.</summary>
    public static IReadOnlyList<string> Write(GameData data, string modFolder, NewMissionSpec spec)
    {
        var written = new List<string>();
        var instances = new List<WorldInstance>();
        var links = new List<WorldLink>();
        for (var i = 0; i < spec.Maps.Count; i++)
        {
            var last = i == spec.Maps.Count - 1;
            var map = spec.Maps[i];
            var next = last ? 0 : spec.Maps[i + 1].Number;
            var files = NewMaps.Build(data, map, last ? MapEnd.Objective : MapEnd.Doorway, next);
            written.AddRange(NewMaps.Write(modFolder, files));

            var id = MissionFile.DefaultId(map.Number);
            var props = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [MissionFile.MusicProperty] = MusicTracks.ForMap(MapFile.Parse(files.Map)!),
                [MissionFile.VehicleProperty] = MissionFile.ModeName(PlayerMode.Foot),
            };
            instances.Add(new WorldInstance(id, MissionFile.MapName(map.Number), MissionFile.MapName(map.Number), null, props));
            if (!last)
            {
                links.Add(new WorldLink(id, files.Doorway, MissionFile.DefaultId(next), false, Arrival));
            }
        }

        var start = spec.Maps[0].Number;
        var briefing = BriefingName(start);
        var file = MissionFile.Create(data.Game, spec.Number, briefing, PlayerMode.Foot, null, null,
            new WorldStart(MissionFile.DefaultId(start), -1), instances, links);
        NewMaps.WriteFile(modFolder, MissionFile.PathOf(spec.Number), file.ToJson(), written);
        NewMaps.WriteFile(modFolder, ModMissions.BriefingPath(briefing), Briefing(spec.Objective), written);
        return written;
    }

    /// <summary>The briefing a mission starting on MAP.<paramref name="map"/> names: "NNN.TXT", as DOS's.</summary>
    public static string BriefingName(int map) => string.Create(CultureInfo.InvariantCulture, $"{map:D3}.TXT");

    /// <summary>The briefing: [EN] the objective, [M1] one entry (the counter's one objective).</summary>
    private static byte[] Briefing(string objective)
    {
        var text = string.Join("\r\n", "[EN]", objective, "%%%%", "[M1]", DoneText, "####", "%%%%", "");
        return Encoding.ASCII.GetBytes(text);
    }
}
