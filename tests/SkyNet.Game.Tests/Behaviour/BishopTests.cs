using System.Numerics;
using SkyNet.Formats;
using SkyNet.Game.Behaviour;
using SkyNet.Game.Campaign;
using SkyNet.Game.Weapons;

namespace SkyNet.Game.Tests.Behaviour;

/// <summary>Issue #59: Bishop (Future Shock MAP.151) is a billboard with state bit 0x02. A hit walks
/// his chain: his live picture jumps +512 along DOS Z (act 0x34), the dead one −512 into his place
/// (0x35), and the vat fires objective [M2] (act 0x27).</summary>
public class BishopTests
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

    private const int Map = 151, Alive = 0x9E7B, Dead = 0x10DDB;

    private static GameData Shock() =>
        GameData.Open(GameDataLocator.OtherGameDir(BehaviourData.Dir!)!, BehaviourData.Dir)!;

    [ShockDataFact]
    public void OneHitSwapsBishopAndCounts()
    {
        var data = Shock();
        var log = new EffectLog();
        var b = new LevelBehaviour(data.LoadMap(Map)!, BehaviourModel.LoadTransfrm(data), new MissionProgress(2), log);
        b.Start(Harness.Away, Harness.Away);
        Tick(b);
        log.Drain();

        b.Hit(Alive, 10f);
        Tick(b);

        Assert.Contains("obj1", log.Drain());
        Assert.Equal(new Vector3(0f, 0f, -512f), Shift(b, Alive));
        Assert.Equal(new Vector3(0f, 0f, 512f), Shift(b, Dead));
    }

    [ShockDataFact]
    public void BishopIsATargetThePoleIsNot()
    {
        var session = new GameSession(Shock());
        session.StartMission(session.Table.ByNumber(15)!.Value);

        Assert.True(session.EnterMap(Map, 0));
        var bishop = session.Weapons.Targets.All.Where(t => t.Radius > 0f)
            .Single(t => Vector3.Distance(t.Centre, new Vector3(33170f, -2816f, -37788f)) < 1f);
        Assert.Equal(143f / 2f, bishop.Radius);

        // MAP.329's pole @71EE: a proximity gate (0xEF) with state 3 that drops the rooms.
        Assert.True(session.EnterMap(329, 0));
        Assert.DoesNotContain(session.Weapons.Targets.All, t => t.Radius > 0f && Vector3.Distance(t.Centre, new Vector3(16633f, -144f, -22644f)) < 1f);
    }

    /// <summary>FUN_00124886 blasts variant-1 records only: a blast by Bishop leaves him standing,
    /// the direct hit still swaps him.</summary>
    [ShockDataFact]
    public void BlastSparesBishopHitSwapsHim()
    {
        var session = new GameSession(Shock());
        session.StartMission(session.Table.ByNumber(15)!.Value);
        Assert.True(session.EnterMap(Map, 0));
        var bishop = session.Weapons.Targets.All.Where(t => t.Radius > 0f)
            .Single(t => Vector3.Distance(t.Centre, new Vector3(33170f, -2816f, -37788f)) < 1f);

        bishop.TakeDamage(new Damage(50f, DamageKind.Blast, bishop.Centre, false));
        Tick(session.Behaviour!);
        Assert.Equal(Vector3.Zero, Shift(session.Behaviour!, Alive));

        bishop.TakeDamage(new Damage(50f, DamageKind.Direct, bishop.Centre, true));
        Tick(session.Behaviour!);
        Assert.Equal(new Vector3(0f, 0f, -512f), Shift(session.Behaviour!, Alive));
    }

    private static void Tick(LevelBehaviour b)
    {
        for (var i = 0; i < 35; i++)
        {
            b.Tick(Harness.Dt, new PlayerProbe(Harness.Away, Harness.Away));
        }
    }

    private static Vector3 Shift(LevelBehaviour b, int off) =>
        b.TryGetPose(off, out var p) ? p.Delta.Translation : Vector3.Zero;
}
