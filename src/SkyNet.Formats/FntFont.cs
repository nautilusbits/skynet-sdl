using System.Buffers.Binary;
using System.Collections.Immutable;

namespace SkyNet.Formats;

/// <summary>One entry of a font's glyph table: the file offset of its rows and its width in pixels.</summary>
public readonly record struct FntGlyph(int DataOffset, int Width);

/// <summary>
/// FONT00NN.FNT, the XnGine 1-bit bitmap fonts (every shipped file is 8644 bytes). Ported from the
/// Godot port's fnt_font.gd, without its atlas: a header <c>u16 em width, u16 line height</c>, then
/// 240 entries <c>{u16 data offset, u16 width}</c> for codepoints 0x21..0x110, then the glyphs, each
/// <c>line height</c> rows of one u16 (32-byte stride); column <c>c</c> is set when bit
/// <c>15 - c</c> of the row word is 1. Space has no glyph; its advance is the em width (at least 2).
/// </summary>
public sealed class FntFont
{
    public const int GlyphCount = 240;
    public const int FirstCodepoint = 0x21;
    public const int HeaderSize = 4 + GlyphCount * 4;
    public const int MaxWidth = 16;
    public const int MaxLineHeight = 16;

    readonly byte[] _bytes;

    public int EmWidth { get; }
    public int LineHeight { get; }
    /// <summary>The 240 table entries; index = codepoint − 0x21.</summary>
    public ImmutableArray<FntGlyph> Glyphs { get; }

    FntFont(byte[] bytes, int em, int lineHeight, ImmutableArray<FntGlyph> glyphs)
    {
        _bytes = bytes;
        EmWidth = em;
        LineHeight = lineHeight;
        Glyphs = glyphs;
    }

    /// <summary>
    /// Parse a font; null when shorter than the table, the line height is 0 or above 16, or a glyph
    /// is wider than 16 (a row is one u16, so a wider entry is corrupt).
    /// </summary>
    public static FntFont? Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < HeaderSize)
            return null;
        int lineH = BinaryPrimitives.ReadUInt16LittleEndian(bytes[2..]);
        if (lineH <= 0 || lineH > MaxLineHeight)
            return null;
        var glyphs = ImmutableArray.CreateBuilder<FntGlyph>(GlyphCount);
        for (int i = 0; i < GlyphCount; i++)
        {
            int e = 4 + i * 4;
            int w = BinaryPrimitives.ReadUInt16LittleEndian(bytes[(e + 2)..]);
            if (w > MaxWidth)
                return null;
            glyphs.Add(new FntGlyph(BinaryPrimitives.ReadUInt16LittleEndian(bytes[e..]), w));
        }
        return new FntFont(bytes.ToArray(), BinaryPrimitives.ReadUInt16LittleEndian(bytes), lineH, glyphs.MoveToImmutable());
    }

    /// <summary>The advance of a space: the em width, at least 2 (as the Godot port synthesises it).</summary>
    public int SpaceAdvance => Math.Max(EmWidth, 2);

    /// <summary>Table index of a codepoint, or −1 outside 0x21..0x110.</summary>
    public static int IndexOf(int codepoint)
    {
        int i = codepoint - FirstCodepoint;
        return i is >= 0 and < GlyphCount ? i : -1;
    }

    /// <summary>
    /// The table index used to draw <paramref name="codepoint"/>: a lower-case letter whose slot is
    /// empty (width 0) borrows its capital, as the Godot port does; −1 when there is no entry.
    /// </summary>
    public int ResolveIndex(int codepoint)
    {
        int i = IndexOf(codepoint);
        if (i < 0)
            return -1;
        if (codepoint is >= 'a' and <= 'z' && Glyphs[i].Width == 0)
            return IndexOf(codepoint - 0x20);
        return i;
    }

    /// <summary>Pixels a glyph advances: its width + 1, or <see cref="SpaceAdvance"/> for a space; 0 when absent.</summary>
    public int Advance(int codepoint)
    {
        if (codepoint == ' ')
            return SpaceAdvance;
        int i = ResolveIndex(codepoint);
        return i < 0 ? 0 : Glyphs[i].Width + 1;
    }

    /// <summary>Row word <paramref name="row"/> of glyph <paramref name="index"/>; 0 when the row lies past the end of the file.</summary>
    public int RowWord(int index, int row)
    {
        int off = Glyphs[index].DataOffset + row * 2;
        return off + 1 < _bytes.Length ? BinaryPrimitives.ReadUInt16LittleEndian(_bytes.AsSpan(off)) : 0;
    }

    public bool Pixel(int index, int x, int y) =>
        x >= 0 && x < Glyphs[index].Width && y >= 0 && y < LineHeight && ((RowWord(index, y) >> (15 - x)) & 1) != 0;

    /// <summary>Glyph <paramref name="index"/> as width × line height bytes, 1 = set, row-major.</summary>
    public byte[] GlyphBitmap(int index)
    {
        int w = Glyphs[index].Width;
        var px = new byte[w * LineHeight];
        for (int y = 0; y < LineHeight; y++)
        {
            int word = RowWord(index, y);
            for (int x = 0; x < w; x++)
                px[y * w + x] = (byte)((word >> (15 - x)) & 1);
        }
        return px;
    }

    /// <summary>Width in pixels of a string drawn glyph after glyph (advances summed).</summary>
    public int MeasureWidth(string text)
    {
        int w = 0;
        foreach (char c in text)
            w += Advance(c);
        return w;
    }
}
