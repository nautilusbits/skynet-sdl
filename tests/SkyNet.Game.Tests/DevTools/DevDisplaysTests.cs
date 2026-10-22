using System.Numerics;
using RetroEng.Commands;
using RetroEng.Diagnostics;
using RetroEng.Loop;
using SkyNet.Game.Campaign;
using SkyNet.Game.DevTools;
using SkyNet.Game.Player;
using SkyNet.Game.Tests.Behaviour;
using SkyNet.Game.Weapons;

namespace SkyNet.Game.Tests.DevTools;

/// <summary>The FPS readout's and the debug overlay's switches, keys and commands alike.</summary>
public class DevDisplaysTests
{
    [Fact]
    public void FpsAndDebugCommandsToggleAndSwitch()
    {
        var displays = new DevDisplays();
        var registry = new CommandRegistry();
        DevDisplays.Register(registry, displays);
        var changes = new List<(bool, bool)>();
        displays.Changed += d => changes.Add((d.Fps, d.Debug));

        Assert.Equal(["fps on"], registry.Run("fps"));
        Assert.Equal(["fps on"], registry.Run("fps on"));
        Assert.Equal(["fps off"], registry.Run("fps"));
        Assert.Equal(["debug overlay on"], registry.Run("debug on"));
        Assert.Equal(["debug overlay off"], registry.Run("overlay"));
        Assert.Equal(["fps on", "debug overlay on"], registry.Run("fps on; debug"));
        Assert.True(displays.Fps && displays.Debug);
        // "fps on" when it was on already changes nothing.
        Assert.Equal([(true, false), (false, false), (false, true), (false, false), (true, false), (true, true)], changes);
        Assert.Equal("Usage: fps [on|off]", registry.Run("fps maybe")[0]);
        Assert.Contains("on", registry.Complete("debug o").Matches);
    }

    [Fact]
    public void KeysToggleAndRestoreIsQuiet()
    {
        var displays = new DevDisplays();
        var changed = 0;
        displays.Changed += _ => changed++;
        displays.Restore(fps: true, debug: true);
        Assert.Equal((true, true, 0), (displays.Fps, displays.Debug, changed));
        displays.ToggleFps();
        displays.ToggleDebug();
        Assert.Equal((false, false, 2), (displays.Fps, displays.Debug, changed));
    }

    [Fact]
    public void FrameStatsCoverTheLastSecond()
    {
        var stats = new FrameStats();
        Assert.Equal(default, stats.Summary());
        for (var i = 0; i < 200; i++)
        {
            stats.Add(10f);
        }

        stats.Add(30f);
        var s = stats.Summary();
        // 100 frames fit in a second: the 30 ms one and 97 of the 10 ms ones.
        Assert.Equal(98, s.Frames);
        Assert.Equal(30f, s.MaxMilliseconds);
        Assert.Equal(1000f / s.AverageMilliseconds, s.Fps, 3);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 5000; i++)
        {
            stats.Add(16.7f);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}

/// <summary>The game's zones: free with no profiler, and where a session tick opens them.</summary>
[Collection(GameZonesCollection.Name)]
public class GameZonesTests
{
    [Fact]
    public void ZonesAllocateNothingWithoutABackend()
    {
        Assert.Null(Prof.Backend);
        static void Work()
        {
            var all = GameZones.All;
            for (var i = 0; i < all.Count; i++)
            {
                using var z = Prof.Zone(all[i]);
            }

            Prof.Plot("Draw calls", 1);
            Prof.Frame();
        }

        var zones = GameZones.All;
        Work();
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 10_000; i++)
        {
            Work();
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.Equal(zones.Count, zones.Select(z => z.Value).Distinct().Count());
    }

    [BehaviourDataFact]
    public void ASessionTickOpensItsParts()
    {
        var session = new GameSession(BehaviourData.Data);
        var recorder = new ThreadRecorder();
        Prof.SetBackend(recorder);
        try
        {
            session.StartAt(210);
            session.Tick(default, new PlayerActions(0, 1, 2, 3, 4, 5, 6), new WeaponInput(false, false, false), use: false);
        }
        finally
        {
            Prof.SetBackend(null);
        }

        var enter = recorder.Events.TakeWhile(e => e != "-Map enter").ToList();
        Assert.Equal(["+Map enter", "+Map parse", "-Map parse", "+Terrain", "-Terrain", "+Behaviour build"], enter.Take(6));
        Assert.Contains("+Collision build", enter);
        Assert.Contains("+Robots place", enter);
        var tick = recorder.Events.SkipWhile(e => e != "-Map enter").Skip(1).Where(e => e.StartsWith('+')).ToList();
        Assert.Equal(["+Player", "+Weapons", "+Projectiles", "+Robots", "+Behaviour", "+Path vehicles", "+Pickups"], tick);
    }

    /// <summary>Records the zones of the thread that made it (other tests' sessions run in parallel).</summary>
    private sealed class ThreadRecorder : IProfilerBackend
    {
        private readonly int _thread = Environment.CurrentManagedThreadId;

        public List<string> Events { get; } = [];

        public void ZoneBegin(ZoneId zone) => Add($"+{zone.Name}");

        public void ZoneEnd(ZoneId zone) => Add($"-{zone.Name}");

        public void Frame(string? name)
        {
        }

        public void Plot(string name, double value)
        {
        }

        public void Message(string text)
        {
        }

        public void ThreadName(string name)
        {
        }

        private void Add(string e)
        {
            if (Environment.CurrentManagedThreadId == _thread)
            {
                Events.Add(e);
            }
        }
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class GameZonesCollection
{
    public const string Name = "Prof";
}

/// <summary>The pointer on the real maps (the game data).</summary>
public class ObjectivePointerTests
{
    [BehaviourDataFact]
    public void Mission1PointsThroughTheDoorwaysToItsFirstObjective()
    {
        var session = new GameSession(BehaviourData.Data);
        session.StartAt(210);
        var report = new ObjectivePointer().Find(session);
        Assert.Equal((1, 3, 0, 3), (report.Mission, report.Total, report.Done, report.Left));
        Assert.Equal(3, report.Pending.Count);
        Assert.StartsWith("[M1] ", report.Pending[0], StringComparison.Ordinal);
        // Nothing raises an objective on MAP.210: the doorway towards the map that does.
        Assert.NotNull(report.Target);
        Assert.Matches(@"^exit @[0-9a-f]{5} -> MAP\.2\d\d \(towards MAP\.2\d\d\)$", report.Target.Text);
        Assert.StartsWith("next objective on MAP.2", report.Note, StringComparison.Ordinal);
        Assert.Equal(Vector3.Distance(session.Player.Position, report.Target.Position), report.Target.Distance, 1);
    }

    [BehaviourDataFact]
    public void Mission2PointsAtTheTriggerOnItsMap()
    {
        var session = new GameSession(BehaviourData.Data);
        session.StartAt(220);
        var report = new ObjectivePointer().Find(session);
        Assert.Equal(2, report.Mission);
        Assert.NotNull(report.Target);
        Assert.Matches(@"^\[M1\] @[0-9a-f]{5} act 26 <- ", report.Target.Text);
    }

    [BehaviourDataFact]
    public void NoMissionNoReport()
    {
        var session = new GameSession(BehaviourData.Data);
        Assert.Same(ObjectiveReport.None, new ObjectivePointer().Find(session));
    }
}
