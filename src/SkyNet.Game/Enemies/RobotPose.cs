using System.Numerics;
using RetroEng.Models;
using SkyNet.Formats;
using SkyNet.Game.Menu;
using SkyNet.Game.Mods;

namespace SkyNet.Game.Enemies;

/// <summary>A segment as a frame caught it: its model, its placement under its parent now
/// (<see cref="EnemyPart.Local"/>: a turret's aim), its model frame, where it is in the world.</summary>
public readonly record struct SegmentState(EnemyModelInfo? Model, Matrix4x4 Local, int Frame, Matrix4x4 World);

/// <summary>One robot as a frame caught it (<see cref="EnemyRenderer.Capture"/>), pooled: drawn on
/// another thread while the next tick runs.</summary>
public sealed class RobotShot
{
    public Enemy Robot { get; private set; } = null!;
    /// <summary>The root's world transform (the pose's <see cref="AnimatedInstance.Placement"/>).</summary>
    public Matrix4x4 World { get; private set; }
    /// <summary>Indoors, the shade of the level's light where it stands; null: outdoors.</summary>
    public int? Shade { get; private set; }
    /// <summary>The root script's block playing and how many it has started (a mod clip restarts on a change).</summary>
    public uint AnimVa { get; private set; }
    public int AnimStarts { get; private set; }
    /// <summary>The segments, depth first, root first.</summary>
    public IReadOnlyList<SegmentState> Segments => _segments;

    private readonly List<SegmentState> _segments = [];

    /// <summary>Catches <paramref name="robot"/> as it stands now (its transforms updated first), the
    /// whole body moved <paramref name="alpha"/> of the way from the previous tick's place (the
    /// segments' turns and frames stay the latest tick's).</summary>
    public void Catch(Enemy robot, int? shade, float alpha = 1f)
    {
        robot.UpdateTransforms();
        var root = robot.Root.World;
        var drawn = robot.DrawnRoot(alpha);
        // The latest root to the drawn one, applied to every segment's world.
        var shift = drawn == root || !Matrix4x4.Invert(root, out var inverse) ? (Matrix4x4?)null : inverse * drawn;
        (Robot, World, Shade) = (robot, drawn, shade);
        (AnimVa, AnimStarts) = robot.Brain is { } brain ? (brain.AnimVa, brain.AnimStarts) : (0u, 0);
        _segments.Clear();
        foreach (var part in robot.Root.Walk())
        {
            _segments.Add(new SegmentState(part.Model, part.Local, part.Frame, shift is { } s ? part.World * s : part.World));
        }
    }
}

/// <summary>
/// One robot's pose of its type's <see cref="RobotRig"/>: an <see cref="AnimatedInstance"/> fed each
/// frame from a <see cref="RobotShot"/>, placed in the world (draw it with an identity world). DOS
/// rigs take each segment's frame (the AIS script stays the truth) and its placement matrix as
/// Enemy.UpdateTransforms makes it (so the joints are the static segments' world matrices, bit for
/// bit); mod rigs play the clip of the block playing and add the segments' turns.
/// </summary>
public sealed class RobotPose
{
    private const float MsPerSecond = 1000f;

    private readonly List<SegmentState> _segments = [];
    private uint _va;
    private int _starts = -1;
    private long _ms = -1;

    public RobotPose(RobotRig rig)
    {
        Rig = rig;
        Instance = new AnimatedInstance(rig.Model);
        if (rig.Source == RigSource.Dos)
        {
            (Instance.OverrideMatrix, Instance.Reshape) = (Place, Frame);
        }
        else
        {
            Instance.Override = Turn;
        }
    }

    public RobotRig Rig { get; }
    public AnimatedInstance Instance { get; }
    /// <summary>A mod rig's level of detail drawn (−1: none yet), kept for the hysteresis.</summary>
    internal int Lod { get; set; } = -1;
    /// <summary>Per segment, the level of detail of its replacement drawn (−1: none yet); null: none drawn.</summary>
    internal int[]? SegmentLods { get; set; }

    /// <summary>Poses the shot's robot; <paramref name="nowMs"/>: the animation clock (a mod clip's time).</summary>
    public void Apply(RobotShot shot, long nowMs)
    {
        _segments.Clear();
        _segments.AddRange(shot.Segments);
        Instance.Placement = shot.World;
        if (Rig.Source == RigSource.Dos)
        {
            Instance.Repose();
            return;
        }

        if (shot.AnimStarts != _starts || shot.AnimVa != _va)
        {
            (_starts, _va) = (shot.AnimStarts, shot.AnimVa);
            if (Rig.ClipOf(shot.AnimVa) is { } clip)
            {
                Instance.Play(clip.Clip, clip.Loop);
            }
            else
            {
                Instance.Rest();
            }
        }

        var seconds = _ms < 0 ? 0f : Math.Max(nowMs - _ms, 0) / MsPerSecond;
        _ms = nowMs;
        Instance.Advance(seconds);
    }

    /// <summary>A DOS segment's node at the placement its segment has now (the root's is the world placement).</summary>
    private void Place(int node, ref Matrix4x4 local)
    {
        var segment = Rig.SegmentOf(node);
        if (segment > 0 && segment < _segments.Count)
        {
            local = _segments[segment].Local;
        }
    }

    /// <summary>A mod joint: the clip's rotation with its segment's turn from rest put before it, so a
    /// joint left at the template's rest turns exactly as DOS's segment.</summary>
    private void Turn(int node, ref Trs local)
    {
        var segment = Rig.SegmentOf(node);
        if (segment <= 0 || segment >= _segments.Count)
        {
            return;
        }

        var now = Quaternion.CreateFromRotationMatrix(_segments[segment].Local);
        local.Rotation = Quaternion.Normalize(local.Rotation * Quaternion.Conjugate(Rig.Segments[segment].Rest) * now);
    }

    /// <summary>A DOS part shows its segment's frame (clamped to the model's).</summary>
    private void Frame(int part, ref ShapeBlend shapes)
    {
        var segment = Rig.PartSegments[part];
        if (segment >= _segments.Count)
        {
            return;
        }

        var count = Rig.Model.Parts[part].Shapes.Count;
        shapes = ShapeBlend.Exact(Math.Clamp(_segments[segment].Frame, 0, count - 1));
    }
}

/// <summary>
/// The robots' looks, headless: a rig per robot type (and per wreck model) and a pose per robot.
/// In the enhanced look a mod's <c>NAME.robot.glb</c> (NAME: the root's model or look) is chosen
/// over the DOS segments.
/// </summary>
public sealed class RobotLooks(TextureStore textures)
{
    private readonly Dictionary<(int Type, string File, int Segments), RobotRig> _dos = [];
    private readonly Dictionary<RobotReplacement, RobotRig> _mods = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, RobotRig> _singles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Enemy, Posed> _poses = new(ReferenceEqualityComparer.Instance);
    private int _generation;

    /// <summary>A robot's pose and what chose its rig (the look, a mod's file), and when last posed.</summary>
    private sealed class Posed(RobotPose pose, Look look, RobotReplacement? mod)
    {
        public RobotPose Pose { get; } = pose;
        public Look Look { get; } = look;
        public RobotReplacement? Mod { get; } = mod;
        public int Seen { get; set; }
    }

    /// <summary>The robots posed now.</summary>
    public int Poses => _poses.Count;

    /// <summary>The rig <paramref name="robot"/> draws with in <paramref name="look"/>.</summary>
    public RobotRig Rig(Enemy robot, Look look, AisData? ais)
    {
        var segments = robot.Root.Walk().ToArray();
        if (ModOf(robot, look) is { } replacement)
        {
            if (!_mods.TryGetValue(replacement, out var mod))
            {
                _mods[replacement] = mod = RobotRig.Mod(segments, replacement, ais);
            }

            return mod;
        }

        var key = (robot.TypeIndex, robot.Root.Model?.File ?? "", segments.Length);
        if (!_dos.TryGetValue(key, out var rig))
        {
            _dos[key] = rig = RobotRig.Dos(segments, textures);
        }

        return rig;
    }

    /// <summary>A lone model's rig (a wreck part in flight: frame 0).</summary>
    public RobotRig Single(EnemyModelInfo model)
    {
        if (!_singles.TryGetValue(model.File, out var rig))
        {
            _singles[model.File] = rig = RobotRig.Dos([new EnemyPart(-1, null, model, null, Vector3.Zero, 0f, 0f, 0f)], textures);
        }

        return rig;
    }

    /// <summary><paramref name="robot"/>'s pose (made on first sight, again when the look or the mods change its rig).</summary>
    public RobotPose Pose(Enemy robot, Look look, AisData? ais)
    {
        if (!_poses.TryGetValue(robot, out var posed) || posed.Look != look || (look == Look.Enhanced && ModOf(robot, look) != posed.Mod))
        {
            _poses[robot] = posed = new Posed(new RobotPose(Rig(robot, look, ais)), look, ModOf(robot, look));
        }

        posed.Seen = _generation;
        return posed.Pose;
    }

    /// <summary>The mod's whole-robot file drawn for <paramref name="robot"/> in <paramref name="look"/>; null: DOS's segments.</summary>
    private static RobotReplacement? ModOf(Enemy robot, Look look) =>
        look == Look.Enhanced && ModContent.HasModels && robot.Root.Model is { } m ? ModContent.Robot(Path.GetFileNameWithoutExtension(m.File)) : null;

    /// <summary>Forgets the poses of robots not posed since the last sweep (dead, gone with their map).</summary>
    public void Sweep()
    {
        foreach (var (robot, entry) in _poses)
        {
            if (entry.Seen != _generation)
            {
                _poses.Remove(robot);
            }
        }

        _generation++;
    }
}
