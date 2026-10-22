using RetroEng.Diagnostics;

/// <summary>The app's profiler zones (the game's are SkyNet.Game.DevTools.GameZones): the parts of a
/// rendered frame, the level's build after the session entered a map, saves and the game switch.
/// <code>
///   Render ─ Draw level ─ Draw robots ─ Draw cockpit ─ Draw weapon ─ Draw HUD ─ Draw menu ─ Draw overlay ─ Draw console ─ Gather lights ─ Present
///   Tick ─ Session ─ ... ─ Audio update ─ HUD tick
///   Map enter ─ ... ─ Level load ─ Level build ─ Robot meshes ─ Warm up ─ Upload flush ─ Music start
/// </code></summary>
internal static class AppZones
{
    public static readonly ZoneId Session = SkyNet.Game.DevTools.GameZones.Session;
    public static readonly ZoneId AudioUpdate = ZoneId.Of("Audio update");
    public static readonly ZoneId HudTick = ZoneId.Of("HUD tick");

    public static readonly ZoneId DrawLevel = ZoneId.Of("Draw level");
    public static readonly ZoneId DrawRobots = ZoneId.Of("Draw robots");
    public static readonly ZoneId DrawCockpit = ZoneId.Of("Draw cockpit");
    public static readonly ZoneId DrawWeapon = ZoneId.Of("Draw weapon");
    public static readonly ZoneId DrawHud = ZoneId.Of("Draw HUD");
    public static readonly ZoneId DrawMenu = ZoneId.Of("Draw menu");
    public static readonly ZoneId DrawOverlay = ZoneId.Of("Draw overlay");
    public static readonly ZoneId DrawConsole = ZoneId.Of("Draw console");
    public static readonly ZoneId GatherLights = ZoneId.Of("Gather lights");
    public static readonly ZoneId Present = ZoneId.Of("Present");

    public static readonly ZoneId LevelLoad = ZoneId.Of("Level load");
    public static readonly ZoneId LevelBuild = ZoneId.Of("Level build");
    public static readonly ZoneId RobotMeshes = ZoneId.Of("Robot meshes");
    public static readonly ZoneId WarmUp = ZoneId.Of("Warm up");
    public static readonly ZoneId UploadFlush = ZoneId.Of("Upload flush");
    public static readonly ZoneId MusicStart = ZoneId.Of("Music start");

    public static readonly ZoneId SaveGame = ZoneId.Of("Save game");
    public static readonly ZoneId LoadGame = ZoneId.Of("Load game");
    public static readonly ZoneId SwitchGame = ZoneId.Of("Switch game");
}
