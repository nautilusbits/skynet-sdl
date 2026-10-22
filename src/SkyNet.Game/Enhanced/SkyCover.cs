using System.Numerics;
using RetroEng.Physics;

namespace SkyNet.Game.Enhanced;

/// <summary>The cover round the eye as the frame build reads it (<see cref="SkyCover.Capture"/>): the
/// grid's first cell (cell units) and each cell's height where the sky's rain or dust stops (world Y;
/// +∞ not known yet).</summary>
public class CoverView
{
    public int OriginX, OriginZ;
    public readonly float[] Tops = new float[SkyCover.Cells * SkyCover.Cells];

    /// <summary>The height the sky stops at over (<paramref name="x"/>, <paramref name="z"/>): a roof or
    /// the ground; +∞ outside the grid or not yet known.</summary>
    public float TopAt(float x, float z)
    {
        var ix = (int)MathF.Floor(x / SkyCover.Cell) - OriginX;
        var iz = (int)MathF.Floor(z / SkyCover.Cell) - OriginZ;
        return (uint)ix < SkyCover.Cells && (uint)iz < SkyCover.Cells ? Tops[ix + iz * SkyCover.Cells] : float.PositiveInfinity;
    }
}

/// <summary>
/// What is open to the sky round the eye (the weather's cover: rain, dust, the wind's howl), found on
/// the simulation's thread from the collision world, only read.
/// <code>
///   a ray down from high over each 48 u cell round the eye (nearest first, a few a tick) finds where
///   the sky stops: a roof or the ground; a new scene or world forgets them
/// </code>
/// </summary>
public sealed class SkyCover
{
    /// <summary>The grid: cell size (units) and cells across.</summary>
    public const float Cell = 48f;
    public const int Cells = 32;
    /// <summary>A ray starts this high over the eye (over any roof) and reaches this far under it.</summary>
    public const float SkyProbe = 4000f, GroundProbe = 2000f;
    /// <summary>Rays cast a tick at most.</summary>
    public const int RaysPerTick = 96;
    /// <summary>The cells round the ears (radius, cells) whose openness sets a sound's shelter.</summary>
    public const int HearCells = 3;

    private readonly CollisionWorld.QueryContext _query = new();
    private readonly long[] _keys = new long[Cells * Cells];
    private readonly float[] _tops = new float[Cells * Cells];
    private readonly (int X, int Z)[] _order;
    private object? _scene;
    private CollisionWorld? _world;
    private int _eyeX, _eyeZ;

    public SkyCover()
    {
        // The cells round the eye, nearest first: the cover is found where it matters first.
        List<(int X, int Z)> order = [];
        for (var z = -Cells / 2; z < Cells / 2; z++)
        {
            for (var x = -Cells / 2; x < Cells / 2; x++)
            {
                order.Add((x, z));
            }
        }

        _order = [.. order.OrderBy(c => c.X * c.X + c.Z * c.Z)];
        Clear();
    }

    /// <summary>Finds the cover round <paramref name="eye"/> over <paramref name="scene"/> from
    /// <paramref name="world"/> (null: open everywhere); returns true when the scene or world is new
    /// (the cover started over).</summary>
    public bool Tick(object? scene, CollisionWorld? world, Vector3 eye)
    {
        var fresh = !ReferenceEquals(scene, _scene) || !ReferenceEquals(world, _world);
        if (fresh)
        {
            (_scene, _world) = (scene, world);
            Clear();
        }

        (_eyeX, _eyeZ) = (CellOf(eye.X), CellOf(eye.Z));
        Find(eye);
        return fresh;
    }

    /// <summary>How open the air round the ears is (0-1): the share of the cells near them the sky
    /// reaches below the eye.</summary>
    public float Openness(Vector3 eye)
    {
        int open = 0, all = 0;
        for (var z = -HearCells; z <= HearCells; z++)
        {
            for (var x = -HearCells; x <= HearCells; x++)
            {
                if (x * x + z * z > HearCells * HearCells)
                {
                    continue;
                }

                all++;
                if (Top(_eyeX + x, _eyeZ + z) < eye.Y)
                {
                    open++;
                }
            }
        }

        return (float)open / all;
    }

    /// <summary>Copies the grid round the eye into <paramref name="view"/>.</summary>
    public void Capture(CoverView view)
    {
        (view.OriginX, view.OriginZ) = (_eyeX - Cells / 2, _eyeZ - Cells / 2);
        for (var z = 0; z < Cells; z++)
        {
            for (var x = 0; x < Cells; x++)
            {
                view.Tops[x + z * Cells] = Top(view.OriginX + x, view.OriginZ + z);
            }
        }
    }

    private void Clear() => Array.Fill(_keys, long.MinValue);

    /// <summary>Casts up to <see cref="RaysPerTick"/> rays for the cells round the eye not known yet, nearest first.</summary>
    private void Find(Vector3 eye)
    {
        var rays = 0;
        foreach (var (dx, dz) in _order)
        {
            var (x, z) = (_eyeX + dx, _eyeZ + dz);
            var slot = Slot(x, z);
            if (_keys[slot] == Key(x, z))
            {
                continue;
            }

            if (rays++ == RaysPerTick)
            {
                return;
            }

            _keys[slot] = Key(x, z);
            _tops[slot] = Probe(eye, x, z);
        }
    }

    /// <summary>Where the sky over cell (<paramref name="x"/>, <paramref name="z"/>) stops: the first
    /// surface a ray down its middle from high over the eye meets; -∞ when it meets none (or no world).</summary>
    private float Probe(Vector3 eye, int x, int z)
    {
        if (_world == null)
        {
            return float.NegativeInfinity;
        }

        var from = new Vector3((x + 0.5f) * Cell, eye.Y + SkyProbe, (z + 0.5f) * Cell);
        return _world.Raycast(_query, from, -Vector3.UnitY, SkyProbe + GroundProbe, out var hit) ? hit.Point.Y : float.NegativeInfinity;
    }

    /// <summary>A cell's top; +∞ while not known.</summary>
    private float Top(int x, int z)
    {
        var slot = Slot(x, z);
        return _keys[slot] == Key(x, z) ? _tops[slot] : float.PositiveInfinity;
    }

    private static int CellOf(float v) => (int)MathF.Floor(v / Cell);

    private static int Slot(int x, int z) => (x & (Cells - 1)) + (z & (Cells - 1)) * Cells;

    private static long Key(int x, int z) => ((long)x << 32) | (uint)z;
}
