using System.Numerics;
using SkyNet.Game.Enemies;
using SkyNet.Game.Net;
using SkyNet.Game.Tests.Behaviour;
using SkyNet.Game.Weapons;
using Xunit.Abstractions;

namespace SkyNet.Game.Tests.Net;

/// <summary>Another player's body: the glide to the poses, the clips, the death and the hits.</summary>
public class DmAvatarTests(ITestOutputHelper output)
{
    [Fact]
    public void First_pose_places_the_body_then_it_glides_and_walks()
    {
        var a = new DmAvatar(2, "B", DmClass.Human);
        Assert.False(a.Visible);
        a.SpawnAt(new Vector3(0, 0, 0), 0f);
        a.ApplyPose(new Vector3(100, 0, 0), 1f, 0f, DmProtocol.FlagMoving);
        a.Step(1f / 60f);
        Assert.InRange(a.Position.X, 1f, 99f);
        Assert.Equal(DmAvatar.Walk, a.Clip);
        for (var i = 0; i < 120; i++)
        {
            a.ApplyPose(new Vector3(100, 0, 0), 1f, 0f, 0);
            a.Step(1f / 60f);
        }

        Assert.Equal(100f, a.Position.X, 0.5f);
        Assert.Equal(1f, a.Yaw, 0.01f);
        Assert.Equal(DmAvatar.Idle, a.Clip);
        Assert.InRange(a.Frame, DmAvatar.Idle.First, DmAvatar.Idle.Last);
    }

    [Fact]
    public void A_far_pose_snaps()
    {
        var a = new DmAvatar(2, "B", DmClass.Human);
        a.SpawnAt(Vector3.Zero, 0f);
        a.ApplyPose(new Vector3(20000, 0, 0), 0f, 0f, 0);
        a.Step(1f / 60f);
        Assert.Equal(new Vector3(20000, 0, 0), a.Position);
    }

    [Fact]
    public void A_shot_plays_the_firing_pose_once()
    {
        var a = new DmAvatar(2, "B", DmClass.Human);
        a.SpawnAt(Vector3.Zero, 0f);
        a.Fired();
        Assert.Equal(DmAvatar.Attack, a.Clip);
        for (var i = 0; i < 60; i++)
        {
            a.Step(1f / 60f);
        }

        Assert.Equal(DmAvatar.Idle, a.Clip);
    }

    [Fact]
    public void Death_holds_the_last_frame_then_the_body_goes()
    {
        var a = new DmAvatar(2, "B", DmClass.Human);
        a.SpawnAt(Vector3.Zero, 0f);
        Assert.True(a.Die());
        Assert.False(a.Die());
        Assert.Equal(0f, a.Radius);
        for (var i = 0; i < 60; i++)
        {
            a.Step(1f / 60f);
        }

        Assert.Equal(DmAvatar.Death.Last, a.Frame);
        Assert.True(a.Visible);
        for (var i = 0; i < 120; i++)
        {
            a.Step(1f / 60f);
        }

        Assert.False(a.Visible);
        a.SpawnAt(new Vector3(5, 0, 5), 0f);
        Assert.True(a.Visible);
        Assert.True(a.Alive);
    }

    [Fact]
    public void Hits_are_reported_while_alive_only()
    {
        var a = new DmAvatar(3, "C", DmClass.Terminator);
        var taken = new List<float>();
        a.Hit = (_, p) => taken.Add(p);
        a.SpawnAt(Vector3.Zero, 0f);
        a.TakeDamage(new Damage(50f, DamageKind.Direct, Vector3.Zero, true));
        a.Die();
        a.TakeDamage(new Damage(50f, DamageKind.Direct, Vector3.Zero, true));
        Assert.Equal([50f], taken);
        Assert.Equal(DmAvatarTable.Get(DmAvatarTable.MachineSoldier), a.Look);
        a.Avatar = 13;
        Assert.Equal(("AVFEMALE.3D", "AVFEMHED.3D", (0x125, 0x13e)), (a.Look.Body, a.Look.Head, a.Look.SkinRemap!.Value));
        a.Avatar = 2;  // not a machine: the class's default
        Assert.Equal(DmAvatarTable.MachineSoldier, a.Look.Number);
    }

    [Fact]
    public void A_ray_strikes_the_body_as_a_sphere()
    {
        var targets = new DamageTargets();
        var a = new DmAvatar(2, "B", DmClass.Human);
        a.SpawnAt(new Vector3(0, 0, -500), 0f);
        targets.Add(a);
        Assert.Same(a, targets.RaySpheres(new Vector3(0, 60, 0), -Vector3.UnitZ, 1000f, out var d));
        Assert.InRange(d, 450f, 500f);
        a.Die();
        Assert.Null(targets.RaySpheres(new Vector3(0, 60, 0), -Vector3.UnitZ, 1000f, out _));
    }

    [BehaviourDataFact]
    public void The_avatar_bodies_are_in_the_data_with_their_clips()
    {
        var models = new EnemyModels(BehaviourData.Data);
        foreach (var (body, head) in DmAvatarTable.All.Select(l => (l.Body, l.Head)).Distinct())
        {
            var b = models.Get(body);
            var h = models.Get(head);
            Assert.NotNull(b);
            Assert.NotNull(h);
            output.WriteLine($"{body}: {b!.Frames} frames, foot {b.FootY}, bounds {b.Bounds.Min} {b.Bounds.Max}; {head}: {h!.Frames} frames");
            Assert.True(b.Frames > DmAvatar.Attack.Last, $"{body} has {b.Frames} frames");
            Assert.True(h.Frames > DmAvatar.Attack.Last, $"{head} has {h.Frames} frames");
        }
    }
}
