using SkyNet.Formats;

namespace SkyNet.Game.Campaign;

/// <summary>How the player moves on a mission (mission table +0x08: 0 foot, 4 jeep, 8 HK).</summary>
public enum PlayerMode { Foot = 0, Jeep = 4, Hk = 8 }

/// <summary>One row of the mission table: the mission's number, the map it starts on (only the
/// start map is in the table; every other map is reached through 0xF0 doorways) and the mode.</summary>
public readonly record struct Mission(int Number, int StartMap, PlayerMode Mode);

/// <summary>
/// The campaign: SKYNET.EXE v1.01's mission table at 0x34846 (Future Shock mode 0x341ce), 0x5C bytes
/// per row (docs/game-logic/objectives.md, the Godot port's MISSION_TABLE / MISSION_TABLE_SHOCK).
/// </summary>
public sealed class MissionTable
{
    public static readonly MissionTable SkyNet = new(
    [
        new(1, 210, PlayerMode.Foot), new(2, 220, PlayerMode.Jeep), new(3, 230, PlayerMode.Foot), new(4, 240, PlayerMode.Foot),
        new(5, 252, PlayerMode.Foot), new(6, 260, PlayerMode.Jeep), new(7, 270, PlayerMode.Hk), new(8, 280, PlayerMode.Foot),
    ]);

    public static readonly MissionTable FutureShock = new(
    [
        new(1, 10, PlayerMode.Foot), new(2, 20, PlayerMode.Jeep), new(3, 30, PlayerMode.Foot), new(5, 50, PlayerMode.Foot),
        new(6, 60, PlayerMode.Hk), new(7, 70, PlayerMode.Foot), new(9, 90, PlayerMode.Foot), new(10, 100, PlayerMode.Jeep),
        new(11, 110, PlayerMode.Hk), new(12, 120, PlayerMode.Hk), new(13, 130, PlayerMode.Foot), new(14, 140, PlayerMode.Foot),
        new(15, 150, PlayerMode.Foot), new(16, 160, PlayerMode.Hk), new(17, 170, PlayerMode.Jeep), new(18, 180, PlayerMode.Foot),
        new(19, 190, PlayerMode.Foot),
    ]);

    public MissionTable(IReadOnlyList<Mission> missions) => Missions = missions;

    public IReadOnlyList<Mission> Missions { get; }

    public static MissionTable For(GameKind game) => game == GameKind.FutureShock ? FutureShock : SkyNet;

    /// <summary>The missions a game can play: the table's, then the ones the mounted mods add
    /// (<see cref="Mods.ModMissions"/>), by number.</summary>
    public static MissionTable Playable(GameKind game) =>
        Mods.ModMissions.For(game) is { Count: > 0 } added ? new MissionTable([.. For(game).Missions, .. added]) : For(game);

    /// <summary>The mission numbered <paramref name="number"/>, null when the table has none.</summary>
    public Mission? ByNumber(int number) => Missions.Where(m => m.Number == number).Select(m => (Mission?)m).FirstOrDefault();

    /// <summary>The mission that starts on MAP.<paramref name="map"/>, null when it starts none.</summary>
    /// <summary>The mission's row in the table (DOS's statistics row index), −1 when not in it.</summary>
    public int IndexOf(Mission mission)
    {
        for (var i = 0; i < Missions.Count; i++)
        {
            if (Missions[i].Number == mission.Number)
            {
                return i;
            }
        }

        return -1;
    }

    public Mission? StartingOn(int map) => Missions.Where(m => m.StartMap == map).Select(m => (Mission?)m).FirstOrDefault();

    /// <summary>The mission a loose map is played in when nothing runs yet: the one starting on it,
    /// else the one whose start map shares its decade (MAP.214 → mission 1, MAP.250 → mission 5).
    /// A side area reached during a mission stays in that mission (the caller's rule).</summary>
    public Mission? ForMap(int map) =>
        StartingOn(map) ?? Missions.Where(m => m.StartMap / 10 == map / 10).Select(m => (Mission?)m).FirstOrDefault();

    /// <summary>The mission after <paramref name="mission"/> in table order; null at the campaign's end.</summary>
    public Mission? After(Mission mission)
    {
        for (var i = 0; i < Missions.Count - 1; i++)
        {
            if (Missions[i].Number == mission.Number)
            {
                return Missions[i + 1];
            }
        }

        return null;
    }
}
