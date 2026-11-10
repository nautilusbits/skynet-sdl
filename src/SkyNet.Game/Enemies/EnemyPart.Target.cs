using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using RetroEng.Physics;
using SkyNet.Formats;
using SkyNet.Game.Weapons;

namespace SkyNet.Game.Enemies;

/// <summary>A segment as a weapons target (issue #58, v1.01): ObjHit (0x139819) drains its own hit
/// points; a shot finds it by its drawn frame's triangles (0x144600), a blast measures to its origin
/// padded by its model's radius (0x124886).</summary>
public sealed partial class EnemyPart
{
    /// <summary>0x129a9d clamps a segment's hit points to a short.</summary>
    public const int MaxHp = 0x7FFF;
    private const int ModelRadiusField = 0x0C, ModelRadiusEnd = ModelRadiusField + 4;

    /// <summary>Each model's triangles per frame, in its own Y-up axes, made on first use.</summary>
    private static readonly ConditionalWeakTable<Model3D, Triangle[]?[]> Shapes = [];

    /// <summary>The robot the segment belongs to.</summary>
    public Enemy? Robot { get; internal set; }
    public float Hp { get; internal set; }
    /// <summary>The type's hit points, as the segment is built.</summary>
    internal float FullHp => Math.Clamp(Type?.HitPoints ?? 0, 0, MaxHp);
    /// <summary>Shot off (EnemyKill on this segment or an ancestor): not drawn, not run, not struck.</summary>
    public bool Gone { get; internal set; }
    /// <summary>Its actor's perception (0x13c4da): the counter it looks at 0 of, the last answer
    /// (flag 0x100) and a hit's alert not yet spent (0x8000). The root's are the robot's.</summary>
    public int SenseCount { get; internal set; }
    public bool Perceived { get; internal set; }
    public bool Alerted { get; internal set; }

    public Vector3 Centre => Model is { } m ? Vector3.Transform((m.Bounds.Min + m.Bounds.Max) * 0.5f, World) : World.Translation;
    /// <summary>The bounds' sphere: what a ray must cross before its triangles are tried.</summary>
    public float Radius => Model is { } m ? (m.Bounds.Max - m.Bounds.Min).Length() * 0.5f : 0f;
    /// <summary>Struck by shots and blasts: a dormant robot's segments are, its root is not (0x13aa1e
    /// tests the root record's dormant bit, 0x13aa44 only the root actor's hidden flag).</summary>
    public bool Alive => !Gone && Robot is { Dead: false, Hidden: false } r && (r.Alive || this != r.Root);
    public object Body => (object?)Robot ?? this;
    public Vector3 BlastOrigin => World.Translation;

    /// <summary>The model's radius (.3D +0x0C, 1/256 units).</summary>
    public float BlastPad => Model is { Model.Header: { Length: >= ModelRadiusEnd } h }
        ? BinaryPrimitives.ReadInt32LittleEndian(h.AsSpan()[ModelRadiusField..]) / 256f
        : Blast.Pad;

    public void TakeDamage(in Damage damage) => Robot?.Hit(this, damage);

    /// <summary>The nearest of the drawn frame's triangles along the ray (both sides, as the
    /// collision does), in the segment's own axes: its placement is rigid.</summary>
    public bool Ray(Vector3 origin, Vector3 direction, float max, out float distance)
    {
        // The quick reject: the sphere round the origin through the drawn frame's farthest vertex.
        distance = max;
        if (Model is not { } m || !DamageTargets.RaySphere(origin, direction, World.Translation, Reach(m), out var near) || near > max
            || !Matrix4x4.Invert(World, out var inverse))
        {
            return false;
        }

        var o = Vector3.Transform(origin, inverse);
        var d = Vector3.TransformNormal(direction, inverse);
        var hit = false;
        foreach (var tri in Triangles(m.Model, Frame))
        {
            if (Intersect.Ray(o, d, in tri, out var t) && t <= distance)
            {
                (distance, hit) = (t, true);
            }
        }

        return hit;
    }

    private float Reach(EnemyModelInfo m) => m.Tips.Length > 0 ? m.Tips[Math.Clamp(Frame, 0, m.Tips.Length - 1)].Length() + 1f : Radius;

    private static Triangle[] Triangles(Model3D model, int frame)
    {
        var frames = Shapes.GetValue(model, x => new Triangle[]?[Math.Max(x.Frames.Length, 1)]);
        frame = Math.Clamp(frame, 0, frames.Length - 1);
        return frames[frame] ??= Build(model, frame);
    }

    private static Triangle[] Build(Model3D model, int frame)
    {
        var verts = model.Frames.IsDefaultOrEmpty ? [] : model.Frames[frame];
        var list = new List<Triangle>();
        foreach (var face in model.Faces)
        {
            foreach (var (a, b, c) in Model3D.TriangleFan(face))
            {
                int ia = face.Indices[a], ib = face.Indices[b], ic = face.Indices[c];
                if (ia < verts.Length && ib < verts.Length && ic < verts.Length)
                {
                    list.Add(new Triangle(verts[ia].ToYUp(), verts[ib].ToYUp(), verts[ic].ToYUp()));
                }
            }
        }

        return [.. list];
    }
}
