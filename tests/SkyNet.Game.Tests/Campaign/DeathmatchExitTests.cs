using SkyNet.Game.Campaign;
using SkyNet.Game.Enemies;
using SkyNet.Game.Tests.Behaviour;

namespace SkyNet.Game.Tests.Campaign;

/// <summary>Leaving a deathmatch: a campaign save loaded after one (QUIT → LOAD) and the end of the
/// network game both put the session back in campaign mode with its DIFFICULTY.</summary>
public class DeathmatchExitTests
{
    private static readonly DateTime When = new(2026, 10, 11, 12, 0, 0);

    private static int DeathmatchMap(GameSession s) =>
        new[] { 604, 605, 600, 601, 602, 603, 606, 607, 608, 609 }.First(s.StartDeathmatch);

    [BehaviourDataFact]
    public void ALoadAfterADeathmatchIsACampaignGame()
    {
        var a = new GameSession(BehaviourData.Data);
        a.StartMission(a.Table.ByNumber(1)!.Value);
        var bytes = SaveFile.Write(a.Capture(When));

        var b = new GameSession(BehaviourData.Data) { Difficulty = Difficulty.Low };
        DeathmatchMap(b);
        Assert.True(b.Deathmatch);
        Assert.Equal(1f, b.Weapons.DamageToEnemies);

        b.Load(SaveFile.Read(bytes));
        Assert.False(b.Deathmatch);
        Assert.Equal(Difficulty.Low.DamageToEnemy, b.Weapons.DamageToEnemies);
        Assert.Equal(Difficulty.Low.DamageToPlayer, b.Weapons.DamageToPlayer);
        Assert.NotNull(b.Robots);
        Assert.True(b.CanSave);
    }

    [BehaviourDataFact]
    public void EndingADeathmatchTakesDifficultyBack()
    {
        var s = new GameSession(BehaviourData.Data) { Difficulty = Difficulty.Low };
        DeathmatchMap(s);
        Assert.True(s.Deathmatch);
        s.EndDeathmatch();
        Assert.False(s.Deathmatch);
        Assert.Equal(Difficulty.Low.DamageToEnemy, s.Weapons.DamageToEnemies);
    }
}
