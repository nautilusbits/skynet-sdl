using System.Numerics;
using RetroEng.Audio;
using RetroEng.Loop;
using SkyNet.Game.Audio;
using SkyNet.Game.Campaign;
using SkyNet.Game.Player;
using SkyNet.Game.Tests.Behaviour;
using SkyNet.Game.Weapons;

namespace SkyNet.Game.Tests.Audio;

/// <summary>The soldier's steps, landings and grunts, the ambient loops and DOS's 3D level, as
/// SKYNET.EXE v1.01 plays them; the session runs headless on a mixer with no device and the
/// sound bank's <see cref="SoundBank.Trace"/> shows what played.</summary>
public class PlayerSoundsTests
{
    private const float Dt = 1f / 60f;

    [Fact]
    public void TheMapsSurfacePicksTheLandingAndTheSteps()
    {
        // Maptype 0 (MAP.210's valley, its catwalks too: DOS has one surface a map) is the ground,
        // 1 metal (MAP.214 and the bases), 2 sewer... in the music table's first word.
        Assert.Equal(PlayerSounds.Ground, PlayerSounds.SurfaceOf(0));
        Assert.Equal(PlayerSounds.Metal, PlayerSounds.SurfaceOf(1));
        Assert.Equal(PlayerSounds.Sewer, PlayerSounds.SurfaceOf(2));
        Assert.Equal(PlayerSounds.Wood, PlayerSounds.SurfaceOf(3));
        Assert.Equal(PlayerSounds.Wood, PlayerSounds.SurfaceOf(11));
        Assert.Equal(PlayerSounds.Ground, PlayerSounds.SurfaceOf(12));
        Assert.Equal(PlayerSounds.Ground, PlayerSounds.SurfaceOf(-1));
        Assert.Equal("jmpcon.raw", SoundIds.NameOf(PlayerSounds.Landing[PlayerSounds.Ground]));
        Assert.Equal("jmpmet.raw", SoundIds.NameOf(PlayerSounds.Landing[PlayerSounds.Metal]));
        Assert.Equal("jmpwod.raw", SoundIds.NameOf(PlayerSounds.Landing[PlayerSounds.Wood]));
        Assert.Equal("jmpsew.raw", SoundIds.NameOf(PlayerSounds.Landing[PlayerSounds.Sewer]));
        Assert.Equal("lfwmet.raw", SoundIds.NameOf(PlayerSounds.LeftFoot[PlayerSounds.Metal]));
        Assert.Equal("rfwgrv.raw", SoundIds.NameOf(PlayerSounds.RightFoot[PlayerSounds.Ground]));
        Assert.Equal("lfwsew.raw", SoundIds.NameOf(PlayerSounds.LeftFoot[PlayerSounds.Sewer]));
        Assert.Equal("leftwatr.wav", SoundIds.NameOf(PlayerSounds.LeftWade));
    }

    [Fact]
    public void StepsAlternateAtTheStridesPace()
    {
        var body = new PlayerSounds();
        body.Enter(1);
        var heard = new List<BodySound>();
        // Walking 250 u/s: 2500 phase a second, a foot every 1024 → 0.4096 s.
        for (var t = 0; t < 120; t++)
        {
            body.Tick(Dt, 250f, true, 0f, 0f, false, false, heard);
        }

        Assert.Equal(4, heard.Count);
        Assert.Equal([75, 74, 75, 74], heard.Select(h => h.Id));
        Assert.All(heard, h => Assert.Equal(SoundBank.DosOverlap.Restart, h.Overlap));

        // Sideways only, in the air, under water: no steps; wading: the water's.
        heard.Clear();
        for (var t = 0; t < 120; t++)
        {
            body.Tick(Dt, 0f, true, 0f, 0f, false, false, heard);
            body.Tick(Dt, 250f, false, -50f, 0f, false, false, heard);
            body.Tick(Dt, 250f, true, 0f, 0f, false, true, heard);
        }

        Assert.Empty(heard);
        for (var t = 0; t < 60; t++)
        {
            body.Tick(Dt, 400f, true, 0f, 0f, true, false, heard);
        }

        Assert.NotEmpty(heard);
        Assert.All(heard, h => Assert.Contains(h.Id, new[] { PlayerSounds.LeftWade, PlayerSounds.RightWade }));
    }

    [Fact]
    public void ALandingFasterThan100UnitsASecondSounds()
    {
        var body = new PlayerSounds();
        body.Enter(0);
        var heard = new List<BodySound>();
        body.Tick(Dt, 0f, true, 0f, 99f, false, false, heard);
        Assert.Empty(heard);
        body.Tick(Dt, 0f, true, 0f, PlayerController.JumpSpeed, false, false, heard);
        Assert.Equal([new BodySound(23, SoundBank.DosOverlap.Skip)], heard);
    }

    [Fact]
    public void PainGruntsFoldTheDrawIntoTheList()
    {
        Assert.Equal(18, PlayerSounds.PainGrunt(0));
        Assert.Equal(105, PlayerSounds.PainGrunt(3));
        // 4 → 1, 15 → 15 − 3·4 = 3.
        Assert.Equal(101, PlayerSounds.PainGrunt(4));
        Assert.Equal(105, PlayerSounds.PainGrunt(15));
        Assert.All(Enumerable.Range(0, 16), d => Assert.Contains(PlayerSounds.PainGrunt(d), PlayerSounds.PainGrunts));
    }

    [Fact]
    public void DosThreeDFadesInAStraightLineAndHalvesBehind()
    {
        Assert.Equal(1f, SoundBank.DosFade(100f));
        Assert.Equal(1f, SoundBank.DosFade(120f));
        // 127 − 880 × 0x1557 / 65536 ≈ 53.6 of 127.
        Assert.Equal((127f - 880f * 0x1557 / 65536f) / 127f, SoundBank.DosFade(1000f), 2);
        Assert.Equal(0f, SoundBank.DosFade(SoundBank.DosFar + 1f));
        Assert.InRange(SoundBank.DosFar, 1643f, 1644f);
        var ahead = -Vector3.UnitZ;
        Assert.Equal(1f, SoundBank.DosBehind(new Vector3(0, 0, -500), ahead));
        Assert.Equal(1f, SoundBank.DosBehind(new Vector3(500, 0, 0), ahead));
        Assert.Equal(0.5f, SoundBank.DosBehind(new Vector3(0, 0, 500), ahead), 3);
        Assert.Equal(1f, SoundBank.DosBehind(new Vector3(0, 0, 50), ahead));
    }

    [Fact]
    public void ADosLoopPlaysAtItsVolumeInsideTheFade()
    {
        var mixer = new Mixer();
        var clip = new byte[44100];
        Array.Fill(clip, (byte)255);
        var bank = new SoundBank(mixer, _ => clip);
        bank.SetListener(new Vector3(0, 0, 3000), -Vector3.UnitZ, Vector3.UnitY);
        var wind = bank.PlayDosLoop3D(SoundIds.Wind, Vector3.Zero)!;
        Assert.False(wind.Audible);
        bank.SetListener(new Vector3(0, 0, 1000), -Vector3.UnitZ, Vector3.UnitY);
        Assert.True(wind.Audible);
        mixer.Mix(new float[Mixer.BlockFrames * 2]);
        // wind.raw is 100 of 128; ahead of the listener, so only the fade.
        Assert.Equal(100f / 128f * SoundBank.DosFade(1000f), mixer.Loudness(wind.Voice), 2);
        bank.SetListener(new Vector3(0, 0, 1800), -Vector3.UnitZ, Vector3.UnitY);
        Assert.False(wind.Audible);
    }

    [Fact]
    public void Dos2DSkipsOrRestartsTheSameId()
    {
        var mixer = new Mixer();
        var bank = new SoundBank(mixer, _ => new byte[44100]);
        var trace = new List<SoundEvent>();
        bank.Trace = trace.Add;
        Assert.False(bank.PlayDos2D(23, SoundBank.DosOverlap.Skip).IsNone);
        Assert.True(bank.PlayDos2D(23, SoundBank.DosOverlap.Skip).IsNone);
        var first = bank.PlayDos2D(70, SoundBank.DosOverlap.Restart);
        var second = bank.PlayDos2D(70, SoundBank.DosOverlap.Restart);
        Assert.False(second.IsNone);
        mixer.Mix(new float[Mixer.BlockFrames * 2]);
        Assert.False(mixer.IsPlaying(first));
        Assert.Equal([true, false, true, true], trace.Select(e => e.Played));
        Assert.Equal(40f / 128f, trace[2].Gain, 3);
    }

    // ------------------------------------------------------------------ the session, with real data

    [BehaviourDataFact]
    public void Map210RunsItsWindMachineryAndFireLoops()
    {
        using var run = new Run();
        run.Session.StartMission(run.Session.Table.ByNumber(1)!.Value);
        Assert.Equal(210, run.Session.MapNumber);
        var loops = run.Trace.Where(e => e.Kind == SoundEventKind.LoopStart).ToList();
        // The census: 0xEE nodes 3 × wind 87, 2 × windmet 88, 5 × amtech1 64; 17 burning sprites (fire 63/83).
        Assert.Equal(3, loops.Count(e => e.Id == 87));
        Assert.Equal(2, loops.Count(e => e.Id == 88));
        Assert.Equal(5, loops.Count(e => e.Id == 64));
        Assert.Equal(17, loops.Count(e => e.Id is 63 or 83));
        Assert.Equal(27, run.Sounds.LoopCount);
        Assert.Equal(PlayerSounds.Ground, run.Session.Body.Surface);
    }

    [BehaviourDataFact]
    public void Map214IsMetal()
    {
        using var run = new Run();
        run.Session.StartMission(run.Session.Table.ByNumber(1)!.Value);
        Assert.True(run.Session.EnterMap(214, 0));
        Assert.Equal(PlayerSounds.Metal, run.Session.Body.Surface);
        Assert.Equal(6, run.Trace.Count(e => e.Kind == SoundEventKind.LoopStart && e.Id is 64 or 66 && e.Position!.Value.Y < 300f));
    }

    [BehaviourDataFact]
    public void AScriptedJumpLandsWithTheGroundsSoundAndAWalkSteps()
    {
        using var run = new Run();
        run.Session.StartMission(run.Session.Table.ByNumber(1)!.Value);
        run.Ticks(30, 0);                                   // settle on the floor
        Assert.True(run.Session.Player.OnFloor);
        run.Trace.Clear();
        run.Ticks(1, Bit(5));                               // Space for one tick
        run.Ticks(90, 0);
        var body = run.Trace.Where(e => e.Kind == SoundEventKind.Play2D).Select(e => e.Name).ToList();
        Assert.Equal(["jmpcon.raw"], body);
        Assert.Contains(run.Trace, e => e.Name == "jmpcon.raw" && e.Played && Math.Abs(e.Gain - 100f / 128f) < 1e-3f);

        run.Trace.Clear();
        run.Ticks(60, Bit(0));                              // walk forward a second
        var steps = run.Trace.Where(e => e.Kind == SoundEventKind.Play2D).Select(e => e.Name).ToList();
        Assert.InRange(steps.Count, 1, 3);
        Assert.All(steps, n => Assert.Contains(n, new[] { "lfwgrv.raw", "rfwgrv.raw" }));
    }

    [BehaviourDataFact]
    public void AHitGruntsAndTheWaterDoesNot()
    {
        using var run = new Run();
        run.Session.StartMission(run.Session.Table.ByNumber(1)!.Value);
        run.Trace.Clear();
        run.Session.Soldier.Hurt(10f, silent: true);
        Assert.Empty(run.Trace);
        run.Session.Soldier.Hurt(10f);
        var grunt = Assert.Single(run.Trace);
        Assert.Contains(grunt.Id, PlayerSounds.PainGrunts);
    }

    private static ulong Bit(int action) => TickInput.Bit(action);

    /// <summary>A session on SkyNET's data with a real sound bank on a device-less mixer.</summary>
    private sealed class Run : IDisposable
    {
        public readonly Mixer Mixer = new();
        public readonly SoundBank Sounds;
        public readonly GameSession Session;
        public readonly List<SoundEvent> Trace = [];
        private readonly PlayerActions _keys = new(0, 1, 2, 3, 4, 5, 6);

        public Run()
        {
            Sounds = SoundBank.Open(Mixer, BehaviourData.Data)!;
            Sounds.Trace = Trace.Add;
            Session = new GameSession(BehaviourData.Data, Sounds);
        }

        public void Ticks(int n, ulong down)
        {
            for (var i = 0; i < n; i++)
            {
                Session.Tick(new TickInput(down, 0, 0, 0, 0, 0, 0, 0), _keys, new WeaponInput(false, false, false), false);
                Sounds.SetListener(Session.Player.EyePosition, PlayerController.Direction(Session.Player.Yaw, Session.Player.Pitch), Vector3.UnitY);
            }
        }

        public void Dispose() => Sounds.Dispose();
    }
}
