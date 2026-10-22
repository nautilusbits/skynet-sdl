using System.Numerics;
using System.Runtime.CompilerServices;
using SkyNet.Game.Campaign;
using SkyNet.Game.Tests.Behaviour;

namespace SkyNet.Game.Tests.Campaign;

/// <summary>The campaign through <see cref="GameSession"/> with real collision: mission 1 played
/// by a script to MISSION COMPLETE and into mission 2, the per-map state kept across a return,
/// death restarting the mission, pickups.</summary>
public class CampaignTests
{
    private static string Here([CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;

    private sealed class Recorder : ISessionListener
    {
        public readonly List<string> Seen = [];
        public void MapEntered(GameSession session) => Seen.Add($"map{session.MapNumber}");
        public void MissionStarted(Mission mission, SkyNet.Formats.Briefing? briefing, bool restart) =>
            Seen.Add($"mission{mission.Number}{(restart ? "r" : "")}{(briefing != null ? "+brief" : "")}");
        public void Objective(string text) => Seen.Add("objective");
        public void MissionComplete(Mission mission) => Seen.Add("complete");
        public void MissionFailed(Mission mission, bool died) => Seen.Add(died ? "died" : "failed");
        public void Status(string text) => Seen.Add($"status:{text}");
        public void RestartOffered(Mission? mission) => Seen.Add("offered");
    }

    [BehaviourDataFact]
    public void Mission1PlaysToTheNextMission()
    {
        var session = new GameSession(BehaviourData.Data);
        var seen = new Recorder();
        session.Listener = seen;
        var log = new List<string>();
        session.Logger = log.Add;
        var script = PlayScript.Load(Path.Combine(Here(), "mission1.play"));
        var failures = script.Run(session, log.Add);
        Assert.True(failures.Count == 0, string.Join("\n", failures) + "\n" + string.Join("\n", log));
        Assert.Equal(["mission1+brief", "map210", "map214", "map215", "map217", "complete", "mission2+brief", "map220"],
            seen.Seen.Where(s => s.StartsWith("map", StringComparison.Ordinal) || s.StartsWith("mission", StringComparison.Ordinal) || s == "complete"));
    }

    [BehaviourDataFact]
    public void ReturningToAMapKeepsItsState()
    {
        var session = new GameSession(BehaviourData.Data);
        session.StartMission(session.Table.ByNumber(1)!.Value);
        var first = session.Behaviour;
        Assert.True(session.EnterMap(214, 0));
        Assert.True(session.EnterMap(210, 0));
        Assert.Same(first, session.Behaviour);
        Assert.Equal(214, session.PreviousMap);
    }

    [BehaviourDataFact]
    public void DeathRestartsTheMissionWithItsKit()
    {
        var session = new GameSession(BehaviourData.Data);
        var seen = new Recorder();
        session.Listener = seen;
        session.StartMission(session.Table.ByNumber(1)!.Value);
        var ammo = session.Weapons.Inventory.Pool(0);
        session.EnterMap(214, 0);
        session.Weapons.Inventory.AddAmmo(0, -ammo);
        session.Soldier.Hurt(5000f);
        Tick(session, 0.1f);
        Assert.Equal(SessionPhase.Failed, session.Phase);
        Tick(session, GameSession.FailedHold + 0.1f);
        Assert.Equal(SessionPhase.Playing, session.Phase);
        Assert.Equal(210, session.MapNumber);
        Assert.Equal(ammo, session.Weapons.Inventory.Pool(0));
        Assert.Equal(Soldier.MaxHealth, session.Soldier.Health);
        Assert.Contains("died", seen.Seen);
        Assert.Contains("mission1r+brief", seen.Seen);
    }

    [BehaviourDataFact]
    public void WithoutAutoRestartAFailedMissionWaitsForTheRestart()
    {
        var session = new GameSession(BehaviourData.Data) { AutoRestart = false };
        var seen = new Recorder();
        session.Listener = seen;
        session.StartMission(session.Table.ByNumber(1)!.Value);
        session.EnterMap(214, 0);
        session.Soldier.Hurt(5000f);
        Tick(session, GameSession.FailedHold + 1f);
        Assert.Equal(SessionPhase.Failed, session.Phase);
        Assert.Equal(214, session.MapNumber);
        Assert.Equal(1, seen.Seen.Count(s => s == "offered"));
        Tick(session, 1f);
        Assert.Equal(1, seen.Seen.Count(s => s == "offered"));
        session.RestartMission();
        Assert.Equal(SessionPhase.Playing, session.Phase);
        Assert.Equal(210, session.MapNumber);
        Assert.Equal(Soldier.MaxHealth, session.Soldier.Health);
        Assert.Contains("mission1r+brief", seen.Seen);
    }

    [BehaviourDataFact]
    public void ARobotKillsTheSoldierAndTheMissionRestarts()
    {
        var session = new GameSession(BehaviourData.Data);
        var seen = new Recorder();
        session.Listener = seen;
        var log = new List<string>();
        session.Logger = log.Add;
        var failures = PlayScript.Load(Path.Combine(Here(), "death210.play")).Run(session, log.Add);
        Assert.True(failures.Count == 0, string.Join("\n", failures) + "\n" + string.Join("\n", log));
        Assert.Contains("died", seen.Seen);
        Assert.Contains("mission1r+brief", seen.Seen);
        Assert.Equal(Soldier.MaxHealth, session.Soldier.Health);
    }

    [BehaviourDataFact]
    public void KilledRobotsStayDeadOnTheirMapUntilARestart()
    {
        var session = new GameSession(BehaviourData.Data);
        session.StartMission(session.Table.ByNumber(1)!.Value);
        Assert.NotNull(session.Robots);
        var robot = session.Robots!.All.First(r => r.Alive && r.MarkerOffset >= 0 && !r.Indestructible && r.DormantDistance == 0f);
        var marker = robot.MarkerOffset;
        robot.TakeDamage(new SkyNet.Game.Weapons.Damage(1e6f, SkyNet.Game.Weapons.DamageKind.Direct, robot.Position, true));
        Assert.False(robot.Alive);
        Assert.Contains(marker, session.Dead);
        Assert.True(session.EnterMap(214, 0));
        Assert.True(session.EnterMap(210, 0));
        Assert.False(session.Robots!.All.Single(r => r.MarkerOffset == marker).Alive);
        session.RestartMission();
        Assert.True(session.Robots!.All.Single(r => r.MarkerOffset == marker).Alive);
    }

    [BehaviourDataFact]
    public void TheTorpedoRideLocksTheKeysAndEndsOnMap254()
    {
        var session = new GameSession(BehaviourData.Data);
        session.StartMission(session.Table.ByNumber(5)!.Value);
        Assert.True(session.EnterMap(TorpedoRide.From, 0));
        Assert.True(session.EnterMap(TorpedoRide.Map, 10));
        Assert.NotNull(session.Torpedo);
        var feet = session.Player.Position;
        Tick(session, 1f);
        Assert.Equal(TorpedoRide.Map, session.MapNumber);
        Assert.True(session.Torpedo!.Riding);
        Assert.True(Vector2.Distance(new(feet.X, feet.Z), new(session.Player.Position.X, session.Player.Position.Z)) < 1f);
        Tick(session, 3f);
        Assert.Equal(TorpedoRide.ToMap, session.MapNumber);
        Assert.Null(session.Torpedo);
    }

    [BehaviourDataFact]
    public void WalkingOverAnItemTakesIt()
    {
        var data = BehaviourData.Data;
        // The first map of the campaign range with an item on it.
        foreach (var n in Enumerable.Range(200, 400))
        {
            var map = data.LoadMap(n);
            var item = map?.Entities.FirstOrDefault(e => e.Variant == SkyNet.Formats.MapEntityVariant.Sprite && !e.IsMarker
                && e.MarkerType == -1 && PickupTable.Items.ContainsKey(e.SpriteIndex));
            if (map == null || item == null)
            {
                continue;
            }

            var session = new GameSession(data);
            var statuses = new Recorder();
            session.Listener = statuses;
            session.StartAt(n);
            session.Soldier.Health = 100f;
            var y = session.Wld != null ? (float)session.Wld.HeightAt(item.X, item.Z) : -item.Y - Level.IndoorSpriteLift;
            session.Player.Spawn(new Vector3(item.X, y, -item.Z), 0f);
            Tick(session, 1f / 60f);
            Assert.Contains(item.FileOffset, session.TakenPickups);
            Assert.Contains(statuses.Seen, s => s.StartsWith("status:", StringComparison.Ordinal));
            return;
        }

        Assert.Fail("no map with an item");
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
