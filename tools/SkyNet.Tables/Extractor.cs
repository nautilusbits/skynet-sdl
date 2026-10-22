using System.Collections.Immutable;
using System.Security.Cryptography;
using SkyNet.Data;
using SkyNet.Formats;

namespace SkyNet.Tables;

/// <summary>
/// Reads every table the game needs out of SKYNET.EXE v1.01 and writes each as one JSON file
/// (<see cref="TableWriter"/>). The addresses are v1.01 VAs (file offset = VA + 0x53AA4); data
/// pointers stored in the image are object-relative, VA = raw + 0x30000 (<see cref="AisData.PointerBase"/>).
/// </summary>
public static class Extractor
{
    /// <summary>The tool's version, written into every file's header; bump it when a file's shape changes.</summary>
    public const string ToolVersion = "SkyNet.Tables 2";

    /// <summary>MD5 of SKYNET.EXE v1.01 (1 839 291 bytes), the only build the tables are extracted from.</summary>
    public const string V101Md5 = "5d1d3d82471cb515db091b4a9045479d";
    public const long V101Size = 1_839_291;
    public const string Source = "SKYNET.EXE v1.01";

    // v1.01 addresses.
    public const uint WeaponsAt = 0x43714, AmmoAt = 0x40828, PoolsAt = 0x440EC, StartSkyNetAt = 0x4356C, StartShockAt = 0x43538,
        PiecesAt = 0x3844B, DestructAt = 0x424D6, DropsAt = 0x42C7E, LocationsAt = 0x53D00, LocationPrefixAt = 0x35D00,
        EnemiesSkyNetAt = 0x53520, EnemiesShockAt = 0x53400;
    /// <summary>The ammo callbacks that name the gravity and bolt families (v1.01).</summary>
    public const uint GravityCallback = 0xF3914, BoltCallback = 0xF394D;

    public const int WeaponStride = 0x60, WeaponCount = 26, AmmoStride = 0x32, AmmoCount = 25, PoolCount = 13,
        DestructRow = 14, DestructCount = 140, DropListCount = 10, LocationRow = 0x26, LocationName = 36,
        SkyNetMissions = 8, ShockMissions = 17;

    /// <summary>Lower-case hex MD5 of <paramref name="bytes"/>.</summary>
    public static string Md5(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(MD5.HashData(bytes));

    /// <summary>Why <paramref name="exeBytes"/> is not SKYNET.EXE v1.01; null when it is.</summary>
    public static string? Reject(ReadOnlySpan<byte> exeBytes)
    {
        var md5 = Md5(exeBytes);
        if (md5 == V101Md5)
        {
            return null;
        }

        return $"not SKYNET.EXE v1.01: {exeBytes.Length} bytes, MD5 {md5} (want {V101Size} bytes, MD5 {V101Md5}); " +
               "the 2017 launcher is ~100 KB, the game image is the 1.8 MB file (GAME.EXE in some installs)";
    }

    /// <summary>Every table file: name → JSON text (LF line ends, final newline).</summary>
    public static IReadOnlyList<(string Name, string Json)> Extract(byte[] exeBytes)
    {
        if (Reject(exeBytes) is { } why)
        {
            throw new InvalidDataException(why);
        }

        var exe = LeExecutable.Parse(exeBytes) ?? throw new InvalidDataException("no LE image");
        var ais = AisData.Parse(exeBytes) ?? throw new InvalidDataException("no enemy table");
        var md5 = Md5(exeBytes);
        var files = new List<(string, string)>();

        void Add<T>(string name, string address, T rows, TableSchema<T> schema) =>
            files.Add((name, TableWriter.Write(new TableFile<T>(name, address, Source, md5, ToolVersion, rows), schema)));

        Add(TableNames.EnemyTypes, Hex(ais.Tables.EnemyTable), ais.Types, TableSchemas.EnemyTypes);
        Add(TableNames.AisOps, "scripts reached from enemy_types", [.. ais.Ops.Values.OrderBy(o => o.Va)], TableSchemas.AisOps);
        Add(TableNames.AisAnims, "blocks started by ais_ops", [.. ais.Anims.Values.OrderBy(a => a.Va)], TableSchemas.AisAnims);
        Add(TableNames.Ammo, Hex(AmmoAt), ReadAmmo(exe), TableSchemas.Ammo);
        Add(TableNames.Weapons, Hex(WeaponsAt), ReadWeapons(exe), TableSchemas.Weapons);
        Add(TableNames.Pools, Hex(PoolsAt), ReadPools(exe), TableSchemas.Pools);
        Add(TableNames.StartLists, $"{Hex(StartSkyNetAt)} {Hex(StartShockAt)}",
            new StartLists(ReadList(exe, StartSkyNetAt), ReadList(exe, StartShockAt)), TableSchemas.StartLists);
        Add(TableNames.Pieces, Hex(PiecesAt), ReadPieces(exe), TableSchemas.Pieces);
        Add(TableNames.Destruction, Hex(DestructAt), ReadDestruction(exe), TableSchemas.Destruction);
        Add(TableNames.DropLists, Hex(DropsAt), ReadDropLists(exe), TableSchemas.DropLists);
        Add(TableNames.Locations, $"{Hex(LocationsAt)} {Hex(LocationPrefixAt)}", ReadLocations(exe), TableSchemas.Locations);
        Add(TableNames.MissionEnemies, $"{Hex(EnemiesSkyNetAt)} {Hex(EnemiesShockAt)}",
            new MissionEnemies(ReadEnemyCounts(exe, EnemiesSkyNetAt, SkyNetMissions), ReadEnemyCounts(exe, EnemiesShockAt, ShockMissions)),
            TableSchemas.MissionEnemies);
        return files;
    }

    private static string Hex(uint va) => $"0x{va:X5}";

    private static void Need(LeExecutable exe, uint va, int size, string what)
    {
        if (!exe.Contains(va, size))
        {
            throw new InvalidDataException($"{what}: 0x{va:X} (+{size}) is outside the image");
        }
    }

    private static bool IsPointer(long raw) => raw > 0x10000 && raw < 0x40000;

    private static ImmutableArray<AmmoRow> ReadAmmo(LeExecutable exe)
    {
        Need(exe, AmmoAt, AmmoStride * AmmoCount, "ammo types");
        var rows = ImmutableArray.CreateBuilder<AmmoRow>();
        for (var i = 0; i < AmmoCount; i++)
        {
            var b = AmmoAt + (uint)(AmmoStride * i);
            int I(int o) => exe.I32(b + (uint)o);
            int U(int o) => exe.U16(b + (uint)o);
            var cb = exe.U32(b);
            var family = cb == GravityCallback ? "gravity" : cb == BoltCallback ? "bolt" : "hitscan";
            var look = I(0x04);
            var model = IsPointer(look) ? exe.CString((uint)look + AisData.PointerBase + 4).ToUpperInvariant() : "";
            rows.Add(new AmmoRow(i, cb, family, model, look, I(0x08), I(0x0C), I(0x10), I(0x18), I(0x1C), I(0x20), I(0x24),
                U(0x28), U(0x2A), U(0x2E)));
        }

        return rows.ToImmutable();
    }

    private static ImmutableArray<WeaponRow> ReadWeapons(LeExecutable exe)
    {
        Need(exe, WeaponsAt, WeaponStride * WeaponCount, "weapons");
        var rows = ImmutableArray.CreateBuilder<WeaponRow>();
        for (var i = 0; i < WeaponCount; i++)
        {
            var b = WeaponsAt + (uint)(WeaponStride * i);
            int I(int o) => exe.I32(b + (uint)o);
            rows.Add(new WeaponRow(i, I(0x00), I(0x14), I(0x18), I(0x1C), I(0x24), I(0x30), I(0x34), I(0x38),
                I(0x48), I(0x4C), I(0x50), I(0x54), I(0x58), I(0x5C)));
        }

        return rows.ToImmutable();
    }

    private static ImmutableArray<PoolRow> ReadPools(LeExecutable exe)
    {
        Need(exe, PoolsAt, 12 * PoolCount, "ammo pools");
        var rows = ImmutableArray.CreateBuilder<PoolRow>();
        for (var i = 0; i < PoolCount; i++)
        {
            var b = PoolsAt + (uint)(12 * i);
            rows.Add(new PoolRow(i, exe.I32(b), exe.I32(b + 4), exe.I32(b + 8)));
        }

        return rows.ToImmutable();
    }

    /// <summary>A start list: dwords up to the first negative one (at most 27).</summary>
    private static ImmutableArray<int> ReadList(LeExecutable exe, uint at)
    {
        var list = ImmutableArray.CreateBuilder<int>();
        for (var i = 0; i < WeaponCount + 1 && exe.Contains(at + (uint)(4 * i), 4); i++)
        {
            var r = exe.I32(at + (uint)(4 * i));
            if (r < 0)
            {
                break;
            }

            list.Add(r);
        }

        return list.ToImmutable();
    }

    private static ImmutableArray<PieceRow> ReadPieces(LeExecutable exe)
    {
        var rows = ImmutableArray.CreateBuilder<PieceRow>();
        for (var b = PiecesAt; exe.Contains(b, 24) && rows.Count < 32; b += 24)
        {
            var type = exe.I32(b);
            if (type < 0)
            {
                break;
            }

            rows.Add(new PieceRow(type, exe.I32(b + 4), exe.I32(b + 8), exe.I32(b + 12), exe.I32(b + 16), exe.I32(b + 20)));
        }

        return rows.ToImmutable();
    }

    private static ImmutableArray<DestructionRow> ReadDestruction(LeExecutable exe)
    {
        Need(exe, DestructAt, DestructRow * DestructCount, "destruction table");
        var rows = ImmutableArray.CreateBuilder<DestructionRow>();
        for (var i = 0; i < DestructCount; i++)
        {
            var b = DestructAt + (uint)(DestructRow * i);
            short S(int o) => (short)exe.U16(b + (uint)o);
            rows.Add(new DestructionRow(i, [exe.U16(b), exe.U16(b + 2), exe.U16(b + 4), exe.U16(b + 6)], S(8), S(10), S(12)));
        }

        return rows.ToImmutable();
    }

    private static ImmutableArray<DropListRow> ReadDropLists(LeExecutable exe)
    {
        Need(exe, DropsAt, 4 * DropListCount, "drop lists");
        var rows = ImmutableArray.CreateBuilder<DropListRow>();
        for (var i = 0; i < DropListCount; i++)
        {
            var at = exe.U32(DropsAt + (uint)(4 * i)) + AisData.PointerBase;
            var count = exe.Contains(at, 4) ? exe.I32(at) : 0;
            var list = ImmutableArray.CreateBuilder<int>();
            for (var k = 0; k < Math.Clamp(count, 0, 64) && exe.Contains(at + 4 + (uint)(2 * k), 2); k++)
            {
                list.Add(exe.U16(at + 4 + (uint)(2 * k)));
            }

            rows.Add(new DropListRow(i, list.ToImmutable()));
        }

        return rows.ToImmutable();
    }

    private static LocationTable ReadLocations(LeExecutable exe)
    {
        Need(exe, LocationPrefixAt, 1, "location prefix");
        var rows = ImmutableArray.CreateBuilder<LocationRow>();
        for (var i = 0; i < 512; i++)
        {
            var at = LocationsAt + (uint)(LocationRow * i);
            Need(exe, at, LocationRow, "location names");
            var map = (short)exe.U16(at);
            if (map <= 0)
            {
                return new LocationTable(exe.CString(LocationPrefixAt), rows.ToImmutable());
            }

            var name = exe.CString(at + 2);
            rows.Add(new LocationRow(map, name.Length > LocationName ? name[..LocationName] : name));
        }

        throw new InvalidDataException("location names: no end row");
    }

    /// <summary>The <c>enemies</c> field (+8) of each 16-byte statistics row.</summary>
    private static ImmutableArray<int> ReadEnemyCounts(LeExecutable exe, uint at, int count)
    {
        Need(exe, at, 16 * count, "mission statistics");
        return [.. Enumerable.Range(0, count).Select(i => exe.I32(at + (uint)(16 * i) + 8))];
    }
}
