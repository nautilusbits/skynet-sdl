using System.Numerics;
using RetroEng.Physics;
using SkyNet.Formats;
using SkyNet.Game.Baking;
using SkyNet.Game.Campaign;
using SkyNet.Game.Collision;
using SkyNet.Game.Tests.Behaviour;
using Xunit.Abstractions;

namespace SkyNet.Game.Tests.Campaign;

/// <summary>Issue #21: an outdoor pickup stands where its record puts it, as indoors (the record
/// <see cref="Level.IndoorSpriteLift"/> over its foot), never under the ground. Items on roofs,
/// ruins and beams (Future Shock MAP.010, SkyNET MAP.230) were snapped down to the terrain. An
/// item over a mesh's top stands on it: its record is the sprite's centre, so a flat one 16 under
/// sank into a beam.</summary>
public class PickupHeightTests(ITestOutputHelper output)
{
    /// <summary>A fact skipped unless Future Shock's data sits beside SKYNET_DATA's.</summary>
    private sealed class ShockDataFactAttribute : FactAttribute
    {
        public ShockDataFactAttribute()
        {
            if (BehaviourData.Dir is not { } dir || GameDataLocator.OtherGameDir(dir) == null)
            {
                Skip = "no Future Shock data beside SKYNET_DATA's SkyNET folder";
            }
        }
    }

    /// <summary>How far a foot may sit from where the record or the ground puts it.</summary>
    private const float Slack = 1f;

    private static GameData Shock() =>
        GameData.Open(GameDataLocator.OtherGameDir(BehaviourData.Dir!)!, BehaviourData.Dir)!;

    /// <summary>How far under the record the beams' and roofs' tops lie (measured: 4-28).</summary>
    private const float Reach = 32f;

    // The foot the record asks for: the mesh top under it, else 16 under it but not under the ground.
    private static float Expected(MapEntity e, WldTerrain wld, CollisionWorld meshes) =>
        Top(meshes, e) ?? MathF.Max((float)wld.HeightAt(e.X, e.Z), -e.Y - Level.IndoorSpriteLift);

    // The first mesh face straight under the record, within Reach (the world holds no ground).
    private static float? Top(CollisionWorld meshes, MapEntity e) =>
        meshes.Raycast(new Vector3(e.X, -e.Y + 4f, -e.Z), -Vector3.UnitY, Reach + 4f, out var hit) ? hit.Point.Y : null;

    // The map's meshes alone, as the level collides with them.
    private static CollisionWorld Meshes(GameData data, MapFile map) => LevelCollision.Build(data, map, null).World;

    [ShockDataFact]
    public void ShockMap010BeamItemsSitOnTheBeam()
    {
        // The girder over the moat: a row of grenades and ammo, records 742-751 high, the ammo
        // boxes' 4-7 over the girder's top (they sank 9-12 into it).
        var data = Shock();
        var map = data.LoadMap(10)!;
        var meshes = Meshes(data, map);
        var block = MapBaker.Bake(data, 10)!;
        var beam = map.Entities.Where(e => SpriteRules.IsItem(e.SpriteIndex) && e.X is > 14000 and < 14500
                && e.Z is > 46500 and < 46800 && -e.Y < 800)
            .Select(e => e.FileOffset).ToHashSet();
        var items = block.Sprites.SelectMany(g => g.Sprites).Where(s => beam.Contains(s.Offset)).ToList();
        Assert.Equal(6, items.Count);
        Assert.All(items, s => Assert.Equal(Top(meshes, map.Entities.First(e => e.FileOffset == s.Offset))!.Value,
            s.Centre.Y - s.Size.Y / 2f, Slack));
    }

    [BehaviourDataFact]
    public void SkyNetOnlyItemsOverMeshesMove() => AssertMoves(BehaviourData.Data, "SkyNET");

    [ShockDataFact]
    public void ShockOnlyItemsOverMeshesMove() => AssertMoves(Shock(), "Future Shock");

    // Every map's items against the rule before the mesh tops (16 under the record, outdoors not
    // under the ground): an indoor item never moves, an outdoor one only onto a mesh's top.
    private void AssertMoves(GameData data, string game)
    {
        int indoor = 0, outdoor = 0, moved = 0;
        foreach (var number in MapBaker.MapNumbers(data))
        {
            if (data.LoadMap(number) is not { } map || MapBaker.Bake(data, number) is not { } block)
            {
                continue;
            }

            var wld = map.IsOutdoor ? data.LoadWldFor(number)?.Wld : null;
            var meshes = Meshes(data, map);
            var byOffset = map.Entities.ToDictionary(e => e.FileOffset);
            foreach (var s in block.Sprites.Where(g => SpriteRules.IsItem(g.Index)).SelectMany(g => g.Sprites))
            {
                var e = byOffset[s.Offset];
                var record = -e.Y - Level.IndoorSpriteLift;
                var before = wld != null ? MathF.Max((float)wld.HeightAt(e.X, e.Z), record) : record;
                var foot = s.Centre.Y - s.Size.Y / 2f;
                _ = wld != null ? outdoor++ : indoor++;
                if (MathF.Abs(foot - before) <= Slack)
                {
                    continue;
                }

                moved++;
                Assert.True(wld != null, $"MAP.{number:D3} indoor item {e.FileOffset} moved {foot - before:0.#}");
                Assert.Equal(Top(meshes, e) ?? float.NaN, foot, Slack);
            }
        }

        output.WriteLine($"{game}: {indoor} indoor items, none moved; {outdoor} outdoor, {moved} onto a mesh top");
        Assert.True(moved > 0);
    }

    [ShockDataFact]
    public void ShockMission1PickupsStandOnTheirRecords()
    {
        var data = Shock();
        var session = new GameSession(data);
        session.StartMission(session.Table.ByNumber(1)!.Value);
        var map = data.LoadMap(10)!;
        var wld = data.LoadWld(10)!;
        var meshes = Meshes(data, map);
        var byOffset = map.Entities.ToDictionary(e => e.FileOffset);

        var left = session.PickupsLeft.ToList();
        Assert.NotEmpty(left);
        Assert.All(left, p => Assert.Equal(Expected(byOffset[p.Offset], wld, meshes), p.At.Y, Slack));
    }

    [ShockDataFact]
    public void ShockMap010DrawsItemsOnTheirRecords() => AssertBaked(Shock(), 10);

    [BehaviourDataFact]
    public void SkyNetMap230DrawsItemsOnTheirRecords() => AssertBaked(BehaviourData.Data, 230);

    // Every baked item billboard's foot (centre less half its height) where its record puts it.
    private static void AssertBaked(GameData data, int number)
    {
        var block = MapBaker.Bake(data, number)!;
        var map = data.LoadMap(number)!;
        var wld = data.LoadWld(number)!;
        var meshes = Meshes(data, map);
        var byOffset = map.Entities.ToDictionary(e => e.FileOffset);

        var items = block.Sprites.Where(g => SpriteRules.IsItem(g.Index)).SelectMany(g => g.Sprites).ToList();
        Assert.NotEmpty(items);
        Assert.All(items, s => Assert.Equal(Expected(byOffset[s.Offset], wld, meshes), s.Centre.Y - s.Size.Y / 2f, Slack));
    }
}
