using System.Collections.Immutable;
using System.Numerics;
using RetroEng.Content;
using RetroEng.Diagnostics;
using SkyNet.Data;

namespace SkyNet.Game.Enhanced;

/// <summary>
/// The night maps' skies in the enhanced look: the table <c>night_skies</c>, one row per map, a mod
/// rule (no shipped file: the rows below are the game's own, a mod's rows replace them by map). A
/// row of a decade's first map (210) covers the decade; a row of an exact map wins.
/// <code>
///   Defaults (NightLook.Defaults per night decade) ◄── tables/night_skies.json of each mod, by "map"
///        │
///        ▼
///   NightSkyTable.Current.For(map) ──► Level (stars, Milky Way, moon) ──► LookLighting (the clouds)
/// </code>
/// </summary>
public sealed class NightSkyTable
{
    /// <summary>The campaign's night decades (missions 1-4).</summary>
    private static readonly int[] NightMaps = [210, 220, 230, 240];

    private sealed record Loaded(VirtualFileSystem? Mods, NightSkyTable Table);

    private static Loaded? _loaded;

    private readonly ImmutableDictionary<int, NightLook> _looks;

    private NightSkyTable(IEnumerable<NightSkyRow> rows)
    {
        var looks = ImmutableDictionary.CreateBuilder<int, NightLook>();
        foreach (var row in rows)
        {
            looks[row.Map] = LookOf(row);
        }

        _looks = looks.ToImmutable();
    }

    /// <summary>The game's own rows: every night decade the defaults.</summary>
    public static TableFile<ImmutableArray<NightSkyRow>> Base =>
        new(TableNames.NightSkies, "", "mod rule", "", "NightSkyTable", [.. NightMaps.Select(m => RowOf(m, NightLook.Defaults))]);

    /// <summary>The table's columns with decimals (all but "map").</summary>
    public static ImmutableArray<string> Decimals { get; } =
        ["stars", "starBrightness", "milkyWay", "cover", "cloudR", "cloudG", "cloudB", "moonGlow"];

    /// <summary>The table of the mounted mods (<see cref="GameTables.Mods"/>), read again when they
    /// change; a file that does not read is logged and the game's rows stay.</summary>
    internal static NightSkyTable Current
    {
        get
        {
            var mods = GameTables.Mods;
            var cached = Volatile.Read(ref _loaded);
            if (cached != null && ReferenceEquals(cached.Mods, mods))
            {
                return cached.Table;
            }

            cached = new Loaded(mods, Load(mods));
            Volatile.Write(ref _loaded, cached);
            return cached.Table;
        }
    }

    /// <summary>The table of <paramref name="mods"/>' rows over <see cref="Base"/>; throws
    /// <see cref="InvalidDataException"/> for a file that does not read.</summary>
    internal static NightSkyTable Of(VirtualFileSystem? mods) =>
        new(GameTables.Layer(TableNames.NightSkies, TableSchemas.NightSkies, Base, mods).Rows);

    /// <summary>Map <paramref name="map"/>'s look: its own row, else its decade's, else the defaults
    /// (a deathmatch arena's).</summary>
    internal NightLook For(int map) =>
        _looks.TryGetValue(map, out var own) ? own : _looks.TryGetValue(map / 10 * 10, out var decade) ? decade : NightLook.Defaults;

    /// <summary>A look as a row of map <paramref name="map"/>.</summary>
    internal static NightSkyRow RowOf(int map, NightLook k) =>
        new(map, D(k.Stars), D(k.StarBrightness), D(k.MilkyWay), D(k.Cover), D(k.Clouds.X), D(k.Clouds.Y), D(k.Clouds.Z), D(k.MoonGlow));

    // A float as the table writes it: 0.035, not 0.03500000014901161.
    private static double D(float x) => Math.Round(x, 4);

    private static NightLook LookOf(NightSkyRow r) => new((float)r.Stars, (float)r.StarBrightness, (float)r.MilkyWay, (float)r.Cover,
        new Vector3((float)r.CloudR, (float)r.CloudG, (float)r.CloudB), (float)r.MoonGlow);

    private static NightSkyTable Load(VirtualFileSystem? mods)
    {
        try
        {
            return Of(mods);
        }
        catch (InvalidDataException e)
        {
            Log.Warn("Mods", e.Message);
            return Of(null);
        }
    }
}
