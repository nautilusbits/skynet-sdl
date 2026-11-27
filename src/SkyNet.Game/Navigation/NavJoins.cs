using DotRecast.Core.Numerics;
using DotRecast.Detour;

namespace SkyNet.Game.Navigation;

/// <summary>
/// Cuts the false joins of a linked mesh: neighbour links whose two polygons' surfaces lie more
/// than the agent's step apart along the edge they share. Detour joins tiles when the border
/// edges' heights differ by under twice the climb (or cross), so a street and a deck 130-160 up
/// meet at a tile border (MAP.605's overpass); within a tile the coarse polygon can hide it too.
/// <code>
///   deck ═════════╗          the shared edge, sampled at Samples points:
///                 ║ 150 u     |surface(A) − surface(B)| &gt; step anywhere ─► cut A→B and B→A
///   street ───────╨──────
/// </code>
/// Detour links on every AddTile, so the cut runs after each bake pass and each load. A cut edge
/// is open (no link on it), and <see cref="NavDrops"/> may then link it as a drop or a climb.
/// </summary>
internal static class NavJoins
{
    /// <summary>Points along a shared edge, ends kept inside: a slope crossing the edge shows.</summary>
    private static readonly float[] Samples = [0.05f, 0.275f, 0.5f, 0.725f, 0.95f];
    /// <summary>A link's portion of its edge is in 255ths (Detour's bmin/bmax); 0xff: not on an edge.</summary>
    private const float PortionScale = 255f;
    private const byte NoEdge = 0xff;

    /// <summary>Cuts <paramref name="nav"/>'s joins steeper than <paramref name="step"/>; how many links went.</summary>
    internal static int Cut(DtNavMesh nav, float step)
    {
        var cut = new HashSet<(long, long)>();
        for (var i = 0; i < nav.GetMaxTiles(); i++)
        {
            var tile = nav.GetTile(i);
            if (tile?.data?.header == null)
            {
                continue;
            }

            Find(nav, tile, step, cut);
        }

        if (cut.Count == 0)
        {
            return 0;
        }

        var removed = 0;
        for (var i = 0; i < nav.GetMaxTiles(); i++)
        {
            var tile = nav.GetTile(i);
            if (tile?.data?.header == null)
            {
                continue;
            }

            removed += Unlink(nav, tile, cut);
        }

        return removed;
    }

    /// <summary>Adds the false joins of <paramref name="tile"/>'s polygons to <paramref name="cut"/> (both ways).</summary>
    private static void Find(DtNavMesh nav, DtMeshTile tile, float step, HashSet<(long, long)> cut)
    {
        var refBase = nav.GetPolyRefBase(tile);
        for (var p = 0; p < tile.data.header.polyCount; p++)
        {
            var poly = tile.data.polys[p];
            if (poly.GetPolyType() == DtPolyTypes.DT_POLYTYPE_OFFMESH_CONNECTION)
            {
                continue;
            }

            var self = refBase | (long)p;
            for (var l = poly.firstLink; l != DtDetour.DT_NULL_LINK; l = tile.links[l].next)
            {
                var link = tile.links[l];
                if (link.edge == NoEdge || cut.Contains((self, link.refs)) || !Steep(nav, tile, poly, self, link, step))
                {
                    continue;
                }

                cut.Add((self, link.refs));
                cut.Add((link.refs, self));
            }
        }
    }

    /// <summary>The two sides of <paramref name="link"/>'s edge lie more than <paramref name="step"/> apart somewhere.</summary>
    private static bool Steep(DtNavMesh nav, DtMeshTile tile, DtPoly poly, long self, DtLink link, float step)
    {
        nav.GetTileAndPolyByRefUnsafe(link.refs, out _, out var other);
        if (other.GetPolyType() == DtPolyTypes.DT_POLYTYPE_OFFMESH_CONNECTION)
        {
            return false;
        }

        // The edge's part this link covers: all of it inside a tile, a stretch across a border.
        var v = tile.data.verts;
        var a = poly.verts[link.edge] * 3;
        var b = poly.verts[(link.edge + 1) % poly.vertCount] * 3;
        var lo = link.side == NoEdge ? 0f : link.bmin / PortionScale;
        var hi = link.side == NoEdge ? 1f : link.bmax / PortionScale;
        foreach (var s in Samples)
        {
            var t = lo + (hi - lo) * s;
            var at = new RcVec3f(v[a] + (v[b] - v[a]) * t, v[a + 1] + (v[b + 1] - v[a + 1]) * t, v[a + 2] + (v[b + 2] - v[a + 2]) * t);
            nav.ClosestPointOnPoly(self, at, out var mine, out _);
            nav.ClosestPointOnPoly(link.refs, at, out var theirs, out _);
            if (MathF.Abs(mine.Y - theirs.Y) > step)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Frees <paramref name="tile"/>'s links listed in <paramref name="cut"/>; how many.</summary>
    private static int Unlink(DtNavMesh nav, DtMeshTile tile, HashSet<(long, long)> cut)
    {
        var refBase = nav.GetPolyRefBase(tile);
        var removed = 0;
        for (var p = 0; p < tile.data.header.polyCount; p++)
        {
            var poly = tile.data.polys[p];
            var self = refBase | (long)p;
            var prev = DtDetour.DT_NULL_LINK;
            var l = poly.firstLink;
            while (l != DtDetour.DT_NULL_LINK)
            {
                var link = tile.links[l];
                var next = link.next;
                if (link.edge == NoEdge || !cut.Contains((self, link.refs)))
                {
                    prev = l;
                    l = next;
                    continue;
                }

                // Out of the polygon's chain, onto the tile's free list.
                if (prev == DtDetour.DT_NULL_LINK)
                {
                    poly.firstLink = next;
                }
                else
                {
                    tile.links[prev].next = next;
                }

                link.next = tile.linksFreeList;
                tile.linksFreeList = l;
                removed++;
                l = next;
            }
        }

        return removed;
    }

    /// <summary>Edge <paramref name="edge"/> of <paramref name="poly"/> keeps a neighbour link (not open).</summary>
    internal static bool Linked(DtMeshTile tile, DtPoly poly, int edge)
    {
        for (var l = poly.firstLink; l != DtDetour.DT_NULL_LINK; l = tile.links[l].next)
        {
            if (tile.links[l].edge == edge)
            {
                return true;
            }
        }

        return false;
    }
}
