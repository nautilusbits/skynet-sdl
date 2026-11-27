using System.Numerics;
using DotRecast.Core;
using DotRecast.Core.Numerics;
using DotRecast.Detour;
using DotRecast.Recast;
using DotRecast.Recast.Geom;

namespace SkyNet.Game.Navigation;

/// <summary>
/// The DotRecast driver: triangle soup in, tiled Detour mesh out. Nothing above
/// <see cref="NavMesh"/> sees a DotRecast type.
/// <code>
///   triangles ─► voxels per tile (Recast) ─► polygons + detail ─► Detour tiles ─► DtNavMesh
///                 cell = NavBuild.CellSize × CellHeight, agent = NavAgent
/// </code>
/// Tiles bake in parallel but join the mesh in (x, z) order, so a bake is the same on every run.
/// </summary>
internal static class NavRecast
{
    /// <summary>Cells per tile side: 64 × 8 u = a 512 u tile at the soldier's cell.</summary>
    private const int TileCells = 64;
    private const int VertsPerPoly = 6;
    /// <summary>Recast's region sizes in cells (its demo's defaults): smaller islands drop, smaller
    /// regions merge.</summary>
    private const int MinRegionCells = 8, MergeRegionCells = 20;
    private const float EdgeMaxCells = 12f, EdgeMaxError = 1.3f;
    /// <summary>Detail mesh: height samples every 6 cells, within 1 cell height of the ground.</summary>
    private const float DetailSample = 6f, DetailError = 1f;
    /// <summary>Recast's walkable area id, and the one flag every walkable polygon carries.</summary>
    private const int WalkableArea = 63, WalkFlag = 1;
    private const int MaxPolysPerTile = 1 << 14;

    internal static int VertsPerPolygon => VertsPerPoly;

    /// <summary>Bakes <paramref name="verts"/> (x, y, z per corner) and <paramref name="faces"/>
    /// (three corners each) inside <paramref name="min"/>-<paramref name="max"/>.</summary>
    /// <param name="world">The collision the drops are probed in (<see cref="NavAgent.MaxDrop"/>).</param>
    internal static DtNavMesh Bake(float[] verts, int[] faces, Vector3 min, Vector3 max, NavAgent agent, NavBuild build,
        RetroEng.Physics.CollisionWorld world)
    {
        var geom = new RcSampleInputGeomProvider(verts, faces);
        var cs = build.CellSize;
        var cfg = new RcConfig(true, TileCells, TileCells, RcConfig.CalcBorder(agent.Radius, cs), RcPartition.WATERSHED,
            cs, build.CellHeight, agent.MaxSlopeDegrees, agent.Height, agent.Radius, agent.StepHeight,
            MinRegionCells * MinRegionCells * cs * cs, MergeRegionCells * MergeRegionCells * cs * cs,
            EdgeMaxCells * cs, EdgeMaxError, VertsPerPoly, DetailSample, DetailError,
            true, true, true, new RcAreaModification(WalkableArea), true);

        // The world's box, not the soup's: a bake covers the same tiles whatever lies in it.
        var bmin = Rc(min);
        var bmax = Rc(max);
        var tiles = BuildTiles(geom, cfg, bmin, bmax, build);

        RcRecast.CalcTileCount(bmin, bmax, cs, TileCells, TileCells, out var tw, out var th);
        var param = new DtNavMeshParams
        {
            orig = bmin,
            tileWidth = TileCells * cs,
            tileHeight = TileCells * cs,
            maxTiles = Math.Max(1, tw * th),
            maxPolys = MaxPolysPerTile,
        };
        var nav = Assemble(tiles, param, agent, cfg, null);
        if (agent.MaxDrop <= agent.StepHeight)
        {
            return nav;
        }

        // A second pass with the drop links found on the first mesh, each in the tile it starts in.
        var drops = NavDrops.Find(nav, agent, world);
        var size = TileCells * cs;
        // Each link in the tile its start lies in, the start kept off the tile's border (Detour
        // takes a start on the border for one outside the tile), the end at most a tile over.
        var byTile = drops
            .Select(d => (Tile: ((int)MathF.Floor((d.From.X - bmin.X) / size), (int)MathF.Floor((d.From.Z - bmin.Z) / size)), Link: d))
            .Select(t => (t.Tile, Link: (From: Inside(t.Link.From, bmin, size, t.Tile), t.Link.To)))
            .Where(t => MathF.Abs(t.Link.To.X - t.Link.From.X) < size && MathF.Abs(t.Link.To.Z - t.Link.From.Z) < size)
            .ToLookup(t => t.Tile, t => t.Link);
        return Assemble(tiles, param, agent, cfg, byTile);
    }

    /// <summary><paramref name="p"/> moved BorderGap inside tile <paramref name="tile"/> on X and Z.</summary>
    private static Vector3 Inside(Vector3 p, RcVec3f bmin, float size, (int X, int Z) tile)
    {
        var x0 = bmin.X + tile.X * size;
        var z0 = bmin.Z + tile.Z * size;
        return new Vector3(Math.Clamp(p.X, x0 + BorderGap, x0 + size - BorderGap), p.Y, Math.Clamp(p.Z, z0 + BorderGap, z0 + size - BorderGap));
    }

    /// <summary>How far inside its tile a link's start is kept.</summary>
    private const float BorderGap = 2f;
    /// <summary>The tiles as one mesh, in (z, x) order; each with the drops that start in it; false joins cut.</summary>
    private static DtNavMesh Assemble(List<RcBuilderResult> tiles, DtNavMeshParams param, NavAgent agent, RcConfig cfg,
        ILookup<(int, int), (Vector3 From, Vector3 To)>? drops)
    {
        var nav = new DtNavMesh();
        nav.Init(in param, VertsPerPoly);
        foreach (var r in tiles.OrderBy(t => t.TileZ).ThenBy(t => t.TileX))
        {
            var mine = drops?[(r.TileX, r.TileZ)].ToList() ?? [];

            // A link landing in another tile takes a third link slot from this tile's pool, which
            // Detour sizes at two a link (DotRecast then runs past it): a spare zero-length link at
            // its start buys the slots.
            var size = param.tileWidth;
            var x0 = param.orig.X + r.TileX * size;
            var z0 = param.orig.Z + r.TileZ * size;
            var spares = mine.Where(l => l.To.X < x0 || l.To.X >= x0 + size || l.To.Z < z0 || l.To.Z >= z0 + size).Select(l => (l.From, l.From)).ToList();
            mine.AddRange(spares);
            if (TileData(r, agent, cfg, mine) is not { } data)
            {
                continue;
            }

            nav.AddTile(data, 0, 0, out _);
        }

        NavJoins.Cut(nav, agent.StepHeight);
        return nav;
    }

    /// <summary>Every tile of the box (near a spot, when the build has spots), baked on the thread pool.</summary>
    private static List<RcBuilderResult> BuildTiles(IRcInputGeomProvider geom, RcConfig cfg, RcVec3f bmin, RcVec3f bmax, NavBuild build)
    {
        RcRecast.CalcTileCount(bmin, bmax, cfg.Cs, TileCells, TileCells, out var tw, out var th);
        var results = new RcBuilderResult[tw * th];
        // DotRecast's console diagnostics go to the log (RecastOutput).
        RecastOutput.Hold();
        try
        {
            Parallel.For(0, tw * th, i =>
            {
                var tx = i % tw;
                var tz = i / tw;
                if (!Wanted(build, bmin, tx, tz))
                {
                    return;
                }

                var builder = new RcBuilder();
                using var quiet = RecastOutput.Quiet();
                results[i] = builder.BuildTile(geom, cfg, bmin, bmax, tx, tz, new RcAtomicInteger(0), tw * th, false);
            });
        }
        finally
        {
            RecastOutput.Release();
        }

        return [.. results.Where(r => r?.Mesh != null)];
    }

    /// <summary>Tile (tx, tz) is within the build's reach of a spot (always, without spots).</summary>
    private static bool Wanted(NavBuild build, RcVec3f bmin, int tx, int tz)
    {
        if (build.Spots is not { } spots)
        {
            return true;
        }

        var size = TileCells * build.CellSize;
        var lo = new Vector2(bmin.X + tx * size, bmin.Z + tz * size);
        var hi = lo + new Vector2(size);
        var reach2 = build.Reach * build.Reach;
        foreach (var p in spots)
        {
            if (Vector2.DistanceSquared(p, Vector2.Clamp(p, lo, hi)) <= reach2)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>One baked tile as Detour data; null when it holds no walkable polygon.</summary>
    private static DtMeshData? TileData(RcBuilderResult r, NavAgent agent, RcConfig cfg, List<(Vector3 From, Vector3 To)> drops)
    {
        var mesh = r.Mesh;
        var detail = r.MeshDetail;
        if (mesh.npolys == 0)
        {
            return null;
        }

        var flags = new int[mesh.npolys];
        Array.Fill(flags, WalkFlag);
        var p = new DtNavMeshCreateParams
        {
            verts = mesh.verts,
            vertCount = mesh.nverts,
            polys = mesh.polys,
            polyAreas = mesh.areas,
            polyFlags = flags,
            polyCount = mesh.npolys,
            nvp = mesh.nvp,
            detailMeshes = detail?.meshes,
            detailVerts = detail?.verts,
            detailVertsCount = detail?.nverts ?? 0,
            detailTris = detail?.tris,
            detailTriCount = detail?.ntris ?? 0,
            walkableHeight = agent.Height,
            walkableRadius = agent.Radius,
            walkableClimb = agent.StepHeight,
            bmin = mesh.bmin,
            bmax = mesh.bmax,
            cs = cfg.Cs,
            ch = cfg.Ch,
            buildBvTree = true,
            tileX = r.TileX,
            tileZ = r.TileZ,
        };
        if (drops.Count > 0)
        {
            // One-way links (dir 0), the walk flag, as wide as the agent.
            p.offMeshConCount = drops.Count;
            p.offMeshConVerts = new float[drops.Count * 6];
            p.offMeshConRad = new float[drops.Count];
            p.offMeshConFlags = new int[drops.Count];
            p.offMeshConAreas = new int[drops.Count];
            p.offMeshConDir = new int[drops.Count];
            p.offMeshConUserID = new int[drops.Count];
            for (var i = 0; i < drops.Count; i++)
            {
                var (a, b) = drops[i];
                (p.offMeshConVerts[i * 6], p.offMeshConVerts[i * 6 + 1], p.offMeshConVerts[i * 6 + 2]) = (a.X, a.Y, a.Z);
                (p.offMeshConVerts[i * 6 + 3], p.offMeshConVerts[i * 6 + 4], p.offMeshConVerts[i * 6 + 5]) = (b.X, b.Y, b.Z);
                p.offMeshConRad[i] = agent.Radius;
                p.offMeshConFlags[i] = WalkFlag;
                p.offMeshConAreas[i] = WalkableArea;
                p.offMeshConUserID[i] = i;
            }
        }

        return DtNavMeshBuilder.CreateNavMeshData(p);
    }

    internal static RcVec3f Rc(Vector3 v) => new(v.X, v.Y, v.Z);

    internal static Vector3 Num(RcVec3f v) => new(v.X, v.Y, v.Z);
}
