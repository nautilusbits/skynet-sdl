using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using RetroEng.Content;
using RetroEng.Diagnostics;
using SkyNet.Data;
using SkyNet.Game.Menu;
using SkyNet.Game.Weapons;

namespace SkyNet.Game.Enhanced;

/// <summary>
/// How the enhanced look's effects draw (docs/modding.md#effects): the table <c>effects</c>, one
/// row per effect, a mod rule (no shipped file: the rows below are the game's own, a mod's rows
/// replace them by name). The effects read their row each frame, so a changed table shows at once.
/// <code>
///   Defaults (the code's numbers) ◄── tables/effects.json of each mod, by "effect"
///        │
///        ▼
///   EffectTable.Current ──► FireFlames, Atmosphere, ImpactEffects, Storm
///   EffectTable.With(row) ──► EffectPreview (the editor's unsaved row)
/// </code>
/// </summary>
public sealed class EffectTable
{
    /// <summary>The effects, by name.</summary>
    public const string Fire = "fire", Embers = "embers", Smoke = "smoke", SmokeColumns = "smoke_columns", Motes = "motes",
        Explosion = "explosion", ExplosionDust = "explosion_dust", ExplosionDebris = "explosion_debris",
        ExplosionSmoke = "explosion_smoke", ImpactDust = "impact_dust", ImpactSparks = "impact_sparks", Sparks = "sparks",
        Lightning = "lightning";

    /// <summary>The columns an effect may read (<see cref="Reads"/>), as the table names them.</summary>
    public const string Low = "low", High = "high", Life = "life", Size = "size", Speed = "speed", Rise = "rise", Spread = "spread",
        Gravity = "gravity", Intensity = "intensity", Flicker = "flicker", R = "r", G = "g", B = "b";

    private static readonly string[] Colour = [Intensity, R, G, B];

    /// <summary>Each effect's row as the game draws it, and the columns it reads.</summary>
    private static readonly (EffectRow Row, string[] Reads)[] Own =
    [
        (Row(Fire, FireFlames.Tongues, 0f), [Low, High, Size, Speed, Spread, Flicker, .. Colour]),
        (Row(Embers, FireFlames.EmbersPerFire, FireFlames.EmberLife), [Low, High, Life, Size, Speed, Spread, .. Colour]),
        (Row(Smoke, Atmosphere.PuffsPerSource, Atmosphere.SmokeLife), [Low, High, Life, Size, Speed, Rise, Spread, .. Colour]),
        (Row(SmokeColumns, Atmosphere.ColumnPuffs, Atmosphere.ColumnLife), [Low, High, Life, Size, Speed, Rise, Spread, .. Colour]),
        (Row(Motes, Atmosphere.MoteCount, 0f), [Low, High, Size, Speed, .. Colour]),
        (Row(Explosion, ImpactEffects.BlastFire, ImpactEffects.FireballLife), [Low, High, Life, Size, Rise, Spread, .. Colour]),
        (Row(ExplosionDust, ImpactEffects.BlastRing, ImpactEffects.RingLife), [Low, High, Life, Size, Spread, .. Colour]),
        (Row(ExplosionDebris, ImpactEffects.BlastSparks, ImpactEffects.BlastSparkLife), [Low, High, Life, Size, Speed, Gravity, .. Colour]),
        (Row(ExplosionSmoke, ImpactEffects.BlastSmoke, 0f), [Low, High, Size, Speed, Rise, Spread, .. Colour]),
        (Row(ImpactDust, ImpactEffects.PuffDust, 0f), [Low, High, Size, Speed, Rise, .. Colour]),
        (Row(ImpactSparks, ImpactEffects.PuffSparks, ImpactEffects.PuffSparkLife), [Low, High, Life, Size, Speed, Gravity, .. Colour]),
        (Row(Sparks, ImpactEffects.ThrownSparks, ImpactEffects.SparkTime), [Low, High, Life, Size, Spread, Gravity, .. Colour]),
        (Row(Lightning, [0, 0, 0], 0f), [Size, .. Colour]),
    ];

    private static readonly FrozenDictionary<string, string[]> ReadsOf = Own.ToFrozenDictionary(o => o.Row.Effect, o => o.Reads);

    private sealed record Loaded(VirtualFileSystem? Mods, EffectTable Table);

    private static Loaded? _loaded;

    private readonly FrozenDictionary<string, EffectRow> _rows;

    private EffectTable(IEnumerable<EffectRow> rows) =>
        _rows = rows.Where(r => ReadsOf.ContainsKey(Key(r.Effect))).ToFrozenDictionary(r => Key(r.Effect));

    /// <summary>The effects' names, in the table's order.</summary>
    public static ImmutableArray<string> Names { get; } = [.. Own.Select(o => o.Row.Effect)];

    /// <summary>The game's own rows (no mod).</summary>
    public static EffectTable Defaults { get; } = new(Own.Select(o => o.Row));

    /// <summary>The table a mod's rows lie over (the editor's base).</summary>
    public static TableFile<ImmutableArray<EffectRow>> Base => new(TableNames.Effects, "", "mod rule", "", "EffectTable", [.. Own.Select(o => o.Row)]);

    /// <summary>The table of the mounted mods (<see cref="GameTables.Mods"/>), read again when they
    /// change; a file that does not read is logged and the game's rows stay.</summary>
    public static EffectTable Current
    {
        get
        {
            var mods = GameTables.Mods;
            var cached = Volatile.Read(ref _loaded);
            if (cached != null && ReferenceEquals(cached.Mods, mods))
            {
                return cached.Table;
            }

            cached = new Loaded(mods, Load(mods));
            Volatile.Write(ref _loaded, cached);
            return cached.Table;
        }
    }

    /// <summary>The rows of <paramref name="mods"/> over <see cref="Base"/>; throws
    /// <see cref="InvalidDataException"/> for a file that does not read.</summary>
    public static ImmutableArray<EffectRow> Rows(VirtualFileSystem? mods) =>
        GameTables.Layer(TableNames.Effects, TableSchemas.Effects, Base, mods).Rows;

    /// <summary>The table of <paramref name="mods"/>; throws <see cref="InvalidDataException"/> as <see cref="Rows"/>.</summary>
    public static EffectTable Of(VirtualFileSystem? mods) => mods == null ? Defaults : new(Rows(mods));

    /// <summary>Effect <paramref name="effect"/>'s row ("fire"; any case). Throws
    /// <see cref="KeyNotFoundException"/> for a name not in <see cref="Names"/>.</summary>
    public EffectRow this[string effect] => _rows[Key(effect)];

    /// <summary>This table with <paramref name="row"/> in place of its effect's (a preview of an unsaved edit).</summary>
    public EffectTable With(EffectRow row)
    {
        if (!ReadsOf.ContainsKey(Key(row.Effect)))
        {
            throw new KeyNotFoundException($"No effect {row.Effect}");
        }

        return new(_rows.Where(r => r.Key != Key(row.Effect)).Select(r => r.Value).Append(row));
    }

    /// <summary>The columns <paramref name="effect"/> reads (the others change nothing): "low", "size"...</summary>
    public static IReadOnlyList<string> Reads(string effect) => ReadsOf[Key(effect)];

    /// <summary>A row's particles at <paramref name="level"/>: none at PARTICLES OFF.</summary>
    public static int Count(EffectRow row, ParticleLevel level) => level switch
    {
        ParticleLevel.Low => row.Low,
        ParticleLevel.High => row.High,
        _ => 0,
    };

    /// <summary>A row's colour filter (linear multipliers).</summary>
    public static Vector3 Tint(EffectRow row) => new((float)row.R, (float)row.G, (float)row.B);

    /// <summary>A row's colour filter times its intensity (an added effect's light).</summary>
    public static Vector3 Light(EffectRow row) => Tint(row) * (float)row.Intensity;

    private static EffectTable Load(VirtualFileSystem? mods)
    {
        try
        {
            var rows = mods == null ? Base.Rows : Rows(mods);
            var changed = rows.Count(r => !Defaults._rows.TryGetValue(Key(r.Effect), out var own) || own != r with { Effect = own.Effect });
            if (changed > 0)
            {
                Log.Info("Mods", $"Table {TableNames.Effects}: {changed} effects draw changed");
            }

            return new(rows);
        }
        catch (InvalidDataException e)
        {
            Log.Warn("Mods", $"{e.Message}; effects draw as the game's");
            return Defaults;
        }
    }

    private static string Key(string effect) => effect.ToLowerInvariant();

    // The game's row: counts at OFF, LOW, HIGH (OFF is always none), its life (as written: 2.4,
    // not 2.4000000953674316; the same float again), the rest as drawn.
    private static EffectRow Row(string effect, int[] counts, float life) =>
        new(effect, counts[(int)ParticleLevel.Low], counts[(int)ParticleLevel.High], double.Parse(life.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture),
            1, 1, 1, 1, 1, 1, 1, 1, 1, 1);
}
