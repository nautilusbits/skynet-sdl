using System.Numerics;
using RetroEng.Models;

namespace SkyNet.Edit;

/// <summary>The heights a heightmap's black (<see cref="Min"/>) and white (<see cref="Max"/>) stand for, world units.</summary>
public readonly record struct HeightRange(double Min, double Max)
{
    /// <summary>The whole height curve: 0 to <see cref="TerrainEdit.MaxHeight"/>.</summary>
    public static HeightRange Full => new(0, TerrainEdit.MaxHeight);

    public double Span => Max - Min;
}

/// <summary>How a mesh is laid over the terrain.</summary>
public enum MeshFit
{
    /// <summary>One scale for x, z and height: the mesh's larger side fits the field, centred; heights from <see cref="HeightRange.Min"/> up.</summary>
    Uniform,
    /// <summary>x and z each fill the field; the mesh's height span fills the <see cref="HeightRange"/>.</summary>
    Stretch,
}

/// <summary>
/// Terrain in and out (editor-tools plan, T7): greyscale heightmap PNGs (8 or 16-bit, one pixel per corner,
/// row 0 north; another size is resampled bilinearly) and a glTF mesh sampled from above (each corner takes
/// the highest surface over it, as a ray cast down would hit it). Imports are one undo step each.
/// </summary>
public static class TerrainImport
{
    private const double SampleMax = ushort.MaxValue;

    /// <summary>The terrain's heights as a greyscale PNG, <paramref name="range"/> (default <see cref="HeightRange.Full"/>) from black to white.</summary>
    public static byte[] ExportPng(TerrainEdit terrain, PngDepth depth, HeightRange? range = null)
    {
        var r = range ?? HeightRange.Full;
        var samples = new ushort[terrain.Width * terrain.Height];
        for (var row = 0; row < terrain.Height; row++)
        {
            for (var col = 0; col < terrain.Width; col++)
            {
                var t = r.Span > 0 ? (terrain.CornerHeight(col, row) - r.Min) / r.Span : 0;
                samples[row * terrain.Width + col] = (ushort)Math.Round(Math.Clamp(t, 0, 1) * SampleMax);
            }
        }

        return TerrainPng.Encode(terrain.Width, terrain.Height, samples, depth);
    }

    /// <summary>Sets the heights from a heightmap PNG, black to white over <paramref name="range"/> (default
    /// <see cref="HeightRange.Full"/>). <see cref="InvalidDataException"/> when the PNG does not read.</summary>
    public static void ImportPng(TerrainEdit terrain, byte[] png, HeightRange? range = null)
    {
        var heights = Heights(TerrainPng.Decode(png), terrain.Width, terrain.Height, range ?? HeightRange.Full);
        terrain.SetHeights([.. heights.Select(h => (double?)h)], "Import heightmap");
    }

    /// <summary>The heights of <paramref name="image"/> resampled (bilinear) to <paramref name="width"/> × <paramref name="height"/> corners.</summary>
    public static double[] Heights(GreyImage image, int width, int height, HeightRange range)
    {
        var heights = new double[width * height];
        for (var row = 0; row < height; row++)
        {
            for (var col = 0; col < width; col++)
            {
                // Corner centres map onto pixel centres: the field's edges onto the image's edges.
                var x = width > 1 ? col * (image.Width - 1) / (double)(width - 1) : 0;
                var y = height > 1 ? row * (image.Height - 1) / (double)(height - 1) : 0;
                heights[row * width + col] = range.Min + Bilinear(image, x, y) / SampleMax * range.Span;
            }
        }

        return heights;
    }

    /// <summary>Sets the heights from a glTF (<c>.glb</c> or <c>.gltf</c>, <paramref name="resolve"/> gives files
    /// beside it); corners the mesh does not cover keep theirs. The corners set; <see cref="InvalidDataException"/>
    /// when the file does not read.</summary>
    public static int ImportMesh(TerrainEdit terrain, byte[] gltf, MeshFit fit = MeshFit.Uniform, HeightRange? range = null,
        Func<string, byte[]?>? resolve = null)
    {
        var heights = SampleMesh(Gltf.Load(gltf, resolve), terrain.Width, terrain.Height, fit, range ?? HeightRange.Full);
        terrain.SetHeights(heights, "Import mesh");
        return heights.Count(h => h != null);
    }

    /// <summary>
    /// The heights a mesh gives <paramref name="width"/> × <paramref name="height"/> corners: the mesh's x runs
    /// east (columns), its z south (rows), y up (glTF's axes as the game draws them); each corner takes the
    /// highest triangle over it, null where none is.
    /// </summary>
    public static double?[] SampleMesh(GltfModel model, int width, int height, MeshFit fit, HeightRange range)
    {
        var heights = new double?[width * height];
        var (min, max) = model.Bounds;
        var span = max - min;
        if (span.X <= 0 || span.Z <= 0)
        {
            return heights;
        }

        // Mesh space → (column, row, height).
        var (sx, sz) = ((width - 1) / span.X, (height - 1) / span.Z);
        var (ox, oz) = (0f, 0f);
        double sy;
        if (fit == MeshFit.Uniform)
        {
            sx = sz = Math.Min(sx, sz);
            (ox, oz) = (((width - 1) - span.X * sx) / 2, ((height - 1) - span.Z * sz) / 2);
            sy = sx * TerrainEdit.CellSize;
        }
        else
        {
            sy = span.Y > 0 ? range.Span / span.Y : 0;
        }

        Vector3 Grid(Vector3 p) => new((p.X - min.X) * sx + ox, (float)((p.Y - min.Y) * sy + range.Min), (p.Z - min.Z) * sz + oz);

        foreach (var prim in model.Primitives)
        {
            for (var t = 0; t + 2 < prim.Indices.Length; t += 3)
            {
                Rasterise(Grid(prim.Positions[prim.Indices[t]]), Grid(prim.Positions[prim.Indices[t + 1]]),
                    Grid(prim.Positions[prim.Indices[t + 2]]), width, height, heights);
            }
        }

        for (var i = 0; i < heights.Length; i++)
        {
            heights[i] = heights[i] is { } h ? Math.Clamp(h, range.Min, range.Max) : null;
        }

        return heights;
    }

    // Every corner inside triangle (a, b, c) (x = column, z = row, y = height) takes the larger of its height there and its own.
    private static void Rasterise(Vector3 a, Vector3 b, Vector3 c, int width, int height, double?[] heights)
    {
        const float edge = -1e-4f;
        var area = (b.X - a.X) * (c.Z - a.Z) - (c.X - a.X) * (b.Z - a.Z);
        if (MathF.Abs(area) < 1e-9f)
        {
            return;
        }

        var (c0, c1) = (Math.Max((int)MathF.Ceiling(MathF.Min(a.X, MathF.Min(b.X, c.X))), 0), Math.Min((int)MathF.Floor(MathF.Max(a.X, MathF.Max(b.X, c.X))), width - 1));
        var (r0, r1) = (Math.Max((int)MathF.Ceiling(MathF.Min(a.Z, MathF.Min(b.Z, c.Z))), 0), Math.Min((int)MathF.Floor(MathF.Max(a.Z, MathF.Max(b.Z, c.Z))), height - 1));
        for (var row = r0; row <= r1; row++)
        {
            for (var col = c0; col <= c1; col++)
            {
                var wa = ((b.X - col) * (c.Z - row) - (c.X - col) * (b.Z - row)) / area;
                var wb = ((c.X - col) * (a.Z - row) - (a.X - col) * (c.Z - row)) / area;
                var wc = 1 - wa - wb;
                if (wa < edge || wb < edge || wc < edge)
                {
                    continue;
                }

                var h = wa * a.Y + wb * b.Y + wc * c.Y;
                var i = row * width + col;
                heights[i] = heights[i] is { } old ? Math.Max(old, h) : h;
            }
        }
    }

    private static double Bilinear(GreyImage image, double x, double y)
    {
        var (x0, y0) = ((int)Math.Floor(x), (int)Math.Floor(y));
        var (x1, y1) = (Math.Min(x0 + 1, image.Width - 1), Math.Min(y0 + 1, image.Height - 1));
        var (u, v) = (x - x0, y - y0);
        double S(int px, int py) => image.Samples[py * image.Width + px];
        var top = S(x0, y0) + (S(x1, y0) - S(x0, y0)) * u;
        var bottom = S(x0, y1) + (S(x1, y1) - S(x0, y1)) * u;
        return top + (bottom - top) * v;
    }
}
