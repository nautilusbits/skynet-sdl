using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using RetroEng.Content;
using RetroEng.Json;
using SkyNet.Data;
using SkyNet.Formats;

namespace SkyNet.Edit;

/// <summary>
/// The robot types in the grid: one row a type, the columns the keys of a robot file
/// (<see cref="ModRobots"/>, docs/modding.md#robots). The base is the enemy type table with the robot
/// files of the mods under the save mod; a changed row goes to the save mod as one
/// <c>robots/&lt;name&gt;.json</c> holding the keys that differ from its base type's table row, a new row
/// is a new type over a table type (<c>base</c>).
/// <code>
///   enemy_types (in effect) ─► table row of base ─┐
///   robots/*.json of the mods under the save mod ─┼─► Base     save mod's robots/*.json ─► Rows
///   robots/type-200.json  { "type": 200, "base": 0, "hitPoints": 300 }
/// </code>
/// </summary>
public sealed class RobotTableSource(IReadOnlyList<string> models, IReadOnlyList<string> sounds) : ITableSource
{
    /// <summary>The grid's name for the robot types.</summary>
    public const string TableName = "robots";

    // The AI states a combo names (docs/formats/ais.md); others show as their number.
    private static readonly (int State, string Name)[] StateNames =
        [(2, "turret"), (6, "hover"), (7, "walker"), (8, "head"), (9, "flyer"), (10, "machine"), (11, "path"), (13, "tank")];

    // Record dword +0x18: the stand height ModRobots sets from "stand".
    private const int StandHeight = 6;

    // The table in effect under the save mod; the files each type's row comes from (save mod's, the mods' under it).
    private ImmutableArray<EnemyType> _table = [];
    private readonly Dictionary<int, string> _own = [], _under = [];

    public string Name => TableName;

    public bool CanAdd => true;

    public IReadOnlyList<GridColumn> Columns { get; } = MakeColumns(models, sounds);

    private static List<GridColumn> MakeColumns(IReadOnlyList<string> models, IReadOnlyList<string> sounds)
    {
        var columns = new List<GridColumn>();
        foreach (var key in ModRobots.Keys)
        {
            columns.Add(key switch
            {
                "type" => new GridColumn(key, CellKind.Int, ReadOnly: true),
                "model" => new GridColumn(key, CellKind.Choice, [.. models.Select(m => new GridChoice(m, m))]),
                "look" or "comment" => new GridColumn(key, CellKind.Text),
                "state" => new GridColumn(key, CellKind.Choice, [.. StateNames.Select(s => new GridChoice((long)s.State, $"{s.State} {s.Name}"))]),
                "engineSound" => new GridColumn(key, CellKind.Choice, TableHints.SoundChoices(sounds)),
                _ => new GridColumn(key, CellKind.Int),
            });
        }

        return columns;
    }

    private int Col(string key) => ModRobots.Keys.IndexOf(key);

    public (List<object?[]> Base, List<object?[]> Rows) Load(VirtualFileSystem? under, string? folder)
    {
        _table = GameTables.Load(TableNames.EnemyTypes, TableSchemas.EnemyTypes, under).Rows;
        _own.Clear();
        _under.Clear();
        var rows = new SortedDictionary<int, object?[]>();
        for (var i = 0; i < _table.Length; i++)
        {
            rows[i] = Flat(_table[i]);
        }

        // The mods under the save mod, highest first: a file name a higher mod has hides the lower's.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mount in under?.Mounts ?? [])
        {
            foreach (var path in mount.Mount.Enumerate(ModRobots.Folder).Where(IsJson).Where(seen.Add))
            {
                Over(rows, mount.Mount.TryReadAllBytes(path)!, $"{mount.Name}:{path}", Path.GetFileName(path), _under);
            }
        }

        var baseRows = rows.Values.Select(r => (object?[])r.Clone()).ToList();
        var dir = folder != null ? Path.Combine(folder, ModRobots.Folder) : null;
        IEnumerable<string> own = dir != null && Directory.Exists(dir) ? Directory.GetFiles(dir, "*.json").Order(StringComparer.Ordinal) : [];
        foreach (var file in own)
        {
            Over(rows, File.ReadAllBytes(file), file, Path.GetFileName(file), _own);
        }

        return (baseRows, [.. rows.Values]);
    }

    private static bool IsJson(string path) => path.EndsWith(".json", StringComparison.OrdinalIgnoreCase);

    // A robot file's row over rows (its base's table row, the file's keys on top); its file name kept by type.
    private void Over(SortedDictionary<int, object?[]> rows, byte[] bytes, string label, string fileName, Dictionary<int, string> files)
    {
        try
        {
            var f = JsonItem.Parse(bytes).AsObject();
            var type = f.Member("type").AsInt(0, ModRobots.MaxType);
            var b = f.Opt("base")?.AsInt(0, _table.Length - 1) ?? (type < _table.Length ? type : throw f.Error("a new type needs \"base\""));
            var row = Flat(_table[b]);
            row[Col("type")] = (long)type;
            foreach (var (key, value) in f.Members)
            {
                if (ModRobots.Keys.IndexOf(key) is var c and >= 0 && !value.IsNull && key != "type")
                {
                    row[c] = TableGrid.CellOf(value, Columns[c].Kind == CellKind.Text || value.Kind == JsonKind.String ? CellKind.Text : CellKind.Int);
                }
            }

            rows[type] = row;
            files[type] = fileName;
        }
        catch (JsonDataException e)
        {
            throw new InvalidDataException($"Robot {label}: {e.Message}", e);
        }
    }

    // A table type as a robot file's keys: lists (script, segments, death) by its own id.
    private object?[] Flat(EnemyType t)
    {
        var fire = t.Fire ?? default;
        var row = new object?[ModRobots.Keys.Length];
        void Put(string key, object? value) => row[Col(key)] = value is int i ? (long)i : value;
        Put("type", t.Index);
        Put("base", t.Index);
        Put("model", t.ModelFile);
        Put("look", t.Look ?? "");
        Put("state", t.State);
        Put("hitPoints", t.HitPoints);
        Put("speed", t.Speed);
        Put("near", t.Near);
        Put("turn", t.Turn);
        Put("axis", t.Axis);
        Put("angleMin", t.AngleMin);
        Put("angleMax", t.AngleMax);
        Put("range", t.Range);
        Put("altitude", t.Altitude);
        Put("verticalAccel", t.VerticalAccel);
        Put("avoidRadius", t.AvoidRadius);
        Put("forwardSpeed", t.ForwardSpeed);
        Put("stand", t.Record.Length > StandHeight ? unchecked((int)t.Record[StandHeight]) : 0);
        Put("fireAmmo", fire.Ammo);
        Put("fireRate", fire.Rate);
        Put("fireRange", fire.Range);
        Put("fireSpeed", fire.Speed);
        Put("fireMuzzleX", fire.MuzzleX);
        Put("fireMuzzleY", fire.MuzzleY);
        Put("fireMuzzleZ", fire.MuzzleZ);
        Put("fireAimGate", fire.AimGate);
        Put("script", t.Index);
        Put("segments", t.Index);
        Put("death", t.Index);
        Put("engineSound", t.EngineSound);
        Put("comment", "");
        return row;
    }

    public IReadOnlyList<string> Save(string folder, IReadOnlyList<object?[]> baseRows, IReadOnlyList<object?[]> rows)
    {
        var under = baseRows.ToDictionary(TableGrid.KeyOf);
        var dir = Path.Combine(folder, ModRobots.Folder);
        var writes = new List<(string Name, byte[]? Bytes)>();
        foreach (var row in rows)
        {
            var type = (int)(long)row[0]!;
            var changed = under.GetValueOrDefault(TableGrid.KeyOf(row)) is not { } b || !b.SequenceEqual(row, CellComparer.Instance);
            if (changed)
            {
                // The save mod's file for the type, else the lower mod's (the same name hides it), else a new one.
                var name = _own.GetValueOrDefault(type) ?? _under.GetValueOrDefault(type) ?? string.Create(CultureInfo.InvariantCulture, $"type-{type:D3}.json");
                writes.Add((name, Encoding.UTF8.GetBytes(Write(row))));
            }
            else if (_own.TryGetValue(type, out var stale))
            {
                writes.Add((stale, null));
            }
        }

        // A saved row since taken out of the grid (a new type reverted).
        var kept = rows.Select(r => (int)(long)r[0]!).ToHashSet();
        writes.AddRange(_own.Where(o => !kept.Contains(o.Key)).Select(o => (o.Value, (byte[]?)null)));

        // What the game would read: the save mod's robot files as they will be, over the table.
        Check(writes);
        var done = new List<string>();
        foreach (var (name, bytes) in writes)
        {
            var path = Path.Combine(dir, name);
            if (bytes == null)
            {
                File.Delete(path);
                done.Add(ModRobots.Folder + name + " (removed)");
                continue;
            }

            Directory.CreateDirectory(dir);
            File.WriteAllBytes(path, bytes);
            done.Add(ModRobots.Folder + name);
        }

        return done;
    }

    /// <summary>The robot types <paramref name="rows"/> (read now) make over an enemy type table, as the game reads
    /// them once saved (<see cref="GameTables.RobotResolver"/>): a robot file per row that differs from its table type.
    /// <see cref="InvalidDataException"/> when a row would not read.</summary>
    public Func<ImmutableArray<EnemyType>, ImmutableArray<EnemyType>> Resolver(IReadOnlyList<object?[]> rows)
    {
        var files = new Dictionary<string, byte[]>();
        foreach (var row in rows)
        {
            var type = (int)(long)row[0]!;
            if (type < _table.Length && row.SequenceEqual(Flat(_table[type]), CellComparer.Instance))
            {
                continue;
            }

            files[ModRobots.Folder + string.Create(CultureInfo.InvariantCulture, $"type-{type:D3}.json")] = Encoding.UTF8.GetBytes(Write(row));
        }

        ImmutableArray<EnemyType> Apply(ImmutableArray<EnemyType> table)
        {
            using var vfs = new VirtualFileSystem();
            vfs.Mount(new MemoryMount(files), 0, "unsaved");
            return ModRobots.Apply(table, vfs);
        }

        // A row that does not read throws here, not where the game asks.
        Apply(_table);
        return Apply;
    }

    // Throws InvalidDataException when a file to write would not read.
    private void Check(List<(string Name, byte[]? Bytes)> writes)
    {
        var mem = new MemoryMount(writes.Where(w => w.Bytes != null).ToDictionary(w => ModRobots.Folder + w.Name, w => w.Bytes!));
        using var vfs = new VirtualFileSystem();
        vfs.Mount(mem, 0, "save");
        ModRobots.Apply(_table, vfs);
    }

    // One robot file: type, base when it is not the type itself, then the keys that differ from the base's table row.
    private string Write(object?[] row)
    {
        var type = (long)row[0]!;
        var b = (int)(long)row[Col("base")]!;
        var table = Flat(_table[b]);
        var keys = new List<string> { $"\"type\": {type}" };
        if (b != type)
        {
            keys.Add($"\"base\": {b}");
        }

        for (var c = 0; c < row.Length; c++)
        {
            var key = Columns[c].Key;
            if (key is "type" or "base" || TableGrid.Same(row[c], table[c]) || (key == "comment" && (string?)row[c] == ""))
            {
                continue;
            }

            keys.Add($"\"{key}\": {TableGrid.JsonOf(row[c])}");
        }

        return "{ " + string.Join(", ", keys) + " }\n";
    }

    public object?[]? NewRow(object?[] from, IReadOnlyList<object?[]> rows)
    {
        var type = Math.Max(_table.Length, rows.Max(r => (int)(long)r[0]!) + 1);
        if (type > ModRobots.MaxType)
        {
            return null;
        }

        var row = (object?[])from.Clone();
        row[0] = (long)type;
        return row;
    }

    // Files in memory, for Check.
    private sealed class MemoryMount(Dictionary<string, byte[]> files) : IVfsMount
    {
        public string Source => "save";

        public bool Exists(string path) => files.ContainsKey(path);

        public Stream? TryOpen(string path) => files.TryGetValue(path, out var b) ? new MemoryStream(b) : null;

        public byte[]? TryReadAllBytes(string path) => files.GetValueOrDefault(path);

        public IEnumerable<string> Enumerate(string prefix) => files.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal);

        public void Dispose()
        {
        }
    }
}
