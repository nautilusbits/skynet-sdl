using SkyNet.Game.Mods;

namespace SkyNet.Game.Tests.Mods;

/// <summary>Meshes a map's scope owns: built once, built again after the map's release (no stale handle drawn).</summary>
public class ScopedCacheTests
{
    [Fact]
    public void A_mesh_is_built_once_while_its_map_lasts()
    {
        var cache = new ScopedCache<string, int>();
        var builds = 0;

        Assert.Equal(1, cache.Get("medkit", _ => ++builds));
        Assert.Equal(1, cache.Get("medkit", _ => ++builds));
        Assert.Equal(1, builds);
    }

    [Fact]
    public void A_released_map_makes_the_next_draw_build_again()
    {
        // A drop's model built on MAP.210 is destroyed with its scope: MAP.211 must not draw that handle.
        var cache = new ScopedCache<string, int>();
        var builds = 0;
        cache.Get("medkit", _ => ++builds);

        cache.Release();

        Assert.Equal(2, cache.Get("medkit", _ => ++builds));
        Assert.Single(cache.Values);
    }
}
