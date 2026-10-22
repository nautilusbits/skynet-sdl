using System.Text;
using RetroEng.Editor;
using SkyNet.Formats;

namespace SkyNet.Edit.Tests;

/// <summary>The terrain model (editor-tools plan, T7): brushes, one undo step per stroke, dirty regions,
/// heightmap PNGs, mesh sampling, the generator.</summary>
public sealed class TerrainEditTests
{
    private const double Cell = TerrainEdit.CellSize;
    private const double Base = 1000;

    // A 256 × 256 field at 1000 units, its own history empty.
    private static TerrainEdit Flat(double height = Base)
    {
        var t = TerrainEdit.Create(900);
        t.SetHeights(Enumerable.Repeat<double?>(height, t.Width * t.Height).ToArray(), "flat");
        t.History.Clear();
        t.TakeDirty();
        return t;
    }

    // World (x, DOS z) of corner (col, row).
    private static (double X, double Z) At(TerrainEdit t, int col, int row) => t.WorldOf(col, row);

    private static TerrainBrush Brush(BrushTool tool, double strength = 1, double radiusCells = 4) =>
        new() { Tool = tool, Strength = strength, Radius = radiusCells * Cell, Falloff = BrushFalloff.Linear };

    [Fact]
    public void Raise_and_lower_move_the_centre_most_and_leave_the_outside()
    {
        var t = Flat();
        var (x, z) = At(t, 100, 100);
        t.Brushes.Dab(Brush(BrushTool.Raise), x, z);
        t.Brushes.Dab(Brush(BrushTool.Raise), x, z);
        t.Brushes.EndStroke();

        Assert.True(t.CornerHeight(100, 100) >= Base + 2 * TerrainBrushes.StepPerDab - 80, $"{t.CornerHeight(100, 100)}");
        Assert.True(t.CornerHeight(102, 100) > Base && t.CornerHeight(102, 100) < t.CornerHeight(100, 100));
        Assert.Equal(Base, t.CornerHeight(105, 100));
        Assert.Equal(t.CornerHeight(100, 100), t.HeightAt(x, z));

        t.Brushes.Dab(Brush(BrushTool.Lower), x, z);
        t.Brushes.Dab(Brush(BrushTool.Lower), x, z);
        t.Brushes.Dab(Brush(BrushTool.Lower), x, z);
        t.Brushes.EndStroke();
        Assert.True(t.CornerHeight(100, 100) < Base);
    }

    [Fact]
    public void Dabs_below_a_curve_step_add_up_within_a_stroke()
    {
        var t = Flat();
        var (x, z) = At(t, 50, 50);
        var brush = Brush(BrushTool.Raise, strength: 5 / TerrainBrushes.StepPerDab);
        t.Brushes.Dab(brush, x, z);
        Assert.Equal(Base, t.CornerHeight(50, 50));   // 5 units: under the 40-unit step there
        for (var i = 0; i < 39; i++)
        {
            t.Brushes.Dab(brush, x, z);
        }

        t.Brushes.EndStroke();
        Assert.InRange(t.CornerHeight(50, 50), Base + 160, Base + 240);
    }

    [Fact]
    public void Smooth_flattens_a_spike_flatten_reaches_the_target_noise_is_seeded()
    {
        var t = Flat();
        var heights = Enumerable.Repeat<double?>(null, t.Width * t.Height).ToArray();
        heights[100 * t.Width + 100] = 3000;
        t.SetHeights(heights, "spike");
        var (x, z) = At(t, 100, 100);
        for (var i = 0; i < 10; i++)
        {
            t.Brushes.Dab(Brush(BrushTool.Smooth), x, z);
        }

        t.Brushes.EndStroke();
        Assert.True(t.CornerHeight(100, 100) < 1500, $"{t.CornerHeight(100, 100)}");

        var flatten = Brush(BrushTool.Flatten) with { FlattenHeight = 400, Falloff = BrushFalloff.Constant };
        for (var i = 0; i < 20; i++)
        {
            t.Brushes.Dab(flatten, x, z);
        }

        t.Brushes.EndStroke();
        Assert.All(Enumerable.Range(98, 5), c => Assert.Equal(400, t.CornerHeight(c, 100)));

        // Noise: the same seed gives the same ground, another seed another.
        double[] Noise(int seed)
        {
            var n = Flat();
            n.Brushes.Dab(Brush(BrushTool.Noise, radiusCells: 10) with { Seed = seed, NoiseCells = 2 }, x, z);
            return [.. Enumerable.Range(90, 21).Select(c => n.CornerHeight(c, 100))];
        }

        Assert.Equal(Noise(7), Noise(7));
        Assert.NotEqual(Noise(7), Noise(8));
        Assert.Contains(Noise(7), h => h != Base);
    }

    [Fact]
    public void Paint_sets_tile_and_turn_on_the_cells_under_the_brush()
    {
        var t = Flat();
        var (x, z) = t.WorldOf(20.5, 30.5);
        t.Brushes.Dab(new TerrainBrush { Tool = BrushTool.Paint, Radius = 1.2 * Cell, Tile = 58, Turn = TileTurn.Half }, x, z);
        t.Brushes.EndStroke();

        Assert.Equal((58, TileTurn.Half), (t.Tile(20, 30), t.Turn(20, 30)));
        Assert.Equal(58, t.Tile(21, 30));
        Assert.Equal(0, t.Tile(22, 30));
        Assert.Equal(1, t.History.UndoCount);

        t.TurnTile(20, 30);
        Assert.Equal(TileTurn.ThreeQuarters, t.Turn(20, 30));
        t.TurnTile(20, 30);
        Assert.Equal((58, TileTurn.None), (t.Tile(20, 30), t.Turn(20, 30)));
        Assert.True(t.ToWld()!.IsWaterAt(x, z));
    }

    [Fact]
    public void A_stroke_is_one_undo_step_and_undo_restores_every_byte()
    {
        var history = new UndoStack();
        var t = TerrainEdit.Create(900, history: history);
        var before = t.ToBytes();
        var (x, z) = At(t, 128, 128);

        for (var i = 0; i < 6; i++)
        {
            t.Brushes.Dab(Brush(BrushTool.Raise), x + i * 100, z);
        }

        t.Brushes.EndStroke();
        Assert.Equal(1, history.UndoCount);
        Assert.Equal("Raise", history.UndoLabel);
        var raised = t.ToBytes();
        Assert.True(t.IsChanged);

        t.Brushes.Dab(Brush(BrushTool.Lower), x, z);
        t.Brushes.EndStroke();
        Assert.Equal(2, history.UndoCount);

        Assert.True(history.Undo());
        Assert.Equal(raised, t.ToBytes());
        Assert.True(history.Undo());
        Assert.Equal(before, t.ToBytes());
        Assert.False(t.IsChanged);
        Assert.True(history.Redo());
        Assert.Equal(raised, t.ToBytes());
    }

    [Fact]
    public void Dirty_covers_the_change_and_its_normals_and_names_its_chunks()
    {
        var t = Flat();
        Assert.True(t.TakeDirty().IsEmpty);
        var (x, z) = At(t, 40, 70);
        t.Brushes.Dab(Brush(BrushTool.Raise, radiusCells: 2), x, z);
        t.Brushes.EndStroke();

        var dirty = t.TakeDirty();
        Assert.Equal(new GridRect(38, 68, 5, 5), dirty);   // corners 39..41 changed, one more each side
        Assert.True(t.TakeDirty().IsEmpty);
        Assert.Equal([(2, 4)], TerrainEdit.ChunksOf(dirty, 16));

        t.History.Undo();
        Assert.Equal(dirty, t.TakeDirty());
    }

    [Fact]
    public void A_16_bit_heightmap_round_trips_every_corner()
    {
        var t = TerrainGenerator.Generate(900, new TerrainGenSettings { Seed = 3, Range = HeightRange.Full });
        var png = TerrainImport.ExportPng(t, PngDepth.Bits16);
        var image = TerrainPng.Decode(png);
        Assert.Equal((256, 256, PngDepth.Bits16), (image.Width, image.Height, image.Depth));

        var back = Flat(0);
        TerrainImport.ImportPng(back, png);
        Assert.Equal(1, back.History.UndoCount);
        for (var row = 0; row < t.Height; row++)
        {
            for (var col = 0; col < t.Width; col++)
            {
                Assert.Equal(t.CornerHeight(col, row), back.CornerHeight(col, row));
            }
        }
    }

    [Fact]
    public void An_8_bit_heightmap_of_another_size_is_resampled_over_the_range()
    {
        // A 2 × 2 PNG, black west, white east: the field ramps from 0 to 2000 across.
        var png = TerrainPng.Encode(2, 2, [0, ushort.MaxValue, 0, ushort.MaxValue], PngDepth.Bits8);
        Assert.Equal(PngDepth.Bits8, TerrainPng.Decode(png).Depth);
        var t = Flat();
        TerrainImport.ImportPng(t, png, new HeightRange(0, 2000));
        Assert.Equal(0, t.CornerHeight(0, 77));
        Assert.Equal(2000, t.CornerHeight(255, 3));
        Assert.InRange(t.CornerHeight(128, 128), 960, 1040);
        Assert.Throws<InvalidDataException>(() => TerrainPng.Decode([1, 2, 3]));
    }

    [Fact]
    public void A_mesh_is_sampled_from_above()
    {
        // A square pyramid, base 10 × 10 at y 0, apex 5 high, stretched over the field and 0..2000.
        var t = Flat();
        var set = TerrainImport.ImportMesh(t, Pyramid(), MeshFit.Stretch, new HeightRange(0, 2000));
        Assert.Equal(t.Width * t.Height, set);
        Assert.Equal(0, t.CornerHeight(0, 0));
        Assert.Equal(0, t.CornerHeight(255, 255));
        Assert.InRange(t.CornerHeight(127, 127), 1950, 2000);
        Assert.InRange(t.CornerHeight(64, 127), 960, 1040);
        Assert.Equal(1, t.History.UndoCount);

        // Uniform: the same scale for height as for x/z, heights clamped to the range.
        var u = Flat();
        TerrainImport.ImportMesh(u, Pyramid(), MeshFit.Uniform, new HeightRange(0, 520));
        Assert.Equal(520, u.CornerHeight(127, 127));
    }

    [Fact]
    public void The_generator_is_deterministic_levels_its_flats_and_bands_its_tiles()
    {
        var s = new TerrainGenSettings
        {
            Seed = 42,
            Flats = [new FlatArea(100, 100, 6, 800)],
            Tiles = [new TileBand(1200, 58), new TileBand(double.MaxValue, 3)],
        };
        Assert.Equal(TerrainGenerator.Heights(s), TerrainGenerator.Heights(s));
        Assert.NotEqual(TerrainGenerator.Heights(s), TerrainGenerator.Heights(s with { Seed = 43 }));

        var t = TerrainGenerator.Generate(901, s);
        Assert.Equal(0, t.History.UndoCount);
        Assert.Equal(TerrainGenerator.Generate(901, s).ToBytes(), t.ToBytes());
        Assert.All(Enumerable.Range(96, 9), c => Assert.Equal(800, t.CornerHeight(c, 100)));
        Assert.Equal(58, t.Tile(100, 100));
        var heights = TerrainGenerator.Heights(s);
        Assert.Contains(heights, h => h >= 1200);
        Assert.Contains(Enumerable.Range(0, t.Width), c => t.Tile(c, 0) == 3);
        Assert.InRange(heights.Min(), s.Range.Min, s.Range.Max);
        Assert.InRange(heights.Max(), s.Range.Min, s.Range.Max);
        Assert.NotNull(t.ToWld());

        // Applied to an open terrain: one undo step.
        var open = Flat();
        TerrainGenerator.Apply(open, s);
        Assert.Equal(1, open.History.UndoCount);
        Assert.Equal(t.ToBytes(), open.ToBytes());
    }

    [Fact]
    public void Save_writes_maps_WLD_NNN_and_moves_the_save_point()
    {
        var mod = Directory.CreateTempSubdirectory("skynet-terrain-save-").FullName;
        try
        {
            var t = Flat();
            Assert.True(t.IsChanged);   // flattened from the new file's zero
            var path = t.Save(mod);
            Assert.Equal(Path.Combine(mod, "maps", "WLD.900"), path);
            Assert.False(t.IsChanged);
            Assert.Equal(t.ToBytes(), File.ReadAllBytes(path));
            Assert.Equal(Base, WldTerrain.Parse(File.ReadAllBytes(path))!.CornerHeight(5, 5));
        }
        finally
        {
            Directory.Delete(mod, recursive: true);
        }
    }

    // A square pyramid as a .gltf with its buffer embedded.
    private static byte[] Pyramid()
    {
        float[] positions = [0, 0, 0, 10, 0, 0, 10, 0, 10, 0, 0, 10, 5, 5, 5];
        uint[] indices = [0, 1, 4, 1, 2, 4, 2, 3, 4, 3, 0, 4];
        var buffer = new byte[positions.Length * 4 + indices.Length * 4];
        Buffer.BlockCopy(positions, 0, buffer, 0, positions.Length * 4);
        Buffer.BlockCopy(indices, 0, buffer, positions.Length * 4, indices.Length * 4);
        var json = $$"""
            { "asset": { "version": "2.0" }, "scene": 0, "scenes": [ { "nodes": [0] } ], "nodes": [ { "mesh": 0 } ],
              "meshes": [ { "primitives": [ { "attributes": { "POSITION": 0 }, "indices": 1 } ] } ],
              "buffers": [ { "byteLength": {{buffer.Length}}, "uri": "data:application/octet-stream;base64,{{Convert.ToBase64String(buffer)}}" } ],
              "bufferViews": [ { "buffer": 0, "byteOffset": 0, "byteLength": 60 }, { "buffer": 0, "byteOffset": 60, "byteLength": 48 } ],
              "accessors": [
                { "bufferView": 0, "componentType": 5126, "count": 5, "type": "VEC3", "min": [0, 0, 0], "max": [10, 5, 10] },
                { "bufferView": 1, "componentType": 5125, "count": 12, "type": "SCALAR" } ] }
            """;
        return Encoding.UTF8.GetBytes(json);
    }
}
