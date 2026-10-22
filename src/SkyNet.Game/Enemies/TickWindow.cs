using System.Numerics;

namespace SkyNet.Game.Enemies;

/// <summary>
/// DOS's actor sweep: which actors run their AI this frame. SKYNET.EXE v1.01 0x1297f0 (called once a
/// frame from the play loop) asks the collector 0x12984d for a list with <c>edx = 5</c> (0x12980f):
/// the always-active list first (0x129a23; only MAP.260's convoy markers join it), then every actor
/// record (flag 0x40) linked into the map-grid cells (1024 u, index = coordinate >> 10) from
/// player − 2 to player + 2 on X and on Z, clamped to the map, skipping hidden ones (0x2000), at most
/// 200. Each listed actor gets one AI run (0x129cb5). An actor outside the 5×5 cells is not run at
/// all: it keeps its state, pose, timers and alert as they were and resumes there when the window
/// reaches it again. Alerted or chasing robots, turrets and flyers follow the same rule; the only
/// exceptions are the convoy list and the one run each start-marker robot gets as the map is built
/// (0x12a439 → 0x129cb5).
/// </summary>
public static class TickWindow
{
    /// <summary>A map-grid cell, units.</summary>
    public const float Cell = 1024f;
    /// <summary>Cells either side of the player's cell (5 >> 1).</summary>
    public const int Reach = 2;

    /// <summary>The grid cell of a DOS coordinate (arithmetic shift by 10).</summary>
    public static int CellOf(float dos) => (int)MathF.Floor(dos / Cell);

    /// <summary>The cell column (X) of a port position.</summary>
    public static int CellX(Vector3 at) => CellOf(at.X);

    /// <summary>The cell row (DOS Z = −port Z) of a port position.</summary>
    public static int CellZ(Vector3 at) => CellOf(-at.Z);

    /// <summary>The actor at <paramref name="actor"/> is in the window round <paramref name="player"/>.</summary>
    public static bool Contains(Vector3 actor, Vector3 player) =>
        Math.Abs(CellX(actor) - CellX(player)) <= Reach && Math.Abs(CellZ(actor) - CellZ(player)) <= Reach;

    /// <summary>ROBOT AI IMPROVED's activation scale. DOS's cells reach 2 to 3 cells from the player
    /// (2.5 on average); IMPROVED also wakes an actor within 1.25 × 2.5 cells (3200 u) on each axis.</summary>
    public const float ImprovedScale = 1.25f;

    /// <summary>IMPROVED's reach on each axis, units.</summary>
    public const float ImprovedReach = (Reach + 0.5f) * Cell * ImprovedScale;

    /// <summary>IMPROVED's window: DOS's cells, or within <see cref="ImprovedReach"/> on both axes.</summary>
    public static bool ContainsImproved(Vector3 actor, Vector3 player) =>
        Contains(actor, player) || (MathF.Abs(actor.X - player.X) <= ImprovedReach && MathF.Abs(actor.Z - player.Z) <= ImprovedReach);
}
