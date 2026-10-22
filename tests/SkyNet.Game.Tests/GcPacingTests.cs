using System.Runtime;

namespace SkyNet.Game.Tests;

/// <summary>The GC's pace around a map's load: blocking collections allowed while loading, a full
/// collection once the map is built, then no blocking gen2 while playing.</summary>
public class GcPacingTests
{
    [Fact]
    public void Playing_collects_once_then_avoids_blocking_gen2()
    {
        var was = GCSettings.LatencyMode;
        try
        {
            GcPacing.Loading();
            Assert.Equal(GCLatencyMode.Interactive, GCSettings.LatencyMode);

            var gen2 = GC.CollectionCount(2);
            GcPacing.Playing();
            Assert.True(GC.CollectionCount(2) > gen2);
            Assert.Equal(GCLatencyMode.SustainedLowLatency, GCSettings.LatencyMode);
        }
        finally
        {
            GCSettings.LatencyMode = was;
        }
    }

    [Fact]
    public void A_runtime_without_latency_modes_still_loads_and_collects()
    {
        // Android's Mono refuses GCSettings.LatencyMode and LargeObjectHeapCompactionMode: entering a
        // map crashed there (0.48-0.49.5).
        static void Refuse(Action change) => throw new PlatformNotSupportedException();

        GcPacing.Loading(Refuse);
        var gen2 = GC.CollectionCount(2);
        GcPacing.Playing(Refuse);
        Assert.True(GC.CollectionCount(2) > gen2);
    }

    [Fact]
    public void Playing_compacts_the_large_object_heap()
    {
        // A load leaves holes among big arrays (decoded textures); uncompacted, the process keeps
        // them committed (~2 GB with skynet-hd while 0.2 GB was live).
        const int Count = 64, Size = 1 << 20, Fragmented = 8 << 20;
        var was = GCSettings.LatencyMode;
        var keep = new byte[Count][];
        try
        {
            for (var i = 0; i < Count; i++)
            {
                keep[i] = new byte[Size];
            }

            for (var i = 0; i < Count; i += 2)
            {
                keep[i] = null!;
            }

            GcPacing.Playing();
            Assert.True(GC.GetGCMemoryInfo().FragmentedBytes < Fragmented,
                $"{GC.GetGCMemoryInfo().FragmentedBytes >> 20} MB still fragmented");
            GC.KeepAlive(keep);
        }
        finally
        {
            GCSettings.LatencyMode = was;
        }
    }
}
