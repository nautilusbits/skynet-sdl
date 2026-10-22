using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using RetroEng.Content;
using RetroEng.Diagnostics;
using RetroEng.Render;
using SkyNet.Data;
using SkyNet.Formats;
using SkyNet.Game.Collision;
using SkyNet.Game.Enhanced;
using SkyNet.Game.Mods;

namespace SkyNet.Game.Baking;

/// <summary>
/// Builds a <see cref="MapBlock"/> from the player's game data, with no GPU: the ground, the static
/// batch (per texture, split per <see cref="CellSize"/> map cell), the robots' rest pose, the dynamic
/// meshes per stage, the sprite placements, the collision triangles and the enhanced lamps, exactly
/// as <see cref="Level"/> and <see cref="LevelCollision"/> build them from the raw files (the level
/// is built from the block).
/// </summary>
public static class MapBaker
{
    /// <summary>The baker's own version: bump it when what a bake produces changes (a stale block is rebaked).</summary>
    public const string ToolVersion = "1";

    /// <summary>The side of the map cells the static batch is split by, world units.</summary>
    public const float CellSize = 1024f;

    /// <summary>A robot's marker Y is its eye: DOS places it at Y + 0x10 (FUN_00129f39).</summary>
    private const int EnemyLift = 0x10;
    private const int MaxSegmentDepth = 4;

    /// <summary>Border boxes grow by this much for the ground built (the Godot port's TERRAIN_VIEW_MARGIN).</summary>
    private const float TerrainViewMargin = 16000f * 1.8f + 200f * 2f;
    private const float TerrainEntityPad = 1024f;

    /// <summary>The block's path inside the system ZIP or a mod.</summary>
    public static string PathOf(int number) => $"maps/MAP.{number:D3}.block";

    /// <summary>Every map number in the data's map archive, ascending.</summary>
    public static int[] MapNumbers(GameData data)
    {
        using var a = data.OpenMapArchive();
        return [.. a.Entries.Select(e => e.Name)
            .Where(n => n.Length == 7 && n.StartsWith("MAP.", StringComparison.OrdinalIgnoreCase))
            .Select(n => int.TryParse(n.AsSpan(4), out var v) ? v : -1)
            .Where(v => v >= 0).Distinct().Order()];
    }

    /// <summary>The source hash of a set of inputs: the format and tool versions and the MD5 of the
    /// inputs' names and MD5s in name order.</summary>
    public static string SourceHashOf(IEnumerable<BlockInput> inputs)
    {
        var text = new StringBuilder();
        foreach (var i in inputs.OrderBy(i => i.Name, StringComparer.Ordinal))
        {
            text.Append(i.Name).Append('=').Append(i.Md5).Append('\n');
        }

        var md5 = Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(text.ToString())));
        return $"mapblock {MapBlock.FormatVersion} tool {ToolVersion} md5 {md5}";
    }

    /// <summary>Bakes MAP.<paramref name="number"/> with <paramref name="tables"/> (default: the
    /// tables in effect, <see cref="BakeTables.Current"/>); null when the map is missing or does not parse.</summary>
    /// <param name="edited">The map to bake in place of the file and its layer (an editor's, in memory).</param>
    public static MapBlock? Bake(GameData data, int number, BakeTables? tables = null, MapFile? edited = null)
    {
        using var inputs = new BakeInputs(data, tables);
        if (inputs.Read($"MAP.{number:D3}") is not { } mapBytes || MapFile.Parse(mapBytes) is not { } parsed)
        {
            return null;
        }

        var map = edited ?? inputs.WithLayer(number, parsed);

        var palette = inputs.Read(BakeInputs.Palette) is { } pb ? Palette.Parse(pb) : null;
        if (palette == null)
        {
            throw new InvalidDataException("No game palette (SKYNET.COL) in the data folder.");
        }

        var haze = inputs.Read("HAZE.000") is { } hb ? HazeTable.Parse(hb) : null;
        var transfrm = inputs.Read(BakeInputs.Transfrm) is { } tb ? TransfrmPrs.Parse(tb) : TransfrmPrs.Parse([]);
        var textures = new TextureStore(null, data);
        var missing = new List<string>();
        var outdoor = map.IsOutdoor;
        WldTerrain? wld = null;
        (int, int, int, int) cells = default;
        MapGround? layer = null;
        if (outdoor)
        {
            var wldName = $"WLD.{number:D3}";
            wld = inputs.Read(wldName) is { } wb ? WldTerrain.Parse(wb) : null;
            if (wld != null)
            {
                // The whole field, shared by every map on this WLD; the block keeps its crop.
                cells = TerrainCrop(map);
                layer = MapGround.Build(wld, Terrain.Layout(textures.Bank(Terrain.TileBank)), inputs.Md5Of(wldName),
                    inputs.Md5Of($"TEXTURE.{Terrain.TileBank:D3}"));
            }
            else
            {
                missing.Add($"WLD.{number:D3}");
            }
        }

        var shading = !outdoor && haze != null ? new IndoorShading(map, haze, palette) : null;
        Func<Vector3, Vector3, byte>? shade = shading != null ? shading.VertexShade : null;

        // The models: props (objects first), robots (enemies first), cached apart as Level does.
        var surfaces = new Dictionary<string, List<ModelSurface>?>(StringComparer.OrdinalIgnoreCase);
        var robotSurfaces = new Dictionary<string, List<ModelSurface>?>(StringComparer.OrdinalIgnoreCase);
        var robotMissing = new List<string>();
        var loadingRobots = false;
        List<ModelSurface>? Load(string file, bool enemyFirst)
        {
            var cache = enemyFirst ? robotSurfaces : surfaces;
            if (cache.TryGetValue(file, out var s))
            {
                return s;
            }

            var bytes = inputs.Read((enemyFirst ? BakeInputs.RobotPrefix : BakeInputs.ModelPrefix) + file);
            var model = bytes is { Length: > 0 } ? Model3D.Parse(bytes) : null;
            s = model != null ? ModelMesh.Surfaces(model, textures, smooth: enemyFirst) : null;
            if (s is not { Count: > 0 })
            {
                s = null;
                (loadingRobots ? robotMissing : missing).Add(file);
            }

            cache[file] = s;
            return s;
        }

        var world = new CellBatch();
        var dynamic = new List<BlockDynamic>();
        var instances = 0;
        foreach (var e in map.Entities)
        {
            if (e.Variant != MapEntityVariant.Mesh || e.NameIndex < 0 || e.NameIndex >= map.Names.Length)
            {
                continue;
            }

            var name = map.Names[e.NameIndex];
            if (string.IsNullOrEmpty(name) || Load(name + ".3D", false) is not { } s)
            {
                continue;
            }

            var placed = Level.Rotation(e.Yaw, e.Pitch, e.Roll) * Matrix4x4.CreateTranslation(e.X, -e.Y, -e.Z);
            instances++;
            if (Behaviour.BehaviourModel.IsDynamic(map, e, transfrm))
            {
                var stages = Behaviour.BehaviourModel.StageMeshes(map, e, transfrm).Select(n =>
                {
                    if (Load(n + ".3D", false) is not { } ss)
                    {
                        return null;
                    }

                    var one = new StaticBatch();
                    one.Add(ss, placed, shade);
                    var buckets = one.Buckets.Select(b => new BlockBucket(KeyOf(textures, b.Key), [.. b.Vertices], [.. b.Indices], [])).ToArray();
                    return buckets.Any(b => b.Indices.Length > 0) ? buckets : null;
                }).ToArray();
                dynamic.Add(new BlockDynamic(e.FileOffset, stages));
                continue;
            }

            world.Add(s, placed, shade, CellOf(e.X, -e.Z));
        }

        var modelTextures = textures.Created.Count;
        var modelCount = surfaces.Count(s => s.Value != null);
        loadingRobots = true;
        var robots = new CellBatch();
        var enemyCount = BakeRobots(map, robots, shade, Load, inputs);
        var robotTextures = textures.Created.Count;
        var modelCountWithRobots = surfaces.Where(s => s.Value != null).Select(s => s.Key)
            .Union(robotSurfaces.Where(s => s.Value != null).Select(s => s.Key), StringComparer.OrdinalIgnoreCase).Count();

        var (sprites, spriteCount, pickups) = BakeSprites(map, textures, wld);
        var created = textures.Created;

        var colliders = new List<BlockCollider>();
        var collisionMissing = BakeColliders(map, inputs, colliders);
        var lamps = EmissiveLamps.ForMap(data, map, number, LevelLights.ArtLampMaxFraction);

        // Everything else read: the texture banks (the ground's tiles, the models', the sprites').
        foreach (var bank in textures.BanksRead.Append(Terrain.TileBank).Distinct())
        {
            inputs.Read($"TEXTURE.{bank:D3}");
        }

        var used = inputs.Used.OrderBy(i => i.Name, StringComparer.Ordinal).ToArray();
        return new MapBlock
        {
            Number = number,
            Outdoor = outdoor,
            SourceHash = SourceHashOf(used),
            Inputs = used,
            MeshInstances = instances,
            EnemyCount = enemyCount,
            SpriteCount = spriteCount,
            PickupCount = pickups,
            ModelCount = modelCount,
            ModelCountWithRobots = modelCountWithRobots,
            Missing = [.. missing],
            MissingRobots = [.. robotMissing],
            TerrainCells = cells,
            Terrain = layer?.Crop(cells),
            Ground = layer,
            ModelTextures = [.. created.Take(modelTextures)],
            RobotTextures = [.. created.Skip(modelTextures).Take(robotTextures - modelTextures)],
            SpriteTextures = [.. created.Skip(robotTextures)],
            World = world.ToBuckets(textures),
            Robots = robots.ToBuckets(textures),
            Dynamic = [.. dynamic],
            Sprites = sprites,
            Colliders = [.. colliders],
            CollisionMissing = collisionMissing,
            Lamps = [.. lamps],
        };
    }

    /// <summary>The source hash the data gives now for a block's inputs (each input read and hashed again).</summary>
    public static string CurrentSourceHash(BakeInputs inputs, IEnumerable<BlockInput> stored) =>
        SourceHashOf(stored.Select(i => new BlockInput(i.Name, inputs.Md5Of(i.Name))));

    private static (int, int) CellOf(float x, float z) => ((int)MathF.Floor(x / CellSize), (int)MathF.Floor(z / CellSize));

    private static BlockKey KeyOf(TextureStore textures, SurfaceKey key) =>
        new(key.Texture.IsNone ? null : textures.RefOf(key.Texture), key.FlatIndex);

    /// <summary>The robots of the enemy-start markers (type 2) in their rest pose, as Level.BuildEnemies places them:
    /// at the marker (Y + <see cref="EnemyLift"/>), not stood on DOS's surface as Robots.StandOnFloor
    /// stands them in play (the bake has no AIS stand heights; docs/formats/map-block.md).</summary>
    private static int BakeRobots(MapFile map, CellBatch batch, Func<Vector3, Vector3, byte>? shade,
        Func<string, bool, List<ModelSurface>?> load, BakeInputs inputs)
    {
        if (!map.Entities.Any(e => e.Variant == MapEntityVariant.Sprite && e.MarkerType == 2))
        {
            return 0;
        }

        var types = inputs.EnemyTypes();
        var count = 0;
        void Place(int type, Matrix4x4 world, int depth, (int, int) cell)
        {
            if (type < 0 || type >= types.Length || depth > MaxSegmentDepth)
            {
                return;
            }

            var t = types[type];
            if (!string.IsNullOrEmpty(t.ModelFile) && load(t.ModelFile, true) is { } s)
            {
                batch.Add(s, world, shade, cell);
            }

            foreach (var seg in t.Segments)
            {
                var local = Level.Rotation(seg.Yaw, seg.Pitch, seg.Roll) * Matrix4x4.CreateTranslation(seg.X, -seg.Y, -seg.Z);
                Place(seg.ChildType, local * world, depth + 1, cell);
            }
        }

        foreach (var e in map.Entities)
        {
            if (e.Variant == MapEntityVariant.Sprite && e.MarkerType == 2 && e.EnemyType >= 0)
            {
                Place(e.EnemyType, Matrix4x4.CreateTranslation(e.X, -(e.Y + EnemyLift), -e.Z), 0, CellOf(e.X, -e.Z));
                count++;
            }
        }

        return count;
    }

    /// <summary>The billboards as Level groups them: by sprite index and phase, in key order.</summary>
    private static (BlockSpriteGroup[], int Count, int Pickups) BakeSprites(MapFile map, TextureStore textures, WldTerrain? wld)
    {
        var groups = new Dictionary<(int Index, int Phase), List<(Vector3 Centre, Vector2 Size, int Offset)>>();
        int count = 0, pickups = 0;
        foreach (var e in map.Entities)
        {
            if (e.Variant != MapEntityVariant.Sprite || e.IsMarker || e.MarkerType != -1 || e.SpriteIndex < 0)
            {
                continue;
            }

            if (textures.Get(e.SpriteBank, e.SpriteRecord, opaque: false) is not { } t)
            {
                continue;
            }

            var item = SpriteRules.IsItem(e.SpriteIndex);
            var scale = item ? Level.ItemScale : Level.SpriteScale;
            var size = new Vector2(t.Width * scale, t.Height * scale);
            float baseY = map.IsOutdoor && wld != null ? (float)wld.HeightAt(e.X, e.Z) : -e.Y - Level.IndoorSpriteLift;
            var phase = t.Animated && !item ? e.FileOffset % t.Frames : 0;
            if (!groups.TryGetValue((e.SpriteIndex, phase), out var list))
            {
                groups[(e.SpriteIndex, phase)] = list = [];
            }

            list.Add((new Vector3(e.X, baseY + size.Y / 2f, -e.Z), size, e.FileOffset));
            count++;
            if (item)
            {
                pickups++;
            }
        }

        var result = groups.OrderBy(g => g.Key)
            .Where(g => textures.Get(g.Key.Index >> 7, g.Key.Index & 0x7F, opaque: false) != null)
            .Select(g => new BlockSpriteGroup(g.Key.Index, g.Key.Phase, [.. g.Value]))
            .ToArray();
        return (result, count, pickups);
    }

    /// <summary>Every placed variant-1 mesh's frame-0 triangles, as <see cref="LevelCollision.Build"/>
    /// places them (no skip: the loader leaves the dynamic records out); returns the models missed.</summary>
    private static string[] BakeColliders(MapFile map, BakeInputs inputs, List<BlockCollider> into)
    {
        var models = new Dictionary<string, Vector3[][]?>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in map.Entities)
        {
            if (e.Variant != MapEntityVariant.Mesh || e.NameIndex < 0 || e.NameIndex >= map.Names.Length)
            {
                continue;
            }

            var name = map.Names[e.NameIndex];
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            if (!models.TryGetValue(name, out var faces))
            {
                var bytes = inputs.Read(BakeInputs.ModelPrefix + name + ".3D");
                var model = bytes is { Length: > 0 } ? Model3D.Parse(bytes) : null;
                models[name] = faces = model != null ? LevelCollision.Faces(model) : null;
            }

            if (faces == null)
            {
                continue;
            }

            var placed = LevelCollision.Placement(e);
            var corners = new Vector3[faces.Length * 3];
            for (var i = 0; i < faces.Length; i++)
            {
                corners[i * 3] = Vector3.Transform(faces[i][0], placed);
                corners[i * 3 + 1] = Vector3.Transform(faces[i][1], placed);
                corners[i * 3 + 2] = Vector3.Transform(faces[i][2], placed);
            }

            into.Add(new BlockCollider(e.FileOffset, corners));
        }

        return [.. models.Where(m => m.Value == null).Select(m => m.Key + ".3D").Order(StringComparer.Ordinal)];
    }

    /// <summary>The heightmap cells an outdoor map builds its ground over (the Godot port's
    /// terrain_crop): its border boxes (marker pairs 30-39) grown by the view margin, and every
    /// entity with a pad round it; the whole field when there is no border box.</summary>
    public static (int Col, int Row, int W, int H) TerrainCrop(MapFile map)
    {
        var byType = new Dictionary<int, List<Vector2>>();
        foreach (var e in map.Entities)
        {
            if (e.Variant == MapEntityVariant.Sprite && e.MarkerType is >= 30 and <= 39)
            {
                if (!byType.TryGetValue(e.MarkerType, out var l)) byType[e.MarkerType] = l = [];
                l.Add(new Vector2(e.X, -e.Z));
            }
        }

        Vector2? lo = null, hi = null;
        foreach (var (_, pts) in byType)
        {
            for (var i = 0; i + 1 < pts.Count; i += 2)
            {
                lo = Vector2.Min(lo ?? pts[i], Vector2.Min(pts[i], pts[i + 1]));
                hi = Vector2.Max(hi ?? pts[i], Vector2.Max(pts[i], pts[i + 1]));
            }
        }

        if (lo is not { } min || hi is not { } max)
        {
            return default;
        }

        min -= new Vector2(TerrainViewMargin);
        max += new Vector2(TerrainViewMargin);
        foreach (var e in map.Entities)
        {
            if (e.Variant == MapEntityVariant.None) continue;
            min = Vector2.Min(min, new Vector2(e.X - TerrainEntityPad, -e.Z - TerrainEntityPad));
            max = Vector2.Max(max, new Vector2(e.X + TerrainEntityPad, -e.Z + TerrainEntityPad));
        }

        var k = (float)WldTerrain.ZFlipK;
        int c0 = (int)MathF.Floor(min.X / 256f), r0 = (int)MathF.Floor((min.Y + k) / 256f);
        int c1 = (int)MathF.Ceiling(max.X / 256f), r1 = (int)MathF.Ceiling((max.Y + k) / 256f);
        c0 = Math.Clamp(c0, 0, 255); r0 = Math.Clamp(r0, 0, 255);
        c1 = Math.Clamp(c1, 0, 255); r1 = Math.Clamp(r1, 0, 255);
        if (c1 <= c0 || r1 <= r0 || (c0 == 0 && r0 == 0 && c1 == 255 && r1 == 255))
        {
            return default;
        }

        return (c0, r0, c1 - c0, r1 - r0);
    }

    /// <summary>Placed surfaces per surface key and per map cell; flattened cell by cell (row-major),
    /// so each cell's part of a key is one index range.</summary>
    private sealed class CellBatch
    {
        private readonly Dictionary<SurfaceKey, SortedDictionary<(int Z, int X), (List<Vertex> V, List<uint> I)>> _buckets = [];
        private readonly List<SurfaceKey> _order = [];

        public void Add(IReadOnlyList<ModelSurface> surfaces, Matrix4x4 world, Func<Vector3, Vector3, byte>? shade, (int X, int Z) cell)
        {
            foreach (var s in surfaces)
            {
                if (!_buckets.TryGetValue(s.Key, out var cells))
                {
                    _buckets[s.Key] = cells = [];
                    _order.Add(s.Key);
                }

                if (!cells.TryGetValue((cell.Z, cell.X), out var part))
                {
                    cells[(cell.Z, cell.X)] = part = ([], []);
                }

                StaticBatch.Place(s, world, shade, part.V, part.I);
            }
        }

        public BlockBucket[] ToBuckets(TextureStore textures)
        {
            var result = new List<BlockBucket>();
            foreach (var key in _order)
            {
                var vertices = new List<Vertex>();
                var indices = new List<uint>();
                var pieces = new List<BlockPiece>();
                foreach (var ((z, x), (v, i)) in _buckets[key])
                {
                    var first = indices.Count;
                    var at = (uint)vertices.Count;
                    var min = new Vector3(float.MaxValue);
                    var max = new Vector3(float.MinValue);
                    foreach (var vertex in v)
                    {
                        min = Vector3.Min(min, vertex.Position);
                        max = Vector3.Max(max, vertex.Position);
                    }

                    vertices.AddRange(v);
                    foreach (var index in i)
                    {
                        indices.Add(at + index);
                    }

                    pieces.Add(new BlockPiece(x, z, first, indices.Count - first, min, max));
                }

                result.Add(new BlockBucket(KeyOf(textures, key), [.. vertices], [.. indices], [.. pieces]));
            }

            return [.. result];
        }
    }
}

/// <summary>
/// Where the bake takes the shipped tables from (SkyNet.Data.GameTables): <see cref="Shipped"/>, the
/// tables embedded in the game, for the system ZIP (a mod never bakes into it); <see cref="Current"/>,
/// the tables in effect (the <c>--mod</c> mods' <c>tables/NAME.json</c>, folders or ZIPs, over the
/// shipped ones: <see cref="GameTables.Mods"/>), for a block baked in memory at map entry.
/// </summary>
/// <param name="Mods">The tables' mods (null: the shipped tables).</param>
/// <param name="Layers">The mods whose map layers (<c>maps/MAP.NNN.layer.json</c>) and terrains (<c>maps/WLD.NNN</c>) the bake applies (null: none).</param>
public sealed record BakeTables(VirtualFileSystem? Mods, VirtualFileSystem? Layers = null)
{
    public static BakeTables Shipped { get; } = new((VirtualFileSystem?)null);
    public static BakeTables Current => new(GameTables.Mods, ModContent.Files);
}

/// <summary>
/// The bake's reads of the game data by input name, each hashed (MD5) once and remembered as used:
/// <c>MAP.NNN</c> (map archive), <c>WLD.NNN</c>, <c>TEXTURE.NNN</c>, <c>HAZE.000</c> (loose),
/// <see cref="Palette"/> (the game palette), <see cref="Transfrm"/> (MDMDBRIF.BSA), models as
/// <see cref="ModelPrefix"/> + file (props: MDMDOBJS.BSA, then MDMDENMS.BSA) or
/// <see cref="RobotPrefix"/> + file (robots: MDMDENMS.BSA first), and the part of a shipped table the
/// bake uses as <see cref="EnemyTypesInput"/> (from <paramref name="tables"/>, default the tables in
/// effect). Archives open once.
/// </summary>
public sealed class BakeInputs(GameData data, BakeTables? tables = null) : IDisposable
{
    public const string Palette = "PALETTE";
    public const string Transfrm = "TRANSFRM.PRS";
    public const string ModelPrefix = "MODEL:";
    public const string RobotPrefix = "ROBOT:";
    public const string TablePrefix = "TABLE:";
    /// <summary>A map layer with edits (<see cref="Mods.MapLayer"/>): <c>LAYER:MAP.210</c>.</summary>
    public const string LayerPrefix = "LAYER:";

    /// <summary>The enemy type table's geometry (each type's model and child segments: what the robots'
    /// rest pose is built from; its AI fields do not change a block).</summary>
    public const string EnemyTypesInput = TablePrefix + TableNames.EnemyTypes;

    private readonly BakeTables _tables = tables ?? BakeTables.Current;
    private ImmutableArray<EnemyType>? _enemyTypes;

    public BakeTables Tables => _tables;

    /// <summary>The enemy types from <see cref="Tables"/>, remembered as the input <see cref="EnemyTypesInput"/>.</summary>
    public ImmutableArray<EnemyType> EnemyTypes()
    {
        Read(EnemyTypesInput);
        return LoadEnemyTypes();
    }

    private ImmutableArray<EnemyType> LoadEnemyTypes() =>
        _enemyTypes ??= GameTables.EnemyTypes(_tables.Mods);

    /// <summary>What a block reads of the enemy type table, as text: per type its index, model and
    /// segments (child type, offset, angles). Only the types a DOS marker can name (the table's own
    /// ids) and their segments: a mod robot under a new id (<see cref="ModRobots"/>) is placed only by
    /// a map layer, whose edits are an input of their own, so adding one keeps every block.</summary>
    private byte[] EnemyGeometry()
    {
        var types = LoadEnemyTypes();
        var shipped = GameTables.Load(TableNames.EnemyTypes, TableSchemas.EnemyTypes, _tables.Mods).Rows.Length;
        var reached = new SortedSet<int>(Enumerable.Range(0, Math.Min(shipped, types.Length)));
        var work = new Stack<int>(reached);
        while (work.Count > 0)
        {
            foreach (var s in types[work.Pop()].Segments)
            {
                if (s.ChildType >= 0 && s.ChildType < types.Length && reached.Add(s.ChildType))
                {
                    work.Push(s.ChildType);
                }
            }
        }

        var text = new StringBuilder();
        foreach (var t in reached.Select(i => types[i]))
        {
            text.Append(CultureInfo.InvariantCulture, $"{t.Index} {t.ModelFile}");
            foreach (var s in t.Segments)
            {
                text.Append(CultureInfo.InvariantCulture, $" {s.ChildType} {s.X} {s.Y} {s.Z} {s.Pitch} {s.Yaw} {s.Roll}");
            }

            text.Append('\n');
        }

        return Encoding.UTF8.GetBytes(text.ToString());
    }

    private readonly Dictionary<string, BsaArchive?> _archives = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _md5 = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _used = new(StringComparer.Ordinal);

    /// <summary>The inputs read so far, with their MD5s.</summary>
    public IEnumerable<BlockInput> Used => _used.Select(u => new BlockInput(u.Key, u.Value));

    /// <summary>
    /// MAP.<paramref name="number"/> with the edits of the layer it plays as
    /// (<see cref="GameData.LayerName"/>) from <see cref="BakeTables.Layers"/>, the layer remembered
    /// as the input <see cref="LayerPrefix"/> + name when it has edits; <paramref name="map"/> itself
    /// when there is none or it is empty. A layer that does not read or fit is logged and left out.
    /// </summary>
    public MapFile WithLayer(int number, MapFile map)
    {
        var layer = data.LayerName(number);
        try
        {
            if (Mods.MapLayers.Find(_tables.Layers, layer) is not { IsEmpty: false } l)
            {
                return map;
            }

            var edited = l.Apply(number, map);
            Read(LayerPrefix + layer);
            return edited;
        }
        catch (Mods.MapLayerException e)
        {
            Log.Warn("Bake", $"{Mods.MapLayer.PathOf(layer)}: {e.Message}; MAP.{number:D3} bakes as its DOS file");
            return map;
        }
    }

    /// <summary>Forgets what was used (the hash cache stays): for the next map.</summary>
    public void ClearUsed() => _used.Clear();

    /// <summary>An input's bytes (null when absent), remembered as used.</summary>
    public byte[]? Read(string name)
    {
        var bytes = ReadRaw(name);
        var md5 = bytes != null ? Convert.ToHexStringLower(MD5.HashData(bytes)) : "";
        _md5[name] = md5;
        _used[name] = md5;
        return bytes;
    }

    /// <summary>An input's MD5 now ("" when absent), cached.</summary>
    public string Md5Of(string name)
    {
        if (!_md5.TryGetValue(name, out var md5))
        {
            var bytes = ReadRaw(name);
            _md5[name] = md5 = bytes != null ? Convert.ToHexStringLower(MD5.HashData(bytes)) : "";
        }

        return md5;
    }

    private byte[]? ReadRaw(string name)
    {
        if (name.StartsWith(ModelPrefix, StringComparison.Ordinal))
        {
            var file = name[ModelPrefix.Length..];
            return Archive("MDMDOBJS.BSA")?.Read(file) is { Length: > 0 } b ? b : Archive("MDMDENMS.BSA")?.Read(file);
        }

        if (name.StartsWith(RobotPrefix, StringComparison.Ordinal))
        {
            var file = name[RobotPrefix.Length..];
            return Archive("MDMDENMS.BSA")?.Read(file) is { Length: > 0 } b ? b : Archive("MDMDOBJS.BSA")?.Read(file);
        }

        if (name.StartsWith("MAP.", StringComparison.Ordinal))
        {
            // A map the archive lacks may be a mod's (maps/MAP.NNN), read as the map layers are.
            return Archive(data.MapArchive)?.Read(name) ?? Mods.ModMaps.Find(_tables.Layers, Mods.ModMaps.MapOf(name));
        }

        if (name.StartsWith(LayerPrefix, StringComparison.Ordinal))
        {
            return Mods.MapLayers.EditBytes(_tables.Layers, name[LayerPrefix.Length..]);
        }

        if (name == EnemyTypesInput)
        {
            return EnemyGeometry();
        }

        // A mod's terrain (maps/WLD.NNN) stands in for the data folder's, as the map layers do.
        if (Mods.MapTerrains.MapOf(name) is var wld and >= 0 && Mods.MapTerrains.Find(_tables.Layers, wld) is { } terrain)
        {
            return terrain;
        }

        return name switch
        {
            Palette => data.PaletteBytes(),
            Transfrm => Archive("MDMDBRIF.BSA")?.Read("TRANSFRM.PRS"),
            _ => data.ReadLoose(name),
        };
    }

    private BsaArchive? Archive(string name)
    {
        if (!_archives.TryGetValue(name, out var a))
        {
            _archives[name] = a = data.Exists(name) ? data.OpenArchive(name) : null;
        }

        return a;
    }

    public void Dispose()
    {
        foreach (var a in _archives.Values)
        {
            a?.Dispose();
        }

        _archives.Clear();
    }
}
