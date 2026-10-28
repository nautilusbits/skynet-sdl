using System.Diagnostics;
using System.Globalization;
using System.Runtime.ExceptionServices;
using RetroEng.Content;
using RetroEng.Diagnostics;
using RetroEng.Jobs;
using RetroEng.World;
using SkyNet.Formats;
using SkyNet.Game.Campaign;

namespace SkyNet.Game.Baking;

/// <summary>
/// The map blocks and mission files of one data set in the mods folder: the system mod
/// <c>mods/&lt;dataset&gt;.zip</c> (<see cref="SystemModName"/>), a standard ZIP, holds a block per
/// map, baked on the player's machine from the player's data; the mods mount over it
/// (<c>mods/&lt;dataset&gt;/maps/MAP.NNN.block</c> and user mods win).
/// <see cref="Stale"/> lists the maps whose block is missing or no longer matches the data,
/// <see cref="Bake"/> bakes them into the ZIP, <see cref="Mount"/> mounts the mods folder and
/// <see cref="Load"/> reads a block through it.
/// <para>The system ZIP is always baked from the shipped tables (<see cref="BakeTables.Shipped"/>),
/// whatever <c>--mod</c> is in effect, so a mod never writes into it and switching mods never makes
/// it stale. A block whose table inputs differ from the tables in effect (a mod's
/// <c>tables/enemy_types.json</c> moves a robot's model or segments) is not used: <see cref="Load"/>
/// gives null and the level bakes that map in memory with the mod's tables.</para>
/// </summary>
public sealed class BlockStore(string modsDirectory, GameData data) : IDisposable
{
    private VirtualFileSystem? _vfs;
    private (int Number, MapBlock Block)? _last;

    /// <summary>The system mod's name for a data set: skynet, or shock for Future Shock.</summary>
    public static string SystemModName(GameKind game) => game == GameKind.FutureShock ? "shock" : "skynet";

    public string ModsDirectory { get; } = Path.GetFullPath(modsDirectory);
    public GameData Data => data;
    /// <summary>How the blocks and mission files are stored in the ZIP: stored (no compression), so
    /// reading one is a single positioned read with nothing to inflate (docs/formats/zip-mods.md).
    /// Deflate at <see cref="CompressionLevel"/> makes the ZIP several times smaller.</summary>
    public ZipCompression Compression { get; set; } = ZipCompression.Stored;
    public System.IO.Compression.CompressionLevel CompressionLevel { get; set; } = System.IO.Compression.CompressionLevel.Fastest;

    /// <summary>The system mod: <c>mods/&lt;dataset&gt;.zip</c>.</summary>
    public string ZipPath => Path.Combine(ModsDirectory, SystemModName(data.Game) + ".zip");

    /// <summary>Where older builds kept the same content in their own container (RPAK); deleted once
    /// the ZIP is there (<see cref="RemoveLegacyPak"/>).</summary>
    public string LegacyPakPath => Path.Combine(ModsDirectory, SystemModName(data.Game) + ".pak");

    /// <summary>The maps whose block in the system ZIP is missing, unreadable or stale (its
    /// source hash differs from the data's now); every map with <paramref name="all"/>.</summary>
    /// <param name="only">The maps to look at (default every map in the data).</param>
    public IReadOnlyList<int> Stale(bool all = false, IEnumerable<int>? only = null)
    {
        var maps = only?.ToArray() ?? MapBaker.MapNumbers(data);
        if (all || !File.Exists(ZipPath))
        {
            return maps;
        }

        // The inputs as at the last check that found these maps fresh: nothing to read again.
        var stamp = Stamp(maps);
        if (ReadStamp() == stamp)
        {
            return [];
        }

        if (OpenZip() is not { } zip)
        {
            return maps;
        }

        var stale = new List<int>();
        using (zip)
        using (var inputs = new BakeInputs(data, BakeTables.Shipped))
        {
            foreach (var n in maps)
            {
                if (!zip.TryGetEntry(MapBaker.PathOf(n), out var entry) || !Fresh(zip, entry, inputs))
                {
                    stale.Add(n);
                }
            }
        }

        if (stale.Count == 0)
        {
            WriteStamp(stamp);
        }

        return stale;
    }

    /// <summary>Beside the system ZIP: <see cref="Stamp"/> of the last check that found every block fresh.</summary>
    private string StampPath => ZipPath + ".stamp";

    /// <summary>
    /// What the blocks of <paramref name="maps"/> depend on, cheap to gather: the maps, the format and
    /// tool versions, the game's build (its code and shipped tables), every data file's size and time,
    /// the ZIP's and the mods folder's entries. A block's real inputs (<see cref="Fresh"/>) are all in
    /// there: when the stamp is the same, so is each block's source hash.
    /// </summary>
    private string Stamp(int[] maps)
    {
        var text = new System.Text.StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"maps {string.Join(',', maps)}\n");
        text.Append(CultureInfo.InvariantCulture, $"block {MapBlock.FormatVersion} tool {MapBaker.ToolVersion}\n");
        text.Append(CultureInfo.InvariantCulture, $"build {typeof(MapBaker).Assembly.ManifestModule.ModuleVersionId} {typeof(SkyNet.Data.GameTables).Assembly.ManifestModule.ModuleVersionId}\n");
        foreach (var dir in new[] { data.Dir, data.SkyNetDir }.OfType<string>().Distinct())
        {
            AppendEntries(text, dir, "");
        }

        AppendEntries(text, ModsDirectory, Path.GetFileName(StampPath));
        return text.ToString();
    }

    // Each entry of a folder: name, size and time (a folder's name only), "skip" left out.
    private static void AppendEntries(System.Text.StringBuilder text, string dir, string skip)
    {
        if (!Directory.Exists(dir))
        {
            return;
        }

        text.Append(CultureInfo.InvariantCulture, $"in {dir}\n");
        foreach (var e in new DirectoryInfo(dir).EnumerateFileSystemInfos().OrderBy(e => e.Name, StringComparer.Ordinal))
        {
            if (e.Name == skip)
            {
                continue;
            }

            var size = e is FileInfo f ? f.Length : -1;
            var time = e is FileInfo ? e.LastWriteTimeUtc.Ticks : 0;
            text.Append(CultureInfo.InvariantCulture, $"{e.Name} {size} {time}\n");
        }
    }

    private string? ReadStamp()
    {
        try
        {
            return File.Exists(StampPath) ? File.ReadAllText(StampPath) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // Written after the check, so the ZIP's time in it is the one checked; a failed write only costs a check.
    private void WriteStamp(string stamp)
    {
        try
        {
            File.WriteAllText(StampPath, stamp);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warn("Bake", $"{Path.GetFileName(StampPath)} cannot be written ({e.Message}): the blocks are checked at every start");
        }
    }

    // The system ZIP, or null (logged) when it does not open: its content is built again then.
    private ZipPack? OpenZip()
    {
        try
        {
            return ZipPack.Open(ZipPath);
        }
        catch (IOException e)
        {
            Log.Warn("Bake", $"{Path.GetFileName(ZipPath)} does not open ({e.Message}): it is built again");
            return null;
        }
    }

    // An update of the system ZIP; a new ZIP when the one there does not open.
    private ZipPackWriter OpenWriter()
    {
        try
        {
            return ZipPackWriter.Update(ZipPath);
        }
        catch (ZipPackException)
        {
            return new ZipPackWriter(ZipPath);
        }
    }

    /// <summary>Deletes an older build's system pak (<see cref="LegacyPakPath"/>) once the ZIP is
    /// there: it was this engine's own cache of the same content, never a player's file (logged).</summary>
    public void RemoveLegacyPak()
    {
        if (!File.Exists(LegacyPakPath) || !File.Exists(ZipPath))
        {
            return;
        }

        try
        {
            File.Delete(LegacyPakPath);
            Log.Info("Bake", $"{Path.GetFileName(LegacyPakPath)} (an older build's cache) deleted: {Path.GetFileName(ZipPath)} replaces it");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warn("Bake", $"{Path.GetFileName(LegacyPakPath)} (an older build's cache) cannot be deleted ({e.Message})");
        }
    }

    private static bool Fresh(ZipPack zip, ZipPackEntry entry, BakeInputs inputs)
    {
        try
        {
            using var stream = zip.OpenEntry(entry);
            var (hash, stored, ground) = MapBlock.ReadSource(stream);
            return hash == entry.SourceHash && (ground.Length == 0 || zip.TryGetEntry(ground, out _))
                && MapBaker.CurrentSourceHash(inputs, stored) == hash;
        }
        catch (Exception e) when (e is InvalidDataException or IOException or EndOfStreamException)
        {
            return false;
        }
    }

    /// <summary>Bakes <paramref name="maps"/> (in parallel) and writes them into the system ZIP
    /// (an update: the other entries are kept). The mods folder is unmounted while the ZIP is
    /// replaced and mounted again after, when it was. <paramref name="progress"/>(done, total) is
    /// called on the calling thread. Returns the maps baked.</summary>
    public int Bake(IReadOnlyList<int> maps, Action<int, int>? progress = null)
    {
        if (maps.Count == 0)
        {
            return 0;
        }

        var clock = Stopwatch.StartNew();
        var blocks = new MapBlock?[maps.Count];
        var done = 0;
        ExceptionDispatchInfo? error = null;
        // One map per job on the pool's background queue; the caller only reports (it draws the
        // progress, so it never takes a map itself: that could hold the window for seconds).
        var handle = JobSystem.Shared.ScheduleParallelFor(maps.Count, 1, new ActionFor(i =>
        {
            try
            {
                blocks[i] = MapBaker.Bake(data, maps[i], BakeTables.Shipped);
            }
            catch (Exception e)
            {
                Interlocked.CompareExchange(ref error, ExceptionDispatchInfo.Capture(e), null);
            }

            Interlocked.Increment(ref done);
        }), priority: JobPriority.Background);
        var shown = -1;
        while (!handle.IsCompleted)
        {
            if (Volatile.Read(ref done) is var d && d != shown)
            {
                progress?.Invoke(shown = d, maps.Count);
            }

            Thread.Sleep(50);
        }

        error?.Throw();
        progress?.Invoke(maps.Count, maps.Count);
        var baked = clock.Elapsed;

        var remount = _vfs != null;
        Unmount();
        long bytes = 0;
        var grounds = 0;
        using (var writer = OpenWriter())
        {
            writer.CompressionLevel = CompressionLevel;
            var written = new HashSet<string>(VfsPath.Comparer);
            var used = new HashSet<string>(VfsPath.Comparer);
            for (var i = 0; i < maps.Count; i++)
            {
                var path = MapBaker.PathOf(maps[i]);
                written.Add(path);
                if (blocks[i] is { } b)
                {
                    var body = b.ToBytes();
                    bytes += body.Length;
                    writer.Add(path, body, b.SourceHash, Compression);
                    // The shared ground layer, once per WLD (named by its inputs: the same name, the same bytes).
                    if (b.Ground is { } g && used.Add(g.Path))
                    {
                        var layer = g.ToBytes();
                        bytes += layer.Length;
                        writer.Add(g.Path, layer, g.SourceHash, Compression);
                        grounds++;
                    }
                }
                else
                {
                    writer.Remove(path);
                }
            }

            // Grounds no block uses any more (their WLD changed, or an older bake's) go.
            if (writer.Basis is { } basis)
            {
                foreach (var e in basis.Entries.Where(e => !written.Contains(e.Path) && e.Path.EndsWith(".block", StringComparison.OrdinalIgnoreCase)))
                {
                    if (GroundOf(basis, e) is { Length: > 0 } g)
                    {
                        used.Add(g);
                    }
                }

                foreach (var e in basis.Entries.Where(e => e.Path.StartsWith(GroundFolder, StringComparison.OrdinalIgnoreCase) && !used.Contains(e.Path)))
                {
                    writer.Remove(e.Path);
                }
            }

            writer.Commit();
        }

        _last = null;
        Log.Info("Bake", $"{blocks.Count(b => b != null)} of {maps.Count} maps baked in {baked.TotalMilliseconds:0} ms, " +
            $"{bytes / 1024} KB of blocks and {grounds} grounds written in {(clock.Elapsed - baked).TotalMilliseconds:0} ms; " +
            $"{Path.GetFileName(ZipPath)} is {new FileInfo(ZipPath).Length / 1024} KB");
        RemoveLegacyPak();
        if (remount)
        {
            Mount();
        }

        return blocks.Count(b => b != null);
    }

    /// <summary>The folder of the shared ground layers in the ZIP (<see cref="MapGround.PathOf"/>).</summary>
    private const string GroundFolder = "maps/ground/";

    // The ground layer a block in the ZIP uses ("" none, null when it does not read).
    private static string? GroundOf(ZipPack zip, ZipPackEntry entry)
    {
        try
        {
            using var stream = zip.OpenEntry(entry);
            return MapBlock.ReadSource(stream).Ground;
        }
        catch (Exception e) when (e is InvalidDataException or IOException or EndOfStreamException)
        {
            return null;
        }
    }

    // ---------------------------------------------------------------- mission files

    /// <summary>The missions whose file in the system ZIP (<see cref="MissionFile.PathOf"/>) is
    /// missing, unreadable or stale (the table row, the generator or a MAP file it read changed);
    /// every mission with <paramref name="all"/>.</summary>
    public IReadOnlyList<Mission> StaleMissions(bool all = false)
    {
        var table = MissionTable.For(data.Game);
        if (all || !File.Exists(ZipPath) || OpenZip() is not { } zip)
        {
            return table.Missions;
        }

        var stale = new List<Mission>();
        using (zip)
        using (var inputs = new BakeInputs(data))
        {
            foreach (var m in table.Missions)
            {
                if (!zip.TryGetEntry(MissionFile.PathOf(m.Number), out var entry) || !FreshMission(zip, entry, inputs, m, table.After(m)?.Number))
                {
                    stale.Add(m);
                }
            }
        }

        return stale;
    }

    private static bool FreshMission(ZipPack zip, ZipPackEntry entry, BakeInputs inputs, Mission mission, int? next)
    {
        try
        {
            var file = MissionFile.Parse(zip.ReadAllBytes(entry));
            var hash = file.World.SourceHash;
            return hash == entry.SourceHash && MissionGenerator.CurrentSourceHash(inputs, mission, next, file.World.Inputs) == hash;
        }
        catch (Exception e) when (e is WorldFormatException or InvalidDataException or IOException)
        {
            return false;
        }
    }

    /// <summary>Generates <paramref name="missions"/>' files and writes them into the system ZIP
    /// (an update: everything else is kept). Returns the files written.</summary>
    public int BakeMissions(IReadOnlyList<Mission> missions)
    {
        if (missions.Count == 0)
        {
            return 0;
        }

        var clock = Stopwatch.StartNew();
        var files = new List<MissionFile>();
        using (var inputs = new BakeInputs(data))
        {
            files.AddRange(missions.Select(m => MissionGenerator.Build(data, m, inputs)));
        }

        var remount = _vfs != null;
        Unmount();
        using (var writer = OpenWriter())
        {
            writer.CompressionLevel = CompressionLevel;
            foreach (var f in files)
            {
                writer.Add(MissionFile.PathOf(f.Number), f.ToJson(), f.World.SourceHash, Compression);
            }

            writer.Commit();
        }

        Log.Info("Bake", $"{files.Count} mission files generated in {clock.Elapsed.TotalMilliseconds:0} ms");
        RemoveLegacyPak();
        if (remount)
        {
            Mount();
        }

        return files.Count;
    }

    /// <summary>Mission <paramref name="number"/>'s file through the mods (a mod's
    /// <c>missions/MNN.json</c> wins); null when there is none or it is not valid (logged).</summary>
    public MissionFile? LoadMission(int number)
    {
        var path = MissionFile.PathOf(number);
        if (_vfs?.TryReadAllBytes(path) is not { } bytes)
        {
            return null;
        }

        try
        {
            var file = MissionFile.Parse(bytes);
            if (file.Game != data.Game || file.Number != number)
            {
                Log.Warn("Bake", $"{path} is mission {file.Number} of {file.Game}, not mission {number} of {data.Game}: generated instead");
                return null;
            }

            return file;
        }
        catch (WorldFormatException e)
        {
            Log.Warn("Bake", $"{path}: {e.Message}; generated instead");
            return null;
        }
    }

    /// <summary>Mounts the mods folder: the system ZIP (locked, lowest) and every user mod above it
    /// (not the other data set's system mod).</summary>
    public void Mount()
    {
        Unmount();
        RemoveLegacyPak();
        var system = File.Exists(ZipPath) || Directory.Exists(Path.Combine(ModsDirectory, SystemModName(data.Game)))
            ? new[] { SystemModName(data.Game) } : [];
        // The other data set's system mod is not a user mod of this one (its missions/MNN.json
        // would shadow this game's).
        var others = new[] { GameKind.SkyNet, GameKind.FutureShock }.Select(SystemModName).ToHashSet(VfsPath.Comparer);
        var root = new ContentRoot(ModsDirectory);
        var user = root.Discover().Select(m => m.Name).Where(n => !others.Contains(n));
        _vfs = root.Mount(system, user);
    }

    public void Unmount()
    {
        _vfs?.Dispose();
        _vfs = null;
        _last = null;
    }

    /// <summary>MAP.<paramref name="number"/>'s block through the mods (null: none, or it does not read;
    /// the caller bakes it from the raw files then). The last one read is kept.</summary>
    public MapBlock? Load(int number)
    {
        if (_last is { } last && last.Number == number && LayerMismatch(number, last.Block) == null && TerrainMismatch(last.Block) == null)
        {
            return last.Block;
        }

        if (_vfs is not { } vfs || vfs.TryReadAllBytes(MapBaker.PathOf(number)) is not { } bytes)
        {
            return null;
        }

        try
        {
            var block = MapBlock.Read(bytes, vfs.TryReadAllBytes);
            if (TableMismatch(block) is { } table)
            {
                if (_refused.Add(number))
                {
                    Log.Info("Bake", $"{MapBaker.PathOf(number)} was baked with other {table} than the mod's: built from the raw files");
                }

                return null;
            }

            if (LayerMismatch(number, block) is { } layer)
            {
                if (_refused.Add(number))
                {
                    Log.Info("Bake", $"{MapBaker.PathOf(number)} was not baked with {layer}: built from the raw files");
                }

                return null;
            }

            if (TerrainMismatch(block) is { } terrain)
            {
                if (_refused.Add(number))
                {
                    Log.Info("Bake", $"{MapBaker.PathOf(number)} was baked with another {terrain} than the mods': built from the raw files");
                }

                return null;
            }

            _last = (number, block);
            return block;
        }
        catch (InvalidDataException e)
        {
            Log.Warn("Bake", $"{MapBaker.PathOf(number)}: {e.Message}; built from the raw files");
            return null;
        }
    }

    /// <summary>The layer the block must have been baked with and was not (its file, "no layer"
    /// for a block baked with one that is gone); null when it matches. An empty layer is no layer.</summary>
    private string? LayerMismatch(int number, MapBlock block)
    {
        var name = BakeInputs.LayerPrefix + data.LayerName(number);
        using var inputs = new BakeInputs(data, PlayTables ?? BakeTables.Current);
        var now = inputs.Md5Of(name);
        var baked = block.Inputs.Where(i => i.Name.StartsWith(BakeInputs.LayerPrefix, StringComparison.Ordinal)).ToArray();
        if (now.Length == 0)
        {
            return baked.Length == 0 ? null : "no layer";
        }

        return baked.Length == 1 && baked[0].Name == name && baked[0].Md5 == now
            ? null : Mods.MapLayer.PathOf(data.LayerName(number));
    }

    /// <summary>The terrain (WLD.NNN) the block was baked with when the mods give another one now
    /// (<see cref="Mods.MapTerrains"/>); null when it matches.</summary>
    private string? TerrainMismatch(MapBlock block)
    {
        var baked = block.Inputs.Where(i => Mods.MapTerrains.MapOf(i.Name) >= 0).ToArray();
        if (baked.Length == 0)
        {
            return null;
        }

        using var inputs = new BakeInputs(data, PlayTables ?? BakeTables.Current);
        foreach (var i in baked)
        {
            if (inputs.Md5Of(i.Name) != i.Md5)
            {
                return i.Name;
            }
        }

        return null;
    }

    private (VirtualFileSystem? Mods, Dictionary<string, string> Md5)? _tableMd5;
    private readonly HashSet<int> _refused = [];

    /// <summary>The tables the levels are played with, which a block's table inputs must match for
    /// <see cref="Load"/> to give it (null: the tables in effect, <see cref="BakeTables.Current"/>).</summary>
    public BakeTables? PlayTables { get; set; }

    // The first table the block was baked with that the tables in effect give another MD5 for (null: none).
    private string? TableMismatch(MapBlock block)
    {
        var tables = PlayTables ?? BakeTables.Current;
        if (_tableMd5 is not { } cache || !ReferenceEquals(cache.Mods, tables.Mods))
        {
            _tableMd5 = cache = (tables.Mods, new Dictionary<string, string>(StringComparer.Ordinal));
        }

        foreach (var i in block.Inputs.Where(i => i.Name.StartsWith(BakeInputs.TablePrefix, StringComparison.Ordinal)))
        {
            if (!cache.Md5.TryGetValue(i.Name, out var md5))
            {
                using var inputs = new BakeInputs(data, tables);
                cache.Md5[i.Name] = md5 = inputs.Md5Of(i.Name);
            }

            if (md5 != i.Md5)
            {
                return i.Name[BakeInputs.TablePrefix.Length..];
            }
        }

        return null;
    }

    public void Dispose() => Unmount();
}
