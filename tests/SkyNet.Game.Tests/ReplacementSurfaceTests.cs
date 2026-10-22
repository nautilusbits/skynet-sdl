using System.Numerics;
using RetroEng.Models;
using RetroEng.Render;
using SkyNet.Game.Mods;

namespace SkyNet.Game.Tests;

/// <summary>A replacement model's materials as drawn: the alpha modes' pipelines (opaque, cut out
/// in the alpha-tested queue, blended back to front), the mask's cutoff moved to half cover, and a
/// physical material's four layers (base colour, normal, occlusion-roughness-metallic, emission).</summary>
public class ReplacementSurfaceTests
{
    private static GltfModel Empty => new() { Primitives = [], Materials = [], Images = [] };

    private static ReplacementSurface Flat(GltfAlphaMode mode, float alpha = 1f, bool doubleSided = false) =>
        ReplacementSurface.Build("test.glb", Empty, new GltfMaterial("m", new Vector4(1f, 0.5f, 0.25f, alpha), null, mode, 0.5f, doubleSided));

    [Fact]
    public void Mask_geometry_gets_the_cut_out_pipeline_and_blend_the_transparent_one()
    {
        var mask = Flat(GltfAlphaMode.Mask, doubleSided: true);
        Assert.Equal(Queues.AlphaTest, mask.State.Queue);
        Assert.Equal((Cull.None, Blend.Opaque, Depth.TestWrite), (mask.State.Cull, mask.State.Blend, mask.State.Depth));

        var blend = Flat(GltfAlphaMode.Blend, alpha: 0.4f);
        Assert.Equal(Queues.Transparent, blend.State.Queue);
        Assert.Equal((Cull.Back, Blend.Alpha, Depth.Test), (blend.State.Cull, blend.State.Blend, blend.State.Depth));
        // Its alpha is kept (premultiplied): 0.4 of 255.
        Assert.Equal(102, blend.Colour.Rgba[3]);

        var opaque = Flat(GltfAlphaMode.Opaque, alpha: 0.4f);
        Assert.Equal(RenderState.Solid, opaque.State);
        Assert.Equal(255, opaque.Colour.Rgba[3]);

        // Applied to a part's material.
        var material = mask.Apply(new Material(default, default, Vector4.One, 1, RenderState.Solid, Shading.Lit));
        Assert.Equal(Queues.AlphaTest, material.State.Queue);
        Assert.False(material.SurfaceMaps);
    }

    [Theory]
    [InlineData(0.3f, 76, false)]
    [InlineData(0.3f, 77, true)]
    [InlineData(0.8f, 203, false)]
    [InlineData(0.8f, 205, true)]
    public void A_mask_cutoff_moves_to_half_cover(float cutoff, byte alpha, bool kept)
    {
        var rgba = new byte[] { 10, 20, 30, alpha };
        ReplacementSurface.Alpha(rgba, GltfAlphaMode.Mask, cutoff);
        Assert.Equal(kept, rgba[3] >= 128);
    }

    [Fact]
    public void A_physical_material_stacks_its_four_layers()
    {
        var m = new GltfMaterial("lamp", Vector4.One, null, GltfAlphaMode.Opaque, 0.5f, false)
        {
            Metallic = 1f, Roughness = 0.5f, Emissive = new Vector3(1f, 0f, 0f), EmissiveStrength = 4f, Physical = true,
        };
        var s = ReplacementSurface.Build("test.glb", Empty, m);

        Assert.True(s.Maps);
        Assert.Equal(4f, s.Emission);
        Assert.Equal((1, Renderer.SurfaceMapLayers), (s.Colour.Width, s.Colour.Height));
        var texel = s.Colour.Rgba;
        // Each layer decodes (sRGB) to its value: the flat normal (0.5, 0.5, 1), then occlusion 1,
        // roughness 0.5, metallic 1, then the red emission.
        float At(int i) => Srgb.ToLinear(texel[i] / 255f);
        Assert.Equal(0.5f, At(4), 2);
        Assert.Equal(0.5f, At(5), 2);
        Assert.Equal(1f, At(6), 2);
        Assert.Equal((1f, 0.5f, 1f), (MathF.Round(At(8), 2), MathF.Round(At(9), 2), MathF.Round(At(10), 2)));
        Assert.Equal((255, 0, 0), (texel[12], texel[13], texel[14]));

        var material = s.Apply(new Material(default, default, Vector4.One, 4, RenderState.Solid, Shading.Lit));
        Assert.True(material.SurfaceMaps);
        Assert.Equal(4f, material.Emission);
    }

    [Fact]
    public void A_light_floor_adds_the_base_colour_to_the_emission()
    {
        // A plain material becomes a rough non-metal with maps: flat normal, emission = base x floor.
        var plain = Flat(GltfAlphaMode.Opaque).WithLightFloor(0.5f);
        Assert.True(plain.Maps);
        Assert.Equal(1f, plain.Emission);
        Assert.Equal((1, Renderer.SurfaceMapLayers), (plain.Colour.Width, plain.Colour.Height));
        float At(ReplacementSurface s, int i) => Srgb.ToLinear(s.Colour.Rgba[i] / 255f);
        Assert.Equal((0.5f, 0.5f, 1f), (MathF.Round(At(plain, 4), 2), MathF.Round(At(plain, 5), 2), MathF.Round(At(plain, 6), 2)));
        Assert.Equal((1f, 1f, 0f), (MathF.Round(At(plain, 8), 2), MathF.Round(At(plain, 9), 2), MathF.Round(At(plain, 10), 2)));
        Assert.Equal(0.5f, At(plain, 12), 2);    // red 1 x 0.5
        Assert.Equal(0.25f, At(plain, 13), 2);   // green 0.5 x 0.5
        Assert.Equal(0.125f, At(plain, 14), 2);  // blue 0.25 x 0.5

        // A physical one keeps its glow on top, at its strength: the floor divided by it.
        var m = new GltfMaterial("lamp", new Vector4(0.5f, 0.5f, 0.5f, 1f), null, GltfAlphaMode.Opaque, 0.5f, false)
        {
            Emissive = new Vector3(0.25f, 0f, 0f), EmissiveStrength = 2f, Physical = true,
        };
        var lamp = ReplacementSurface.Build("test.glb", Empty, m).WithLightFloor(0.4f);
        Assert.Equal(2f, lamp.Emission);
        Assert.Equal(0.25f + 0.5f * 0.4f / 2f, At(lamp, 12), 2);
        Assert.Equal(0.5f * 0.4f / 2f, At(lamp, 13), 2);

        // No floor: the surface as it was.
        var same = Flat(GltfAlphaMode.Opaque);
        Assert.Same(same, same.WithLightFloor(0f));
    }

    [Fact]
    public void Released_pixels_are_made_again_the_same()
    {
        var m = new GltfMaterial("lamp", new Vector4(0.5f, 0.5f, 0.5f, 1f), null, GltfAlphaMode.Opaque, 0.5f, false)
        {
            Emissive = new Vector3(0.25f, 0f, 0f), EmissiveStrength = 2f, Physical = true,
        };
        foreach (var s in new[] { ReplacementSurface.Build("test.glb", Empty, m), Flat(GltfAlphaMode.Blend, 0.4f).WithLightFloor(0.3f) })
        {
            var first = s.Colour;
            Assert.Same(first, s.Colour);
            s.ReleasePixels();
            var again = s.Colour;
            Assert.NotSame(first, again);
            Assert.Equal((first.Width, first.Height), (again.Width, again.Height));
            Assert.Equal(first.Rgba, again.Rgba);
        }

        // A surface given whole keeps its pixels.
        var whole = new ReplacementSurface(new HdTexture(1, 1, [1, 2, 3, 4]), false);
        var pixels = whole.Colour;
        whole.ReleasePixels();
        Assert.Same(pixels, whole.Colour);
    }

    [Fact]
    public void A_plain_material_keeps_one_layer()
    {
        var s = Flat(GltfAlphaMode.Opaque);
        Assert.False(s.Maps);
        Assert.Equal((1, 1), (s.Colour.Width, s.Colour.Height));
    }

    [Fact]
    public void A_tangent_packs_into_the_vertex_colour_with_its_sign()
    {
        Assert.Equal(Vertex.Rgba(255, 128, 128, 255), ReplacementSurface.PackTangent(new Vector4(1, 0, 0, 1), Matrix4x4.Identity, false));
        // A mirroring placement turns the sign.
        Assert.Equal(Vertex.Rgba(0, 128, 128, 0), ReplacementSurface.PackTangent(new Vector4(1, 0, 0, 1), Matrix4x4.CreateScale(-1, 1, 1), true));
    }

    [Fact]
    public void A_huge_material_is_scaled_to_fit_the_gpu()
    {
        var layers = Renderer.SurfaceMapLayers;
        Assert.Equal((512, 256), ReplacementSurface.FitTexture(512, 256, layers));
        // 8192 x 8192 with its maps would stack 8192 x 32768: made at the size whose stack fits.
        var (w, h) = ReplacementSurface.FitTexture(8192, 8192, layers);
        Assert.Equal(w, h);
        Assert.True((long)h * layers <= ReplacementSurface.MaxTextureSide);
        Assert.Equal(ReplacementSurface.MaxTextureSide / layers, h);
        // A wide plain texture past the side keeps its aspect.
        Assert.Equal((16384, 4096), ReplacementSurface.FitTexture(32768, 8192, 1));
        // A side of 65535 by 65535 (no int overflow on the way).
        var (bw, bh) = ReplacementSurface.FitTexture(65535, 65535, layers);
        Assert.True(bw <= ReplacementSurface.MaxTextureSide && (long)bh * layers <= ReplacementSurface.MaxTextureSide);
    }
}
