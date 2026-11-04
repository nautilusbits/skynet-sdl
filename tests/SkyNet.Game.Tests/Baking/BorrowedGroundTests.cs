using SkyNet.Formats;
using SkyNet.Game.Baking;
using SkyNet.Game.Collision;
using SkyNet.Game.Tests.Behaviour;

namespace SkyNet.Game.Tests.Baking;

/// <summary>Future Shock's outdoor maps without a WLD of their own (MAP.029, 040, 080, 114, 171) stand
/// on the best-fit heightmap (<see cref="GameData.LoadWldFor"/>): baked ground and collision both.
/// Skipped without Future Shock's data beside SKYNET_DATA's.</summary>
public sealed class BorrowedGroundTests
{
    private static GameData? FutureShock() =>
        BehaviourData.Dir is { } dir && GameDataLocator.OtherGameDir(dir) is { } fs ? GameData.Open(fs, dir) : null;

    [Theory]
    [InlineData(29)]
    [InlineData(40)]
    [InlineData(80)]
    [InlineData(114)]
    [InlineData(171)]
    public void AMapWithoutItsOwnWldGetsGround(int number)
    {
        if (FutureShock() is not { } data)
        {
            return;
        }

        Assert.Null(data.LoadWld(number));
        Assert.NotNull(MapBaker.Bake(data, number)!.Terrain);
        Assert.NotNull(LevelCollision.Load(data, number)!.Value.Wld);
    }
}
