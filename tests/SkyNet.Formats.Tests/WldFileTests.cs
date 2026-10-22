namespace SkyNet.Formats.Tests;

/// <summary>WLD.NNN as an editable container (editor-tools plan, T7): byte round trip, new files, other sizes.</summary>
public class WldFileTests
{
    // Every WLD of SkyNET's folder and of Future Shock's beside it.
    private static IEnumerable<string> ShippedWlds()
    {
        var dirs = new[] { TestData.Dir, GameDataLocator.OtherGameDir(TestData.Dir!) }.OfType<string>();
        return dirs.SelectMany(d => Directory.EnumerateFiles(d))
            .Where(f => Path.GetFileName(f) is var n && n.Length == 7 && n.StartsWith("WLD.", StringComparison.OrdinalIgnoreCase));
    }

    [DataFact]
    public void Every_shipped_WLD_writes_back_byte_for_byte_and_holds_the_layers_the_game_reads()
    {
        var files = ShippedWlds().ToList();
        var shock = GameDataLocator.OtherGameDir(TestData.Dir!) != null;
        Assert.True(files.Count >= (shock ? 27 + 16 : 27), $"{files.Count} WLD files");
        foreach (var path in files)
        {
            var bytes = File.ReadAllBytes(path);
            var file = WldFile.Read(bytes)!;
            Assert.Equal(TestData.Md5(bytes), TestData.Md5(file.Write()));

            var game = WldTerrain.Parse(bytes, smoothMaterials: false)!;
            for (var layer = 0; layer < WldFile.LayerCount; layer++)
            {
                Assert.True(game.Layer(layer).AsSpan().SequenceEqual(file.Layer((WldLayer)layer)), $"{path} layer {layer}");
            }
        }
    }

    [DataFact]
    public void An_edit_lands_in_its_chunk_and_nowhere_else()
    {
        var bytes = File.ReadAllBytes(ShippedWlds().First());
        var file = WldFile.Read(bytes)!;
        file.Set(WldLayer.Height, 200, 140, 99);
        file.Set(WldLayer.Tile, 3, 4, 7);
        var written = file.Write();

        Assert.Equal(bytes.Length, written.Length);
        var (height, tile) = (Offset(bytes, 200, 140, 0), Offset(bytes, 3, 4, 2));
        Assert.Equal((99, 7), (written[height], written[tile]));
        Assert.All(Enumerable.Range(0, bytes.Length).Where(i => bytes[i] != written[i]), i => Assert.Contains(i, new[] { height, tile }));
        var game = WldTerrain.Parse(written, smoothMaterials: false)!;
        Assert.Equal(99, game.SampleByte(0, 200, 140));
        Assert.Equal(7, game.SampleByte(2, 3, 4));
    }

    // File offset of (col, row) of a layer in a 2 × 2 chunked file.
    private static int Offset(byte[] bytes, int col, int row, int layer)
    {
        var chunk = row / 128 * 2 + col / 128;
        var table = BitConverter.ToInt32(bytes, WldTerrain.ChunkTableOffset + chunk * 4);
        return table + WldTerrain.ChunkHeaderSize + layer * 128 * 128 + row % 128 * 128 + col % 128;
    }

    [Fact]
    public void A_new_file_has_the_shipped_layout_and_the_game_reads_it()
    {
        var file = WldFile.Create();
        file.Set(WldLayer.Height, 10, 20, 50);
        var bytes = file.Write();

        // 263 416 bytes as WLD.601: header, table at 0x90, chunks from 0x4A0.
        Assert.Equal(0x4A0 + 4 * (22 + 4 * 128 * 128), bytes.Length);
        Assert.Equal(0x4A0, BitConverter.ToInt32(bytes, WldTerrain.ChunkTableOffset));
        var game = WldTerrain.Parse(bytes)!;
        Assert.Equal(0, game.MissingChunkLayers);
        Assert.Equal(WldTerrain.HeightCurve[50], game.CornerHeight(10, 20));
        Assert.Equal(bytes, WldFile.Read(bytes)!.Write());
    }

    [Fact]
    public void Size_is_a_property_not_a_limit()
    {
        var file = WldFile.Create(384, 128);
        Assert.Equal((384, 128, 3, 1), (file.Width, file.Height, file.ChunksX, file.ChunksY));
        file.Set(WldLayer.Height, 383, 127, 127);
        var back = WldFile.Read(file.Write())!;
        Assert.Equal((384, 128), (back.Width, back.Height));
        Assert.Equal(127, back.Get(WldLayer.Height, 383, 127));
        Assert.Throws<ArgumentException>(() => WldFile.Create(200, 256));

        // The game's own loader takes 256 × 256 only.
        Assert.Null(WldTerrain.Parse(file.Write()));
    }

    [Fact]
    public void A_cut_file_is_refused()
    {
        var bytes = WldFile.Create().Write();
        Assert.Null(WldFile.Read(bytes.AsSpan(0, bytes.Length - 1)));
        Assert.Null(WldFile.Read(bytes.AsSpan(0, 0x80)));
    }
}
