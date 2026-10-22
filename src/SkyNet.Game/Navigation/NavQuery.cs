using System.Numerics;
using DotRecast.Core.Numerics;
using DotRecast.Detour;

namespace SkyNet.Game.Navigation;

/// <summary>
/// Questions to one <see cref="NavMesh"/>: the walkable point nearest a spot, a path as corners
/// (string-pulled: straight legs between them), a straight walk along the surface. Holds its own
/// search buffers, so one query per thread.
/// </summary>
public sealed class NavQuery
{
    /// <summary>Polygons a path may cross; a longer one comes back partial.</summary>
    private const int MaxPolys = 512;
    private const int MaxCorners = 128;
    /// <summary>The search box about an end, in agent sizes: ends a little off the mesh (feet in
    /// the air, an item on a ledge) still land on it; <see cref="Wide"/> times wider as a last try.</summary>
    private const float SnapAcross = 4f, SnapUp = 2f, Wide = 8f;

    private readonly NavMesh _mesh;
    private readonly DtNavMeshQuery _query;
    private readonly DtQueryDefaultFilter _filter = new();
    private readonly long[] _polys = new long[MaxPolys];
    private readonly DtStraightPath[] _straight = new DtStraightPath[MaxCorners];

    public NavQuery(NavMesh mesh)
    {
        _mesh = mesh;
        _query = new DtNavMeshQuery(mesh.Detour);
    }

    /// <summary>The mesh point nearest <paramref name="point"/> within ± <paramref name="extents"/>.</summary>
    public bool Nearest(Vector3 point, Vector3 extents, out Vector3 onMesh) => Snap(point, extents, out _, out onMesh);

    /// <summary>The way from <paramref name="from"/> to <paramref name="to"/> into
    /// <paramref name="corners"/> (cleared first): the snapped start, the turns, the end.</summary>
    public PathResult FindPath(Vector3 from, Vector3 to, List<Vector3> corners)
    {
        corners.Clear();
        if (!SnapEnd(from, out var startRef, out var start) || !SnapEnd(to, out var endRef, out var end))
        {
            return PathResult.None;
        }

        var status = _query.FindPath(startRef, endRef, NavRecast.Rc(start), NavRecast.Rc(end), _filter, _polys, out var count, MaxPolys);
        if (status.Failed() || count == 0)
        {
            return PathResult.None;
        }

        // A partial path stops on the polygon nearest the goal: aim at its closest point.
        var partial = status.IsPartial() || _polys[count - 1] != endRef;
        if (partial)
        {
            _query.ClosestPointOnPoly(_polys[count - 1], NavRecast.Rc(end), out var near, out _);
            end = NavRecast.Num(near);
        }

        _query.FindStraightPath(NavRecast.Rc(start), NavRecast.Rc(end), _polys, count, _straight, out var n, MaxCorners, 0);
        for (var i = 0; i < n; i++)
        {
            corners.Add(NavRecast.Num(_straight[i].pos));
        }

        return partial ? PathResult.Partial : PathResult.Found;
    }

    /// <summary>Walks the surface from <paramref name="from"/> towards <paramref name="to"/>; true
    /// when an edge of the mesh stops it at <paramref name="hit"/> (false: clear, hit = to).</summary>
    public bool Raycast(Vector3 from, Vector3 to, out Vector3 hit)
    {
        hit = to;
        if (!SnapEnd(from, out var startRef, out var start))
        {
            hit = from;
            return true;
        }

        _query.Raycast(startRef, NavRecast.Rc(start), NavRecast.Rc(to), _filter, out var t, out _, _polys, out _, MaxPolys);
        if (t >= 1f)
        {
            return false;
        }

        hit = Vector3.Lerp(start, to, t);
        return true;
    }

    /// <summary>A path end on the mesh: near first, then <see cref="Wide"/> times wider.</summary>
    private bool SnapEnd(Vector3 point, out long poly, out Vector3 onMesh)
    {
        var a = _mesh.Agent;
        var box = new Vector3(a.Radius * SnapAcross, a.Height * SnapUp, a.Radius * SnapAcross);
        return Snap(point, box, out poly, out onMesh) || Snap(point, box * Wide, out poly, out onMesh);
    }

    private bool Snap(Vector3 point, Vector3 extents, out long poly, out Vector3 onMesh)
    {
        var status = _query.FindNearestPoly(NavRecast.Rc(point), NavRecast.Rc(extents), _filter, out poly, out RcVec3f near, out _);
        onMesh = NavRecast.Num(near);
        return status.Succeeded() && poly != 0;
    }
}
