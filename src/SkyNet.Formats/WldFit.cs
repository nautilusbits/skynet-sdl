namespace SkyNet.Formats;

/// <summary>A heightmap chosen for a map: its file suffix ("030") and how many ground points it carries.</summary>
public readonly record struct WldFitResult(string Suffix, int OnGround, int Points);

/// <summary>
/// The substitute heightmap for an outdoor map that ships without its own WLD (Future Shock's
/// MAP.040 and MAP.080). DOS opens <c>gamedata\wld.NNN</c> and, when it is missing, keeps whatever
/// heightmap is already in memory; which one that was cannot be known, so the map is asked: every
/// WLD is scored by how many of the map's ground points lie within <see cref="Step"/> of its ground,
/// and the best one wins if it carries at least <see cref="MinShare"/> of them (the Godot port's
/// LevelLoader._best_fit_wld). MAP.040 → WLD.030 (174 of 196 markers).
/// </summary>
public static class WldFit
{
    public const double Step = 60.0;
    public const double MinShare = 0.33;
    public const int MinPoints = 8;

    /// <summary>
    /// The points scored, as (x, height up, DOS z): every marker (variant 3 with a marker type) at
    /// −(y + 16), the marker's foot; with fewer than 8 markers, the map's meshes at −y are added.
    /// </summary>
    public static List<(double X, double Up, double Z)> GroundPoints(MapFile map)
    {
        var pts = new List<(double, double, double)>();
        foreach (var e in map.Entities)
            if ((e.Flags & 3) == 3 && e.MarkerType >= 0)
                pts.Add((e.X, -(double)(e.Y + 0x10), e.Z));
        if (pts.Count < MinPoints)
            foreach (var e in map.Entities)
                if ((e.Flags & 3) == 1)
                    pts.Add((e.X, -(double)e.Y, e.Z));
        return pts;
    }

    /// <summary>How many of <paramref name="points"/> lie within <see cref="Step"/> of the drawn ground.</summary>
    public static int Score(WldTerrain wld, IReadOnlyList<(double X, double Up, double Z)> points)
    {
        int on = 0;
        foreach (var (x, up, z) in points)
            if (Math.Abs(up - wld.HeightAt(x, z)) <= Step)
                on++;
        return on;
    }

    /// <summary>
    /// The best of <paramref name="candidates"/> (suffix, heightmap), taken in the order given (the
    /// first wins a tie); null when the map has fewer than 8 points or nothing reaches the share.
    /// </summary>
    public static WldFitResult? BestFit(MapFile map, IEnumerable<(string Suffix, WldTerrain Wld)> candidates)
    {
        var pts = GroundPoints(map);
        if (pts.Count < MinPoints)
            return null;
        string best = "";
        int bestOn = 0;
        foreach (var (suffix, wld) in candidates)
        {
            int on = Score(wld, pts);
            if (on > bestOn)
            {
                bestOn = on;
                best = suffix;
            }
        }
        return bestOn < pts.Count * MinShare ? null : new WldFitResult(best, bestOn, pts.Count);
    }
}
