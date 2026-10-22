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
/// the ground probes 0x126d00 / 0x126f1f set its height and tilt with, not a collision box.)
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
        foreach (var v in vehicles())
        {
            if (Place(v) is not { } p || !p.Bounds.Overlaps(box))
            {
                continue;
            }

            foreach (var t in p.Triangles)
            {
                if (new Box(t.Min, t.Max).Overlaps(box))
                {
                    into.Add(t);
                }
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
            _shapes[kind] = shape = model != null ? (LevelCollision.Faces(model.Model), model.FootY) : null;
        }

        return shape;
    }
}
