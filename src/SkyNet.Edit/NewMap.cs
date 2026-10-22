using System.Text;
using RetroEng.Content;
using SkyNet.Formats;
using SkyNet.Game.Audio;
using SkyNet.Game.Behaviour;
using SkyNet.Game.Campaign;
using SkyNet.Game.Mods;

namespace SkyNet.Edit;

/// <summary>How a new outdoor map's ground starts.</summary>
public enum TerrainStart { Flat, Generated }

/// <summary>What a new map ends in when a mission is made of it: nothing, a doorway to the next map, or
/// the mission's end (a proximity pad that counts the one objective).</summary>
public enum MapEnd { None, Doorway, Objective }

/// <summary>
/// File > New Map's choices (editor plan step T7). <see cref="Height"/> is a height-curve index
/// (docs/formats/wld.md), <see cref="Tile"/> a TEXTURE.302 record, <see cref="MapType"/> the maptype
/// marker's value (the song, MusicTracks), <see cref="Room"/> the interior whose walls an interior copies
/// (0: the data set's default). The sky follows the number as in DOS (night with the moon outside the
/// dusk decades 250-280).
/// </summary>
public sealed record NewMapSpec(int Number, MapSetting Setting)
{
    public int Size { get; init; } = WldTerrain.GridW;
    public TerrainStart Terrain { get; init; } = TerrainStart.Flat;
    public int Height { get; init; } = NewMaps.DefaultHeight;
    public int Seed { get; init; } = 1;
    public int Tile { get; init; }
    public int MapType { get; init; }
    public int Room { get; init; }
}

/// <summary>A new map's files: MAP.NNN, its WLD.NNN (outdoors) and the key of its doorway (−1: none).</summary>
public sealed record NewMapFiles(int Number, byte[] Map, byte[]? Wld, int Doorway);

/// <summary>
/// Makes new maps in the DOS format (MAP.NNN with <see cref="MapWriter"/>, WLD.NNN with <see cref="WldFile"/>)
/// for a mod's <c>maps/</c> (<see cref="ModMaps"/>, <see cref="MapTerrains"/>). A map holds the player
/// start (markers 0 and 1), the maptype marker and, in a mission, its end:
/// <code>
///   outdoor (flat or generated ground)          interior (another map's walls)
///      end ●   (start + 3072 north)                 end ● at its farthest spawn marker
///          |                                         start ● at its marker 0
///   start ● → facing marker 1
/// </code>
/// An end is a doorway (0xF0 sprite to the next map, arrival set 0, taken by touch or the use key) or
/// an objective pad (an 0xF1 sprite whose chain counts the briefing's one objective).
/// </summary>
public static class NewMaps
{
    public const int DefaultHeight = 20;
    /// <summary>The ground a generated field swings over, in height-curve steps either side.</summary>
    public const int GeneratedSwing = 24;
    /// <summary>How far the end stands ahead of the start outdoors, world units.</summary>
    public const int EndDistance = 3072;
    /// <summary>The flat ground kept round the start and the end of a generated field, world units.</summary>
    public const int FlatRadius = 768;
    /// <summary>The doorway sprite DOS's exits and gates use (TEXTURE.203 record 0).</summary>
    public const int DoorwaySprite = 203 << MapWriter.SpriteBankShift;

    /// <summary>TEXTURE.302's ground tiles (WLD layer 2 bits 0-5).</summary>
    public const int TileCount = 64;

    private const int StartMarker = 0, FacingMarker = 1, LastSpawnMarker = 99;
    private const int FacingStep = 256;
    /// <summary>A marker stands 16 units over its ground (DOS: ground = −(y + 16)).</summary>
    private const int MarkerLift = 16;
    private const int PadEyeLift = 60;
    private const int CellsPerMapCell = 4;
    private const double NoiseScale = 1.0 / 24.0;
    private const int NoiseOctaves = 4;

    /// <summary>The interiors a new interior copies by default: MAP.211 (SkyNET), MAP.011 (Future Shock).</summary>
    public static int DefaultRoom(GameKind game) => game == GameKind.FutureShock ? 11 : 211;

    /// <summary>The map numbers in use: the map archive's and every mod's in <paramref name="mods"/>.</summary>
    public static SortedSet<int> Taken(GameData data, VirtualFileSystem? mods)
    {
        using var archive = data.OpenMapArchive();
        var taken = new SortedSet<int>(archive.Entries.Select(e => ModMaps.MapOf(e.Name)).Where(n => n >= 0));
        taken.UnionWith(ModMaps.Numbers(mods));
        return taken;
    }

    /// <summary>The first number of the mod range not in <paramref name="taken"/>; −1 when the range is full.</summary>
    public static int FirstFree(IReadOnlySet<int> taken) =>
        Enumerable.Range(ModMaps.FirstNew, ModMaps.LastNew - ModMaps.FirstNew + 1).FirstOrDefault(n => !taken.Contains(n), -1);

    /// <summary>Why <paramref name="spec"/> cannot be made (null: it can).</summary>
    public static string? Refuse(NewMapSpec spec, GameData data, IReadOnlySet<int> taken)
    {
        if (spec.Number is < ModMaps.FirstNew or > ModMaps.LastNew)
        {
            return $"MAP.{spec.Number:D3}: new maps take {ModMaps.FirstNew}-{ModMaps.LastNew} (free in SkyNET and Future Shock)";
        }

        if (taken.Contains(spec.Number))
        {
            return $"MAP.{spec.Number:D3} exists (the data or a mod has it)";
        }

        if (spec.Setting == MapSetting.Interior)
        {
            var room = RoomOf(spec, data.Game);
            return data.LoadMap(room) is { IsOutdoor: false } ? null : $"MAP.{room:D3} is not an interior to copy";
        }

        if (spec.Size <= 0 || spec.Size % WldFile.ChunkCells != 0)
        {
            return $"size {spec.Size}: a multiple of {WldFile.ChunkCells}";
        }

        if (spec.Height < 0 || spec.Height >= WldTerrain.HeightCurve.Length || spec.Tile is < 0 or >= TileCount)
        {
            return "height 0-127, tile 0-63";
        }

        return WldTerrain.Parse(Ground(spec, []).Write()) == null ? $"size {spec.Size}: the game reads {WldTerrain.GridW} x {WldTerrain.GridH} ground only" : null;
    }

    /// <summary>The map's files. <paramref name="end"/> says what it ends in; a doorway leads to
    /// MAP.<paramref name="next"/>.</summary>
    public static NewMapFiles Build(GameData data, NewMapSpec spec, MapEnd end = MapEnd.None, int next = 0) =>
        spec.Setting == MapSetting.Outdoor ? Outdoor(spec, end, next) : Interior(data, spec, end, next);

    /// <summary>Writes <paramref name="files"/> into the folder mod <paramref name="modFolder"/>; the paths written.</summary>
    public static IReadOnlyList<string> Write(string modFolder, NewMapFiles files)
    {
        var written = Begin(modFolder);
        WriteFile(modFolder, ModMaps.PathOf(files.Number), files.Map, written);
        if (files.Wld != null)
        {
            WriteFile(modFolder, MapTerrains.PathOf(files.Number), files.Wld, written);
        }

        return written;
    }

    private static NewMapFiles Outdoor(NewMapSpec spec, MapEnd end, int next)
    {
        var side = spec.Size * (int)WldTerrain.WorldPerCell;
        var (x, z) = (side / 2, side / 2 - EndDistance / 2);
        var endZ = z + EndDistance;
        var ground = Ground(spec, [(x, z), (x, endZ)]);
        var floor = -(int)WldTerrain.HeightCurve[spec.Height];
        var records = new List<MapRecord>
        {
            MapRecord.Marker(StartMarker, x, floor - MarkerLift, z),
            MapRecord.Marker(FacingMarker, x, floor - MarkerLift, z + FacingStep),
            MapRecord.Marker(MusicTracks.MapTypeMarker, x, floor - MarkerLift, z - FacingStep) with { Param16 = spec.MapType },
        };
        var doorway = End(records, end, next, x, floor, endZ);
        var grid = spec.Size / CellsPerMapCell;
        var map = MapWriter.Write(grid, grid, MapSetting.Outdoor, [], records, out var offsets);
        return new NewMapFiles(spec.Number, map, ground.Write(), doorway >= 0 ? offsets[doorway] : -1);
    }

    private static NewMapFiles Interior(GameData data, NewMapSpec spec, MapEnd end, int next)
    {
        var room = data.LoadMap(RoomOf(spec, data.Game)) ?? throw new InvalidDataException($"MAP.{RoomOf(spec, data.Game):D3} does not load");

        // The room: its meshes (walls and props) without acts, links or hit points, under their own names.
        var names = new List<string>();
        var records = new List<MapRecord>();
        foreach (var e in room.Entities.Where(e => e.Variant == MapEntityVariant.Mesh && room.EntityName(e).Length > 0))
        {
            var name = room.EntityName(e);
            var index = names.IndexOf(name);
            if (index < 0)
            {
                names.Add(name);
                index = names.Count - 1;
            }

            records.Add(new MapRecord(MapEntityVariant.Mesh, e.X, e.Y, e.Z) { Pitch = e.Pitch, Yaw = e.Yaw, Roll = e.Roll, NameIndex = index });
        }

        // The start as the room has it; the end at the spawn marker farthest from it.
        var spawns = room.Entities.Where(e => e.IsMarker && e.MarkerType is >= StartMarker and <= LastSpawnMarker && !IsReserved(e.MarkerType)).ToArray();
        var start = spawns.FirstOrDefault(e => e.MarkerType == StartMarker) ?? throw new InvalidDataException($"MAP.{RoomOf(spec, data.Game):D3} has no start marker");
        var facing = spawns.FirstOrDefault(e => e.MarkerType == FacingMarker) ?? start;
        records.Add(MapRecord.Marker(StartMarker, start.X, start.Y, start.Z));
        records.Add(MapRecord.Marker(FacingMarker, facing.X, facing.Y, facing.Z));
        records.Add(MapRecord.Marker(MusicTracks.MapTypeMarker, start.X, start.Y, start.Z) with { Param16 = spec.MapType });
        var far = spawns.MaxBy(e => (long)(e.X - start.X) * (e.X - start.X) + (long)(e.Z - start.Z) * (e.Z - start.Z))!;
        var doorway = End(records, end, next, far.X, far.Y + MarkerLift, far.Z);
        var map = MapWriter.Write(room.GridWidth, room.GridHeight, MapSetting.Interior, names, records, out var offsets);
        return new NewMapFiles(spec.Number, map, null, doorway >= 0 ? offsets[doorway] : -1);
    }

    // Marker types 2-8 and 30-39 inside the spawn range mean other things (docs/formats/map.md).
    private static bool IsReserved(int type) => type is >= 2 and <= 8 or >= 30 and <= 39;

    /// <summary>Adds the map's end at (x, floor, z) (DOS units); the index of the doorway record, −1 none.</summary>
    private static int End(List<MapRecord> records, MapEnd end, int next, int x, int floor, int z)
    {
        switch (end)
        {
            case MapEnd.Doorway:
                records.Add(new MapRecord(MapEntityVariant.Sprite, x, floor, z) { Sprite = DoorwaySprite, Param16 = next, Act = ActRules.Exit });
                return records.Count - 1;
            case MapEnd.Objective:
                // The pad (0xF1, armed) flips the objective record (a dark light: drawn as nothing).
                records.Add(new MapRecord(MapEntityVariant.Sprite, x, floor - PadEyeLift, z)
                {
                    Sprite = DoorwaySprite, State = 1, Act = ActRules.ProxChainA, Next = records.Count + 1,
                });
                records.Add(new MapRecord(MapEntityVariant.Light, x, floor - PadEyeLift, z) { Act = ActRules.ObjectiveFirst });
                return -1;
            default:
                return -1;
        }
    }

    /// <summary>The ground: every corner at the flat height with the chosen tile; a generated field
    /// swings round it in value noise, flat within <see cref="FlatRadius"/> of <paramref name="keep"/>.</summary>
    private static WldFile Ground(NewMapSpec spec, IReadOnlyList<(int X, int Z)> keep)
    {
        var ground = WldFile.Create(spec.Size, spec.Size);
        ground.Layer(WldLayer.Tile).Fill((byte)spec.Tile);
        var heights = ground.Layer(WldLayer.Height);
        heights.Fill((byte)spec.Height);
        if (spec.Terrain != TerrainStart.Generated)
        {
            return ground;
        }

        var top = WldTerrain.HeightCurve.Length - 1;
        for (var row = 0; row < ground.Height; row++)
        {
            for (var col = 0; col < ground.Width; col++)
            {
                var (x, z) = (col * WldTerrain.WorldPerCell, (ground.Height - row) * WldTerrain.WorldPerCell);
                var near = keep.Count == 0 ? double.MaxValue : keep.Min(k => Math.Sqrt((k.X - x) * (k.X - x) + (k.Z - z) * (k.Z - z)));
                var weight = Math.Clamp((near - FlatRadius) / FlatRadius, 0.0, 1.0);
                var swing = TerrainNoise.Fractal(spec.Seed, col * NoiseScale, row * NoiseScale, NoiseOctaves) * GeneratedSwing * weight;
                heights[row * ground.Width + col] = (byte)Math.Clamp(spec.Height + (int)Math.Round(swing), 0, top);
            }
        }

        return ground;
    }

    private static int RoomOf(NewMapSpec spec, GameKind game) => spec.Room > 0 ? spec.Room : DefaultRoom(game);

    /// <summary>The folder mod's <c>maps/</c> and its <c>mod.json</c> when it has none (then in the list).</summary>
    internal static List<string> Begin(string modFolder)
    {
        var written = new List<string>();
        Directory.CreateDirectory(Path.Combine(modFolder, "maps"));
        var info = Path.Combine(modFolder, ModInfo.FileName);
        if (!File.Exists(info))
        {
            var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(modFolder));
            WriteFile(modFolder, ModInfo.FileName, Encoding.UTF8.GetBytes($"{{\n  \"name\": \"{System.Text.Json.JsonEncodedText.Encode(name)}\"\n}}\n"), written);
        }

        return written;
    }

    /// <summary>One file of the mod, written beside the old one and moved over it.</summary>
    internal static void WriteFile(string modFolder, string path, byte[] bytes, List<string> written)
    {
        var full = Path.Combine(modFolder, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full + ".tmp", bytes);
        File.Move(full + ".tmp", full, overwrite: true);
        written.Add(path);
    }
}
