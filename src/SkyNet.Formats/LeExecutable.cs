using System.Buffers.Binary;
using System.Text;

namespace SkyNet.Formats;

/// <summary>One object (segment) of an LE image: where its bytes sit in the file and where they load.</summary>
public readonly record struct LeObject(int Number, uint VirtualSize, uint BaseVa, uint Flags, int FileOffset, int FileSize);

/// <summary>
/// The game's DOS/4G LE executable, read as a static image so the tables in it can be parsed.
/// SKYNET.EXE is a bound executable: DOS/4GW comes first, then the application's own MZ stub whose
/// e_lfanew (+0x3C) points at the "LE" header. The header's data-pages offset is relative to that
/// stub, not to the start of the file (for SkyNET v1.01: stub 0x352A4, object 2 = VA 0x30000 at
/// file 0x83AA4, so file = VA + 0x53AA4; v1.00 was VA + 0x538A4).
/// <para>
/// Pointers stored as data in object 2 are object-relative: the true VA is the raw dword + 0x30000
/// (the fixups add the object base at load time, the file holds the offset).
/// </para>
/// </summary>
public sealed class LeExecutable
{
    readonly byte[] _file;

    public int StubOffset { get; }
    public int HeaderOffset { get; }
    public IReadOnlyList<LeObject> Objects { get; }
    /// <summary>"1.01", "1.00", or null when no "SkyNet vX.YY" string is in the image.</summary>
    public string? SkyNetVersion { get; }

    LeExecutable(byte[] file, int stub, int header, List<LeObject> objects, string? version)
    {
        _file = file;
        StubOffset = stub;
        HeaderOffset = header;
        Objects = objects;
        SkyNetVersion = version;
    }

    /// <summary>Find the bound LE image; null when the bytes hold none.</summary>
    public static LeExecutable? Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 0x40 || bytes[0] != 'M' || bytes[1] != 'Z')
            return null;
        // Every MZ whose e_lfanew lands on "LE\0\0" is a candidate; the first one wins.
        for (int mz = 0; mz >= 0 && mz + 0x40 <= bytes.Length;)
        {
            if (bytes[mz] == 'M' && bytes[mz + 1] == 'Z')
            {
                long le = mz + (long)BinaryPrimitives.ReadUInt32LittleEndian(bytes[(mz + 0x3C)..]);
                if (le >= 0 && le + 0xB0 <= bytes.Length && bytes[(int)le] == 'L' && bytes[(int)le + 1] == 'E'
                    && bytes[(int)le + 2] == 0 && bytes[(int)le + 3] == 0)
                    return Build(bytes, mz, (int)le);
            }
            int next = bytes[(mz + 1)..].IndexOf("MZ"u8);
            mz = next < 0 ? -1 : mz + 1 + next;
        }
        return null;
    }

    static LeExecutable? Build(ReadOnlySpan<byte> b, int stub, int le)
    {
        var file = b.ToArray();
        uint U(int o) => BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(o));
        uint pageSize = U(le + 0x28);
        uint lastPage = U(le + 0x2C);
        uint objTab = U(le + 0x40);
        uint objCount = U(le + 0x44);
        uint pageCount = U(le + 0x14);
        long dataPages = stub + (long)U(le + 0x80);
        if (pageSize == 0 || objCount == 0 || objCount > 64)
            return null;
        var objects = new List<LeObject>();
        for (int i = 0; i < objCount; i++)
        {
            int o = le + (int)objTab + 24 * i;
            if (o + 24 > b.Length)
                return null;
            uint vsize = U(o), baseVa = U(o + 4), flags = U(o + 8), first = U(o + 12), count = U(o + 16);
            long off = dataPages + (first - 1) * (long)pageSize;
            long size = count * (long)pageSize;
            if (count > 0 && first + count - 1 == pageCount)
                size -= pageSize - lastPage;
            size = Math.Clamp(Math.Min(size, b.Length - off), 0, int.MaxValue);
            objects.Add(new LeObject(i + 1, vsize, baseVa, flags, (int)Math.Min(off, b.Length), (int)size));
        }
        string? version = null;
        int v = b.IndexOf("SkyNet v"u8);
        if (v >= 0 && v + 12 <= b.Length)
            version = Encoding.ASCII.GetString(b.Slice(v + 8, 4));
        return new LeExecutable(file, stub, le, objects, version);
    }

    /// <summary>File offset of a VA inside an object's loaded bytes; -1 when outside every object's file data.</summary>
    public int FileOffsetOf(uint va, int size = 1)
    {
        foreach (var o in Objects)
        {
            if (va >= o.BaseVa && va - o.BaseVa + (uint)size <= (uint)o.FileSize)
                return o.FileOffset + (int)(va - o.BaseVa);
        }
        return -1;
    }

    public bool Contains(uint va, int size = 1) => FileOffsetOf(va, size) >= 0;

    public ReadOnlySpan<byte> Bytes(uint va, int size)
    {
        int o = FileOffsetOf(va, size);
        if (o < 0)
            throw new ArgumentOutOfRangeException(nameof(va), $"VA 0x{va:X} is outside the image");
        return _file.AsSpan(o, size);
    }

    public uint U32(uint va) => BinaryPrimitives.ReadUInt32LittleEndian(Bytes(va, 4));
    public int I32(uint va) => BinaryPrimitives.ReadInt32LittleEndian(Bytes(va, 4));
    public ushort U16(uint va) => BinaryPrimitives.ReadUInt16LittleEndian(Bytes(va, 2));

    /// <summary>NUL-terminated Latin-1 string at a VA (at most 256 bytes).</summary>
    public string CString(uint va)
    {
        int o = FileOffsetOf(va);
        if (o < 0)
            return "";
        var s = _file.AsSpan(o, Math.Min(256, _file.Length - o));
        int n = s.IndexOf((byte)0);
        return Encoding.Latin1.GetString(n < 0 ? s : s[..n]);
    }
}
