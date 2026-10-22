using System.Numerics;
using System.Runtime.InteropServices;
using RetroEng.Render;
using SkyNet.Game.Navigation;

namespace SkyNet.Game.Net;

/// <summary>
/// The console's <c>navmesh on</c> view: the bots' (or the campaign walkers', <see cref="Enemies.RobotNav"/>)
/// nav mesh as polygon outlines and each one's path ahead as a line from its feet through its
/// corners, drawn over the level unfogged. The
/// outlines are built once per mesh and uploaded once; the paths every frame.
/// </summary>
internal sealed class DmNavLines(Renderer renderer)
{
    /// <summary>Lines float this far above the mesh, clear of the ground they lie on.</summary>
    private const float Lift = 6f;
    /// <summary>The jeep's mesh floats higher, apart from the soldier's it overlaps.</summary>
    internal const float JeepLift = 24f;
    private static readonly Vector4 MeshColour = new(0.2f, 0.9f, 1f, 1f);
    private static readonly Vector4 PathColour = new(1f, 0.3f, 0.2f, 1f);
    private static readonly Material MeshMaterial = Material.Flat(MeshColour, RenderState.Solid) with { Unfogged = true };
    private static readonly Material PathMaterial = Material.Flat(PathColour, RenderState.Solid) with { Unfogged = true };

    private MeshHandle? _mesh, _paths;
    private Vertex[]? _uploaded;

    /// <summary>Every polygon edge of <paramref name="nav"/> as line pairs.</summary>
    internal static Vertex[] Outline(NavMesh nav) => Lifted(nav, Lift);

    /// <summary>As <see cref="Outline"/>, <paramref name="lift"/> over the mesh.</summary>
    internal static Vertex[] Lifted(NavMesh nav, float lift)
    {
        var lines = new List<Vertex>();
        foreach (var poly in nav.Outlines())
        {
            for (var i = 0; i < poly.Length; i++)
            {
                lines.Add(Point(poly[i], lift));
                lines.Add(Point(poly[(i + 1) % poly.Length], lift));
            }
        }

        return [.. lines];
    }

    /// <summary>Each bot's path ahead (feet, then its corners) as line pairs into <paramref name="into"/>.</summary>
    internal static void Paths(IEnumerable<BotBrain> bots, List<Vertex> into)
    {
        into.Clear();
        foreach (var b in bots)
        {
            Path(b.Body.Position, b.Path, into);
        }
    }

    /// <summary>One path from <paramref name="from"/> through <paramref name="corners"/> as line pairs.</summary>
    internal static void Path(Vector3 from, IEnumerable<Vector3> corners, List<Vertex> into)
    {
        foreach (var c in corners)
        {
            into.Add(Point(from));
            into.Add(Point(c));
            from = c;
        }
    }

    /// <summary>Queues what <paramref name="frame"/> caught; nothing when the view is off.</summary>
    internal void Draw(DmFrame frame) => Draw(frame.NavMesh, frame.NavPaths);

    /// <summary>Queues <paramref name="outline"/> and <paramref name="paths"/>; nothing without an outline.</summary>
    internal void Draw(Vertex[]? outline, List<Vertex> paths)
    {
        if (outline is not { Length: > 0 })
        {
            return;
        }

        _mesh ??= renderer.CreateDynamicMesh(outline.Length, 0);
        if (!ReferenceEquals(_uploaded, outline))
        {
            renderer.UpdateMesh(_mesh.Value, outline);
            _uploaded = outline;
        }

        renderer.DrawLines(_mesh.Value, 0, outline.Length, MeshMaterial);
        if (paths.Count == 0)
        {
            return;
        }

        _paths ??= renderer.CreateDynamicMesh(paths.Count, 0);
        renderer.UpdateMesh(_paths.Value, CollectionsMarshal.AsSpan(paths));
        renderer.DrawLines(_paths.Value, 0, paths.Count, PathMaterial);
    }

    private static Vertex Point(Vector3 at, float lift = Lift) => new(at + new Vector3(0f, lift, 0f), Vector2.Zero, 0xFFFFFFFFu);
}
