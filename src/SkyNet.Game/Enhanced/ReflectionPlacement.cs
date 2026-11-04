using System.Numerics;
using RetroEng.Render;
using SkyNet.Game.Baking;

namespace SkyNet.Game.Enhanced;

/// <summary>
/// Where a map's reflection probes go (<see cref="ProbeCapture"/>): over the floors the player walks.
/// <code>
///   block.World triangles facing up (floors, roofs, platforms) ─► weighted k-means of their centres
///     (height counted LevelWeight times: storeys apart), k from their area: interiors one a room's
///     floor (RoomArea), outdoors one or two over the built area ─► a cluster's floors' box = the
///     probe's influence and room, from its lowest floor to the lowest ceiling over its probe
///   probe = an eye over the floor nearest the cluster's centre of floor area (never in the void)
///   +-----------+------+
///   |  room A   |  B   |   A: influence box = A's floors × floor..ceiling, probe over A's central floor
///   |     *     |  *   |
///   +-----------+------+
/// </code>
/// Deterministic: the seeds are the largest floor, then the floor farthest from the seeds so far.
/// </summary>
public static class ReflectionPlacement
{
    /// <summary>Probes a map gets at most (<see cref="Reflections.MaxProbes"/>).</summary>
    public const int MaxProbes = Reflections.MaxProbes;
    /// <summary>Outdoors: one probe, two over a built area wider than <see cref="OutdoorSplit"/>.</summary>
    public const int MaxOutdoorProbes = 2;
    /// <summary>Interiors: one probe per this much floor (units²: a room about 1000 u across).</summary>
    public const float RoomArea = 1000f * 1000f;
    /// <summary>Outdoors: a built area wider than this (units) gets two probes.</summary>
    public const float OutdoorSplit = 6000f;
    /// <summary>A face is a floor when its normal's y is above this (about 25° of slope).</summary>
    public const float FloorNormalY = 0.9f;
    /// <summary>The soldier's eye over the floor (units): the probe sees the room as he does.</summary>
    public const float EyeHeight = 75f;
    /// <summary>A room's height when nothing is found over its floor (outdoors: the air kept).</summary>
    public const float DefaultHeight = 400f;
    /// <summary>The influence box grown round the floors (units): the walls' foot, the room's edge.</summary>
    public const float InteriorMargin = 32f;
    /// <summary>Outdoors: the box grown round the built area (units), as the irradiance bake's.</summary>
    public const float OutdoorMargin = IrradianceScene.OutdoorMargin;
    /// <summary>The capture's side (texels) and planes (units).</summary>
    public const int Size = 128;
    public const float Near = 4f;

    /// <summary>Height counts this many times plan distance in the clustering: a floor 100 u up or
    /// down is as far as one 400 u across (storeys and pits are rooms of their own).</summary>
    public const float LevelWeight = 4f;

    private const int Iterations = 12;

    /// <summary>One upward or downward face: its centre, area and corners' box.</summary>
    public readonly record struct Face(Vector3 Centre, float Area, Vector3 Min, Vector3 Max);

    /// <summary>The probes of a map's static block; none when it has no floor.</summary>
    public static IReadOnlyList<ProbeCapture> For(MapBlock block, bool outdoor, float far)
    {
        var (floors, ceilings) = Faces(block.World);
        return Place(floors, ceilings, outdoor, far);
    }

    /// <summary>The probes over <paramref name="floors"/>, their rooms closed by <paramref name="ceilings"/>.</summary>
    public static IReadOnlyList<ProbeCapture> Place(IReadOnlyList<Face> floors, IReadOnlyList<Face> ceilings, bool outdoor, float far)
    {
        if (floors.Count == 0)
        {
            return [];
        }

        var k = Math.Min(Count(floors, outdoor), floors.Count);
        var clusters = Cluster(floors, k);
        var probes = new List<ProbeCapture>(k);
        foreach (var members in clusters)
        {
            if (members.Count > 0)
            {
                probes.Add(Probe(members, ceilings, outdoor, far));
            }
        }

        return probes;
    }

    /// <summary>How many probes the floors get: by floor area indoors, by width outdoors.</summary>
    public static int Count(IReadOnlyList<Face> floors, bool outdoor)
    {
        if (outdoor)
        {
            var (min, max) = PlanBox(floors);
            var size = max - min;
            return MathF.Max(size.X, size.Y) > OutdoorSplit ? MaxOutdoorProbes : 1;
        }

        var area = floors.Sum(f => f.Area);
        return Math.Clamp((int)MathF.Round(area / RoomArea), 1, MaxProbes);
    }

    /// <summary>The block's faces turned up (floors) and down (ceilings), by their drawn side.</summary>
    public static (List<Face> Floors, List<Face> Ceilings) Faces(IEnumerable<BlockBucket> world)
    {
        var (floors, ceilings) = (new List<Face>(), new List<Face>());
        foreach (var bucket in world)
        {
            var (v, ix) = (bucket.Vertices, bucket.Indices);
            for (var t = 0; t + 2 < ix.Length; t += 3)
            {
                var (a, b, c) = (v[ix[t]], v[ix[t + 1]], v[ix[t + 2]]);
                var normal = Vertex.UnpackNormal(a.Normal) + Vertex.UnpackNormal(b.Normal) + Vertex.UnpackNormal(c.Normal);
                if (normal.LengthSquared() < 1e-12f)
                {
                    continue;
                }

                var y = Vector3.Normalize(normal).Y;
                var area = Vector3.Cross(b.Position - a.Position, c.Position - a.Position).Length() * 0.5f;
                if (area <= 0f || MathF.Abs(y) < FloorNormalY)
                {
                    continue;
                }

                var face = new Face((a.Position + b.Position + c.Position) / 3f, area,
                    Vector3.Min(a.Position, Vector3.Min(b.Position, c.Position)), Vector3.Max(a.Position, Vector3.Max(b.Position, c.Position)));
                (y > 0f ? floors : ceilings).Add(face);
            }
        }

        return (floors, ceilings);
    }

    // Weighted k-means (height scaled): seeds the largest floor, then the farthest from those so far.
    private static List<List<Face>> Cluster(IReadOnlyList<Face> floors, int k)
    {
        var centres = new List<Vector3> { Key(floors.MaxBy(f => f.Area).Centre) };
        while (centres.Count < k)
        {
            centres.Add(Key(floors.MaxBy(f => centres.Min(c => Vector3.DistanceSquared(c, Key(f.Centre)))).Centre));
        }

        var groups = new List<List<Face>>();
        for (var i = 0; i < Iterations; i++)
        {
            groups = Assign(floors, centres);
            for (var c = 0; c < k; c++)
            {
                var area = groups[c].Sum(f => f.Area);
                if (area > 0f)
                {
                    centres[c] = groups[c].Aggregate(Vector3.Zero, (sum, f) => sum + Key(f.Centre) * f.Area) / area;
                }
            }
        }

        return groups;
    }

    // Each floor to its nearest centre.
    private static List<List<Face>> Assign(IReadOnlyList<Face> floors, List<Vector3> centres)
    {
        var groups = centres.Select(_ => new List<Face>()).ToList();
        foreach (var f in floors)
        {
            var p = Key(f.Centre);
            var best = 0;
            for (var c = 1; c < centres.Count; c++)
            {
                if (Vector3.DistanceSquared(centres[c], p) < Vector3.DistanceSquared(centres[best], p))
                {
                    best = c;
                }
            }

            groups[best].Add(f);
        }

        return groups;
    }

    // A cluster's probe: an eye over the floor nearest its centre of floor area; its floors' box from
    // the lowest floor to the ceiling over the probe.
    private static ProbeCapture Probe(List<Face> floors, IReadOnlyList<Face> ceilings, bool outdoor, float far)
    {
        var (min, max) = PlanBox(floors);
        var area = floors.Sum(f => f.Area);
        var centre = floors.Aggregate(Vector3.Zero, (sum, f) => sum + Key(f.Centre) * f.Area) / area;
        var under = floors.MinBy(f => Vector3.DistanceSquared(Key(f.Centre), centre));
        var floor = under.Centre.Y;
        var spot = Plan(under.Centre);
        var ceiling = outdoor ? floor + DefaultHeight : Ceiling(ceilings, spot, floor);
        var margin = outdoor ? OutdoorMargin : InteriorMargin;
        var top = MathF.Max(ceiling, floors.Max(f => f.Max.Y));
        var box = new Bounds(new Vector3(min.X - margin, floors.Min(f => f.Min.Y) - margin, min.Y - margin),
            new Vector3(max.X + margin, top + margin, max.Y + margin));
        var eye = MathF.Min(floor + EyeHeight, (floor + ceiling) * 0.5f);
        return new ProbeCapture(new Vector3(spot.X, eye, spot.Y), box)
        {
            Projection = box,
            Size = Size,
            Near = Near,
            Far = far,
        };
    }

    // The lowest ceiling over the spot (an eye up at least); else a default room.
    private static float Ceiling(IReadOnlyList<Face> ceilings, Vector2 spot, float floor)
    {
        var best = floor + DefaultHeight;
        var found = false;
        foreach (var c in ceilings)
        {
            if (spot.X < c.Min.X || spot.X > c.Max.X || spot.Y < c.Min.Z || spot.Y > c.Max.Z || c.Centre.Y < floor + EyeHeight)
            {
                continue;
            }

            best = found ? MathF.Min(best, c.Centre.Y) : c.Centre.Y;
            found = true;
        }

        return best;
    }

    private static (Vector2 Min, Vector2 Max) PlanBox(IEnumerable<Face> faces)
    {
        var (min, max) = (new Vector2(float.MaxValue), new Vector2(float.MinValue));
        foreach (var f in faces)
        {
            (min, max) = (Vector2.Min(min, Plan(f.Min)), Vector2.Max(max, Plan(f.Max)));
        }

        return (min, max);
    }

    private static Vector2 Plan(Vector3 p) => new(p.X, p.Z);

    // A floor's place in the clustering: plan, and height LevelWeight times.
    private static Vector3 Key(Vector3 p) => new(p.X, p.Y * LevelWeight, p.Z);
}
