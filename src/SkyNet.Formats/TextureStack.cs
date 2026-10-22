using System.Buffers.Binary;

namespace SkyNet.Formats;

/// <summary>
/// The TEXTURE.NNN records as one image each, the way the game uploads them and the hi-res tools
/// read them (tools/SkyNet.Tables <c>textures</c>, <c>SkyNet.Game.Mods.HiresTextures</c>): an
/// animated record has its frames stacked downwards, each centred in the largest frame's box.
/// Flat-colour records (1 x 1) and empty ones are left out.
/// </summary>
public static class TextureStack
{
    public const int MaxBank = 999;

    /// <summary>One record: frame size, frame count, the stacked palette indices, the record's
    /// frame time and whether it is a sprite (layout B) rather than a wall (layout A).</summary>
    public sealed record Record(int Bank, int Index, int Width, int Height, int Frames, byte[] Pixels, int MsPerFrame, bool Sprite)
    {
        /// <summary>Index 0 (a hole) occurs.</summary>
        public bool Holes => Array.IndexOf(Pixels, (byte)0) >= 0;
    }

    /// <summary>Every record of one bank file's bytes, in record order.</summary>
    public static IEnumerable<Record> Records(int bank, byte[] bytes)
    {
        if (TextureBank.Parse(bytes) is not { } parsed)
        {
            yield break;
        }

        for (var r = 0; r < parsed.Records.Length; r++)
        {
            var rec = parsed.Records[r];
            if (rec.IsEmpty || (rec.Width == 1 && rec.Height == 1))
            {
                continue;
            }

            var (w, h, frames, pixels) = Stacked(bytes, r, rec);
            yield return new Record(bank, r, w, h, frames, pixels, rec.MsPerFrame, IsSprite(bytes, r, rec));
        }
    }

    /// <summary>The record's frames stacked downwards in the largest frame's box (one frame: as is).</summary>
    public static (int W, int H, int Frames, byte[] Pixels) Stacked(byte[] bytes, int record, TextureRecord rec)
    {
        var frames = rec.Depth > 1 ? TextureBank.ParseRecordFrames(bytes, record).Where(f => !f.IsEmpty).ToArray() : [];
        if (frames.Length <= 1)
        {
            return (rec.Width, rec.Height, 1, rec.Pixels.ToArray());
        }

        int w = frames.Max(f => f.Width), h = frames.Max(f => f.Height);
        var pixels = new byte[w * h * frames.Length];
        for (var k = 0; k < frames.Length; k++)
        {
            var f = frames[k];
            var (ox, oy) = ((w - f.Width) / 2, (h - f.Height) / 2);
            for (var y = 0; y < f.Height; y++)
            {
                f.Pixels.AsSpan().Slice(y * f.Width, f.Width).CopyTo(pixels.AsSpan((k * h + oy + y) * w + ox, f.Width));
            }
        }

        return (w, h, frames.Length, pixels);
    }

    /// <summary>Layout B (row gap 0, not a 256-wide wall): a sprite (texture.md).</summary>
    public static bool IsSprite(byte[] bytes, int record, TextureRecord rec)
    {
        var ro = 28 + record * 20;
        var desc = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(ro));
        if (desc == 0 || desc + 28 > bytes.Length)
        {
            return false;
        }

        var rowGap = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan((int)desc + 18));
        return rowGap == 0 && !(rec.Width == 256 && rec.Depth <= 1);
    }
}
