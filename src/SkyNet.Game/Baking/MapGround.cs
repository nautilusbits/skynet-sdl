using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using RetroEng.Render;
using SkyNet.Formats;

namespace SkyNet.Game.Baking;

/// <summary>
/// The ground layer of the map blocks (docs/formats/map-block.md): the whole heightmap field of one
/// WLD as the ground's vertices, four per cell, row by row. It depends only on the WLD and the
/// ground's tiles (TEXTURE.302), so every map standing on the same WLD (the variant maps:
/// WLD.216 = WLD.217, WLD.230 = WLD.234 = WLD.235...) shares one entry, named by the hash of those
/// inputs (<see cref="Key"/>). A block keeps only its crop (the cells it builds) and the entry's
/// path; <see cref="Crop"/> cuts the block's ground out of the field exactly as
/// <see cref="Terrain.Geometry"/> builds it (same vertices, indices and chunks).
/// </summary>
public sealed class MapGround
{
    public const int FormatVersion = 1;
    public static ReadOnlySpan<byte> Magic => "SNMG"u8;
    public const int HeaderSize = 32;

    /// <summary>The input names of a ground (not map-specific: one ground serves every map on that WLD).</summary>
    public const string WldInput = "WLD", TilesInput = "TEXTURE.302";

    /// <summary>The heightmap's cells across and down (the field Terrain builds: one less than the corners).</summary>
    public const int Columns = WldTerrain.GridW - 1, Rows = WldTerrain.GridH - 1;

    private MapGround(string key, BlockInput[] inputs, Vertex[] vertices)
    {
        Key = key;
        Inputs = inputs;
        Vertices = vertices;
    }

    /// <summary>The MD5 (hex) of the versions and the inputs: the entry's name.</summary>
    public string Key { get; }

    /// <summary>The WLD's and the tiles' MD5s (names <see cref="WldInput"/>, <see cref="TilesInput"/>).</summary>
    public BlockInput[] Inputs { get; }

    /// <summary>Four vertices per cell (NW, NE, SE, SW), cells row by row over the whole field.</summary>
    public Vertex[] Vertices { get; }

    public string Path => PathOf(Key);
    public string SourceHash => SourceHashOf(Key);

    /// <summary>A ground's path inside the system ZIP or a mod.</summary>
    public static string PathOf(string key) => $"maps/ground/{key}.ground";

    public static string SourceHashOf(string key) => $"mapground {FormatVersion} tool {MapBaker.ToolVersion} md5 {key}";

    /// <summary>The key of the ground of a WLD and tile bank with these MD5s.</summary>
    public static string KeyOf(string wldMd5, string tilesMd5) =>
        Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(
            $"mapground {FormatVersion} tool {MapBaker.ToolVersion}\n{TilesInput}={tilesMd5}\n{WldInput}={wldMd5}\n")));

    /// <summary>The ground of <paramref name="wld"/> over the tiles of <paramref name="layout"/>
    /// (<see cref="Terrain.Build"/> of the whole field, reordered row by row).</summary>
    public static MapGround Build(WldTerrain wld, Terrain.AtlasLayout layout, string wldMd5, string tilesMd5)
    {
        var full = Terrain.Build(wld, layout, default);
        if (full.Vertices.Length != Columns * Rows * 4)
        {
            throw new InvalidDataException($"The ground has {full.Vertices.Length} vertices, not {Columns * Rows * 4}.");
        }

        var vertices = new Vertex[full.Vertices.Length];
        var at = 0;
        foreach (var (c0, r0, c1, r1) in Chunks(default))
        {
            for (var row = r0; row < r1; row++)
            {
                Array.Copy(full.Vertices, at, vertices, (row * Columns + c0) * 4, (c1 - c0) * 4);
                at += (c1 - c0) * 4;
            }
        }

        return new MapGround(KeyOf(wldMd5, tilesMd5), [new(TilesInput, tilesMd5), new(WldInput, wldMd5)], vertices);
    }

    /// <summary>The ground of <paramref name="cells"/> (an empty rectangle: the whole field), as
    /// <see cref="Terrain.Geometry"/> builds it: chunk by chunk, each chunk's cells row by row,
    /// four vertices and two triangles per cell, each chunk's box over its corners.</summary>
    public Terrain.TerrainGeometry Crop((int Col, int Row, int W, int H) cells)
    {
        var chunks = Chunks(cells).ToArray();
        var total = chunks.Sum(c => (c.C1 - c.C0) * (c.R1 - c.R0));
        var vertices = new Vertex[total * 4];
        var indices = new uint[total * 6];
        var list = new List<Terrain.Chunk>(chunks.Length);
        int at = 0, index = 0;
        foreach (var (c0, r0, c1, r1) in chunks)
        {
            var first = index;
            var min = new Vector3(float.MaxValue);
            var max = new Vector3(float.MinValue);
            var run = (c1 - c0) * 4;
            for (var row = r0; row < r1; row++)
            {
                Array.Copy(Vertices, (row * Columns + c0) * 4, vertices, at, run);
                for (var k = at; k < at + run; k++)
                {
                    min = Vector3.Min(min, vertices[k].Position);
                    max = Vector3.Max(max, vertices[k].Position);
                }

                for (var v = (uint)at; v < at + run; v += 4)
                {
                    indices[index++] = v;
                    indices[index++] = v + 1;
                    indices[index++] = v + 2;
                    indices[index++] = v;
                    indices[index++] = v + 2;
                    indices[index++] = v + 3;
                }

                at += run;
            }

            if (index > first)
            {
                list.Add(new Terrain.Chunk(first, index - first, min, max));
            }
        }

        return new Terrain.TerrainGeometry(vertices, indices, list);
    }

    // The chunks' cell rectangles [C0, C1) x [R0, R1) in Terrain.Geometry's order (the same clamping
    // of the rectangle; within a chunk the cells go row by row).
    private static IEnumerable<(int C0, int R0, int C1, int R1)> Chunks((int Col, int Row, int W, int H) cells)
    {
        var (c0, r0) = (Math.Max(cells.Col, 0), Math.Max(cells.Row, 0));
        var (c1, r1) = cells.W <= 0 || cells.H <= 0
            ? (WldTerrain.GridW - 1, WldTerrain.GridH - 1)
            : (Math.Min(cells.Col + cells.W, WldTerrain.GridW - 1), Math.Min(cells.Row + cells.H, WldTerrain.GridH - 1));
        for (var cr = r0; cr < r1; cr += Terrain.ChunkCells)
        {
            for (var cc = c0; cc < c1; cc += Terrain.ChunkCells)
            {
                yield return (cc, cr, Math.Min(cc + Terrain.ChunkCells, c1), Math.Min(cr + Terrain.ChunkCells, r1));
            }
        }
    }

    // ---- The entry: header, key, inputs, vertices ----

    public byte[] ToBytes()
    {
        if (!BitConverter.IsLittleEndian)
        {
            throw new PlatformNotSupportedException("Map grounds are written on little-endian hosts only.");
        }

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(Magic);
        w.Write((ushort)FormatVersion);
        w.Write((ushort)HeaderSize);
        w.Write(Columns);
        w.Write(Rows);
        w.Write(new byte[HeaderSize - 16]);
        Str(w, Key);
        w.Write(Inputs.Length);
        foreach (var i in Inputs)
        {
            Str(w, i.Name);
            Str(w, i.Md5);
        }

        w.Write(Vertices.Length);
        w.Write(MemoryMarshal.AsBytes(Vertices.AsSpan()));
        w.Flush();
        return ms.ToArray();
    }

    /// <summary>The ground in <paramref name="bytes"/>; <see cref="InvalidDataException"/> when it is
    /// not a ground of this version or is cut short.</summary>
    public static MapGround Read(byte[] bytes)
    {
        if (bytes.Length < HeaderSize || !bytes.AsSpan(0, 4).SequenceEqual(Magic))
        {
            throw new InvalidDataException("Not a map ground.");
        }

        if (BitConverter.ToUInt16(bytes, 4) is var version && version != FormatVersion)
        {
            throw new InvalidDataException($"Map ground version {version}, this build reads {FormatVersion}.");
        }

        if (BitConverter.ToInt32(bytes, 8) != Columns || BitConverter.ToInt32(bytes, 12) != Rows)
        {
            throw new InvalidDataException("Map ground: not a field of this size.");
        }

        try
        {
            using var r = new BinaryReader(new MemoryStream(bytes, HeaderSize, bytes.Length - HeaderSize, writable: false));
            var key = ReadStr(r);
            var inputs = new BlockInput[Count(r)];
            for (var i = 0; i < inputs.Length; i++)
            {
                inputs[i] = new BlockInput(ReadStr(r), ReadStr(r));
            }

            var vertices = new Vertex[Count(r)];
            if (vertices.Length != Columns * Rows * 4)
            {
                throw new InvalidDataException("Map ground: not four vertices per cell.");
            }

            r.BaseStream.ReadExactly(MemoryMarshal.AsBytes(vertices.AsSpan()));
            return new MapGround(key, inputs, vertices);
        }
        catch (EndOfStreamException e)
        {
            throw new InvalidDataException("Map ground: cut short.", e);
        }
    }

    private static void Str(BinaryWriter w, string s)
    {
        var b = Encoding.UTF8.GetBytes(s);
        w.Write(b.Length);
        w.Write(b);
    }

    private static int Count(BinaryReader r)
    {
        var n = r.ReadInt32();
        return n >= 0 && n <= r.BaseStream.Length - r.BaseStream.Position ? n
            : throw new InvalidDataException("Map ground: a bad count.");
    }

    private static string ReadStr(BinaryReader r) => Encoding.UTF8.GetString(r.ReadBytes(Count(r)));
}
