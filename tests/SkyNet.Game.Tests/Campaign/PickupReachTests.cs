using System.Numerics;
using RetroEng.Loop;
using SkyNet.Game.Campaign;
using SkyNet.Game.Tests.Behaviour;
using SkyNet.Game.Weapons;

namespace SkyNet.Game.Tests.Campaign;

/// <summary>Issue #48: DOS takes an item within 80 units of the eye, true 3D (handler 0x11d970,
/// distance 0x14d775); the port took it 80 across and 160 up or down, so a soldier on a roof
/// collected the items under him.</summary>
public class PickupReachTests
{
    [BehaviourDataFact]
    public void AnItemUnderARoofStays()
    {
        var (s, offset, at) = Start();

        // On a roof 100 over the item: the eye 175 over its foot.
        Take(s, at + new Vector3(0f, 100f, 0f));
        Assert.DoesNotContain(offset, s.TakenPickups);
    }

    [BehaviourDataFact]
    public void AnItemBesideTheFeetIsTaken()
    {
        var (s, offset, at) = Start();

        Take(s, at + new Vector3(40f, 0f, 0f));
        Assert.Contains(offset, s.TakenPickups);
    }

    // Mission 1 begun; MAP.210's first item.
    private static (GameSession, int, Vector3) Start()
    {
        var s = new GameSession(BehaviourData.Data);
        s.StartMission(s.Table.ByNumber(1)!.Value);
        var (offset, at) = s.PickupsLeft.First();
        return (s, offset, at);
    }

    // The feet put down at the spot for one tick.
    private static void Take(GameSession s, Vector3 feet)
    {
        var keys = new SkyNet.Game.Player.PlayerActions(0, 1, 2, 3, 4, 5, 6);
        s.Player.Spawn(feet, 0f);
        s.Tick(default, keys, new WeaponInput(false, false, false), false);
    }
}
