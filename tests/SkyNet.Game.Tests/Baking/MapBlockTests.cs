using System.Diagnostics;
using System.Numerics;
using RetroEng.Content;
using SkyNet.Formats;
using SkyNet.Game.Baking;
using SkyNet.Game.Behaviour;
using SkyNet.Game.Collision;
using Xunit.Abstractions;

namespace SkyNet.Game.Tests.Baking;

/// <summary>The map block: its format, the baker and the store in a mods folder (temp folders only).
/// Skipped without the game data (SKYNET_DATA).</summary>
public sealed class MapBlockTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _temp = Directory.CreateTempSubdirectory("skynet-blocks-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_temp, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static GameData? Data()
    {
        var dir = Environment.GetEnvironmentVariable("SKYNET_DATA");
        return string.IsNullOrWhiteSpace(dir) || !GameDataLocator.HasData(dir) ? null : GameData.Open(dir);
    }

    [Theory]
    [InlineData(210)]
    [InlineData(211)]
    [InlineData(250)]
    public void ABlockReadsBackAsWritten(int number)
    {
        if (Data() is not { } data || MapBaker.Bake(data, number) is not { } block)
        {
            return;
        }

        var bytes = block.ToBytes();
        var read = MapBlock.Read(bytes, Grounds(block));
        Assert.Equal(bytes, read.ToBytes());
        Assert.Equal(block.Number, read.Number);
        Assert.Equal(block.Outdoor, read.Outdoor);
        Assert.Equal(block.SourceHash, read.SourceHash);
        Assert.Equal(block.Inputs, read.Inputs);
        Assert.Equal(block.World.Sum(b => b.Indices.Length), read.World.Sum(b => b.Indices.Length));
        Assert.Equal(block.Lamps, read.Lamps);
        Assert.Equal(block.Terrain?.Vertices, read.Terrain?.Vertices);
        Assert.Equal(block.Terrain?.Indices, read.Terrain?.Indices);
        Assert.Equal(block.Terrain?.Chunks, read.Terrain?.Chunks);
        Assert.Equal(block.Ground?.ToBytes(), read.Ground?.ToBytes());

        using var stream = new MemoryStream(bytes);
        var (hash, inputs, ground) = MapBlock.ReadSource(stream);
        Assert.Equal(block.SourceHash, hash);
        Assert.Equal(block.Inputs, inputs);
        Assert.Equal(block.GroundPath, ground);
        Assert.Equal(block.Outdoor, ground.Length > 0);
    }

    // The ground layer of a block baked in memory, as a resolver for MapBlock.Read.
    private static Func<string, byte[]?> Grounds(params MapBlock[] blocks) =>
        path => blocks.Select(b => b.Ground).FirstOrDefault(g => g?.Path == path)?.ToBytes();

    /// <summary>The ground cut from the shared layer is the ground Terrain builds over the block's
    /// crop, vertex for vertex, for every outdoor map of the data.</summary>
    [Fact]
    public void TheGroundCutFromTheLayerIsTheGroundOfTheCrop()
    {
        if (Data() is not { } data)
        {
            return;
        }

        var layout = Terrain.Layout(data.LoadTextureBank(Terrain.TileBank));
        var checkedMaps = 0;
        foreach (var n in MapBaker.MapNumbers(data))
        {
            if (data.LoadMap(n) is not { IsOutdoor: true } map || data.LoadWld(n) is not { } wld)
            {
                continue;
            }

            var layer = MapGround.Build(wld, layout, "wld", "tiles");
            var crop = MapBaker.TerrainCrop(map);
            var expected = Terrain.Build(wld, layout, crop);
            var cut = layer.Crop(crop);
            Assert.Equal(expected.Vertices, cut.Vertices);
            Assert.Equal(expected.Indices, cut.Indices);
            Assert.Equal(expected.Chunks, cut.Chunks);
            checkedMaps++;
        }

        Assert.True(checkedMaps > 10);
    }

    /// <summary>Maps on the same WLD (MAP.216 and MAP.217) share one ground entry in the ZIP; a
    /// ground no block uses any more is removed, and a block whose ground is missing is stale.</summary>
    [Fact]
    public void VariantMapsShareOneGround()
    {
        if (Data() is not { } data || data.Game != GameKind.SkyNet)
        {
            return;
        }

        int[] maps = [216, 217];
        using var store = new BlockStore(_temp, data);
        store.Bake(maps);
        using (var zip = ZipPack.Open(store.ZipPath))
        {
            var grounds = zip.Entries.Where(e => e.Path.StartsWith("maps/ground/", StringComparison.Ordinal)).ToArray();
            Assert.Single(grounds);
            Assert.All(maps, n =>
            {
                using var s = zip.OpenEntry(zip.Entries.Single(e => e.Path == MapBaker.PathOf(n)));
                Assert.Equal(grounds[0].Path, MapBlock.ReadSource(s).Ground);
            });
        }

        store.Mount();
        foreach (var n in maps)
        {
            var block = store.Load(n)!;
            var expected = Terrain.Build(data.LoadWld(n)!, Terrain.Layout(data.LoadTextureBank(Terrain.TileBank)), MapBaker.TerrainCrop(data.LoadMap(n)!));
            Assert.Equal(expected.Vertices, block.Terrain!.Vertices);
            Assert.Equal(expected.Chunks, block.Terrain.Chunks);
        }

        store.Unmount();

        // A stray ground goes with the next bake; without its ground a block is stale.
        string path;
        using (var writer = ZipPackWriter.Update(store.ZipPath))
        {
            path = writer.Basis!.Entries.Single(e => e.Path.StartsWith("maps/ground/", StringComparison.Ordinal)).Path;
            writer.Add("maps/ground/0123.ground", [1, 2, 3], "mapground stray");
            writer.Commit();
        }

        store.Bake([216]);
        using (var zip = ZipPack.Open(store.ZipPath))
        {
            Assert.False(zip.TryGetEntry("maps/ground/0123.ground", out _));
            Assert.True(zip.TryGetEntry(path, out _));
        }

        using (var writer = ZipPackWriter.Update(store.ZipPath))
        {
            writer.Remove(path);
            writer.Commit();
        }

        Assert.Equal(maps, store.Stale(only: maps));
    }

    /// <summary>The enemy table's geometry is an input of the blocks with robots (only theirs), and a
    /// change of it makes them stale; a change of the AI fields alone does not. A table mod never
    /// makes the system ZIP stale: its blocks are refused at load and the map is baked in memory,
    /// whether the mod is a folder or a ZIP.</summary>
    [Fact]
    public void TheEnemyTableFeedsTheBlocksWithRobots()
    {
        if (Data() is not { } data || data.Game != GameKind.SkyNet)
        {
            return;
        }

        var shipped = System.Text.Encoding.UTF8.GetString(SkyNet.Data.GameTables.EmbeddedBytes(SkyNet.Data.TableNames.EnemyTypes)!);
        // "stats" (a folder) changes only AI fields; "models" (a ZIP) moves a robot's model.
        var stats = Directory.CreateDirectory(Path.Combine(_temp, "stats", "tables")).FullName;
        File.WriteAllText(Path.Combine(stats, "enemy_types.json"), shipped.Replace("\"hitPoints\": 125,", "\"hitPoints\": 999,", StringComparison.Ordinal));
        using (var zip = System.IO.Compression.ZipFile.Open(Path.Combine(_temp, "models.zip"), System.IO.Compression.ZipArchiveMode.Create))
        using (var w = new StreamWriter(zip.CreateEntry("tables/enemy_types.json").Open()))
        {
            w.Write(shipped.Replace("\"modelFile\": \"flencer.3d\"", "\"modelFile\": \"hunter.3d\"", StringComparison.Ordinal));
        }

        var root = new ContentRoot(_temp);
        using var statsMod = root.Mount([], ["stats"]);
        using var modelsMod = root.Mount([], ["models"]);

        int[] maps = [210, 211];
        var robots = maps.Where(n => data.LoadMap(n)!.Entities.Any(e => e.Variant == MapEntityVariant.Sprite && e.MarkerType == 2)).ToArray();
        Assert.NotEmpty(robots);
        foreach (var n in maps)
        {
            var block = MapBaker.Bake(data, n, BakeTables.Shipped)!;
            Assert.Equal(robots.Contains(n), block.Inputs.Any(i => i.Name == BakeInputs.EnemyTypesInput));
            string HashWith(VirtualFileSystem mods)
            {
                using var inputs = new BakeInputs(data, new BakeTables(mods));
                return MapBaker.CurrentSourceHash(inputs, block.Inputs);
            }

            Assert.Equal(block.SourceHash, HashWith(statsMod));
            Assert.Equal(!robots.Contains(n), block.SourceHash == HashWith(modelsMod));
        }

        // The system ZIP is baked from the shipped tables; a mod's tables refuse only the blocks they change.
        using var store = new BlockStore(_temp, data);
        store.Bake(maps);
        Assert.Empty(store.Stale(only: maps));
        store.Mount();
        store.PlayTables = new BakeTables(modelsMod);
        foreach (var n in maps)
        {
            Assert.Equal(robots.Contains(n), store.Load(n) == null);
        }

        store.PlayTables = new BakeTables(statsMod);
        Assert.All(maps, n => Assert.NotNull(store.Load(n)));
        store.Unmount();
    }

    [Fact]
    public void ACutOrForeignBlockIsRefused()
    {
        if (Data() is not { } data || MapBaker.Bake(data, 211) is not { } block)
        {
            return;
        }

        var bytes = block.ToBytes();
        Assert.Throws<InvalidDataException>(() => MapBlock.Read(bytes[..(bytes.Length / 2)]));
        var other = (byte[])bytes.Clone();
        other[4] = 99;
        Assert.Throws<InvalidDataException>(() => MapBlock.Read(other));
        Assert.Throws<InvalidDataException>(() => MapBlock.Read("PK not a block at all"u8.ToArray()));
    }

    [Fact]
    public void TheBakerIsDeterministic()
    {
        if (Data() is not { } data)
        {
            return;
        }

        foreach (var n in new[] { 210, 214 })
        {
            Assert.Equal(MapBaker.Bake(data, n)!.ToBytes(), MapBaker.Bake(data, n)!.ToBytes());
        }
    }

    /// <summary>DOS draws every map sprite one world unit per texel: the map walker queues it at
    /// scale 0x100 (v1.01 0x136f3f), the draw multiplies the descriptor's size by that 8.8 scale
    /// (0x14f049) in the 24.8 space mesh vertices use. Scenery and items alike (issue #2).</summary>
    [Fact]
    public void ASpriteIsOneUnitPerTexel()
    {
        if (Data() is not { } data || MapBaker.Bake(data, 210) is not { } block)
        {
            return;
        }

        foreach (var g in block.Sprites)
        {
            var t = data.LoadTextureBank(g.Index >> 7)!.Records[g.Index & 0x7F];
            Assert.All(g.Sprites, s => Assert.Equal(new Vector2(t.Width, t.Height), s.Size));
        }
    }

    [Fact]
    public void TheStaticBatchIsSplitPerMapCell()
    {
        if (Data() is not { } data || MapBaker.Bake(data, 210) is not { } block)
        {
            return;
        }

        foreach (var b in block.World)
        {
            Assert.Equal(b.Indices.Length, b.Pieces.Sum(p => p.Count));
            Assert.Equal(b.Pieces.Length, b.Pieces.Select(p => (p.CellX, p.CellZ)).Distinct().Count());
            foreach (var p in b.Pieces)
            {
                for (var i = p.First; i < p.First + p.Count; i++)
                {
                    var v = b.Vertices[b.Indices[i]].Position;
                    Assert.True(Vector3.Min(v, p.Min) == p.Min && Vector3.Max(v, p.Max) == p.Max);
                }
            }
        }

        Assert.True(block.World.Sum(b => b.Pieces.Length) > block.World.Length);
    }

    /// <summary>What the block gives equals what the raw files give: the collision world (with the
    /// behaviour's dynamic records left out), the ground, the counts.</summary>
    [Theory]
    [InlineData(210)]
    [InlineData(214)]
    [InlineData(250)]
    public void ALevelFromTheBlockEqualsOneFromTheRawFiles(int number)
    {
        if (Data() is not { } data || MapBaker.Bake(data, number) is not { } baked || data.LoadMap(number) is not { } map)
        {
            return;
        }

        var block = MapBlock.Read(baked.ToBytes(), Grounds(baked));
        var wld = map.IsOutdoor ? data.LoadWld(number) : null;
        var transfrm = BehaviourModel.LoadTransfrm(data);
        bool Skip(MapEntity e) => BehaviourModel.IsDynamic(map, e, transfrm);
        var (raw, rawStats) = LevelCollision.Build(data, map, wld, Skip);
        var (fromBlock, blockStats) = LevelCollision.FromBlock(block, map, wld, Skip);
        Assert.Equal(rawStats.Meshes, blockStats.Meshes);
        Assert.Equal(rawStats.Triangles, blockStats.Triangles);
        var rawGrid = raw.Sources.OfType<RetroEng.Physics.TriangleGrid>().SingleOrDefault();
        var blockGrid = fromBlock.Sources.OfType<RetroEng.Physics.TriangleGrid>().SingleOrDefault();
        Assert.Equal(rawGrid?.Bounds, blockGrid?.Bounds);
        Assert.Equal(rawGrid?.Triangles, blockGrid?.Triangles);

        // Every variant-1 record with a model is either in the batch or dynamic.
        var meshes = map.Entities.Count(e => e.Variant == MapEntityVariant.Mesh && e.NameIndex >= 0 && e.NameIndex < map.Names.Length
            && !string.IsNullOrEmpty(map.Names[e.NameIndex]) && data.LoadModel(map.Names[e.NameIndex] + ".3D") != null);
        Assert.Equal(meshes, block.MeshInstances);
        Assert.Equal(map.Entities.Count(e => e.Variant == MapEntityVariant.Mesh && BehaviourModel.IsDynamic(map, e, transfrm)
            && e.NameIndex >= 0 && data.LoadModel(map.EntityName(e) + ".3D") != null), block.Dynamic.Length);

        if (wld != null)
        {
            var ground = Terrain.Build(wld, Terrain.Layout(data.LoadTextureBank(Terrain.TileBank)), MapBaker.TerrainCrop(map));
            Assert.Equal(ground.Vertices, block.Terrain!.Vertices);
            Assert.Equal(ground.Indices, block.Terrain.Indices);
            Assert.Equal(ground.Chunks, block.Terrain.Chunks);
        }
        else
        {
            Assert.Null(block.Terrain);
        }
    }

    [Fact]
    public void AChangedSourceHashIsBakedAgain()
    {
        if (Data() is not { } data)
        {
            return;
        }

        int[] maps = [211, 213];
        using var store = new BlockStore(_temp, data);
        Assert.Equal(maps, store.Stale(only: maps));
        Assert.Equal(2, store.Bake(maps));
        Assert.Empty(store.Stale(only: maps));
        Assert.Equal(maps, store.Stale(all: true, only: maps));

        // The ZIP entry's source hash no longer matches the data's.
        using (var writer = ZipPackWriter.Update(store.ZipPath))
        {
            var bytes = writer.Basis!.ReadAllBytes(MapBaker.PathOf(211));
            writer.Add(MapBaker.PathOf(211), bytes, "mapblock 1 tool 0 md5 stale");
            writer.Commit();
        }

        Assert.Equal([211], store.Stale(only: maps));

        // An input changed: the block lists an MD5 the data no longer gives.
        var block = MapBaker.Bake(data, 213)!;
        var inputs = block.Inputs.Select((i, k) => k == 0 ? i with { Md5 = new string('0', 32) } : i).ToArray();
        var hash = MapBaker.SourceHashOf(inputs);
        var fresh = block.ToBytes();
        var old = new MapBlock { Number = 213, SourceHash = hash, Inputs = inputs };
        using (var writer = ZipPackWriter.Update(store.ZipPath))
        {
            writer.Add(MapBaker.PathOf(213), old.ToBytes(), hash);
            writer.Commit();
        }

        Assert.Equal(maps, store.Stale(only: maps));
        Assert.Equal(2, store.Bake(store.Stale(only: maps)));
        Assert.Empty(store.Stale(only: maps));
        Assert.Equal(fresh, Read(ZipPack.Open(store.ZipPath), 213));
    }

    public enum StampChange { None, Stamp, DataTouched, DataEdited, ModAdded }

    [Theory]
    [InlineData(StampChange.None)]
    [InlineData(StampChange.Stamp)]
    [InlineData(StampChange.DataTouched)]
    [InlineData(StampChange.DataEdited)]
    [InlineData(StampChange.ModAdded)]
    public void TheStampSkipsTheCheckUntilAnInputChanges(StampChange change)
    {
        if (Data() is not { } real)
        {
            return;
        }

        // A copy of the data (no videos) the test may change.
        var dataDir = Path.Combine(_temp, "data");
        Directory.CreateDirectory(dataDir);
        foreach (var f in Directory.EnumerateFiles(real.Dir).Where(f => !f.EndsWith(".VID", StringComparison.OrdinalIgnoreCase)))
        {
            File.Copy(f, Path.Combine(dataDir, Path.GetFileName(f)));
        }

        var data = GameData.Open(dataDir)!;
        var mods = Path.Combine(_temp, "mods");
        int[] maps = [211, 213];
        using var store = new BlockStore(mods, data);
        Assert.Equal(2, store.Bake(maps));
        Assert.Empty(store.Stale(only: maps));
        Assert.Equal(["skynet"], new ContentRoot(mods).Discover().Select(m => m.Name));

        // MAP.211's block goes stale behind the stamp's back: same ZIP size and time.
        var zipTime = File.GetLastWriteTimeUtc(store.ZipPath);
        var zipBytes = File.ReadAllBytes(store.ZipPath);
        string hash;
        using (var zip = ZipPack.Open(store.ZipPath))
        {
            hash = zip.Entries.Single(e => e.Path == MapBaker.PathOf(211)).SourceHash;
        }

        // The block's own copy (written before the manifest's) no longer matches the entry's.
        var at = System.Text.Encoding.Latin1.GetString(zipBytes).IndexOf(hash, StringComparison.Ordinal) + hash.Length - 1;
        zipBytes[at] = (byte)(zipBytes[at] == '0' ? '1' : '0');
        File.WriteAllBytes(store.ZipPath, zipBytes);
        File.SetLastWriteTimeUtc(store.ZipPath, zipTime);

        int[] expected = [211];
        switch (change)
        {
            case StampChange.None:
                expected = [];
                break;
            case StampChange.Stamp:
                foreach (var f in Directory.EnumerateFiles(mods, "*.stamp"))
                {
                    File.WriteAllText(f, "another build");
                }

                break;
            case StampChange.DataTouched:
                File.SetLastWriteTimeUtc(data.PathOf("MDMDOBJS.BSA"), DateTime.UtcNow);
                break;
            case StampChange.DataEdited:
                // HAZE.000 is an input of every map.
                var haze = data.PathOf("HAZE.000");
                var bytes = File.ReadAllBytes(haze);
                bytes[0] ^= 0xFF;
                File.WriteAllBytes(haze, bytes);
                expected = maps;
                break;
            case StampChange.ModAdded:
                Directory.CreateDirectory(Path.Combine(mods, "newmod", "maps"));
                break;
        }

        Assert.Equal(expected, store.Stale(only: maps));
    }

    private static byte[] Read(ZipPack zip, int number)
    {
        using (zip)
        {
            zip.Verify();
            return zip.ReadAllBytes(MapBaker.PathOf(number));
        }
    }

    [Fact]
    public void TheSystemZipIsAStoredZipThatRebuildsToTheSameBytesAndReplacesTheOldPak()
    {
        if (Data() is not { } data)
        {
            return;
        }

        int[] maps = [211, 213];
        using var store = new BlockStore(_temp, data);
        File.WriteAllText(store.LegacyPakPath, "RPAK from an older build");
        store.Bake(maps);
        Assert.False(File.Exists(store.LegacyPakPath)); // the old cache is gone once the ZIP is there
        Assert.False(File.Exists(store.ZipPath + ".tmp"));
        var first = File.ReadAllBytes(store.ZipPath);

        // Every entry stored, with its source hash in the manifest; System.IO.Compression reads it.
        using (var zip = ZipPack.Open(store.ZipPath))
        {
            Assert.True(zip.HasManifest);
            Assert.All(zip.Entries, e => Assert.Equal(ZipCompression.Stored, e.Compression));
            Assert.All(zip.Entries, e => Assert.NotEqual("", e.SourceHash));
            zip.Verify();
        }

        using (var archive = System.IO.Compression.ZipFile.OpenRead(store.ZipPath))
        {
            Assert.Equal(["manifest.json", MapBaker.PathOf(211), MapBaker.PathOf(213)], archive.Entries.Select(e => e.FullName).Order(StringComparer.Ordinal));
        }

        // Rebaking unchanged content, in another order and from scratch, gives the same bytes.
        store.Bake([213, 211]);
        Assert.Equal(first, File.ReadAllBytes(store.ZipPath));
        File.Delete(store.ZipPath);
        store.Bake([213]);
        store.Bake([211]);
        Assert.Equal(first, File.ReadAllBytes(store.ZipPath));
    }

    [Fact]
    public void TheModsFolderBeatsTheZip()
    {
        if (Data() is not { } data)
        {
            return;
        }

        using var store = new BlockStore(_temp, data);
        store.Bake([210, 211]);
        store.Mount();
        Assert.Equal(210, store.Load(210)!.Number);

        // mods/skynet/maps/MAP.210.block (here: MAP.211's block under 210's name) wins over the ZIP.
        var folder = Path.Combine(_temp, BlockStore.SystemModName(data.Game), "maps");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "MAP.210.block"), MapBaker.Bake(data, 211)!.ToBytes());
        store.Mount();
        Assert.Equal(211, store.Load(210)!.Number);
        Assert.Equal(211, store.Load(211)!.Number);

        // A broken override is refused (the caller builds from the raw files).
        File.WriteAllBytes(Path.Combine(folder, "MAP.210.block"), [1, 2, 3]);
        store.Mount();
        Assert.Null(store.Load(210));
        store.Unmount();
    }

    /// <summary>Opt-in (SKYNET_BENCH=1): read and parse times of blocks stored raw and Deflate-compressed.</summary>
    [Fact]
    public void Benchmark()
    {
        if (Environment.GetEnvironmentVariable("SKYNET_BENCH") != "1" || Data() is not { } data)
        {
            return;
        }

        int[] maps = [210, 220, 250, 214];
        foreach (var mode in new[] { ZipCompression.Stored, ZipCompression.Deflate })
        {
            var dir = Path.Combine(_temp, mode.ToString());
            using var store = new BlockStore(dir, data) { Compression = mode };
            store.Bake(maps);
            store.Mount();
            output.WriteLine($"{mode}: zip {new FileInfo(store.ZipPath).Length / 1024} KB");
            for (var m = 0; m < maps.Length; m++)
            {
                var n = maps[m];
                var best = double.MaxValue;
                var bake = Stopwatch.StartNew();
                var size = MapBaker.Bake(data, n)!.ToBytes().Length;
                var bakeMs = bake.Elapsed.TotalMilliseconds;
                for (var k = 0; k < 4; k++)
                {
                    store.Load(maps[(m + 1) % maps.Length]);
                    var clock = Stopwatch.StartNew();
                    store.Load(n);
                    best = Math.Min(best, clock.Elapsed.TotalMilliseconds);
                }

                var map = data.LoadMap(n)!;
                var wld = map.IsOutdoor ? data.LoadWld(n) : null;
                var transfrm = BehaviourModel.LoadTransfrm(data);
                bool Skip(MapEntity e) => BehaviourModel.IsDynamic(map, e, transfrm);
                var raw = Stopwatch.StartNew();
                LevelCollision.Build(data, map, wld, Skip);
                var rawMs = raw.Elapsed.TotalMilliseconds;
                var fromBlock = Stopwatch.StartNew();
                LevelCollision.FromBlock(store.Load(n)!, map, wld, Skip);
                output.WriteLine($"  MAP.{n}: {size / 1024} KB, read {best:0.0} ms, bake {bakeMs:0} ms; " +
                    $"collision raw {rawMs:0} ms, from the block {fromBlock.Elapsed.TotalMilliseconds:0} ms");
            }

            store.Unmount();
        }
    }
}
