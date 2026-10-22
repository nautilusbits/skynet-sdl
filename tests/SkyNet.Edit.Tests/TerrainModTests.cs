using System.Numerics;
using RetroEng.Physics;
using SkyNet.Formats;
using SkyNet.Game.Baking;
using SkyNet.Game.Collision;
using SkyNet.Game.Mods;

namespace SkyNet.Edit.Tests;

/// <summary>An edited terrain saved into a mod (<c>maps/WLD.NNN</c>) is the ground the game stands MAP.NNN on:
/// its heightmap, collision and bake input; the system ZIP's block baked on the DOS ground is not used.</summary>
[Collection(EditModsCollection.Name)]
public sealed class TerrainModTests : IDisposable
{
    private const string ModName = "terrain-test";
    private const int Map = 210;
    private readonly string _mods = Directory.CreateTempSubdirectory("skynet-edit-terrain-").FullName;

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

    [MapDataFact]
    public void The_game_reads_the_mods_terrain_for_ground_collision_and_bake()
    {
        var data = GameData.Open(EditDocumentTests.DataDir!)!;
        var dos = data.WldBytes(Map)!;
        var terrain = TerrainEdit.Open(data, Map)!;
        Assert.Equal(dos, terrain.ToBytes());

        // A hill at corner (60, 60).
        var (x, z) = terrain.WorldOf(60, 60);
        var before = terrain.HeightAt(x, z);
        var hill = new TerrainBrush { Tool = BrushTool.Raise, Strength = 1, Radius = 4 * TerrainEdit.CellSize, Falloff = BrushFalloff.Constant };
        for (var i = 0; i < 5; i++)
        {
            terrain.Brushes.Dab(hill, x + 37, z - 61);
        }

        terrain.Brushes.EndStroke();
        var (px, pz) = (x + 37, z - 61);
        var edited = terrain.HeightAt(px, pz);
        Assert.True(edited > before + 400, $"{before} → {edited}");
        terrain.Save(Path.Combine(_mods, ModName));

        ModContent.Mount(_mods, [ModName]);
        Assert.Equal(edited, data.LoadWld(Map)!.HeightAt(px, pz));

        // Collision: the ground heightfield, and a ray cast down onto it (render z = −DOS z).
        var (world, _, wld, _) = LevelCollision.Load(data, Map)!.Value;
        Assert.Equal(edited, wld!.HeightAt(px, pz));
        Assert.Equal(edited, world.Sources.OfType<HeightField>().Single().HeightAt((float)px, (float)-pz), 2);
        Assert.True(world.Raycast(new Vector3((float)px, 20000f, (float)-pz), -Vector3.UnitY, 40000f, out var hit));
        Assert.Equal(edited, hit.Point.Y, 1);

        // The bake reads the mod's ground when it plays with the mods, the DOS one for the system ZIP.
        using (var played = new BakeInputs(data, BakeTables.Current))
        {
            Assert.Equal(terrain.ToBytes(), played.Read(MapTerrains.FileName(Map)));
        }

        using (var shipped = new BakeInputs(data, BakeTables.Shipped))
        {
            Assert.Equal(dos, shipped.Read(MapTerrains.FileName(Map)));
        }

        ModContent.Unmount();
        Assert.Equal(before, data.LoadWld(Map)!.HeightAt(x, z));
    }

    [MapDataFact]
    public void A_block_baked_on_the_dos_ground_is_refused_while_a_mod_changes_it()
    {
        var data = GameData.Open(EditDocumentTests.DataDir!)!;
        using var store = new BlockStore(_mods, data);
        store.Bake([Map]);
        store.Mount();
        Assert.NotNull(store.Load(Map));

        var terrain = TerrainEdit.Open(data, Map)!;
        terrain.SetTile(10, 10, 5, TileTurn.Quarter);
        terrain.Save(Path.Combine(_mods, ModName));
        ModContent.Mount(_mods, [ModName]);
        store.Mount();
        Assert.Null(store.Load(Map));

        ModContent.Unmount();
        Assert.NotNull(store.Load(Map));
        store.Unmount();
    }
}
