using System.Numerics;

namespace SkyNet.Game.Navigation;

/// <summary>The body a nav mesh is baked for: one mesh per agent type (soldier, jeep).
/// Lengths in world units; <paramref name="StepHeight"/> is the tallest ledge it walks up.</summary>
public sealed record NavAgent(float Radius, float Height, float StepHeight, float MaxSlopeDegrees);

/// <summary>How fine a nav mesh is baked: voxel width and height in world units, and the part of
/// the world it covers on X and Z (<paramref name="AreaMin"/>-<paramref name="AreaMax"/>; null: all).
/// An outdoor map is a 65 536 u square; an arena plays in a tenth of it.</summary>
public sealed record NavBuild(float CellSize, float CellHeight, Vector2? AreaMin = null, Vector2? AreaMax = null)
{
    /// <summary>Only the tiles within <see cref="Reach"/> of one of these (x, z) are baked (the robots'
    /// starts: a whole outdoor map takes seconds); null: every tile of the area.</summary>
    public IReadOnlyList<Vector2>? Spots { get; init; }
    public float Reach { get; init; }

    /// <summary>These cells over the tiles within <paramref name="reach"/> of <paramref name="spots"/>
    /// (the area is their box and the reach).</summary>
    public NavBuild Around(IReadOnlyList<Vector2> spots, float reach)
    {
        var lo = new Vector2(spots.Min(p => p.X), spots.Min(p => p.Y)) - new Vector2(reach);
        var hi = new Vector2(spots.Max(p => p.X), spots.Max(p => p.Y)) + new Vector2(reach);
        return this with { AreaMin = lo, AreaMax = hi, Spots = spots, Reach = reach };
    }

    // Spots by value: a cached mesh is fresh only for the same spots.
    public bool Equals(NavBuild? other) =>
        other is not null && CellSize == other.CellSize && CellHeight == other.CellHeight && AreaMin == other.AreaMin
        && AreaMax == other.AreaMax && Reach == other.Reach
        && (Spots ?? []).SequenceEqual(other.Spots ?? []) && (Spots == null) == (other.Spots == null);

    public override int GetHashCode() => HashCode.Combine(CellSize, CellHeight, AreaMin, AreaMax, Reach, Spots?.Count ?? -1);
}

/// <summary>What <see cref="NavQuery.FindPath"/> found.</summary>
public enum PathResult
{
    /// <summary>No mesh under either end.</summary>
    None,
    /// <summary>The goal is cut off: the corners lead to the nearest reachable point.</summary>
    Partial,
    Found,
}
