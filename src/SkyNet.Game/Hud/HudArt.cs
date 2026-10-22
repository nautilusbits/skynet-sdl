using System.Numerics;
using RetroEng.Render;
using SkyNet.Formats;

namespace SkyNet.Game.Hud;

/// <summary>An image on the GPU (index texture) with its pixels kept for the CPU side (reticle shape).</summary>
public sealed record HudImage(TextureHandle Handle, int Width, int Height, byte[] Pixels, bool FromHires)
{
    public Vector2 Size => new(Width, Height);
}

/// <summary>
/// The HUD's art for one <see cref="HudArtSet"/>: .IMG and .CFA images read from MDMDHRES.BSA
/// first in the hi-res set (as SKYNET.EXE /g does through its MDMDHRES handle), MDMDIMGS.BSA
/// otherwise and for anything the hi-res archive lacks, made into index textures once each,
/// plus the game palette and a one-colour mask palette (index 1 white) for tinted masks.
/// </summary>
public sealed class HudArt : IDisposable
{
    private readonly Renderer _renderer;
    private readonly GameData _data;
    private readonly Dictionary<string, HudImage?> _images = [];
    private readonly Dictionary<string, HudImage?> _masks = [];
    private readonly Dictionary<string, HudImage[]> _cels = [];
    private readonly List<TextureHandle> _owned = [];
    private readonly Palette _palette;

    public HudArtSet Set { get; }
    public TextureHandle Palette { get; }
    public TextureHandle MaskPalette { get; }

    public HudArt(Renderer renderer, GameData data, HudArtSet set)
    {
        _renderer = renderer;
        _data = data;
        Set = set;
        _palette = data.LoadPalette() ?? throw new InvalidDataException("No game palette (SKYNET.COL) in the data folder.");
        var rgba = new byte[256 * 4];
        for (var i = 0; i < 256; i++)
        {
            var (r, g, b) = _palette[i];
            (rgba[i * 4], rgba[i * 4 + 1], rgba[i * 4 + 2], rgba[i * 4 + 3]) = (r, g, b, 255);
        }

        Palette = Own(renderer.CreatePalette(rgba));
        var mask = new byte[256 * 4];
        mask[4] = mask[5] = mask[6] = mask[7] = 255;
        MaskPalette = Own(renderer.CreatePalette(mask));
    }

    /// <summary>A palette index's colour (opaque) as a tint.</summary>
    public Vector4 Colour(int index)
    {
        var (r, g, b) = _palette[index & 0xFF];
        return new Vector4(r / 255f, g / 255f, b / 255f, 1f);
    }

    /// <summary>An .IMG of the set; null when neither archive has it.</summary>
    public HudImage? Image(string name)
    {
        // A handle a renderer scope let go (made while a level's scope was open) is made again.
        if (!_images.TryGetValue(name, out var image) || image is { } old && !_renderer.Exists(old.Handle))
        {
            image = Read(name) is { } src && ImgFile.Parse(src.Bytes) is { } img
                ? Make(img.Width, img.Height, [.. img.Pixels], src.Hires)
                : null;
            _images[name] = image;
        }

        return image;
    }

    /// <summary>An .IMG as a mask: every non-zero pixel index 1 (drawn with <see cref="MaskPalette"/>, tinted).</summary>
    public HudImage? Mask(string name)
    {
        if (!_masks.TryGetValue(name, out var mask) || mask is { } old && !_renderer.Exists(old.Handle))
        {
            mask = null;
            if (Image(name) is { } img)
            {
                var px = img.Pixels.Select(p => p != 0 ? (byte)1 : (byte)0).ToArray();
                mask = Make(img.Width, img.Height, px, img.FromHires);
            }

            _masks[name] = mask;
        }

        return mask;
    }

    /// <summary>A .CFA's frames (either layout); empty when absent.</summary>
    public HudImage[] Cel(string name)
    {
        if (!_cels.TryGetValue(name, out var frames) || frames.Length > 0 && !_renderer.Exists(frames[0].Handle))
        {
            frames = Read(name) is { } src && CfaFile.Parse(src.Bytes) is { } cfa
                ? [.. cfa.Frames.Select(f => Make(cfa.Width, cfa.Height, [.. f], src.Hires))]
                : [];
            _cels[name] = frames;
        }

        return frames;
    }

    /// <summary>An .IMG of the 640 × 480 set (MDMDHRES.BSA) whatever the set in use, its pixels only (no
    /// texture: the vehicle reticle's shape); null when the archive or the image is missing.</summary>
    public ImgFile? HiresImage(string name) =>
        ReadFrom(_data.HiresArchive(), name) is { } bytes ? ImgFile.Parse(bytes) : null;

    /// <summary>The weapon icon of DOS record <paramref name="record"/> (WEAPONnn.IMG).</summary>
    public HudImage? WeaponIcon(int record) => record < 0 ? null : Image($"WEAPON{record:D2}.IMG");

    private (byte[] Bytes, bool Hires)? Read(string name)
    {
        if (Set == HudArtSet.HiRes && ReadFrom(_data.HiresArchive(), name) is { } hi)
        {
            return (hi, true);
        }

        return ReadFrom(_data.FrontArchive("MDMDIMGS.BSA"), name) is { } lo ? (lo, false) : null;
    }

    private static byte[]? ReadFrom((string Path, BsaVariant Variant) archive, string name)
    {
        if (!File.Exists(archive.Path))
        {
            return null;
        }

        using var bsa = BsaArchive.Open(archive.Path, archive.Variant);
        return bsa.Read(name);
    }

    private HudImage Make(int w, int h, byte[] px, bool hires) => new(Own(_renderer.CreateIndexTexture(w, h, px)), w, h, px, hires);

    private TextureHandle Own(TextureHandle t)
    {
        _owned.Add(t);
        return t;
    }

    public void Dispose()
    {
        foreach (var t in _owned)
        {
            _renderer.Destroy(t);
        }

        _owned.Clear();
        _images.Clear();
        _masks.Clear();
        _cels.Clear();
    }
}
