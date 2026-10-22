using System.Numerics;
using System.Text;
using SkyNet.Formats;
using SkyNet.Game.Campaign;
using SkyNet.Game.Collision;
using SkyNet.Game.Enemies;
using SkyNet.Game.Mods;
using SkyNet.Game.Weapons;

namespace SkyNet.Edit.Tests;

/// <summary>Placing (editor plan step E7): the palettes, place / delete / duplicate through undo, redo
/// and Save, a placed prop taking the map's per-name defaults, and a robot placed in a saved layer
/// that the game spawns and that sees and shoots the player.</summary>
[Collection(EditModsCollection.Name)]
public sealed class PlaceTests : IDisposable
{
    private const float Dt = 1f / 35f;
    private readonly string _mods = Directory.CreateTempSubdirectory("skynet-edit-place-").FullName;

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

    private static string Snapshot(EditDocument doc, string layer)
    {
        var s = new StringBuilder(Convert.ToHexString(doc.Layer(layer).ToBytes())).Append('\n');
        foreach (var e in doc.Map(layer).Entities)
        {
            s.Append(EditDocument.EntityText(e)).Append('\n');
        }

        return s.ToString();
    }

    private static (GameData Data, MissionFile Mission, EditDocument Doc, string Layer) Mission1()
    {
        var data = GameData.Open(EditDocumentTests.DataDir!)!;
        var mission = MissionGenerator.Build(data, MissionTable.For(data.Game).ByNumber(1)!.Value);
        var doc = EditDocument.Load(data, mission, null);
        var layer = mission.World.Instance(mission.World.Start.Instance)!.Layer;
        return (data, mission, doc, layer);
    }

    [MapDataFact]
    public void Place_delete_duplicate_go_through_undo_redo_and_save()
    {
        var (data, mission, doc, layer) = Mission1();
        var map = doc.Map(layer);
        foreach (var kind in Enum.GetValues<PlaceKind>())
        {
            Assert.NotEmpty(PlacePalette.Items(kind, map));
        }

        var start = map.Entities.First(e => e.IsMarker && e.MarkerType == 0);
        var feet = new Vector3(start.X, -start.Y - 0x10, -start.Z);
        // A prop whose name has a defaults node with a link record: it takes them as DOS does,
        // and the layer does not write them.
        var prop = PlacePalette.Items(PlaceKind.Prop, map).First(i => map.Defaults.TryGetValue(i.Fields["nameIndex"], out var d) && d.Link != null);
        var def = map.Defaults[prop.Fields["nameIndex"]];
        var propKey = PlacePalette.Place(doc, layer, prop, feet + new Vector3(200f, 0f, 0f));
        Assert.True(doc.Map(layer).TryGetEntityAt(propKey, out var placed));
        Assert.True(placed.UsesDefaults);
        Assert.Equal((def.HitPoints, def.StateByte, def.LinkOffset, def.Link!.Value.ActType), (placed.HitPoints, placed.StateByte, placed.LinkOffset, placed.ActType));
        Assert.DoesNotContain("hitPoints", Encoding.UTF8.GetString(doc.Layer(layer).ToBytes()), StringComparison.Ordinal);

        var robot = PlacePalette.Items(PlaceKind.Robot, map).First(i => i.Label.Contains("endo", StringComparison.Ordinal));
        var robotKey = PlacePalette.Place(doc, layer, robot, feet);
        Assert.True(doc.Map(layer).TryGetEntityAt(robotKey, out var r));
        Assert.Equal((2, robot.Fields["enemy"], start.X, start.Y, start.Z), (r.MarkerType, r.EnemyType, r.X, r.Y, r.Z));
        PlacePalette.Place(doc, layer, PlacePalette.Items(PlaceKind.Pickup, map)[0], feet + new Vector3(0f, 0f, 100f));
        PlacePalette.Place(doc, layer, PlacePalette.Items(PlaceKind.Marker, map).First(i => i.Label.Contains("(new)", StringComparison.Ordinal)), feet);
        PlacePalette.Place(doc, layer, PlacePalette.Items(PlaceKind.Light, map)[0], feet);
        var copy = PlacePalette.Duplicate(doc, layer, robotKey);
        Assert.True(doc.Map(layer).TryGetEntityAt(copy, out var c));
        Assert.Equal((r.MarkerType, r.EnemyType, r.X + PlacePalette.DuplicateOffset, r.Z), (c.MarkerType, c.EnemyType, c.X, c.Z));
        var propCopy = PlacePalette.Duplicate(doc, layer, propKey);
        Assert.True(doc.Map(layer).TryGetEntityAt(propCopy, out var pc) && pc.UsesDefaults);
        var baseMesh = map.Entities.First(e => e.Variant == MapEntityVariant.Mesh);
        var baseCopy = PlacePalette.Duplicate(doc, layer, baseMesh.FileOffset);
        Assert.True(doc.Map(layer).TryGetEntityAt(baseCopy, out var bc));
        Assert.Equal((baseMesh.NameIndex, baseMesh.HitPoints, baseMesh.ActType, baseMesh.Yaw), (bc.NameIndex, bc.HitPoints, bc.ActType, bc.Yaw));
        doc.DeleteEntity(layer, baseMesh.FileOffset);
        doc.DeleteEntity(layer, propCopy);
        Assert.False(doc.Map(layer).TryGetEntityAt(baseMesh.FileOffset, out _));
        var steps = 10;
        var after = Snapshot(doc, layer);

        // Every step undoes back to the opened map and redoes to the same one.
        for (var i = 0; i < steps; i++)
        {
            Assert.True(doc.Undo());
        }

        Assert.False(doc.Undo());
        Assert.True(doc.Layer(layer).IsEmpty);
        Assert.False(doc.IsDirty);
        for (var i = 0; i < steps; i++)
        {
            Assert.True(doc.Redo());
        }

        Assert.Equal(after, Snapshot(doc, layer));

        // Saved, reopened with the mod, played by the game: the same map.
        var folder = SaveTarget.Folder(_mods, "e7", ["skynet", "shock"], out _)!;
        doc.Save(folder);
        ModContent.Mount(_mods, ["e7"]);
        var back = EditDocument.Load(data, mission, ModContent.Files);
        Assert.Equal(after, Snapshot(back, layer));
        Assert.Equal(doc.Map(layer).Entities.Select(EditDocument.EntityText), data.LoadMap(doc.MapNumber(layer))!.Entities.Select(EditDocument.EntityText));
    }

    [MapDataFact]
    public void A_robot_placed_in_a_saved_layer_spawns_in_the_game_and_sees_and_shoots_the_player()
    {
        var (data, _, doc, layer) = Mission1();
        var number = doc.MapNumber(layer);
        var wld = data.LoadWld(number);
        var map = doc.Map(layer);

        // Where to stand it: the spot 300 units from one of the map's armed walkers, in its sight;
        // that walker is deleted and the player stands where it stood.
        var (spot, player, walkerKey, type) = Spot(data, map, wld);
        var item = PlacePalette.Items(PlaceKind.Robot, map).First(i => i.Fields["enemy"] == type);
        doc.DeleteEntity(layer, walkerKey);
        var key = PlacePalette.Place(doc, layer, item, spot);
        doc.Save(SaveTarget.Folder(_mods, "e7robot", ["skynet", "shock"], out _)!);

        ModContent.Mount(_mods, ["e7robot"]);
        var played = data.LoadMap(number)!;
        Assert.True(played.TryGetEntityAt(key, out _));
        var world = LevelCollision.Build(data, played, wld).World;
        var soldier = new Soldier { Feet = player };
        var weapons = new WeaponSystem(WeaponTable.Load(data), world) { Player = soldier };
        var robots = new Robots(SkyNet.Data.GameTables.Ais, new EnemyModels(data), world, weapons, seed: 7);
        robots.Populate(played);
        Assert.DoesNotContain(robots.All, x => x.MarkerOffset == walkerKey);
        var robot = Assert.Single(robots.All, x => x.MarkerOffset == key);
        for (var t = 0f; t < 20f && (robot.ShotsFired == 0 || soldier.Taken <= 0f); t += Dt)
        {
            robots.Tick(Dt, player);
            weapons.Tick(default, new Shooter(player + new Vector3(0f, 75f, 0f), 0f, 0f), Dt);
        }

        Assert.False(robot.Dead);
        Assert.True(Vector2.Distance(new(robot.Position.X, robot.Position.Z), new(spot.X, spot.Z)) < 200f, $"{robot.Position} vs {spot}");
        Assert.True(robot.Sees);
        Assert.True(robot.ShotsFired > 0);
        Assert.True(soldier.Taken > 0f, $"{robot.ShotsFired} shots, none hit");
    }

    private static (Vector3 Spot, Vector3 Player, int WalkerKey, int Type) Spot(GameData data, MapFile map, WldTerrain? wld)
    {
        var world = LevelCollision.Build(data, map, wld).World;
        var robots = new Robots(SkyNet.Data.GameTables.Ais, new EnemyModels(data), world, null, seed: 7);
        robots.Populate(map);
        robots.Tick(Dt, null);
        foreach (var r in robots.All.Where(r => r.State == 7 && r.Root.Fire != null).OrderBy(r => r.Name != "endorfl"))
        {
            var feetY = r.Position.Y + r.FootOffset;
            for (var i = 0; i < 16; i++)
            {
                var a = i * MathF.Tau / 16f;
                var p = r.Position + new Vector3(MathF.Cos(a), 0f, MathF.Sin(a)) * 300f;
                if (world.Raycast(new Vector3(p.X, feetY + 60f, p.Z), -Vector3.UnitY, 300f, out var hit)
                    && MathF.Abs(hit.Point.Y - feetY) < 40f && r.LineOfSight(hit.Point)
                    && map.TryGetEntityAt(r.MarkerOffset, out var marker))
                {
                    return (hit.Point, new Vector3(r.Position.X, feetY, r.Position.Z), r.MarkerOffset, marker.EnemyType);
                }
            }
        }

        throw new InvalidOperationException("no walker in the open");
    }

    private sealed class Soldier : IDamageable
    {
        public Vector3 Feet { get; set; }
        public float Taken { get; private set; }
        public Vector3 Centre => Feet + new Vector3(0f, 38f, 0f);
        public float Radius => 0f;
        public bool Alive => true;

        public void TakeDamage(in Damage damage) => Taken += damage.Points;
    }
}
