using SkyNet.Formats;

namespace SkyNet.Game.Campaign;

/// <summary>
/// The ammo a won mission brings into the next one (SKYNET.EXE v1.01 FUN_00126605, called by the
/// campaign loop FUN_0011c900 with the next mission's row; never on a new game, a jump or a
/// restart). Per mission row a list of {pool, amount}, each capped at the pool's max: SkyNET's
/// at 0x443c4, Future Shock's at 0x4423c. Example: Future Shock's row 10 (MAP.130) brings the
/// two satchel charges its briefing asks for (issue #67).
/// </summary>
internal static class MissionGrants
{
    private static readonly (int Pool, int Amount)[] None = [];

    // SkyNET: missions 3, 4, 8 restock, 6 and 7 (the jeep, the HK) the vehicle rockets.
    private static readonly (int, int)[] SkyNetStock = [(0, 200), (4, 200), (6, 20), (5, 25), (8, 2), (9, 1)];
    private static readonly (int, int)[] SkyNetRockets = [(11, 40)];
    private static readonly (int, int)[][] SkyNet =
        [None, None, SkyNetStock, SkyNetStock, None, SkyNetRockets, SkyNetRockets, SkyNetStock];

    // Future Shock: a vehicle mission brings vehicle rockets, the others a restock.
    private static readonly (int, int)[] ShockFirst = [(0, 300), (4, 350), (1, 100)];
    private static readonly (int, int)[] ShockRockets = [(11, 50)];
    private static readonly (int, int)[] ShockStock = [(0, 300), (4, 300), (1, 50), (3, 1), (5, 25), (2, 10), (8, 1)];
    private static readonly (int, int)[][] FutureShock =
    [
        ShockFirst, ShockRockets, [(0, 300), (1, 50), (5, 25), (8, 1), (9, 1)], ShockStock, ShockRockets, ShockStock,
        [(0, 300), (4, 300), (1, 50), (3, 1), (2, 10)], ShockRockets, ShockRockets, ShockRockets,
        [.. ShockStock, (9, 2)], ShockStock, ShockStock, ShockRockets, ShockRockets, [.. ShockStock, (9, 1)], None,
    ];

    /// <summary>The grant on entering mission row <paramref name="row"/>; none past the DOS table (a mod's missions).</summary>
    public static IReadOnlyList<(int Pool, int Amount)> Of(GameKind game, int row)
    {
        var table = game == GameKind.FutureShock ? FutureShock : SkyNet;
        return row >= 0 && row < table.Length ? table[row] : None;
    }
}
