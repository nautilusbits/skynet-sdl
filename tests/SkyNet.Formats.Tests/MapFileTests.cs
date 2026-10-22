namespace SkyNet.Formats.Tests;

public class MapFileTests
{
    /// <summary>A 2 × 1 grid map; <paramref name="body"/> is appended after the grid at 0x2544.</summary>
    static Bytes Header(uint cell0, uint cell1, uint outdoor = 2, int defaultsHead = 0)
    {
        var b = new Bytes().U32(0).U32(2).U32(1).U32(MapFile.GridOffset).U32(0xFFFFFFFF);
        b.Raw("WALL"u8).Zeros(4);            // slot 0
        b.Raw("PC"u8).U8(0).Raw("ZZYYX"u8);  // slot 1: short name, stale bytes after the NUL
        b.Raw("XXXXXXXX"u8);                 // slot 2: past the table, but looks like text
        b.Raw(" junk.."u8).U8(0);            // slot 3: not a name
        b.PadTo(MapFile.DefaultsHeadOffset).U32((uint)defaultsHead).Zeros(4).U32(outdoor);
        b.PadTo(MapFile.GridOffset).U32(cell0).U32(cell1);
        return b;
    }

    static Bytes Head(Bytes b, uint next, int x, int y, int z, int flags)
    {
        uint sub = (uint)(b.Length + MapFile.HeadLength);
        return b.U32(next).U32(0).U32((uint)x).U32((uint)y).U32((uint)z).U8(flags).U32(sub);
    }

    [Fact]
    public void Walks_variants_defaults_names_and_a_trailing_marker()
    {
        const int e1 = 0x2544;              // variant 1, 48 bytes
        const int e2 = e1 + 48;             // variant 2, 35 bytes
        const int link = e2 + 35;           // 10-byte link record
        const int def = link + 10;          // 13-byte defaults node
        const int e3 = def + 13;            // variant 1 without a link: takes the default
        const int skip = e3 + 48;           // flag 0x08, skipped
        const int e4 = skip + 36;           // the last block, a 36-byte marker heading cell 1

        var b = Header(e1, skip, outdoor: 1, defaultsHead: def);
        Head(b, e2, 100, -200, 300, 1).U32(0x800 + 512).U32(1024).U32(0x7FF).U16(1).U16(40).U16(0xBEEF).U8(6).U32(link);
        Head(b, e3, 5, 6, 7, 2).U16(300).U8(1).U8(0x5f).U32((uint)e1).U16(120);
        b.U8(3).U16(unchecked((ushort)-50)).U16(0).U32((uint)e2).U8(0xF1);
        b.U32(0).U16(0).U32((uint)link).U16(77).U8(4);
        Head(b, 0xFFFFFFFE, 8, 9, 10, 1).Zeros(12).U16(0).U16(0).U16(0).U8(0).U32(0xFFFFFFFE);
        Head(b, e4, 0, 0, 0, 0x08 | 3).Zeros(11);
        Head(b, 0, 11, 12, 13, 3).U16(299 * 128 + 2).U16(25).U8(9).U8(0).U32(0).U8(0x80 | 46);
        Assert.Equal(e4 + 36, b.Length);

        var m = MapFile.Parse(b.ToArray())!;
        Assert.True(m.IsOutdoor);
        Assert.Equal((2, 1), (m.GridWidth, m.GridHeight));
        Assert.Equal(4, m.Entities.Length);
        Assert.Equal([skip], m.SkippedOffsets.ToArray());
        Assert.Equal(["WALL", "PC"], m.Names.ToArray());   // max(name index) + 1 = 2 slots

        var a = m.Entities[0];
        Assert.Equal((MapEntityVariant.Mesh, 100, -200, 300), (a.Variant, a.X, a.Y, a.Z));
        Assert.Equal(Math.PI / 2, a.PitchRadians, 9);
        Assert.Equal(Math.PI, a.YawRadians, 9);
        Assert.Equal("PC", m.EntityName(a));
        Assert.Equal((40, 0xBEEF, 6, link, false), (a.HitPoints, a.Sub10, a.StateByte, a.LinkOffset, a.UsesDefaults));
        Assert.Equal(new MapLinkRecord(link, 3, -50, 0, e2, 0xF1), a.Link);
        Assert.Equal((0xF1, e2), (a.ActType, a.LinkNext));
        Assert.Equal(48, a.Block.Length);
        Assert.Equal(23, a.SubRecord.Length);

        var l = m.Entities[1];
        Assert.Equal((MapEntityVariant.Light, 300, 120, 1, 0x5f, e1), (l.Variant, l.LightIntensity, l.LightEnable, l.StateByte, l.ActType, l.LinkNext));

        var d = m.Entities[2];
        Assert.Equal((true, 77, 4, link, -2), (d.UsesDefaults, d.HitPoints, d.StateByte, d.LinkOffset, d.OwnLinkOffset));
        Assert.Equal("WALL", m.EntityName(d));

        var mk = m.Entities[3];
        Assert.Equal((1, 0), (mk.CellX, mk.CellZ));
        Assert.True(mk.IsMarker);
        Assert.Equal((2, 46, true, 25, 9), (mk.MarkerType, mk.EnemyType, mk.Convoy, mk.Param16, mk.Param8));
        Assert.True(m.TryGetEntityAt(e2, out var back) && back == l);
    }

    [Fact]
    public void A_cycle_is_walked_once()
    {
        const int a = 0x2544, b2 = a + 36;
        var b = Header(a, 0);
        Head(b, b2, 1, 0, 0, 3).Zeros(11);
        Head(b, a, 2, 0, 0, 3).Zeros(11);
        var m = MapFile.Parse(b.ToArray())!;
        Assert.Equal(2, m.Entities.Length);
        Assert.Empty(m.Names);
    }

    [Fact]
    public void Rejects_bad_headers()
    {
        Assert.Null(MapFile.Parse(new byte[100]));
        var ok = Header(0, 0).ToArray();
        Assert.NotNull(MapFile.Parse(ok));
        var bad = (byte[])ok.Clone();
        bad[16] = 0;
        Assert.Null(MapFile.Parse(bad));
        Assert.Null(MapFile.Parse(ok.AsSpan(0, ok.Length - 1)));   // grid cut off
    }

    [Theory]
    [InlineData("PC\0ZZYYX", "PC")]
    [InlineData("TREE01\0\0", "TREE01")]
    [InlineData("A_B-C.D\0", "A_B-C.D")]
    [InlineData("_ABC\0\0\0\0", "")]
    [InlineData("AB C\0\0\0\0", "")]
    [InlineData("ABCDEFGH", "ABCDEFGH")]
    public void Slot_names(string slot, string name) =>
        Assert.Equal(name, MapFile.SlotName(System.Text.Encoding.ASCII.GetBytes(slot)));

    [Fact]
    public void Block_lengths() =>
        Assert.Equal([36, 48, 35, 36], new[] { 0, 1, 2, 3 }.Select(MapFile.BlockLength));
}
