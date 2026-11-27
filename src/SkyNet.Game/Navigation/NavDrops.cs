using System.Numerics;
using DotRecast.Detour;
using RetroEng.Physics;

namespace SkyNet.Game.Navigation;

/// <summary>
/// One-way links at the mesh's open edges: drops (a roof's rim, a deck's side: the soldier walks
/// off and lands on walkable mesh below, unhurt) and climbs (a floor right below the edge, within
/// his step-up: he walks up onto it). Found on a first bake, added on the second.
/// <code>
///   open edge ── sample every Spacing ─► out along the edge's normal by Outward[k]
///        │                                  │ clear at the chest (no wall)?
///        │                                  ▼
///        └────── drop: StepHeight &lt; floor below &lt;= MaxDrop, on the mesh ─► link (edge → floor)
/// </code>
/// Generic: no map's spots are named; a link closer than Spacing to one kept is not added.
/// </summary>
internal static class NavDrops
{
    /// <summary>One sample this often along an open edge, and no two links' starts nearer.</summary>
    private const float Spacing = 64f;
    /// <summary>How far past the edge the landing is looked for (the walk off carries the body:
    /// at run speed it falls 160 u in under a second's 400 u).</summary>
    private static readonly float[] Outward = [48f, 80f, 120f, 160f];
    /// <summary>The landing is on the mesh within this height and the agent's width.</summary>
    private const float LandSlack = 24f;
    /// <summary>The start sits this far inside the edge (on its polygon).</summary>
    private const float Inset = 4f;
    /// <summary>The low ray over the edge (a railing a walk does not cross).</summary>
    private const float Knee = 30f;
    /// <summary>A climb link: a rise over MinClimb (less is walked), its foot within ClimbOut of the
    /// edge. Its top is the soldier's step-up: measured on PlayerController, a run and a jump onto
    /// a ledge reach 82 u, the step-up alone too (the jump adds nothing: there is no step-up in the air).</summary>
    private const float MinClimb = 16f, ClimbOut = 96f;
    /// <summary>The step-up's reach over its 80 (measured: 82 u climbed, 84 not).</summary>
    private const float ClimbSpare = 2f;
    /// <summary>Landings this far apart in height are other levels (a pad, the street).</summary>
    private const float LevelBand = 32f;

    /// <summary>The drops of <paramref name="nav"/> for <paramref name="agent"/>, probed in <paramref name="world"/>.</summary>
    public static List<(Vector3 From, Vector3 To)> Find(DtNavMesh nav, NavAgent agent, CollisionWorld world)
    {
        var query = new DtNavMeshQuery(nav);
        var filter = new DtQueryDefaultFilter();
        var links = new List<(Vector3 From, Vector3 To)>();
        var taken = new HashSet<(int, int, int)>();
        for (var i = 0; i < nav.GetMaxTiles(); i++)
        {
            var tile = nav.GetTile(i);
            if (tile?.data?.header == null)
            {
                continue;
            }

            for (var p = 0; p < tile.data.header.polyCount; p++)
            {
                Poly(tile, tile.data.polys[p], agent, world, query, filter, links, taken);
            }
        }

        return links;
    }

    /// <summary>The open edges (no link to a neighbour: none baked, or a false join cut) of one polygon.</summary>
    private static void Poly(DtMeshTile tile, DtPoly poly, NavAgent agent, CollisionWorld world, DtNavMeshQuery query,
        DtQueryDefaultFilter filter, List<(Vector3, Vector3)> links, HashSet<(int, int, int)> taken)
    {
        var data = tile.data;
        var centre = Vector3.Zero;
        for (var k = 0; k < poly.vertCount; k++)
        {
            centre += Vert(data, poly.verts[k]);
        }

        centre /= MathF.Max(1, poly.vertCount);
        for (var k = 0; k < poly.vertCount; k++)
        {
            if (NavJoins.Linked(tile, poly, k))
            {
                continue; // a neighbour, here or across a tile's border
            }

            var a = Vert(data, poly.verts[k]);
            var b = Vert(data, poly.verts[(k + 1) % poly.vertCount]);
            var along = new Vector2(b.X - a.X, b.Z - a.Z);
            var len = along.Length();
            if (len < 1f)
            {
                continue;
            }

            var n = new Vector3(along.Y, 0f, -along.X) / len;
            if (Vector3.Dot(n, (a + b) / 2f - centre) < 0f)
            {
                n = -n;
            }

            var count = Math.Max(1, (int)(len / Spacing));
            for (var s = 0; s < count; s++)
            {
                var at = Vector3.Lerp(a, b, (s + 0.5f) / count) - n * Inset;
                Edge(at, n, agent, world, query, filter, links, taken);
            }
        }
    }

    /// <summary>The first landing out from <paramref name="at"/> along <paramref name="n"/>, if any.</summary>
    private static void Edge(Vector3 at, Vector3 n, NavAgent agent, CollisionWorld world, DtNavMeshQuery query,
        DtQueryDefaultFilter filter, List<(Vector3, Vector3)> links, HashSet<(int, int, int)> taken)
    {

        var chest = new Vector3(0f, agent.Height * 0.5f, 0f);
        foreach (var d in Outward)
        {
            var q = at + n * d;

            // A wall or a railing at the edge (knee to head): no drop that way, and none farther.
            if (world.Raycast(at + chest, n, d, out _) || world.Raycast(at + new Vector3(0f, Knee, 0f), n, d, out _)
                || world.Raycast(at + new Vector3(0f, agent.Height, 0f), n, d, out _))
            {
                return;
            }

            if (!world.Raycast(new Vector3(q.X, at.Y + LandSlack, q.Z), -Vector3.UnitY, agent.MaxDrop + LandSlack, out var floor))
            {
                continue;
            }

            // Down past a step: a drop. Up to a step from a floor right by the edge (a dirt pile
            // by stairs that start above the ground, a pad the voxels split off): a climb up.
            var drop = at.Y - floor.Point.Y;
            var climb = drop > MinClimb && drop <= agent.StepHeight + ClimbSpare && d <= ClimbOut;
            if (!climb && (drop <= agent.StepHeight || drop > agent.MaxDrop))
            {
                continue;
            }

            // Room to land standing (no slab right over the landing).
            if (world.Raycast(floor.Point + new Vector3(0f, 1f, 0f), Vector3.UnitY, agent.Height, out _))
            {
                continue;
            }

            var box = new DotRecast.Core.Numerics.RcVec3f(agent.Radius * 2f, LandSlack, agent.Radius * 2f);
            var status = query.FindNearestPoly(NavRecast.Rc(floor.Point), box, filter, out var poly, out var land, out _);
            if (status.Failed() || poly == 0 || MathF.Abs(land.Y - floor.Point.Y) > LandSlack)
            {
                continue;
            }

            // One link a cell of the ledge to each level below it (a drop to the street and a climb
            // from the pad beside it both stay).
            var cell = ((int)MathF.Floor(at.X / Spacing), (int)MathF.Floor(at.Z / Spacing), (int)MathF.Floor(land.Y / LevelBand));
            if (!taken.Add(cell))
            {
                return;
            }

            links.Add(climb ? (NavRecast.Num(land), at) : (at, NavRecast.Num(land)));
            return;
        }
    }

    private static Vector3 Vert(DtMeshData data, int v) => new(data.verts[v * 3], data.verts[v * 3 + 1], data.verts[v * 3 + 2]);
}
