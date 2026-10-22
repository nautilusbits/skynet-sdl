using System.Numerics;
using RetroEng.Physics;

namespace SkyNet.Game.Collision;

/// <summary>
/// The floor a sprite drawn as a model stands on indoors. The billboard's foot is the record's Y
/// less <see cref="Level.IndoorSpriteLift"/>, which suits a tall picture but sinks a flat model:
/// litter records sit about 8 above the floor, cans about 13. So the model looks for the highest
/// face pointing up straight under the record, from <see cref="Above"/> over it to
/// <see cref="Reach"/> under it, and keeps the billboard's foot when there is none.
/// <code>
///   record ●            Y
///          │  ≤ Reach
///   ───────┴──────── floor → the model's foot
///   ─ ─ ─ ─ ─ ─ ─ ─  Y − 16 (billboard foot, the fallback)
/// </code>
/// </summary>
public static class SpriteFloor
{
    /// <summary>How far under the record a floor is looked for (twice the billboard lift).</summary>
    public const float Reach = 2f * Level.IndoorSpriteLift;
    /// <summary>How far over the record a floor still counts (a record placed into the floor).</summary>
    public const float Above = 4f;

    /// <summary>The top of the highest upward face straight under <paramref name="record"/> (level
    /// axes, Y up) within the window; null: none.</summary>
    public static float? Below(CollisionWorld world, Vector3 record)
    {
        var top = record.Y + Above;
        var bottom = record.Y - Reach;
        var tris = new List<Triangle>();
        world.Gather(new Box(new Vector3(record.X - 0.5f, bottom - 1f, record.Z - 0.5f),
            new Vector3(record.X + 0.5f, top + 1f, record.Z + 0.5f)), tris);
        var from = record with { Y = top + 1f };
        float? best = null;
        foreach (var tri in tris)
        {
            if (tri.Cross.Y <= 0f || !Intersect.Ray(from, -Vector3.UnitY, tri, out var d))
            {
                continue;
            }

            var y = from.Y - d;
            if (y >= bottom && y <= top && (best is not { } b || y > b))
            {
                best = y;
            }
        }

        return best;
    }
}
