using SkyNet.Formats;
using SkyNet.Game.Campaign;
using SkyNet.Game.Player;
using SkyNet.Game.Tests.Behaviour;
using SkyNet.Game.Weapons;

namespace SkyNet.Game.Tests.Campaign;

/// <summary>Issue #22: a hit the soldier lives through flashes the view red (SKYNET.EXE v1.01
/// FUN_0013d6e3, SHADE.003 rows); a killing hit does not.</summary>
public class HitFlashTests
{
    // DOS's step: 40 rows a second.
    private const float RowSeconds = 1f / 40f;

    private static List<int> Rows(HitFlash flash)
    {
        var rows = new List<int>();
        while (flash.Row is { } row)
        {
            rows.Add(row);
            flash.Tick(RowSeconds);
        }

        return rows;
    }

    [Fact]
    public void FirstFlashRunsAllRows()
    {
        var flash = new HitFlash();
        Assert.Null(flash.Row);

        flash.Start();
        Assert.Equal([0, 1, 2, 3, 4, 5, 6, 7], Rows(flash));
        Assert.False(flash.Active);
    }

    [Fact]
    public void LaterFlashesStartAtRowFour()
    {
        var flash = new HitFlash();
        flash.Start();
        Rows(flash);

        flash.Start();
        Assert.Equal([4, 5, 6, 7], Rows(flash));
    }

    [Fact]
    public void FadesOverTheDecodedTime()
    {
        var flash = new HitFlash();
        flash.Start();
        flash.Tick(0.17f);
        Assert.True(flash.Active);
        flash.Tick(0.01f);
        Assert.False(flash.Active);

        // 4.0 → past 7.0 at 40 rows a second: 75 ms.
        flash.Start();
        flash.Tick(0.07f);
        Assert.True(flash.Active);
        flash.Tick(0.01f);
        Assert.False(flash.Active);
    }

    [Fact]
    public void StrengthFallsWithTheRow()
    {
        var flash = new HitFlash();
        Assert.Equal(0f, flash.Strength);
        flash.Start();
        Assert.Equal(1f, flash.Strength);
        flash.Tick(7 * RowSeconds);
        Assert.Equal(0f, flash.Strength, 3);
    }

    [Fact]
    public void ResetStartsFromRowZeroAgain()
    {
        var flash = new HitFlash();
        flash.Start();
        Rows(flash);
        flash.Start();
        flash.Reset();
        Assert.Null(flash.Row);

        flash.Start();
        Assert.Equal(0, flash.Row);
    }

    [BehaviourDataFact]
    public void SurvivedHitFlashes()
    {
        var session = Started();
        session.Soldier.Hurt(10f);
        Assert.True(session.HitFlash.Active);
    }

    [BehaviourDataFact]
    public void SilentHitFlashesToo()
    {
        var session = Started();
        session.Soldier.Hurt(10f, silent: true);
        Assert.True(session.HitFlash.Active);
    }

    [BehaviourDataFact]
    public void KillingHitDoesNotFlash()
    {
        var session = Started();
        session.Soldier.Hurt(5000f);
        Assert.False(session.Soldier.Alive);
        Assert.False(session.HitFlash.Active);
    }

    [BehaviourDataFact]
    public void FlashEndsInTheSession()
    {
        var session = Started();
        session.Soldier.Hurt(10f);
        for (var t = 0f; t < 0.2f; t += GameSession.Dt)
        {
            session.Tick(default, new PlayerActions(0, 1, 2, 3, 4, 5, 6), new WeaponInput(false, false, false), false);
        }

        Assert.False(session.HitFlash.Active);
        Assert.True(session.Soldier.Alive);
    }

    [BehaviourDataFact]
    public void TableHasEightRows()
    {
        var table = HitFlash.LoadTable(BehaviourData.Data);
        Assert.NotNull(table);
        Assert.Equal(HitFlash.Rows * Palette.Colors, table.Length);
    }

    [BehaviourDataFact]
    public void FutureShockReadsSkyNetsTable()
    {
        if (GameDataLocator.OtherGameDir(BehaviourData.Dir!) is not { } dir)
        {
            return;
        }

        var shock = GameData.Open(dir, BehaviourData.Dir)!;
        Assert.Equal(HitFlash.LoadTable(BehaviourData.Data), HitFlash.LoadTable(shock));
    }

    private static GameSession Started()
    {
        var session = new GameSession(BehaviourData.Data);
        session.StartMission(session.Table.ByNumber(1)!.Value);
        Assert.False(session.HitFlash.Active);
        return session;
    }
}
