using System.Numerics;
using SkyNet.Formats;
using SkyNet.Game.Weapons;

namespace SkyNet.Game.Enemies;

/// <summary>
/// One segment of a robot: the root body or a child from the type's segment list (turret head →
/// gun → missile pod), with its own type, model and frame. A turret segment (state 2/8) aims one
/// axis; a machine segment (state 10: the grabber and welding arms) runs its own AIS script.
/// <see cref="Local"/> is DOS's FUN_0014e100 placement in Y-up axes (as Level places segments):
/// Ry(yaw)·Rx(pitch)·Rz(−roll) then the offset (x, −y, −z), for row vectors.
/// <para>Each segment is its own map record (v1.01 0x129a9d → 0x13996e) and so its own target:
/// its own hit points (its type's), struck by its own triangles and blasted by its own distance.</para>
/// </summary>
public sealed partial class EnemyPart : IDamageable
{
    public EnemyPart(int typeIndex, EnemyType? type, EnemyModelInfo? model, EnemyPart? parent, Vector3 offset, float yaw, float pitch, float roll)
    {
        TypeIndex = typeIndex;
        Type = type;
        Model = model;
        Parent = parent;
        Offset = offset;
        RestYaw = Yaw = yaw;
        RestPitch = Pitch = pitch;
        Roll = roll;
        Hp = FullHp;
    }

    public int TypeIndex { get; }
    public EnemyType? Type { get; }
    public EnemyModelInfo? Model { get; }
    public EnemyPart? Parent { get; }
    public List<EnemyPart> Children { get; } = [];
    public Vector3 Offset { get; }
    public float RestYaw { get; }
    public float RestPitch { get; }
    public float Roll { get; }
    /// <summary>The segment's angles now (radians); a turret turns one of them.</summary>
    public float Yaw { get; set; }
    public float Pitch { get; set; }
    /// <summary>The model frame drawn.</summary>
    public int Frame { get; set; }
    /// <summary>A machine segment's own script (state 10).</summary>
    public AisBrain? Brain { get; set; }
    /// <summary>A twin gun's muzzle X stands swapped (a negative bolt speed swaps it each shot, 0x137d66).</summary>
    public bool MuzzleSwapped { get; set; }
    /// <summary>Where the segment is in the world (after <see cref="Enemy.UpdateTransforms"/>).</summary>
    public Matrix4x4 World { get; set; } = Matrix4x4.Identity;

    public int State => Type?.State ?? 0;
    public bool IsTurret => State is 2 or 8;
    public bool IsMachine => State == 10;
    public AisFire? Fire => Type?.Fire;

    public Matrix4x4 Local =>
        Matrix4x4.CreateRotationY(Yaw) * Matrix4x4.CreateRotationX(Pitch) * Matrix4x4.CreateRotationZ(-Roll)
        * Matrix4x4.CreateTranslation(Offset);

    /// <summary>This part and every segment under it, depth first.</summary>
    public IEnumerable<EnemyPart> Walk()
    {
        yield return this;
        foreach (var c in Children)
        {
            foreach (var p in c.Walk())
            {
                yield return p;
            }
        }
    }

    /// <summary>The segment's forward (its −Z) in the world.</summary>
    public Vector3 Forward => Vector3.Normalize(Vector3.TransformNormal(-Vector3.UnitZ, World));
}
