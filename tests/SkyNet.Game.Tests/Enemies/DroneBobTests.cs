using SkyNet.Formats;
using SkyNet.Game.Collision;
using SkyNet.Game.Enemies;
using SkyNet.Game.Tests.Behaviour;

namespace SkyNet.Game.Tests.Enemies;

/// <summary>Issue #47: a hover whose motion kind (dw27) is 1 bobs (v1.01 0x13cf19 off the hover's
/// 0x13cf00): the drone sinks up to ~45 under its place and rises back every 2 s.</summary>
public class DroneBobTests
{
    private const int DroneType = 2;
    private const float Dt = 1f / 60f;

    /// <summary>A fact skipped unless Future Shock's data sits beside SKYNET_DATA's.</summary>
    private sealed class ShockDataFactAttribute : FactAttribute
    {
        public ShockDataFactAttribute()
        {
            if (BehaviourData.Dir is not { } dir || GameDataLocator.OtherGameDir(dir) == null)
            {
                Skip = "no Future Shock data beside SKYNET_DATA's SkyNET folder";
            }
        }
    }

    [ShockDataFact]
    public void TheDroneBobs()
    {
        // Future Shock's MAP.010 (mission 1): SkyNET's maps hold no drone.
        var data = GameData.Open(GameDataLocator.OtherGameDir(BehaviourData.Dir!)!, BehaviourData.Dir)!;
        var number = 10;
        var map = data.LoadMap(number)!;
        var world = LevelCollision.Build(data, map, data.LoadWld(number)).World;
        var robots = new Robots(SkyNet.Data.GameTables.Ais, new EnemyModels(data), world, seed: 7) { HoldFire = true };
        robots.Populate(map);
        var drone = robots.All.First(r => r.TypeIndex == DroneType && r.Alive && r.DormantDistance == 0f);

        // Its drawn height against its place over one period, no soldier about.
        float low = 0f, high = 0f;
        for (var t = 0f; t < 2f; t += Dt)
        {
            drone.Tick(Dt, null);
            var dy = drone.Root.World.Translation.Y - drone.Position.Y;
            (low, high) = (MathF.Min(low, dy), MathF.Max(high, dy));
        }

        Assert.InRange(low, -48f, -40f);
        Assert.Equal(0f, high, 0.5f);
    }
}
