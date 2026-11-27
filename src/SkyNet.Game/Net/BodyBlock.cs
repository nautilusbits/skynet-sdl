using System.Numerics;
using RetroEng.Physics;

namespace SkyNet.Game.Net;

/// <summary>
/// The players' bodies block each other in a deathmatch (the owner's rule; DOS has no bots and the
/// Godot port let bodies pass): each body is an upright capsule of <see cref="Radius"/> across and
/// <see cref="DmAvatar.Height"/> tall on its feet (a driver the vehicle's sphere), and a move that
/// ends inside another is pushed out sideways to the touching distance. A push that would put the
/// body into the level is not taken: the body keeps its place across (it may still fall or climb).
/// Bots (on the server, against every other living player) and each machine's own soldier (against
/// the other players' avatars) are separated after their controller's step.
/// </summary>
public static class BodyBlock
{
    /// <summary>A body's half width (the Godot port's 22-unit player capsule).</summary>
    public const float Radius = 22f;

    /// <summary>Where a body that moved from <paramref name="before"/> to <paramref name="after"/>
    /// may stand, given the other bodies (feet and half width).</summary>
    public static Vector3 Separate(Vector3 before, Vector3 after, IEnumerable<(Vector3 Feet, float Radius)> others, CollisionWorld world,
        SphereBody body)
    {
        var pos = after;
        foreach (var (feet, r) in others)
        {
            pos = Push(before, pos, feet, r);
        }

        return Settle(before, after, pos, world, body);
    }

    /// <inheritdoc cref="Separate(Vector3, Vector3, IEnumerable{ValueTuple{Vector3, float}}, CollisionWorld, SphereBody)"/>
    /// <remarks>A list walks without allocating (the bots, every tick).</remarks>
    internal static Vector3 Separate(Vector3 before, Vector3 after, List<(Vector3 Feet, float Radius)> others, CollisionWorld world,
        SphereBody body)
    {
        var pos = after;
        foreach (var (feet, r) in others)
        {
            pos = Push(before, pos, feet, r);
        }

        return Settle(before, after, pos, world, body);
    }

    /// <summary><paramref name="pos"/> pushed out of the body at <paramref name="feet"/>.</summary>
    private static Vector3 Push(Vector3 before, Vector3 pos, Vector3 feet, float r)
    {
        if (MathF.Abs(feet.Y - pos.Y) >= DmAvatar.Height)
        {
            return pos; // one stands on the other's head height or above: no side contact
        }

        var min = Radius + r;
        var d = new Vector2(pos.X - feet.X, pos.Z - feet.Z);
        var len = d.Length();
        if (len >= min)
        {
            return pos;
        }

        if (len < 1e-3f)
        {
            // Right on top: out the way it came, else along +X.
            d = new Vector2(before.X - feet.X, before.Z - feet.Z);
            len = d.Length();
            if (len < 1e-3f)
            {
                (d, len) = (Vector2.UnitX, 1f);
            }
        }

        var dir = d / len;
        return new Vector3(feet.X + dir.X * min, pos.Y, feet.Z + dir.Y * min);
    }

    /// <summary>The pushed place, unless it is in the level: then the body keeps its place across.</summary>
    private static Vector3 Settle(Vector3 before, Vector3 after, Vector3 pos, CollisionWorld world, SphereBody body)
    {
        if (pos != after && world.Overlaps(body, pos, slack: 1f))
        {
            return new Vector3(before.X, after.Y, before.Z);
        }

        return pos;
    }
}
