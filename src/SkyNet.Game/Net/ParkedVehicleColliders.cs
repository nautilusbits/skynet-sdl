using System.Numerics;
using RetroEng.Physics;
using SkyNet.Game.Collision;
using SkyNet.Game.Enemies;
using SkyNet.Game.Vehicles;

namespace SkyNet.Game.Net;

/// <summary>
/// The parked vehicles of a deathmatch as solid geometry. SKYNET.EXE v1.01 parks each one as a map
/// record of its own (0x1310cc: 0x13996e makes a variant-1 mesh record, its model the parked
/// NETHUMMT.3D / NET_HKMT.3D handle; 0x13110d links it into the cell grid at the vehicle's place and
/// heading), so it stops bodies and shots as every placed mesh of the map does; boarding moves the
/// record 10000 away (0x130daf) and a wreck too (0x130edf). Here: the parked model's frame-0
/// triangles (<see cref="LevelCollision.Faces"/>) where <see cref="AvatarRenderer"/> draws it, for
/// every vehicle with no driver that is not wrecked. A driven vehicle is its driver's avatar.
/// (The four corners at 0x51F54 / 0x51FFE, jeep ±57 × ±72 at 45 down, HK ±50 × ±115 at 125 down, are
/// the ground probes 0x126d00 / 0x126f1f set its height and tilt with; the HK's also bound the block
/// under its belly.)
/// </summary>
public sealed class ParkedVehicleColliders(Func<IEnumerable<DmVehicle>> vehicles, EnemyModels models) : ITriangleSource
{
    private sealed record Placed(Vector3 At, float Yaw, Triangle[] Triangles, Box Bounds);

    private readonly Dictionary<VehicleMode, (Vector3[][] Faces, float FootY)?> _shapes = [];
    private readonly Dictionary<int, Placed> _placed = [];

    public Box Bounds
    {
        get
        {
            var b = Box.Empty;
            foreach (var v in vehicles())
            {
                if (Place(v) is { } p)
                {
                    b = b.Union(p.Bounds);
                }
            }

            return b;
        }
    }

    public void Gather(in Box box, List<Triangle> into)
    {
        // A dictionary's values walked as such: through the interface each query boxes an enumerator.
        var all = vehicles();
        if (all is Dictionary<int, DmVehicle>.ValueCollection table)
        {
            foreach (var v in table)
            {
                Gather(v, box, into);
            }

            return;
        }

        foreach (var v in all)
        {
            Gather(v, box, into);
        }
    }

    private void Gather(DmVehicle v, in Box box, List<Triangle> into)
    {
        if (Place(v) is not { } p || !p.Bounds.Overlaps(box))
        {
            return;
        }

        foreach (var t in p.Triangles)
        {
            if (new Box(t.Min, t.Max).Overlaps(box))
            {
                into.Add(t);
            }
        }
    }

    /// <summary>The vehicle's triangles where it stands; null while driven, wrecked or without a model.</summary>
    private Placed? Place(DmVehicle v)
    {
        if (v.Driver != 0 || v.Wrecked || Shape(v.Kind) is not { } shape)
        {
            return null;
        }

        if (_placed.TryGetValue(v.Key, out var p) && p.At == v.Position && p.Yaw == v.Yaw)
        {
            return p;
        }

        var world = Matrix4x4.CreateTranslation(0f, -shape.FootY, 0f) * Matrix4x4.CreateRotationY(v.Yaw) * Matrix4x4.CreateTranslation(v.Position);
        var tris = new Triangle[shape.Faces.Length];
        var bounds = Box.Empty;
        for (var i = 0; i < tris.Length; i++)
        {
            var f = shape.Faces[i];
            tris[i] = new Triangle(Vector3.Transform(f[0], world), Vector3.Transform(f[1], world), Vector3.Transform(f[2], world));
            bounds = bounds.Include(tris[i].A).Include(tris[i].B).Include(tris[i].C);
        }

        return _placed[v.Key] = new Placed(v.Position, v.Yaw, tris, bounds);
    }

    private (Vector3[][] Faces, float FootY)? Shape(VehicleMode kind)
    {
        if (!_shapes.TryGetValue(kind, out var shape))
        {
            var (first, second) = kind == VehicleMode.Hk
                ? (AvatarRenderer.ParkedHk, AvatarRenderer.DrivenHk)
                : (AvatarRenderer.ParkedJeep, AvatarRenderer.DrivenJeep);
            var model = models.Get(first) ?? models.Get(second);
            _shapes[kind] = shape = model != null ? (WithBelly(kind, LevelCollision.Faces(model.Model), model.FootY), model.FootY) : null;
        }

        return shape;
    }

    /// <summary>The HK's half width and half length about its marker (its ground probes, 0x51FFE).</summary>
    private const float HkHalfWidth = 50f, HkHalfLength = 115f;

    /// <summary>How high the block under the HK reaches over its feet: its belly rises from 46 to
    /// 135 towards the tail, so the gap left over the block (≤ 55) is lower than the soldier.</summary>
    private const float HkBellyTop = 80f;

    /// <summary>The HK's mesh stands on its skids with a belly the soldier walked under (MAP.601):
    /// a solid box under it, ±50 × ±115 from the feet up to <see cref="HkBellyTop"/>.</summary>
    private static Vector3[][] WithBelly(VehicleMode kind, Vector3[][] faces, float footY)
    {
        if (kind != VehicleMode.Hk)
        {
            return faces;
        }

        var (lo, hi) = (new Vector3(-HkHalfWidth, footY, -HkHalfLength), new Vector3(HkHalfWidth, footY + HkBellyTop, HkHalfLength));
        Vector3 C(int i) => new((i & 1) == 0 ? lo.X : hi.X, (i & 2) == 0 ? lo.Y : hi.Y, (i & 4) == 0 ? lo.Z : hi.Z);

        // The box's six sides as quads of corner indices (bit 0 x, bit 1 y, bit 2 z), two triangles each.
        int[][] quads = [[0, 1, 3, 2], [4, 6, 7, 5], [0, 2, 6, 4], [1, 5, 7, 3], [0, 4, 5, 1], [2, 3, 7, 6]];
        var box = quads.SelectMany(q => new[] { new[] { C(q[0]), C(q[1]), C(q[2]) }, new[] { C(q[0]), C(q[2]), C(q[3]) } });
        return [.. faces, .. box];
    }
}
