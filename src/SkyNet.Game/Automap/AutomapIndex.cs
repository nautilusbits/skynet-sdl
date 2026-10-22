using System.Numerics;
using RetroEng.Physics;
using SkyNet.Formats;
using SkyNet.Game.Collision;

namespace SkyNet.Game.Automap;

/// <summary>A record the automap can show: a placed mesh, its home cell (DOS's grid, 1024 units)
/// and its box in the world.</summary>
internal readonly record struct AutomapEntry(int Offset, int CellX, int CellZ, Box Bounds);

/// <summary>
/// The map's records the automap knows of (SKYNET.EXE v1.01): variant-1 meshes that are not actors
/// (no robots, lights, sprites, pickups or markers). <see cref="Mark"/> stands in for DOS's
/// FUN_00136fb1, which after every frame marks seen (flags bit 0x80) each object a face of which
/// its renderer drew: here, each box the view's frustum holds within the far clip. <see cref="Shown"/>
/// is FUN_0013e300's window: the seen records whose home cell is within two cells of the eye.
/// <code>
///   cz+2 . . . . .
///   cz+1 . . . . .
///   cz   . . E . .    E = the eye's cell (x >> 10, z >> 10 in DOS's axes)
///   cz-1 . . . . .
///   cz-2 . . . . .
/// </code>
/// </summary>
public sealed class AutomapIndex
{
    /// <summary>DOS's map cell (1024 units).</summary>
    internal const float CellSize = 1024f;
    /// <summary>Cells either side of the eye's (5 × 5).</summary>
    internal const int Reach = 2;

    private readonly AutomapEntry[] _entries;

    internal AutomapIndex(IEnumerable<AutomapEntry> entries) => _entries = [.. entries];

    internal IReadOnlyList<AutomapEntry> Entries => _entries;

    /// <summary>The map's meshes with their models' boxes as placed (<see cref="LevelCollision.Placement"/>);
    /// a record whose model is missing is left out.</summary>
    public static AutomapIndex Build(MapFile map, Func<string, Model3D?> model)
    {
        var boxes = new Dictionary<string, Box?>(StringComparer.OrdinalIgnoreCase);
        var entries = new List<AutomapEntry>();
        foreach (var e in map.Entities)
        {
            if (e.Variant != MapEntityVariant.Mesh || e.IsActor || e.NameIndex < 0 || e.NameIndex >= map.Names.Length)
            {
                continue;
            }

            var name = map.Names[e.NameIndex];
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            if (!boxes.TryGetValue(name, out var local))
            {
                boxes[name] = local = BoxOf(model(name + ".3D"));
            }

            if (local is not { } box)
            {
                continue;
            }

            entries.Add(new AutomapEntry(e.FileOffset, e.CellX, e.CellZ, Placed(box, LevelCollision.Placement(e))));
        }

        return new AutomapIndex(entries);
    }

    /// <summary>Marks seen what <paramref name="viewProjection"/> holds within <paramref name="far"/>
    /// of <paramref name="eye"/>.</summary>
    public void Mark(AutomapSeen seen, in Matrix4x4 viewProjection, Vector3 eye, float far)
    {
        foreach (var e in _entries)
        {
            if (seen.Contains(e.Offset) || Distance(e.Bounds, eye) > far || !InView(e.Bounds, viewProjection))
            {
                continue;
            }

            seen.Mark(e.Offset);
        }
    }

    /// <summary>The seen records whose home cell is in the 5 × 5 cells round <paramref name="eye"/>.</summary>
    internal IEnumerable<AutomapEntry> Shown(AutomapSeen seen, Vector3 eye)
    {
        // DOS's axes: x as the port's, z the port's −Z.
        var cx = (int)MathF.Floor(eye.X / CellSize);
        var cz = (int)MathF.Floor(-eye.Z / CellSize);
        return _entries.Where(e => Math.Abs(e.CellX - cx) <= Reach && Math.Abs(e.CellZ - cz) <= Reach && seen.Contains(e.Offset));
    }

    private static Box? BoxOf(Model3D? model)
    {
        if (model is not { Faces.Length: > 0 })
        {
            return null;
        }

        var box = Box.Empty;
        foreach (var v in model.Vertices)
        {
            box = box.Include(v.ToYUp());
        }

        return box;
    }

    /// <summary>The world box round a local box's corners placed by <paramref name="placement"/>.</summary>
    private static Box Placed(Box local, in Matrix4x4 placement)
    {
        var box = Box.Empty;
        foreach (var corner in Corners(local))
        {
            box = box.Include(Vector3.Transform(corner, placement));
        }

        return box;
    }

    private static IEnumerable<Vector3> Corners(Box b)
    {
        for (var i = 0; i < 8; i++)
        {
            yield return new Vector3((i & 1) == 0 ? b.Min.X : b.Max.X, (i & 2) == 0 ? b.Min.Y : b.Max.Y, (i & 4) == 0 ? b.Min.Z : b.Max.Z);
        }
    }

    private static float Distance(Box b, Vector3 p) => Vector3.Distance(Vector3.Clamp(p, b.Min, b.Max), p);

    /// <summary>False when every corner lies past one clip plane (the box is surely out of view).</summary>
    private static bool InView(Box b, in Matrix4x4 viewProjection)
    {
        Span<Vector4> c = stackalloc Vector4[8];
        var i = 0;
        foreach (var corner in Corners(b))
        {
            c[i++] = Vector4.Transform(new Vector4(corner, 1f), viewProjection);
        }

        return !AllOut(c, v => v.X < -v.W) && !AllOut(c, v => v.X > v.W)
            && !AllOut(c, v => v.Y < -v.W) && !AllOut(c, v => v.Y > v.W)
            && !AllOut(c, v => v.W <= 0f);
    }

    private static bool AllOut(ReadOnlySpan<Vector4> corners, Func<Vector4, bool> outside)
    {
        foreach (var v in corners)
        {
            if (!outside(v))
            {
                return false;
            }
        }

        return true;
    }
}
