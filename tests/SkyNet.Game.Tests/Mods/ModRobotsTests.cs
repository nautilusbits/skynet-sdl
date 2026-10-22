using System.Collections.Immutable;
using RetroEng.Content;
using SkyNet.Data;
using SkyNet.Formats;
using SkyNet.Game.Baking;
using SkyNet.Game.Collision;
using SkyNet.Game.Enemies;
using SkyNet.Game.Mods;
using SkyNet.Game.Tests.Behaviour;

namespace SkyNet.Game.Tests.Mods;

/// <summary>Mod robots (<c>robots/*.json</c>): the sample mod's new type loads over its base, gets
/// its row and spawns with its params where its map layer puts it; an existing row is changed the
/// same way; bad files stop with their place.</summary>
public class ModRobotsTests
{
    private const string Sample = "flencer-mk2";
    private const int NewType = 200, Flencer = 0, Globe = 1;
    /// <summary>The MAP.210 globe marker the sample's layer turns into the new robot.</summary>
    private const int GlobeMarker = 50926;

    private static string Here([System.Runtime.CompilerServices.CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;

    private static string Samples => Path.GetFullPath(Path.Combine(Here(), "..", "..", "..", "mods-samples"));

    private static ImmutableArray<EnemyType> Shipped => GameTables.Load(TableNames.EnemyTypes, TableSchemas.EnemyTypes, null).Rows;

    // A temporary mods folder with one mod of robot files; the VFS over it.
    private static VirtualFileSystem ModWith(string root, params (string Name, string Json)[] robots)
    {
        var dir = Path.Combine(root, "m", "robots");
        Directory.CreateDirectory(dir);
        foreach (var (name, json) in robots)
        {
            File.WriteAllText(Path.Combine(dir, name + ".json"), json);
        }

        return new ContentRoot(root).Mount([], ["m"]);
    }

    private static void InTemp(Action<string> body)
    {
        var root = Directory.CreateTempSubdirectory("skynet-robots-").FullName;
        try
        {
            body(root);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void The_sample_robot_gets_its_row_over_its_base()
    {
        using var mods = new ContentRoot(Samples).Mount([], [Sample]);
        var types = GameTables.EnemyTypes(mods);
        var shipped = Shipped;
        var b = shipped[Flencer];
        var t = types[NewType];

        // Its own keys...
        Assert.Equal(NewType, t.Index);
        Assert.Equal(300, t.HitPoints);
        Assert.Equal(350, t.Speed);
        Assert.Equal(1024, t.Turn);
        Assert.Equal(200, t.Fire?.Rate);

        // ...the rest its base's.
        Assert.Equal(b.ModelFile, t.ModelFile);
        Assert.Equal(b.Name, t.Name);
        Assert.Equal(b.State, t.State);
        Assert.Equal(b.ScriptVa, t.ScriptVa);
        Assert.Equal(b.Fire?.Ammo, t.Fire?.Ammo);
        Assert.Equal(b.Death.AsEnumerable(), t.Death.AsEnumerable());
        Assert.Equal(b.Record.AsEnumerable(), t.Record.AsEnumerable());

        // The DOS rows stay; the ids between are empty.
        Assert.Equal(NewType + 1, types.Length);
        Assert.All(Enumerable.Range(0, shipped.Length), i => Assert.Equal((shipped[i].ModelFile, shipped[i].HitPoints, shipped[i].Speed), (types[i].ModelFile, types[i].HitPoints, types[i].Speed)));
        Assert.All(Enumerable.Range(shipped.Length, NewType - shipped.Length), i => Assert.Equal("", types[i].ModelFile));
    }

    [Fact]
    public void A_robot_file_changes_an_existing_row() => InTemp(root =>
    {
        using var mods = ModWith(root, ("globe", """{ "type": 1, "hitPoints": 99, "segments": 14, "death": 0, "fireRate": null, "comment": "x" }"""));
        var types = GameTables.EnemyTypes(mods);
        var shipped = Shipped;

        Assert.Equal(shipped.Length, types.Length);
        Assert.Equal(99, types[Globe].HitPoints);
        Assert.Equal(shipped[Globe].Speed, types[Globe].Speed);
        Assert.Equal(shipped[14].Segments.AsEnumerable(), types[Globe].Segments.AsEnumerable());
        Assert.Equal(shipped[Flencer].Death.AsEnumerable(), types[Globe].Death.AsEnumerable());
        Assert.Equal(shipped[Flencer].Record[4], types[Globe].Record[4]);
        Assert.Equal(shipped[2].HitPoints, types[2].HitPoints);
    });

    [Theory]
    [InlineData("""{ "type": 300 }""", "base")]
    [InlineData("""{ "type": 300, "base": 0, "colour": 3 }""", "colour")]
    [InlineData("""{ "type": 5000, "base": 0 }""", "type")]
    [InlineData("""{ "type": 300, "base": 999 }""", "base")]
    public void A_bad_robot_file_names_its_place(string json, string place) => InTemp(root =>
    {
        using var mods = ModWith(root, ("bad", json));
        var e = Assert.Throws<InvalidDataException>(() => GameTables.EnemyTypes(mods));

        Assert.Contains("m:robots/bad.json", e.Message, StringComparison.Ordinal);
        Assert.Contains(place, e.Message, StringComparison.Ordinal);
    });

    [Fact]
    public void Two_files_cannot_take_one_type() => InTemp(root =>
    {
        using var mods = ModWith(root, ("a", """{ "type": 300, "base": 0 }"""), ("b", """{ "type": 300, "base": 1 }"""));
        var e = Assert.Throws<InvalidDataException>(() => GameTables.EnemyTypes(mods));

        Assert.Contains("taken", e.Message, StringComparison.Ordinal);
    });

    [BehaviourDataFact]
    public void A_layer_marker_takes_a_mod_type_whole()
    {
        var map = BehaviourData.Data.LoadMap(210)!;
        var act = map.Entities.Single(e => e.FileOffset == GlobeMarker).ActType;
        MapLayer Set(int enemy) => new("MAP.210", [new MapEdit(MapEditOp.Set, GlobeMarker, ImmutableSortedDictionary<string, int>.Empty.Add("enemy", enemy))]);
        var e = Set(NewType).Apply(210, map).Entities.Single(e => e.FileOffset == GlobeMarker);

        // The type whole; the DOS byte (the marker's act) keeps what it had.
        Assert.Equal(NewType, e.EnemyType);
        Assert.Equal(act, e.ActType);
        Assert.Throws<MapLayerException>(() => Set(ModRobots.MaxType + 1).Apply(210, map));
    }

    [BehaviourDataFact]
    public void A_look_names_the_model_for_its_replacement()
    {
        var models = new EnemyModels(BehaviourData.Data);

        var dos = models.Get("flencer.3d");
        var looked = models.Get("flencer.3d", "FLNCRMK2");

        Assert.NotNull(looked);
        Assert.Equal("FLNCRMK2", looked.File);
        Assert.Same(dos!.Model, looked.Model);
        Assert.Equal("flencer.3d", models.Get("flencer.3d")!.File);
    }

    [BehaviourDataFact]
    public void The_sample_robot_spawns_with_its_params_on_MAP_210()
    {
        using var mods = new ContentRoot(Samples).Mount([], [Sample]);
        var data = BehaviourData.Data;
        var layer = MapLayer.Parse(mods.ReadAllBytes(MapLayer.PathOf("MAP.210")));
        var map = layer.Apply(210, data.LoadMap(210)!);
        var world = LevelCollision.Build(data, map, data.LoadWld(210)).World;
        var shipped = GameTables.Ais;
        var ais = new AisData(shipped.Tables, GameTables.EnemyTypes(mods), shipped.Ammo, shipped.Ops.Values, shipped.Anims.Values);
        var robots = new Robots(ais, new EnemyModels(data), world, seed: 7);
        robots.Populate(map);

        var robot = robots.All.Single(r => r.MarkerOffset == GlobeMarker);
        Assert.Equal(NewType, robot.TypeIndex);
        Assert.Equal(300f, robot.MaxHp);
        Assert.Equal(350f, robot.MoveSpeed);
        Assert.Equal(1024f / 2048f * MathF.Tau, robot.TurnRate, 4);
        Assert.Equal(200, robot.Root.Fire?.Rate);
        Assert.NotNull(robot.Root.Model);
        Assert.True(robot.Brain?.HasScript);
        Assert.Single(robots.All, r => r.TypeIndex == NewType);
    }

    [BehaviourDataFact]
    public void A_new_robot_type_keeps_the_baked_blocks()
    {
        using var mods = new ContentRoot(Samples).Mount([], [Sample]);
        using var with = new BakeInputs(BehaviourData.Data, new BakeTables(mods));
        using var without = new BakeInputs(BehaviourData.Data, BakeTables.Shipped);

        Assert.Equal(without.Read(BakeInputs.EnemyTypesInput), with.Read(BakeInputs.EnemyTypesInput));
    }
}
