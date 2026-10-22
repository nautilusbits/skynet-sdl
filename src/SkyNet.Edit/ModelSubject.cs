using System.Numerics;
using RetroEng.Physics;
using SkyNet.Formats;
using SkyNet.Game.Enemies;

namespace SkyNet.Edit;

/// <summary>What the playback steps through: one of the robot's AIS animation blocks, or every frame of the .3D.</summary>
public enum PlaybackSource
{
    Block,
    AllFrames,
}

/// <summary>
/// The model viewer's subject (editor-tools plan, T2): a .3D or a robot, assembled as the game
/// assembles it (<see cref="Robots.Add"/> builds the <see cref="Enemy"/>, its child segments placed
/// as in game), at the origin, facing −Z. A plain model is a robot of a one-type table naming it.
/// Its root shows a frame picked by the playback: an AIS block, played by the game's stepper
/// (<see cref="AisBrain"/>), or the .3D's frames one by one at the game's strip rate.
/// <code>
///   type ─► Robots.Add ─► Enemy (root + segments) ─► Root.Frame ◄─ AisBrain.PlayAnim(block) / strip
/// </code></summary>
public sealed class ModelSubject : IPlayback
{
    /// <summary>Names of the AI states (EnemyType.State).</summary>
    public static readonly IReadOnlyDictionary<int, string> StateNames = new Dictionary<int, string>
    {
        [2] = "turret", [6] = "hover", [7] = "walker", [8] = "head", [9] = "flyer", [10] = "machine",
        [11] = "path vehicle", [12] = "dormant trap", [13] = "tank",
    };

    // A step lands inside its frame: the stepper's cursor is a float.
    private const float StepInside = 0.5f;
    private const double MsPerSecond = 1000.0;

    private readonly Robots _robots;
    // The block player: the game's stepper with no script (Pc 0), so only PlayAnim moves it.
    private readonly AisBrain _player;
    // Seconds into the .3D strip's current frame, and into the block played.
    private float _stripAt, _blockAt;
    // Seconds on the mod clip's clock: still while paused (RobotPose plays the clip by its deltas).
    private double _clock;

    private ModelSubject(string title, Robots robots, Enemy robot, AisData ais)
    {
        Title = title;
        _robots = robots;
        Robot = robot;
        _player = new AisBrain(new Dictionary<uint, AisOp>(), ais.Anims, 0);
        Anims = AnimsOf(ais, robot.Type);
        Source = Anims.Count > 0 ? PlaybackSource.Block : PlaybackSource.AllFrames;
        // The walk (or another loop) first: what the robot does most.
        var loop = Anims.Select((a, i) => (a, i)).FirstOrDefault(x => (x.a.Block.Flags & AisAnimBlock.Loop) != 0);
        Select(loop.a != null ? loop.i : 0);
    }

    public string Title { get; }

    /// <summary>The assembled robot (a plain model: its root alone).</summary>
    public Enemy Robot { get; }

    /// <summary>The robots context the renderer captures from (one robot).</summary>
    public Robots Robots => _robots;

    /// <summary>The robot's type row; null for a plain model.</summary>
    public EnemyType? Type { get; private init; }

    /// <summary>Every segment, root first.</summary>
    public IReadOnlyList<EnemyPart> Parts => [.. Robot.Root.Walk()];

    public IReadOnlyList<RobotAnim> Anims { get; }
    public PlaybackSource Source { get; private set; }
    /// <summary>The block chosen (an index into <see cref="Anims"/>).</summary>
    public int AnimIndex { get; private set; }
    public bool Playing { get; set; } = true;

    public void TogglePlay() => Playing = !Playing;

    /// <summary>The mod clip's clock (ms): runs only while playing, a step moves it to the step.</summary>
    public long ClockMs => (long)(_clock * MsPerSecond);

    /// <summary>The root's frame count in the .3D.</summary>
    public int ModelFrames => Robot.Root.Model?.Frames ?? 1;

    /// <summary>The frame shown, and where it is in its sequence (block entry or .3D frame).</summary>
    public int Frame => Robot.Root.Frame;
    public int Step { get; private set; }
    public int Steps => Source == PlaybackSource.Block ? Anims[AnimIndex].Block.Frames.Length : ModelFrames;

    /// <summary>A robot of enemy type <paramref name="type"/>; null when the type has no model.</summary>
    public static ModelSubject? ForRobot(AisData ais, EnemyModels models, int type)
    {
        if (type < 0 || type >= ais.Types.Length || models.Get(ais.Types[type].ModelFile) == null)
        {
            return null;
        }

        var robots = new Robots(ais, models, new CollisionWorld());
        var t = ais.Types[type];
        return new ModelSubject($"{t.Name} (type {type})", robots, robots.Add(type, Vector3.Zero), ais) { Type = t };
    }

    /// <summary>A .3D (<paramref name="file"/>, "BOX.3D") alone; null when missing.</summary>
    public static ModelSubject? ForModel(EnemyModels models, string file)
    {
        if (models.Get(file) == null)
        {
            return null;
        }

        var ais = new AisData(AisData.V101, [new EnemyType { Index = 0, Name = Path.GetFileNameWithoutExtension(file), ModelFile = file }], [], [], []);
        var robots = new Robots(ais, models, new CollisionWorld());
        return new ModelSubject(file, robots, robots.Add(0, Vector3.Zero), ais);
    }

    /// <summary>The blocks <paramref name="type"/>'s script can start (<see cref="RobotAnims.Of"/>).</summary>
    public static IReadOnlyList<RobotAnim> AnimsOf(AisData ais, EnemyType? type) => RobotAnims.Of(ais, type);

    /// <summary>Plays block <paramref name="index"/> from its start.</summary>
    public void Select(int index)
    {
        if (Anims.Count == 0)
        {
            Show(PlaybackSource.AllFrames);
            return;
        }

        (Source, AnimIndex) = (PlaybackSource.Block, Math.Clamp(index, 0, Anims.Count - 1));
        GoTo(0);
    }

    /// <summary>Steps through the .3D's frames (or back to the block chosen).</summary>
    public void Show(PlaybackSource source)
    {
        if (source == PlaybackSource.Block && Anims.Count > 0)
        {
            Select(AnimIndex);
            return;
        }

        Source = PlaybackSource.AllFrames;
        GoTo(0);
    }

    /// <summary>Moves the playback on by <paramref name="seconds"/> while playing.</summary>
    public void Advance(float seconds)
    {
        if (!Playing || seconds <= 0f)
        {
            return;
        }

        _clock += seconds;
        if (Source == PlaybackSource.Block)
        {
            // The stepper shows the frame; the step shown beside it follows the same cursor (fps × time).
            var block = Anims[AnimIndex].Block;
            _player.Update(seconds, AisSenses.None);
            _blockAt += seconds;
            var cursor = (int)(_blockAt * block.Fps);
            Step = (block.Flags & AisAnimBlock.OneShot) != 0 ? Math.Min(cursor, Steps - 1) : cursor % Steps;
            Show(_player.Frame);
            return;
        }

        _stripAt += seconds;
        var steps = (int)(_stripAt * Enemy.StripFps);
        _stripAt -= steps / Enemy.StripFps;
        GoTo((Step + steps) % ModelFrames);
    }

    /// <summary>Pauses and moves one step (±) through the sequence, wrapping.</summary>
    public void StepBy(int delta)
    {
        Playing = false;
        GoTo(((Step + delta) % Steps + Steps) % Steps);
    }

    /// <summary>Shows step <paramref name="step"/> of the sequence.</summary>
    public void GoTo(int step)
    {
        Step = Math.Clamp(step, 0, Math.Max(0, Steps - 1));
        if (Source == PlaybackSource.AllFrames)
        {
            Show(Step);
            return;
        }

        // The stepper from the block's start, on to the step (the block's own fps: one step = 1/fps s).
        var block = Anims[AnimIndex].Block;
        _player.PlayAnim(0);
        _player.PlayAnim(block.Va);

        // A mod's model plays the clip of the robot's own brain's block (RobotPose).
        Robot.Brain?.PlayAnim(0);
        Robot.Brain?.PlayAnim(block.Va);
        _blockAt = Step > 0 && block.Fps > 0 ? (Step + StepInside) / block.Fps : 0f;
        _clock += _blockAt;
        if (Step > 0 && block.Fps > 0)
        {
            _player.Update(_blockAt, AisSenses.None);
        }

        Show(Step < block.Frames.Length ? block.Frames[Step].Frame : _player.Frame);
    }

    private void Show(int frame) => Robot.Root.Frame = Math.Clamp(frame, 0, ModelFrames - 1);

    /// <summary>The assembled robot's box (frame 0 of each segment, placed), for the camera.</summary>
    public Box Bounds()
    {
        Robot.UpdateTransforms();
        var box = Box.Empty;
        foreach (var p in Robot.Root.Walk())
        {
            if (p.Model is not { } m)
            {
                continue;
            }

            for (var i = 0; i < 8; i++)
            {
                var corner = new Vector3((i & 1) != 0 ? m.Bounds.Max.X : m.Bounds.Min.X, (i & 2) != 0 ? m.Bounds.Max.Y : m.Bounds.Min.Y,
                    (i & 4) != 0 ? m.Bounds.Max.Z : m.Bounds.Min.Z);
                box = box.Include(Vector3.Transform(corner, p.World));
            }
        }

        return box.IsEmpty ? new Box(new Vector3(-30f), new Vector3(30f)) : box;
    }
}
