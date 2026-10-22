using System.Text;

namespace SkyNet.Formats.Tests;

public class BsaTests
{
    static byte[] Toc(string name, byte flags, uint size)
    {
        var e = new byte[18];
        Encoding.ASCII.GetBytes(name).CopyTo(e, 0);
        e[13] = flags;
        BitConverter.GetBytes(size).CopyTo(e, 14);
        return e;
    }

    static byte[] Archive(params (string Name, byte Flags, byte[] Data)[] items)
    {
        var b = new Bytes().U16(items.Length);
        foreach (var it in items) b.Raw(it.Data);
        foreach (var it in items) b.Raw(Toc(it.Name, it.Flags, (uint)it.Data.Length));
        return b.ToArray();
    }

    [Fact]
    public void Key_derivation_rejects_bad_passphrases()
    {
        Assert.True(BsaKey.Derive("").IsEmpty);
        Assert.True(BsaKey.Derive("abcZ").IsEmpty);   // 'Z' is not a digit
        Assert.True(BsaKey.Derive("ab-c").IsEmpty);
        Assert.Equal(21, BsaKey.Derive("kaJogANDrea").Length);
        Assert.True(BsaKey.For(BsaVariant.Installer).IsEmpty);
    }

    [Fact]
    public void Key_of_one_digit_is_its_base62_accumulation()
    {
        // "b" = digit 1: key = sum over 28 rounds of 62^k, i.e. the base-256 digits of (62^28 - 1) / 61.
        var key = BsaKey.Derive("b");
        var n = System.Numerics.BigInteger.Zero;
        for (int r = 0; r < 28; r++) n = n * 62 + 1;
        var expect = n.ToByteArray(isUnsigned: true, isBigEndian: false);
        var padded = new byte[21];
        expect.AsSpan(0, Math.Min(21, expect.Length)).CopyTo(padded);
        Assert.Equal(padded, key.ToArray());
    }

    [Fact]
    public void Reads_plain_and_encrypted_entries_with_running_offsets()
    {
        var plain = "HELLO WORLD, THIS IS LONGER THAN TWENTY-ONE BYTES"u8.ToArray();
        var key = BsaKey.For(BsaVariant.SkyNetFull).ToArray();
        var enc = plain.Select((x, i) => (byte)(x + key[i % 21])).ToArray();
        var file = Archive(("A.TXT", 0, [1, 2, 3]), ("B.TXT", 1, enc));
        using var a = BsaArchive.Open(new MemoryStream(file), BsaVariant.SkyNetFull);
        Assert.Equal(2, a.Entries.Length);
        Assert.Equal(2, a.Entries[0].Offset);
        Assert.Equal(5, a.Entries[1].Offset);
        Assert.Equal(new byte[] { 1, 2, 3 }, a.Read("a.txt"));
        Assert.Equal(plain, a.Read("B.TXT"));
        Assert.Null(a.Read("C.TXT"));
    }

    [Fact]
    public void Drops_unsafe_names_and_entries_past_the_end_but_keeps_offsets()
    {
        var file = Archive(("../X", 0, [9]), ("OK.BIN", 0, [7, 7]), ("OK.BIN", 0, [8]));
        using (var a = BsaArchive.Open(new MemoryStream(file), BsaVariant.SkyNetFull))
        {
            Assert.Equal(1, a.DroppedEntries);
            Assert.Equal(2, a.Entries.Length);
            Assert.Equal(3, a.Entries[0].Offset);           // the dropped entry still occupied its byte
            Assert.Equal(new byte[] { 7, 7 }, a.Read("ok.bin")); // first of a duplicated name wins
        }
        // An entry claiming more data than the file holds is left out.
        var big = Archive(("BIG.BIN", 0, [1, 2]));
        BitConverter.GetBytes(0xFFFFFFFFu).CopyTo(big, big.Length - 4);
        using var b = BsaArchive.Open(new MemoryStream(big), BsaVariant.SkyNetFull);
        Assert.Empty(b.Entries);
        Assert.Equal(1, b.DroppedEntries);
    }

    [Theory]
    [InlineData("SKYNET.COL", true)]
    [InlineData("T_1-2.X", true)]
    [InlineData(".HIDDEN", false)]
    [InlineData("-X", false)]
    [InlineData("A..B", false)]
    [InlineData("A.-B", false)]
    [InlineData("A/B", false)]
    [InlineData("", false)]
    public void Name_safety(string name, bool ok) => Assert.Equal(ok, BsaArchive.NameIsSafe(Encoding.ASCII.GetBytes(name)));

    [Fact]
    public void Bad_headers_throw()
    {
        Assert.Throws<InvalidDataException>(() => BsaArchive.Open(new MemoryStream([0]), BsaVariant.SkyNetFull));
        Assert.Throws<InvalidDataException>(() => BsaArchive.Open(new MemoryStream([0, 0, 0, 0]), BsaVariant.SkyNetFull));
        Assert.Throws<InvalidDataException>(() => BsaArchive.Open(new MemoryStream([5, 0, 0, 0]), BsaVariant.SkyNetFull));
    }
}
