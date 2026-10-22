using SkyNet.Data;
using SkyNet.Formats;
using SkyNet.Game.Enemies;

namespace SkyNet.Edit.Tests;

/// <summary>The model and robot viewer (editor-tools plan, T2): robots assembled from their segments
/// as in game, a walker's AIS blocks by name, the playback, and the read-only property tables.
/// Skipped without the game data.</summary>
public sealed class ModelViewerTests
{
    private const int T800 = 37, TRexLegs = 40, BossChassis = 42;

    private static GameData Data => GameData.Open(EditDocumentTests.DataDir!)!;

    [MapDataFact]
    public void Multi_segment_robots_are_assembled()
    {
        var models = new EnemyModels(Data);

        // The boss: chassis, chest, head, turret, guns (7 parts); the T-rex: legs, head and its two guns.
        var boss = ModelSubject.ForRobot(GameTables.Ais, models, BossChassis)!;
        Assert.Equal(7, boss.Parts.Count);
        Assert.All(boss.Parts, p => Assert.NotNull(p.Model));
        Assert.Equal(4, ModelSubject.ForRobot(GameTables.Ais, models, TRexLegs)!.Parts.Count);

        // Children sit where their segment puts them, not at the root's origin.
        Assert.Contains(boss.Parts.Skip(1), p => p.World.Translation != System.Numerics.Vector3.Zero);
    }

    [MapDataFact]
    public void A_walker_lists_its_blocks_by_name_and_starts_walking()
    {
        var t800 = ModelSubject.ForRobot(GameTables.Ais, new EnemyModels(Data), T800)!;
        var names = t800.Anims.Select(a => a.Name).ToList();

        Assert.Equal(14, names.Count);
        Assert.Contains("walk", names);
        Assert.Equal(["fire 1", "fire 2", "fire 3"], names.Where(n => n.StartsWith("fire", StringComparison.Ordinal)));
        Assert.Equal("walk", t800.Anims[t800.AnimIndex].Name);
        var walk = t800.Anims[t800.AnimIndex].Block;
        Assert.Equal(5, walk.Frames[0].Frame);
        Assert.Equal(20, walk.Frames[^1].Frame);
    }

    [MapDataFact]
    public void Playback_steps_through_a_block_and_the_models_frames()
    {
        var t800 = ModelSubject.ForRobot(GameTables.Ais, new EnemyModels(Data), T800)!;
        var fire = t800.Anims.Select((a, i) => (a, i)).First(x => x.a.Name == "fire 1");
        t800.Select(fire.i);

        // Each step shows the block's frame; a step past the end wraps.
        t800.GoTo(3);
        Assert.Equal(fire.a.Block.Frames[3].Frame, t800.Frame);
        t800.GoTo(t800.Steps - 1);
        t800.StepBy(1);
        Assert.False(t800.Playing);
        Assert.Equal((0, fire.a.Block.Frames[0].Frame), (t800.Step, t800.Frame));

        // Played by the game's stepper at the block's fps: a second on is 16 steps on (the walk loops).
        t800.Select(t800.Anims.Select((a, i) => (a, i)).First(x => x.a.Name == "walk").i);
        t800.Playing = true;
        t800.Advance(2.5f / 16f);
        Assert.Equal(t800.Anims[t800.AnimIndex].Block.Frames[2].Frame, t800.Frame);

        // Every .3D frame, one by one.
        t800.Show(PlaybackSource.AllFrames);
        Assert.Equal(84, t800.Steps);
        t800.StepBy(-1);
        Assert.Equal(83, t800.Frame);
    }

    [MapDataFact]
    public void A_chosen_block_reaches_the_robots_brain()
    {
        // A mod's robot model plays the clip of the brain's block (RobotPose), not the frame shown.
        var t800 = ModelSubject.ForRobot(GameTables.Ais, new EnemyModels(Data), T800)!;
        var fire = t800.Anims.Select((a, i) => (a, i)).First(x => x.a.Name == "fire 1");
        var starts = t800.Robot.Brain!.AnimStarts;

        t800.Select(fire.i);

        Assert.Equal(fire.a.Block.Va, t800.Robot.Brain.AnimVa);
        Assert.True(t800.Robot.Brain.AnimStarts > starts);
    }

    [MapDataFact]
    public void A_mod_clip_clock_stops_on_pause_and_follows_steps()
    {
        // A mod's clip runs on the subject's clock: still while paused, on to the step chosen.
        var t800 = ModelSubject.ForRobot(GameTables.Ais, new EnemyModels(Data), T800)!;
        var fps = t800.Anims[t800.AnimIndex].Block.Fps;
        t800.Playing = false;
        var paused = t800.ClockMs;

        t800.Advance(1f);
        Assert.Equal(paused, t800.ClockMs);

        t800.Playing = true;
        t800.Advance(0.5f);
        Assert.Equal(paused + 500, t800.ClockMs);

        // A step restarts the clip, then moves its clock to the step: 3.5 frames at the block's fps.
        var before = t800.ClockMs;
        t800.GoTo(3);
        Assert.Equal(before + (long)(3.5f / fps * 1000f), t800.ClockMs);
    }

    [MapDataFact]
    public void A_plain_model_has_no_blocks_but_its_frames()
    {
        var model = ModelSubject.ForModel(new EnemyModels(Data), "T800PST.3D")!;
        Assert.Empty(model.Anims);
        Assert.Equal(PlaybackSource.AllFrames, model.Source);
        Assert.Single(model.Parts);
        Assert.Null(model.Type);
    }

    [Fact]
    public void A_robot_entry_shows_its_table_row_and_ai_params()
    {
        var ais = GameTables.Ais;
        var entry = Assert.Single(new RobotAssets(ais).Enumerate(RobotAssets.Robots), e => e.Id == RobotAssets.Id(T800));
        Assert.Equal("t800pst", entry.Name);
        Assert.Equal("walker", entry.Group);

        var fields = RobotTables.Fields(ais.Types[T800]);
        Assert.Equal(7 + 3, fields.Count(f => f.Section == RobotTables.Row));
        Assert.Contains(new RobotField(RobotTables.Ai, "hit points", "500"), fields);
        Assert.Contains(new RobotField(RobotTables.Ai, "state", "7 (walker)"), fields);
        Assert.Equal(AisData.ParamCount, fields.Count(f => f.Section == RobotTables.Block));
        // The chassis lists one child (the chest), which carries the rest.
        Assert.Single(RobotTables.Fields(ais.Types[BossChassis]), f => f.Section == RobotTables.Segments);
    }
}
