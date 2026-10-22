using System.Globalization;
using RetroEng.Json;
using RetroEng.World;
using SkyNet.Formats;

namespace SkyNet.Game.Campaign;

/// <summary>Mission 5's scripted torpedo flight as a mission file names it: on instance
/// <see cref="At"/> entered from <see cref="After"/>, the ride runs and ends on <see cref="To"/> at
/// marker set <see cref="Arrival"/> (<see cref="TorpedoRide"/>).</summary>
public sealed record MissionTorpedo(string At, string After, string To, int Arrival);

/// <summary>
/// A SkyNET or Future Shock mission as a world file (docs/formats/mission.md): the map instances
/// (block = the baked MAP.NNN, layer = the MAP file's records; a group names the place variant
/// maps share), the 0xF0 exits as links (source = the exit record's file offset, arrival = the
/// marker set), the start, and the game section: the mission number, the briefing the objective
/// counter comes from, the table's vehicle mode, the next mission and the torpedo ride. Each
/// instance carries its music and vehicle as properties.
/// </summary>
public sealed class MissionFile
{
    /// <summary>The game section's own version (inside a <see cref="WorldDefinition.Version"/> file).</summary>
    public const int GameVersion = 1;
    public const string MusicProperty = "music", VehicleProperty = "vehicle";

    private MissionFile(WorldDefinition world, GameKind game, int number, string? briefing, PlayerMode mode, int? next, MissionTorpedo? torpedo)
    {
        World = world;
        Game = game;
        Number = number;
        Briefing = briefing;
        Mode = mode;
        Next = next;
        Torpedo = torpedo;
    }

    public WorldDefinition World { get; }
    public GameKind Game { get; }
    public int Number { get; }
    /// <summary>The briefing in MDMDBRIF.BSA ("210.TXT"); its [M1]..[M5] entries are the counter. Null: none.</summary>
    public string? Briefing { get; }
    /// <summary>The mission table's mode (+0x08).</summary>
    public PlayerMode Mode { get; }
    /// <summary>The mission after this one; null at the campaign's end.</summary>
    public int? Next { get; }
    public MissionTorpedo? Torpedo { get; }

    /// <summary>The file's path in the system ZIP and in a mod.</summary>
    public static string PathOf(int mission) => string.Create(CultureInfo.InvariantCulture, $"missions/M{mission:D2}.json");

    /// <summary>"MAP.210" for map 210: how blocks and layers are named.</summary>
    public static string MapName(int map) => string.Create(CultureInfo.InvariantCulture, $"MAP.{map:D3}");

    /// <summary>The instance id the generator gives MAP.<paramref name="map"/>'s first instance (and an old save's map).</summary>
    public static string DefaultId(int map) => map.ToString(CultureInfo.InvariantCulture);

    /// <summary>MAP.NNN's number; −1 when <paramref name="name"/> is not one.</summary>
    public static int MapOf(string name) =>
        name.Length == 7 && name.StartsWith("MAP.", StringComparison.Ordinal)
        && int.TryParse(name.AsSpan(4), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : -1;

    /// <summary>The map whose records drive <paramref name="instance"/>: its layer's base map
    /// (<see cref="Mods.MapLayers.BaseOf"/>: "MAP.NNN", or a named layer in the mods); −1 when unknown.</summary>
    public static int LayerMap(WorldInstance instance) => Mods.MapLayers.BaseOf(instance.Layer);

    /// <summary>The map whose block <paramref name="instance"/> draws.</summary>
    public static int BlockMap(WorldInstance instance) => MapOf(instance.Block);

    /// <summary>The first instance whose layer is MAP.<paramref name="map"/>, null when none.</summary>
    public WorldInstance? InstanceOf(int map) => World.Instances.FirstOrDefault(i => LayerMap(i) == map);

    public static string? Music(WorldInstance instance) => instance.Property(MusicProperty);

    public static PlayerMode? Vehicle(WorldInstance instance) => instance.Property(VehicleProperty) is { } v ? ParseMode(v) : null;

    public static string ModeName(PlayerMode mode) => mode switch
    {
        PlayerMode.Jeep => "jeep",
        PlayerMode.Hk => "hk",
        _ => "foot",
    };

    private static PlayerMode ParseMode(string text) => text switch
    {
        "foot" => PlayerMode.Foot,
        "jeep" => PlayerMode.Jeep,
        "hk" => PlayerMode.Hk,
        _ => throw new WorldFormatException($"vehicle \"{text}\" is not foot, jeep or hk"),
    };

    public static string GameName(GameKind game) => game == GameKind.FutureShock ? "shock" : "skynet";

    /// <summary>Builds the file's world from its parts (the generator, tests).</summary>
    public static MissionFile Create(GameKind game, int number, string? briefing, PlayerMode mode, int? next, MissionTorpedo? torpedo,
        WorldStart start, IReadOnlyList<WorldInstance> instances, IReadOnlyList<WorldLink> links, string sourceHash = "",
        IReadOnlyList<WorldInput>? inputs = null)
    {
        var g = JsonItem.NewObject().Add("data", GameName(game)).Add("version", GameVersion).Add("mission", number);
        if (briefing != null)
        {
            g.Add("briefing", briefing);
        }

        g.Add("vehicle", ModeName(mode));
        if (next != null)
        {
            g.Add("next", next.Value);
        }

        if (torpedo != null)
        {
            g.Add("torpedo", JsonItem.NewObject().Add("at", torpedo.At).Add("after", torpedo.After).Add("to", torpedo.To)
                .Add("arrival", torpedo.Arrival));
        }

        var id = string.Create(CultureInfo.InvariantCulture, $"{GameName(game)}/M{number:D2}");
        return From(new WorldDefinition(id, start, instances, links, g, sourceHash, inputs));
    }

    /// <summary>Reads and validates a mission file; <see cref="WorldFormatException"/> when it is not a valid one.</summary>
    public static MissionFile Parse(ReadOnlySpan<byte> utf8) => From(WorldDefinition.Parse(utf8));

    /// <summary>The SkyNET reading of <paramref name="world"/>, validated.</summary>
    public static MissionFile From(WorldDefinition world)
    {
        var g = world.Game ?? throw new WorldFormatException($"{world.Id}: no \"game\" section");
        var game = Str(g, "data") switch
        {
            "skynet" => GameKind.SkyNet,
            "shock" => GameKind.FutureShock,
            var other => throw new WorldFormatException($"{world.Id}: game.data \"{other}\" is not skynet or shock"),
        };
        if (Int(g, "version") != GameVersion)
        {
            throw new WorldFormatException($"{world.Id}: game.version {Int(g, "version")}, this build reads {GameVersion}");
        }

        foreach (var i in world.Instances)
        {
            if (LayerMap(i) < 0 || BlockMap(i) < 0)
            {
                throw new WorldFormatException($"{world.Id}: instance \"{i.Id}\": block must be MAP.NNN and layer MAP.NNN or a layer file in the mods");
            }

            // A layer is a base MAP's records plus edits (maps/<layer>.layer.json), keyed as the
            // block's sections are (record file offsets): an instance draws its layer's base map's block.
            if (LayerMap(i) != BlockMap(i))
            {
                throw new WorldFormatException($"{world.Id}: instance \"{i.Id}\": layer {i.Layer} on block {i.Block} (the layer's base must be the block's map)");
            }

            _ = Vehicle(i);
        }

        MissionTorpedo? torpedo = null;
        if (g.Opt("torpedo") is { Kind: JsonKind.Object } t)
        {
            torpedo = new MissionTorpedo(Str(t, "at"), Str(t, "after"), Str(t, "to"), Int(t, "arrival"));
            foreach (var id in new[] { torpedo.At, torpedo.After, torpedo.To })
            {
                if (world.Instance(id) == null)
                {
                    throw new WorldFormatException($"{world.Id}: game.torpedo names unknown instance \"{id}\"");
                }
            }
        }

        return new MissionFile(world, game, Int(g, "mission"), g.Opt("briefing") is null ? null : Str(g, "briefing"),
            ParseMode(Str(g, "vehicle")), g.Opt("next") is null ? null : Int(g, "next"), torpedo);
    }

    public byte[] ToJson() => World.ToJson();

    private static string Str(JsonItem o, string key) =>
        o.Opt(key) is { } v && v.TryString(out var s) && s.Length > 0 ? s
            : throw new WorldFormatException(Where(o, key) + "missing or not a string");

    private static int Int(JsonItem o, string key) =>
        o.Opt(key) is { } v && v.TryInt(out var i) ? i
            : throw new WorldFormatException(Where(o, key) + "missing or not an integer");

    /// <summary>"$.game.key (line L, column C): " for a member of a parsed file, "game.key: " for one built in code.</summary>
    private static string Where(JsonItem o, string key)
    {
        var at = o[key] ?? o;
        var path = o.Line > 0 ? o.Path : "game";
        return at.Line > 0 ? $"{path}.{key} (line {at.Line}, column {at.Column}): " : $"{path}.{key}: ";
    }
}
