using System.Globalization;
using SkyNet.Data;
using SkyNet.Formats;

namespace SkyNet.Game.Campaign;

/// <summary>One row of DOS's statistics (16 bytes: shots fired, hits, enemies, kills).</summary>
public readonly record struct StatCounters(int Shots, int Hits, int Enemies, int Kills)
{
    public static StatCounters operator +(StatCounters a, StatCounters b) =>
        new(a.Shots + b.Shots, a.Hits + b.Hits, a.Enemies + b.Enemies, a.Kills + b.Kills);

    /// <summary>SHOTS HIT-RATIO: hits × 100 / shots, 0 with no shot, held to 0..100 (0x134ce9).</summary>
    public int HitRatio => Shots == 0 ? 0 : Math.Clamp((int)(Hits * 100L / Shots), 0, 100);

    /// <summary>ENEMIES DESTROYED: kills × 100 / enemies; 100 with no enemy, at least 1 once one
    /// is killed, at most 100 (0x134d7a).</summary>
    public int Destroyed => Enemies == 0 ? 100 : Math.Clamp((int)(Kills * 100L / Enemies), Kills > 0 ? 1 : 0, 100);
}

/// <summary>What the STATISTICS tab shows: this mission's row (null: STATS ARE NOT YET AVAILABLE)
/// and the career with that row already in it, as DOS adds it for the page.</summary>
public readonly record struct StatSheet(StatCounters? Mission, StatCounters Career);

/// <summary>The counters as a save keeps them (format 3): the mission's row (null before the
/// first mission's play began), its row index, the career before it, and the career when it began
/// (what RESTART MISSION goes back to).</summary>
public readonly record struct SavedStats(StatCounters? Mission, int Row, StatCounters Career, StatCounters CareerAtStart);

/// <summary>
/// DOS's mission statistics (SKYNET.EXE v1.01). The current row's pointer is DAT_000536d0 into a
/// table of 16-byte rows {shots, hits, enemies, kills}: SkyNET's 8 at 0x53520, Future Shock's 17
/// at 0x53400 (FUN_00134b95, flag 0x200000 of DAT_00030a50). The <c>enemies</c> field is static
/// data, the mission's enemy count (<see cref="EnemiesSkyNet"/>), never counted at run time; the
/// others are zeroed when a mission's play begins (FUN_00134b38, which first adds the previous
/// row to the career at 0x53510..0x5351c). Shots: +1 per trigger pull or throw (FUN_00134bb6 from
/// FUN_001261b1 / FUN_001263cc); hits: +1 per shot whose impact damages an object (FUN_00134bc5
/// from FUN_0012311f); kills: +1 per object destroyed (FUN_00134bd5 from FUN_00139819). A new game
/// clears the career and the row (FUN_00134b00); a mission's start snapshots the career (FUN_00134f77)
/// and RESTART MISSION puts it back (FUN_00134fa0). The page (FUN_00134be5) is drawn by
/// <see cref="Menu.MissionScreen"/>.
/// </summary>
public sealed class MissionStats
{
    /// <summary>Enemies per SkyNET mission (0x53520 +8, stride 16; data/tables mission_enemies).</summary>
    public static IReadOnlyList<int> EnemiesSkyNet => GameTables.MissionEnemies.SkyNet;
    /// <summary>Enemies per Future Shock mission (0x53400 +8, stride 16; data/tables mission_enemies).</summary>
    public static IReadOnlyList<int> EnemiesFutureShock => GameTables.MissionEnemies.FutureShock;

    private readonly IReadOnlyList<int> _enemies;
    private StatCounters _row;
    private bool _active;

    public MissionStats(GameKind game) =>
        _enemies = game == GameKind.FutureShock ? EnemiesFutureShock : EnemiesSkyNet;

    /// <summary>The career before the current mission (0x53510..0x5351c).</summary>
    public StatCounters Career { get; private set; }

    /// <summary>The career when the current mission began (0x535a0).</summary>
    public StatCounters CareerAtStart { get; private set; }

    /// <summary>The current mission's row, null before any mission's play began.</summary>
    public StatCounters? Mission => _active ? _row : null;

    /// <summary>The current row's index in the table, −1 none.</summary>
    public int Row { get; private set; } = -1;

    /// <summary>The page as it stands: the row and the career with the row in it.</summary>
    public StatSheet Sheet => new(Mission, Career + (Mission ?? default));

    /// <summary>A new game (FUN_00134b00): no career, no row.</summary>
    public void NewGame()
    {
        Career = CareerAtStart = default;
        _row = default;
        _active = false;
        Row = -1;
    }

    /// <summary>Play begins on the mission in table row <paramref name="row"/> (FUN_00134b38): the
    /// previous row goes into the career (a restart instead puts the career back to where the
    /// mission began), the new row starts at zero with the mission's enemy count.</summary>
    public void Begin(int row, bool restart)
    {
        if (restart)
        {
            Career = CareerAtStart;
        }
        else if (_active)
        {
            Career += _row;
        }

        CareerAtStart = Career;
        Row = row;
        _row = new StatCounters(0, 0, row >= 0 && row < _enemies.Count ? _enemies[row] : 0, 0);
        _active = row >= 0;
    }

    /// <summary>No mission (a loose map): no row; the career stays.</summary>
    public void Leave()
    {
        if (_active)
        {
            Career += _row;
        }

        CareerAtStart = Career;
        _active = false;
        _row = default;
        Row = -1;
    }

    public void Shot()
    {
        if (_active)
        {
            _row = _row with { Shots = _row.Shots + 1 };
        }
    }

    public void Hit()
    {
        if (_active)
        {
            _row = _row with { Hits = _row.Hits + 1 };
        }
    }

    public void Kill()
    {
        if (_active)
        {
            _row = _row with { Kills = _row.Kills + 1 };
        }
    }

    public SavedStats Save() => new(Mission, Row, Career, CareerAtStart);

    public void Restore(SavedStats s)
    {
        Career = s.Career;
        CareerAtStart = s.CareerAtStart;
        _active = s.Mission.HasValue;
        _row = s.Mission ?? default;
        Row = _active ? s.Row : -1;
    }

    // ---------------------------------------------------------------- the page

    /// <summary>The page's header (STRINGS.PRS Stat_Header, its #N the player's name,
    /// <see cref="Pilot.Expand"/>) or, with no row, Stat_NotAvail.</summary>
    public static string Header(StatSheet? sheet, PrsFile? strings, Pilot? pilot = null)
    {
        if (sheet?.Mission == null)
        {
            return strings?.Text("Stat_NotAvail", "STATS ARE NOT YET AVAILABLE.") ?? "STATS ARE NOT YET AVAILABLE.";
        }

        var text = strings?.Text("Stat_Header", "#N HERE ARE YOUR STATS:") ?? "#N HERE ARE YOUR STATS:";
        return (pilot ?? Pilot.None).Expand(text).Trim();
    }

    /// <summary>The four rows DOS prints (mission's two, then the career's two); none with no row.</summary>
    public static IReadOnlyList<(string Label, string Value)> Rows(StatSheet? sheet, PrsFile? strings)
    {
        if (sheet is not { Mission: { } m } s)
        {
            return [];
        }

        string L(string key, string fallback) => strings?.Text(key, fallback) ?? fallback;
        return
        [
            (L("Stat_Shots", "SHOTS HIT-RATIO:"), Percent(m.HitRatio)),
            (L("Stat_Enem", "ENEMIES DESTROYED:"), Percent(m.Destroyed)),
            (L("Stat_TShots", "HIT-RATIO TOTAL:"), Percent(s.Career.HitRatio)),
            (L("Stat_TEnem", "ENEMIES TOTAL:"), Percent(s.Career.Destroyed)),
        ];
    }

    /// <summary>Three digits, zero-filled, and '%' (FUN_00146b00 with 3 digits).</summary>
    public static string Percent(int value) => value.ToString("D3", CultureInfo.InvariantCulture) + "%";
}
