using System.Numerics;
using RetroEng.Editor;
using SkyNet.Formats;
using SkyNet.Game.Behaviour;

namespace SkyNet.Edit;

/// <summary>The snapping a drag applies: position to a grid (DOS units), angles to a step of 11-bit
/// units, and a moved entity onto the floor under it.</summary>
public readonly record struct SnapSettings(bool Grid = false, int GridSize = 64, bool Angle = false, int AngleStep = 128, bool Floor = false);

/// <summary>
/// One gizmo drag of an entity turned into set-field commands on the <see cref="EditDocument"/>: every
/// update sets x, y, z (translate) or pitch, yaw, roll (rotate) from where the drag began, so the
/// document merges them into one step, closed by <see cref="End"/>: one undo per drag.
/// World positions are DOS's (x, -y, -z); angles are 11-bit units as <see cref="BehaviourModel.Placement"/> turns them.
/// </summary>
public sealed class EntityDrag
{
    /// <summary>The units of a full turn (DOS 11-bit angles).</summary>
    internal const int AngleUnits = 2048;

    private readonly EditDocument _doc;
    private readonly string _layer;
    private readonly int _key;
    private readonly GizmoMode _mode;
    private readonly GizmoSpace _space;
    private readonly int _axis;
    private readonly Vector3 _origin, _axisDir;
    private readonly MapEntity _start;
    private readonly SnapSettings _snap;
    private readonly Func<Vector3, float?>? _floor;
    private readonly float _from;
    private float _lastAngle, _turned;

    private EntityDrag(EditDocument doc, string layer, int key, GizmoMode mode, GizmoSpace space, int axis, MapEntity start,
        SnapSettings snap, Func<Vector3, float?>? floor, PickRay ray)
    {
        (_doc, _layer, _key, _mode, _space, _axis, _start, _snap, _floor) = (doc, layer, key, mode, space, axis, start, snap, floor);
        _origin = BehaviourModel.Position(start);
        _axisDir = Axes(mode, space, start)[axis];
        _from = mode == GizmoMode.Translate
            ? TransformGizmo.AxisParameter(ray, _origin, _axisDir)
            : TransformGizmo.RingAngle(ray, _origin, _axisDir) ?? 0f;
        _lastAngle = _from;
        // A drag never merges into the edit before it.
        doc.EndDrag();
    }

    /// <summary>A drag of handle <paramref name="axis"/> begun by <paramref name="ray"/>; null when the
    /// entity is not in the layer's map. <paramref name="floor"/>: the floor's height under a world point.</summary>
    public static EntityDrag? Begin(EditDocument doc, string layer, int key, GizmoMode mode, GizmoSpace space, int axis,
        PickRay ray, SnapSettings snap, Func<Vector3, float?>? floor = null) =>
        doc.Map(layer).TryGetEntityAt(key, out var e) ? new EntityDrag(doc, layer, key, mode, space, axis, e, snap, floor, ray) : null;

    /// <summary>The entity's rotation (no translation) from 11-bit (pitch, yaw, roll).</summary>
    public static Matrix4x4 Rotation(int pitch, int yaw, int roll) => BehaviourModel.Placement(new Vector3(pitch, yaw, roll), Vector3.Zero);

    /// <summary>Whether an entity turns (a placed mesh has angles; lights, sprites and markers do not).</summary>
    public static bool Turns(MapEntity e) => e.Variant == MapEntityVariant.Mesh;

    /// <summary>The gizmo's three unit axes for an entity. Translate: the world's, or the entity's own
    /// (its rotation's X, Y, Z). Rotate: X, Y, Z turn pitch, yaw, roll; World shows the world's axes,
    /// Local the axes each angle really turns about (yaw innermost, then pitch, then roll).</summary>
    public static Vector3[] Axes(GizmoMode mode, GizmoSpace space, MapEntity e)
    {
        if (space == GizmoSpace.World || !Turns(e))
        {
            return [Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ];
        }

        if (mode == GizmoMode.Translate)
        {
            var r = Rotation(e.Pitch, e.Yaw, e.Roll);
            return [Unit(Vector3.TransformNormal(Vector3.UnitX, r)), Unit(Vector3.TransformNormal(Vector3.UnitY, r)), Unit(Vector3.TransformNormal(Vector3.UnitZ, r))];
        }

        var roll = Matrix4x4.CreateRotationZ(-Snap.ToRadians(e.Roll, AngleUnits));
        var pitchRoll = Matrix4x4.CreateRotationX(Snap.ToRadians(e.Pitch, AngleUnits)) * roll;
        return [Unit(Vector3.TransformNormal(Vector3.UnitX, roll)), Unit(Vector3.TransformNormal(Vector3.UnitY, pitchRoll)), Vector3.UnitZ];
    }

    private static Vector3 Unit(Vector3 v) => v.LengthSquared() > 1e-12f ? Vector3.Normalize(v) : v;

    /// <summary>The dragged handle (0 X, 1 Y, 2 Z), the mode and the space.</summary>
    public int Axis => _axis;
    public GizmoMode Mode => _mode;
    public GizmoSpace Space => _space;
    public int Key => _key;
    public string Layer => _layer;

    /// <summary>The fields the pointer's ray puts on the entity now (not yet set).</summary>
    public IReadOnlyDictionary<string, int> FieldsFor(PickRay ray)
    {
        if (_mode == GizmoMode.Translate)
        {
            var t = TransformGizmo.AxisParameter(ray, _origin, _axisDir) - _from;
            if (_snap.Grid && _space == GizmoSpace.Local && Turns(_start))
            {
                t = Snap.Grid(t, _snap.GridSize);
            }

            return Position(_origin + _axisDir * t);
        }

        if (TransformGizmo.RingAngle(ray, _origin, _axisDir) is { } a)
        {
            _turned += TransformGizmo.AngleDelta(_lastAngle, a);
            _lastAngle = a;
        }

        return Turn(Snap.ToUnits(_turned, AngleUnits));
    }

    /// <summary>The position fields of a world point: the dragged world axis snapped to the grid, the
    /// height onto the floor when that is on (unless the drag is the height itself).</summary>
    public IReadOnlyDictionary<string, int> Position(Vector3 world)
    {
        var (x, y, z) = ((int)MathF.Round(world.X), (int)MathF.Round(-world.Y), (int)MathF.Round(-world.Z));
        if (_snap.Grid && (_space == GizmoSpace.World || !Turns(_start)))
        {
            switch (_axis)
            {
                case 0: x = Snap.Grid(x, _snap.GridSize); break;
                case 1: y = Snap.Grid(y, _snap.GridSize); break;
                default: z = Snap.Grid(z, _snap.GridSize); break;
            }
        }

        var vertical = MathF.Abs(_axisDir.Y) > 0.99f;
        if (_snap.Floor && !vertical && _floor?.Invoke(new Vector3(x, -y, -z)) is { } floorY)
        {
            y = (int)MathF.Round(-floorY);
        }

        return new Dictionary<string, int> { ["x"] = x, ["y"] = y, ["z"] = z };
    }

    /// <summary>The angle fields after turning the dragged ring by <paramref name="units"/> (11-bit,
    /// right-handed about its axis), snapped to the angle step when that is on.</summary>
    public IReadOnlyDictionary<string, int> Turn(int units)
    {
        var (pitch, yaw, roll) = (_start.Pitch, _start.Yaw, _start.Roll);
        int Next(int start, int delta) => _snap.Angle ? Snap.Angle(start + delta, _snap.AngleStep, AngleUnits) : Snap.WrapAngle(start + delta, AngleUnits);
        switch (_axis)
        {
            case 0: pitch = Next(pitch, units); break;
            case 1: yaw = Next(yaw, units); break;
            // Placement turns by -roll about Z.
            default: roll = Next(roll, -units); break;
        }

        return new Dictionary<string, int> { ["pitch"] = pitch, ["yaw"] = yaw, ["roll"] = roll };
    }

    /// <summary>Moves the drag to <paramref name="ray"/>: the entity's fields set when they change (one
    /// merged step for the whole drag). True when something was set.</summary>
    public bool Update(PickRay ray) => Apply(FieldsFor(ray));

    /// <summary>Sets <paramref name="fields"/> on the entity when they differ from what it has now.</summary>
    public bool Apply(IReadOnlyDictionary<string, int> fields)
    {
        if (!_doc.Map(_layer).TryGetEntityAt(_key, out var now) || fields.All(f => Current(now, f.Key) == f.Value))
        {
            return false;
        }

        _doc.SetFields(_layer, _key, fields, label: _mode == GizmoMode.Translate ? "Move" : "Rotate");
        return true;
    }

    /// <summary>Ends the drag: the next edit is a step of its own.</summary>
    public void End() => _doc.EndDrag();

    /// <summary>An entity's position or angle field.</summary>
    public static int Current(MapEntity e, string field) => field switch
    {
        "x" => e.X,
        "y" => e.Y,
        "z" => e.Z,
        "pitch" => e.Pitch,
        "yaw" => e.Yaw,
        "roll" => e.Roll,
        _ => throw new ArgumentException($"{field} is not a position or angle field", nameof(field)),
    };
}
