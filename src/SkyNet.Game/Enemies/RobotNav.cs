using System.Numerics;
using RetroEng.Physics;
using SkyNet.Game.Campaign;
using SkyNet.Game.Navigation;
using SkyNet.Game.Player;

namespace SkyNet.Game.Enemies;

/// <summary>
/// The walking robots' nav meshes of one map (ROBOT AI IMPROVED): one per body
/// (<see cref="Enemy.NavBody"/>), baked from the static collision only over the tiles within
/// <see cref="Reach"/> of those robots' starts and the soldier's (a whole outdoor map takes
/// seconds); slow bakes are cached (<see cref="NavCache"/>). CLASSIC never builds one.
/// <code>
///   robots ─group by body─► spots ─► NavMesh per body ─► NavQuery ─► Enemy.Steer
/// </code>
/// </summary>
internal sealed class RobotNav
{
    /// <summary>How far round a start the mesh reaches: past DOS's wander (1 200) and the sight (2 000).</summary>
    internal const float Reach = 2048f;

    private readonly Dictionary<NavAgent, NavQuery> _queries = [];
    private readonly List<NavMesh> _meshes = [];

    /// <summary>The meshes (one per body), for the debug view.</summary>
    internal IReadOnlyList<NavMesh> Meshes => _meshes;

    /// <summary>The query of <paramref name="body"/>'s mesh; null when none was baked for it.</summary>
    internal NavQuery? QueryFor(NavAgent body) => _queries.GetValueOrDefault(body);

    /// <summary>The meshes for <paramref name="robots"/> on <paramref name="world"/>, round their starts (the
    /// dead too: a map visited again finds its cache)
    /// and <paramref name="extra"/> spots; cached as <paramref name="name"/>-&lt;body&gt; (null: never).</summary>
    internal static RobotNav Bake(CollisionWorld world, Robots robots, IEnumerable<Vector3> extra, string? name)
    {
        var nav = new RobotNav();
        var more = extra.Select(Flat).ToList();
        foreach (var group in robots.All.Where(r => r.NavBody != null).GroupBy(r => r.NavBody!))
        {
            var spots = group.Select(r => Flat(r.Position)).Concat(more).Distinct().ToList();
            var body = group.Key;
            var cells = (body == NavAgents.BigWalker ? NavAgents.BigCells : NavAgents.SoldierCells).Around(spots, Reach);
            var mesh = NavCache.Get(name != null ? $"{name}-{NameOf(body)}" : null, world, body, cells);
            nav._meshes.Add(mesh);
            nav._queries[body] = new NavQuery(mesh);
        }

        return nav;
    }

    /// <summary>The meshes of <paramref name="session"/>'s map for its robots; null before a map is in.</summary>
    internal static RobotNav? For(GameSession session)
    {
        if (session.StaticWorld is not { } world || session.Map is not { } map || session.Robots is not { } robots)
        {
            return null;
        }

        var start = PlayerStart.Find(map, session.Wld).Feet;
        return Bake(world, robots, [start], $"{session.Data.Game}-MAP.{session.MapNumber:D3}");
    }

    private static string NameOf(NavAgent body) =>
        body == NavAgents.Terminator ? "terminator" : body == NavAgents.BigWalker ? "big" : "walker";

    private static Vector2 Flat(Vector3 v) => new(v.X, v.Z);
}
