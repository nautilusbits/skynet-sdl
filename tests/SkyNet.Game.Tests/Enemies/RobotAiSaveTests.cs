using System.Numerics;
using System.Runtime.CompilerServices;
using SkyNet.Formats;
using SkyNet.Game.Campaign;
using SkyNet.Game.Enemies;
using SkyNet.Game.Player;
using SkyNet.Game.Tests.Behaviour;
using SkyNet.Game.Weapons;
using Xunit.Abstractions;

namespace SkyNet.Game.Tests.Enemies;

/// <summary>#23's saves (Future Shock, medium): robots that see the soldier engage him in both ROBOT
/// AI settings. MAP.051: the Terminator fires; MAP.035: the homing orb goes off.</summary>
public class RobotAiSaveTests(ITestOutputHelper output)
{
    private const int TerminatorType = 35, OrbType = 1;
    private const string Map051 = "issue23-map051.sav", Map035 = "issue23-map035.sav";

    /// <summary>A fact skipped unless Future Shock's data sits beside SKYNET_DATA's.</summary>
    private sealed class ShockDataTheoryAttribute : TheoryAttribute
    {
        public ShockDataTheoryAttribute()
        {
            if (BehaviourData.Dir is not { } dir || GameDataLocator.OtherGameDir(dir) == null)
            {
                Skip = "no Future Shock data beside SKYNET_DATA's SkyNET folder";
            }
        }
    }

    private static string Here([CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;

    private static GameSession Loaded(string save, RobotAi ai)
    {
        var data = GameData.Open(GameDataLocator.OtherGameDir(BehaviourData.Dir!)!, BehaviourData.Dir)!;
        var s = new GameSession(data) { RobotAi = ai, Difficulty = Difficulty.Medium };
        s.Load(SaveFile.Read(File.ReadAllBytes(Path.Combine(Here(), "Saves", save))));
        s.RobotAi = ai;
        return s;
    }

    private static void Tick(GameSession s)
    {
        var keys = new PlayerActions(0, 1, 2, 3, 4, 5, 6);
        s.Tick(default, keys, new WeaponInput(false, false, false), false);
    }

    /// <summary>The Terminator in sight of the soldier, in range, opens fire within a few seconds
    /// (IMPROVED stood facing away, rocking on its follow heading, and never fired).</summary>
    [ShockDataTheory]
    [InlineData(RobotAi.Classic)]
    [InlineData(RobotAi.Improved)]
    public void TheTerminatorFires(RobotAi ai)
    {
        var s = Loaded(Map051, ai);
        s.Soldier.God = true;
        var t800 = s.Robots!.All.Where(r => r.TypeIndex == TerminatorType && r.Alive)
            .MinBy(r => Vector3.Distance(r.Position, s.Player.Position))!;
        var fired = t800.ShotsFired;
        for (var t = 0f; t < 6f && t800.ShotsFired == fired; t += GameSession.Dt)
        {
            Tick(s);
        }

        output.WriteLine($"{ai}: at {t800.Position} yaw {t800.Yaw:0.00}, soldier {s.Player.Position}, sees {t800.Sees}, shots {t800.ShotsFired - fired}, unstuck {t800.Unstuck}");
        Assert.True(t800.ShotsFired > fired, $"{ai}: never fired, at {t800.Position}");
    }

    /// <summary>The orb homing on the soldier goes off within a few seconds (it circled ~87 u out
    /// for 13 s). CLASSIC's orb never sights him from this spot (DOS's single ray): not asserted.</summary>
    [ShockDataTheory]
    [InlineData(RobotAi.Improved)]
    public void TheOrbGoesOff(RobotAi ai)
    {
        var s = Loaded(Map035, ai);
        s.Soldier.God = true;
        var orb = s.Robots!.All.Where(r => r.TypeIndex == OrbType && r.Alive)
            .MinBy(r => Vector3.Distance(r.Position, s.Player.Position))!;
        for (var t = 0f; t < 5f && orb.Alive; t += GameSession.Dt)
        {
            Tick(s);
        }

        output.WriteLine($"{ai}: orb at {orb.Position}, soldier {s.Player.Position}, sees {orb.Sees}, alive {orb.Alive}");
        Assert.False(orb.Alive, $"{ai}: orb still at {orb.Position}");
    }
}
