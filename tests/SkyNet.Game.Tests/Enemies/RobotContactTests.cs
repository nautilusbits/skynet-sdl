using System.Numerics;
using RetroEng.Loop;
using SkyNet.Formats;
using SkyNet.Game.Campaign;
using SkyNet.Game.Enemies;
using SkyNet.Game.Player;
using SkyNet.Game.Tests.Behaviour;
using SkyNet.Game.Weapons;

namespace SkyNet.Game.Tests.Enemies;

/// <summary>Issue #64. SKYNET.EXE v1.01 builds every robot segment with a contact word of 4 in
/// its link record (0x129b5a); the soldier's move hurts him by it whenever its probe meets a
/// robot (0x12b7f1 → 0x122d2f): 4 points a DOS frame, through the armour, unscaled.</summary>
public class RobotContactTests
{
    private static readonly PlayerActions Keys = new(Forward: 0, Back: 1, Left: 2, Right: 3, Run: 4, Jump: 5, Crouch: 6);

    /// <summary>MAP.210 with the robots holding fire, no armour, a T-800 <paramref name="ahead"/>
    /// units in front of the soldier.</summary>
    private static GameSession Facing(float ahead)
    {
        var session = new GameSession(BehaviourData.Data);
        session.StartAt(210);
        session.Robots!.HoldFire = true;
        session.Soldier.Armour = 0f;
        var type = SkyNet.Data.GameTables.Ais.Types.First(t => t.Name == "t800pst").Index;
        var fwd = new Vector3(-MathF.Sin(session.Player.Yaw), 0f, -MathF.Cos(session.Player.Yaw));
        var robot = session.Robots.Add(type, session.Player.Position + fwd * ahead);
        robot.UpdateTransforms();
        return session;
    }

    private static void Run(GameSession session, float seconds, ulong down)
    {
        for (var t = 0f; t < seconds; t += GameSession.Dt)
        {
            session.Tick(new TickInput(down, 0, 0, 0, 0, 0, 0, 0), Keys, new WeaponInput(false, false, false), false);
        }
    }

    [BehaviourDataFact]
    public void WalkingIntoARobotHurts()
    {
        var session = Facing(60f);
        Run(session, 1f, TickInput.Bit(Keys.Forward));

        var lost = SkyNet.Game.Campaign.Soldier.MaxHealth - session.Soldier.Health;
        Assert.True(lost >= RobotColliders.ContactPoints * 10, $"lost {lost}");
        Assert.Equal(0f, lost % RobotColliders.ContactPoints);
    }

    [BehaviourDataFact]
    public void StandingClearOfARobotDoesNot()
    {
        var session = Facing(300f);
        Run(session, 1f, 0);

        Assert.Equal(SkyNet.Game.Campaign.Soldier.MaxHealth, session.Soldier.Health);
    }

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

    /// <summary>The issue's case: a flencer hangs in front of the soldier (it stops dw6 = 35 off
    /// his trail point lifted by its shape height); he steps into its arms.</summary>
    [ShockDataFact]
    public void AFlencersArmsHurt()
    {
        var data = GameData.Open(GameDataLocator.OtherGameDir(BehaviourData.Dir!)!, BehaviourData.Dir)!;
        var session = new GameSession(data);
        session.StartAt(70);
        session.Robots!.HoldFire = true;
        session.Soldier.Armour = 0f;
        var fwd = new Vector3(-MathF.Sin(session.Player.Yaw), 0f, -MathF.Cos(session.Player.Yaw));
        var flencer = session.Robots.Add(SkyNet.Data.GameTables.Ais.Types.First(t => t.Name == "flencer").Index,
            session.Player.Position + fwd * 200f + new Vector3(0f, 60f, 0f));
        Run(session, 0.2f, TickInput.Bit(Keys.Forward));       // a trail point the hover can take (0x13b7b6)
        Run(session, 3f, 0);
        Assert.Equal(SkyNet.Game.Campaign.Soldier.MaxHealth, session.Soldier.Health);

        var to = flencer.Position - session.Player.Position;
        session.Player.Yaw = MathF.Atan2(-to.X, -to.Z);
        Run(session, 0.5f, TickInput.Bit(Keys.Forward));

        Assert.True(session.Soldier.Health < SkyNet.Game.Campaign.Soldier.MaxHealth, $"{session.Soldier.Health}");
    }
}
