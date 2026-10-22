using System.Numerics;
using RetroEng.Diagnostics;
using RetroEng.Images;
using RetroEng.Models;
using RetroEng.Platform;
using RetroEng.Render;

namespace SkyNet.Game.Mods;

/// <summary>A replacement's material as the game draws it: an RGBA texture (premultiplied; the base
/// colour factor multiplied in) or, without a texture, a 1 × 1 of the factor. With
/// <see cref="Maps"/> the texture is <see cref="Renderer.SurfaceMapLayers"/> layers of one size
/// stacked downwards (<see cref="Material.SurfaceMaps"/>): base colour, normal, occlusion-roughness-
/// metallic, emission. A built surface makes its texture on first use and can let it go once on the
/// GPU (<see cref="ReleasePixels"/>): made again from its source if asked for later.</summary>
public sealed record ReplacementSurface(HdTexture Colour, bool DoubleSided)
{
    /// <summary>Makes the texture (null: given whole, never let go).</summary>
    private readonly Func<HdTexture>? _source;
    private HdTexture? _colour = Colour;
    /// <summary>The DETAIL <see cref="_colour"/> was made at (<see cref="ModContent.Detail"/>).</summary>
    private Detail _madeAt = ModContent.Detail;
    private readonly Lock _gate = new();

    /// <summary>A surface whose texture <paramref name="source"/> makes on first use.</summary>
    private ReplacementSurface(Func<HdTexture> source, bool doubleSided) : this((HdTexture)null!, doubleSided) => _source = source;

    /// <summary>The texture, made now when not made yet, let go, or made at another DETAIL.</summary>
    public HdTexture Colour
    {
        get
        {
            lock (_gate)
            {
                if (Stale())
                {
                    _colour = null;
                }

                if (_colour == null)
                {
                    _madeAt = ModContent.Detail;
                    _colour = _source!();
                }

                return _colour;
            }
        }
        init => _colour = value;
    }

    /// <summary>Whether the texture's pixels are made (reading <see cref="Colour"/> costs no decode).</summary>
    internal bool Made
    {
        get
        {
            lock (_gate)
            {
                return _colour != null && !Stale();
            }
        }
    }

    /// <summary>Made from a source at another DETAIL than now (a surface given whole never is).</summary>
    private bool Stale() => _source != null && _colour != null && _madeAt != ModContent.Detail;

    /// <summary>Lets the texture's pixels go (its colour texture is on the GPU); a surface given
    /// whole keeps them.</summary>
    internal void ReleasePixels()
    {
        lock (_gate)
        {
            if (_source != null)
            {
                _colour = null;
            }
        }
    }

    public GltfAlphaMode AlphaMode { get; init; }
    public bool Maps { get; init; }
    /// <summary>The emission layer's scale (<see cref="Material.Emission"/>).</summary>
    public float Emission { get; init; } = 1f;

    /// <summary>How it is drawn: opaque; cut out (the alpha-tested queue: its holes cut in the
    /// depth and shadow passes too, the cutoff moved to half cover in the texels); or blended (the
    /// transparent queue, back to front, depth tested, not written, no shadow). Two-sided ones keep
    /// both faces.</summary>
    public RenderState State
    {
        get
        {
            var cull = DoubleSided ? Cull.None : Cull.Back;
            return AlphaMode switch
            {
                GltfAlphaMode.Mask => new RenderState(Queues.AlphaTest, cull),
                GltfAlphaMode.Blend => RenderState.Blended with { Cull = cull },
                _ => new RenderState(Queues.Opaque, cull),
            };
        }
    }

    /// <summary>A part's material with this surface's state (two-sided, cut out, blended) and maps.</summary>
    public Material Apply(Material material) =>
        material with { State = State, SurfaceMaps = Maps, Emission = Maps ? Emission : material.Emission };

    /// <summary>
    /// This surface lit at least <paramref name="floor"/> (0..1) of its base colour wherever the
    /// scene's light leaves it dark: the base colour times the floor added to the emission layer
    /// (linear light; divided by <see cref="Emission"/>, which scales the layer). A surface without
    /// maps gets them: a flat normal, a rough non-metal (lit as the plain surface is). Floor 0: this.
    /// </summary>
    public ReplacementSurface WithLightFloor(float floor)
    {
        if (!(floor > 0f))
        {
            return this;
        }

        var strength = Maps && Emission > 0f ? Emission : 1f;
        return new ReplacementSurface(() => Floored(floor, strength), DoubleSided)
        {
            AlphaMode = AlphaMode, Maps = true, Emission = strength,
        };
    }

    /// <summary><see cref="WithLightFloor"/>'s texture; this surface's own pixels go after (made again if asked).</summary>
    private HdTexture Floored(float floor, float strength)
    {
        var layers = Renderer.SurfaceMapLayers;
        var (w, h) = Maps ? (Colour.Width, Colour.Height / layers) : (Colour.Width, Colour.Height);
        var size = w * h * 4;
        var rgba = new byte[size * layers];
        if (Maps)
        {
            Colour.Rgba.AsSpan(0, size * layers).CopyTo(rgba);
        }
        else
        {
            Colour.Rgba.AsSpan(0, size).CopyTo(rgba);
            NormalLayer(null, 1f, w, h, rgba.AsSpan(size, size));
            OrmLayer(null, 1f, null, 1f, 0f, w, h, rgba.AsSpan(size * 2, size));
            Fill(rgba.AsSpan(size * 3, size), 0, 0, 0, 255);
        }

        var share = floor / strength;
        var emission = rgba.AsSpan(size * 3, size);
        for (var i = 0; i < size; i += 4)
        {
            for (var c = 0; c < 3; c++)
            {
                emission[i + c] = ToSrgb(Srgb.ToLinearTable[emission[i + c]] + Srgb.ToLinearTable[rgba[i + c]] * share);
            }

            emission[i + 3] = 255;
        }

        ReleasePixels();
        return new HdTexture(w, h * layers, rgba);
    }

    /// <summary>A glTF tangent placed and packed in a vertex colour (<see cref="Material.SurfaceMaps"/>):
    /// xyz as rgb 0-255, the bitangent's sign as alpha (255: positive; a mirroring placement turns it).</summary>
    public static uint PackTangent(Vector4 tangent, Matrix4x4 placement, bool mirrored)
    {
        var t = Vector3.TransformNormal(new Vector3(tangent.X, tangent.Y, tangent.Z), placement);
        t = t.LengthSquared() > 1e-20f ? Vector3.Normalize(t) : Vector3.Zero;
        static byte Byte(float v) => (byte)Math.Clamp((int)MathF.Round((v * 0.5f + 0.5f) * 255f), 0, 255);
        var positive = tangent.W >= 0f != mirrored;
        return Vertex.Rgba(Byte(t.X), Byte(t.Y), Byte(t.Z), positive ? (byte)255 : (byte)0);
    }

    /// <summary>A glTF material's surface, its images decoded (PNG through SDL, JPEG by
    /// <see cref="Jpeg"/>); an image that does not decode is left out with a warning.</summary>
    public static ReplacementSurface Build(string path, GltfModel model, GltfMaterial m) => Build(path, model.Images, m);

    /// <summary>As above, the images given (an animated model's). Decoded on the texture's first use.</summary>
    public static ReplacementSurface Build(string path, IReadOnlyList<GltfImage> images, GltfMaterial m) =>
        new(() => Texture(path, images, m), m.DoubleSided)
        {
            AlphaMode = m.AlphaMode,
            Maps = m.Physical,
            Emission = m.EmissiveStrength,
        };

    /// <summary>Makes the textures of <paramref name="surfaces"/> on every core, ahead of a build that
    /// asks for them one by one (a map's load); those <paramref name="kept"/> holds on the GPU are skipped.</summary>
    public static void Warm(IEnumerable<ReplacementSurface> surfaces, KeptSurfaces? kept)
    {
        var todo = surfaces.Distinct(ReferenceEqualityComparer.Instance).Cast<ReplacementSurface>()
            .Where(s => kept?.Holds(s) != true).ToArray();
        RetroEng.Jobs.JobSystem.Shared.ParallelFor(todo.Length, 1, new RetroEng.Jobs.ActionFor(i => _ = todo[i].Colour));
    }

    /// <summary>A replacement's surfaces with those of its per-frame models.</summary>
    public static IEnumerable<ReplacementSurface> Of(ModelReplacement? r) =>
        r == null ? [] : r.Surfaces.Concat(r.FrameModels.SelectMany(f => f.Model.Surfaces));

    /// <summary>A glTF material's texture: its images decoded, at one size, layered.</summary>
    private static HdTexture Texture(string path, IReadOnlyList<GltfImage> images, GltfMaterial m)
    {
        // DETAIL's size: each image boxed down as it is decoded (HdDetail).
        var divisor = HdDetail.Divisor(ModContent.Detail);
        (int W, int H, byte[] Rgba)? Image(int? index, string what, Texels kind)
        {
            if (index is not { } i)
            {
                return null;
            }

            var image = images[i];
            if (Decode(image.Data) is { } decoded)
            {
                return HdDetail.Shrink(decoded, divisor, kind);
            }

            Log.Warn("Mods", $"{path}: material '{m.Name}': its {what} ({(image.MimeType.Length > 0 ? image.MimeType : "image")}) is neither a PNG nor a JPEG this reader takes; left out");
            return null;
        }

        var factor = m.BaseColour;
        var colour = Image(m.BaseColourImage, "base colour", m.AlphaMode == GltfAlphaMode.Opaque ? Texels.Opaque : Texels.Colour);
        var maps = m.Physical
            ? new[] { Image(m.NormalImage, "normal map", Texels.Normal), Image(m.MetallicRoughnessImage, "metallic-roughness map", Texels.Data),
                Image(m.OcclusionImage, "occlusion map", Texels.Data), Image(m.EmissiveImage, "emissive map", Texels.Opaque) }
            : [];

        // One size for every layer: the base colour's, else the largest map's.
        var (w, h) = colour is { } c ? (c.W, c.H) : (1, 1);
        if (colour == null)
        {
            foreach (var map in maps)
            {
                if (map is { } mm && (long)mm.W * mm.H > (long)w * h)
                {
                    (w, h) = (mm.W, mm.H);
                }
            }
        }

        var layers = m.Physical ? Renderer.SurfaceMapLayers : 1;
        (w, h) = FitTexture(w, h, layers);
        var size = w * h * 4;
        var rgba = new byte[size * layers];
        var base0 = rgba.AsSpan(0, size);
        if (colour is { } image)
        {
            Resize(image, w, h, base0);
            if (factor != Vector4.One)
            {
                // The factor is linear; the texels are sRGB: multiply in linear light.
                for (var i = 0; i < size; i += 4)
                {
                    base0[i] = ToSrgb(Srgb.ToLinearTable[base0[i]] * factor.X);
                    base0[i + 1] = ToSrgb(Srgb.ToLinearTable[base0[i + 1]] * factor.Y);
                    base0[i + 2] = ToSrgb(Srgb.ToLinearTable[base0[i + 2]] * factor.Z);
                    base0[i + 3] = (byte)Math.Clamp(MathF.Round(base0[i + 3] * factor.W), 0, 255);
                }
            }
        }
        else
        {
            Fill(base0, Srgb.FromLinear(factor.X), Srgb.FromLinear(factor.Y), Srgb.FromLinear(factor.Z),
                (byte)Math.Clamp(MathF.Round(factor.W * 255f), 0, 255));
        }

        Alpha(base0, m.AlphaMode, m.AlphaCutoff);
        ModContent.Premultiply(rgba.AsSpan(0, size), opaque: m.AlphaMode == GltfAlphaMode.Opaque);
        if (m.Physical)
        {
            NormalLayer(maps[0], m.NormalScale, w, h, rgba.AsSpan(size, size));
            OrmLayer(maps[2], m.OcclusionStrength, maps[1], m.Roughness, m.Metallic, w, h, rgba.AsSpan(size * 2, size));
            EmissiveLayer(maps[3], m.Emissive, w, h, rgba.AsSpan(size * 3, size));
        }

        return new HdTexture(w, h * layers, rgba);
    }

    /// <summary>The longest texture side every GPU backend takes (SDL GPU guarantees 16384 for a 2D
    /// texture); a surface's layers stack downwards, so its height is the layer's times the layers.</summary>
    public const int MaxTextureSide = 16384;

    /// <summary>The layer size a surface is made at: <paramref name="w"/> x <paramref name="h"/>, scaled
    /// down (aspect kept) until the width and the stacked height (<paramref name="layers"/> layers)
    /// fit <see cref="MaxTextureSide"/>; an 8192 x 8192 PBR material is made at 4096 x 4096.</summary>
    public static (int W, int H) FitTexture(int w, int h, int layers)
    {
        var scale = Math.Min(1.0, Math.Min((double)MaxTextureSide / w, (double)MaxTextureSide / ((long)h * layers)));
        if (scale >= 1.0)
        {
            return (w, h);
        }

        return (Math.Clamp((int)(w * scale), 1, MaxTextureSide), Math.Clamp((int)(h * scale), 1, MaxTextureSide / layers));
    }

    /// <summary>An embedded image decoded to straight RGBA8: PNG (SDL) or JPEG; null: neither.</summary>
    public static (int W, int H, byte[] Rgba)? Decode(byte[] data)
    {
        try
        {
            if (Jpeg.IsJpeg(data))
            {
                return Jpeg.Decode(data);
            }

            return PngImage.Decode(data) is { } png ? (png.Width, png.Height, png.Rgba) : null;
        }
        catch (Exception e) when (!ModContent.Critical(e))
        {
            Log.Warn("Mods", $"{(Jpeg.IsJpeg(data) ? "JPEG" : "image")}: {e.Message}");
            return null;
        }
    }

    /// <summary>The alpha mode's alpha: opaque left for <see cref="ModContent.Premultiply"/>; a mask's
    /// cutoff moved to half cover (the enhanced shader's and the depth passes' cut, so the mips and
    /// alpha to coverage still blend about it); a blend's kept.</summary>
    public static void Alpha(Span<byte> rgba, GltfAlphaMode mode, float cutoff)
    {
        if (mode != GltfAlphaMode.Mask)
        {
            return;
        }

        var c = Math.Clamp(cutoff, 1f / 255f, 254f / 255f);
        for (var i = 3; i < rgba.Length; i += 4)
        {
            var a = rgba[i] / 255f;
            var moved = a < c ? a * 0.5f / c : 0.5f + (a - c) * 0.5f / (1f - c);
            rgba[i] = (byte)Math.Clamp((int)(moved * 255f + 0.5f), 0, 255);
        }
    }

    private static void Fill(Span<byte> rgba, byte r, byte g, byte b, byte a)
    {
        for (var i = 0; i < rgba.Length; i += 4)
        {
            (rgba[i], rgba[i + 1], rgba[i + 2], rgba[i + 3]) = (r, g, b, a);
        }
    }

    /// <summary>An image at <paramref name="w"/> × <paramref name="h"/> (bilinear, wrapping as the
    /// texture repeats), into <paramref name="target"/>.</summary>
    private static void Resize((int W, int H, byte[] Rgba) image, int w, int h, Span<byte> target)
    {
        if (image.W == w && image.H == h)
        {
            image.Rgba.AsSpan(0, w * h * 4).CopyTo(target);
            return;
        }

        for (var y = 0; y < h; y++)
        {
            var sy = (y + 0.5f) * image.H / h - 0.5f;
            var y0 = (int)MathF.Floor(sy);
            var fy = sy - y0;
            var (r0, r1) = (((y0 % image.H) + image.H) % image.H, ((y0 + 1) % image.H + image.H) % image.H);
            for (var x = 0; x < w; x++)
            {
                var sx = (x + 0.5f) * image.W / w - 0.5f;
                var x0 = (int)MathF.Floor(sx);
                var fx = sx - x0;
                var (c0, c1) = (((x0 % image.W) + image.W) % image.W, ((x0 + 1) % image.W + image.W) % image.W);
                for (var ch = 0; ch < 4; ch++)
                {
                    float At(int row, int col) => image.Rgba[(row * image.W + col) * 4 + ch];
                    var top = At(r0, c0) + (At(r0, c1) - At(r0, c0)) * fx;
                    var bottom = At(r1, c0) + (At(r1, c1) - At(r1, c0)) * fx;
                    target[(y * w + x) * 4 + ch] = (byte)Math.Clamp((int)(top + (bottom - top) * fy + 0.5f), 0, 255);
                }
            }
        }
    }

    /// <summary>Linear 0-1 to sRGB bytes in 4096 steps (a texture's worth of pow calls is slow).</summary>
    private static readonly byte[] ToSrgbTable = Enumerable.Range(0, 4096).Select(i => Srgb.FromLinear(i / 4095f)).ToArray();

    private static byte ToSrgb(float linear) => ToSrgbTable[Math.Clamp((int)(linear * 4095f + 0.5f), 0, 4095)];

    /// <summary>A layer of unsigned values (0-1), stored so the sRGB texture's decode gives them back.</summary>
    private static void Store(Span<byte> target, int at, float r, float g, float b)
    {
        target[at] = ToSrgb(r);
        target[at + 1] = ToSrgb(g);
        target[at + 2] = ToSrgb(b);
        target[at + 3] = 255;
    }

    /// <summary>The normal map (its scale applied: x and y scaled, made unit again); flat without one.</summary>
    private static void NormalLayer((int W, int H, byte[] Rgba)? map, float scale, int w, int h, Span<byte> target)
    {
        if (map is not { } image)
        {
            Store(target, 0, 0.5f, 0.5f, 1f);
            Fill(target, target[0], target[1], target[2], 255);
            return;
        }

        Resize(image, w, h, target);
        for (var i = 0; i < target.Length; i += 4)
        {
            var n = new Vector3(target[i], target[i + 1], target[i + 2]) / 127.5f - Vector3.One;
            n = new Vector3(n.X * scale, n.Y * scale, n.Z);
            n = n.LengthSquared() > 1e-12f ? Vector3.Normalize(n) : Vector3.UnitZ;
            Store(target, i, n.X * 0.5f + 0.5f, n.Y * 0.5f + 0.5f, n.Z * 0.5f + 0.5f);
        }
    }

    /// <summary>Occlusion (red of its map, by its strength), roughness and metallic (green and blue
    /// of theirs, by their factors).</summary>
    private static void OrmLayer((int W, int H, byte[] Rgba)? occlusion, float strength, (int W, int H, byte[] Rgba)? metallicRoughness,
        float roughness, float metallic, int w, int h, Span<byte> target)
    {
        var size = w * h * 4;
        if (occlusion == null && metallicRoughness == null)
        {
            Store(target, 0, 1f, roughness, metallic);
            Fill(target, target[0], target[1], target[2], 255);
            return;
        }

        var occ = occlusion is { } o ? new byte[size] : null;
        var mr = metallicRoughness is { } r ? new byte[size] : null;
        if (occ != null)
        {
            Resize(occlusion!.Value, w, h, occ);
        }

        if (mr != null)
        {
            Resize(metallicRoughness!.Value, w, h, mr);
        }

        for (var i = 0; i < size; i += 4)
        {
            var ao = occ != null ? 1f + strength * (occ[i] / 255f - 1f) : 1f;
            var rough = roughness * (mr != null ? mr[i + 1] / 255f : 1f);
            var metal = metallic * (mr != null ? mr[i + 2] / 255f : 1f);
            Store(target, i, ao, rough, metal);
        }
    }

    /// <summary>The emission: its map (sRGB) times its factor (linear), sRGB again; black without.</summary>
    private static void EmissiveLayer((int W, int H, byte[] Rgba)? map, Vector3 factor, int w, int h, Span<byte> target)
    {
        if (map is not { } image)
        {
            Fill(target, ToSrgb(factor.X), ToSrgb(factor.Y), ToSrgb(factor.Z), 255);
            return;
        }

        Resize(image, w, h, target);
        for (var i = 0; i < target.Length; i += 4)
        {
            target[i] = ToSrgb(Srgb.ToLinearTable[target[i]] * factor.X);
            target[i + 1] = ToSrgb(Srgb.ToLinearTable[target[i + 1]] * factor.Y);
            target[i + 2] = ToSrgb(Srgb.ToLinearTable[target[i + 2]] * factor.Z);
            target[i + 3] = 255;
        }
    }
}
