using System.Buffers.Binary;
using System.Numerics;
using RetroEng.Physics;
using SkyNet.Formats;
using SkyNet.Game.Collision;

namespace SkyNet.Game.Tests;

public class TerrainCollisionTests
{
    /// <summary>A WLD.NNN of 2 × 2 chunks whose height layer is random (fixed seed).</summary>
    private static WldTerrain RandomWld(int seed)
    {
        const int chunk = WldTerrain.ChunkHeaderSize + 4 * 128 * 128;
        var bytes = new byte[WldTerrain.ChunkTableOffset + 16 + 4 * chunk];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), 2);
        var rng = new Random(seed);
        for (var c = 0; c < 4; c++)
        {
            var at = WldTerrain.ChunkTableOffset + 16 + c * chunk;
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(WldTerrain.ChunkTableOffset + c * 4), (uint)at);
            rng.NextBytes(bytes.AsSpan(at + WldTerrain.ChunkHeaderSize, 128 * 128));
        }

        return WldTerrain.Parse(bytes) ?? throw new InvalidOperationException("synthetic WLD did not parse");
    }

    [Fact]
    public void GroundHeightEqualsWldHeightAt()
    {
        var wld = RandomWld(3);
        var ground = LevelCollision.Ground(wld);
        var world = new CollisionWorld().Add(ground);
        var rng = new Random(11);
        for (var i = 0; i < 2000; i++)
        {
            // Inside the field (world z = −DOS z, from −65 536 to 0).
            var x = 1f + (float)rng.NextDouble() * 65000f;
            var z = -65535f + (float)rng.NextDouble() * 65000f;
            var expected = (float)wld.HeightAt(x, -z);
            Assert.Equal(expected, ground.HeightAt(x, z), 0.01f);
            if (i % 10 == 0)
            {
                // …and the triangles the walker collides with are that same surface.
                Assert.True(world.Raycast(new Vector3(x, 9000f, z), -Vector3.UnitY, 10000f, out var hit));
                Assert.Equal(expected, 9000f - hit.Time, 0.05f);
            }
        }

        // Exactly on the corners.
        for (var i = 0; i < 100; i++)
        {
            int col = rng.Next(0, 255), row = rng.Next(0, 255);
            var x = col * 256f;
            var z = row * 256f - 65536f;
            Assert.Equal((float)wld.CornerHeight(col, row), ground.HeightAt(x, z), 0.001f);
        }
    }

    [Fact]
    public void RealWld210MatchesWhenTheDataIsThere()
    {
        var dir = Environment.GetEnvironmentVariable("SKYNET_DATA");
        if (string.IsNullOrWhiteSpace(dir) || !GameDataLocator.HasData(dir) || GameData.Open(dir)?.LoadWld(210) is not { } wld)
        {
            return;
        }

        var ground = LevelCollision.Ground(wld);
        var rng = new Random(5);
        for (var i = 0; i < 2000; i++)
        {
            var x = 1f + (float)rng.NextDouble() * 65000f;
            var z = -65535f + (float)rng.NextDouble() * 65000f;
            Assert.Equal((float)wld.HeightAt(x, -z), ground.HeightAt(x, z), 0.01f);
        }
    }
}
