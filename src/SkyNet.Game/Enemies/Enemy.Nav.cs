using System.Numerics;
using SkyNet.Game.Navigation;

namespace SkyNet.Game.Enemies;

/// <summary>
/// ROBOT AI IMPROVED's paths: a walker with a nav mesh (<see cref="Robots.Nav"/>) heads for the
/// next corner of its path to the goal instead of the goal itself; the walk (probes, following,
/// backing off) stays as it is, so it still copes with what the mesh does not hold (doors, other
/// robots). No mesh, off it, no path or past a partial path's end: straight at the goal.
/// <code>
///   R ─► c1 ─► c2 ─► goal     repath: goal moved, every RepathEvery, off the leg, stuck
/// </code>
/// </summary>
public sealed partial class Enemy
{
    /// <summary>Ground robots this wide (half, flat units) or less use the small bodies.</summary>
    private const float SmallSpacing = 45f;
    /// <summary>A corner this near (flat, plus the body's radius) is passed.</summary>
    private const float CornerReached = 24f;
    /// <summary>A goal moved this far, or the robot this far off its leg, asks for a new path.</summary>
    private const float RepathMoved = 100f, OffPath = 120f;
    private const float RepathEvery = 1.5f;
    /// <summary>The path's start snapped further than this (body radii across, units up) from the
    /// feet: the robot is off the mesh.</summary>
    private const float OffMeshRadii = 3f, OffMeshUp = 64f;
    /// <summary>The drop probe's reach ahead: DOS's, and the least on a path.</summary>
    private const float DropLook = 120f, DropLookMin = 40f;

    private readonly List<Vector3> _corners = [];
    private int _corner;
    private Vector3? _pathTo;
    private float _repathT;
    private PathResult _path;
    private float _dropLook = DropLook;
    private NavStep _step;

    /// <summary>How far out the shoulder probes run: about the body's edge; on a nav path no wider
    /// than the mesh's body (it vouches for that much room: a catwalk narrower than the robot).</summary>
    private float Shoulder => _step == NavStep.Corner && NavBody is { } body ? MathF.Min(Spacing * 0.8f, body.Radius) : Spacing * 0.8f;

    /// <summary>The body its nav mesh is baked for (<see cref="NavAgents"/>); null for a robot that
    /// does not path-find: flyers, hovers (6), turrets, path vehicles.</summary>
    internal NavAgent? NavBody
    {
        get
        {
            if (IsFlying || IsStationary || OnPath || State is not (7 or 13))
            {
                return null;
            }

            return Terminator ? NavAgents.Terminator : Spacing <= SmallSpacing ? NavAgents.Walker : NavAgents.BigWalker;
        }
    }

    /// <summary>The corners still ahead (the debug view); empty when not on a path.</summary>
    internal IReadOnlyList<Vector3> NavPath => _corner < _corners.Count ? _corners.Skip(_corner).ToList() : [];

    /// <summary>What a walker does on its way to a goal.</summary>
    private enum NavStep
    {
        /// <summary>No mesh, off it or no path: straight at the goal.</summary>
        Straight,
        /// <summary>Toward the path's next corner.</summary>
        Corner,
        /// <summary>At the end of a partial path (the goal is cut off): stand there.</summary>
        Hold,
    }

    /// <summary>The way to <paramref name="goal"/>: <paramref name="corner"/> the path's next corner,
    /// <paramref name="left"/> the way still to go along it.</summary>
    private NavStep Steer(Vector3 goal, float dt, out Vector3 corner, out float left)
    {
        left = 0f;
        corner = goal;
        if (NavBody is not { } body || _ctx.Nav?.QueryFor(body) is not { } nav)
        {
            return NavStep.Straight;
        }

        var feet = Position + new Vector3(0f, FootOffset, 0f);
        _repathT -= dt;
        if (_pathTo is not { } old || Flat(old - goal).Length() > RepathMoved || _repathT <= 0f || OffLeg(feet))
        {
            Plan(nav, body, feet, goal);
        }

        var reached = CornerReached + body.Radius;
        while (_corner < _corners.Count && Flat(_corners[_corner] - feet).Length() < reached)
        {
            _corner++;
        }

        if (_corner >= _corners.Count)
        {
            return _path == PathResult.Partial && _corners.Count > 0 ? NavStep.Hold : NavStep.Straight;
        }

        var at = feet;
        for (var i = _corner; i < _corners.Count; i++)
        {
            left += Flat(_corners[i] - at).Length();
            at = _corners[i];
        }

        corner = _corners[_corner];
        return NavStep.Corner;
    }

    /// <summary>A new path from <paramref name="feet"/>; none when the start snaps too far (off the mesh).</summary>
    private void Plan(NavQuery nav, NavAgent body, Vector3 feet, Vector3 goal)
    {
        _pathTo = goal;
        _repathT = RepathEvery;
        _corner = 1;
        _path = nav.FindPath(feet, goal, _corners);
        if (_path == PathResult.None)
        {
            return;
        }

        var start = _corners[0];
        if (Flat(start - feet).Length() > body.Radius * OffMeshRadii || MathF.Abs(start.Y - feet.Y) > OffMeshUp)
        {
            _corners.Clear();
        }
    }

    /// <summary>Farther than <see cref="OffPath"/> from the leg being walked.</summary>
    private bool OffLeg(Vector3 feet)
    {
        if (_corner == 0 || _corner >= _corners.Count)
        {
            return false;
        }

        var a = _corners[_corner - 1];
        var b = _corners[_corner];
        var ab = new Vector2(b.X - a.X, b.Z - a.Z);
        var ap = new Vector2(feet.X - a.X, feet.Z - a.Z);
        var t = ab.LengthSquared() > 1f ? Math.Clamp(Vector2.Dot(ap, ab) / ab.LengthSquared(), 0f, 1f) : 0f;
        return (ap - ab * t).Length() > OffPath;
    }
}
