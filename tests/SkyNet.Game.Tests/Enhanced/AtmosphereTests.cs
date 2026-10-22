using System.Numerics;
using SkyNet.Game.Enhanced;

namespace SkyNet.Game.Tests.Enhanced;

public class AtmosphereTests
{
    private const int Map = 10;
    private const int Columns = 2;
    private const float Far = 3992f;
    /// <summary>MAP.010's ground (Future Shock): its middle and reach, roughly.</summary>
    private static readonly GroundSpan Ground = new(new Vector3(26000f, 600f, -37000f), 38000f);
    private static readonly Vector3 Eye = new(20316f, 827f, -38618f);
    /// <summary>A strafe: 400 units sideways.</summary>
    private static readonly Vector3 Strafe = new(-200f, 0f, 346f);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void HorizonColumnStaysPutWhenTheEyeMoves(int column)
    {
        var (here, _) = Atmosphere.ColumnFoot(Map, column, Columns, Far, Eye, Ground);
        var (there, _) = Atmosphere.ColumnFoot(Map, column, Columns, Far, Eye + Strafe, Ground);

        Assert.Equal(here, there);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void HorizonColumnStandsPastTheGroundAndTheFarClip(int column)
    {
        var (foot, _) = Atmosphere.ColumnFoot(Map, column, Columns, Far, Eye, Ground);
        var across = new Vector2(foot.X - Ground.Centre.X, foot.Z - Ground.Centre.Z).Length();

        Assert.True(across > Ground.Radius + Far, $"{across} from the ground's middle");
    }
}
