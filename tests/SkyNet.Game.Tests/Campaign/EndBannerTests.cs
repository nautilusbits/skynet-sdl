using SkyNet.Formats;
using SkyNet.Game.Campaign;
using SkyNet.Game.Hud;
using SkyNet.Game.Tests.Behaviour;

namespace SkyNet.Game.Tests.Campaign;

/// <summary>The mission's end as SKYNET.EXE v1.01 FUN_00121fe3 runs it: the game plays on 0x68 ticks of
/// its 70 Hz clock, then WELLDONE.IMG (or FAILED.IMG) stands over the frozen frame for 43 BIOS ticks,
/// then the next mission (or RESTART MISSION?).</summary>
public class EndBannerTests
{
    [Fact]
    public void TheTimingIsDos()
    {
        Assert.Equal(104f / 70f, GameSession.CompleteDelay, 4);
        Assert.Equal(43f * 65536f / 1193182f, GameSession.BannerHold, 4);
        Assert.Equal(GameSession.BannerHold, GameSession.CompleteHold);
        Assert.Equal(GameSession.BannerHold, GameSession.FailedHold);
    }

    [Theory]
    [InlineData(1280, 720)]
    [InlineData(1920, 1080)]
    [InlineData(640, 400)]
    public void TheBannerIsCentredOnTheViewCentre(int width, int height)
    {
        var at = EndBanner.Place(189, 18, false, width, height);
        var c = DosHud.ViewCentre(width, height);
        var n = at.Width / 189;
        Assert.Equal(Math.Max(1, (int)MathF.Round(height / 200f)), n);
        Assert.Equal(18 * n, at.Height);
        Assert.Equal((int)c.X - 94 * n, at.X);
        Assert.Equal((int)c.Y - 9 * n, at.Y);
    }

    [BehaviourDataFact]
    public void TheArtComesFromTheData()
    {
        var (path, variant) = BehaviourData.Data.FrontArchive("MDMDIMGS.BSA");
        using var bsa = BsaArchive.Open(path, variant);
        var done = ImgFile.Parse(bsa.Read(GameSession.CompleteImage)!)!;
        var failed = ImgFile.Parse(bsa.Read(GameSession.FailedImage)!)!;
        Assert.Equal((189, 18), (done.Width, done.Height));
        Assert.Equal((228, 18), (failed.Width, failed.Height));
        // Index 0 is the see-through colour the frozen frame shows through.
        Assert.Contains(done.Pixels.ToArray(), p => p == 0);
    }

    [BehaviourDataFact]
    public void WellDoneFollowsTheLastObjectiveThenTheNextMission()
    {
        var session = new GameSession(BehaviourData.Data);
        session.StartMission(session.Table.ByNumber(1)!.Value);
        Assert.Null(session.BannerImage);
        Assert.True(session.CompleteMission());
        Tick(session, GameSession.CompleteDelay - 0.1f);
        Assert.Equal(SessionPhase.Playing, session.Phase);
        Assert.Null(session.BannerImage);
        Tick(session, 0.2f);
        Assert.Equal(SessionPhase.Complete, session.Phase);
        Assert.Equal("WELLDONE.IMG", session.BannerImage);
        Tick(session, GameSession.CompleteHold - 0.2f);
        Assert.Equal(SessionPhase.Complete, session.Phase);
        Tick(session, 0.2f);
        Assert.Equal(SessionPhase.Playing, session.Phase);
        Assert.Equal(2, session.Mission?.Number);
        Assert.Null(session.BannerImage);
    }

    [BehaviourDataFact]
    public void DeathShowsFailedAtOnceThenRestarts()
    {
        var session = new GameSession(BehaviourData.Data);
        session.StartMission(session.Table.ByNumber(1)!.Value);
        session.Soldier.Hurt(5000f);
        Tick(session, GameSession.Dt);
        Assert.Equal(SessionPhase.Failed, session.Phase);
        Assert.Equal("FAILED.IMG", session.BannerImage);
        Tick(session, GameSession.FailedHold - 0.1f);
        Assert.Equal(SessionPhase.Failed, session.Phase);
        Tick(session, 0.2f);
        Assert.Equal(SessionPhase.Playing, session.Phase);
        Assert.Null(session.BannerImage);
    }

    private static void Tick(GameSession s, float seconds)
    {
        var keys = new SkyNet.Game.Player.PlayerActions(0, 1, 2, 3, 4, 5, 6);
        for (var t = 0f; t < seconds; t += GameSession.Dt)
        {
            s.Tick(default, keys, new SkyNet.Game.Weapons.WeaponInput(false, false, false), false);
        }
    }
}
