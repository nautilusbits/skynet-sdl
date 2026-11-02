using RetroEng.Audio;
using SkyNet.Formats;
using SkyNet.Game.Audio;
using SkyNet.Game.Campaign;
using SkyNet.Game.Tests.Behaviour;

namespace SkyNet.Game.Tests.Audio;

/// <summary>Issue #22: Future Shock's MDMDSFXS.BSA has grunt1 only. SKYNET.EXE plays Future Shock
/// with SkyNET's sounds, so the soldier's grunts come from SkyNET's archive when Future Shock's
/// lacks them.</summary>
public class ShockSoundsTests
{
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
    public void FutureShockHasEveryGrunt()
    {
        using var bank = SoundBank.Open(new Mixer(), Shock())!;
        Assert.All(PlayerSounds.PainGrunts, id => Assert.NotNull(bank.Get(id)));
        Assert.NotNull(bank.Get(PlayerSounds.DeathGrunt));
    }

    [ShockDataFact]
    public void FutureShockHitsGrunt()
    {
        var data = Shock();
        using var bank = SoundBank.Open(new Mixer(), data)!;
        var trace = new List<SoundEvent>();
        bank.Trace = trace.Add;
        var session = new GameSession(data, bank);
        session.StartMission(session.Table.ByNumber(1)!.Value);

        // Each hit draws another grunt: each is heard, unless that grunt is still sounding (DOS skips it).
        var heard = new HashSet<int>();
        for (var i = 0; i < 8; i++)
        {
            trace.Clear();
            session.Soldier.Hurt(1f);
            var grunt = Assert.Single(trace);
            Assert.True(grunt.Played || heard.Contains(grunt.Id), $"{grunt.Name} not played");
            heard.Add(grunt.Id);
        }
    }
}
