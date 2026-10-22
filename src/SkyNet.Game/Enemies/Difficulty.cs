using System.Collections.Immutable;
using RetroEng.Content;
using SkyNet.Data;

namespace SkyNet.Game.Enemies;

/// <summary>The difficulty settings, LOW / MEDIUM / HIGH.</summary>
public enum DifficultyLevel { Low, Medium, High }

/// <summary>
/// The four multipliers DIFFICULTY changes, and nothing else (FUN_0013b300 copies four 16.16
/// values out of the table at 0x5c210, rows {1, 4, 7} for LOW/MED/HIGH; docs damage-hud.md):
/// the robots' rate of fire, the damage the player's shots deal to machines, the damage
/// projectiles deal to the player, and the armour's regeneration (a fraction of a full bar per
/// second). The numbers are the Godot port's settings.gd table.
/// </summary>
public readonly record struct Difficulty(float EnemyFire, float DamageToEnemy, float DamageToPlayer, float ArmourRegen)
{
    public static readonly Difficulty Low = new(0.25f, 1.5f, 0.75f, 0.025f);
    public static readonly Difficulty Medium = new(0.625f, 1.25f, 1f, 0.015f);
    public static readonly Difficulty High = new(1f, 1f, 1f, 0.0125f);

    /// <summary>The multipliers of <paramref name="level"/> in effect: the DOS ones, or a mod's
    /// <c>tables/difficulty.json</c> row (<see cref="GameTables.Mods"/>).</summary>
    public static Difficulty Of(DifficultyLevel level) => Load(GameTables.Mods)[(int)level];

    /// <summary>The DOS multipliers, <see cref="DifficultyLevel"/> by <see cref="DifficultyLevel"/>.</summary>
    public static Difficulty Dos(DifficultyLevel level) => level switch
    {
        DifficultyLevel.Low => Low,
        DifficultyLevel.High => High,
        _ => Medium,
    };

    /// <summary>The level whose multipliers (<see cref="Of"/>) <paramref name="d"/> are; MEDIUM when none.</summary>
    public static DifficultyLevel LevelOf(Difficulty d) =>
        d == Of(DifficultyLevel.Low) ? DifficultyLevel.Low : d == Of(DifficultyLevel.High) ? DifficultyLevel.High : DifficultyLevel.Medium;

    /// <summary>The DOS multipliers as the difficulty table's rows (what a mod's file lies over).</summary>
    public static TableFile<ImmutableArray<DifficultyRow>> DosTable => new(TableNames.Difficulty, "0x5c210", "SKYNET.EXE v1.01", "", "Difficulty",
        [.. Enum.GetValues<DifficultyLevel>().Select(l => Row(l, Dos(l)))]);

    private static DifficultyRow Row(DifficultyLevel level, Difficulty d) =>
        new(TableSchemas.Levels[(int)level], Exact(d.EnemyFire), Exact(d.DamageToEnemy), Exact(d.DamageToPlayer), Exact(d.ArmourRegen));

    // The float's shortest decimal as a double (0.015f → 0.015, not 0.014999999664723873).
    private static double Exact(float f) => double.Parse(f.ToString("R", System.Globalization.CultureInfo.InvariantCulture), System.Globalization.CultureInfo.InvariantCulture);

    private sealed record Loaded(IReadOnlyList<Difficulty> Levels, VirtualFileSystem? From);

    private static Loaded? _loaded;

    /// <summary>The three levels' multipliers with the <c>tables/difficulty.json</c> of <paramref name="mods"/>
    /// over the DOS ones, LOW to HIGH.</summary>
    public static IReadOnlyList<Difficulty> Load(VirtualFileSystem? mods)
    {
        var cached = Volatile.Read(ref _loaded);
        if (cached != null && ReferenceEquals(cached.From, mods))
        {
            return cached.Levels;
        }

        var rows = GameTables.Layer(TableNames.Difficulty, TableSchemas.Difficulty, DosTable, mods).Rows;
        var levels = Enum.GetValues<DifficultyLevel>().Select(l => rows.Single(r => r.Level == TableSchemas.Levels[(int)l]))
            .Select(r => new Difficulty((float)r.EnemyFire, (float)r.DamageToEnemy, (float)r.DamageToPlayer, (float)r.ArmourRegen)).ToArray();
        Volatile.Write(ref _loaded, new Loaded(levels, mods));
        return levels;
    }
}
