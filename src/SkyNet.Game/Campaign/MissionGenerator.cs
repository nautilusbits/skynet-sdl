using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using RetroEng.World;
using SkyNet.Formats;
using SkyNet.Game.Audio;
using SkyNet.Game.Baking;
using SkyNet.Game.Behaviour;
using SkyNet.Game.Vehicles;

namespace SkyNet.Game.Campaign;

/// <summary>
/// Builds a <see cref="MissionFile"/> from the mission table and the player's MAP files: the start
/// map, then every map an 0xF0 exit reaches (target 0, "back", adds none), and MAP.254 behind the
/// torpedo ride when the mission holds MAP.250 and MAP.253. Each map is one instance (id = its
/// number, block and layer = MAP.NNN), each exit record a link; an exit to a map the data lacks
/// stays as a closed link (to null: refused when taken, as before).
/// <para>Variant maps (<see cref="Variants"/>, the Godot port's RulesSkynet.VARIANTS): the same
/// place re-authored for a later phase, its own MAP file and its own WLD (WLD.216 = WLD.217,
/// WLD.234 = WLD.235 = WLD.230 byte for byte). They are instances of one group (the base map's
/// number); each keeps its own block, because a block's collision, sprites and dynamic meshes are
/// keyed by its own records' file offsets.</para>
/// </summary>
public static class MissionGenerator
{
    /// <summary>Bump when what the generator writes changes for the same inputs (every mission file is then stale).</summary>
    public const string ToolVersion = "1";

    /// <summary>SkyNET's variant maps → the map whose place they re-author (Future Shock has none).</summary>
    public static readonly IReadOnlyDictionary<int, int> Variants = new Dictionary<int, int> { [216] = 210, [217] = 210, [234] = 230, [235] = 230 };

    /// <summary>The group an instance of MAP.<paramref name="map"/> belongs to: its base map's number, or null.</summary>
    public static string? GroupOf(GameKind game, int map, IReadOnlyCollection<int> maps)
    {
        if (game != GameKind.SkyNet)
        {
            return null;
        }

        if (Variants.TryGetValue(map, out var b))
        {
            return MissionFile.DefaultId(b);
        }

        return Variants.Any(v => v.Value == map && maps.Contains(v.Key)) ? MissionFile.DefaultId(map) : null;
    }

    /// <summary>The source hash of a mission file: the format, game-section and tool versions, the
    /// table row and the MD5 of the inputs' names and MD5s in name order.</summary>
    public static string SourceHashOf(Mission mission, int? next, IEnumerable<WorldInput> inputs)
    {
        var text = new StringBuilder();
        foreach (var i in inputs.OrderBy(i => i.Name, StringComparer.Ordinal))
        {
            text.Append(i.Name).Append('=').Append(i.Md5).Append('\n');
        }

        var md5 = Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(text.ToString())));
        return string.Create(CultureInfo.InvariantCulture,
            $"mission {WorldDefinition.Version}.{MissionFile.GameVersion} tool {ToolVersion} row {mission.Number},{mission.StartMap},{(int)mission.Mode},{next ?? 0} md5 {md5}");
    }

    /// <summary>The hash <paramref name="file"/>'s inputs give now (compare with its <see cref="WorldDefinition.SourceHash"/>).</summary>
    public static string CurrentSourceHash(BakeInputs inputs, Mission mission, int? next, IEnumerable<WorldInput> stored) =>
        SourceHashOf(mission, next, stored.Select(i => new WorldInput(i.Name, inputs.Md5Of(i.Name))));

    /// <summary>Builds every mission of <paramref name="data"/>'s table.</summary>
    public static IReadOnlyList<MissionFile> BuildAll(GameData data)
    {
        using var inputs = new BakeInputs(data);
        var table = MissionTable.For(data.Game);
        return [.. table.Missions.Select(m => Build(data, m, inputs))];
    }

    /// <summary>Builds <paramref name="mission"/>'s file from <paramref name="data"/>.</summary>
    public static MissionFile Build(GameData data, Mission mission, BakeInputs? shared = null)
    {
        var inputs = shared ?? new BakeInputs(data);
        try
        {
            return BuildWith(data, mission, inputs);
        }
        finally
        {
            if (shared == null)
            {
                inputs.Dispose();
            }
        }
    }

    private static MissionFile BuildWith(GameData data, Mission mission, BakeInputs inputs)
    {
        var table = MissionTable.For(data.Game);
        var used = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var files = new Dictionary<int, MapFile?>();
        MapFile? Open(int map)
        {
            if (!files.TryGetValue(map, out var f))
            {
                var name = MissionFile.MapName(map);
                var bytes = inputs.Read(name);
                used[name] = inputs.Md5Of(name);
                files[map] = f = bytes != null ? MapFile.Parse(bytes) : null;
            }

            return f;
        }

        // The maps in the order they are reached (breadth first, exits in record order).
        var order = new List<int>();
        var queue = new Queue<int>();
        void Reach(int map)
        {
            if (!order.Contains(map) && Open(map) != null)
            {
                order.Add(map);
                queue.Enqueue(map);
            }
        }

        Reach(mission.StartMap);
        while (true)
        {
            while (queue.TryDequeue(out var map))
            {
                foreach (var e in Exits(files[map]!))
                {
                    if (e.Param16 > 0)
                    {
                        Reach(e.Param16);
                    }
                }
            }

            if (order.Contains(TorpedoRide.Map) && order.Contains(TorpedoRide.From) && !order.Contains(TorpedoRide.ToMap) && Open(TorpedoRide.ToMap) != null)
            {
                Reach(TorpedoRide.ToMap);
                continue;
            }

            break;
        }

        var instances = new List<WorldInstance>();
        var links = new List<WorldLink>();
        foreach (var map in order)
        {
            var file = files[map]!;
            var props = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [MissionFile.MusicProperty] = MusicTracks.ForMap(file),
                [MissionFile.VehicleProperty] = MissionFile.ModeName(ModeOf(VehicleModes.ForMission(data.Game, mission, map))),
            };
            instances.Add(new WorldInstance(MissionFile.DefaultId(map), MissionFile.MapName(map), MissionFile.MapName(map),
                GroupOf(data.Game, map, order), props));
            foreach (var e in Exits(file))
            {
                var to = e.Param16 > 0 && order.Contains(e.Param16) ? MissionFile.DefaultId(e.Param16) : null;
                links.Add(new WorldLink(MissionFile.DefaultId(map), e.FileOffset, to, e.Param16 == 0, e.Param8));
            }
        }

        var torpedo = order.Contains(TorpedoRide.Map) && order.Contains(TorpedoRide.From) && order.Contains(TorpedoRide.ToMap)
            ? new MissionTorpedo(MissionFile.DefaultId(TorpedoRide.Map), MissionFile.DefaultId(TorpedoRide.From),
                MissionFile.DefaultId(TorpedoRide.ToMap), TorpedoRide.ToSet)
            : null;
        var next = table.After(mission)?.Number;
        var inputList = used.Select(u => new WorldInput(u.Key, u.Value)).ToArray();
        return MissionFile.Create(data.Game, mission.Number, string.Create(CultureInfo.InvariantCulture, $"{mission.StartMap:D3}.TXT"),
            mission.Mode, next, torpedo, new WorldStart(MissionFile.DefaultId(mission.StartMap), -1), instances, links,
            SourceHashOf(mission, next, inputList), inputList);
    }

    /// <summary>The map's 0xF0 exit records, in record order.</summary>
    private static IEnumerable<MapEntity> Exits(MapFile file) => file.Entities.Where(e => e.ActType == ActRules.Exit);

    private static PlayerMode ModeOf(VehicleMode mode) => mode switch
    {
        VehicleMode.Jeep => PlayerMode.Jeep,
        VehicleMode.Hk => PlayerMode.Hk,
        _ => PlayerMode.Foot,
    };

    /// <summary>The blocks several missions place, with the missions and instance ids (the shared interiors).</summary>
    public static IReadOnlyList<(string Block, IReadOnlyList<(int Mission, string Instance)> Uses)> SharedBlocks(IEnumerable<MissionFile> missions) =>
        [.. missions.SelectMany(m => m.World.Instances.Select(i => (i.Block, Use: (m.Number, i.Id))))
            .GroupBy(x => x.Block, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => (g.Key, (IReadOnlyList<(int, string)>)[.. g.Select(x => x.Use)]))];
}
