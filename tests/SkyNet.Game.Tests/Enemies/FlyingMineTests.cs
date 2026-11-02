using System.Numerics;
using SkyNet.Game.Collision;
using SkyNet.Game.Enemies;
using SkyNet.Game.Tests.Behaviour;

namespace SkyNet.Game.Tests.Enemies;

/// <summary>Flying mines (globes) indoors on the shipped maps (#23): with the soldier in sight on the
/// floor a few hundred units away, the mine comes for him and goes off.</summary>
public class FlyingMineTests
{
    private const int GlobeType = 1;
    private const float Dt = 1f / 60f, Budget = 15f;

    /// <summary>Each case once failed: MAP.282 rose into or through the roof (the climb aimed over
    /// the roof), MAP.213 circled him once kept under the ceiling (steered by its turn rate),
    /// MAP.214 turned away from the wall 40 units behind him (the look-ahead ran past him).</summary>
    [BehaviourDataTheory]
    [InlineData(282, 10, RobotAi.Classic, 1729f, 16f, -1931f)]
    [InlineData(282, 11, RobotAi.Improved, 1857f, 16f, -1867f)]
    [InlineData(213, 1, RobotAi.Classic, 1990f, -256f, -1723f)]
    [InlineData(214, 0, RobotAi.Improved, 4103.4873f, -319.51953f, -4776.5127f)]
    public void TheMineGoesOff(int mapNo, int globe, RobotAi ai, float x, float y, float z)
    {
        var data = BehaviourData.Data;
        var map = data.LoadMap(mapNo)!;
        var world = LevelCollision.Build(data, map, data.LoadWld(mapNo)).World;
        var robots = new Robots(SkyNet.Data.GameTables.Ais, new EnemyModels(data), world, seed: 7) { Ai = ai, HoldFire = true };
        robots.Populate(map);
        var mine = robots.All.Where(r => r.TypeIndex == GlobeType).ElementAt(globe);
        var feet = new Vector3(x, y, z);
        for (var t = 0; t < Budget / Dt && mine.Alive; t++)
        {
            robots.Tick(Dt, feet);
        }

        Assert.False(mine.Alive, $"still at {mine.Position}, the soldier at {feet}");
    }
}
