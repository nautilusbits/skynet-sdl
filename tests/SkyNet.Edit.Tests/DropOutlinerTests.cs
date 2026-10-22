using System.Diagnostics;
using System.Numerics;
using RetroEng.Editor;
using SkyNet.Data;
using SkyNet.Formats;
using SkyNet.Game.Baking;
using SkyNet.Game.Campaign;
using SkyNet.Game.Collision;
using SkyNet.Game.Mods;

namespace SkyNet.Edit.Tests;

/// <summary>Drag and drop placing (a drop lands where the pointer's ray meets the level, as the record kind the
/// dragged asset is; mod robot types offered) and the outliner's filter, "Select all matching" and its
/// hidden / locked toggles (picking goes through them; saved as an editor-only file).</summary>
[Collection(EditModsCollection.Name)]
public sealed class DropOutlinerTests : IDisposable
{
    private readonly string _mods = Directory.CreateTempSubdirectory("skynet-edit-drop-").FullName;

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

    private static (GameData Data, MissionFile Mission, EditDocument Doc, string Layer) Mission1()
    {
        var data = GameData.Open(EditDocumentTests.DataDir!)!;
        var mission = MissionGenerator.Build(data, MissionTable.For(data.Game).ByNumber(1)!.Value);
        var doc = EditDocument.Load(data, mission, null);
        var layer = mission.World.Instance(mission.World.Start.Instance)!.Layer;
        return (data, mission, doc, layer);
    }

    // A ray straight down onto the player start, from high above it.
    private static PickRay DownOnStart(MapFile map)
    {
        var start = map.Entities.First(e => e.IsMarker && e.MarkerType == 0);
        return new PickRay(new Vector3(start.X, -start.Y + 2000f, -start.Z), -Vector3.UnitY);
    }

    [MapDataFact]
    public void A_drop_lands_on_the_collision_raycast_point_as_the_dragged_kind()
    {
        var (data, _, doc, layer) = Mission1();
        var number = doc.MapNumber(layer);
        var map = doc.Map(layer);
        var wld = map.IsOutdoor ? data.LoadWld(number) : null;
        var picking = new EntityPicking(map, MapBaker.Bake(data, number)!);
        var ray = DownOnStart(map);

        // The drop's point is where the game's collision meets the same ray.
        var at = picking.Surface(ray, wld != null ? LevelCollision.Ground(wld) : null);
        Assert.NotNull(at);
        var world = LevelCollision.Build(data, map, wld).World;
        Assert.True(world.Raycast(ray.Origin, ray.Direction, 4000f, out var hit));
        Assert.True(Vector3.Distance(hit.Point, at.Value) < 4f, $"{at} vs {hit.Point}");

        // Robot type, pickup, sprite and a model the map has no name for: each its record kind, at that point.
        var robot = PlacePalette.FromAsset(RobotAssets.Robots, RobotAssets.Id(map.Entities.First(e => EditorWorld.KindOf(e) == EntityKind.Robot).EnemyType), map)!;
        var pickup = PlacePalette.FromAsset(GameAssets.Sprites, GameAssets.RecordId(PickupTable.Items.Keys.Min() >> 7, PickupTable.Items.Keys.Min() & 0x7F), map)!;
        var spriteIndex = map.Entities.First(e => EditorWorld.KindOf(e) == EntityKind.Sprite).SpriteIndex;
        var sprite = PlacePalette.FromAsset(GameAssets.Sprites, GameAssets.RecordId(spriteIndex >> 7, spriteIndex & 0x7F), map)!;
        var model = new GameAssets(data).ModelFiles()
            .Select(f => f[..^3]).First(n => !map.Names.Contains(n, StringComparer.OrdinalIgnoreCase));
        var prop = PlacePalette.FromAsset(GameAssets.Models, model, map)!;
        var names = map.Names.Length;
        foreach (var (item, kind) in new[] { (robot, EntityKind.Robot), (pickup, EntityKind.Pickup), (sprite, EntityKind.Sprite), (prop, EntityKind.Prop) })
        {
            var key = PlacePalette.Place(doc, layer, item, at.Value);
            Assert.True(doc.Map(layer).TryGetEntityAt(key, out var e));
            Assert.Equal(kind, EditorWorld.KindOf(e));
            Assert.Equal(PlacePalette.DosPosition(item, at.Value), (e.X, e.Y, e.Z));
        }

        // The model's name joined the map's name slots; the drop is one undo step.
        var placed = doc.Map(layer);
        Assert.Equal(names + 1, placed.Names.Length);
        Assert.Equal(model, placed.EntityName(placed.Entities.Last()), StringComparer.OrdinalIgnoreCase);
        Assert.True(doc.Undo());
        Assert.Equal(names, doc.Map(layer).Names.Length);
        Assert.Equal(PlaceKind.Prop, PlacePalette.FromAsset(GameAssets.Models, map.Names.First(n => n.Length > 0), map)!.Kind);
        Assert.Null(PlacePalette.FromAsset(GameAssets.Textures, "200/001", map));
    }

    [MapDataFact]
    public void The_robot_palette_offers_mod_robot_types()
    {
        var (_, _, doc, layer) = Mission1();
        var map = doc.Map(layer);
        var dir = Path.Combine(_mods, "modbots", "robots");
        Directory.CreateDirectory(dir);
        const int ModType = 700;
        var baseType = map.Entities.First(e => EditorWorld.KindOf(e) == EntityKind.Robot).EnemyType;
        File.WriteAllText(Path.Combine(dir, "big.json"), $"{{ \"type\": {ModType}, \"base\": {baseType}, \"hitPoints\": 9999 }}");
        ModContent.Mount(_mods, ["modbots"]);

        var robots = PlacePalette.Items(PlaceKind.Robot, map);
        var item = Assert.Single(robots, r => r.Fields["enemy"] == ModType);
        Assert.Contains("hp 9999", item.Label, StringComparison.Ordinal);
        Assert.Contains(robots, r => r.Fields["enemy"] >= 0x80 && r.Fields["enemy"] < ModType);
        Assert.Equal(item.Fields, PlacePalette.FromAsset(RobotAssets.Robots, RobotAssets.Id(ModType), map)!.Fields);
    }

    [MapDataFact]
    public void The_filter_matches_key_act_type_name_and_kind()
    {
        var (_, _, doc, layer) = Mission1();
        var map = doc.Map(layer);
        var robots = map.Entities.Where(e => EditorWorld.KindOf(e) == EntityKind.Robot).ToList();
        var type = robots.GroupBy(r => r.EnemyType).OrderByDescending(g => g.Count()).First().Key;
        var name = GameTables.Ais.Types[type].Name;
        var acted = map.Entities.First(e => e.ActType > 0);
        var f = new OutlinerFilter();
        List<MapEntity> Match(string text)
        {
            f.Text = text;
            return f.Matching(map, map.Entities);
        }

        Assert.False(f.IsActive);
        Assert.Equal(map.Entities.Length, Match("").Count);
        Assert.Equal([acted.FileOffset], Match("@" + acted.FileOffset).Select(e => e.FileOffset));
        Assert.Equal([acted.FileOffset], Match($"@0x{acted.FileOffset:x}").Select(e => e.FileOffset));
        Assert.All(Match($"act:{acted.ActType}"), e => Assert.Equal(acted.ActType, e.ActType));
        Assert.Contains(acted, Match($"ACT:0x{acted.ActType:X}"));

        // A robot type by number or by name: every robot of it, nothing else.
        var ofType = robots.Where(r => r.EnemyType == type).Select(r => r.FileOffset).ToList();
        Assert.Equal(ofType, Match($"type:{type}").Select(e => e.FileOffset));
        Assert.Equal(ofType, Match(name.ToUpperInvariant()).Where(e => EditorWorld.KindOf(e) == EntityKind.Robot && GameTables.Ais.Types[e.EnemyType].Name == name).Select(e => e.FileOffset));
        Assert.Equal(robots.Count, Match("robot").Count(e => EditorWorld.KindOf(e) == EntityKind.Robot));

        // Words are all needed; chips keep their kinds only.
        Assert.DoesNotContain(Match($"type:{type} @{acted.FileOffset}"), e => e.FileOffset != acted.FileOffset);
        f.Text = "";
        f.Kinds.Add(EntityKind.Light);
        Assert.True(f.IsActive);
        Assert.All(f.Matching(map, map.Entities), e => Assert.Equal(EntityKind.Light, EditorWorld.KindOf(e)));
        Assert.Equal(map.Entities.Count(e => EditorWorld.KindOf(e) == EntityKind.Light), f.Matching(map, map.Entities).Count);
    }

    [MapDataFact]
    public void Select_all_matching_takes_every_match_but_the_hidden_and_locked()
    {
        var (_, _, doc, layer) = Mission1();
        var map = doc.Map(layer);
        var type = map.Entities.Where(e => EditorWorld.KindOf(e) == EntityKind.Robot).GroupBy(r => r.EnemyType).OrderByDescending(g => g.Count()).First().Key;
        var all = map.Entities.Where(e => EditorWorld.KindOf(e) == EntityKind.Robot && e.EnemyType == type).Select(e => e.FileOffset).Order().ToList();
        Assert.True(all.Count >= 3, "mission 1 has fewer than 3 robots of its commonest type");
        var f = new OutlinerFilter { Text = $"type:{type}" };
        Assert.Equal(all, f.Selection(map, map.Entities, doc.Outliner.Unpickable(layer)));

        doc.Outliner.Set(layer, [all[0]], EntityFlag.Hidden, FlagValue.On);
        doc.Outliner.Set(layer, [all[1]], EntityFlag.Locked, FlagValue.On);
        Assert.Equal(all.Skip(2), f.Selection(map, map.Entities, doc.Outliner.Unpickable(layer)));
    }

    [MapDataFact]
    public void Hidden_and_locked_entities_are_not_picked_and_are_kept_in_an_editor_file()
    {
        var (data, mission, doc, layer) = Mission1();
        var number = doc.MapNumber(layer);
        var map = doc.Map(layer);
        var picking = new EntityPicking(map, MapBaker.Bake(data, number)!);
        var robot = map.Entities.First(e => EditorWorld.KindOf(e) == EntityKind.Robot);
        Assert.True(picking.TryGet(robot.FileOffset, out var box));

        // A ray down through the robot's box picks it; hidden or locked, the ray goes through.
        var centre = (box.Min + box.Max) * 0.5f;
        var ray = new PickRay(new Vector3(centre.X, box.Max.Y + 300f, centre.Z), -Vector3.UnitY);
        Assert.Equal(robot.FileOffset, picking.Pick(ray, null, doc.Outliner.Unpickable(layer)));
        foreach (var flag in new[] { EntityFlag.Hidden, EntityFlag.Locked })
        {
            doc.Outliner.Set(layer, [robot.FileOffset], flag, FlagValue.On);
            Assert.NotEqual(robot.FileOffset, picking.Pick(ray, null, doc.Outliner.Unpickable(layer)));
            Assert.True(doc.Undo());
            Assert.Equal(robot.FileOffset, picking.Pick(ray, null, doc.Outliner.Unpickable(layer)));
        }

        // Hidden: the view's map is without it (the document keeps it); saved beside the graph layouts, read back.
        doc.Outliner.Set(layer, [robot.FileOffset], EntityFlag.Hidden, FlagValue.On);
        doc.Outliner.Set(layer, [robot.FileOffset], EntityFlag.Locked, FlagValue.On);
        Assert.False(OutlinerStates.Without(doc, layer, doc.Outliner.Of(layer, EntityFlag.Hidden)).TryGetEntityAt(robot.FileOffset, out _));
        Assert.True(doc.Map(layer).TryGetEntityAt(robot.FileOffset, out _));
        var folder = SaveTarget.Folder(_mods, "hide", ["skynet", "shock"], out _)!;
        Assert.Contains(OutlinerStates.PathOf(layer), doc.Save(folder));
        ModContent.Mount(_mods, ["hide"]);
        var back = EditDocument.Load(data, mission, ModContent.Files);
        Assert.True(back.Outliner.Has(layer, robot.FileOffset, EntityFlag.Hidden));
        Assert.True(back.Outliner.Has(layer, robot.FileOffset, EntityFlag.Locked));
        Assert.True(back.Layer(layer).IsEmpty);
    }

    /// <summary>The editor itself, hidden: a robot dragged from the Place panel and dropped on the view, then the
    /// filter's "Select all matching" (through the editor's commands).</summary>
    [MapDataFact]
    public void The_editor_drops_a_dragged_robot_and_selects_all_matching()
    {
        var exe = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "SkyNetEdit.exe" : "SkyNetEdit");
        Assert.True(File.Exists(exe), exe + " is not built beside the tests");
        var prefs = Path.Combine(Path.GetTempPath(), $"SkyNetEdit-{Guid.NewGuid():N}.json");
        var info = new ProcessStartInfo(exe)
        {
            ArgumentList =
            {
                "--mission", "1", "--prefs", prefs, "--data", EditDocumentTests.DataDir!, "--width", "640", "--height", "360",
                "--pitch", "-60", "--frames", "4", "--console-log",
                "--command", "place.drag robot: 320 250; place.drop; outliner.filter robot; outliner.select",
            },
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        info.Environment["SKYNET_OFFSCREEN"] = "1";
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync();
        Assert.True(process.WaitForExit(80_000), "SkyNetEdit did not finish");
        Assert.Equal(0, process.ExitCode);
        Assert.Contains("Dropped as @", output.Result, StringComparison.Ordinal);
        Assert.Matches(@"[1-9]\d* selected", output.Result);
    }
}
