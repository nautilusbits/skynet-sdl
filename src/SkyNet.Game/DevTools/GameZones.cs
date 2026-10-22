using RetroEng.Diagnostics;

namespace SkyNet.Game.DevTools;

/// <summary>
/// The game's profiler zones (<see cref="Prof.Zone(ZoneId)"/>), registered once so a zone costs no
/// lookup and no allocation; the debug overlay's zone times see them by these names.
/// <code>
///   Tick ─ Session ─ Player (Vehicle) ─ Weapons ─ Robots ─ Behaviour (Path vehicles) ─ Pickups ─ Exit
///   Map enter ─ Map parse ─ Terrain ─ Collision build ─ Robots place ─ Behaviour start ─ Pickups place ─ Level load
///   Music synth (its own thread)
/// </code>
/// The app adds its own (the draws, the present, the level's build stages, save and load).
/// </summary>
public static class GameZones
{
    // The session's tick.
    public static readonly ZoneId Session = ZoneId.Of("Session");
    public static readonly ZoneId Player = ZoneId.Of("Player");
    public static readonly ZoneId Vehicle = ZoneId.Of("Vehicle");
    public static readonly ZoneId Weapons = ZoneId.Of("Weapons");
    public static readonly ZoneId Projectiles = ZoneId.Of("Projectiles");
    public static readonly ZoneId Robots = ZoneId.Of("Robots");
    public static readonly ZoneId Behaviour = ZoneId.Of("Behaviour");
    public static readonly ZoneId PathVehicles = ZoneId.Of("Path vehicles");
    public static readonly ZoneId Pickups = ZoneId.Of("Pickups");
    public static readonly ZoneId Exit = ZoneId.Of("Exit");

    // Entering a map.
    public static readonly ZoneId MapEnter = ZoneId.Of("Map enter");
    public static readonly ZoneId MapParse = ZoneId.Of("Map parse");
    public static readonly ZoneId Terrain = ZoneId.Of("Terrain");
    public static readonly ZoneId BehaviourBuild = ZoneId.Of("Behaviour build");
    public static readonly ZoneId CollisionBuild = ZoneId.Of("Collision build");
    public static readonly ZoneId RobotsPlace = ZoneId.Of("Robots place");
    public static readonly ZoneId BehaviourStart = ZoneId.Of("Behaviour start");
    public static readonly ZoneId PickupsPlace = ZoneId.Of("Pickups place");

    // Saves.
    public static readonly ZoneId Capture = ZoneId.Of("Save capture");
    public static readonly ZoneId Restore = ZoneId.Of("Save restore");

    // The score's producer thread.
    public static readonly ZoneId MusicSynth = ZoneId.Of("Music synth");

    /// <summary>Every zone above (the tests touch each).</summary>
    public static IReadOnlyList<ZoneId> All { get; } =
    [
        Session, Player, Vehicle, Weapons, Projectiles, Robots, Behaviour, PathVehicles, Pickups, Exit,
        MapEnter, MapParse, Terrain, BehaviourBuild, CollisionBuild, RobotsPlace, BehaviourStart, PickupsPlace,
        Capture, Restore, MusicSynth,
    ];
}
