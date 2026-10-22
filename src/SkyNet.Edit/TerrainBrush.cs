using SkyNet.Formats;

namespace SkyNet.Edit;

/// <summary>What a terrain brush does.</summary>
public enum BrushTool
{
    /// <summary>Lifts the ground.</summary>
    Raise,
    /// <summary>Sinks the ground.</summary>
    Lower,
    /// <summary>Pulls each corner toward its neighbours' mean.</summary>
    Smooth,
    /// <summary>Pulls each corner toward <see cref="TerrainBrush.FlattenHeight"/>.</summary>
    Flatten,
    /// <summary>Adds seeded noise (<see cref="TerrainBrush.Seed"/>, <see cref="TerrainBrush.NoiseCells"/>).</summary>
    Noise,
    /// <summary>Paints <see cref="TerrainBrush.Tile"/> with <see cref="TerrainBrush.Turn"/> on the cells under it.</summary>
    Paint,
}

/// <summary>How a brush's effect fades from its centre (weight 1) to its rim (weight 0).</summary>
public enum BrushFalloff
{
    /// <summary>Full weight to the rim.</summary>
    Constant,
    /// <summary>1 − t.</summary>
    Linear,
    /// <summary>Smoothstep: 1 − (3t² − 2t³).</summary>
    Smooth,
    /// <summary>A dome: √(1 − t²).</summary>
    Sphere,
}

/// <summary>
/// A brush's settings. <see cref="Radius"/> in world units; <see cref="Strength"/> 0..1 per dab: Raise,
/// Lower and Noise move a corner up to <see cref="TerrainBrushes.StepPerDab"/> world units, Smooth and
/// Flatten pull it that share of the way.
/// </summary>
public sealed record TerrainBrush
{
    public BrushTool Tool { get; init; } = BrushTool.Raise;
    public double Radius { get; init; } = 4 * TerrainEdit.CellSize;
    public double Strength { get; init; } = 0.5;
    public BrushFalloff Falloff { get; init; } = BrushFalloff.Smooth;

    /// <summary>Flatten's target, world units (pick it with <see cref="TerrainEdit.HeightAt"/>).</summary>
    public double FlattenHeight { get; init; }

    /// <summary>Paint's tile (0-63) and its turn.</summary>
    public int Tile { get; init; }
    public TileTurn Turn { get; init; }

    /// <summary>Noise's seed and feature size in cells.</summary>
    public int Seed { get; init; } = 1;
    public double NoiseCells { get; init; } = 4;

    /// <summary>The weight at <paramref name="distance"/> from the centre: 0 at the rim and beyond.</summary>
    public double Weight(double distance)
    {
        if (Radius <= 0 || distance >= Radius)
        {
            return 0;
        }

        var t = distance / Radius;
        return Falloff switch
        {
            BrushFalloff.Constant => 1,
            BrushFalloff.Linear => 1 - t,
            BrushFalloff.Sphere => Math.Sqrt(1 - t * t),
            _ => 1 - t * t * (3 - 2 * t),
        };
    }
}

/// <summary>
/// The brushes over a <see cref="TerrainEdit"/>. Each <see cref="Dab"/> is an undo step that merges into the
/// stroke's previous dab, so one stroke (press, drag, <see cref="EndStroke"/> on release) is one undo step.
/// A stroke sculpts a fine copy of the heights and writes each corner's nearest curve index, so dabs smaller
/// than a curve step add up instead of rounding away.
/// </summary>
public sealed class TerrainBrushes(TerrainEdit terrain)
{
    /// <summary>World units a Raise / Lower / Noise dab of strength 1 moves a corner at the centre.</summary>
    public const double StepPerDab = 160;

    private double[]? _fine;
    private object? _stroke;

    /// <summary>True while a stroke is open.</summary>
    public bool InStroke => _stroke != null;

    /// <summary>The corners (or, for Paint, the cells) a dab at world (x, DOS z) reaches, with their weights.</summary>
    public IEnumerable<(int Col, int Row, double Weight)> Footprint(TerrainBrush brush, double x, double z)
    {
        // Paint weighs cell centres, the sculpt tools corners.
        var centre = brush.Tool == BrushTool.Paint ? 0.5 : 0.0;
        var (fc, fr) = (x / TerrainEdit.CellSize - centre, terrain.Height - z / TerrainEdit.CellSize - centre);
        var reach = brush.Radius / TerrainEdit.CellSize;
        var (c0, c1) = (Math.Max((int)Math.Floor(fc - reach), 0), Math.Min((int)Math.Ceiling(fc + reach), terrain.Width - 1));
        var (r0, r1) = (Math.Max((int)Math.Floor(fr - reach), 0), Math.Min((int)Math.Ceiling(fr + reach), terrain.Height - 1));
        for (var row = r0; row <= r1; row++)
        {
            for (var col = c0; col <= c1; col++)
            {
                var d = Math.Sqrt((col - fc) * (col - fc) + (row - fr) * (row - fr)) * TerrainEdit.CellSize;
                if (brush.Weight(d) is var w and > 0)
                {
                    yield return (col, row, w);
                }
            }
        }
    }

    /// <summary>One dab at world (x, DOS z); opens a stroke when none is open.</summary>
    public void Dab(TerrainBrush brush, double x, double z)
    {
        _stroke ??= new object();
        var footprint = Footprint(brush, x, z).ToList();
        var diff = new TerrainDiff(terrain.Width);
        if (brush.Tool == BrushTool.Paint)
        {
            var paint = TerrainEdit.TileByte(brush.Tile, brush.Turn);
            foreach (var (col, row, _) in footprint)
            {
                diff.Add(WldLayer.Tile, col, row, terrain.Raw(WldLayer.Tile, col, row), paint);
            }

            terrain.Execute(diff, $"Paint tile {brush.Tile}", _stroke);
            return;
        }

        var fine = Fine();
        var targets = footprint.Select(f => (f.Col, f.Row, Value: Target(brush, fine, f.Col, f.Row, f.Weight))).ToList();
        foreach (var (col, row, value) in targets)
        {
            fine[row * terrain.Width + col] = Math.Clamp(value, 0, TerrainEdit.MaxHeight);
            diff.Add(WldLayer.Height, col, row, terrain.Raw(WldLayer.Height, col, row), terrain.HeightByte(col, row, fine[row * terrain.Width + col]));
        }

        terrain.Execute(diff, brush.Tool.ToString(), _stroke);
    }

    /// <summary>Ends the stroke: the next dab starts a new undo step.</summary>
    public void EndStroke()
    {
        _stroke = null;
        _fine = null;
        terrain.History.EndMerge();
    }

    // The corner's new fine height under one dab.
    private double Target(TerrainBrush brush, double[] fine, int col, int row, double weight)
    {
        var h = FineAt(fine, col, row);
        var s = Math.Clamp(brush.Strength, 0, 1) * weight;
        return brush.Tool switch
        {
            BrushTool.Raise => h + StepPerDab * s,
            BrushTool.Lower => h - StepPerDab * s,
            BrushTool.Flatten => h + (brush.FlattenHeight - h) * s,
            BrushTool.Smooth => h + (Neighbours(fine, col, row) - h) * s,
            BrushTool.Noise => h + StepPerDab * s * TerrainNoise.Value(brush.Seed, col / brush.NoiseCells, row / brush.NoiseCells),
            _ => h,
        };
    }

    private double Neighbours(double[] fine, int col, int row)
    {
        double sum = 0;
        var n = 0;
        foreach (var (dc, dr) in (ReadOnlySpan<(int, int)>)[(-1, 0), (1, 0), (0, -1), (0, 1)])
        {
            var (c, r) = (col + dc, row + dr);
            if (c < 0 || r < 0 || c >= terrain.Width || r >= terrain.Height)
            {
                continue;
            }

            sum += FineAt(fine, c, r);
            n++;
        }

        return n > 0 ? sum / n : FineAt(fine, col, row);
    }

    // The stroke's fine height of a corner: taken from the terrain when the corner's byte no longer matches
    // it (first touch, or changed by an undo or another edit since).
    private double FineAt(double[] fine, int col, int row)
    {
        var i = row * terrain.Width + col;
        var index = terrain.HeightIndex(col, row);
        if (double.IsNaN(fine[i]) || TerrainEdit.IndexFor(fine[i], index) != index)
        {
            fine[i] = terrain.CornerHeight(col, row);
        }

        return fine[i];
    }

    private double[] Fine()
    {
        if (_fine == null || _fine.Length != terrain.Width * terrain.Height)
        {
            _fine = new double[terrain.Width * terrain.Height];
            Array.Fill(_fine, double.NaN);
        }

        return _fine;
    }
}
