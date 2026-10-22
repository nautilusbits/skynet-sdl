using RetroEng.World;
using SkyNet.Formats;
using SkyNet.Game.Campaign;

namespace SkyNet.Edit;

/// <summary>What File > Open opens: mission <see cref="Number"/> of <see cref="Game"/>, or with
/// <see cref="Arena"/> the deathmatch arena on MAP.<see cref="Number"/> (SkyNET's data).</summary>
public readonly record struct OpenTarget(GameKind Game, int Number, bool Arena = false);

/// <summary>
/// A deathmatch arena (a NETLEVEL.PRS level) opened like a mission: a one-instance world whose
/// instance plays the arena's MAP.NNN layer, so the editor's document, save and Place panel work on it
/// unchanged. Saved edits land in the mod's <c>maps/MAP.NNN.layer.json</c>, which the deathmatch host
/// reads through <see cref="GameData.LoadMap"/> (<see cref="SkyNet.Game.Net.DmArena.Load"/>).
/// </summary>
public static class ArenaWorld
{
    /// <summary>The mission number an arena's world carries (no campaign mission has it).</summary>
    public const int Number = 0;

    /// <summary>The arenas the host menu offers (NETLEVEL.PRS, or the built-in list without it), SkyNET's data only.</summary>
    public static IReadOnlyList<NetLevel> Levels(GameData data) => data.Game == GameKind.SkyNet ? NetLevelList.Load(data) : [];

    /// <summary>MAP.<paramref name="map"/> as a one-instance world.</summary>
    public static MissionFile Build(int map)
    {
        var id = MissionFile.DefaultId(map);
        var name = MissionFile.MapName(map);
        return MissionFile.Create(GameKind.SkyNet, Number, null, PlayerMode.Foot, null, null, new WorldStart(id, 0),
            [new WorldInstance(id, name, name, null, new Dictionary<string, string>())], []);
    }
}
