using System.Numerics;
using SkyNet.Formats;
using SkyNet.Game.Player;

namespace SkyNet.Game.Vehicles;

/// <summary>
/// The map's own fence (DOS FUN_00122711 builds the boxes, FUN_00122789 checks every commit of the
/// player's move; the Godot port's LevelLoader.border_boxes and fly_camera._border_clamp). PAIRS of
/// markers of one type 30-39, in record order, span boxes in x/z; every move of the player — on
/// foot, in the jeep or in the HK — has to end inside one of them, or it is UNDONE and the speed
/// zeroed. That is MAP.260's invisible wall, which keeps the jeep in the town and on the inner
/// highway lane. A map without such markers has no fence.
/// <para>Within <see cref="HintMargin"/> of an edge or of the map's extent, standing in one box only (or on an unfenced map), the engine prints hint slot 8 ([G9]; on MAP.260
/// "The highway is the other way.") once, re-armed when the player leaves the band.</para>
/// <para>The Godot port's refuge rule: when the position before the move is outside every box too
/// (a spawn, a loaded save, a stuck reset put the player there), the move stands — restoring it
/// would freeze him for good — and the fence holds from the moment he is inside a box.</para>
/// </summary>
public sealed class MapBorder
{
    /// <summary>The hint band along an edge (world units).</summary>
    public const float HintMargin = 64f;
    /// <summary>The hint slot the edge prints ([G9]).</summary>
    public const int HintSlot = 8;
    public const int FirstType = 30, LastType = 39;

    /// <summary>The map's extent inside its grid (FUN_001369d5): DOS x and z from this far in.</summary>
    public const int ExtentInset = 0x100;

    private readonly (Vector2 Min, Vector2 Max)[] _boxes;
    // World x/z; null: no extent (a test's fence).
    private readonly (Vector2 Min, Vector2 Max)? _extent;
    private bool _hinted;

    public MapBorder(IEnumerable<(Vector2 Min, Vector2 Max)> boxes, (Vector2 Min, Vector2 Max)? extent = null)
    {
        _boxes = [.. boxes];
        _extent = extent;
    }

    public static MapBorder None { get; } = new([]);

    /// <summary>The boxes in world x/z (x, −z of the records), min/max corners.</summary>
    public IReadOnlyList<(Vector2 Min, Vector2 Max)> Boxes => _boxes;
    public bool Fenced => _boxes.Length > 0;

    /// <summary>The fence of <paramref name="map"/>: for each type 30..39, its markers paired in
    /// record order (an odd one left over spans nothing).</summary>
    public static MapBorder From(MapFile map)
    {
        var boxes = new List<(Vector2, Vector2)>();
        for (var type = FirstType; type <= LastType; type++)
        {
            var pts = map.Entities
                .Where(e => e.Variant == MapEntityVariant.Sprite && e.IsMarker && e.MarkerType == type)
                .Select(e => new Vector2(e.X, -e.Z))
                .ToList();
            for (var i = 0; i + 1 < pts.Count; i += 2)
            {
                boxes.Add((Vector2.Min(pts[i], pts[i + 1]), Vector2.Max(pts[i], pts[i + 1])));
            }
        }

        // DOS 0x100 .. grid × 0x400 − 0x101 on x and z; world z is −DOS z.
        var far = new Vector2(map.GridWidth * 0x400 - ExtentInset - 1, map.GridHeight * 0x400 - ExtentInset - 1);
        var extent = (new Vector2(ExtentInset, -far.Y), new Vector2(far.X, -ExtentInset));
        return new MapBorder(boxes, extent);
    }

    /// <summary>Is the x/z point inside one of the boxes (min inclusive, max exclusive, as Godot's Rect2)?</summary>
    public bool Inside(Vector3 p) => BoxAt(p) >= 0;

    private int BoxAt(Vector3 p)
    {
        for (var i = 0; i < _boxes.Length; i++)
        {
            var (min, max) = _boxes[i];
            if (p.X >= min.X && p.X < max.X && p.Z >= min.Y && p.Z < max.Y)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>FUN_00122789's band: inside one box at most (none on an unfenced map) and within
    /// <see cref="HintMargin"/> of an edge, the hint prints once; elsewhere it re-arms. True: print.</summary>
    private bool Band(float edge, int boxes)
    {
        if (edge > HintMargin || boxes > 1)
        {
            _hinted = false;
            return false;
        }

        if (_hinted)
        {
            return false;
        }

        _hinted = true;
        return true;
    }

    /// <summary>How far in from the map's extent <paramref name="p"/> stands; null outside it.</summary>
    private float? ExtentEdge(Vector3 p)
    {
        if (_extent is not var (min, max))
        {
            return float.PositiveInfinity;
        }

        if (p.X < min.X || p.X > max.X || p.Z < min.Y || p.Z > max.Y)
        {
            return null;
        }

        return MathF.Min(MathF.Min(p.X - min.X, max.X - p.X), MathF.Min(p.Z - min.Y, max.Y - p.Z));
    }

    private int BoxCount(Vector3 p)
    {
        var count = 0;
        foreach (var (min, max) in _boxes)
        {
            if (p.X >= min.X && p.X < max.X && p.Z >= min.Y && p.Z < max.Y)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>After a move from <paramref name="before"/>: puts the body back and stops it when the
    /// move left the fence (true; the caller zeroes a vehicle's own speed too).
    /// <paramref name="hint"/>: the edge band was just entered, print the hint.</summary>
    public bool Clamp(PlayerController body, Vector3 before, out bool hint)
    {
        hint = false;
        if (body.Noclip)
        {
            return false;
        }

        var p = body.Position;
        if (ExtentEdge(p) is not { } toExtent)
        {
            return false;                          // FUN_00122789 outside the extent: no hint either way
        }

        if (_boxes.Length == 0)
        {
            hint = Band(toExtent, 1);
            return false;
        }

        var i = BoxAt(p);
        if (i < 0)
        {
            if (!Inside(before))
            {
                return false;
            }

            body.Position = before;
            body.Velocity = Vector3.Zero;
            return true;
        }

        var (min, max) = _boxes[i];
        var edge = MathF.Min(MathF.Min(p.X - min.X, max.X - p.X), MathF.Min(p.Z - min.Y, max.Y - p.Z));
        hint = Band(MathF.Min(edge, toExtent), BoxCount(p));
        return false;
    }
}
