using SkyNet.Formats;
using SkyNet.Game.Campaign;
using SkyNet.Game.Hud;
using SkyNet.Game.Player;
using SkyNet.Game.Tests.Behaviour;
using SkyNet.Game.Weapons;

namespace SkyNet.Game.Tests.Campaign;

/// <summary>FUN_00121fe3 (v1.01) ends a mission only after 0x68 clock ticks and once flag 0x80 of
/// [0x30a50] is clear: FUN_0012fa3d sets it with a new message, the typewriter FUN_0012fb4c clears
/// it at the text's end. The last objective's line is typed out before WELL DONE (issue #45).</summary>
public class EndWaitsMessageTests
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

    private sealed class Lines : ISessionListener
    {
        public string Last = "";
        public void Objective(string text) => Last = text;
    }

    /// <summary>Future Shock MAP.010: the HUMMERTK gate @0fe82 fires JWALL03 @08b2b, the last
    /// objective left ([M3], John Connor's 130-character line).</summary>
    [ShockDataFact]
    public void LastObjectiveLineIsTypedOut()
    {
        const int Car = 0x0fe82;
        var data = GameData.Open(GameDataLocator.OtherGameDir(BehaviourData.Dir!)!, BehaviourData.Dir)!;
        var session = new GameSession(data);
        var lines = new Lines();
        session.Listener = lines;
        session.StartAt(10);
        foreach (var other in new[] { 0, 1, 3 })
        {
            session.Progress.Take(other);
        }

        Assert.Equal(1, session.Progress.Left);

        session.Behaviour!.Flip(Car);
        var seconds = 0f;
        while (session.Phase == SessionPhase.Playing && seconds < 30f)
        {
            session.Tick(default, new PlayerActions(0, 1, 2, 3, 4, 5, 6), new WeaponInput(false, false, false), false);
            seconds += GameSession.Dt;
        }

        Assert.Equal(SessionPhase.Complete, session.Phase);
        var typing = lines.Last.Length / MessageBox.CharsPerSecond;
        Assert.True(typing > GameSession.CompleteDelay, $"line too short to tell: {lines.Last} | " + string.Join(" / ", SkyNet.Game.Behaviour.MissionProgress.Load(data, 10).GetType().Name));
        Assert.True(seconds >= typing, $"ended after {seconds} s, the line types {typing} s");
    }
}
