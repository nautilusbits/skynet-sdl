using System.Numerics;
using RetroEng.Physics;
using SkyNet.Game.Behaviour;
using SkyNet.Game.Collision;

namespace SkyNet.Game.Tests.Behaviour;

/// <summary>Scripted play on real maps (docs/missions.md, the Godot port's mission specs and the
/// DOS run notes): MAP.210's gate and cargo boxes, mission 1's chain of maps to the end of the
/// counter, doors that open and close in the collision world, a lift that carries the player.</summary>
public class ScenarioTests
{
    private static Vector3 Eye(Harness h, int off) => h.PositionOf(off);

    /// <summary>MAP.210: coming out of the canyon the gate (BIGDOOR, two leaves sliding apart)
    /// opens and the first cargo truck sets off along its path; walking on towards the gate the
    /// 0xF2 trigger behind it closes it again; the second cargo box is the doorway in (MAP.212).</summary>
    [BehaviourDataFact]
    public void Map210GateOpensForTheTruckThenCloses()
    {
        var h = new Harness(210);
        Assert.Equal("BIGDOOR", h.Map.EntityName(h.Map.TryGetEntityAt(0x078f7, out var leaf) ? leaf : null!));

        // The truck's trigger: its path starts.
        var fx = h.Prox(0x0c084);
        Assert.Contains("path@078d3", fx);

        // The canyon exit: the gate opens (if the truck trigger's reach did not already open it).
        fx.AddRange(h.Prox(0x077f3));
        Assert.Contains("move@078f7:slideX-128", fx);
        Assert.Contains("move@07991:slideX+128", fx);
        Assert.Equal(128f, h.B.MoverProgress(0x078f7).Progress);
        Assert.True(h.B.TryGetPose(0x078f7, out var open));
        Assert.Equal(-128f, open.Delta.Translation.X, 0.01f);

        // On towards the gate: the 1024-unit trigger behind it shuts it.
        Assert.True(h.B.Enabled(0x07851), "the 0xF2 behind the gate is armed at load");
        fx = h.Prox(0x07851);
        Assert.Contains("move@078f7:slideX+128", fx);
        Assert.Contains("move@07991:slideX-128", fx);
        Assert.Equal(0f, h.B.MoverProgress(0x078f7).Progress);

        // The cargo box doorways: the use key at the box, the sound, the map change.
        Assert.Equal(["exit212/0", "snd40"], Harness.Comparable(h.Use(0x0773f)));
        Assert.True(h.B.ExitTaken);
        Assert.Equal(212, h.B.PendingExit!.Value.TargetMap);
    }

    /// <summary>A use-key gate needs the eye within 60 (+26) units and the key going down:
    /// standing in it does nothing, the key from 200 units does nothing.</summary>
    [BehaviourDataFact]
    public void UseKeyGateNeedsReachAndTheKey()
    {
        var h = new Harness(210);
        var gate = Eye(h, 0x0773f);
        h.B.Tick(Harness.Dt, new PlayerProbe(gate, gate));
        h.Settle(1f);
        Assert.Empty(h.Log.Drain());
        var far = gate + new Vector3(200f, 0f, 0f);
        h.B.Tick(Harness.Dt, new PlayerProbe(far, far, UsePressed: true));
        h.Settle(1f);
        Assert.Empty(h.Log.Drain());
        Assert.False(h.B.ExitTaken);
        h.B.Tick(Harness.Dt, new PlayerProbe(gate, gate, UsePressed: true));
        Assert.True(h.B.ExitTaken);
    }

    /// <summary>Arriving at a doorway's marker inside the return gate's reach: the 0xF1/0xF2
    /// triggers around the spawn are pre-latched and fire only after the player has left them.</summary>
    [BehaviourDataFact]
    public void ProximityTriggersArePreLatchedAtTheSpawn()
    {
        var h = new Harness(210);
        var at = Eye(h, 0x0c084);
        h.B.Arm(at, at);
        h.B.Tick(Harness.Dt, new PlayerProbe(at, at));
        Assert.DoesNotContain("path@078d3", h.Log.Drain());
        h.Settle(0.1f);
        h.B.Tick(Harness.Dt, new PlayerProbe(at, at));
        Assert.Contains("path@078d3", h.Log.Drain());
    }

    /// <summary>Mission 1 as the Godot port's mission spec plays it (counter 3 from 210.TXT):
    /// 210 → 214 → 215 (the floor pad, then the lift button) → 217 and the last objective.</summary>
    [BehaviourDataFact]
    public void Mission1RunsTheCounterDown()
    {
        var mission = MissionProgress.Load(BehaviourData.Data, 210);
        Assert.Equal(3, mission.Total);

        var m210 = new Harness(210, mission);
        Assert.Equal(["exit214/0", "snd40"], Harness.Comparable(m210.Use(0x07fcb)));
        var m214 = new Harness(214, mission);
        Assert.Equal(["exit215/0", "snd40"], Harness.Comparable(m214.Use(0x02f4e)));

        var m215 = new Harness(215, mission);
        Assert.Equal(Harness.Comparable(["snd77", "obj0", "voice21038", "move@03125:slideZ+256", "move@030eb:slideX-256",
            "move@030b1:slideZ-256", "move@03077:slideX+256"]), Harness.Comparable(m215.Walk(0x032cb)));
        Assert.Equal(2, mission.Left);
        Assert.Equal(Harness.Comparable(["snd77", "obj1", "voice21039", "move@047f0:slideY-1024", "move@04719:slideY-1024",
            "move@03515:slideY-128"]), Harness.Comparable(m215.Use(0x02f8f, aim: true)));
        Assert.Equal(1, mission.Left);
        Assert.Equal(["exit217/15", "snd41"], Harness.Comparable(m215.Use(0x04130)));

        var done = new CompletionProbe();
        var m217 = new Harness(217, mission);
        var b = new LevelBehaviour(m217.Map, BehaviourData.Transfrm, mission, new EffectLog(done));
        b.Start(Harness.Away, Harness.Away);
        var at = m217.PositionOf(0x0ba48);
        b.Tick(Harness.Dt, new PlayerProbe(at, at, UsePressed: true));
        Assert.Equal(0, mission.Left);
        Assert.True(mission.Complete);
        Assert.True(done.Complete);
        Assert.Equal(2, done.Index);
    }

    private sealed class CompletionProbe : IBehaviourListener
    {
        public bool Complete;
        public int Index = -1;
        public void Objective(int source, int index, int left, string text) => Index = index;
        public void MissionComplete() => Complete = true;
    }

    /// <summary>A door that opens moves out of the way in the collision world: the static grid no
    /// longer holds it, its dynamic collider follows the pose (MAP.214's sliding door 03163).</summary>
    [BehaviourDataFact]
    public void OpenDoorMovesItsCollider()
    {
        var h = new Harness(214);
        var door = 0x03163;
        var (world, _) = LevelCollision.Build(BehaviourData.Data, h.Map, null, h.B.IsDynamic);
        var colliders = h.B.BuildColliders(BehaviourData.Data);
        world.Add(colliders);
        Assert.True(colliders.Count > 0);
        Assert.DoesNotContain(world.Sources.OfType<TriangleGrid>().SelectMany(g => g.Triangles), t => t.Owner == door);

        var box = Owned(world, door);
        Assert.False(box.IsEmpty);
        h.Use(0x031b4, aim: true);
        var (progress, span, _) = h.B.MoverProgress(door);
        Assert.Equal(span, progress);
        var moved = Owned(world, door);
        // slideY −128 on DOS Y (down) is +128 up in level axes.
        Assert.Equal(box.Min.Y + 128f, moved.Min.Y, 0.5f);
        Assert.Equal(box.Min.X, moved.Min.X, 0.5f);
    }

    private static Box Owned(CollisionWorld world, int owner)
    {
        var all = new List<Triangle>();
        world.Gather(new Box(new Vector3(-1e7f), new Vector3(1e7f)), all);
        var b = Box.Empty;
        foreach (var t in all.Where(t => t.Owner == owner))
        {
            b = b.Include(t.A).Include(t.B).Include(t.C);
        }

        return b;
    }

    /// <summary>A lift carries what stands on it: MAP.215's 0x70 platforms rise 1024 units and the
    /// tick returns the climb for feet whose floor owner is the platform.</summary>
    [BehaviourDataFact]
    public void LiftCarriesThePlayer()
    {
        var h = new Harness(215);
        var lift = 0x047f0;
        var feet = h.PositionOf(lift);
        var at = h.PositionOf(0x02f8f);
        var climbed = h.B.Tick(Harness.Dt, new PlayerProbe(feet, at, FloorOwner: lift, UsePressed: true, AimedOwner: 0x02f8f)).Y;
        for (var i = 0; i < 35 * 30; i++)
        {
            var carry = h.B.Tick(Harness.Dt, new PlayerProbe(feet, feet, FloorOwner: lift));
            feet += carry;
            climbed += carry.Y;
        }

        Assert.Equal(1024f, climbed, 1f);
        Assert.False(h.B.MoverRunning(lift));
    }

    /// <summary>Destruction: a crate (hp, no act) goes at one blow and leaves the collision world;
    /// a TRANSFRM.PRS wreck steps a stage per hit; demolish (0x1B) kills through the same path.</summary>
    [BehaviourDataFact]
    public void DestructiblesBreakThroughTheDamageApi()
    {
        var h = new Harness(200);
        // A wreck: one stage per qualifying hit, whatever the damage.
        Assert.Contains("break@074a5:1", h.Shoot(0x074a5, 100f));
        Assert.Equal(1, h.B.StageOf(0x074a5));
        Assert.True(h.B.TryGetPose(0x074a5, out var wreck) && wreck.Visible);

        // A plain prop with hit points (MAP.210): gone when they run out.
        h = new Harness(210);
        var crate = h.Map.Entities.First(e => e.Variant == SkyNet.Formats.MapEntityVariant.Mesh && e.HitPoints > 0
            && e.ActType == 0 && (e.StateByte & 6) == 0 && h.B.KindOf(e.FileOffset) == NodeKind.Damageable).FileOffset;
        Assert.True(h.B.IsDamageable(crate));
        h.B.Hit(crate, 1f);
        Assert.True(h.B.TryGetPose(crate, out var hurt) && hurt.Visible);
        h.B.Hit(crate, 10000f);
        Assert.Contains($"destroyed@{crate:x5}", h.Log.Drain());
        Assert.True(h.B.TryGetPose(crate, out var gone) && !gone.Visible && !gone.Solid);
        Assert.False(h.B.IsDamageable(crate));
    }

    /// <summary>
    /// A TRANSFRM.PRS car (MAP.210's COPCAR0A: hp 150, state 2, act 0x19, frames a/b/c): ObjHit
    /// (v1.01 0x139819) runs handler 0x120833 on every hit, one stage per hit whatever the damage,
    /// and the last frame stays; the hit points drain all the while, and when they run out the car
    /// leaves the world (0x139778) through the destruction path (type 0: one blast, no drop).
    /// </summary>
    [BehaviourDataFact]
    public void A_staged_car_steps_one_frame_a_hit_then_burns_out_when_its_points_run_out()
    {
        const int Car = 0x0c9fb;
        var h = new Harness(210);
        Assert.Equal(3, BehaviourData.Transfrm.StagesOf(h.Map.EntityName(h.Map.Entities.First(e => e.FileOffset == Car))).Length);
        Assert.Equal(150f, h.B.Hp(Car));

        Assert.Equal(["break@0c9fb:1"], h.Shoot(Car, 12.5f));
        Assert.True(h.B.TryGetPose(Car, out var p1) && p1.Visible && p1.Solid && p1.Stage == 1);
        Assert.Equal(["break@0c9fb:2"], h.Shoot(Car, 12.5f));
        Assert.True(h.B.TryGetPose(Car, out var p2) && p2.Visible && p2.Solid && p2.Stage == 2);

        // The last frame stands and still takes damage.
        Assert.Empty(h.Shoot(Car, 12.5f));
        Assert.True(h.B.IsDamageable(Car));
        Assert.Equal(112.5f, h.B.Hp(Car));
        Assert.True(h.B.TryGetPose(Car, out var p3) && p3.Visible && p3.Stage == 2);

        Assert.Equal(["destroyed@0c9fb"], h.Shoot(Car, 112.5f));
        Assert.True(h.B.TryGetPose(Car, out var gone) && !gone.Visible && !gone.Solid);
        Assert.False(h.B.IsDamageable(Car));
        Assert.Empty(h.Shoot(Car, 50f));
    }

    /// <summary>Mission 3's consoles: a chain of nine buttons ends in demolish 0x1B and the robot
    /// spawns (the mission spec's MAP.232 step).</summary>
    [BehaviourDataFact]
    public void Map232LastConsoleDemolishesAndSpawns()
    {
        var mission = MissionProgress.Load(BehaviourData.Data, 230);
        var h = new Harness(232, mission);
        foreach (var console in new[] { 0x0325c, 0x04461, 0x04966, 0x049a0, 0x04a14, 0x058a5, 0x05919, 0x05fb0 })
        {
            Assert.Contains("obj0", h.Use(console, aim: true));
        }

        Assert.Equal(2, mission.Left);
        var fx = h.Use(0x05fea, aim: true);
        Assert.Equal(1, mission.Left);
        Assert.Contains("demolish@03512", fx);
        Assert.Contains("hint4", fx);
        foreach (var s in new[] { "04f42", "04f66", "04f8a", "036b2", "04612", "04636", "0465a" })
        {
            Assert.Contains($"spawn@{s}", fx);
        }

        Assert.True(h.B.TryGetPose(0x03512, out var p) && !p.Visible);
    }
}
