using System.Numerics;
using RetroEng.Commands;
using RetroEng.Content;
using RetroEng.Editor;
using RetroEng.Json;
using SkyNet.Data;
using SkyNet.Game.Audio;
using SkyNet.Game.Campaign;
using RetroEng.Physics;
using SkyNet.Game.Enemies;
using SkyNet.Game.Mods;
using SkyNet.Game.Weapons;

namespace SkyNet.Edit.Tests;

/// <summary>The Tables window's grids (editor-tools plan, T6): each table's edit round-trips through the save
/// mod and is what the game reads, only changed rows are written, edits are undo steps, a new robot type is a
/// robot file over its base, and an edited weapon damage is the damage the game deals.</summary>
[Collection(EditModsCollection.Name)]
public sealed class TableGridTests : IDisposable
{
    private const string ModName = "tables-test";
    private const int Row = 1;
    private readonly string _mods = Directory.CreateTempSubdirectory("skynet-edit-tables-").FullName;

    public TableGridTests() => ModContent.Unmount();

    public void Dispose()
    {
        ModContent.Unmount();
        try
        {
            Directory.Delete(_mods, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string Folder => Directory.CreateDirectory(Path.Combine(_mods, ModName)).FullName;

    private static IReadOnlyList<ITableSource> Sources => TableHints.Sources(["DRONE.3D", "LASER1.3D"], SoundIds.Names);

    private static ITableSource Source(string name) => Sources.Single(s => s.Name == name);

    private VirtualFileSystem Mod() => new ContentRoot(_mods).Mount([], [ModName]);

    private static int Col(TableGrid g, string key) => g.Columns.Select(c => c.Key).ToList().IndexOf(key);

    // The column each table's test edits, and how the game reads row Row's value back through a mod.
    public static TheoryData<string, string> Edited => new()
    {
        { TableNames.Weapons, "rate" }, { TableNames.Ammo, "damage" }, { TableNames.Pools, "max" }, { TableNames.Pieces, "life" },
        { TableNames.Items, "amount" }, { TableNames.Difficulty, "enemyFire" }, { RobotTableSource.TableName, "hitPoints" },
    };

    private static double GameValue(string table, VirtualFileSystem mods, object?[] row) => table switch
    {
        TableNames.Weapons => GameTables.Load(table, TableSchemas.Weapons, mods).Rows[Row].Rate,
        TableNames.Ammo => GameTables.Load(table, TableSchemas.Ammo, mods).Rows[Row].Damage,
        TableNames.Pools => GameTables.Load(table, TableSchemas.Pools, mods).Rows[Row].Max,
        TableNames.Pieces => GameTables.Load(table, TableSchemas.Pieces, mods).Rows[Row].Life,
        TableNames.Items => PickupTable.Load(mods)[(int)(long)row[0]!].Amount,
        TableNames.Difficulty => Difficulty.Load(mods)[Row].EnemyFire,
        _ => GameTables.EnemyTypes(mods)[Row].HitPoints,
    };

    [Theory]
    [MemberData(nameof(Edited))]
    public void An_edited_cell_round_trips_through_the_mod_and_the_game_reads_it(string table, string column)
    {
        var history = new UndoStack();
        var grid = new TableGrid(Source(table), null, Folder);
        var c = Col(grid, column);
        var row = grid.Rows[Row];
        var key = TableGrid.KeyOf(row);
        object value = row[c] is double d ? d + 0.5 : (long)row[c]! + 3;
        grid.Set(history, key, c, value);
        Assert.True(grid.Dirty);
        Assert.Equal(1, grid.ChangedRows);

        var written = Assert.Single(grid.Save());
        Assert.False(grid.Dirty);

        // One row in the file, the base's other values with the edited one.
        var again = new TableGrid(Source(table), null, Folder);
        Assert.Equal(grid.Rows.Count, again.Rows.Count);
        Assert.All(grid.Rows.Zip(again.Rows), p => Assert.Equal(p.First, p.Second, CellComparer.Instance));
        Assert.Equal(1, again.ChangedRows);
        Assert.True(again.CellChanged(again.Rows[Row], c));
        Assert.False(again.Dirty);
        var json = JsonItem.Parse(File.ReadAllBytes(Path.Combine(Folder, written)));
        if (table == RobotTableSource.TableName)
        {
            Assert.Equal($"{{\"type\":{key},\"{column}\":{value}}}", json.ToString());
        }
        else
        {
            Assert.Single(json.Member("rows").Items);
        }

        using var mods = Mod();
        Assert.Equal(Convert.ToDouble(value), GameValue(table, mods, row), 3);
    }

    [Fact]
    public void Only_the_changed_rows_are_written_and_a_table_back_at_its_base_removes_its_file()
    {
        var history = new UndoStack();
        var grid = new TableGrid(Source(TableNames.Weapons), null, Folder);
        var rate = Col(grid, "rate");
        grid.Set(history, "1", rate, 9L);
        grid.Set(history, "2", rate, 9L);
        grid.RevertRow(history, "2");
        Assert.Equal(["tables/weapons.json"], grid.Save());
        var rows = JsonItem.Parse(File.ReadAllBytes(Path.Combine(Folder, "tables", "weapons.json"))).Member("rows").Items;
        Assert.Equal(1, Assert.Single(rows).Member("index").AsInt());
        Assert.Equal(15, rows[0].Members.Count);

        grid.RevertCell(history, "1", rate);
        Assert.Equal(["tables/weapons.json (removed)"], grid.Save());
        Assert.False(File.Exists(Path.Combine(Folder, "tables", "weapons.json")));
    }

    [Fact]
    public void Edits_are_steps_of_the_editors_undo_stack_and_one_cells_typing_is_one_step()
    {
        var history = new UndoStack();
        var grid = new TableGrid(Source(TableNames.Ammo), null, Folder);
        var damage = Col(grid, "damage");
        var before = grid.Row("0")![damage];
        grid.Set(history, "0", damage, 3L);
        grid.Set(history, "0", damage, 37L);
        Assert.Equal(1, history.UndoCount);
        Assert.Equal("ammo_types 0 damage", history.UndoLabel);

        history.Undo();
        Assert.Equal(before, grid.Row("0")![damage]);
        Assert.False(grid.Dirty);
        history.Redo();
        Assert.Equal(37L, grid.Row("0")![damage]);

        history.EndMerge();
        grid.RevertRow(history, "0");
        Assert.Equal(before, grid.Row("0")![damage]);
        history.Undo();
        Assert.Equal(37L, grid.Row("0")![damage]);
    }

    [Fact]
    public void A_new_robot_row_is_a_new_type_over_its_base_in_a_robot_file()
    {
        var history = new UndoStack();
        var grid = new TableGrid(Source(RobotTableSource.TableName), null, Folder);
        var table = GameTables.EnemyTypes(null);
        var key = grid.AddRow(history, "0");
        Assert.Equal(table.Length.ToString(System.Globalization.CultureInfo.InvariantCulture), key);
        grid.Set(history, key!, Col(grid, "hitPoints"), 300L);
        Assert.Equal(["robots/type-171.json"], grid.Save());
        Assert.Equal("{\"type\":171,\"base\":0,\"hitPoints\":300}", JsonItem.Parse(File.ReadAllBytes(Path.Combine(Folder, "robots", "type-171.json"))).ToString());

        using var mods = Mod();
        var robot = GameTables.EnemyTypes(mods)[171];
        Assert.Equal((300, table[0].Speed, table[0].ModelFile), (robot.HitPoints, robot.Speed, robot.ModelFile));

        // Reverting the new row takes it out; the save removes its file.
        var again = new TableGrid(Source(RobotTableSource.TableName), null, Folder);
        Assert.False(again.BaseOf(again.Row("171")!) != null);
        again.RevertRow(history, "171");
        Assert.Null(again.Row("171"));
        Assert.Equal(["robots/type-171.json (removed)"], again.Save());
    }

    [Fact]
    public void The_base_is_the_mods_under_the_save_mod_and_two_mods_change_different_rows()
    {
        // Mod "lower" makes the uzi's rate 9; the save mod then changes the shotgun's.
        var lower = Directory.CreateDirectory(Path.Combine(_mods, "lower", "tables")).FullName;
        File.WriteAllText(Path.Combine(lower, "weapons.json"), "{ \"rows\": [" + Row1Rate(9) + "] }");
        using var under = new ContentRoot(_mods).Mount([], ["lower"]);
        var history = new UndoStack();
        var grid = new TableGrid(Source(TableNames.Weapons), under, Folder);
        var rate = Col(grid, "rate");
        Assert.Equal(9L, grid.Row("1")![rate]);
        Assert.Equal(0, grid.ChangedRows);
        grid.Set(history, "4", rate, 6L);
        grid.Save();

        using var both = new ContentRoot(_mods).Mount([], ["lower", ModName]);
        var weapons = GameTables.Load(TableNames.Weapons, TableSchemas.Weapons, both).Rows;
        Assert.Equal((9, 6, 26), (weapons[1].Rate, weapons[4].Rate, weapons.Length));
    }

    private static string Row1Rate(int rate)
    {
        var row = GameTables.Load(TableNames.Weapons, TableSchemas.Weapons, null).Rows[1] with { Rate = rate };
        return TableSchemas.Weapons.Write(new TableFile<System.Collections.Immutable.ImmutableArray<WeaponRow>>("", "", "", "", "", [row])).Member("rows").Items[0].ToString();
    }

    [Fact]
    public void A_weapon_damage_edited_by_command_is_the_damage_the_game_deals()
    {
        // The editor: table.set + table.save into the save mod (the uzi fires ammo type 0).
        var history = new UndoStack();
        var window = new TablesWindow(Sources, () => history, () => Folder);
        var registry = new CommandRegistry();
        window.Register(registry);
        Assert.Equal("ammo_types 0 damage = 37", registry.Run("table.set ammo_types 0 damage 37").Single());
        Assert.StartsWith("Tables saved to", registry.Run("table.save").Single());

        // The game with that mod on: every uzi round hits for 37, not DOS's 10.
        ModContent.Mount(_mods, [ModName]);
        var weapons = new WeaponSystem(WeaponTable.Shipped, new CollisionWorld()) { DamageToEnemies = 1f };
        var target = new Target(new Vector3(0, 0, -400));
        weapons.Targets.Add(target);
        for (var i = 0; i < 60; i++)
        {
            weapons.Tick(new WeaponInput(Fire: true, Throw: false, Run: false), new Shooter(Vector3.Zero, 0f, 0f), 1f / 60f);
        }

        Assert.NotEmpty(target.Hits);
        Assert.All(target.Hits, h => Assert.Equal(37f, h.Points));
    }

    private sealed class Target(Vector3 centre) : IDamageable
    {
        public List<Damage> Hits { get; } = [];
        public Vector3 Centre => centre;
        public float Radius => 30f;
        public bool Alive => true;
        public void TakeDamage(in Damage damage) => Hits.Add(damage);
    }
}
