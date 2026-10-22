namespace SkyNet.Edit;

/// <summary>A disc the generator levels: corners within <see cref="Radius"/> cells of (col, row) at
/// <see cref="Height"/> (null: their mean), blended into the land over <see cref="Blend"/> more cells.</summary>
public readonly record struct FlatArea(int Col, int Row, double Radius, double? Height = null, double Blend = 8);

/// <summary>A height band's tile: corners below <see cref="Below"/> world units take <see cref="Tile"/> (the first band that fits).</summary>
public readonly record struct TileBand(double Below, int Tile);

/// <summary>
/// The generator's settings. Heights run from <see cref="Range"/>'s min to max; <see cref="FeatureCells"/> is the
/// size of the largest hills in cells; erosion moves material down slopes steeper than <see cref="Talus"/>
/// world units per cell, <see cref="ErosionPasses"/> times.
/// </summary>
public sealed record TerrainGenSettings
{
    public int Seed { get; init; } = 1;
    public int Width { get; init; } = Formats.WldTerrain.GridW;
    public int Height { get; init; } = Formats.WldTerrain.GridH;
    public HeightRange Range { get; init; } = new(0, 2400);
    public double FeatureCells { get; init; } = 64;
    public int Octaves { get; init; } = 5;
    public double Persistence { get; init; } = 0.5;
    public double Lacunarity { get; init; } = 2.0;
    public int ErosionPasses { get; init; } = 20;
    public double Talus { get; init; } = 160;
    /// <summary>Share (0..1) of the excess slope a pass moves.</summary>
    public double ErosionRate { get; init; } = 0.5;
    public IReadOnlyList<FlatArea> Flats { get; init; } = [];
    /// <summary>Tiles by height; empty: tiles stay.</summary>
    public IReadOnlyList<TileBand> Tiles { get; init; } = [];
}

/// <summary>
/// A new map's ground (editor-tools plan, T7): seeded fractal noise, a light thermal erosion, levelled areas
/// (bases, landing pads) and tiles by height. Integer-hashed noise and a fixed pass order: one seed, one field.
/// </summary>
public static class TerrainGenerator
{
    /// <summary>The corner heights (row-major, <see cref="TerrainGenSettings.Width"/> × <see cref="TerrainGenSettings.Height"/>), world units.</summary>
    public static double[] Heights(TerrainGenSettings s)
    {
        var (w, h) = (s.Width, s.Height);
        var heights = new double[w * h];
        for (var row = 0; row < h; row++)
        {
            for (var col = 0; col < w; col++)
            {
                var n = TerrainNoise.Fractal(s.Seed, col / s.FeatureCells, row / s.FeatureCells, s.Octaves, s.Persistence, s.Lacunarity);
                heights[row * w + col] = s.Range.Min + (n + 1) / 2 * s.Range.Span;
            }
        }

        Erode(heights, w, h, s);
        foreach (var flat in s.Flats)
        {
            Flatten(heights, w, h, flat);
        }

        for (var i = 0; i < heights.Length; i++)
        {
            heights[i] = Math.Clamp(heights[i], s.Range.Min, s.Range.Max);
        }

        return heights;
    }

    /// <summary>The tiles for <paramref name="heights"/> by <see cref="TerrainGenSettings.Tiles"/>; null where no band fits.</summary>
    public static int?[] Tiles(TerrainGenSettings s, IReadOnlyList<double> heights)
    {
        var tiles = new int?[heights.Count];
        for (var i = 0; i < tiles.Length; i++)
        {
            foreach (var band in s.Tiles)
            {
                if (heights[i] < band.Below)
                {
                    tiles[i] = band.Tile;
                    break;
                }
            }
        }

        return tiles;
    }

    /// <summary>A new terrain for MAP.<paramref name="map"/> made from <paramref name="s"/> (no undo step on
    /// <paramref name="history"/>: it is new).</summary>
    public static TerrainEdit Generate(int map, TerrainGenSettings s, RetroEng.Editor.UndoStack? history = null)
    {
        var file = Formats.WldFile.Create(s.Width, s.Height);
        Apply(new TerrainEdit(map, file), s);
        return new TerrainEdit(map, file, history);
    }

    /// <summary>Regenerates <paramref name="terrain"/>'s heights (and tiles, with bands) as one undo step.</summary>
    public static void Apply(TerrainEdit terrain, TerrainGenSettings s)
    {
        s = s with { Width = terrain.Width, Height = terrain.Height };
        var heights = Heights(s);
        var tiles = s.Tiles.Count > 0 ? Tiles(s, heights) : null;
        terrain.Apply([.. heights.Select(h => (double?)h)], tiles, $"Generate terrain (seed {s.Seed})");
    }

    // Thermal erosion: each pass, a corner steeper than the talus to its lowest neighbour sheds part of the excess
    // to it; changes are gathered first and applied after, so the scan order does not matter.
    private static void Erode(double[] heights, int w, int h, TerrainGenSettings s)
    {
        var delta = new double[heights.Length];
        for (var pass = 0; pass < s.ErosionPasses; pass++)
        {
            Array.Clear(delta);
            for (var row = 0; row < h; row++)
            {
                for (var col = 0; col < w; col++)
                {
                    var i = row * w + col;
                    var (lowest, drop) = (-1, s.Talus);
                    foreach (var (dc, dr) in (ReadOnlySpan<(int, int)>)[(-1, 0), (1, 0), (0, -1), (0, 1)])
                    {
                        var (c, r) = (col + dc, row + dr);
                        if (c < 0 || r < 0 || c >= w || r >= h)
                        {
                            continue;
                        }

                        if (heights[i] - heights[r * w + c] is var d && d > drop)
                        {
                            (lowest, drop) = (r * w + c, d);
                        }
                    }

                    if (lowest < 0)
                    {
                        continue;
                    }

                    var moved = (drop - s.Talus) / 2 * s.ErosionRate;
                    delta[i] -= moved;
                    delta[lowest] += moved;
                }
            }

            for (var i = 0; i < heights.Length; i++)
            {
                heights[i] += delta[i];
            }
        }
    }

    private static void Flatten(double[] heights, int w, int h, FlatArea flat)
    {
        var reach = flat.Radius + Math.Max(flat.Blend, 0);
        var (c0, c1) = (Math.Max((int)Math.Floor(flat.Col - reach), 0), Math.Min((int)Math.Ceiling(flat.Col + reach), w - 1));
        var (r0, r1) = (Math.Max((int)Math.Floor(flat.Row - reach), 0), Math.Min((int)Math.Ceiling(flat.Row + reach), h - 1));
        double Distance(int col, int row) => Math.Sqrt((col - flat.Col) * (col - flat.Col) + (row - flat.Row) * (row - flat.Row));

        // The level: given, or the mean inside the disc.
        var level = flat.Height;
        if (level == null)
        {
            double sum = 0;
            var n = 0;
            for (var row = r0; row <= r1; row++)
            {
                for (var col = c0; col <= c1; col++)
                {
                    if (Distance(col, row) <= flat.Radius)
                    {
                        sum += heights[row * w + col];
                        n++;
                    }
                }
            }

            level = n > 0 ? sum / n : heights[Math.Clamp(flat.Row, 0, h - 1) * w + Math.Clamp(flat.Col, 0, w - 1)];
        }

        for (var row = r0; row <= r1; row++)
        {
            for (var col = c0; col <= c1; col++)
            {
                var d = Distance(col, row);
                if (d > reach)
                {
                    continue;
                }

                // 1 inside the disc, a smoothstep down to 0 across the blend ring.
                var t = d <= flat.Radius || flat.Blend <= 0 ? 1.0 : 1 - (d - flat.Radius) / flat.Blend;
                var k = t * t * (3 - 2 * t);
                var i = row * w + col;
                heights[i] += (level.Value - heights[i]) * k;
            }
        }
    }
}
