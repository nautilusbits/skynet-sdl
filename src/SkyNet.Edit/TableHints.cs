using System.Globalization;
using SkyNet.Data;
using SkyNet.Game.Campaign;
using SkyNet.Game.Enemies;
using SkyNet.Game.Enhanced;
using SkyNet.Game.Weapons;

namespace SkyNet.Edit;

/// <summary>
/// The tables the Tables window edits (editor-tools plan, T6) and how their columns are edited: sounds,
/// models, ammo types, pools and weapons as combos of names, the difficulty's multipliers as decimals.
/// <code>
///   weapons  ammo_types  ammo_pools  impact_pieces  items  difficulty   tables/&lt;name&gt;.json (changed rows)
///   robots                                                              robots/&lt;name&gt;.json (one a type)
///   sprite_models                                                       tables/sprite_models.json (T3)
///   effects                                                             tables/effects.json (T5)
/// </code>
/// </summary>
public static class TableHints
{
    /// <summary>"-1 none" then each sound id with its clip.</summary>
    public static IReadOnlyList<GridChoice> SoundChoices(IReadOnlyList<string> sounds) =>
        [new GridChoice(-1L, "-1 none"), .. sounds.Select((s, i) => new GridChoice((long)i, Inv($"{i} {s}")))];

    /// <summary>The tables, in the window's order; <paramref name="models"/> the .3D names, <paramref name="sounds"/> the sound ids' clips.</summary>
    public static IReadOnlyList<ITableSource> Sources(IReadOnlyList<string> models, IReadOnlyList<string> sounds)
    {
        var sound = SoundChoices(sounds);
        var ammo = GameTables.Ammo.Select(a => new GridChoice((long)a.Index, Inv($"{a.Index} {a.Family} {a.Damage}"))).ToArray();
        GridChoice[] pools = [new(-1L, "-1 none"), .. GameTables.Pools.Select(p => new GridChoice((long)p.Index, Inv($"{p.Index} max {p.Max}")))];
        GridChoice[] weapons = [new(-1L, "-1 none"), .. WeaponTable.Names.Select((n, i) => new GridChoice((long)i, Inv($"{i} {n}")))];
        GridChoice[] families = [.. new[] { "hitscan", "gravity", "bolt" }.Select(f => new GridChoice(f, f))];
        GridChoice[] modelNames = [new("", "(none)"), .. models.Select(m => new GridChoice(m, m))];
        GridColumn Sound(string key) => new(key, CellKind.Choice, sound);

        return
        [
            new JsonTableSource<WeaponRow>(TableNames.Weapons, TableSchemas.Weapons, m => GameTables.Load(TableNames.Weapons, TableSchemas.Weapons, m),
                Hints(new GridColumn("index", CellKind.Int, weapons[1..], ReadOnly: true), new GridColumn("ammo", CellKind.Choice, ammo),
                    new GridColumn("pool", CellKind.Choice, pools), Sound("fireSound"), Sound("selectSound"), Sound("drySound"))),
            new JsonTableSource<AmmoRow>(TableNames.Ammo, TableSchemas.Ammo, m => GameTables.Load(TableNames.Ammo, TableSchemas.Ammo, m),
                Hints(new GridColumn("family", CellKind.Choice, families), new GridColumn("model", CellKind.Choice, modelNames),
                    Sound("fireSound"), Sound("impactSound"))),
            new JsonTableSource<PoolRow>(TableNames.Pools, TableSchemas.Pools, m => GameTables.Load(TableNames.Pools, TableSchemas.Pools, m)),
            new JsonTableSource<PieceRow>(TableNames.Pieces, TableSchemas.Pieces, m => GameTables.Load(TableNames.Pieces, TableSchemas.Pieces, m)),
            new JsonTableSource<ItemRow>(TableNames.Items, TableSchemas.Items, m => GameTables.Layer(TableNames.Items, TableSchemas.Items, PickupTable.DosTable, m),
                Hints(new GridColumn("pool", CellKind.Choice, pools), new GridColumn("weapon", CellKind.Choice, weapons))),
            new JsonTableSource<DifficultyRow>(TableNames.Difficulty, TableSchemas.Difficulty,
                m => GameTables.Layer(TableNames.Difficulty, TableSchemas.Difficulty, Difficulty.DosTable, m),
                Hints(new GridColumn("level", CellKind.Text, ReadOnly: true), new GridColumn("enemyFire", CellKind.Float),
                    new GridColumn("damageToEnemy", CellKind.Float), new GridColumn("damageToPlayer", CellKind.Float), new GridColumn("armourRegen", CellKind.Float))),
            new RobotTableSource(models, sounds),
            new SpriteModelSource(),
            new JsonTableSource<EffectRow>(TableNames.Effects, TableSchemas.Effects, m => GameTables.Layer(TableNames.Effects, TableSchemas.Effects, EffectTable.Base, m),
                Hints([new GridColumn("effect", CellKind.Text, ReadOnly: true), new GridColumn(EffectTable.Low, CellKind.Int), new GridColumn(EffectTable.High, CellKind.Int),
                    .. EffectFloats.Select(k => new GridColumn(k, CellKind.Float))])),
            new JsonTableSource<SkyRow>(TableNames.Skies, TableSchemas.Skies, m => GameTables.Layer(TableNames.Skies, TableSchemas.Skies, SunsetTable.Base, m),
                Hints([new GridColumn("map", CellKind.Int), .. SunsetTable.Decimals.Select(k => new GridColumn(k, CellKind.Float))])),
            new JsonTableSource<NightSkyRow>(TableNames.NightSkies, TableSchemas.NightSkies,
                m => GameTables.Layer(TableNames.NightSkies, TableSchemas.NightSkies, NightSkyTable.Base, m),
                Hints([new GridColumn("map", CellKind.Int), .. NightSkyTable.Decimals.Select(k => new GridColumn(k, CellKind.Float))])),
        ];
    }

    // The effects' decimal columns (a whole default, 1, would read as a whole number).
    private static readonly string[] EffectFloats =
    [
        EffectTable.Life, EffectTable.Size, EffectTable.Speed, EffectTable.Rise, EffectTable.Spread, EffectTable.Gravity,
        EffectTable.Intensity, EffectTable.Flicker, EffectTable.R, EffectTable.G, EffectTable.B,
    ];

    private static Dictionary<string, GridColumn> Hints(params GridColumn[] columns) => columns.ToDictionary(c => c.Key);

    private static string Inv(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
}
