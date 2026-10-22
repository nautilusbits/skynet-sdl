using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Text;

namespace SkyNet.Formats;

/// <summary>An entity's variant, flags bits 0-1.</summary>
public enum MapEntityVariant : byte
{
    /// <summary>Variant 0: no sub-record is read (not seen in shipped maps).</summary>
    None = 0,
    /// <summary>Variant 1: a placed .3D mesh or actor (23-byte sub-record).</summary>
    Mesh = 1,
    /// <summary>Variant 2: a point light (10-byte sub-record).</summary>
    Light = 2,
    /// <summary>Variant 3: a billboard sprite or a placement marker (11-byte sub-record).</summary>
    Sprite = 3,
}

/// <summary>
/// One node of the per-name defaults list at 0x233C (DOS "Map_SetDefault"): a variant-1 entity
/// of that name with no link record of its own (and not an actor) takes these values, and
/// <see cref="Link"/>, the record at <see cref="LinkOffset"/> when it lies inside the file.
/// </summary>
public readonly record struct MapDefault(int NodeOffset, int NameIndex, int LinkOffset, int HitPoints, int StateByte, MapLinkRecord? Link = null);

/// <summary>
/// The 10-byte link record a variant-1 entity points at: destruction data and the action chain.
/// </summary>
/// <param name="Offset">File offset of the record.</param>
/// <param name="DestroyType">+0: index into the destruction table (v1.01 0x424d6, v1.00 0x423d6).</param>
/// <param name="DestroyParam">+1 (i16): blast parameter for destruction type 0.</param>
/// <param name="Unknown3">+3 (u16): meaning unknown.</param>
/// <param name="Next">+5 (i32): next entity in the action chain, ≤ 0 ends.</param>
/// <param name="ActType">+9: action type (index into the handler table at 0x59b00).</param>
public readonly record struct MapLinkRecord(int Offset, int DestroyType, int DestroyParam, int Unknown3, int Next, int ActType)
{
    public const int Size = 10;
}

/// <summary>
/// One entity block of a MAP file. Fields that do not apply to the variant keep the Godot port's
/// "unset" values (-1 for indices, 0 otherwise). <see cref="Block"/> and <see cref="SubRecord"/>
/// keep the raw bytes so fields not decoded here stay readable.
/// </summary>
public sealed class MapEntity
{
    public MapEntity()
    {
    }

    /// <summary>A copy of <paramref name="other"/>, for an object initializer to change (a map layer's edit).</summary>
    public MapEntity(MapEntity other)
    {
        FileOffset = other.FileOffset;
        CellX = other.CellX;
        CellZ = other.CellZ;
        X = other.X;
        Y = other.Y;
        Z = other.Z;
        Flags = other.Flags;
        NextInCell = other.NextInCell;
        Word4 = other.Word4;
        SubRecordOffset = other.SubRecordOffset;
        Block = other.Block;
        SubRecord = other.SubRecord;
        Pitch = other.Pitch;
        Yaw = other.Yaw;
        Roll = other.Roll;
        NameIndex = other.NameIndex;
        HitPoints = other.HitPoints;
        Sub10 = other.Sub10;
        OwnLinkOffset = other.OwnLinkOffset;
        LinkOffset = other.LinkOffset;
        Link = other.Link;
        UsesDefaults = other.UsesDefaults;
        LightIntensity = other.LightIntensity;
        LightEnable = other.LightEnable;
        SpriteIndex = other.SpriteIndex;
        MarkerType = other.MarkerType;
        EnemyType = other.EnemyType;
        Convoy = other.Convoy;
        Param16 = other.Param16;
        Param8 = other.Param8;
        StateByte = other.StateByte;
        LinkNext = other.LinkNext;
        ActType = other.ActType;
    }

    /// <summary>File offset of the block: the key action chains point at.</summary>
    public int FileOffset { get; init; }
    /// <summary>The grid cell whose chain holds the entity.</summary>
    public int CellX { get; init; }
    public int CellZ { get; init; }
    /// <summary>World position, DOS axes (Y down: negative is up), used verbatim.</summary>
    public int X { get; init; }
    public int Y { get; init; }
    public int Z { get; init; }
    /// <summary>The flags byte (+20): bits 0-1 variant, 0x08 skip (never in <see cref="MapFile.Entities"/>), 0x40 actor.</summary>
    public byte Flags { get; init; }
    /// <summary>+0: next block in the cell's chain (0, 0xFFFFFFFE or 0xFFFFFFFF end it).</summary>
    public uint NextInCell { get; init; }
    /// <summary>+4: unused / back pointer.</summary>
    public uint Word4 { get; init; }
    /// <summary>File offset the sub-record was read from (+21, or block + 25 when that is out of range).</summary>
    public int SubRecordOffset { get; init; }
    /// <summary>The whole block (<see cref="MapFile.BlockLength"/> bytes).</summary>
    public ImmutableArray<byte> Block { get; init; } = [];
    /// <summary>The sub-record (23 / 10 / 11 bytes for variants 1 / 2 / 3), clipped to the file.</summary>
    public ImmutableArray<byte> SubRecord { get; init; } = [];

    public MapEntityVariant Variant => (MapEntityVariant)(Flags & 3);
    /// <summary>Flags 0x40 on a variant-1 entity: an actor (mesh from MDMDENMS.BSA).</summary>
    public bool IsActor => (Flags & 0x40) != 0;

    // ---- variant 1 ----

    /// <summary>Variant 1, sub+0: pitch (about X), raw i32; use <see cref="MapFile.AngleRadians"/>.</summary>
    public int Pitch { get; init; }
    /// <summary>Variant 1, sub+4: yaw (about Y), raw i32.</summary>
    public int Yaw { get; init; }
    /// <summary>Variant 1, sub+8: roll (about Z), raw i32.</summary>
    public int Roll { get; init; }
    /// <summary>Variant 1, sub+0x0C: index into <see cref="MapFile.Names"/>; -1 for other variants.</summary>
    public int NameIndex { get; init; } = -1;
    /// <summary>Variant 1: hit points (sub+0x0E, or the per-name default).</summary>
    public int HitPoints { get; init; }
    /// <summary>Variant 1, sub+0x10 (u16): meaning unknown.</summary>
    public int Sub10 { get; init; }
    /// <summary>Variant 1, sub+0x13 as stored: the entity's own link record offset (&lt; 1 = none).</summary>
    public int OwnLinkOffset { get; init; }
    /// <summary>Variant 1: the link record offset in effect (own, or the per-name default's).</summary>
    public int LinkOffset { get; init; }
    /// <summary>Variant 1: the link record in effect, when it lies inside the file.</summary>
    public MapLinkRecord? Link { get; init; }
    /// <summary>
    /// Variant 1: hp, state byte and link came from <see cref="MapFile.Defaults"/>; an editor must
    /// not write them back into the entity.
    /// </summary>
    public bool UsesDefaults { get; init; }

    // ---- variant 2 ----

    /// <summary>Variant 2, sub+0 (u16): light intensity; -1 for other variants.</summary>
    public int LightIntensity { get; init; } = -1;
    /// <summary>Variant 2, sub+8 (read as u16, as the Godot port does): the light is added only when &gt; 0.</summary>
    public int LightEnable { get; init; }

    // ---- variant 3 ----

    /// <summary>Variant 3, sub+0 (u16): sprite index, bank = &gt;&gt; 7, record = &amp; 0x7F; -1 for other variants.</summary>
    public int SpriteIndex { get; init; } = -1;
    public int SpriteBank => SpriteIndex < 0 ? -1 : SpriteIndex >> 7;
    public int SpriteRecord => SpriteIndex < 0 ? -1 : SpriteIndex & 0x7F;
    /// <summary>Variant 3 with bank 299: a placement marker, not a sprite.</summary>
    public bool IsMarker => SpriteBank == MapFile.MarkerBank;
    /// <summary>Marker type (<see cref="SpriteIndex"/> &amp; 0x7F) for markers, else -1.</summary>
    public int MarkerType { get; init; } = -1;
    /// <summary>Markers: sub+10 &amp; 0x7F (the enemy type of an enemy-start marker), else -1.</summary>
    public int EnemyType { get; init; } = -1;
    /// <summary>Markers: bit 7 of sub+10, a convoy vehicle.</summary>
    public bool Convoy { get; init; }
    /// <summary>Variant 3, sub+2 (u16): per-use value — teleport target map, marker parameter (radiation, music, trap distance).</summary>
    public int Param16 { get; init; }
    /// <summary>Variant 3, sub+4 (u8): per-use value — teleport spawn set, Future Shock robot act.</summary>
    public int Param8 { get; init; }

    // ---- action chain, every variant ----

    /// <summary>The state byte (v1 sub+0x12 or default, v2 sub+2, v3 sub+5).</summary>
    public int StateByte { get; init; }
    /// <summary>Next entity of the action chain (≤ 0 ends): v1 link+5, v2 sub+4, v3 sub+6.</summary>
    public int LinkNext { get; init; }
    /// <summary>Action type: v1 link+9, v2 sub+3, v3 sub+10 (the enemy type byte on markers).</summary>
    public int ActType { get; init; }

    public double PitchRadians => MapFile.AngleRadians(Pitch);
    public double YawRadians => MapFile.AngleRadians(Yaw);
    public double RollRadians => MapFile.AngleRadians(Roll);
}

/// <summary>
/// MAP.NNN, one level's entities (ported from the Godot port's map_file.gd). Header: u32 word 0
/// (unknown), grid width at +4, height at +8, 0x253C at +12 and 0xFFFFFFFF at +16 (both checked
/// as a signature), 8-byte name slots from 0x14, the per-name defaults list head at 0x233C, the
/// outdoor flag at 0x2344 (MAP+9028; 1 = outdoor), and the cell grid of u32 chain heads at 0x253C.
/// The name table has no count: exactly max(name index) + 1 slots are read after the entity walk.
/// </summary>
public sealed class MapFile
{
    public const int GridOffset = 0x253C;
    public const int NamesOffset = 0x14;
    public const int NameLength = 8;
    public const uint HeaderSentinel = 0xFFFFFFFF;
    public const int DefaultsHeadOffset = 0x233C;
    /// <summary>MAP+9028: 1 = outdoor (load WLD, draw sky), else interior.</summary>
    public const int OutdoorFlagOffset = 0x2344;
    /// <summary>Shortest entity block (variant 2: 25 + 10 bytes).</summary>
    public const int BlockMin = 35;
    public const int HeadLength = 25;
    public const int MaxGridSide = 1024;
    public const int MaxChainLength = 1024;
    /// <summary>The sprite bank that marks a variant-3 entity as a placement marker.</summary>
    public const int MarkerBank = 299;

    /// <summary>Header bytes 0 .. 0x253C, raw (name slots, defaults head, outdoor flag, unknown words).</summary>
    public ImmutableArray<byte> Header { get; }
    /// <summary>Header word 0 (cell count? unused).</summary>
    public uint Word0 { get; }
    public int GridWidth { get; }
    public int GridHeight { get; }
    /// <summary>The u32 at 0x2344, raw.</summary>
    public uint OutdoorFlag { get; }
    public bool IsOutdoor => OutdoorFlag == 1;
    /// <summary>Names by index; a slot that does not start with a name character is "".</summary>
    public ImmutableArray<string> Names { get; }
    /// <summary>The per-name defaults by name index (a later node for the same index wins).</summary>
    public ImmutableDictionary<int, MapDefault> Defaults { get; }
    /// <summary>Entities in walk order (cells row by row, then each cell's chain); skipped (0x08) blocks excluded.</summary>
    public ImmutableArray<MapEntity> Entities { get; }
    /// <summary>File offsets of blocks with flag 0x08, which DOS skips.</summary>
    public ImmutableArray<int> SkippedOffsets { get; }
    /// <summary>True when the walk hit the "more blocks than the file can hold" bound.</summary>
    public bool WalkCut { get; }

    readonly uint[] _grid;
    readonly Dictionary<int, MapEntity> _byOffset;

    MapFile(byte[] header, int w, int h, uint[] grid, ImmutableArray<string> names,
        ImmutableDictionary<int, MapDefault> defaults, ImmutableArray<MapEntity> entities,
        ImmutableArray<int> skipped, bool cut)
    {
        Header = ImmutableCollectionsMarshal.AsImmutableArray(header);
        Word0 = BinaryPrimitives.ReadUInt32LittleEndian(header);
        GridWidth = w;
        GridHeight = h;
        OutdoorFlag = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(OutdoorFlagOffset));
        _grid = grid;
        Names = names;
        Defaults = defaults;
        Entities = entities;
        SkippedOffsets = skipped;
        WalkCut = cut;
        _byOffset = new Dictionary<int, MapEntity>(entities.Length);
        foreach (var e in entities)
            _byOffset[e.FileOffset] = e;
    }

    /// <summary>
    /// This map with <paramref name="entities"/> in place of its own (in walk order; a key at or
    /// above <see cref="FirstAddedKey"/> is an added one) and <paramref name="names"/> (default: its
    /// own) as the name table: what a map layer builds. Header, grid, defaults and skipped blocks stay
    /// the DOS file's; entities keep the DOS bytes they were read from (<see cref="MapEntity.Block"/>).
    /// </summary>
    public MapFile With(IEnumerable<MapEntity> entities, IEnumerable<string>? names = null)
    {
        var ents = entities.ToImmutableArray();
        var keys = new HashSet<int>();
        foreach (var e in ents)
            if (!keys.Add(e.FileOffset))
                throw new ArgumentException($"two entities share the key {e.FileOffset}", nameof(entities));
        return new MapFile(Header.ToArray(), GridWidth, GridHeight, _grid, names?.ToImmutableArray() ?? Names,
            Defaults, ents, SkippedOffsets, WalkCut);
    }

    /// <summary>The first key an added entity takes: 2^24, past any DOS map's file offsets.</summary>
    public const int FirstAddedKey = 1 << 24;

    /// <summary>11-bit angle (&amp; 0x7FF, 2048 = 360°) in radians.</summary>
    public static double AngleRadians(int a) => (a & 0x7FF) * Math.Tau / 2048.0;

    /// <summary>On-disk block length for a flags byte: 48 / 35 / 36 for variants 1 / 2 / 3 (and 0).</summary>
    public static int BlockLength(int flags) => (flags & 3) switch
    {
        1 => 48,
        2 => 35,
        _ => 36,
    };

    /// <summary>Sub-record length per variant: 23 / 10 / 11 (variant 0 reads none).</summary>
    public static int SubRecordLength(int flags) => (flags & 3) switch
    {
        1 => 23,
        2 => 10,
        3 => 11,
        _ => 0,
    };

    /// <summary>The u32 chain head of cell (cx, cz); 0 outside the grid.</summary>
    public uint CellHead(int cx, int cz) =>
        cx < 0 || cz < 0 || cx >= GridWidth || cz >= GridHeight ? 0 : _grid[cz * GridWidth + cx];

    /// <summary>The (non-skipped) entity whose block starts at <paramref name="fileOffset"/>, for chain walks.</summary>
    public bool TryGetEntityAt(int fileOffset, out MapEntity entity) =>
        _byOffset.TryGetValue(fileOffset, out entity!);

    /// <summary>A variant-1 entity's name, "" when it has none.</summary>
    public string EntityName(MapEntity e) =>
        e.NameIndex >= 0 && e.NameIndex < Names.Length ? Names[e.NameIndex] : "";

    /// <summary>
    /// Parse a MAP file. Null when it is shorter than 0x253C, the signature words are wrong, a
    /// grid side is 0 or over 1024, or the grid runs past the end of the file.
    /// </summary>
    public static MapFile? Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < GridOffset)
            return null;
        if (U32(bytes, 12) != GridOffset || U32(bytes, 16) != HeaderSentinel)
            return null;
        uint gw = U32(bytes, 4), gh = U32(bytes, 8);
        if (gw == 0 || gh == 0 || gw > MaxGridSide || gh > MaxGridSide)
            return null;
        int w = (int)gw, h = (int)gh;
        if (GridOffset + (long)w * h * 4 > bytes.Length)
            return null;

        // Per-name defaults list.
        var defaults = new Dictionary<int, MapDefault>();
        int node = S32(bytes, DefaultsHeadOffset);
        for (int guard = 0; node > 0 && (long)node + 13 <= bytes.Length && guard < 4096; guard++)
        {
            int idx = BinaryPrimitives.ReadInt16LittleEndian(bytes[(node + 4)..]);
            int dl = S32(bytes, node + 6);
            MapLinkRecord? drec = dl > 0 && (long)dl + MapLinkRecord.Size <= bytes.Length
                ? new MapLinkRecord(dl, bytes[dl], BinaryPrimitives.ReadInt16LittleEndian(bytes[(dl + 1)..]), U16(bytes, dl + 3), S32(bytes, dl + 5), bytes[dl + 9])
                : null;
            defaults[idx] = new MapDefault(node, idx, dl,
                BinaryPrimitives.ReadUInt16LittleEndian(bytes[(node + 10)..]), bytes[node + 12], drec);
            node = S32(bytes, node);
        }

        var grid = new uint[w * h];
        for (int i = 0; i < grid.Length; i++)
            grid[i] = U32(bytes, GridOffset + i * 4);

        var entities = ImmutableArray.CreateBuilder<MapEntity>();
        var skipped = ImmutableArray.CreateBuilder<int>();
        var visited = new HashSet<long>();
        int maxBlocks = bytes.Length / BlockMin;
        for (int cz = 0; cz < h; cz++)
        {
            for (int cx = 0; cx < w; cx++)
            {
                long off = grid[cz * w + cx];
                int safety = 0;
                while (off != 0 && off != 0xFFFFFFFF && off != 0xFFFFFFFE
                       && off + BlockMin <= bytes.Length && safety < MaxChainLength)
                {
                    if (visited.Contains(off) || visited.Count >= maxBlocks)
                        break;
                    visited.Add(off);
                    safety++;
                    int eo = (int)off;
                    uint next = U32(bytes, eo);
                    byte flags = bytes[eo + 20];
                    // The last block is often a 36-byte variant-3 record heading its chain: bound
                    // each block by its own length, never a flat 48.
                    if (eo + BlockLength(flags) > bytes.Length)
                        break;
                    if ((flags & 0x08) != 0)
                    {
                        skipped.Add(eo);
                        if (next == eo) break;
                        off = next;
                        continue;
                    }
                    entities.Add(ReadEntity(bytes, eo, cx, cz, flags, next, defaults));
                    if (next == eo) break;
                    off = next;
                }
            }
        }
        bool cut = visited.Count >= maxBlocks;
        var ents = entities.ToImmutable();
        return new MapFile(bytes[..GridOffset].ToArray(), w, h, grid, ReadNames(bytes, ents),
            defaults.ToImmutableDictionary(), ents, skipped.ToImmutable(), cut);
    }

    static MapEntity ReadEntity(ReadOnlySpan<byte> bytes, int eo, int cx, int cz, byte flags, uint next,
        Dictionary<int, MapDefault> defaults)
    {
        long sp = U32(bytes, eo + 21);
        if (sp <= 0 || sp + 16 > bytes.Length)
            sp = eo + HeadLength;
        int sub = (int)sp;
        int subLen = Math.Max(0, Math.Min(SubRecordLength(flags), bytes.Length - sub));

        int pitch = 0, yaw = 0, roll = 0, nameIndex = -1, hp = 0, sub10 = 0, own = 0, link = 0;
        bool usesDefaults = false;
        MapLinkRecord? rec = null;
        int light = -1, lightEnable = 0;
        int sprite = -1, marker = -1, enemy = -1, p16 = 0, p8 = 0;
        bool convoy = false;
        int state = 0, linkNext = 0, act = 0;
        switch (flags & 3)
        {
            case 1:
                pitch = S32(bytes, sub);
                yaw = S32(bytes, sub + 4);
                roll = S32(bytes, sub + 8);
                nameIndex = U16(bytes, sub + 12);
                if ((long)sub + 0x17 <= bytes.Length)
                {
                    hp = U16(bytes, sub + 0x0E);
                    sub10 = U16(bytes, sub + 0x10);
                    state = bytes[sub + 0x12];
                    own = link = S32(bytes, sub + 0x13);
                    if (link < 1 && (flags & 0x40) == 0 && defaults.TryGetValue(nameIndex, out var d))
                    {
                        hp = d.HitPoints;
                        state = d.StateByte;
                        link = d.LinkOffset;
                        usesDefaults = true;
                    }
                    if (link > 0 && (long)link + MapLinkRecord.Size <= bytes.Length)
                    {
                        rec = new MapLinkRecord(link, bytes[link], BinaryPrimitives.ReadInt16LittleEndian(bytes[(link + 1)..]),
                            U16(bytes, link + 3), S32(bytes, link + 5), bytes[link + 9]);
                        act = rec.Value.ActType;
                        linkNext = rec.Value.Next;
                    }
                }
                break;
            case 2:
                light = U16(bytes, sub);
                lightEnable = U16(bytes, sub + 8);
                if ((long)sub + 8 <= bytes.Length)
                {
                    state = bytes[sub + 2];
                    act = bytes[sub + 3];
                    linkNext = S32(bytes, sub + 4);
                }
                break;
            case 3:
                sprite = U16(bytes, sub);
                if (sprite >> 7 == MarkerBank)
                {
                    marker = sprite & 0x7F;
                    int t = sub + 10 < bytes.Length ? bytes[sub + 10] : 0;
                    enemy = t & 0x7F;
                    convoy = (t & 0x80) != 0;
                }
                p16 = U16(bytes, sub + 2);
                p8 = sub + 4 < bytes.Length ? bytes[sub + 4] : 0;
                if ((long)sub + 11 <= bytes.Length)
                {
                    state = bytes[sub + 5];
                    linkNext = S32(bytes, sub + 6);
                    act = bytes[sub + 10];
                }
                break;
        }
        return new MapEntity
        {
            FileOffset = eo,
            CellX = cx,
            CellZ = cz,
            X = S32(bytes, eo + 8),
            Y = S32(bytes, eo + 12),
            Z = S32(bytes, eo + 16),
            Flags = flags,
            NextInCell = next,
            Word4 = U32(bytes, eo + 4),
            SubRecordOffset = sub,
            Block = [.. bytes.Slice(eo, BlockLength(flags))],
            SubRecord = [.. bytes.Slice(sub, subLen)],
            Pitch = pitch,
            Yaw = yaw,
            Roll = roll,
            NameIndex = nameIndex,
            HitPoints = hp,
            Sub10 = sub10,
            OwnLinkOffset = own,
            LinkOffset = link,
            Link = rec,
            UsesDefaults = usesDefaults,
            LightIntensity = light,
            LightEnable = lightEnable,
            SpriteIndex = sprite,
            MarkerType = marker,
            EnemyType = enemy,
            Convoy = convoy,
            Param16 = p16,
            Param8 = p8,
            StateByte = state,
            LinkNext = linkNext,
            ActType = act,
        };
    }

    /// <summary>
    /// max(name index) + 1 slots of the variant-1 entities (indices below 1024). A slot that does
    /// not start with a letter or digit, or holds a non-name character before its NUL, is "".
    /// </summary>
    static ImmutableArray<string> ReadNames(ReadOnlySpan<byte> bytes, ImmutableArray<MapEntity> entities)
    {
        int count = 0;
        foreach (var e in entities)
            if (e.Variant == MapEntityVariant.Mesh && e.NameIndex >= 0 && e.NameIndex < 1024)
                count = Math.Max(count, e.NameIndex + 1);
        var names = ImmutableArray.CreateBuilder<string>(count);
        int off = NamesOffset;
        for (int i = 0; i < count && off + NameLength <= GridOffset; i++, off += NameLength)
            names.Add(SlotName(bytes.Slice(off, NameLength)));
        return names.ToImmutable();
    }

    /// <summary>The name in one 8-byte slot (cut at the first NUL), or "" when it does not look like one.</summary>
    internal static string SlotName(ReadOnlySpan<byte> slot)
    {
        if (!IsAlnum(slot[0]))
            return "";
        int n = 0;
        for (; n < slot.Length && slot[n] != 0; n++)
        {
            byte c = slot[n];
            if (!IsAlnum(c) && c != '_' && c != '-' && c != '.')
                return "";
        }
        return Encoding.ASCII.GetString(slot[..n]);
    }

    static bool IsAlnum(byte c) => c is >= (byte)'0' and <= (byte)'9' or >= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z';

    static uint U32(ReadOnlySpan<byte> b, long off) => off >= 0 && off + 4 <= b.Length ? BinaryPrimitives.ReadUInt32LittleEndian(b[(int)off..]) : 0;
    static int S32(ReadOnlySpan<byte> b, long off) => (int)U32(b, off);
    static int U16(ReadOnlySpan<byte> b, long off) => off >= 0 && off + 2 <= b.Length ? BinaryPrimitives.ReadUInt16LittleEndian(b[(int)off..]) : 0;
}
