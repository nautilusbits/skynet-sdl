using System.Numerics;
using RetroEng.Loop;
using SkyNet.Formats;
using SkyNet.Game.Campaign;
using SkyNet.Game.Demos;
using SkyNet.Game.Player;
using SkyNet.Game.Tests.Behaviour;

namespace SkyNet.Game.Tests.Demos;

/// <summary>Determinism and demos (docs/demos.md): the same start and the same input give the
/// same state hash every tick, on several maps with the robots fighting; a recorded run plays back
/// without a desync, also after the file round trip; a game saved and loaded twice continues the
/// same both times, its generator where the saved one stood.</summary>
public class DemoTests
{
    private const int Ticks = 3000;

    /// <summary>A soldier who walks, runs, turns, jumps, fires in bursts, uses and throws: input
    /// that depends only on the tick (the same both runs).</summary>
    internal static TickInput Input(CampaignControls c, int t, TickInput previous)
    {
        ulong down = 0;
        if (t % 400 < 300)
        {
            down |= TickInput.Bit(c.Forward);
        }

        if (t % 900 > 600)
        {
            down |= TickInput.Bit(c.Run);
        }

        if (t % 240 is > 160 and < 200)
        {
            down |= TickInput.Bit(c.Left);
        }

        if (t % 150 < 60)
        {
            down |= TickInput.Bit(c.Fire);
        }

        if (t % 333 == 7)
        {
            down |= TickInput.Bit(c.Jump);
        }

        if (t % 500 == 250)
        {
            down |= TickInput.Bit(c.Use);
        }

        if (t % 700 == 350)
        {
            down |= TickInput.Bit(c.Throw);
        }

        var pressed = down & ~previous.Down;
        var released = previous.Down & ~down;
        var look = MathF.Sin(t / 47f) * 6f;
        var pitch = MathF.Sin(t / 113f) * 1.5f;
        return new TickInput(down, pressed, released, look, pitch, 0f, 0f, 0f);
    }


    /// <summary>Puts the soldier 500 units from the nearest robot awake, facing it, through the
    /// console's tp (a recorded event), so the runs have a fight in them.</summary>
    internal static void Engage(HeadlessCampaign game)
    {
        var s = game.Session;
        var me = s.Player.Position;
        var robot = s.Robots?.All.Where(r => r.Alive && !r.Hidden && r.DormantDistance == 0f && !r.IsPassive)
            .OrderBy(r => Vector3.DistanceSquared(r.Position, me)).FirstOrDefault();
        if (robot == null)
        {
            return;
        }

        var away = me - robot.Position;
        away.Y = 0f;
        away = away.LengthSquared() > 1f ? Vector3.Normalize(away) : Vector3.UnitX;
        var at = robot.Position + away * 500f;
        at.Y = s.Wld is { } wld ? (float)wld.HeightAt(at.X, -at.Z) + 2f : robot.Position.Y;
        var best = Enumerable.Range(0, 360).MaxBy(d =>
            Vector3.Dot(PlayerController.Direction(d * MathF.PI / 180f, 0f), -away));
        game.Commands.Run(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"tp {at.X} {at.Y} {at.Z} {best}"));
    }

    internal static (DemoData Demo, List<ulong> Prints) Record(int map, int ticks, bool god)
    {
        var game = new HeadlessCampaign(BehaviourData.Data);
        var mission = game.Session.Table.ForMap(map) ?? throw new InvalidOperationException($"no mission for MAP.{map}");
        var rec = game.RecordMission(mission.Number, mission.StartMap == map ? null : map, "unused" + CampaignDemo.Extension);
        if (god)
        {
            // A console command as the player would type it: kept as an event.
            game.Commands.Run("god on");
            Engage(game);
        }

        var prints = new List<ulong>();
        var previous = default(TickInput);
        for (var t = 0; t < ticks; t++)
        {
            var input = Input(game.Controls, t, previous);
            game.Tick(rec, input);
            previous = input;
            prints.Add(SessionHash.Fingerprint(game.Session));
        }

        return (rec.Demo, prints);
    }

    [BehaviourDataTheory]
    [InlineData(210)]
    [InlineData(211)]
    [InlineData(220)]
    [InlineData(252)]
    public void TheSameStartAndInputGiveTheSameHashesEveryTick(int map)
    {
        var (a, printsA) = Record(map, Ticks, god: true);
        var (b, printsB) = Record(map, Ticks, god: true);
        Assert.Equal(a.Hashes.Count, b.Hashes.Count);
        var first = Enumerable.Range(0, printsA.Count).FirstOrDefault(i => printsA[i] != printsB[i], -1);
        Assert.True(first < 0, $"MAP.{map}: the runs part at tick {first}");
        Assert.Equal(a.Hashes, b.Hashes);
        Assert.Contains(a.Events, e => e.Text == "god on" && e.Tick == 0);
    }

    [BehaviourDataFact]
    public void TheRobotsFightInTheseRuns()
    {
        // The determinism runs are worth something only if robots move, shoot and die in them.
        var game = new HeadlessCampaign(BehaviourData.Data);
        var rec = game.RecordMission(1, null, "unused.sndemo");
        game.Commands.Run("god on");
        Engage(game);
        var previous = default(TickInput);
        var shots = 0;
        for (var t = 0; t < Ticks; t++)
        {
            var input = Input(game.Controls, t, previous);
            game.Tick(rec, input);
            previous = input;
            shots = Math.Max(shots, game.Session.Robots!.All.Sum(r => r.ShotsFired));
        }

        Assert.True(shots > 0, "no robot fired");
        Assert.True(game.Session.Stats.Sheet.Mission is { Shots: > 0 }, "the soldier never fired");
    }

    [BehaviourDataTheory]
    [InlineData(210)]
    [InlineData(220)]
    public void ARecordedRunPlaysBackWithoutADesync(int map)
    {
        var (demo, _) = Record(map, Ticks, god: true);
        var back = DemoFile.Read(DemoFile.Write(demo));
        var run = HeadlessCampaign.Play(BehaviourData.Data, back);
        Assert.True(run.Desync == null, run.Desync?.ToString());
        Assert.Equal(Ticks, run.Ticks);
        Assert.Equal(Ticks, back.Checks);
    }

    [BehaviourDataFact]
    public void PlaybackReportsTheFirstTickThatDiffers()
    {
        var (demo, _) = Record(210, 600, god: true);
        // The input of tick 300 changed after recording: the hashes part from there.
        var t = demo.Inputs[300];
        demo.Inputs[300] = t with { LookX = t.LookX + 40f };
        var run = HeadlessCampaign.Play(BehaviourData.Data, demo);
        Assert.NotNull(run.Desync);
        Assert.Equal(300, run.Desync!.Tick);
        Assert.Contains("player", run.Desync.Components);
    }

    [BehaviourDataFact]
    public void ASavedGameLoadsAndContinuesTheSameEveryTime()
    {
        // Mission 1 played a while (robots awake, shots flying), then saved.
        var a = new HeadlessCampaign(BehaviourData.Data);
        var rec = a.RecordMission(1, null, "unused.sndemo");
        a.Commands.Run("god on");
        Engage(a);
        var previous = default(TickInput);
        for (var t = 0; t < 900; t++)
        {
            var input = Input(a.Controls, t, previous);
            a.Tick(rec, input);
            previous = input;
        }

        Assert.True(a.Session.CanSave);
        var save = SaveFile.Read(SaveFile.Write(a.Session.Capture(new DateTime(2026, 10, 11))));
        Assert.Equal(a.Session.Random.State, save.RandomState);

        // Two fresh sessions load it and play the same input on: the same every tick.
        List<ulong> Continue(out DemoData demo)
        {
            var b = new HeadlessCampaign(BehaviourData.Data);
            var r = b.RecordSave(save, "unused.sndemo");
            Assert.Equal(save.RandomState, b.Session.Random.State);
            var prints = new List<ulong>();
            var last = default(TickInput);
            for (var t = 900; t < 900 + Ticks; t++)
            {
                var input = Input(b.Controls, t, last);
                b.Tick(r, input);
                last = input;
                prints.Add(SessionHash.Fingerprint(b.Session));
            }

            demo = r.Demo;
            return prints;
        }

        var first = Continue(out var demo1);
        var second = Continue(out _);
        Assert.Equal(first, second);

        // The embedded-save demo plays back on its own.
        var run = HeadlessCampaign.Play(BehaviourData.Data, DemoFile.Read(DemoFile.Write(demo1)));
        Assert.True(run.Desync == null, run.Desync?.ToString());
        Assert.Equal(Ticks, run.Ticks);
    }

    [BehaviourDataFact]
    public void ADemoOfTheOtherGameIsRefused()
    {
        var (demo, _) = Record(210, 10, god: false);
        demo.SetField(CampaignDemo.KeyGame, GameKind.FutureShock.ToString());
        Assert.Throws<DemoFormatException>(() => HeadlessCampaign.Play(BehaviourData.Data, demo));
    }
}
