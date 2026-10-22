using System.Collections.Immutable;
using System.Numerics;
using RetroEng.Content;
using RetroEng.Diagnostics;
using SkyNet.Data;

namespace SkyNet.Game.Enhanced;

/// <summary>
/// The dusk maps' skies in the enhanced look (docs/modding.md): the table <c>skies</c>, one row per
/// map, a mod rule (no shipped file: the rows below are the game's own, a mod's rows replace them by
/// map). A row of a decade's first map (250) covers the decade; a row of an exact map wins.
/// <code>
///   Defaults (SunsetLook.Defaults per dusk decade) ◄── tables/skies.json of each mod, by "map"
///        │
///        ▼
///   SunsetTable.Current.For(map) ──► Level (the sky sphere, the haze) ──► LookLighting (the clouds)
/// </code>
/// </summary>
public sealed class SunsetTable
{
    private sealed record Loaded(VirtualFileSystem? Mods, SunsetTable Table);

    private static Loaded? _loaded;

    private readonly ImmutableDictionary<int, SunsetLook> _looks;

    private SunsetTable(IEnumerable<SkyRow> rows)
    {
        var looks = ImmutableDictionary.CreateBuilder<int, SunsetLook>();
        foreach (var row in rows)
        {
            looks[row.Map] = LookOf(row);
        }

        _looks = looks.ToImmutable();
    }

    /// <summary>The game's own rows: every dusk decade the DOS dome's look.</summary>
    public static TableFile<ImmutableArray<SkyRow>> Base =>
        new(TableNames.Skies, "", "mod rule", "", "SunsetTable", [.. Level.DuskMaps.Select(m => RowOf(m, SunsetLook.Defaults))]);

    /// <summary>The table of the mounted mods (<see cref="GameTables.Mods"/>), read again when they
    /// change; a file that does not read is logged and the game's rows stay.</summary>
    internal static SunsetTable Current
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
    internal static SunsetTable Of(VirtualFileSystem? mods) =>
        new(GameTables.Layer(TableNames.Skies, TableSchemas.Skies, Base, mods).Rows);

    /// <summary>Map <paramref name="map"/>'s look: its own row, else its decade's, else the defaults.</summary>
    internal SunsetLook For(int map) =>
        _looks.TryGetValue(map, out var own) ? own : _looks.TryGetValue(map / 10 * 10, out var decade) ? decade : SunsetLook.Defaults;

    /// <summary>A look as a row of map <paramref name="map"/>.</summary>
    internal static SkyRow RowOf(int map, SunsetLook k) => new(map, D(k.Horizon.X), D(k.Horizon.Y), D(k.Horizon.Z), D(k.Low.X), D(k.Low.Y),
        D(k.Low.Z), D(k.Zenith.X), D(k.Zenith.Y), D(k.Zenith.Z), D(k.Glow.X), D(k.Glow.Y), D(k.Glow.Z), D(k.SunElevation), D(k.GlowWidth),
        D(k.Cover), D(k.Clouds.X), D(k.Clouds.Y), D(k.Clouds.Z));

    /// <summary>The table's columns with decimals (all but "map").</summary>
    public static ImmutableArray<string> Decimals { get; } =
    [
        "horizonR", "horizonG", "horizonB", "lowR", "lowG", "lowB", "zenithR", "zenithG", "zenithB", "glowR", "glowG", "glowB",
        "sunElevation", "glowWidth", "cover", "cloudR", "cloudG", "cloudB",
    ];

    // A float as the table writes it: 0.44f, not 0.4399999976158142.
    private static double D(float x) => Math.Round(x, 4);

    private static SunsetLook LookOf(SkyRow r) => new(Colour(r.HorizonR, r.HorizonG, r.HorizonB), Colour(r.LowR, r.LowG, r.LowB),
        Colour(r.ZenithR, r.ZenithG, r.ZenithB), Colour(r.GlowR, r.GlowG, r.GlowB), (float)r.SunElevation, (float)r.GlowWidth, (float)r.Cover,
        Colour(r.CloudR, r.CloudG, r.CloudB));

    private static Vector3 Colour(double r, double g, double b) => new((float)r, (float)g, (float)b);

    private static SunsetTable Load(VirtualFileSystem? mods)
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
