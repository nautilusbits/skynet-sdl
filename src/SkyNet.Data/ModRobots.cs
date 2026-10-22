using System.Collections.Immutable;
using RetroEng.Content;
using RetroEng.Json;
using SkyNet.Formats;

namespace SkyNet.Data;

/// <summary>
/// Robot types a mod adds or changes, one flat file per robot: <c>robots/&lt;name&gt;.json</c>
/// (docs/modding.md, "Robots"). Each file is one row over a base row of the enemy type table: the
/// keys it gives replace the base's, a key left out or null keeps it. Flat on purpose (one record,
/// plain numbers and names), so a grid editor maps it 1:1. Lists (segments, death parts, the AIS
/// script and its frame sounds) are taken whole from another type by id.
/// <code>
///   enemy_types.json  rows 0..170 (DOS)
///   robots/a.json     "type": 0    → row 0 changed
///   robots/b.json     "type": 200  → row 200 added (171..199 empty placeholders)
/// </code>
/// </summary>
public static class ModRobots
{
    /// <summary>Where the robot files are in a mod.</summary>
    public const string Folder = "robots/";

    /// <summary>The highest type id a mod robot may take (a spawn sprite's type is 16-bit; a map
    /// layer marker's <c>enemy</c> reaches it too).</summary>
    public const int MaxType = 1023;

    /// <summary>The keys a robot file may have.</summary>
    public static readonly ImmutableArray<string> Keys =
    [
        "type", "base", "model", "look", "state", "hitPoints", "speed", "near", "turn", "axis", "angleMin", "angleMax",
        "range", "altitude", "verticalAccel", "avoidRadius", "forwardSpeed", "stand",
        "fireAmmo", "fireRate", "fireRange", "fireSpeed", "fireMuzzleX", "fireMuzzleY", "fireMuzzleZ", "fireAimGate",
        "script", "segments", "death", "engineSound", "comment",
    ];

    // Record dword +0x10 (death spec) and +0x18 (stand height).
    private const int DeathSpec = 4, StandHeight = 6;

    /// <summary><paramref name="table"/> with the robot files of <paramref name="mods"/> applied (in
    /// file-name order); the same rows when there are none. A bad file throws
    /// <see cref="InvalidDataException"/> naming the mod, the file and the place in it.</summary>
    public static ImmutableArray<EnemyType> Apply(ImmutableArray<EnemyType> table, VirtualFileSystem? mods)
    {
        var files = mods?.Enumerate(Folder).Where(p => p.EndsWith(".json", StringComparison.OrdinalIgnoreCase)).ToArray() ?? [];
        if (files.Length == 0)
        {
            return table;
        }

        var rows = new List<EnemyType>(table);
        var owner = new Dictionary<int, string>();
        foreach (var path in files)
        {
            var label = $"{mods!.Resolve(path)?.Name}:{path}";
            try
            {
                var file = JsonItem.Parse(mods.ReadAllBytes(path)).AsObject();
                var type = file.Member("type").AsInt(0, MaxType);
                if (!owner.TryAdd(type, label))
                {
                    throw file.Member("type").Error($"type {type} is taken by {owner[type]}");
                }

                Put(rows, Read(file, type, table));
            }
            catch (JsonDataException e)
            {
                throw new InvalidDataException($"Robot {label}: {e.Message}", e);
            }
        }

        return [.. rows];
    }

    // Grows rows to reach type, with empty placeholders between, and sets it.
    private static void Put(List<EnemyType> rows, EnemyType row)
    {
        while (rows.Count <= row.Index)
        {
            rows.Add(new EnemyType { Index = rows.Count });
        }

        rows[row.Index] = row;
    }

    // One robot file as a row: its base's fields, the file's on top.
    private static EnemyType Read(JsonItem f, int type, ImmutableArray<EnemyType> table)
    {
        foreach (var m in f.Members)
        {
            if (!Keys.Contains(m.Key))
            {
                throw m.Value.Error($"\"{m.Key}\" is not a robot key");
            }
        }

        // An existing type is its own base; a new one names its base.
        var isNew = type >= table.Length;
        if (isNew && f.Opt("base") == null)
        {
            throw f.Error($"type {type} is new: give \"base\", the type it starts from");
        }

        var b = Row(f, "base", table) ?? table[type];
        int? Opt(string key) => f.Opt(key)?.AsInt();
        int? Field(string key, int? now) => f.Opt(key) is { } v ? v.AsInt() : now;

        var model = f.Opt("model")?.AsString() ?? b.ModelFile;
        var record = b.Record.IsDefaultOrEmpty ? new uint[7] : b.Record.ToArray();
        var death = Row(f, "death", table) ?? b;
        var script = Row(f, "script", table) ?? b;
        record[DeathSpec] = death.Record.Length > DeathSpec ? death.Record[DeathSpec] : 0;
        if (Opt("stand") is { } stand)
        {
            record[StandHeight] = unchecked((uint)stand);
        }

        return new EnemyType
        {
            Index = type,
            Record = [.. record],
            Name = f.Opt("model") != null ? Path.GetFileNameWithoutExtension(model).ToLowerInvariant() : b.Name,
            ModelFile = model,
            Look = f.Opt("look")?.AsString() ?? b.Look,
            State = Field("state", b.State)!.Value,
            Params = b.Params,
            HitPoints = f.Opt("hitPoints")?.AsInt(0, 0x7FFF) ?? b.HitPoints,
            Speed = Field("speed", b.Speed),
            Near = Field("near", b.Near),
            Turn = Field("turn", b.Turn),
            Axis = Field("axis", b.Axis),
            AngleMin = Field("angleMin", b.AngleMin),
            AngleMax = Field("angleMax", b.AngleMax),
            Range = Field("range", b.Range),
            Altitude = Field("altitude", b.Altitude),
            VerticalAccel = Field("verticalAccel", b.VerticalAccel),
            AvoidRadius = Field("avoidRadius", b.AvoidRadius),
            ForwardSpeed = Field("forwardSpeed", b.ForwardSpeed),
            Fire = Fire(f, b.Fire),
            ScriptVa = script.ScriptVa,
            Events = script.Events,
            EngineSound = Field("engineSound", b.EngineSound),
            Death = death.Death,
            DeathKind = death.DeathKind,
            Segments = (Row(f, "segments", table) ?? b).Segments,
        };
    }

    // The type a key names by id (null: the key is left out).
    private static EnemyType? Row(JsonItem f, string key, ImmutableArray<EnemyType> table) =>
        f.Opt(key) is { } v ? table[v.AsInt(0, table.Length - 1)] : null;

    // The base's fire with the file's fire keys on top; none when rate or range is not positive (as DOS).
    private static AisFire? Fire(JsonItem f, AisFire? b)
    {
        var o = b ?? default;
        int Get(string key, int now) => f.Opt(key)?.AsInt() ?? now;
        var fire = new AisFire(Get("fireMuzzleX", o.MuzzleX), Get("fireMuzzleY", o.MuzzleY), Get("fireMuzzleZ", o.MuzzleZ),
            Get("fireAmmo", o.Ammo), Get("fireSpeed", o.Speed), Get("fireRate", o.Rate), Get("fireRange", o.Range),
            Get("fireAimGate", o.AimGate));
        return fire.Rate > 0 && fire.Range > 0 ? fire : null;
    }
}
