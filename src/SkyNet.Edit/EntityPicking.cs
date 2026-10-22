using System.Numerics;
using RetroEng.Editor;
using RetroEng.Physics;
using SkyNet.Formats;
using SkyNet.Game.Baking;
using SkyNet.Game.Behaviour;

namespace SkyNet.Edit;

/// <summary>What a click in the view can pick on one shown map, keyed by MAP file offset: a placed
/// mesh by its collision triangles (the block's colliders), a sprite by its billboard's box, and
/// what draws nothing of its own (lights, markers, the robots' markers) by a small box at its place.</summary>
public sealed class EntityPicking
{
    /// <summary>Half the box of an entity picked by its place only.</summary>
    public static readonly Vector3 PointHalf = new(48f);
    /// <summary>A robot's box: its marker's place up to about a soldier's height.</summary>
    public static readonly Vector3 RobotHalf = new(64f, 96f, 64f);

    private readonly Dictionary<int, PickTarget> _byKey = [];

    public EntityPicking(MapFile map, MapBlock block)
    {
        foreach (var group in block.Colliders.GroupBy(c => c.Offset))
        {
            Vector3[] corners = [.. group.SelectMany(c => c.Corners)];
            if (corners.Length >= 3)
            {
                _byKey[group.Key] = PickTarget.OfTriangles(group.Key, corners);
            }
        }

        foreach (var g in block.Sprites)
        {
            foreach (var (centre, size, offset) in g.Sprites)
            {
                var half = new Vector3(size.X * 0.5f, size.Y * 0.5f, size.X * 0.5f);
                _byKey.TryAdd(offset, PickTarget.Around(offset, centre, half));
            }
        }

        foreach (var e in map.Entities)
        {
            if (_byKey.ContainsKey(e.FileOffset))
            {
                continue;
            }

            var at = BehaviourModel.Position(e);
            _byKey[e.FileOffset] = EditorWorld.KindOf(e) == EntityKind.Robot
                ? PickTarget.Around(e.FileOffset, at + new Vector3(0f, RobotHalf.Y, 0f), RobotHalf)
                : PickTarget.Around(e.FileOffset, at, PointHalf);
        }
    }

    public IReadOnlyCollection<PickTarget> Targets => _byKey.Values;

    /// <summary>The entity's pick target (its box and triangles), if it has one.</summary>
    public bool TryGet(int offset, out PickTarget target) => _byKey.TryGetValue(offset, out target);

    /// <summary>The MAP file offset of the nearest entity the ray hits; null: none.</summary>
    public int? Pick(PickRay ray, float far = float.MaxValue) => Picker.Nearest(ray, _byKey.Values, far)?.Key;

    /// <summary>The MAP file offset of the nearest entity the ray hits before the ground does
    /// (<paramref name="ground"/>: an outdoor map's collision field; null: no ground in the way); null: none.</summary>
    public int? Pick(PickRay ray, HeightField? ground) => Pick(ray, ground != null ? GroundDistance(ray, ground) : float.MaxValue);

    /// <summary>As <see cref="Pick(PickRay, HeightField?)"/>, the entities in <paramref name="skip"/> (the outliner's
    /// hidden and locked ones) not picked: the ray goes through them.</summary>
    public int? Pick(PickRay ray, HeightField? ground, IReadOnlySet<int> skip) =>
        Picker.Nearest(ray, _byKey.Values.Where(t => !skip.Contains(t.Key)), ground != null ? GroundDistance(ray, ground) : float.MaxValue)?.Key;

    /// <summary>Where the ray first meets a mesh's collision or the ground (what a drop or a placing click lands on;
    /// the boxes of sprites, markers and lights are no surface); null: nothing.</summary>
    public Vector3? Surface(PickRay ray, HeightField? ground)
    {
        var hit = Picker.Nearest(ray, _byKey.Values.Where(t => !t.Triangles.IsEmpty))?.Distance ?? float.MaxValue;
        if (ground != null)
        {
            hit = MathF.Min(hit, GroundDistance(ray, ground));
        }

        return hit == float.MaxValue ? null : ray.At(hit);
    }

    /// <summary>How far along the ray it first goes under the drawn ground (stepped, then halved down
    /// to a unit); float.MaxValue: not within <paramref name="reach"/>.</summary>
    public static float GroundDistance(PickRay ray, HeightField ground, float reach = 40000f, float step = 32f)
    {
        bool Under(float t)
        {
            var p = ray.At(t);
            return p.Y < ground.HeightAt(p.X, p.Z);
        }

        if (Under(0f))
        {
            return float.MaxValue;
        }

        for (var t = step; t <= reach; t += step)
        {
            if (!Under(t))
            {
                continue;
            }

            var (lo, hi) = (t - step, t);
            while (hi - lo > 1f)
            {
                var mid = (lo + hi) * 0.5f;
                (lo, hi) = Under(mid) ? (lo, mid) : (mid, hi);
            }

            return hi;
        }

        return float.MaxValue;
    }
}
