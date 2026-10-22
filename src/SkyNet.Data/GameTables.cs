using System.Collections.Concurrent;
using System.Collections.Immutable;
using RetroEng.Content;
using RetroEng.Json;
using SkyNet.Formats;

namespace SkyNet.Data;

/// <summary>
/// The tables SkyNET's DOS program keeps in its image, as shipped in data/tables and embedded in
/// this assembly. A file <c>tables/&lt;name&gt;.json</c> in the mounted user mods (<see cref="Mods"/>,
/// folders or ZIPs; the highest mod that has the file wins, table by table) replaces the embedded
/// one. Each table is parsed once and cached.
/// </summary>
public static class GameTables
{
    /// <summary>The mods and the tables parsed from them, swapped as one: a worker reading a table
    /// while the mods change caches it in the snapshot it read from, never in the new one.</summary>
    private sealed record Source(VirtualFileSystem? Mods, ConcurrentDictionary<string, object> Cache);

    private static Source _source = new(null, new());

    /// <summary>The user mods' files whose <c>tables/&lt;name&gt;.json</c> replace the embedded
    /// tables (SkyNet.Game.Mods.ModContent mounts them); null for none.</summary>
    public static VirtualFileSystem? Mods
    {
        get => Volatile.Read(ref _source).Mods;
        set => Volatile.Write(ref _source, new Source(value, new()));
    }

    private static Func<ImmutableArray<EnemyType>, ImmutableArray<EnemyType>>? _robots;

    /// <summary>The robot types over the enemy type table in place of the mounted mods' robot files
    /// (<see cref="ModRobots"/>): the editor's unsaved robots grid, as GameData.LayerResolver gives its
    /// unsaved layers. Read for <see cref="Mods"/> only; null: the mods' files. Setting it drops the
    /// parsed tables.</summary>
    public static Func<ImmutableArray<EnemyType>, ImmutableArray<EnemyType>>? RobotResolver
    {
        get => Volatile.Read(ref _robots);
        set
        {
            Volatile.Write(ref _robots, value);
            Volatile.Write(ref _source, new Source(Mods, new()));
        }
    }

    /// <summary>Table <paramref name="name"/>'s path in a mod.</summary>
    public static string PathOf(string name) => $"tables/{name}.json";

    /// <summary>The embedded table file's bytes (the shipped one, never a mod's); null when absent.</summary>
    public static byte[]? EmbeddedBytes(string name)
    {
        using var s = typeof(GameTables).Assembly.GetManifestResourceStream($"tables/{name}.json");
        if (s == null)
        {
            return null;
        }

        using var m = new MemoryStream();
        s.CopyTo(m);
        return m.ToArray();
    }

    /// <summary>Where table <paramref name="name"/> comes from: "mod:tables/name.json" (the highest mod
    /// that has it), or "embedded".</summary>
    public static string SourceOf(string name) => ModFiles(name, Mods).FirstOrDefault().Label ?? "embedded";

    // The mod files of table name, highest mod first, read now.
    private static List<(string Label, byte[] Bytes)> ModFiles(string name, VirtualFileSystem? mods)
    {
        var path = VfsPath.Normalize(PathOf(name));
        var files = new List<(string, byte[])>();
        foreach (var mount in mods?.Mounts ?? [])
        {
            if (mount.Mount.TryReadAllBytes(path) is { } bytes)
            {
                files.Add(($"{mount.Name}:{path}", bytes));
            }
        }

        return files;
    }

    /// <summary>Table <paramref name="name"/>, from the mounted mods or the embedded copy.</summary>
    public static TableFile<T> Load<T>(string name, TableSchema<T> schema) => Load(name, schema, Mods);

    /// <summary>Table <paramref name="name"/>: the embedded copy with the <c>tables/name.json</c> of
    /// <paramref name="mods"/> laid over it (<see cref="Layer"/>).</summary>
    public static TableFile<T> Load<T>(string name, TableSchema<T> schema, VirtualFileSystem? mods)
    {
        var bytes = EmbeddedBytes(name) ?? throw new InvalidOperationException($"Table {name} is not embedded");
        return Layer(name, schema, Parsed(name, "embedded", bytes, b => schema.Read(JsonItem.Parse(b))), mods);
    }

    /// <summary><paramref name="under"/> with each mod's <c>tables/name.json</c> laid over it in load
    /// order (<see cref="TableSchema{T}.Apply"/>: a keyed table takes a mod's rows by key, so two mods
    /// can change different rows). A file that does not read throws <see cref="InvalidDataException"/>
    /// naming the table, the mod and the file; the highest mod's is reported first.</summary>
    public static TableFile<T> Layer<T>(string name, TableSchema<T> schema, TableFile<T> under, VirtualFileSystem? mods)
    {
        var files = ModFiles(name, mods).Select(f => (f.Label, Json: Parsed(name, f.Label, f.Bytes, b => JsonItem.Parse(b)))).ToList();
        for (var i = files.Count - 1; i >= 0; i--)
        {
            var (label, json) = files[i];
            under = Parsed(name, label, json, j => schema.Apply(under, j));
        }

        return under;
    }

    // read over the input, its JSON errors said with the table and the file.
    private static TOut Parsed<TIn, TOut>(string name, string label, TIn input, Func<TIn, TOut> read)
    {
        try
        {
            return read(input);
        }
        catch (JsonDataException e)
        {
            throw new InvalidDataException($"Table {name} ({label}): {e.Message}", e);
        }
    }

    private static T Get<T>(string name, Func<VirtualFileSystem?, T> load) where T : notnull
    {
        var source = Volatile.Read(ref _source);
        return (T)source.Cache.GetOrAdd(name, static (_, s) => s.Load(s.Source.Mods), (Source: source, Load: load));
    }

    public static ImmutableArray<AmmoRow> Ammo => Get(TableNames.Ammo, m => Load(TableNames.Ammo, TableSchemas.Ammo, m).Rows);
    public static ImmutableArray<WeaponRow> Weapons => Get(TableNames.Weapons, m => Load(TableNames.Weapons, TableSchemas.Weapons, m).Rows);
    public static ImmutableArray<PoolRow> Pools => Get(TableNames.Pools, m => Load(TableNames.Pools, TableSchemas.Pools, m).Rows);
    public static StartLists StartLists => Get(TableNames.StartLists, m => Load(TableNames.StartLists, TableSchemas.StartLists, m).Rows);
    public static ImmutableArray<PieceRow> Pieces => Get(TableNames.Pieces, m => Load(TableNames.Pieces, TableSchemas.Pieces, m).Rows);
    public static ImmutableArray<DestructionRow> Destruction => Get(TableNames.Destruction, m => Load(TableNames.Destruction, TableSchemas.Destruction, m).Rows);
    public static ImmutableArray<DropListRow> DropLists => Get(TableNames.DropLists, m => Load(TableNames.DropLists, TableSchemas.DropLists, m).Rows);
    public static LocationTable Locations => Get(TableNames.Locations, m => Load(TableNames.Locations, TableSchemas.Locations, m).Rows);
    public static MissionEnemies MissionEnemies => Get(TableNames.MissionEnemies, m => Load(TableNames.MissionEnemies, TableSchemas.MissionEnemies, m).Rows);

    /// <summary>The enemy type table of <paramref name="mods"/> with their robot files
    /// (<see cref="ModRobots"/>) applied: row i is type i.</summary>
    public static ImmutableArray<EnemyType> EnemyTypes(VirtualFileSystem? mods) =>
        Robots(Load(TableNames.EnemyTypes, TableSchemas.EnemyTypes, mods).Rows, mods);

    // The robot files of mods over table; the resolver's rows instead when mods are the mounted ones.
    private static ImmutableArray<EnemyType> Robots(ImmutableArray<EnemyType> table, VirtualFileSystem? mods) =>
        RobotResolver is { } resolve && ReferenceEquals(mods, Mods) ? resolve(table) : ModRobots.Apply(table, mods);

    /// <summary>The enemy AI data: the type table (with the mods' robots), the AIS ops and animation
    /// blocks its scripts reach, and the ammo types.</summary>
    public static AisData Ais => Get("ais", m =>
    {
        var types = Load(TableNames.EnemyTypes, TableSchemas.EnemyTypes, m);
        var rows = Robots(types.Rows, m);
        var ops = Load(TableNames.AisOps, TableSchemas.AisOps, m).Rows;
        var anims = Load(TableNames.AisAnims, TableSchemas.AisAnims, m).Rows;
        var ammo = Ammo.Select(a => new AmmoType(a.Index, a.Callback, FamilyNumber(a.Family), a.Model,
            a.Effect > 0 ? a.Effect >> 7 : 0, a.Damage, a.Size, a.Life, a.FireSound, a.ImpactSound));
        var build = types.Source.EndsWith("v1.00", StringComparison.Ordinal) ? AisData.V100 : AisData.V101;
        return new AisData(build, rows, ammo, ops, anims);
    });

    /// <summary>The ammo family as AisData numbers it: 0 hitscan, 1 gravity, 2 bolt.</summary>
    public static int FamilyNumber(string family) => family switch
    {
        "gravity" => 1,
        "bolt" => 2,
        _ => 0,
    };
}
