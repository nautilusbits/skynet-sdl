using System.Numerics;
using SkyNet.Formats;
using SkyNet.Game.Campaign;
using SkyNet.Game.Player;
using SkyNet.Game.Tests.Behaviour;
using SkyNet.Game.Weapons;

namespace SkyNet.Game.Tests.Campaign;

/// <summary>
/// Future Shock mission 5: the sewer (MAP.051) leads into the crater at MAP.050's marker set 12,
/// which floats over the walk-on pad SEWRENTR @0ac3c ([M4] "I'm in the crater"). The marker spot
/// clips the pipe mouth above; DOS never moves the soldier off a marker, he drops onto the pad.
/// Moved sideways, he missed the pad and the mission could never end.
/// </summary>
public class ArrivalPadTests
{
    private const int CraterMap = 50, SewerMap = 51, PadSet = 12, FsMission = 5;
    private const int Pad = 0x0ac3c;
    private static readonly Vector2 PadMarker = new(13440, -40503);

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

    private static GameData Shock() =>
        GameData.Open(GameDataLocator.OtherGameDir(BehaviourData.Dir!)!, BehaviourData.Dir)!;

    [ShockDataFact]
    public void SewerArrivalLandsOnCraterPad()
    {
        var s = new GameSession(Shock());
        s.StartMission(s.Table.ByNumber(FsMission)!.Value);
        Assert.True(s.EnterMap(SewerMap, -1));
        Assert.True(s.EnterMap(CraterMap, PadSet));
        var left = s.Progress.Left;

        var feet = s.Player.Position;
        Assert.True(Vector2.Distance(new Vector2(feet.X, feet.Z), PadMarker) < 1f, $"spawned at {feet}");

        // One second: the fall onto the pad fires it.
        var keys = new PlayerActions(0, 1, 2, 3, 4, 5, 6);
        for (var t = 0f; t < 1f; t += GameSession.Dt)
        {
            s.Tick(default, keys, new WeaponInput(false, false, false), false);
        }

        Assert.Equal(left - 1, s.Progress.Left);
    }
}
