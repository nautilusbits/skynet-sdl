using System.Numerics;
using SkyNet.Game.Collision;
using SkyNet.Game.Enemies;
using SkyNet.Game.Tests.Behaviour;

namespace SkyNet.Game.Tests.Enemies;

/// <summary><see cref="Enemy.SurfaceAt"/> gathers its column once instead of casting the engine's ray;
/// the answer must stay that ray's, bit for bit (CLASSIC flyers and DosParityTests hang on it).</summary>
public class SurfaceAtTests
{
    [BehaviourDataFact]
    public void TheSurfaceIsTheLongRaysToTheBit()
    {
        var data = BehaviourData.Data;
        var map = data.LoadMap(210)!;
        var world = LevelCollision.Build(data, map, data.LoadWld(210)).World;
        var robots = new Robots(SkyNet.Data.GameTables.Ais, new EnemyModels(data), world, seed: 7);
        robots.Populate(map);
        var robot = robots.All[0];
        var centre = robot.Position;

        // A grid round the first robot, at heights from under the floor to over the roofs.
        var hits = 0;
        for (var x = -3000f; x <= 3000f; x += 137f)
        {
            for (var z = -3000f; z <= 3000f; z += 137f)
            {
                foreach (var y in new[] { -500f, 0f, 300f, 2000f })
                {
                    var at = centre + new Vector3(x, y, z);
                    var hit = world.Raycast(at + new Vector3(0f, 6000f, 0f), -Vector3.UnitY, 26000f, out var h);
                    var want = hit ? h.Point.Y : at.Y;
                    hits += hit ? 1 : 0;
                    Assert.Equal(BitConverter.SingleToInt32Bits(want), BitConverter.SingleToInt32Bits(robot.SurfaceAt(at)));
                }
            }
        }

        Assert.True(hits > 1000, $"{hits} hits: the grid missed the level");
    }
}
