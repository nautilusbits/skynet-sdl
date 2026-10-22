using System.Numerics;
using RetroEng.Render;
using SkyNet.Formats;

namespace SkyNet.Game;

/// <summary>
/// The outdoor ground: the WLD heightmap as one indexed mesh, cut in chunks of
/// <see cref="ChunkCells"/> × <see cref="ChunkCells"/> cells drawn as ranges of it (so a chunk past
/// the far clip is skipped), textured from one atlas of the TEXTURE.302 tiles: the whole ground is
/// one texture and a few draws.
/// As the Godot port builds it (wld_terrain.gd): each 256-unit cell takes one whole tile
/// (layer 2 &amp; 0x3F), turned by bits 6/7, its corners at the height curve of layer 0, split on
/// the NW–SE diagonal; world x = col·256, z = row·256 − 65 536 (Y up, −Z north). Each vertex
/// carries the smooth normal of its corner (<see cref="CornerNormal"/>; the enhanced look reads it,
/// the original ignores it).
/// </summary>
public sealed class Terrain
{
    public const int ChunkCells = 16;
    public const int TileBank = 302;
    private const int Tiles = 64;
    private const int AtlasColumns = 8;

    private readonly MeshHandle _mesh;
    private readonly List<Chunk> _chunks = [];
    private readonly Material _material;

    /// <summary>A chunk's index range and bounds (box; the sphere round it).</summary>
    public readonly record struct Chunk(int First, int Count, Vector3 Min, Vector3 Max)
    {
        public Vector3 Centre => (Min + Max) / 2f;
        public float Radius => Vector3.Distance(Min, Max) / 2f;
    }

    /// <summary>The ground's vertices and indices, and its chunks as ranges of them.</summary>
    public sealed record TerrainGeometry(Vertex[] Vertices, uint[] Indices, IReadOnlyList<Chunk> Chunks);

    /// <summary>Where the TEXTURE.302 tiles sit in the atlas: 8 × 8 slots of <paramref name="Slot"/>
    /// texels (the largest tile), <paramref name="Side"/> texels across, each tile's own size.</summary>
    public sealed record AtlasLayout(int Slot, int Side, (int W, int H)[] Sizes);

    public int ChunkCount => _chunks.Count;
    public int Triangles { get; }
    public IReadOnlyList<Chunk> Chunks => _chunks;

    /// <param name="cells">The cells to build (col, row, width, height); an empty one: the whole field.</param>
    public Terrain(Renderer renderer, WldTerrain wld, TextureStore textures, TextureHandle palette, (int Col, int Row, int W, int H) cells)
        : this(renderer, textures, palette, Build(wld, Layout(textures.Bank(TileBank)), cells))
    {
    }

    /// <summary>The ground of a baked <paramref name="geometry"/>; the atlas is made from the data's TEXTURE.302.</summary>
    public Terrain(Renderer renderer, TextureStore textures, TextureHandle palette, TerrainGeometry geometry)
    {
        var bank = textures.Bank(TileBank);
        var (slot, side, _) = Layout(bank);
        var atlas = new byte[side * side];
        Array.Fill(atlas, (byte)HazeTable.HazeIndex);
        for (var t = 0; t < Tiles; t++)
        {
            var r = bank != null && t < bank.Records.Length ? bank.Records[t] : null;
            if (r == null || r.IsEmpty)
            {
                continue;
            }

            var (ax, ay) = (t % AtlasColumns * slot, t / AtlasColumns * slot);
            for (var y = 0; y < r.Height; y++)
            {
                r.Pixels.AsSpan().Slice(y * r.Width, r.Width).CopyTo(atlas.AsSpan((ay + y) * side + ax, r.Width));
            }
        }

        TextureStore.OpaqueZero(atlas);
        var texture = renderer.CreateIndexTexture(side, side, atlas);
        if (HdAtlas(textures, bank, slot, side) is { } hd)
        {
            // Released: the atlas is made here alone and drawn through the level's palette only.
            renderer.ReplaceTexture(texture, hd.Side, hd.Side, hd.Rgba, hd.Relief, HdPixels.Released);
        }

        textures.TrackBankSurface(texture, TileBank);

        _material = new Material(texture, palette, Vector4.One, 1, RenderState.DoubleSided, Shading.Lit);
        _chunks.AddRange(geometry.Chunks);
        Triangles = geometry.Indices.Length / 3;
        _mesh = renderer.CreateMesh(geometry.Vertices, geometry.Indices);
    }

    /// <summary>The atlas from the tiles' HD textures (TextureStore.HdImage), all at one scale; null
    /// unless every tile has one. With a relief atlas when any tile has relief maps (the rest flat).</summary>
    private static (int Side, byte[] Rgba, byte[]? Relief)? HdAtlas(TextureStore textures, TextureBank? bank, int slot, int side)
    {
        if (bank == null)
        {
            return null;
        }

        var scale = 0;
        byte[]? atlas = null;
        byte[]? relief = null;
        var hdSide = 0;
        for (var t = 0; t < Tiles && t < bank.Records.Length; t++)
        {
            var r = bank.Records[t];
            if (r.IsEmpty)
            {
                continue;
            }

            if (textures.HdImage(TileBank, t, opaque: true, r.Width, r.Height) is not { } hd || (scale != 0 && hd.Width != r.Width * scale))
            {
                return null;
            }

            if (atlas == null)
            {
                scale = hd.Width / r.Width;
                hdSide = side * scale;
                atlas = new byte[hdSide * hdSide * 4];
                for (var i = 3; i < atlas.Length; i += 4)
                {
                    atlas[i] = 255;
                }
            }

            var (ax, ay) = (t % AtlasColumns * slot * scale, t / AtlasColumns * slot * scale);
            for (var y = 0; y < r.Height * scale; y++)
            {
                hd.Rgba.AsSpan(y * hd.Width * 4, hd.Width * 4).CopyTo(atlas.AsSpan(((ay + y) * hdSide + ax) * 4));
            }

            if (hd.Relief is { } tile)
            {
                relief ??= FlatRelief(hdSide * hdSide);
                for (var y = 0; y < r.Height * scale; y++)
                {
                    tile.AsSpan(y * hd.Width * 4, hd.Width * 4).CopyTo(relief.AsSpan(((ay + y) * hdSide + ax) * 4));
                }
            }

            // Copied into the atlas: the tile's own pixels are not needed after the load.
            Mods.ModContent.Handed(TileBank, t, opaque: true);
        }

        return atlas == null ? null : (hdSide, atlas, relief);
    }

    /// <summary>A relief layer of <paramref name="texels"/> facing straight out, as rough as can be
    /// (the normal (0, 0, 1) as the sRGB decode gives it back, roughness 1).</summary>
    private static byte[] FlatRelief(int texels)
    {
        var relief = new byte[texels * 4];
        var half = RetroEng.Render.Srgb.FromLinear(128 / 255f);
        for (var i = 0; i < relief.Length; i += 4)
        {
            (relief[i], relief[i + 1], relief[i + 2], relief[i + 3]) = (half, half, 255, 255);
        }

        return relief;
    }

    /// <summary>The atlas layout of the tile bank (8 × 8 slots of the largest tile size, at least 64).</summary>
    public static AtlasLayout Layout(TextureBank? bank)
    {
        var slot = 64;
        if (bank != null)
        {
            foreach (var r in bank.Records)
            {
                slot = Math.Max(slot, Math.Max(r.Width, r.Height));
            }
        }

        var sizes = new (int W, int H)[Tiles];
        for (var t = 0; t < Tiles; t++)
        {
            var r = bank != null && t < bank.Records.Length ? bank.Records[t] : null;
            sizes[t] = r == null || r.IsEmpty ? (1, 1) : (r.Width, r.Height);
        }

        return new AtlasLayout(slot, slot * AtlasColumns, sizes);
    }

    /// <summary>The ground mesh of a WLD over <paramref name="cells"/> with the atlas <paramref name="layout"/> (no GPU).</summary>
    public static TerrainGeometry Build(WldTerrain wld, AtlasLayout layout, (int Col, int Row, int W, int H) cells) =>
        Geometry(wld.CornerHeight, (col, row) => wld.SampleByte(2, col, row), layout.Sizes, layout.Slot, layout.Side, cells);

    /// <summary>The ground mesh of <paramref name="cells"/> (see the constructor) over corner
    /// heights <paramref name="height"/>(col, row) and layer-2 bytes <paramref name="layer2"/>, the
    /// tiles' atlas slots from <paramref name="sizes"/>, <paramref name="slot"/> and
    /// <paramref name="side"/>; every vertex carries its <see cref="CornerNormal"/>.</summary>
    public static TerrainGeometry Geometry(Func<int, int, double> height, Func<int, int, int> layer2, (int W, int H)[] sizes,
        int slot, int side, (int Col, int Row, int W, int H) cells)
    {
        var (c0, r0) = (Math.Max(cells.Col, 0), Math.Max(cells.Row, 0));
        var (c1, r1) = cells.W <= 0 || cells.H <= 0
            ? (WldTerrain.GridW - 1, WldTerrain.GridH - 1)
            : (Math.Min(cells.Col + cells.W, WldTerrain.GridW - 1), Math.Min(cells.Row + cells.H, WldTerrain.GridH - 1));

        // One normal per heightfield corner, shared by the (up to four) cells and chunks meeting there.
        var normals = new Dictionary<(int, int), uint>();
        uint Normal(int col, int row)
        {
            if (!normals.TryGetValue((col, row), out var packed))
            {
                normals[(col, row)] = packed = Vertex.PackNormal(CornerNormal(height, col, row));
            }

            return packed;
        }

        var chunks = new List<Chunk>();
        var vertices = new List<Vertex>();
        var indices = new List<uint>();
        Span<Vector2> q = stackalloc Vector2[4];
        for (var cr = r0; cr < r1; cr += ChunkCells)
        {
            for (var cc = c0; cc < c1; cc += ChunkCells)
            {
                var first = indices.Count;
                var min = new Vector3(float.MaxValue);
                var max = new Vector3(float.MinValue);
                for (var row = cr; row < Math.Min(cr + ChunkCells, r1); row++)
                {
                    for (var col = cc; col < Math.Min(cc + ChunkCells, c1); col++)
                    {
                        float x0 = col * 256f, x1 = (col + 1) * 256f;
                        float zn = row * 256f - (float)WldTerrain.ZFlipK, zs = (row + 1) * 256f - (float)WldTerrain.ZFlipK;
                        var pNW = new Vector3(x0, (float)height(col, row), zn);
                        var pNE = new Vector3(x1, (float)height(col + 1, row), zn);
                        var pSE = new Vector3(x1, (float)height(col + 1, row + 1), zs);
                        var pSW = new Vector3(x0, (float)height(col, row + 1), zs);
                        min = Vector3.Min(min, Vector3.Min(Vector3.Min(pNW, pNE), Vector3.Min(pSE, pSW)));
                        max = Vector3.Max(max, Vector3.Max(Vector3.Max(pNW, pNE), Vector3.Max(pSE, pSW)));

                        var b2 = layer2(col, row);
                        var tile = b2 & 0x3F;
                        var orient = ((b2 & 0x40) >> 6) | ((b2 & 0x80) >> 6);
                        (q[0], q[1], q[2], q[3]) = (new(0f, 0f), new(1f, 0f), new(1f, 1f), new(0f, 1f));
                        for (var turn = 0; turn < orient; turn++)
                        {
                            for (var i = 0; i < 4; i++)
                            {
                                q[i] = new Vector2(1f - q[i].Y, q[i].X);
                            }
                        }

                        var (w, h) = sizes[tile];
                        var (ax, ay) = (tile % AtlasColumns * slot, tile / AtlasColumns * slot);
                        var at = (uint)vertices.Count;
                        // Half a texel in from each edge: nearest sampling never reaches the next slot.
                        Vector2 Uv(Vector2 local) => new((ax + 0.5f + local.X * (w - 1)) / side, (ay + 0.5f + local.Y * (h - 1)) / side);
                        vertices.Add(new Vertex(pNW, Uv(q[0]), Vertex.White, Normal(col, row)));
                        vertices.Add(new Vertex(pNE, Uv(q[1]), Vertex.White, Normal(col + 1, row)));
                        vertices.Add(new Vertex(pSE, Uv(q[2]), Vertex.White, Normal(col + 1, row + 1)));
                        vertices.Add(new Vertex(pSW, Uv(q[3]), Vertex.White, Normal(col, row + 1)));
                        indices.AddRange([at, at + 1, at + 2, at, at + 2, at + 3]);
                    }
                }

                var count = indices.Count - first;
                if (count > 0)
                {
                    chunks.Add(new Chunk(first, count, min, max));
                }
            }
        }

        return new TerrainGeometry([.. vertices], [.. indices], chunks);
    }

    /// <summary>
    /// The smooth ground normal at corner (col, row): central differences of the corner heights
    /// (one-sided at the field's edge), so it depends on the heightfield alone, not on the NW–SE
    /// split or on which cell or chunk the vertex belongs to; flat ground is +Y. Row grows southward
    /// (+Z), col eastward (+X).
    /// </summary>
    public static Vector3 CornerNormal(Func<int, int, double> height, int col, int row)
    {
        var (cw, ce) = (Math.Max(col - 1, 0), Math.Min(col + 1, WldTerrain.GridW - 1));
        var (rn, rs) = (Math.Max(row - 1, 0), Math.Min(row + 1, WldTerrain.GridH - 1));
        var dx = ce > cw ? (height(ce, row) - height(cw, row)) / ((ce - cw) * WldTerrain.WorldPerCell) : 0.0;
        var dz = rs > rn ? (height(col, rs) - height(col, rn)) / ((rs - rn) * WldTerrain.WorldPerCell) : 0.0;
        return Vector3.Normalize(new Vector3((float)-dx, 1f, (float)-dz));
    }

    public Material Material => _material;

    /// <summary>Queues the chunks that reach within <paramref name="far"/> of the eye; returns how many.</summary>
    /// <param name="material">In place of the ground's own (the automap's, out of the haze); null: its own.</param>
    public int Draw(Renderer renderer, Vector3 eye, float far, Material? material = null)
    {
        var drawn = 0;
        var surface = material ?? _material;
        foreach (var chunk in _chunks)
        {
            if (Vector3.Distance(chunk.Centre, eye) - chunk.Radius > far)
            {
                continue;
            }

            renderer.Draw(_mesh, chunk.First, chunk.Count, surface, 0, Matrix4x4.Identity, new Bounds(chunk.Min, chunk.Max));
            drawn++;
        }

        return drawn;
    }
}
