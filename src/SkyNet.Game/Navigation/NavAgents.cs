using SkyNet.Game.Enemies;
using SkyNet.Game.Player;

namespace SkyNet.Game.Navigation;

/// <summary>The game's agent types and how fine each is baked.</summary>
public static class NavAgents
{
    /// <summary>The soldier's body (<see cref="PlayerController"/>): the bots on foot.</summary>
    public static readonly NavAgent Soldier = new(PlayerController.BodyRadius, PlayerController.BodyHeight,
        PlayerController.StepHeight, PlayerController.FootMaxSlopeDegrees);

    /// <summary>Half the soldier's radius across, a 20th of his step up.</summary>
    public static readonly NavBuild SoldierCells = new(8f, 4f);

    /// <summary>The jeep a bot drives (<see cref="Vehicles.VehicleController"/>): its hull's half
    /// width and a margin for its turns, a low step (a ledge stalls it on the slope it makes) and a gentle slope
    /// (uphill pulls back 1148·sin of it).</summary>
    internal static readonly NavAgent Jeep = new(80f, 120f, 16f, 35f);

    /// <summary>The jeep's cells: twice the soldier's across.</summary>
    internal static readonly NavBuild JeepCells = new(16f, 4f);

    /// <summary>The walking robots (ROBOT AI IMPROVED, <see cref="RobotNav"/>), grouped by body: the
    /// small ones (raptor, man tank), the terminators (a higher step, a steeper slope) and the big
    /// ones (t-rex legs, spider, heavy tank). Steps and slopes are <see cref="Enemy"/>'s; the radius is
    /// about where its shoulder probes run.</summary>
    public static readonly NavAgent Walker = new(16f, 60f, Enemy.StepOther, Degrees(Enemy.SlopeOther));
    public static readonly NavAgent Terminator = new(20f, 64f, Enemy.StepTerminator, Degrees(Enemy.SlopeTerminator));
    public static readonly NavAgent BigWalker = new(64f, 110f, Enemy.StepOther, Degrees(Enemy.SlopeOther));

    /// <summary>The big walkers' cells: twice the soldier's across (a quarter of the bake).</summary>
    public static readonly NavBuild BigCells = new(16f, 4f);

    private static float Degrees(float radians) => radians * 180f / MathF.PI;
}
