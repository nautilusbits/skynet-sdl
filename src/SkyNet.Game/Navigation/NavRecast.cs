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
    internal static DtNavMesh Bake(float[] verts, int[] faces, Vector3 min, Vector3 max, NavAgent agent, NavBuild build)
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
        var nav = new DtNavMesh();
        nav.Init(in param, VertsPerPoly);

        foreach (var r in tiles.OrderBy(t => t.TileZ).ThenBy(t => t.TileX))
        {
            if (TileData(r, agent, cfg) is not { } data)
            {
                continue;
            }

            nav.AddTile(data, 0, 0, out _);
        }

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
    private static DtMeshData? TileData(RcBuilderResult r, NavAgent agent, RcConfig cfg)
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
        return DtNavMeshBuilder.CreateNavMeshData(p);
    }

    internal static RcVec3f Rc(Vector3 v) => new(v.X, v.Y, v.Z);

    internal static Vector3 Num(RcVec3f v) => new(v.X, v.Y, v.Z);
}
