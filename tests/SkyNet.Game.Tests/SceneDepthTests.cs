using System.Numerics;

namespace SkyNet.Game.Tests;

/// <summary>MAP.210's gate (#72): BIGDOOR's leaves stand 10 units behind the wall's front face
/// (6 behind its back) where they slide behind it, some 50 000 units from the origin. The
/// original look's standard depth from a float32 view-projection of such coordinates rounds
/// several units of distance away at 2000; the leaves flickered through the wall.</summary>
public class SceneDepthTests
{
    private const float FovY = 1.2f, Aspect = 16f / 9f;

    /// <summary>The wall's face by the gate: x 49 455-49 655, y 737-987, z −55 818.</summary>
    private static readonly Vector3 Wall = new(49455f, 737f, -55818f);

    // Out to the high detail's far distance (Level.DetailFar: 3992).
    [Theory]
    [InlineData(6f, 2000f)]
    [InlineData(6f, 3992f)]
    [InlineData(10f, 3992f)]
    public void LeafBehindWallStaysBehind(float gap, float distance)
    {
        var random = new Random(72);
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(FovY, Aspect, SceneDepth.Near, SceneDepth.FarPlane);
        var wrong = 0;
        for (var i = 0; i < 2000; i++)
        {
            // The eye strafes across the approach, looking at the gate.
            var eye = new Vector3(49250f + random.NextSingle() * 400f, 1160f, Wall.Z + distance);
            var viewProjection = Matrix4x4.CreateLookAt(eye, eye + new Vector3(0.1f, -0.1f, -1f), Vector3.UnitY) * projection;
            var front = Wall + new Vector3(random.NextSingle() * 200f, random.NextSingle() * 250f, 0f);
            if (Depth(viewProjection, front - new Vector3(0f, 0f, gap)) <= Depth(viewProjection, front))
            {
                wrong++;
            }
        }

        Assert.Equal(0, wrong);
    }

    /// <summary>A point's depth after the float32 transform, as the GPU computes it: fused
    /// multiply-adds. Not Vector4.Transform: it fuses only on a CPU with FMA, and unfused clip z
    /// and w round apart (118 of 2000 leaves through the wall at 3992 on a Celeron N5105).</summary>
    private static float Depth(Matrix4x4 m, Vector3 p)
    {
        var z = MathF.FusedMultiplyAdd(m.M33, p.Z, MathF.FusedMultiplyAdd(m.M23, p.Y, m.M13 * p.X)) + m.M43;
        var w = MathF.FusedMultiplyAdd(m.M34, p.Z, MathF.FusedMultiplyAdd(m.M24, p.Y, m.M14 * p.X)) + m.M44;
        return z / w;
    }
}
