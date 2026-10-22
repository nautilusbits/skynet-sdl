using SkyNet.Formats;
using SkyNet.Game.Mods;

namespace SkyNet.Edit;

/// <summary>The Terrain mode's <see cref="ITerrainModel"/> over the model API's <see cref="TerrainEdit"/>:
/// render z ↔ DOS z (z = −DOS z), per-frame amounts ↔ per-dab strengths, the dirty box as a list.
/// Imports and the generator give a preview grid; Apply is TerrainEdit.SetHeights (one undo step).</summary>
public sealed class TerrainModeModel(TerrainEdit terrain) : ITerrainModel
{
    private const int TileBits = 0x3F;
    private const int TurnBits = 0xC0;

    public int Corners => terrain.Width;

    public float Height(int col, int row) => (float)terrain.CornerHeight(col, row);

    /// <summary>Layer 2 as the game draws it (smoothed when loaded), else as stored.</summary>
    public int Tile(int col, int row) => terrain.ToWld()?.SampleByte((int)WldLayer.Tile, col, row) ?? terrain.Raw(WldLayer.Tile, col, row);

    public int Map => terrain.Map;

    public WldTerrain? Ground() => terrain.ToWld();

    public byte[]? Edited() => terrain.IsChanged ? terrain.ToBytes() : null;

    // TerrainBrushes opens a stroke on its first dab.
    public void BeginStroke(string label)
    {
    }

    public void Dab(in BrushDab dab)
    {
        // Turn: the cell under the centre a quarter on (its own undo step).
        if (dab.Tool == TerrainTool.Turn)
        {
            var (col, row) = terrain.CellAt(dab.Centre.X, -dab.Centre.Y);
            terrain.TurnTile(col, row);
            return;
        }

        var tool = dab.Tool switch
        {
            TerrainTool.Lower => BrushTool.Lower,
            TerrainTool.Smooth => BrushTool.Smooth,
            TerrainTool.Flatten => BrushTool.Flatten,
            TerrainTool.Noise => BrushTool.Noise,
            TerrainTool.Paint => BrushTool.Paint,
            _ => BrushTool.Raise,
        };

        // Raise/Lower/Noise amounts are world units; a dab of strength 1 moves StepPerDab.
        var strength = tool is BrushTool.Raise or BrushTool.Lower or BrushTool.Noise ? dab.Amount / TerrainBrushes.StepPerDab : dab.Amount;
        var brush = new TerrainBrush
        {
            Tool = tool,
            Radius = dab.Radius,
            Strength = Math.Clamp(strength, 0.0, 1.0),
            Falloff = dab.Falloff,
            FlattenHeight = dab.Target,
            Tile = dab.Tile & TileBits,
            Turn = (TileTurn)(dab.Tile & TurnBits),
            Seed = dab.Seed,
        };
        terrain.Brushes.Dab(brush, dab.Centre.X, -dab.Centre.Y);
    }

    public void EndStroke() => terrain.Brushes.EndStroke();

    public IReadOnlyList<GridRect> TakeDirty() => terrain.TakeDirty() is { IsEmpty: false } rect ? [rect] : [];

    public HeightGrid ImportPng(string path) =>
        Grid(TerrainImport.Heights(TerrainPng.Decode(File.ReadAllBytes(path)), terrain.Width, terrain.Height, HeightRange.Full).Select(h => (double?)h));

    public void ExportPng(string path) => File.WriteAllBytes(path, TerrainImport.ExportPng(terrain, PngDepth.Bits16));

    // Corners the mesh does not cover keep their heights.
    public HeightGrid ImportMesh(string path)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        byte[]? Beside(string name) => File.Exists(Path.Combine(dir, name)) ? File.ReadAllBytes(Path.Combine(dir, name)) : null;
        var model = RetroEng.Models.Gltf.Load(File.ReadAllBytes(path), Beside);
        return Grid(TerrainImport.SampleMesh(model, terrain.Width, terrain.Height, MeshFit.Uniform, HeightRange.Full));
    }

    // Roughness: smaller hills and rougher octaves.
    public HeightGrid Generate(GenerateSettings settings) => Grid(TerrainGenerator.Heights(new TerrainGenSettings
    {
        Seed = settings.Seed,
        Width = terrain.Width,
        Height = terrain.Height,
        Range = new HeightRange(settings.MinHeight, settings.MaxHeight),
        FeatureCells = 96 - 64 * settings.Roughness,
        Persistence = 0.3 + 0.4 * settings.Roughness,
    }).Select(h => (double?)h));

    public void Apply(HeightGrid grid, string label)
    {
        var heights = new double?[grid.Heights.Length];
        for (var i = 0; i < heights.Length; i++)
        {
            heights[i] = grid.Heights[i];
        }

        terrain.SetHeights(heights, label);
    }

    public string? Save(string modFolder)
    {
        if (!terrain.IsChanged)
        {
            return null;
        }

        terrain.Save(modFolder);
        return MapTerrains.PathOf(terrain.Map);
    }

    public void WriteCopy(string modFolder)
    {
        if (terrain.IsChanged)
        {
            terrain.WriteCopy(modFolder);
        }
    }

    // Heights as a preview grid; a null keeps the corner's height now.
    private HeightGrid Grid(IEnumerable<double?> heights)
    {
        var all = heights.ToArray();
        var grid = new float[all.Length];
        for (var i = 0; i < all.Length; i++)
        {
            grid[i] = (float)(all[i] ?? terrain.CornerHeight(i % terrain.Width, i / terrain.Width));
        }

        return new HeightGrid(terrain.Width, grid);
    }
}
