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
    private readonly BlockFilter _blocking;
    private readonly long[] _polys = new long[MaxPolys];
    private readonly DtStraightPath[] _straight = new DtStraightPath[MaxCorners];

    public NavQuery(NavMesh mesh)
    {
        _mesh = mesh;
        _query = new DtNavMeshQuery(mesh.Detour);
        _blocking = new BlockFilter(_filter);
    }

    /// <summary>The mesh point nearest <paramref name="point"/> within ± <paramref name="extents"/>.</summary>
    public bool Nearest(Vector3 point, Vector3 extents, out Vector3 onMesh) => Snap(point, extents, out _, out onMesh);

    /// <summary>The way from <paramref name="from"/> to <paramref name="to"/> into
    /// <paramref name="corners"/> (cleared first): the snapped start, the turns, the end.</summary>
    public PathResult FindPath(Vector3 from, Vector3 to, List<Vector3> corners) => FindPath(from, to, corners, null);

    /// <inheritdoc cref="FindPath(Vector3, Vector3, List{Vector3})"/>
    /// <param name="blocked">Polygons the path must not cross (<see cref="PolyAt"/>); null: none.</param>
    /// <param name="maxNodes">Polygons the search may expand at most; more: it stops and the path
    /// leads to the nearest one reached (<see cref="OutOfNodes"/>). A search for a cut-off goal
    /// expands every polygon it can reach.</param>
    internal PathResult FindPath(Vector3 from, Vector3 to, List<Vector3> corners, List<long>? blocked, int maxNodes = int.MaxValue)
    {
        corners.Clear();
        Nodes = 0;
        OutOfNodes = false;
        _blocking.Blocked = blocked;
        if (!SnapEnd(from, out var startRef, out var start) || !SnapEnd(to, out var endRef, out var end))
        {
            return PathResult.None;
        }

        var status = maxNodes == int.MaxValue
            ? _query.FindPath(startRef, endRef, NavRecast.Rc(start), NavRecast.Rc(end), _blocking, _polys, out var count, MaxPolys)
            : Sliced(startRef, endRef, start, end, maxNodes, out count);
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

    /// <summary>Detour's sliced A* (its FindPath's search, not bit for bit the same path) with at
    /// most <paramref name="maxNodes"/> polygons expanded; <see cref="Nodes"/> counts them.</summary>
    private DtStatus Sliced(long startRef, long endRef, Vector3 start, Vector3 end, int maxNodes, out int count)
    {
        count = 0;
        var status = _query.InitSlicedFindPath(startRef, endRef, NavRecast.Rc(start), NavRecast.Rc(end), _blocking, 0);
        if (status.InProgress())
        {
            status = _query.UpdateSlicedFindPath(maxNodes, out var done);
            Nodes = done;
            OutOfNodes = status.InProgress();
        }

        return status.Failed() ? status : _query.FinalizeSlicedFindPath(_polys, out count, MaxPolys);
    }

    /// <summary>Polygons the last capped <see cref="FindPath(Vector3, Vector3, List{Vector3}, List{long}?, int)"/> expanded.</summary>
    internal int Nodes { get; private set; }

    /// <summary>The last search hit its node cap before it ended: its result says nothing of the goal's reach.</summary>
    internal bool OutOfNodes { get; private set; }

    /// <summary>The polygon a path to or from <paramref name="point"/> starts or ends on (the
    /// search's own snap); 0 none.</summary>
    internal long EndPoly(Vector3 point) => SnapEnd(point, out var poly, out _) ? poly : 0;

    /// <summary>Every polygon reachable from <paramref name="starts"/> along the mesh's links (the
    /// one-way drops and climbs one way only): a goal on none is cut off from them.</summary>
    internal HashSet<long> Flood(IEnumerable<Vector3> starts)
    {
        var reached = new HashSet<long>();
        var open = new Stack<long>();
        foreach (var p in starts)
        {
            if (EndPoly(p) is var poly and not 0 && reached.Add(poly))
            {
                open.Push(poly);
            }
        }

        var nav = _mesh.Detour;
        while (open.Count > 0)
        {
            nav.GetTileAndPolyByRefUnsafe(open.Pop(), out var tile, out var poly);
            for (var l = poly.firstLink; l != DtDetour.DT_NULL_LINK; l = tile.links[l].next)
            {
                var next = tile.links[l].refs;
                if (next != 0 && reached.Add(next))
                {
                    open.Push(next);
                }
            }
        }

        return reached;
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

    /// <summary>A baked link (drop or climb) runs along <paramref name="from"/>-<paramref name="to"/> (a leg off a ledge).</summary>
    internal bool IsLink(Vector3 from, Vector3 to) => _mesh.IsLink(from, to, _mesh.Agent.Radius * 2f);

    /// <summary>The polygon at <paramref name="point"/> (an agent's size round it) and its surface there; 0 none.</summary>
    internal long PolyAt(Vector3 point, out Vector3 onMesh)
    {
        var a = _mesh.Agent;
        return Snap(point, new Vector3(a.Radius, a.Height, a.Radius), out var poly, out onMesh) ? poly : 0;
    }
    /// <summary>The default filter less a list of polygons (a bot's legs it found it cannot walk).</summary>
    private sealed class BlockFilter(DtQueryDefaultFilter inner) : IDtQueryFilter
    {
        public List<long>? Blocked { get; set; }

        public bool PassFilter(long refs, DtMeshTile tile, DtPoly poly) =>
            (Blocked == null || !Blocked.Contains(refs)) && inner.PassFilter(refs, tile, poly);

        public float GetCost(RcVec3f pa, RcVec3f pb, long prevRef, DtMeshTile prevTile, DtPoly prevPoly, long curRef, DtMeshTile curTile,
            DtPoly curPoly, long nextRef, DtMeshTile nextTile, DtPoly nextPoly) =>
            inner.GetCost(pa, pb, prevRef, prevTile, prevPoly, curRef, curTile, curPoly, nextRef, nextTile, nextPoly);
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
