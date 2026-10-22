using System.Collections.Immutable;
using SkyNet.Data;

namespace SkyNet.Game.Hud;

/// <summary>
/// The "YOU ENTERED &lt;location&gt;" status line of a level start (SKYNET.EXE v1.01 FUN_00136768,
/// called once from the play loop FUN_001177f0 on every map entry, with AX = the map number
/// 0x38c10). The table at 0x53d00 holds rows of 0x26 bytes <c>{i16 map, char name[36]}</c>, ended by
/// a row whose map is ≤ 0; the first row matching the map gives the line: the text at 0x35d00
/// ("YOU ENTERED"), a space, the name, queued by FUN_0012f8f7 in colour 0xB3. A map with no row
/// shows nothing.
/// <para>The rows name Future Shock's maps (MAP.011 THE TIKI GRAND HOTEL … MAP.194) and the
/// generic city buildings both games' archives carry (MAP.340-599), plus MAP.957; no SkyNET
/// campaign map (2xx) has one. The table lives in the executable, so Future Shock played through
/// SKYNET.EXE shows it the same way.</para>
/// <para>FUN_00136768 skips the line while flag 0x200 of 0x30a50 is set (a game being restored:
/// FUN_0013f04a sets 0x620), but FUN_001177f0 masks the flags with 0xc16e0000 just before the
/// call, so the test never holds: the line comes on every entry, a return and a loaded game too.</para>
/// </summary>
public sealed class LocationNames
{
    public const int RowSize = 0x26, NameSize = 36, MaxRows = 512;

    /// <summary>The rows in table order (the first row of a map wins, as DOS's scan).</summary>
    public ImmutableArray<(int Map, string Name)> Rows { get; }

    /// <summary>The text before the name ("YOU ENTERED").</summary>
    public string Prefix { get; }

    public LocationNames(string prefix, IEnumerable<(int Map, string Name)> rows)
    {
        Prefix = prefix;
        Rows = [.. rows];
    }

    /// <summary>No line for any map.</summary>
    public static LocationNames Empty { get; } = new("", []);

    /// <summary>The location name of MAP.<paramref name="map"/>; null when the table has none.</summary>
    public string? NameOf(int map)
    {
        foreach (var (m, name) in Rows)
        {
            if (m == map)
            {
                return name;
            }
        }

        return null;
    }

    /// <summary>The status line DOS queues on entering MAP.<paramref name="map"/>; null for none.</summary>
    public string? EnteredLine(int map) =>
        Prefix.Length > 0 && NameOf(map) is { } name ? $"{Prefix} {name}" : null;

    /// <summary>The shipped table (data/tables location_names).</summary>
    public static LocationNames Load() =>
        GameTables.Locations is var t ? new LocationNames(t.Prefix, t.Rows.Select(r => (r.Map, r.Name))) : Empty;
}
