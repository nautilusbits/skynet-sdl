using System.Numerics;
using SkyNet.Formats;
using SkyNet.Game.Campaign;
using SkyNet.Game.Player;
using SkyNet.Game.Tests.Behaviour;
using SkyNet.Game.Weapons;

namespace SkyNet.Game.Tests.Campaign;

/// <summary>Radiation (SKYNET.EXE v1.01 FUN_0013ba9a, dose FUN_0013b9e0): marker 4 sources hurt the
/// soldier every frame he stands near one, silently, so the red flash never ends (issue #49).</summary>
public class RadiationTests
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

    /// <summary>Stands the soldier's dose point (half the eye height up) on the map's first
    /// marker 4 for one second, without armour; returns the session.</summary>
    private static GameSession StandOnSource(GameData data, int map)
    {
        var session = new GameSession(data);
        session.StartAt(map);
        session.Soldier.Armour = 0f;                // armour takes radiation too (FUN_00122b52)
        var source = session.Map!.Entities.First(e => e.Variant == MapEntityVariant.Sprite && e.IsMarker && e.MarkerType == 4 && e.Param16 > 0);
        var at = new Vector3(source.X, -source.Y, -source.Z);
        for (var t = 0f; t < 1f; t += GameSession.Dt)
        {
            session.Player.Position = at - new Vector3(0f, session.Player.Eye / 2f, 0f);
            session.Player.Velocity = Vector3.Zero;
            session.Tick(default, new PlayerActions(0, 1, 2, 3, 4, 5, 6), new WeaponInput(false, false, false), false);
        }

        return session;
    }

    [BehaviourDataFact]
    public void SkyNetSourceHurts()
    {
        var session = StandOnSource(BehaviourData.Data, 235);
        Assert.True(session.Soldier.Health < Soldier.MaxHealth - 30f, $"{session.Soldier.Health}");
        Assert.True(session.HitFlash.Active);
    }

    /// <summary>Future Shock MAP.010's bomb craters are the same marker 4.</summary>
    [ShockDataFact]
    public void FutureShockCraterHurts()
    {
        var data = GameData.Open(GameDataLocator.OtherGameDir(BehaviourData.Dir!)!, BehaviourData.Dir)!;
        var session = StandOnSource(data, 10);
        Assert.True(session.Soldier.Health < Soldier.MaxHealth - 30f, $"{session.Soldier.Health}");
        Assert.True(session.HitFlash.Active);
    }
}
