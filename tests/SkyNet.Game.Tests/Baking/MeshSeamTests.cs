using System.Numerics;
using SkyNet.Formats;
using SkyNet.Game.Baking;
using SkyNet.Game.Tests.Behaviour;

namespace SkyNet.Game.Tests.Baking;

/// <summary>Neighbouring map pieces miss each other by under a unit: Future Shock's sewer
/// corridors (MAP.035) end at ±59.12 where the next piece starts at ±59 (1/256 fixed point).
/// DOS's 320-wide picture hides the crack; a window-sized one drew it as a black line (#55).</summary>
public class MeshSeamTests
{
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

    // The joint of CORA403J and CORA4003 (z 19880 in the map's units).
    [ShockDataFact]
    public void ShockSewerJointMeets()
    {
        var data = GameData.Open(GameDataLocator.OtherGameDir(BehaviourData.Dir!)!, BehaviourData.Dir)!;
        var block = MapBaker.Bake(data, 35)!;

        var ring = block.World.SelectMany(b => b.Vertices).Select(v => v.Position)
            .Where(p => MathF.Abs(p.Z + 19880f) < 1f && p.X is > 17640f and < 17790f)
            .Distinct().ToList();
        Assert.NotEmpty(ring);
        var cracks = ring.SelectMany(a => ring.Where(b => a != b && Vector3.Distance(a, b) < 1f)).Count();
        Assert.Equal(0, cracks);
    }
}
