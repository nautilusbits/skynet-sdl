namespace SkyNet.Game;

/// <summary>
/// The scene views' depth range. The maps' vertices sit some 50 000 units from the origin, and
/// the original look's standard 0-1 depth comes from two float32 dot products (clip z and w)
/// that round apart by about 0.003 each: an error in distance that grows as distance × 50 000 ×
/// 2⁻²⁴ / near. At near 1 that is several units at 2000 away, and MAP.210's gate leaves, 10
/// units behind the wall they slide behind, flickered through it (#72). Near 4 makes it a
/// quarter; the eye never comes that close to a face (the soldier's radius is 22).
/// </summary>
public static class SceneDepth
{
    /// <summary>The near plane (world units).</summary>
    public const float Near = 4f;

    /// <summary>The far plane of the original look's standard depth (the enhanced look's is infinite).</summary>
    public const float FarPlane = 100000f;
}
