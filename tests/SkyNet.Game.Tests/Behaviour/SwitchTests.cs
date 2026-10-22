using System.Numerics;
using SkyNet.Formats;
using SkyNet.Game.Behaviour;

namespace SkyNet.Game.Tests.Behaviour;

/// <summary>
/// The wall switches. The 0xEF handler (v1.01 0x13868d) ends with a turn: a variant-1 gate with
/// state bit 3 adds 0x400 to its yaw on every press (0x13873e). The switch models are one quad
/// with the OFF art on the front face and the ON art on the back, so the half turn shows the ON
/// art where the OFF art was: the white panel lights, the purple one is pressed in, the lever
/// stands up. A second press turns it back.
/// </summary>
public class SwitchTests
{
    /// <summary>A fact skipped unless Future Shock's data sits beside SKYNET_DATA's.</summary>
    private sealed class ShockDataFactAttribute : FactAttribute
    {
        public ShockDataFactAttribute()
        {
            if (BehaviourData.Dir is not { } dir || GameDataLocator.OtherGameDir(dir) == null)
            {
                Skip = "no Future Shock data beside SKYNET_DATA's SkyNET folder";
            }
        }
    }

    /// <summary>The texture (bank / record) of the switch's face that looks the way the OFF face
    /// looked as the map placed it.</summary>
    private static (int Bank, int Record) ShownFace(Harness h, int off)
    {
        Assert.True(h.Map.TryGetEntityAt(off, out var e));
        var model = BehaviourData.Data.LoadModel(h.Map.EntityName(e) + ".3D")!;
        var placed = BehaviourModel.Placement(BehaviourModel.Euler(e), BehaviourModel.Position(e));
        Assert.True(h.B.TryGetPose(off, out var pose));
        var now = placed * pose.Delta;
        // FaceNormal is in level axes already (the winding normal of the Y-up vertices).
        var front = Vector3.TransformNormal(model.FaceNormal(model.Faces[0]), placed);
        foreach (var f in model.Faces)
        {
            var n = Vector3.TransformNormal(model.FaceNormal(f), now);
            if (Vector3.Dot(n, front) > 0.99f)
            {
                return (f.TextureBank, f.TextureRecord);
            }
        }

        throw new InvalidOperationException("no face looks the front's way");
    }

    private static void PressTurns(int map, int off, string model, (int, int) offFace, (int, int) onFace)
    {
        var h = new Harness(map);
        Assert.True(h.Map.TryGetEntityAt(off, out var e));
        Assert.Equal(model, h.Map.EntityName(e));
        Assert.True(BehaviourModel.IsSwitch(e));
        Assert.True(BehaviourModel.IsDynamic(h.Map, e, BehaviourData.Transfrm), "a switch is drawn through its pose");
        Assert.Equal(offFace, ShownFace(h, off));
        Assert.False(h.B.SwitchOn(off));

        h.Use(off, aim: true);
        Assert.True(h.B.SwitchOn(off));
        Assert.Equal(onFace, ShownFace(h, off));
        // A half turn about the record's own vertical: it stays where it is.
        Assert.True(h.B.TryGetPose(off, out var pose));
        Assert.True(Vector3.Distance(Vector3.Transform(h.PositionOf(off), pose.Delta), h.PositionOf(off)) < 0.01f);

        h.Use(off, aim: true);
        Assert.False(h.B.SwitchOn(off));
        Assert.Equal(offFace, ShownFace(h, off));
    }

    /// <summary>MAP.210's tower panel (BUTTON01, the white switch): OFF art 222/0, lit 222/1.</summary>
    [BehaviourDataFact]
    public void PressingAWhiteSwitchLightsIt() => PressTurns(210, 0x09551, "BUTTON01", (222, 0), (222, 1));

    /// <summary>MAP.210's fence panel (BUTTON02, the purple switch): 222/2, pressed in 222/3.</summary>
    [BehaviourDataFact]
    public void PressingAPurpleSwitchPressesItIn() => PressTurns(210, 0x0886b, "BUTTON02", (222, 2), (222, 3));

    /// <summary>MAP.280's lever (BUTTON03, yellow): lying 222/4, standing 222/5.</summary>
    [BehaviourDataFact]
    public void PressingALeverFlipsIt() => PressTurns(280, 0x072f3, "BUTTON03", (222, 4), (222, 5));

    /// <summary>Only the key's handler turns a switch: a chain reaching one (MAP.280's BUTTON01
    /// @08a18 runs through BUTTON01 @08bae) flips its bit and leaves it facing as it was.</summary>
    [BehaviourDataFact]
    public void AChainReachingASwitchDoesNotTurnIt()
    {
        var h = new Harness(280);
        h.Use(0x08a18, aim: true);
        Assert.True(h.B.SwitchOn(0x08a18));
        Assert.False(h.B.SwitchOn(0x08bae));
    }

    /// <summary>A turned switch survives a save.</summary>
    [BehaviourDataFact]
    public void ATurnedSwitchIsInTheSnapshot()
    {
        var h = new Harness(210);
        h.Use(0x09551, aim: true);
        var snap = h.B.Snapshot();
        Assert.Equal(new[] { 0x09551 }, snap.Turned ?? []);
        var back = new LevelBehaviour(h.Map, BehaviourData.Transfrm, new MissionProgress(5));
        back.Restore(snap);
        Assert.True(back.SwitchOn(0x09551));
    }

    /// <summary>
    /// MAP.215's silo switch (BUTTON01 @02f8f → HADES02, HADES01 (0x70 slides up 1024), the silo
    /// sound, DORB, objective [M2], the "my god, it's a nuke" line). DOS has no one-shot guard on
    /// this chain: ObjFlipLink (0x139caa) XORs bit 0 of every link and does not stop at a retired
    /// act; the slide (0x138287) clears its bit and swaps its act to the other parity (0x70 → 0x6F)
    /// at the limit; the voice (0x13865c) clears its own bit; only the objective (0x137fd0) retires.
    /// So the first press raises the missile and counts the objective; a second press after the
    /// rise sends the missile back down and says the line again, but counts nothing.
    /// </summary>
    [BehaviourDataFact]
    public void TheSiloSwitchCountsOnceAndASecondPressRunsTheChainBackAsDos()
    {
        var mission = new MissionProgress(5);
        var h = new Harness(215, mission);
        const int Button = 0x02f8f, Missile = 0x047f0;
        var first = Harness.Comparable(h.Use(Button, aim: true));
        Assert.Contains("obj1", first);
        Assert.Equal(4, mission.Left);
        Assert.Equal(1024f, h.B.MoverProgress(Missile).Progress);

        var second = Harness.Comparable(h.Use(Button, aim: true));
        Assert.DoesNotContain(second, f => f.StartsWith("obj", StringComparison.Ordinal));
        Assert.Equal(4, mission.Left);
        Assert.Contains("move@047f0:slideY+1024", second);
        Assert.Equal(0f, h.B.MoverProgress(Missile).Progress);
    }

    /// <summary>Future Shock MAP.013: the BUTTON01 @03fb8 opens the doors on its floor. The doorway
    /// sprite @03d84 (0xF0 → MAP.010) hangs 293 u over the soldier's feet, two storeys up: the key
    /// presses the switch and does not take that exit (DOS 0x138081 has no touch at all).</summary>
    [ShockDataFact]
    public void FutureShockMap013SwitchDoesNotTakeTheExitAbove()
    {
        var data = GameData.Open(GameDataLocator.OtherGameDir(BehaviourData.Dir!)!, BehaviourData.Dir)!;
        var map = data.LoadMap(13)!;
        var b = new LevelBehaviour(map, BehaviourModel.LoadTransfrm(data), new MissionProgress(5));
        b.Start(Harness.Away, Harness.Away);
        var eye = new Vector3(2021f, -176f, -2421f);
        var feet = eye - new Vector3(0f, Player.PlayerController.EyeHeight, 0f);
        const int Button = 0x03fb8;

        b.Tick(Harness.Dt, new PlayerProbe(feet, eye, UsePressed: true, AimedOwner: Button));
        b.Tick(Harness.Dt, new PlayerProbe(feet, eye));

        Assert.True(b.SwitchOn(Button));
        Assert.False(b.ExitTaken);
    }
}
