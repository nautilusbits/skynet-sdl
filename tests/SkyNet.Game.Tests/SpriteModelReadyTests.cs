using System.Numerics;
using RetroEng.Models;
using RetroEng.Render;
using SkyNet.Data;
using SkyNet.Game.Mods;

namespace SkyNet.Game.Tests;

/// <summary>A mod's textures made on first use: a map's load makes them on every core
/// (<see cref="ReplacementSurface.Warm"/>); a drop's model waits for a worker's decode, its billboard
/// drawn meanwhile (<see cref="SpriteModelView.Ready"/>), instead of decoding in the frame.</summary>
public class SpriteModelReadyTests
{
    private static GltfModel Empty => new() { Primitives = [], Materials = [], Images = [] };

    private static ReplacementSurface Lazy() =>
        ReplacementSurface.Build("test.glb", Empty, new GltfMaterial("m", Vector4.One, null, GltfAlphaMode.Opaque, 0.5f, false));

    [Fact]
    public void Warm_makes_every_texture()
    {
        var surfaces = new[] { Lazy(), Lazy() };
        Assert.All(surfaces, s => Assert.False(s.Made));

        ReplacementSurface.Warm(surfaces, kept: null);
        Assert.All(surfaces, s => Assert.True(s.Made));
    }

    [Fact]
    public void A_drop_waits_for_its_textures_on_a_worker()
    {
        var surface = Lazy();
        var model = new SpriteModel
        {
            Sprite = 27399, Row = new SpriteModelRow("214/007", "medkit.glb", 0, 0, 0, 0, 0), Box = (Vector3.Zero, Vector3.One),
            Surfaces = [surface],
        };
        var view = new SpriteModelView(null!, default);

        // Not made: a worker decodes it, the billboard is drawn.
        Assert.False(view.Ready(model));
        SpinWait.SpinUntil(() => surface.Made, TimeSpan.FromSeconds(10));
        Assert.True(surface.Made);
        Assert.True(view.Ready(model));
    }
}
