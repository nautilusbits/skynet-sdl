using SkyNet.Data;
using SkyNet.Formats;
using SkyNet.Game.Audio;
using SkyNet.Game.Baking;
using SkyNet.Game.Campaign;
using SkyNet.Game.Mods;

namespace SkyNet.Edit.Tests;

/// <summary>A theory skipped unless SKYNET_DATA points at SkyNET's data folder.</summary>
public sealed class MapDataTheoryAttribute : TheoryAttribute
{
    public MapDataTheoryAttribute()
    {
        if (EditDocumentTests.DataDir == null)
        {
            Skip = "SKYNET_DATA is not set to a SkyNET data folder";
        }
    }
}

/// <summary>The Details panel's model: each field's edit one undo step through the layer file, a multi-selection's
/// mixed values and one-step edit, a robot type's parameter on its Tables row, "Make unique" and the model's
/// replacement through <see cref="ModelReplace"/>.</summary>
[Collection(EditModsCollection.Name)]
public sealed class DetailsTests : IDisposable
{
    private const string L = "MAP.210";
    private readonly string _dir = Directory.CreateTempSubdirectory("skynet-edit-details-").FullName;

    public DetailsTests() => ModContent.Unmount();

    public void Dispose()
    {
        ModContent.Unmount();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string Folder => Directory.CreateDirectory(Path.Combine(_dir, "mods", "details-test")).FullName;

    private static (GameData Data, EditDocument Doc) Open()
    {
        var data = GameData.Open(EditDocumentTests.DataDir!)!;
        var mission = MissionGenerator.Build(data, MissionTable.For(data.Game).ByNumber(1)!.Value);
        return (data, EditDocument.Load(data, mission, null));
    }

    private static TableGrid Robots(string? folder) => new(new RobotTableSource(["DRONE.3D"], SoundIds.Names), null, folder);

    private static List<MapEntity> Of(EditDocument doc, EntityKind kind) =>
        [.. doc.Map(L).Entities.Where(e => EditorWorld.KindOf(e) == kind).OrderBy(e => e.FileOffset)];

    private static MapEntity First(EditDocument doc, EntityKind kind) => Of(doc, kind).First(e => kind != EntityKind.Prop || doc.Map(L).EntityName(e).Length > 0);

    private static DetailRow Row(EditDocument doc, IReadOnlyList<int> keys, string field, int mask = 0, IReadOnlyList<DetailChoice>? robots = null)
    {
        var map = doc.Map(L);
        var entities = keys.Select(k => map.TryGetEntityAt(k, out var e) ? e : throw new KeyNotFoundException()).ToList();
        return EntityDetails.Of(map, entities, robots ?? []).SelectMany(s => s.Rows).Single(r => r.Field == field && r.Mask == mask);
    }

    // The entity as the layer file gives it back: written, read, applied to the DOS map.
    private static MapEntity Saved(EditDocument doc, int key)
    {
        var map = MapLayer.Parse(doc.Layer(L).ToBytes()).Apply(210, doc.BaseMap(L));
        return map.TryGetEntityAt(key, out var e) ? e : throw new KeyNotFoundException();
    }

    public static TheoryData<EntityKind, string, int> Fields => new()
    {
        { EntityKind.Prop, "x", 0 }, { EntityKind.Prop, "yaw", 0 }, { EntityKind.Prop, "nameIndex", 0 }, { EntityKind.Prop, "actor", 0 },
        { EntityKind.Prop, "hitPoints", 0 }, { EntityKind.Prop, "act", 0 }, { EntityKind.Prop, "state", 1 },
        { EntityKind.Light, "light", 0 }, { EntityKind.Light, "lightEnable", 0 },
        { EntityKind.Robot, "enemy", 0 }, { EntityKind.Robot, "convoy", 0 }, { EntityKind.Robot, "param16", 0 },
        { EntityKind.Pickup, "sprite", 0 },
    };

    [MapDataTheory]
    [MemberData(nameof(Fields))]
    public void A_field_edit_round_trips_through_the_layer_as_one_undo_step(EntityKind kind, string field, int mask)
    {
        var (_, doc) = Open();
        var e = First(doc, kind);
        var row = Row(doc, [e.FileOffset], field, mask, [new DetailChoice(e.EnemyType, "now"), new DetailChoice(e.EnemyType + 1, "next")]);
        Assert.False(row.ReadOnly);
        var before = row.Value!.Value;

        // Another value the row takes: the next choice, the other bit value, or one more.
        var value = row.Kind switch
        {
            DetailKind.Choice => row.Choices!.First(c => c.Value != before).Value,
            DetailKind.Bool => 1 - before,
            _ => before < row.Max ? before + 1 : before - 1,
        };
        EntityDetails.Set(doc, L, [e.FileOffset], row, value);

        Assert.Equal(1, doc.History.UndoCount);
        Assert.Equal(value, Row(doc, [e.FileOffset], field, mask).Value);
        Assert.Equal(EditDocument.EntityText(doc.Map(L).Entities.Single(x => x.FileOffset == e.FileOffset)), EditDocument.EntityText(Saved(doc, e.FileOffset)));

        doc.Undo();
        Assert.Equal(before, Row(doc, [e.FileOffset], field, mask).Value);
        Assert.True(doc.Layer(L).IsEmpty);
    }

    [MapDataFact]
    public void A_multi_selection_shows_mixed_values_and_sets_all_in_one_step()
    {
        var (_, doc) = Open();
        var props = Of(doc, EntityKind.Prop).Where(p => p.X != Of(doc, EntityKind.Prop)[0].X).Take(1).Prepend(Of(doc, EntityKind.Prop)[0]).ToList();
        var light = First(doc, EntityKind.Light);
        int[] keys = [props[0].FileOffset, props[1].FileOffset];

        // Different x: mixed; the kind's section and the trigger's rows they share stay.
        var x = Row(doc, keys, "x");
        Assert.True(x.Mixed);
        Assert.Contains(EntityDetails.Of(doc.Map(L), props, []), s => s.Title == nameof(EntityKind.Prop));

        // A prop and a light share the transform's position, not the prop's angles or model.
        var mixed = EntityDetails.Of(doc.Map(L), [props[0], light], []);
        Assert.DoesNotContain(mixed.SelectMany(s => s.Rows), r => r.Field is "yaw" or "nameIndex");
        Assert.Contains(mixed.SelectMany(s => s.Rows), r => r.Field == "z");

        EntityDetails.Set(doc, L, keys, x, 1234);
        Assert.Equal(1, doc.History.UndoCount);
        Assert.All(keys, k => Assert.Equal(1234, Saved(doc, k).X));
        Assert.Equal(1234, Row(doc, keys, "x").Value);

        // A state bit lands on each record's own byte.
        var states = keys.Select(k => Saved(doc, k).StateByte).ToArray();
        EntityDetails.Set(doc, L, keys, Row(doc, keys, "state", 2), 1);
        Assert.Equal(2, doc.History.UndoCount);
        for (var i = 0; i < keys.Length; i++)
        {
            Assert.Equal(states[i] | 2, Saved(doc, keys[i]).StateByte);
        }

        doc.Undo();
        doc.Undo();
        Assert.Equal(props[0].X, Saved(doc, keys[0]).X);
        Assert.Equal(props[1].X, Saved(doc, keys[1]).X);
    }

    [MapDataFact]
    public void A_robot_type_parameter_is_set_on_the_types_row()
    {
        var (_, doc) = Open();
        var robot = First(doc, EntityKind.Robot);
        var grid = Robots(Folder);
        var hp = RobotTypeEdits.Column(grid, "hitPoints");
        var before = (long)grid.Row(RobotTypeEdits.Key(robot.EnemyType))![hp]!;

        RobotTypeEdits.Set(grid, doc.History, robot.EnemyType, "hitPoints", before + 50);
        Assert.Equal(before + 50, (long)grid.Row(RobotTypeEdits.Key(robot.EnemyType))![hp]!);
        Assert.True(doc.Layer(L).IsEmpty);
        Assert.Contains(RobotTypeEdits.Choices(grid), c => c.Value == robot.EnemyType);
        Assert.NotEqual("", RobotTypeEdits.Name(grid, robot.EnemyType));

        // Saved: the game's robot type has it.
        grid.Save();
        using var mods = new RetroEng.Content.ContentRoot(Path.GetDirectoryName(Folder)!).Mount([], ["details-test"]);
        Assert.Equal(before + 50, GameTables.EnemyTypes(mods)[robot.EnemyType].HitPoints);

        doc.Undo();
        Assert.Equal(before, (long)grid.Row(RobotTypeEdits.Key(robot.EnemyType))![hp]!);
    }

    [MapDataFact]
    public void Make_unique_adds_a_type_for_that_robot_only_in_one_step()
    {
        var (_, doc) = Open();
        var robot = First(doc, EntityKind.Robot);
        var grid = Robots(Folder);
        var rows = grid.Rows.Count;
        var before = grid.Rows.Select(TableGrid.KeyOf).ToHashSet();

        var type = RobotTypeEdits.MakeUnique(doc, grid, L, robot.FileOffset);

        Assert.Equal(1, doc.History.UndoCount);
        Assert.Equal(rows + 1, grid.Rows.Count);
        Assert.DoesNotContain(RobotTypeEdits.Key(type), before);
        Assert.Equal(type, Saved(doc, robot.FileOffset).EnemyType);
        Assert.All(Of(doc, EntityKind.Robot).Where(r => r.FileOffset != robot.FileOffset), r => Assert.NotEqual(type, r.EnemyType));

        // Its row copies the old type's; an edit of it leaves the old type be.
        var hp = RobotTypeEdits.Column(grid, "hitPoints");
        Assert.Equal(grid.Row(RobotTypeEdits.Key(robot.EnemyType))![hp], grid.Row(RobotTypeEdits.Key(type))![hp]);
        RobotTypeEdits.Set(grid, doc.History, type, "hitPoints", 999L);
        Assert.NotEqual(999L, grid.Row(RobotTypeEdits.Key(robot.EnemyType))![hp]);

        // Undone: row and assignment go together; redone: back together.
        doc.Undo();
        doc.Undo();
        Assert.Equal(rows, grid.Rows.Count);
        Assert.Equal(robot.EnemyType, doc.Map(L).Entities.Single(e => e.FileOffset == robot.FileOffset).EnemyType);
        doc.Redo();
        Assert.Equal(rows + 1, grid.Rows.Count);
        Assert.Equal(type, doc.Map(L).Entities.Single(e => e.FileOffset == robot.FileOffset).EnemyType);
        Assert.Throws<MapLayerException>(() => RobotTypeEdits.MakeUnique(doc, grid, L, First(doc, EntityKind.Prop).FileOffset));
    }

    [MapDataFact]
    public void Unsaved_robot_types_are_in_effect_before_the_save()
    {
        var (data, doc) = Open();
        var robot = First(doc, EntityKind.Robot);
        var tables = new TablesWindow([new RobotTableSource(["DRONE.3D"], SoundIds.Names)], () => doc.History, () => Folder);
        var grid = tables.Grid(RobotTableSource.TableName)!;
        var robots = MapBaker.Bake(data, 210, edited: doc.Map(L))!;
        int Triangles(MapBlock b) => b.Robots.Sum(r => r.Indices.Length) / 3;

        using (new UnsavedRobots(tables))
        {
            // Made unique and changed, not saved: the game's tables and the bake have the new type.
            var type = RobotTypeEdits.MakeUnique(doc, grid, L, robot.FileOffset);
            RobotTypeEdits.Set(grid, doc.History, type, "hitPoints", 777L);
            Assert.Equal(777, GameTables.Ais.Types[type].HitPoints);
            Assert.Equal(GameTables.Ais.Types[robot.EnemyType].ModelFile, GameTables.EnemyTypes(GameTables.Mods)[type].ModelFile);
            var edited = MapBaker.Bake(data, 210, edited: doc.Map(L))!;
            Assert.Equal((robots.EnemyCount, Triangles(robots)), (edited.EnemyCount, Triangles(edited)));

            // Undone: the type is gone again.
            doc.Undo();
            doc.Undo();
            Assert.True(GameTables.Ais.Types.Length <= type);
        }

        Assert.Null(GameTables.RobotResolver);
    }

    [MapDataFact]
    public void Replace_model_goes_through_the_replace_service()
    {
        var (data, doc) = Open();
        var map = doc.Map(L);
        var prop = First(doc, EntityKind.Prop);
        var grid = Robots(Folder);
        var m = EntityModel.Of(map, prop, t => RobotTypeEdits.ModelFile(grid, t));
        Assert.Equal(ModelSlot.Prop, m.Slot);
        Assert.Equal(ModelReplace.NameOf(map.EntityName(prop)), m.Name);

        var glb = Path.Combine(Directory.CreateDirectory(Path.Combine(_dir, "picked")).FullName, m.Name + ".glb");
        File.WriteAllBytes(glb, new GltfExport(data, GameTables.Ais).Export(m.Name, RobotSegments.Without)!);
        var changed = 0;
        m.Replace(doc.History, Folder, glb, null, changed: () => changed++);

        Assert.True(m.Replaced(Folder, null));
        Assert.True(File.Exists(Path.Combine(Folder, "models", m.Name + ".glb")));
        Assert.True(File.Exists(Path.Combine(Folder, "models", m.Name + ".json")));
        Assert.Equal(1, changed);
        doc.Undo();
        Assert.False(m.Replaced(Folder, null));

        // A robot's is its type's model, as a whole-robot file; a pickup's its sprite's row.
        var robot = EntityModel.Of(map, First(doc, EntityKind.Robot), t => RobotTypeEdits.ModelFile(grid, t));
        Assert.Equal(ModelSlot.Robot, robot.Slot);
        Assert.Equal(ModelReplace.NameOf(RobotTypeEdits.ModelFile(grid, First(doc, EntityKind.Robot).EnemyType)!), robot.Name);
        var pickup = EntityModel.Of(map, First(doc, EntityKind.Pickup), _ => null);
        Assert.Equal(new EntityModel(ModelSlot.Sprite, SpriteModels.KeyOf(First(doc, EntityKind.Pickup).SpriteIndex)), pickup);
    }
}
