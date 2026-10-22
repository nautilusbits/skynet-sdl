using System.Numerics;
using RetroEng.Loop;
using RetroEng.Physics;
using SkyNet.Formats;
using SkyNet.Game.Behaviour;
using SkyNet.Game.Campaign;
using SkyNet.Game.Collision;
using SkyNet.Game.Enemies;
using SkyNet.Game.Tests.Behaviour;
using SkyNet.Game.Water;
using SkyNet.Game.Weapons;

namespace SkyNet.Game.Tests;

/// <summary>Render interpolation: what moves is drawn between its last two ticks at the frame's
/// alpha; a jump (spawn, teleport, load) is drawn where it ends; alpha 1 is the latest tick.</summary>
public class InterpolationTests
{
    private const float Dt = 1f / 60f;

    [Fact]
    public void A_position_is_lerped_and_alpha_one_is_the_latest()
    {
        var a = new Vector3(0f, 0f, 0f);
        var b = new Vector3(10f, -20f, 4f);
        Assert.Equal(a, Interpolation.Lerp(a, b, 0f));
        Assert.Equal(new Vector3(5f, -10f, 2f), Interpolation.Lerp(a, b, 0.5f));
        Assert.Equal(b, Interpolation.Lerp(a, b, 1f));
        Assert.Equal(b, Interpolation.Lerp(a, b, 1.5f));
    }

    [Fact]
    public void A_jump_past_the_snap_distance_is_drawn_where_it_ends()
    {
        var a = Vector3.Zero;
        var far = new Vector3(Interpolation.SnapDistance + 1f, 0f, 0f);
        Assert.Equal(far, Interpolation.Lerp(a, far, 0.25f));
        var near = new Vector3(Interpolation.SnapDistance - 1f, 0f, 0f);
        Assert.NotEqual(near, Interpolation.Lerp(a, near, 0.25f));
    }

    [Fact]
    public void An_angle_turns_the_short_way_round()
    {
        // From just under +π to just over −π: through π, not back through 0.
        var mid = Interpolation.LerpAngle(3.0f, -3.0f, 0.5f);
        Assert.Equal(MathF.PI, MathF.Abs(MathF.IEEERemainder(mid, MathF.Tau)), 0.01f);
        Assert.Equal(-3.0f, Interpolation.LerpAngle(3.0f, -3.0f, 1f));
        Assert.Equal(1.5f, Interpolation.LerpAngle(1f, 2f, 0.5f), 1e-5f);
        // DOS angle units: 2040 → 8 passes 2048 (0).
        Assert.Equal(0f, Interpolation.LerpAngle(2040f, 8f, 0.5f, 2048f) % 2048f, 1e-3f);
    }

    // ---------------------------------------------------------------- shots and pieces

    private sealed class Gun : IDamageable
    {
        public Vector3 Centre => new(0f, 1e6f, 0f);
        public float Radius => 0f;
        public bool Alive => true;
        public void TakeDamage(in Damage damage) { }
    }

    private static WeaponSystem NewWeapons() => new(WeaponTable.Shipped, new CollisionWorld());

    private static void Tick(WeaponSystem w)
    {
        w.KeepPoses();
        w.Tick(new WeaponInput(false, false, false), new Shooter(new Vector3(0f, 1e6f, 0f), 0f, 0f), Dt);
    }

    [Fact]
    public void A_shot_is_drawn_where_it_is_until_a_tick_keeps_it_then_between_ticks()
    {
        var w = NewWeapons();
        var ammo = WeaponTable.Shipped.Ammo.First(a => a.Family == AmmoFamily.Bolt && a.Life > 0.5f);
        w.EnemyFire(new Gun(), ammo, Vector3.Zero, -Vector3.UnitZ, 600f);
        var shot = Assert.Single(w.Projectiles);
        Assert.Null(shot.Previous);
        Assert.Equal(shot.Position, shot.DrawnAt(0f));

        Tick(w);
        var before = shot.Previous!.Value;
        Assert.NotEqual(before, shot.Position);
        Assert.Equal(before, shot.DrawnAt(0f));
        Assert.Equal(Vector3.Lerp(before, shot.Position, 0.5f), shot.DrawnAt(0.5f));
        Assert.Equal(shot.Position, shot.DrawnAt(1f));
        // The rocket eye's alpha 1 is the property's.
        Assert.Equal(w.RocketEye, w.RocketEyeAt(1f));
    }

    [Fact]
    public void A_piece_starts_where_it_was_thrown_and_keeps_its_previous_position()
    {
        var pieces = new ImpactPieces();
        var at = new Vector3(5f, 6f, 7f);
        Assert.True(pieces.Spawn(3, ImpactPieces.Types[0].Type, at, -Vector3.UnitZ, Vector3.UnitZ) > 0);
        Assert.Equal(at, pieces.Slots[0].Previous);
        pieces.KeepPoses();
        pieces.Step(Dt, null);
        Assert.Equal(at, pieces.Slots[0].Previous);
        Assert.NotEqual(at, pieces.Slots[0].Position);
        pieces.KeepPoses();
        Assert.Equal(pieces.Slots[0].Position, pieces.Slots[0].Previous);
    }

    // ---------------------------------------------------------------- the torpedo ride and the water

    [Fact]
    public void The_torpedo_view_starts_at_the_eye_and_glides_between_ticks()
    {
        var ride = new TorpedoRide();
        var eye = new Vector3(0f, 50f, 0f);
        ride.KeepPose();
        ride.Step(Dt, Vector3.Zero, eye, 0f, out _);
        Assert.Equal(eye, ride.ViewAt(0f));                 // armed: no glide from the old view
        ride.KeepPose();
        ride.Step(Dt, Vector3.Zero, eye, 0f, out _);
        Assert.Equal(eye, ride.ViewAt(0f));
        Assert.Equal(Vector3.Lerp(eye, ride.View, 0.5f), ride.ViewAt(0.5f));
        Assert.Equal(ride.View, ride.ViewAt(1f));
        Assert.Equal(ride.Roll, ride.RollAt(1f));
    }

    [Fact]
    public void The_water_surface_glides_between_ticks_and_a_load_does_not_sweep()
    {
        var water = new WaterLevel(WaterLevel.MarkerClear, 100f);
        water.Move(200f, absolute: true);
        water.KeepPose();
        water.Tick(Dt * 10f);
        Assert.Equal(100f, water.SurfaceAt(0f));
        Assert.Equal((100f + water.Surface) / 2f, water.SurfaceAt(0.5f), 1e-4f);
        water.Set(150f, 150f);
        Assert.Equal(150f, water.SurfaceAt(0f));
    }

    // ---------------------------------------------------------------- the level's movers

    [BehaviourDataFact]
    public void A_mover_is_drawn_between_its_last_two_progresses_and_a_load_does_not_sweep()
    {
        const int gate = 0x078f7;
        var h = new Harness(210);
        var trigger = h.PositionOf(0x077f3);
        h.B.Tick(Harness.Dt, new PlayerProbe(trigger, trigger));
        for (var i = 0; i < 10 && !h.B.MoverRunning(gate); i++)
        {
            h.B.Tick(Harness.Dt, new PlayerProbe(Harness.Away, Harness.Away));
        }

        Assert.True(h.B.MoverRunning(gate));
        h.B.KeepPoses();
        var before = h.B.MoverProgress(gate).Progress;
        h.B.Tick(Harness.Dt, new PlayerProbe(Harness.Away, Harness.Away));
        var now = h.B.MoverProgress(gate).Progress;
        Assert.NotEqual(before, now);

        // The gate slides along −X as it opens (ScenarioTests: −128 open).
        Assert.True(h.B.TryGetPose(gate, 0f, out var from));
        Assert.True(h.B.TryGetPose(gate, 0.5f, out var mid));
        Assert.True(h.B.TryGetPose(gate, 1f, out var to));
        Assert.True(h.B.TryGetPose(gate, out var latest));
        Assert.Equal(-before, from.Delta.Translation.X, 0.01f);
        Assert.Equal(-(before + now) / 2f, mid.Delta.Translation.X, 0.01f);
        Assert.Equal(latest.Delta, to.Delta);

        // A load puts the progress back with no glide from where it was.
        h.B.Restore(h.B.Snapshot());
        Assert.True(h.B.TryGetPose(gate, 0f, out var loaded));
        Assert.Equal(latest.Delta, loaded.Delta);
    }

    // ---------------------------------------------------------------- the robots and their wrecks

    private static Robots Map210()
    {
        var data = BehaviourData.Data;
        var map = data.LoadMap(210)!;
        var world = LevelCollision.Build(data, map, data.LoadWld(210)).World;
        var robots = new Robots(SkyNet.Data.GameTables.Ais, new EnemyModels(data), world, seed: 3);
        robots.Populate(map);
        return robots;
    }

    [BehaviourDataFact]
    public void A_robot_is_drawn_between_its_last_two_ticks_whole()
    {
        var robots = Map210();
        var robot = robots.All.First(r => r.Alive && r.Root.Children.Count > 0);
        Assert.Equal(robot.Root.World, robot.DrawnRoot(0f));   // built: nothing to glide from

        robots.KeepPoses();
        var from = robot.Position;
        robot.Position += new Vector3(12f, 0f, -6f);
        robot.Yaw += 0.2f;
        robot.UpdateTransforms();
        Assert.Equal(Vector3.Lerp(from, robot.Position, 0.25f), robot.DrawnRoot(0.25f).Translation);
        Assert.Equal(robot.Root.World, robot.DrawnRoot(1f));

        // The shot moves every segment with the root: each keeps its place on the body.
        var shot = new RobotShot();
        shot.Catch(robot, null, 0.5f);
        Assert.Equal(robot.DrawnRoot(0.5f), shot.World);
        var segments = robot.Root.Walk().ToList();
        Matrix4x4.Invert(robot.Root.World, out var latestInverse);
        Matrix4x4.Invert(shot.World, out var drawnInverse);
        for (var i = 0; i < segments.Count; i++)
        {
            var onBody = segments[i].World * latestInverse;
            var drawnOnBody = shot.Segments[i].World * drawnInverse;
            Assert.Equal(onBody.Translation.X, drawnOnBody.Translation.X, 0.01f);
            Assert.Equal(onBody.Translation.Y, drawnOnBody.Translation.Y, 0.01f);
            Assert.Equal(onBody.Translation.Z, drawnOnBody.Translation.Z, 0.01f);
        }

        // Alpha 1 is the latest tick, bit for bit.
        shot.Catch(robot, null, 1f);
        Assert.Equal(robot.Root.World, shot.World);
        Assert.Equal(segments[^1].World, shot.Segments[^1].World);
    }

    [BehaviourDataFact]
    public void A_robot_that_teleports_or_is_loaded_is_drawn_where_it_is()
    {
        var robots = Map210();
        var robot = robots.All.First(r => r.Alive);
        robots.KeepPoses();
        robot.Position += new Vector3(Interpolation.SnapDistance * 2f, 0f, 0f);
        robot.UpdateTransforms();
        Assert.Equal(robot.Root.World, robot.DrawnRoot(0.5f));

        robots.KeepPoses();
        var snapshot = robots.Snapshot();
        robot.Position += new Vector3(20f, 0f, 0f);
        Assert.True(robots.Restore(snapshot));
        Assert.Equal(robot.Position, robot.PreviousPosition);
        Assert.Equal(robot.Root.World, robot.DrawnRoot(0f));
    }

    [BehaviourDataFact]
    public void A_wreck_part_flies_between_its_last_two_ticks()
    {
        var robots = Map210();
        var wreckage = robots.Wreckage;
        var at = robots.All[0].Position + new Vector3(0f, 2000f, 0f);
        Assert.True(wreckage.Add(90, null, at, at - new Vector3(10f, 0f, 0f), new SimRandom(5)));
        var part = wreckage.Parts.ToArray().First(p => p.Live);
        Assert.Equal(part.World, part.WorldAt(0f));       // thrown: nothing to glide from

        wreckage.KeepPoses();
        wreckage.Tick(Dt, new CollisionWorld(), (_, _) => { });
        part = wreckage.Parts.ToArray().First(p => p.Live);
        Assert.Equal(part.PreviousPosition, part.WorldAt(0f).Translation);
        Assert.Equal(Vector3.Lerp(part.PreviousPosition, part.Position, 0.5f), part.WorldAt(0.5f).Translation);
        Assert.Equal(part.World, part.WorldAt(1f));
    }
}
