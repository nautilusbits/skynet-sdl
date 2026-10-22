using System.Security.Cryptography;

namespace SkyNet.Formats.Tests;

/// <summary>
/// The player's own game data, found through the SKYNET_DATA environment variable. Tests that
/// need it use <see cref="DataFactAttribute"/> and are skipped when it is not set.
/// </summary>
static class TestData
{
    public const string EnvVar = "SKYNET_DATA";

    public static string? Dir
    {
        get
        {
            var d = Environment.GetEnvironmentVariable(EnvVar);
            return !string.IsNullOrWhiteSpace(d) && GameDataLocator.HasData(d) ? d : null;
        }
    }

    public static GameData Open() => GameData.Open(Dir!) ?? throw new InvalidOperationException("no game data");

    public static string Md5(ReadOnlySpan<byte> b) => Convert.ToHexStringLower(MD5.HashData(b));
}

/// <summary>A fact that is skipped unless SKYNET_DATA points at SkyNET's data folder.</summary>
sealed class DataFactAttribute : FactAttribute
{
    public DataFactAttribute()
    {
        if (TestData.Dir == null)
            Skip = $"{TestData.EnvVar} is not set to a SkyNET data folder";
    }
}

/// <summary>Little-endian byte array builder for synthetic files.</summary>
sealed class Bytes
{
    readonly List<byte> _b = [];
    public int Length => _b.Count;
    public Bytes U8(int v) { _b.Add((byte)v); return this; }
    public Bytes U16(int v) { _b.Add((byte)v); _b.Add((byte)(v >> 8)); return this; }
    public Bytes U32(long v) { for (int i = 0; i < 4; i++) _b.Add((byte)(v >> (8 * i))); return this; }
    public Bytes Raw(ReadOnlySpan<byte> s) { foreach (var x in s) _b.Add(x); return this; }
    public Bytes Zeros(int n) { for (int i = 0; i < n; i++) _b.Add(0); return this; }
    public Bytes PadTo(int n) => Zeros(n - _b.Count);
    public byte[] ToArray() => [.. _b];
}
