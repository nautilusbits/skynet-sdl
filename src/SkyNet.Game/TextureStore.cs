using RetroEng.Diagnostics;
using RetroEng.Render;
using SkyNet.Formats;

namespace SkyNet.Game;

/// <summary>A TEXTURE.NNN record on the GPU: its index texture and the record's own size (the
/// divisor of a model face's texel coordinates). An animated record (a sprite-layout record with
/// several frames, descriptor +20) holds its <see cref="Frames"/> stacked downwards, each
/// <see cref="Width"/> × <see cref="Height"/>, shown <see cref="MsPerFrame"/> ms each.</summary>
public readonly record struct GpuTexture(TextureHandle Handle, int Width, int Height, int Frames = 1, int MsPerFrame = 0)
{
    /// <summary>The Godot port's rate for a record that says none (level_loader.gd SPRITE_ANIM_FPS 12).</summary>
    public const int FallbackMsPerFrame = 1000 / 12;

    public bool Animated => Frames > 1;

    /// <summary>The frame shown at <paramref name="ms"/> on the free-running clock, started
    /// <paramref name="phase"/> frames in (DOS: clock / ms % frames, FUN_00149e00).</summary>
    public int FrameAt(long ms, int phase = 0) =>
        Frames <= 1 ? 0 : (int)((ms / (MsPerFrame > 0 ? MsPerFrame : FallbackMsPerFrame) + phase) % Frames);
}

/// <summary>A TEXTURE.NNN record as a texture: bank, record and whether index 0 is drawn opaque.</summary>
public readonly record struct TextureRef(int Bank, int Record, bool Opaque);

/// <summary>
/// TEXTURE.NNN banks parsed once each, and their records made into index textures once each
/// (per opacity). Sprites keep index 0 transparent; opaque surfaces (models) have it drawn as
/// <see cref="HazeTable.HazeIndex"/>, which is what HAZE.000 makes of index 0 on every row, so
/// the result through the table is the same as DOS's. A record with several frames comes with
/// every frame (as the Godot port's sprite_anim does), each centred in the largest frame's box.
/// Without a renderer (the map baker) nothing is uploaded: each texture gets a stand-in handle
/// (index = its place in <see cref="Created"/> + 1) with the record's real size and frames.
/// </summary>
public sealed class TextureStore(Renderer? renderer, GameData data)
{
    private readonly List<TextureRef> _created = [];
    private readonly Dictionary<int, TextureBank?> _banks = [];
    private readonly Dictionary<int, byte[]?> _bankBytes = [];
    private readonly Dictionary<(int Bank, int Record, bool Opaque), GpuTexture?> _textures = [];
    /// <summary>Whole-bank textures (<see cref="TrackBankSurface"/>) and their bank.</summary>
    private readonly Dictionary<TextureHandle, int> _bankTextures = [];

    public TextureBank? Bank(int bank)
    {
        if (!_banks.TryGetValue(bank, out var b))
        {
            var bytes = BankBytes(bank);
            b = bytes != null ? TextureBank.Parse(bytes) : null;
            _banks[bank] = b;
        }

        return b;
    }

    private byte[]? BankBytes(int bank)
    {
        if (!_bankBytes.TryGetValue(bank, out var bytes))
        {
            bytes = data.ReadLoose($"TEXTURE.{bank:D3}");
            _bankBytes[bank] = bytes;
        }

        return bytes;
    }

    /// <summary>A bank's record (the index clamped to the bank, as the Godot port's provider does);
    /// null when the bank is missing or the record empty.</summary>
    public TextureRecord? Record(int bank, int record)
    {
        var b = Bank(bank);
        if (b == null || b.Records.IsEmpty)
        {
            return null;
        }

        var r = b.Records[Math.Clamp(record, 0, b.Records.Length - 1)];
        return r.IsEmpty ? null : r;
    }

    /// <summary>The record as an index texture (every frame of an animated one); null when there is none.</summary>
    public GpuTexture? Get(int bank, int record, bool opaque)
    {
        if (_textures.TryGetValue((bank, record, opaque), out var t))
        {
            return t;
        }

        var r = Record(bank, record);
        if (r != null)
        {
            var frames = r.Depth > 1 ? Frames(bank, record) : [];
            if (frames.Length > 1)
            {
                int w = 0, h = 0;
                foreach (var f in frames)
                {
                    (w, h) = (Math.Max(w, f.Width), Math.Max(h, f.Height));
                }

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

                if (opaque)
                {
                    OpaqueZero(pixels);
                }

                t = new GpuTexture(Upload(w, h * frames.Length, pixels), w, h, frames.Length, r.MsPerFrame);
            }
            else
            {
                var pixels = r.Pixels.ToArray();
                if (opaque)
                {
                    OpaqueZero(pixels);
                }

                t = new GpuTexture(Upload(r.Width, r.Height, pixels), r.Width, r.Height);
            }

            var stacked = t.Value.Height * t.Value.Frames;
            if (renderer != null && HdImage(bank, record, opaque, t.Value.Width, stacked) is { } hd && hd.Height * t.Value.Width == stacked * hd.Width)
            {
                // Relief (lit faces only) for a single frame: the colour texture's next layer.
                var pixels = PixelsOf(bank, record, opaque);
                renderer.ReplaceTexture(t.Value.Handle, hd.Width, hd.Height, hd.Rgba, t.Value.Frames == 1 ? hd.Relief : null, pixels);
                if (pixels == HdPixels.Released)
                {
                    Mods.ModContent.Handed(bank, record, opaque);
                }
            }

            ApplySurface(bank, record, t.Value.Handle);
            _created.Add(new TextureRef(bank, record, opaque));
        }

        _textures[(bank, record, opaque)] = t;
        return t;
    }

    /// <summary>A record's HD texture from the enabled mods (<see cref="Mods.ModContent"/>:
    /// <c>textures/TEXTURE.NNN/RRR.png</c>), premultiplied RGBA8 (the enhanced look's colour texture),
    /// when there is one whose width is a whole multiple of the record's <paramref name="width"/> and
    /// whose height a multiple of <paramref name="height"/> at that scale (an animated record's frames
    /// stacked downwards). Opaque: the image's alpha is ignored (index 0's texels carry the haze colour
    /// in the file). Decoded ahead on worker threads when the level asked for its set. With its relief
    /// layer (<see cref="Mods.HdTexture.Relief"/>) when the mod has one.</summary>
    public (int Width, int Height, byte[] Rgba, byte[]? Relief)? HdImage(int bank, int record, bool opaque, int width, int height)
    {
        if (Mods.ModContent.Texture(bank, record, opaque, width) is not { } hd)
        {
            return null;
        }

        if (hd.Width < width || hd.Width % width != 0 || hd.Height % (height * (hd.Width / width)) != 0)
        {
            Log.Warn("Textures", $"{Mods.ModContent.TexturePath(bank, record)}: {hd.Width} x {hd.Height} does not scale the record's {width} x {height}");
            return null;
        }

        HdCount++;
        return (hd.Width, hd.Height, hd.Rgba, hd.Relief);
    }

    /// <summary>Whether the renderer keeps a record's HD pixels once uploaded: a fire sprite meets a
    /// second palette (its flames cut out: Level.FireMaskPalette, a new one each map), so it keeps
    /// them; every other texture is drawn through one palette at one frame count.</summary>
    internal static HdPixels PixelsOf(int bank, int record, bool opaque) =>
        !opaque && Enhanced.EmissiveLamps.IsFire(bank << SpriteRecordBits | record) ? HdPixels.Kept : HdPixels.Released;

    /// <summary>A sprite's index: bank &lt;&lt; 7 | record.</summary>
    private const int SpriteRecordBits = 7;

    /// <summary>The enhanced look's material of a record's texture (<see cref="Mods.Materials"/>).</summary>
    private void ApplySurface(int bank, int record, TextureHandle handle) =>
        renderer?.SetSurface(handle, Mods.Materials.Current(data.Game).Resolve(bank, record));

    /// <summary>Gives <paramref name="handle"/> (a texture of a whole bank: the ground's atlas) the
    /// bank's material, now and at every <see cref="ApplySurfaces"/>.</summary>
    public void TrackBankSurface(TextureHandle handle, int bank)
    {
        _bankTextures[handle] = bank;
        renderer?.SetSurface(handle, Mods.Materials.Current(data.Game).ResolveBank(bank));
    }

    /// <summary>Sets every texture made its material again (the materials changed: an editor's edit).</summary>
    public void ApplySurfaces()
    {
        foreach (var ((bank, record, _), t) in _textures)
        {
            if (t is { } texture)
            {
                ApplySurface(bank, record, texture.Handle);
            }
        }

        foreach (var (handle, bank) in _bankTextures)
        {
            renderer?.SetSurface(handle, Mods.Materials.Current(data.Game).ResolveBank(bank));
        }
    }

    /// <summary>HD textures loaded so far.</summary>
    public int HdCount { get; private set; }

    private TextureHandle Upload(int width, int height, byte[] pixels) =>
        renderer != null ? renderer.CreateIndexTexture(width, height, pixels) : new TextureHandle(_created.Count + 1, 1);

    /// <summary>The textures made so far, in the order they were made (the GPU's handle order).</summary>
    public IReadOnlyList<TextureRef> Created => _created;

    /// <summary>The TEXTURE.NNN banks read so far (present or not).</summary>
    public IEnumerable<int> BanksRead => _bankBytes.Keys;

    /// <summary>Without a renderer: the texture a stand-in handle names.</summary>
    public TextureRef RefOf(TextureHandle handle) => _created[handle.Index - 1];

    /// <summary>Every decodable frame of a record (the index clamped as in <see cref="Record"/>).</summary>
    /// <summary>The width <see cref="Get"/> gives a record's texture (an animated record's widest
    /// frame): what its HD texture must be a whole multiple of. 0: no record.</summary>
    internal int RecordWidth(int bank, int record)
    {
        if (Record(bank, record) is not { } r)
        {
            return 0;
        }

        var frames = r.Depth > 1 ? Frames(bank, record) : [];
        return frames.Length > 1 ? frames.Max(f => f.Width) : r.Width;
    }

    private TextureRecord[] Frames(int bank, int record)
    {
        var bytes = BankBytes(bank);
        var b = Bank(bank);
        if (bytes == null || b == null || b.Records.IsEmpty)
        {
            return [];
        }

        var frames = TextureBank.ParseRecordFrames(bytes, Math.Clamp(record, 0, b.Records.Length - 1));
        return [.. frames.Where(f => !f.IsEmpty)];
    }

    /// <summary>Index 0 (discarded by the palette look) drawn as the haze colour instead.</summary>
    public static void OpaqueZero(Span<byte> pixels)
    {
        for (var i = 0; i < pixels.Length; i++)
        {
            if (pixels[i] == 0)
            {
                pixels[i] = HazeTable.HazeIndex;
            }
        }
    }

    public int TextureCount => _textures.Count;
}
