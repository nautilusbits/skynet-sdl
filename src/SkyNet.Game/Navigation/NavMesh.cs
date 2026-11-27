using System.Numerics;
using DotRecast.Core;
using DotRecast.Detour;
using DotRecast.Detour.Io;
using RetroEng.Physics;

namespace SkyNet.Game.Navigation;

/// <summary>
/// Where one agent type can walk on a map: polygons baked from the map's static collision
/// (movers and doors stay out: they are dynamic). Query it through a <see cref="NavQuery"/>.
/// <code>
///   CollisionWorld ──Build──► NavMesh ──Save/Load──► stream (versioned, source hash)
///                                │
///                                └──► NavQuery: Nearest, FindPath, Raycast
/// </code>
/// </summary>
public sealed class NavMesh
{
    /// <summary>"NAVM" and the layout's version: a change of either makes old files stale.</summary>
    private const int Magic = 0x4D56414E;
    private const int Version = 13;
    /// <summary>FNV-1a 64-bit.</summary>
    private const ulong HashStart = 14695981039346656037UL, HashPrime = 1099511628211UL;

    private NavMesh(DtNavMesh detour, NavAgent agent, NavBuild settings, ulong source)
    {
        Detour = detour;
        Agent = agent;
        Settings = settings;
        Source = source;
    }

    public NavAgent Agent { get; }
    public NavBuild Settings { get; }
    /// <summary>The hash of the collision it was baked from (<see cref="Hash"/>).</summary>
    public ulong Source { get; }

    internal DtNavMesh Detour { get; }

    /// <summary>Bakes <paramref name="world"/>'s triangles for <paramref name="agent"/>. Both sides
    /// of a triangle collide, so a face turned down counts as its upturned twin.</summary>
    public static NavMesh Build(CollisionWorld world, NavAgent agent, NavBuild settings)
    {
        var tris = Triangles(world, out var box);
        var source = Hash(tris);
        if (Clip(box, settings) is { } area)
        {
            box = area;
            tris.Clear();
            world.Gather(box, tris);
        }

        var verts = new float[tris.Count * 9];
        var faces = new int[tris.Count * 3];
        var n = 0;
        foreach (var t in tris)
        {
            if (t.Cross.LengthSquared() < 1e-6f)
            {
                continue;
            }

            // Upturned: Recast walks only faces whose normal points up.
            var (b, c) = t.Cross.Y < 0f ? (t.C, t.B) : (t.B, t.C);
            Put(verts, n * 3, t.A);
            Put(verts, n * 3 + 1, b);
            Put(verts, n * 3 + 2, c);
            faces[n * 3] = n * 3;
            faces[n * 3 + 1] = n * 3 + 1;
            faces[n * 3 + 2] = n * 3 + 2;
            n++;
        }

        Array.Resize(ref verts, n * 9);
        Array.Resize(ref faces, n * 3);
        var detour = NavRecast.Bake(verts, faces, box.Min, box.Max, agent, settings, world);
        return new NavMesh(detour, agent, settings, source);
    }

    /// <summary>The world's box cut to the settings' area on X and Z; null when no area is set.</summary>
    private static Box? Clip(Box box, NavBuild settings)
    {
        if (settings.AreaMin is not { } lo || settings.AreaMax is not { } hi)
        {
            return null;
        }

        var min = new Vector3(MathF.Max(box.Min.X, lo.X), box.Min.Y, MathF.Max(box.Min.Z, lo.Y));
        var max = new Vector3(MathF.Min(box.Max.X, hi.X), box.Max.Y, MathF.Min(box.Max.Z, hi.Y));
        return new Box(min, Vector3.Max(min, max));
    }

    /// <summary>The hash a mesh baked from <paramref name="world"/> now carries as <see cref="Source"/>.</summary>
    public static ulong Hash(CollisionWorld world) => Hash(Triangles(world, out _));

    /// <summary>Baked from this world, for this agent, at these settings: not stale.</summary>
    public bool IsFor(CollisionWorld world, NavAgent agent, NavBuild settings) =>
        Agent == agent && Settings == settings && Source == Hash(world);

    public void Save(Stream stream)
    {
        using var w = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        w.Write(Magic);
        w.Write(Version);
        w.Write(Agent.Radius);
        w.Write(Agent.Height);
        w.Write(Agent.StepHeight);
        w.Write(Agent.MaxSlopeDegrees);
        w.Write(Agent.MaxDrop);
        w.Write(Settings.CellSize);
        w.Write(Settings.CellHeight);
        WriteArea(w, Settings.AreaMin);
        WriteArea(w, Settings.AreaMax);
        WriteSpots(w, Settings);
        w.Write(Source);
        new DtMeshSetWriter().Write(w, Detour, RcByteOrder.LITTLE_ENDIAN, false);
    }

    /// <summary>A saved mesh; null when the stream holds none of this version.</summary>
    public static NavMesh? Load(Stream stream)
    {
        using var r = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        if (stream.Length - stream.Position < sizeof(int) * 2 || r.ReadInt32() != Magic || r.ReadInt32() != Version)
        {
            return null;
        }

        var agent = new NavAgent(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle()) { MaxDrop = r.ReadSingle() };
        var settings = ReadSpots(r, new NavBuild(r.ReadSingle(), r.ReadSingle(), ReadArea(r), ReadArea(r)));
        var source = r.ReadUInt64();
        var detour = new DtMeshSetReader().Read(r, NavRecast.VertsPerPolygon);
        NavJoins.Cut(detour, agent.StepHeight); // the reader links the tiles again
        return new NavMesh(detour, agent, settings, source);
    }

    /// <summary>The drop links by their start's cell (<see cref="DropCell"/> across), built on first use.</summary>
    private Dictionary<(int, int), List<(Vector3 From, Vector3 To)>>? _dropsByCell;
    private const float DropCell = 64f;

    /// <summary>A baked link (a drop, a climb) runs from within <paramref name="slack"/> of <paramref name="from"/>
    /// to within it of <paramref name="to"/> (a path's leg off a ledge).</summary>
    internal bool IsLink(Vector3 from, Vector3 to, float slack)
    {
        _dropsByCell ??= DropsByCell();
        var cx = (int)MathF.Floor(from.X / DropCell);
        var cz = (int)MathF.Floor(from.Z / DropCell);
        for (var x = cx - 1; x <= cx + 1; x++)
        {
            for (var z = cz - 1; z <= cz + 1; z++)
            {
                if (!_dropsByCell.TryGetValue((x, z), out var list))
                {
                    continue;
                }

                foreach (var (a, b) in list)
                {
                    if (Vector3.Distance(a, from) <= slack && Vector3.Distance(b, to) <= slack)
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private Dictionary<(int, int), List<(Vector3 From, Vector3 To)>> DropsByCell()
    {
        var map = new Dictionary<(int, int), List<(Vector3 From, Vector3 To)>>();
        foreach (var d in Drops())
        {
            var key = ((int)MathF.Floor(d.From.X / DropCell), (int)MathF.Floor(d.From.Z / DropCell));
            if (!map.TryGetValue(key, out var list))
            {
                map[key] = list = [];
            }

            list.Add(d);
        }

        return map;
    }
    /// <summary>The one-way links baked in (drops off ledges, climbs onto them).</summary>
    internal IEnumerable<(Vector3 From, Vector3 To)> Drops()
    {
        for (var i = 0; i < Detour.GetMaxTiles(); i++)
        {
            if (Detour.GetTile(i)?.data?.offMeshCons is not { } cons)
            {
                continue;
            }

            foreach (var c in cons)
            {
                if (c.pos[0] == c.pos[1])
                {
                    continue; // a spare (link slots for a neighbour's landing), not a way
                }

                yield return (NavRecast.Num(c.pos[0]), NavRecast.Num(c.pos[1]));
            }
        }
    }

    /// <summary>Every polygon's corners, for a debug view.</summary>
    public IEnumerable<Vector3[]> Outlines()
    {
        for (var i = 0; i < Detour.GetMaxTiles(); i++)
        {
            var data = Detour.GetTile(i)?.data;
            if (data?.header == null)
            {
                continue;
            }

            for (var p = 0; p < data.header.polyCount; p++)
            {
                var poly = data.polys[p];
                var corners = new Vector3[poly.vertCount];
                for (var k = 0; k < poly.vertCount; k++)
                {
                    var v = poly.verts[k] * 3;
                    corners[k] = new Vector3(data.verts[v], data.verts[v + 1], data.verts[v + 2]);
                }

                yield return corners;
            }
        }
    }

    /// <summary>All the world's triangles and the box they fill.</summary>
    private static List<Triangle> Triangles(CollisionWorld world, out Box box)
    {
        box = Box.Empty;
        foreach (var s in world.Sources)
        {
            box = box.Union(s.Bounds);
        }

        var tris = new List<Triangle>();
        if (!box.IsEmpty)
        {
            world.Gather(box, tris);
        }

        return tris;
    }

    private static ulong Hash(List<Triangle> tris)
    {
        var h = HashStart;
        foreach (var t in tris)
        {
            h = Mix(h, t.A);
            h = Mix(h, t.B);
            h = Mix(h, t.C);
        }

        return h;
    }

    private static ulong Mix(ulong h, Vector3 v)
    {
        h = (h ^ BitConverter.SingleToUInt32Bits(v.X)) * HashPrime;
        h = (h ^ BitConverter.SingleToUInt32Bits(v.Y)) * HashPrime;
        return (h ^ BitConverter.SingleToUInt32Bits(v.Z)) * HashPrime;
    }

    /// <summary>An area corner: a presence byte, then x and z.</summary>
    private static void WriteArea(BinaryWriter w, Vector2? corner)
    {
        w.Write(corner.HasValue);
        if (corner is not { } v)
        {
            return;
        }

        w.Write(v.X);
        w.Write(v.Y);
    }

    /// <summary>The spots: a count (-1: none), x and z each, then the reach.</summary>
    private static void WriteSpots(BinaryWriter w, NavBuild settings)
    {
        w.Write(settings.Spots?.Count ?? -1);
        foreach (var p in settings.Spots ?? [])
        {
            w.Write(p.X);
            w.Write(p.Y);
        }

        w.Write(settings.Reach);
    }

    private static NavBuild ReadSpots(BinaryReader r, NavBuild settings)
    {
        var n = r.ReadInt32();
        Vector2[]? spots = n < 0 ? null : new Vector2[n];
        for (var i = 0; i < n; i++)
        {
            spots![i] = new Vector2(r.ReadSingle(), r.ReadSingle());
        }

        return settings with { Spots = spots, Reach = r.ReadSingle() };
    }

    private static Vector2? ReadArea(BinaryReader r) => r.ReadBoolean() ? new Vector2(r.ReadSingle(), r.ReadSingle()) : null;

    private static void Put(float[] into, int corner, Vector3 v)
    {
        into[corner * 3] = v.X;
        into[corner * 3 + 1] = v.Y;
        into[corner * 3 + 2] = v.Z;
    }
}
