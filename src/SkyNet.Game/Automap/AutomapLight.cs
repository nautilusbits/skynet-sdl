using System.Numerics;
using SkyNet.Formats;

namespace SkyNet.Game.Automap;

/// <summary>How the automap fills its faces: textured (RENDER DETAIL HIGH) or one colour per
/// texture (LOW / MED: DOS's face mode 4).</summary>
internal enum AutomapStyle { Textured, Flat }

/// <summary>
/// The automap's light (SKYNET.EXE v1.01): DOS adds one directional light, FUN_0014d7f7(−0x400,
/// 0x2000, 0x1000) of intensity 24 (of 32), indoors too, and turns the haze off; the map's own
/// lights are not in it. Each face is shaded by it to one row of the shade table.
/// <code>
///   light ↓ (mostly from above, a little from the south-west)
///   row = (ambient + 24/32 · max(0, n · towards light)) · 63, at most 63
/// </code>
/// The ambient's share is a guess (DOS's DAT_00064b11 is not decoded).
/// </summary>
internal static class AutomapLight
{
    /// <summary>Towards the light in the port's axes: DOS's vector (Y down, Z north) is its travel.</summary>
    private static readonly Vector3 TowardsLight = Vector3.Normalize(new Vector3(0x400, 0x2000, 0x1000));
    /// <summary>The light's intensity over DOS's cap (0x18 of 0x20).</summary>
    private const float Intensity = 24f / 32f;
    /// <summary>The share of full light a face turned away keeps (GUESS).</summary>
    private const float Ambient = 0.5f;

    /// <summary>The row of a face turned away from the light.</summary>
    public static int AmbientRow => Row(Ambient);

    /// <summary>The shade row of a face with <paramref name="normal"/> (zero: as if facing the light).</summary>
    public static byte Shade(Vector3 normal)
    {
        var facing = normal == Vector3.Zero ? 1f : MathF.Max(0f, Vector3.Dot(Vector3.Normalize(normal), TowardsLight));
        return (byte)Row(Ambient + Intensity * facing);
    }

    /// <summary>The fill of RENDER DETAIL <paramref name="detail"/> (DOS's detail table 0x61a00: 4, 4, 8).</summary>
    public static AutomapStyle StyleOf(Detail detail) => detail == Detail.High ? AutomapStyle.Textured : AutomapStyle.Flat;

    private static int Row(float brightness) => (int)MathF.Round(Math.Clamp(brightness, 0f, 1f) * (HazeTable.Steps - 1));
}
