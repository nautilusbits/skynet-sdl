using System.Numerics;
using RetroEng.Physics;
using SkyNet.Data;
using SkyNet.Formats;
using SkyNet.Game;
using SkyNet.Game.Enemies;
using SkyNet.Game.Menu;
using SkyNet.Game.Mods;

namespace SkyNet.Edit.Tests;

/// <summary>A robot template the editor exports (NAME.robot.glb), dropped into a mod unchanged, drawn
/// as the game's robot: its joints where the DOS segments are, a turret's aim reaching its joint, the
/// clip of the AIS block playing. Skipped without the game data.</summary>
[Collection(EditModsCollection.Name)]
public sealed class RobotTemplateTests : IDisposable
{
    private const float Tolerance = 0.05f;
    private const string ModName = "template";

    private readonly string _mods = Directory.CreateTempSubdirectory("skynet-edit-robot-").FullName;

    public void Dispose()
    {
        ModContent.Unmount();
        Directory.Delete(_mods, recursive: true);
    }

    private static bool Near(Matrix4x4 a, Matrix4x4 b) =>
        Enumerable.Range(0, 16).All(i => MathF.Abs(a[i / 4, i % 4] - b[i / 4, i % 4]) < Tolerance);

    /// <summary>The robot built on <paramref name="name"/>, its template in a mounted mod, posed in the enhanced look.</summary>
    private (Robots Robots, RobotLooks Looks, Enemy Robot) Mounted(string name)
    {
        var data = GameData.Open(EditDocumentTests.DataDir!)!;
        var export = new GltfExport(data, GameTables.Ais);
        var models = Path.Combine(_mods, ModName, "models");
        Directory.CreateDirectory(models);
        File.WriteAllBytes(Path.Combine(models, GltfExport.FileName(name, RobotSegments.With)), export.Export(name, RobotSegments.With)!);
        ModContent.Mount(_mods, [ModName]);
        var robots = new Robots(GameTables.Ais, new EnemyModels(data), new CollisionWorld());
        var robot = robots.Add(export.Robot(name)!.Index, new Vector3(3000f, 200f, -7000f), 0.4f);
        return (robots, new RobotLooks(new TextureStore(null, data)), robot);
    }

    [MapDataFact]
    public void A_turrets_aim_reaches_the_templates_joint()
    {
        var (robots, looks, robot) = Mounted("HVYTRRT");
        var turret = robot.Turrets[0];
        turret.Yaw += 0.8f;
        turret.Pitch -= 0.3f;
        var pose = looks.Pose(robot, Look.Enhanced, robots.Ais);
        var shot = new RobotShot();
        shot.Catch(robot, null);

        pose.Apply(shot, 0);

        Assert.Equal(RigSource.Mod, pose.Rig.Source);
        var segments = robot.Root.Walk().ToArray();
        var model = pose.Rig.Model;
        for (var i = 0; i < segments.Length; i++)
        {
            var node = Enumerable.Range(0, model.Nodes.Count).Single(n => model.Nodes[n].Name.StartsWith(RobotRig.JointPrefix(i), StringComparison.Ordinal));
            Assert.True(Near(segments[i].World, pose.Instance.Nodes[node]), $"segment {i}: {segments[i].World} vs {pose.Instance.Nodes[node]}");
        }
    }

    [MapDataFact]
    public void The_block_playing_picks_the_clip_of_its_name()
    {
        var (robots, looks, robot) = Mounted("T-REXLEG");
        var pose = looks.Pose(robot, Look.Enhanced, robots.Ais);
        var shot = new RobotShot();
        shot.Catch(robot, null);

        pose.Apply(shot, 0);

        var block = RobotAnims.Of(robots.Ais, robot.Type).Single(a => a.Block.Va == robot.Brain!.AnimVa);
        Assert.Equal(block.Name, pose.Instance.Clip?.Name);
    }

    [MapDataFact]
    public void The_templates_shape_keys_play_the_blocks_frames()
    {
        var (robots, looks, robot) = Mounted("T-REXLEG");
        var pose = looks.Pose(robot, Look.Enhanced, robots.Ais);
        var model = pose.Rig.Model;
        var root = Enumerable.Range(0, model.Parts.Count).Where(p => model.Parts[p].Shapes.Count > 1).ToArray();
        var frames = model.Parts[root[0]].Shapes.Count;
        var played = 0;

        // Each block's clip: at its k-th step every root part shows the block's k-th DOS frame.
        foreach (var anim in RobotAnims.Of(robots.Ais, robot.Type))
        {
            var clip = model.FindClip(anim.Name)!;
            var fps = anim.Block.Fps > 0 ? anim.Block.Fps : 10f;
            for (var k = 0; k < anim.Block.Frames.Length; k++)
            {
                var shown = Math.Min((int)anim.Block.Frames[k].Frame, frames - 1);
                pose.Instance.Play(clip, RetroEng.Models.ClipLoop.Once);
                pose.Instance.Advance((k + 0.5f) / fps);
                Assert.All(root, p => Assert.Equal(RetroEng.Models.ShapeBlend.Exact(shown), pose.Instance.Shapes[p]));
                played += shown > 0 ? 1 : 0;
            }
        }

        Assert.True(played > 0, "no block shows a frame past 0");
    }
}
