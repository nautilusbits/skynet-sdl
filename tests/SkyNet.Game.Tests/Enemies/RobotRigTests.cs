using System.Numerics;
using RetroEng.Models;
using RetroEng.Physics;
using SkyNet.Formats;
using SkyNet.Game.Enemies;
using SkyNet.Game.Menu;
using SkyNet.Game.Mods;
using SkyNet.Game.Tests.Behaviour;

namespace SkyNet.Game.Tests.Enemies;

/// <summary>The robots' animated models on real data: a rig per type, a pose per robot, the DOS
/// segments' frames and placements reaching the joints bit for bit, a mod's whole-robot glTF
/// chosen over the DOS segments in the enhanced look.</summary>
[Collection(ModsCollection.Name)]
public class RobotRigTests
{
    /// <summary>HVYTRRT: a turret on a base, three segments.</summary>
    private const int TurretType = 25;
    /// <summary>T-REXLEG: a walker with frames, four segments.</summary>
    private const int TrexType = 40;

    private static readonly GltfPrimitive Triangle = new([new(-0.5f, 0, -0.5f), new(0.5f, 0, -0.5f), new(0.5f, 2, 0.5f)], null, null, [0, 1, 2], -1);

    private static (Robots Robots, RobotLooks Looks) Setup()
    {
        var data = BehaviourData.Data;
        var robots = new Robots(SkyNet.Data.GameTables.Ais, new EnemyModels(data), new CollisionWorld());
        return (robots, new RobotLooks(new TextureStore(null, data)));
    }

    private static RobotShot Shot(Enemy robot)
    {
        var shot = new RobotShot();
        shot.Catch(robot, null);
        return shot;
    }

    [BehaviourDataFact]
    public void Each_robot_has_its_own_pose_of_its_types_rig()
    {
        var (robots, looks) = Setup();
        var a = robots.Add(TurretType, new Vector3(100f, 0f, 0f));
        var b = robots.Add(TurretType, new Vector3(900f, 0f, 0f), 1f);

        var poseA = looks.Pose(a, Look.Original, robots.Ais);
        var poseB = looks.Pose(b, Look.Original, robots.Ais);

        Assert.NotSame(poseA, poseB);
        Assert.NotSame(poseA.Instance, poseB.Instance);
        Assert.Same(poseA.Rig, poseB.Rig);
        Assert.Same(poseA, looks.Pose(a, Look.Original, robots.Ais));

        // A robot not posed since the last sweep (dead, gone) loses its pose.
        looks.Sweep();
        looks.Pose(a, Look.Original, robots.Ais);
        looks.Sweep();
        Assert.Equal(1, looks.Poses);
    }

    [BehaviourDataFact]
    public void A_turrets_aim_reaches_its_joint_bit_for_bit()
    {
        var (robots, looks) = Setup();
        var robot = robots.Add(TurretType, new Vector3(4000f, 120f, -9000f), 0.7f);
        var turret = robot.Turrets[0];
        turret.Yaw += 1.1f;
        turret.Pitch -= 0.2f;
        var pose = looks.Pose(robot, Look.Original, robots.Ais);

        pose.Apply(Shot(robot), 0);

        // Every part's joint is its segment's world matrix as the static drawing placed it.
        var segments = robot.Root.Walk().ToArray();
        var turretParts = 0;
        for (var p = 0; p < pose.Rig.Model.Parts.Count; p++)
        {
            var segment = segments[pose.Rig.PartSegments[p]];
            Assert.Equal(segment.World, pose.Instance.Joints[pose.Rig.Model.JointsOf(p).First]);
            turretParts += segment == turret ? 1 : 0;
        }

        Assert.True(turretParts > 0);
    }

    [BehaviourDataFact]
    public void The_script_picks_each_segments_frame()
    {
        var (robots, looks) = Setup();
        var robot = robots.Add(TrexType, Vector3.Zero);
        robot.Root.Frame = 5;
        var pose = looks.Pose(robot, Look.Original, robots.Ais);

        pose.Apply(Shot(robot), 0);

        var rootParts = Enumerable.Range(0, pose.Rig.Model.Parts.Count).Where(p => pose.Rig.PartSegments[p] == 0).ToArray();
        Assert.NotEmpty(rootParts);
        Assert.All(rootParts, p => Assert.Equal(ShapeBlend.Exact(5), pose.Instance.Shapes[p]));
        Assert.Equal(robot.Root.Model!.Frames, pose.Rig.Model.Parts[rootParts[0]].Shapes.Count);
    }

    [BehaviourDataFact]
    public void A_mods_robot_glb_is_drawn_over_the_DOS_segments_in_the_enhanced_look()
    {
        var root = Directory.CreateTempSubdirectory("skynet-robot-").FullName;
        try
        {
            var models = Path.Combine(root, "m", "models");
            Directory.CreateDirectory(models);
            File.WriteAllBytes(Path.Combine(models, "HVYTRRT" + RobotReplacement.Suffix + ".glb"), Gltf.WriteGlb([Triangle], [], []));
            ModContent.Mount(root, ["m"]);
            var (robots, looks) = Setup();
            var robot = robots.Add(TurretType, Vector3.Zero);

            var original = looks.Pose(robot, Look.Original, robots.Ais);
            var enhanced = looks.Pose(robot, Look.Enhanced, robots.Ais);

            Assert.Equal(RigSource.Dos, original.Rig.Source);
            Assert.Equal(RigSource.Mod, enhanced.Rig.Source);
            Assert.Same(ModContent.Robot("HVYTRRT"), enhanced.Rig.Replacement);
            Assert.Equal(RigSource.Dos, looks.Pose(robots.Add(TrexType, Vector3.Zero), Look.Enhanced, robots.Ais).Rig.Source);
        }
        finally
        {
            ModContent.Unmount();
            Directory.Delete(root, recursive: true);
        }
    }
}
