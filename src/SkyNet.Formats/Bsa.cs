using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text;

namespace SkyNet.Formats;

/// <summary>The archive key set (CTPAX-X variant). The numbers match the Godot port and unfsnbsa.</summary>
public enum BsaVariant
{
    FutureShockDemo = 0,
    FutureShockFull = 1,
    SkyNetFull = 2,
    /// <summary>Installer archives; no passphrase, never decrypted.</summary>
    Installer = 3,
}

/// <summary>One table-of-contents entry. <see cref="Offset"/> is absolute in the file.</summary>
public readonly record struct BsaEntry(string Name, byte Flags, uint Size, long Offset)
{
    public const byte FlagEncrypted = 0x01;
    public bool Encrypted => (Flags & FlagEncrypted) != 0;
}

/// <summary>
/// The per-game archive keys: a 21-byte key derived from a passphrase by 28 rounds of base-62
/// accumulation, and the byte-wise subtraction that decrypts an entry.
/// </summary>
public static class BsaKey
{
    public const int Length = 21;

    static readonly string[] Passphrases =
    [
        "d1mOforVIrring",   // Future Shock demo
        "moRt1nErDej7lig",  // Future Shock full
        "kaJogANDrea",      // SkyNET
        "",                 // installer (not used)
    ];

    static readonly byte[]?[] Cache = new byte[]?[4];

    /// <summary>
    /// The 21-byte key of a passphrase. a-z map to 0..25, 0-9 to 26..35, A-Y to 36..60 ('Z' is
    /// not a digit). Empty for an empty passphrase or one with any other character.
    /// </summary>
    public static ImmutableArray<byte> Derive(string passphrase)
    {
        if (string.IsNullOrEmpty(passphrase))
            return [];
        var digits = new byte[passphrase.Length];
        for (int i = 0; i < passphrase.Length; i++)
        {
            char c = passphrase[i];
            if (c >= 'a' && c <= 'z') digits[i] = (byte)(c - 'a');
            else if (c >= '0' && c <= '9') digits[i] = (byte)(c - '0' + 26);
            else if (c >= 'A' && c <= 'Y') digits[i] = (byte)(c - 'A' + 36);
            else return [];
        }
        var key = new byte[Length];
        for (int r = 0; r < 28; r++)
        {
            int carry = digits[r % digits.Length];
            for (int i = 0; i < Length; i++)
            {
                int v = key[i] * 62 + carry;
                key[i] = (byte)v;
                carry = v >> 8;
            }
        }
        return ImmutableArray.Create(key);
    }

    /// <summary>The key of a variant; empty for <see cref="BsaVariant.Installer"/> or an unknown value.</summary>
    public static ImmutableArray<byte> For(BsaVariant variant)
    {
        int v = (int)variant;
        if (v < 0 || v >= Passphrases.Length)
            return [];
        var k = Volatile.Read(ref Cache[v]);
        if (k == null)
        {
            k = Derive(Passphrases[v]).ToArray();
            Volatile.Write(ref Cache[v], k);
        }
        return ImmutableArray.Create(k);
    }

    /// <summary>Decrypt in place: <c>data[i] -= key[i % 21]</c> (mod 256), counted from the entry start.</summary>
    public static void Decrypt(Span<byte> data, ReadOnlySpan<byte> key)
    {
        if (key.Length != Length)
            throw new ArgumentException($"key must be {Length} bytes", nameof(key));
        for (int i = 0; i < data.Length; i++)
            data[i] = (byte)(data[i] - key[i % Length]);
    }
}

/// <summary>
/// A BSA archive (XnGine, CTPAX-X layout): u16 entry count, the entries' data back to back from
/// offset 2, and an 18-byte-per-entry table of contents at the end of the file
/// (char name[13], u8 flags, u32 size). There is no offset field; entry <c>i</c> starts at
/// 2 + the sum of the sizes before it.
/// </summary>
/// <remarks>
/// As in the Godot port, an entry whose data would run past the end of the file, or whose name is
/// not a plain DOS name, is left out of <see cref="Entries"/> (the running offset still advances
/// past it). Names match case-insensitively and the first of a duplicated name wins.
/// Reads are serialised; one archive may be shared between threads.
/// </remarks>
public sealed class BsaArchive : IDisposable
{
    public const int NameLength = 13;
    public const int EntrySize = 18;
    public const int MaxEntries = 50000;

    readonly Stream _stream;
    readonly bool _leaveOpen;
    readonly Lock _lock = new();
    readonly Dictionary<string, BsaEntry> _byName;

    public BsaVariant Variant { get; }
    public ImmutableArray<BsaEntry> Entries { get; }
    /// <summary>Entries the table listed but that were left out (bad name, or data past the end).</summary>
    public int DroppedEntries { get; }

    BsaArchive(Stream stream, bool leaveOpen, BsaVariant variant, ImmutableArray<BsaEntry> entries, int dropped)
    {
        _stream = stream;
        _leaveOpen = leaveOpen;
        Variant = variant;
        Entries = entries;
        DroppedEntries = dropped;
        _byName = new Dictionary<string, BsaEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in entries)
            _byName.TryAdd(e.Name, e);
    }

    public static BsaArchive Open(string path, BsaVariant variant)
    {
        var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.RandomAccess);
        try { return Open(fs, variant, leaveOpen: false); }
        catch { fs.Dispose(); throw; }
    }

    /// <summary>Read the table of contents of a seekable stream. Throws <see cref="InvalidDataException"/> on a bad header.</summary>
    public static BsaArchive Open(Stream stream, BsaVariant variant, bool leaveOpen = false)
    {
        if (!stream.CanSeek || !stream.CanRead)
            throw new ArgumentException("BSA stream must be readable and seekable", nameof(stream));
        long fileSize = stream.Length;
        if (fileSize < 2)
            throw new InvalidDataException("BSA too small");
        Span<byte> hdr = stackalloc byte[2];
        stream.Position = 0;
        stream.ReadExactly(hdr);
        int n = BinaryPrimitives.ReadUInt16LittleEndian(hdr);
        if (n <= 0 || n > MaxEntries)
            throw new InvalidDataException($"BSA bad entry count {n}");
        int tocBytes = n * EntrySize;
        if (tocBytes + 2 > fileSize)
            throw new InvalidDataException("BSA table of contents overflows the file");
        var toc = new byte[tocBytes];
        stream.Position = fileSize - tocBytes;
        stream.ReadExactly(toc);
        var (entries, dropped) = ParseToc(toc, n, fileSize);
        return new BsaArchive(stream, leaveOpen, variant, entries, dropped);
    }

    /// <summary>Parse <paramref name="count"/> TOC entries against a file of <paramref name="fileSize"/> bytes.</summary>
    public static (ImmutableArray<BsaEntry> Entries, int Dropped) ParseToc(ReadOnlySpan<byte> toc, int count, long fileSize)
    {
        var list = ImmutableArray.CreateBuilder<BsaEntry>(count);
        int dropped = 0;
        long cursor = 2;
        for (int i = 0; i < count; i++)
        {
            var rec = toc.Slice(i * EntrySize, EntrySize);
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(rec[(NameLength + 1)..]);
            long offset = cursor;
            cursor += size;
            var nameBytes = rec[..NameLength];
            int nul = nameBytes.IndexOf((byte)0);
            if (nul >= 0) nameBytes = nameBytes[..nul];
            if (offset + size > fileSize || !NameIsSafe(nameBytes))
            {
                dropped++;
                continue;
            }
            list.Add(new BsaEntry(Encoding.ASCII.GetString(nameBytes), rec[NameLength], size, offset));
        }
        return (list.ToImmutable(), dropped);
    }

    /// <summary>
    /// A plain DOS name: letters, digits, '_', '.' and '-', where '.' and '-' may neither open the
    /// name nor follow a '.'. Every entry of the SkyNET and Future Shock archives passes.
    /// </summary>
    public static bool NameIsSafe(ReadOnlySpan<byte> name)
    {
        if (name.IsEmpty)
            return false;
        byte prev = (byte)'.';
        foreach (byte c in name)
        {
            bool ok = (c >= '0' && c <= '9') || (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || c == '_'
                || (prev != '.' && (c == '.' || c == '-'));
            if (!ok)
                return false;
            prev = c;
        }
        return true;
    }

    public bool TryFind(string name, out BsaEntry entry) => _byName.TryGetValue(name, out entry);

    public bool Contains(string name) => _byName.ContainsKey(name);

    /// <summary>The entry's bytes, decrypted when flagged; null when the name is absent or the entry is empty.</summary>
    public byte[]? Read(string name) => TryFind(name, out var e) ? Read(e) : null;

    /// <summary>The entry's bytes, decrypted when flagged; null for an empty entry.</summary>
    public byte[]? Read(in BsaEntry entry)
    {
        if (entry.Size == 0)
            return null;
        var data = new byte[entry.Size];
        lock (_lock)
        {
            _stream.Position = entry.Offset;
            _stream.ReadExactly(data);
        }
        if (entry.Encrypted)
        {
            var key = BsaKey.For(Variant);
            if (key.IsEmpty)
                throw new InvalidDataException($"BSA entry {entry.Name} is encrypted but variant {Variant} has no key");
            BsaKey.Decrypt(data, key.AsSpan());
        }
        return data;
    }

    public void Dispose()
    {
        if (!_leaveOpen)
            _stream.Dispose();
    }
}
