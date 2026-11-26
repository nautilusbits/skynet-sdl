using SkyNet.Formats;
using SkyNet.Game.Campaign;
using SkyNet.Game.Tests.Behaviour;

namespace SkyNet.Game.Tests.Campaign;

/// <summary>Issue #67: a won mission grants the next one's ammo (SKYNET.EXE v1.01 FUN_00126605,
/// lists per mission row at 0x443c4 SkyNET / 0x4423c Future Shock). Future Shock's mission 11
/// (MAP.130) comes with two satchel charges.</summary>
public class MissionGrantTests
{
    private const int Satchels = 9;
    private const int Bullets = 0;

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
    public void FutureShockMap130StartsWithTwoMoreSatchels()
    {
        var data = GameData.Open(GameDataLocator.OtherGameDir(BehaviourData.Dir!)!, BehaviourData.Dir)!;
        var session = new GameSession(data);
        session.StartMission(session.Table.ByNumber(12)!.Value);
        var before = session.Weapons.Inventory.Pool(Satchels);

        Win(session);

        Assert.Equal(130, session.MapNumber);
        Assert.Equal(before + 2, session.Weapons.Inventory.Pool(Satchels));
    }

    [BehaviourDataFact]
    public void SkyNetMission3GrantsBullets()
    {
        var session = new GameSession(BehaviourData.Data);
        session.StartMission(session.Table.ByNumber(2)!.Value);
        var before = session.Weapons.Inventory.Pool(Bullets);

        Win(session);

        Assert.Equal(3, session.Mission?.Number);
        Assert.Equal(before + 200, session.Weapons.Inventory.Pool(Bullets));
    }

    [BehaviourDataFact]
    public void ARestartGrantsNothing()
    {
        var session = new GameSession(BehaviourData.Data);
        session.StartMission(session.Table.ByNumber(3)!.Value);
        var before = session.Weapons.Inventory.Pool(Bullets);

        session.RestartMission();

        Assert.Equal(before, session.Weapons.Inventory.Pool(Bullets));
    }

    private static void Win(GameSession s)
    {
        Assert.True(s.CompleteMission());
        var keys = new SkyNet.Game.Player.PlayerActions(0, 1, 2, 3, 4, 5, 6);
        for (var t = 0f; t < GameSession.CompleteDelay + GameSession.CompleteHold + 0.5f; t += GameSession.Dt)
        {
            s.Tick(default, keys, new SkyNet.Game.Weapons.WeaponInput(false, false, false), false);
        }
    }
}
