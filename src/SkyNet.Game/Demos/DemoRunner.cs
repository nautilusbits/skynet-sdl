using System.Diagnostics;
using RetroEng.Commands;
using RetroEng.Loop;
using SkyNet.Formats;
using SkyNet.Game.Campaign;
using SkyNet.Game.DevTools;
using SkyNet.Game.Enemies;
using SkyNet.Game.Vehicles;

namespace SkyNet.Game.Demos;

/// <summary>What a headless playback found: the ticks played, the first desync (null: none), the
/// final state's fingerprint, the time it took and the header's warnings.</summary>
public sealed record DemoRun(int Ticks, DemoDesync? Desync, ulong Fingerprint, double Seconds, IReadOnlyList<string> Warnings);

/// <summary>
/// A campaign session with no window, renderer or sound, wired as the app wires its own: the
/// vehicle rig (boarded on every map entry), the controls, the session commands, RESTART MISSION
/// held for the player. Demos are recorded and played on it by tests and tools, the same ticks the
/// app runs.
/// </summary>
public sealed class HeadlessCampaign : ISessionListener
{
    public HeadlessCampaign(GameData data)
    {
        Session = new GameSession(data) { AutoRestart = false, Listener = this };
        Vehicles = new VehicleRig(Session);
        Controls = CampaignControls.Create();
        Commands = new CommandRegistry();
        SessionCommands.Register(Commands, () => Session);
    }

    public GameSession Session { get; }
    public VehicleRig Vehicles { get; }
    public CampaignControls Controls { get; }
    public CommandRegistry Commands { get; }

    public void MapEntered(GameSession session) => Vehicles.MapEntered();

    /// <summary>A recording of this session from now: mission <paramref name="mission"/> started (on
    /// <paramref name="map"/>) with the kit and generator as they stand.</summary>
    public CampaignRecorder RecordMission(int mission, int? map, string path, DifficultyLevel difficulty = DifficultyLevel.Medium)
    {
        Session.Difficulty = Difficulty.Of(difficulty);
        var demo = CampaignDemo.Create(Session, Controls, difficulty, "", baked: false);
        CampaignDemo.SetMissionStart(demo, Session, mission, map);
        CampaignDemo.Begin(Session, demo);
        return Listen(new CampaignRecorder(demo, path));
    }

    /// <summary>A recording from <paramref name="save"/>, loaded into this (fresh) session.</summary>
    public CampaignRecorder RecordSave(SaveGame save, string path, DifficultyLevel difficulty = DifficultyLevel.Medium)
    {
        var demo = CampaignDemo.Create(Session, Controls, difficulty, "", baked: false);
        CampaignDemo.SetSaveStart(demo, save);
        CampaignDemo.Begin(Session, demo);
        return Listen(new CampaignRecorder(demo, path));
    }

    private CampaignRecorder Listen(CampaignRecorder recorder)
    {
        Commands.Executed += recorder.Command;
        return recorder;
    }

    /// <summary>One recorded tick.</summary>
    public void Tick(CampaignRecorder? recorder, in TickInput input)
    {
        recorder?.BeforeTick(LevelOf(Session.Difficulty), Session.RobotAi);
        Controls.Tick(Session, input);
        recorder?.AfterTick(Session, input);
    }

    /// <summary>The level of a difficulty's multipliers.</summary>
    public static DifficultyLevel LevelOf(Difficulty d) =>
        Difficulty.LevelOf(d);

    /// <summary>Plays <paramref name="demo"/> on a fresh headless session of <paramref name="data"/>
    /// as fast as it goes; stops at the first desync unless <paramref name="toTheEnd"/>.
    /// <paramref name="each"/> sees the session after every tick (analysis).</summary>
    public static DemoRun Play(GameData data, DemoData demo, bool toTheEnd = false, Action<int, GameSession>? each = null)
    {
        var warnings = new List<string>();
        if (CampaignDemo.Refusal(demo, data, "", baked: false, warnings) is { } why)
        {
            throw new DemoFormatException(why);
        }

        var clock = Stopwatch.StartNew();
        var game = new HeadlessCampaign(data);
        CampaignDemo.Begin(game.Session, demo);
        var playback = new CampaignPlayback(demo, game.Controls);
        while (!playback.Done)
        {
            var tick = playback.Cursor.Tick;
            var found = playback.Step(game.Session, text => CampaignDemo.Apply(game.Session, text, game.Commands));
            each?.Invoke(tick, game.Session);
            if (found != null && !toTheEnd)
            {
                break;
            }
        }

        return new DemoRun(playback.Cursor.Tick, playback.Desync, SessionHash.Fingerprint(game.Session), clock.Elapsed.TotalSeconds, warnings);
    }
}
