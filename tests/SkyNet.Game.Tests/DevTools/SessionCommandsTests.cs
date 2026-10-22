using System.Numerics;
using RetroEng.Commands;
using SkyNet.Game.Campaign;
using SkyNet.Game.DevTools;
using SkyNet.Game.Tests.Behaviour;
using SkyNet.Game.Weapons;

namespace SkyNet.Game.Tests.DevTools;

/// <summary>The console's SkyNET commands on a headless <see cref="GameSession"/> (no window, no GPU).</summary>
public class SessionCommandsTests
{
    private static (GameSession Session, CommandRegistry Registry, List<string> Started) Make()
    {
        var session = new GameSession(BehaviourData.Data);
        var registry = new CommandRegistry();
        var started = new List<string>();
        SessionCommands.Register(registry, () => session, () => started.Add($"MAP.{session.MapNumber:D3}"));
        return (session, registry, started);
    }

    private sealed class Lines : ISessionListener
    {
        public List<(string, byte)> Said { get; } = [];
        public void Status(string text, byte colour) => Said.Add((text, colour));
    }

    [BehaviourDataFact]
    public void MoonSaysOwAndFallsOnNightMapsOnly()
    {
        var (session, registry, _) = Make();
        var lines = new Lines();
        session.Listener = lines;
        registry.Run("map 210");
        Assert.True(session.Moon.Shown);
        Assert.StartsWith("moon: 1 hits", registry.Run("moon hit")[0], StringComparison.Ordinal);
        Assert.Equal([("OW!", (byte)8)], lines.Said);

        Assert.Contains("falling", registry.Run("moon fall")[0], StringComparison.Ordinal);
        for (var i = 0; i < 600 && session.Moon.Falling; i++)
        {
            session.Moon.Tick(GameSession.Dt);
        }

        Assert.Contains("down below the horizon", registry.Run("moon")[0], StringComparison.Ordinal);

        // The map's load puts it back; the dusk maps have the dome and no moon.
        registry.Run("map 210");
        Assert.Equal(0, session.Moon.Drop);
        registry.Run("map 250");
        Assert.False(session.Moon.Shown);
        Assert.StartsWith("No moon", registry.Run("moon hit")[0], StringComparison.Ordinal);
    }

    [BehaviourDataFact]
    public void ClearAutomapForgetsWhatWasSeen()
    {
        var (session, registry, _) = Make();
        registry.Run("map 210");
        session.Seen.Mark(0x2600);

        // DOS's line, its typo kept (SKYNET.EXE v1.01 0x612d1); its CHEAT.PRS code and id.
        Assert.Equal([SessionCommands.AutomapCleared], registry.Run("clearautomap"));
        Assert.Equal("Automap cleaered.", SessionCommands.AutomapCleared);
        Assert.Equal(0, session.Seen.Count);
        session.Seen.Mark(1);
        registry.Run("ckort");
        Assert.Equal(0, session.Seen.Count);

        // A map change forgets it too.
        session.Seen.Mark(1);
        registry.Run("map 214");
        Assert.Equal(0, session.Seen.Count);
    }

    [BehaviourDataFact]
    public void CommandsOfTheMapWaitForOne()
    {
        var (_, registry, _) = Make();
        Assert.Equal([SessionCommands.NotInMap], registry.Run("god"));
        Assert.Equal([SessionCommands.NotInMap], registry.Run("give all"));
    }

    [BehaviourDataFact]
    public void MapStartsTheMapInItsMission()
    {
        var (session, registry, started) = Make();
        Assert.Equal(["MAP.210, mission 1"], registry.Run("map 210"));
        Assert.Equal(210, session.MapNumber);
        Assert.Equal(1, session.Mission?.Number);
        Assert.Equal(["MAP.214, mission 1"], registry.Run("map MAP.214"));
        Assert.Equal(214, session.MapNumber);
        Assert.Equal(["MAP.210", "MAP.214"], started);
        Assert.StartsWith("No MAP.999", registry.Run("map 999")[0], StringComparison.Ordinal);
        Assert.Equal(214, session.MapNumber);
        Assert.Equal("Usage: map <NNN | MAP.NNN>", registry.Run("map x")[0]);
        Assert.Contains("210", registry.Complete("map 21").Matches);
    }

    [BehaviourDataFact]
    public void GodAndItsCheatCodeSwitchTheSoldier()
    {
        var (session, registry, _) = Make();
        registry.Run("map 210");
        Assert.Equal(["god on"], registry.Run("god"));
        Assert.True(session.Soldier.God);
        session.Soldier.Hurt(500f);
        Assert.Equal(Soldier.MaxHealth, session.Soldier.Health);
        Assert.Equal(["god off"], registry.Run("csej"));
        Assert.Equal(["god on", "god off"], registry.Run("willnotstop on; god off"));
        Assert.False(session.Soldier.God);
    }

    [BehaviourDataFact]
    public void GiveTakesWeaponsAndAmmo()
    {
        var (session, registry, _) = Make();
        registry.Run("map 210");
        var inventory = session.Weapons.Inventory;
        Assert.False(inventory.Owns(WeaponTable.SuperUzi));
        Assert.StartsWith("SUPER UZI", registry.Run("give super")[0], StringComparison.Ordinal);
        Assert.True(inventory.Owns(WeaponTable.SuperUzi));
        Assert.Equal(WeaponTable.SuperUzi, inventory.Primary);

        Assert.False(inventory.Owns(3));
        Assert.Equal(["MACHINE GUN"], registry.Run("give machine gun"));
        Assert.True(inventory.Owns(3));
        Assert.Equal(["PLASMA PISTOL"], registry.Run("give 9"));
        Assert.True(inventory.Owns(9));

        registry.Run("give all");
        Assert.All(Enumerable.Range(0, WeaponTable.SuperUzi + 1), r => Assert.True(inventory.Owns(r)));
        Assert.All(Enumerable.Range(0, inventory.Table.Pools.Length), p => Assert.Equal(inventory.Table.Pools[p].Max, inventory.Pool(p)));
        Assert.StartsWith("Unknown weapon", registry.Run("give banana")[0], StringComparison.Ordinal);
    }

    [BehaviourDataFact]
    public void HealthTeleportAndKillAll()
    {
        var (session, registry, _) = Make();
        registry.Run("map 210");
        Assert.Equal(["health 500 / 1000, armour 0 %"], registry.Run("health 500"));
        Assert.Equal(["health 1000 / 1000, armour 100 %"], registry.Run("surgery"));
        Assert.Equal("Usage: health [points]", registry.Run("health 5000")[^1]);

        registry.Run("tp 100 200 -300 90");
        Assert.Equal(new Vector3(100f, 200f, -300f), session.Player.Position);
        Assert.Equal(MathF.PI / 2f, session.Player.Yaw, 4);

        var robots = session.Robots!.Active.Count();
        Assert.True(robots > 0);
        Assert.Equal([$"Killed {robots} of {robots} robots"], registry.Run("killall"));
        Assert.Empty(session.Robots.Active);
    }
}
