using System.Numerics;

namespace SkyNet.Game;

/// <summary>
/// Render interpolation: what moves is drawn between the last two ticks' poses at the frame's alpha
/// (0: the previous tick, 1: the latest), so a 60 Hz simulation looks smooth on a faster display.
/// Presentation only: the simulation keeps the previous pose (<c>KeepPoses</c>, at a tick's start)
/// and never reads it back. A move longer than <see cref="SnapDistance"/> in one tick (a teleport,
/// a respawn) is drawn where it ends, not swept across.
/// </summary>
public static class Interpolation
{
    /// <summary>Further than this in one tick is a jump, not a move (the fastest robot covers ~15).</summary>
    public const float SnapDistance = 256f;

    /// <summary>Between <paramref name="previous"/> and <paramref name="current"/>; the current at alpha 1
    /// or past a jump of more than <paramref name="snap"/>.</summary>
    public static Vector3 Lerp(Vector3 previous, Vector3 current, float alpha, float snap = SnapDistance) =>
        alpha >= 1f || Vector3.DistanceSquared(previous, current) > snap * snap ? current : Vector3.Lerp(previous, current, Math.Max(alpha, 0f));

    /// <summary>Radians the short way round (a turn through ±π does not spin back the long way).</summary>
    public static float LerpAngle(float previous, float current, float alpha) => LerpAngle(previous, current, alpha, MathF.Tau);

    /// <summary>An angle of <paramref name="period"/> a turn (2048: DOS's angle units), the short way round.</summary>
    public static float LerpAngle(float previous, float current, float alpha, float period)
    {
        if (alpha >= 1f)
        {
            return current;
        }

        var d = (current - previous) % period;
        var half = period / 2f;
        if (d > half)
        {
            d -= period;
        }
        else if (d < -half)
        {
            d += period;
        }

        return current - d * (1f - Math.Max(alpha, 0f));
    }
}
