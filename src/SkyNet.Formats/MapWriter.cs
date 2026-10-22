using System.Buffers.Binary;
using System.Text;

namespace SkyNet.Formats;

/// <summary>Whether a map stands outdoors on a WLD under the sky or is an interior (MAP+0x2344).</summary>
public enum MapSetting { Outdoor, Interior }

/// <summary>
/// One record a new map holds, in DOS units (Y down) and 11-bit angles. Which fields count follows the
/// variant: a mesh its name, angles, hit points and link record (written when it has an act or a
/// next); a light its intensity and enable; a sprite or marker (bank 299) its sprite and params.
/// <see cref="Next"/> is the index of another record of the same <see cref="MapWriter.Write"/> call
/// (−1: none); the writer turns it into that record's file offset.
/// </summary>
public sealed record MapRecord(MapEntityVariant Variant, int X, int Y, int Z)
{
    public int Pitch { get; init; }
    public int Yaw { get; init; }
    public int Roll { get; init; }
    public int NameIndex { get; init; }
    public int HitPoints { get; init; }
    public bool Actor { get; init; }
    public int Intensity { get; init; }
    public int LightEnable { get; init; }
    public int Sprite { get; init; }
    public int Param16 { get; init; }
    public int Param8 { get; init; }
    public int State { get; init; }
    public int Act { get; init; }
    public int Next { get; init; } = -1;

    /// <summary>A placement marker of type <paramref name="type"/> (sprite bank 299).</summary>
    public static MapRecord Marker(int type, int x, int y, int z) =>
        new(MapEntityVariant.Sprite, x, y, z) { Sprite = MapFile.MarkerBank << MapWriter.SpriteBankShift | type };
}

/// <summary>
/// Writes a MAP.NNN in the DOS layout <see cref="MapFile.Parse"/> reads (docs/formats/map.md): the
/// header (grid size, the 0x253C and 0xFFFFFFFF signature words, the name slots, an empty per-name
/// defaults list, the outdoor flag), the cell grid of chain heads, the entity blocks cell by cell
/// (head + sub-record at entity + 25) and the variant-1 link records after them.
/// <code>
///   0x0000 header   0x253C u32[W·H] chain heads   blocks (48 / 35 / 36 bytes)   link records (10 bytes)
/// </code>
/// Header words the format does not explain are written 0.
/// </summary>
public static class MapWriter
{
    public const int SpriteBankShift = 7;
    /// <summary>The cell side, world units (cell = coordinate &gt;&gt; 10).</summary>
    public const int CellShift = 10;
    /// <summary>The name slots between 0x14 and the defaults head at 0x233C.</summary>
    public const int MaxNames = (MapFile.DefaultsHeadOffset - MapFile.NamesOffset) / MapFile.NameLength;

    private const uint ChainEnd = 0xFFFFFFFF;
    private const uint OutdoorFlag = 1, InteriorFlag = 2;
    private const byte ActorFlag = 0x40;

    /// <summary>The file for <paramref name="records"/> on a <paramref name="gridWidth"/> ×
    /// <paramref name="gridHeight"/> cell grid; <paramref name="offsets"/> gets each record's file
    /// offset (its key). <see cref="ArgumentException"/> for a record outside the grid, a name too long
    /// or too many names.</summary>
    public static byte[] Write(int gridWidth, int gridHeight, MapSetting setting, IReadOnlyList<string> names,
        IReadOnlyList<MapRecord> records, out int[] offsets)
    {
        if (gridWidth is <= 0 or > MapFile.MaxGridSide || gridHeight is <= 0 or > MapFile.MaxGridSide)
        {
            throw new ArgumentException($"grid {gridWidth} x {gridHeight}: each side 1-{MapFile.MaxGridSide}");
        }

        if (names.Count > MaxNames)
        {
            throw new ArgumentException($"{names.Count} names: at most {MaxNames}");
        }

        // Blocks in cell order (rows of cells, then the records' own order): each cell's chain is contiguous.
        var order = Enumerable.Range(0, records.Count)
            .OrderBy(i => CellIndex(records[i], gridWidth, gridHeight)).ThenBy(i => i).ToArray();
        offsets = new int[records.Count];
        var at = MapFile.GridOffset + gridWidth * gridHeight * 4;
        foreach (var i in order)
        {
            offsets[i] = at;
            at += MapFile.BlockLength((int)records[i].Variant);
        }

        // Link records after the blocks, for the meshes that act or chain.
        var links = new Dictionary<int, int>();
        foreach (var i in order.Where(i => records[i].Variant == MapEntityVariant.Mesh && (records[i].Act != 0 || records[i].Next >= 0)))
        {
            links[i] = at;
            at += MapLinkRecord.Size;
        }

        var bytes = new byte[at];
        Header(bytes, gridWidth, gridHeight, setting, names, records.Count);
        var heads = new Dictionary<int, int>();
        for (var k = 0; k < order.Length; k++)
        {
            var i = order[k];
            var cell = CellIndex(records[i], gridWidth, gridHeight);
            heads.TryAdd(cell, offsets[i]);
            var sameCellNext = k + 1 < order.Length && CellIndex(records[order[k + 1]], gridWidth, gridHeight) == cell
                ? (uint)offsets[order[k + 1]] : ChainEnd;
            Block(bytes, offsets[i], records[i], sameCellNext, NextOffset(records[i], offsets), links.GetValueOrDefault(i));
        }

        for (var cell = 0; cell < gridWidth * gridHeight; cell++)
        {
            U32(bytes, MapFile.GridOffset + cell * 4, heads.TryGetValue(cell, out var head) ? (uint)head : ChainEnd);
        }

        foreach (var (i, offset) in links)
        {
            Link(bytes, offset, records[i], NextOffset(records[i], offsets));
        }

        return bytes;
    }

    private static void Header(byte[] b, int w, int h, MapSetting setting, IReadOnlyList<string> names, int count)
    {
        U32(b, 0, (uint)count);
        U32(b, 4, (uint)w);
        U32(b, 8, (uint)h);
        U32(b, 12, MapFile.GridOffset);
        U32(b, 16, MapFile.HeaderSentinel);
        for (var n = 0; n < names.Count; n++)
        {
            var name = Encoding.ASCII.GetBytes(names[n]);
            if (name.Length > MapFile.NameLength)
            {
                throw new ArgumentException($"name \"{names[n]}\" is longer than {MapFile.NameLength}");
            }

            name.CopyTo(b, MapFile.NamesOffset + n * MapFile.NameLength);
        }

        U32(b, MapFile.OutdoorFlagOffset, setting == MapSetting.Outdoor ? OutdoorFlag : InteriorFlag);
    }

    // One block: the 25-byte head, then the variant's sub-record.
    private static void Block(byte[] b, int at, MapRecord r, uint nextInCell, int next, int link)
    {
        var flags = (byte)((int)r.Variant | (r.Actor ? ActorFlag : 0));
        U32(b, at, nextInCell);
        U32(b, at + 4, ChainEnd);
        U32(b, at + 8, (uint)r.X);
        U32(b, at + 12, (uint)r.Y);
        U32(b, at + 16, (uint)r.Z);
        b[at + 20] = flags;
        var sub = at + MapFile.HeadLength;
        U32(b, at + 21, (uint)sub);
        switch (r.Variant)
        {
            case MapEntityVariant.Mesh:
                U32(b, sub, (uint)r.Pitch);
                U32(b, sub + 4, (uint)r.Yaw);
                U32(b, sub + 8, (uint)r.Roll);
                U16(b, sub + 0x0C, r.NameIndex);
                U16(b, sub + 0x0E, r.HitPoints);
                b[sub + 0x12] = (byte)r.State;
                U32(b, sub + 0x13, (uint)link);
                break;
            case MapEntityVariant.Light:
                U16(b, sub, r.Intensity);
                b[sub + 2] = (byte)r.State;
                b[sub + 3] = (byte)r.Act;
                U32(b, sub + 4, (uint)next);
                U16(b, sub + 8, r.LightEnable);
                break;
            case MapEntityVariant.Sprite:
                U16(b, sub, r.Sprite);
                U16(b, sub + 2, r.Param16);
                b[sub + 4] = (byte)r.Param8;
                b[sub + 5] = (byte)r.State;
                U32(b, sub + 6, (uint)next);
                b[sub + 10] = (byte)r.Act;
                break;
        }
    }

    // A mesh's link record: destruction type 0, its next, its act.
    private static void Link(byte[] b, int at, MapRecord r, int next)
    {
        U32(b, at + 5, (uint)next);
        b[at + 9] = (byte)r.Act;
    }

    private static int NextOffset(MapRecord r, int[] offsets) => r.Next >= 0 && r.Next < offsets.Length ? offsets[r.Next] : 0;

    private static int CellIndex(MapRecord r, int w, int h)
    {
        int cx = r.X >> CellShift, cz = r.Z >> CellShift;
        if (cx < 0 || cz < 0 || cx >= w || cz >= h)
        {
            throw new ArgumentException($"record at {r.X}, {r.Z} lies outside the {w} x {h} cell grid");
        }

        return cz * w + cx;
    }

    private static void U32(byte[] b, int at, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(at), v);

    private static void U16(byte[] b, int at, int v) => BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(at), (ushort)v);
}
