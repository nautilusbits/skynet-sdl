using System.Drawing;
using System.Numerics;
using SkyNet.Game.Automap;
using SkyNet.Game.Player;

namespace SkyNet.Game.Tests.Automap;

/// <summary>The automap's logic (SKYNET.EXE v1.01 0x13dc00): its camera, its keys and buttons,
/// and the seen set.</summary>
public class AutomapTests
{
    private const float Second = 1f;

    private static AutomapCamera OpenCamera(float yaw = 0f)
    {
        var camera = new AutomapCamera();
        camera.Open(new Vector3(100f, 75f, 200f), yaw, PlayerController.Direction(yaw, 0f));
        return camera;
    }

    [Fact]
    public void TheEyeStartsBehindAndAboveAtFortyFiveDegrees()
    {
        // Looking north (−Z): the eye is 0x200 south (+Z) and 0x200 up.
        var camera = OpenCamera();
        var eye = camera.Eye;
        Assert.Equal(100f, eye.X, 3);
        Assert.Equal(75f + 512f, eye.Y, 3);
        Assert.Equal(200f + 512f, eye.Z, 3);
        Assert.Equal(AutomapCamera.StartPitch, camera.Pitch);
    }

    [Fact]
    public void TurningOrbitsTheEyeAndTheLookTogether()
    {
        // A quarter turn left (512 units = 2 s): the eye goes round to the east, looking west.
        var camera = OpenCamera();
        camera.Step(AutomapKeys.TurnLeft, 2 * Second);
        Assert.Equal(512f, camera.Orbit, 3);
        Assert.Equal(100f + 512f, camera.Eye.X, 2);
        Assert.Equal(200f, camera.Eye.Z, 2);
        var look = PlayerController.Direction(camera.Look.Yaw, 0f);
        Assert.Equal(-1f, look.X, 3);
    }

    [Fact]
    public void TiltAndZoomStopAtDosLimits()
    {
        var camera = OpenCamera();
        camera.Step(AutomapKeys.TiltUp | AutomapKeys.ZoomIn, 10 * Second);
        Assert.Equal((AutomapCamera.MaxPitch, AutomapCamera.MinDistance), (camera.Pitch, camera.Distance));
        camera.Step(AutomapKeys.TiltDown | AutomapKeys.ZoomOut, 10 * Second);
        Assert.Equal((AutomapCamera.MinPitch, AutomapCamera.MaxDistance), (camera.Pitch, camera.Distance));

        // Zoom: 250 units a second.
        camera.Step(AutomapKeys.ZoomIn, 0.5f);
        Assert.Equal(AutomapCamera.MaxDistance - 125f, camera.Distance, 3);
    }

    [Fact]
    public void TheZoomIsKeptFromOneOpeningToTheNext()
    {
        var camera = OpenCamera();
        camera.Step(AutomapKeys.ZoomOut | AutomapKeys.TurnLeft | AutomapKeys.TiltUp, Second);
        camera.Open(Vector3.Zero, 0f, -Vector3.UnitZ);
        Assert.Equal(AutomapCamera.StartDistance + AutomapCamera.ZoomRate, camera.Distance);
        Assert.Equal((0f, AutomapCamera.StartPitch), (camera.Orbit, camera.Pitch));
    }

    [Fact]
    public void EscTabOrExitClose()
    {
        var map = new AutomapScreen();
        Assert.False(map.IsOpen);
        map.Open(Vector3.Zero, 0f, -Vector3.UnitZ);
        Assert.True(map.IsOpen);

        map.Update(new AutomapInput { Close = true }, Second / 60);
        Assert.False(map.IsOpen);

        // The EXIT button (DOS x 277..320 of the bar) closes on a click.
        map.Open(Vector3.Zero, 0f, -Vector3.UnitZ);
        map.Update(new AutomapInput { Pointer = new PointF(300, 8), Click = true, Held = true }, Second / 60);
        Assert.False(map.IsOpen);
    }

    [Fact]
    public void AHeldBarButtonRepeatsItsKey()
    {
        var map = new AutomapScreen();
        map.Open(Vector3.Zero, 0f, -Vector3.UnitZ);

        // The pointer over "+" (x 185..231) shows it lit; held, it zooms every frame.
        map.Update(new AutomapInput { Pointer = new PointF(200, 8) }, Second);
        Assert.Equal(AutomapButton.ZoomIn, map.Hovered);
        Assert.Equal(AutomapCamera.StartDistance, map.Camera.Distance);
        map.Update(new AutomapInput { Pointer = new PointF(200, 8), Held = true }, 0.2f);
        Assert.Equal(AutomapCamera.StartDistance - 50f, map.Camera.Distance, 3);

        // Below the bar: no button.
        map.Update(new AutomapInput { Pointer = new PointF(200, 100), Held = true }, 0.2f);
        Assert.Equal(AutomapButton.None, map.Hovered);
        Assert.Equal(AutomapCamera.StartDistance - 50f, map.Camera.Distance, 3);
    }

    [Fact]
    public void KeysAndButtonsAddUp()
    {
        var map = new AutomapScreen();
        map.Open(Vector3.Zero, 0f, -Vector3.UnitZ);
        map.Update(new AutomapInput { Keys = AutomapKeys.TurnRight }, 0.5f);
        Assert.Equal(-128f, map.Camera.Orbit, 3);
    }

    [Fact]
    public void TheSeenSetMarksClearsAndRestores()
    {
        var seen = new AutomapSeen();
        Assert.True(seen.Mark(0x3000));
        Assert.False(seen.Mark(0x3000));
        seen.Mark(0x2600);
        Assert.Equal([0x2600, 0x3000], seen.Save());

        seen.Clear();
        Assert.Equal(0, seen.Count);
        seen.Restore([7, 9]);
        Assert.True(seen.Contains(9));
    }
}

/// <summary>Which records the soldier's view marks seen (DOS FUN_00136fb1 marks what its renderer
/// drew) and which the automap's 5 × 5 cells hold.</summary>
public class AutomapIndexTests
{
    private static AutomapEntry Box(int offset, Vector3 centre) =>
        new(offset, (int)MathF.Floor(centre.X / 1024f), (int)MathF.Floor(-centre.Z / 1024f), new RetroEng.Physics.Box(centre - new Vector3(50f), centre + new Vector3(50f)));

    private static Matrix4x4 ViewNorth(Vector3 eye) =>
        Matrix4x4.CreateLookAt(eye, eye - Vector3.UnitZ, Vector3.UnitY) * Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 3f, 1.6f, 1f, 100000f);

    [Fact]
    public void WhatTheViewHoldsIsMarked()
    {
        var eye = new Vector3(500f, 75f, -500f);
        var index = new AutomapIndex([
            Box(1, eye + new Vector3(0f, 0f, -800f)),      // ahead
            Box(2, eye + new Vector3(0f, 0f, 800f)),       // behind
            Box(3, eye + new Vector3(3000f, 0f, -800f)),   // far off to the side
            Box(4, eye + new Vector3(0f, 0f, -9000f)),     // ahead, past the far clip
        ]);
        var seen = new AutomapSeen();

        index.Mark(seen, ViewNorth(eye), eye, far: 4000f);
        Assert.Equal([1], seen.Save());

        // No far clip (indoors): the distant one too.
        index.Mark(seen, ViewNorth(eye), eye, far: float.MaxValue);
        Assert.Equal([1, 4], seen.Save());
    }

    [Fact]
    public void TheWindowIsTheFiveByFiveCellsRoundTheEye()
    {
        // Cells 0..9 along X on DOS row 3; the eye in cell 5.
        var entries = Enumerable.Range(0, 10).Select(i => Box(i, new Vector3(i * 1024f + 512f, 0f, -(3 * 1024f + 512f)))).ToArray();
        var index = new AutomapIndex(entries);
        var seen = new AutomapSeen();
        foreach (var e in entries)
        {
            seen.Mark(e.Offset);
        }

        seen.Clear();
        seen.Restore([2, 3, 5, 7, 8, 9]);
        var shown = index.Shown(seen, new Vector3(5 * 1024f + 10f, 0f, -(3 * 1024f + 10f))).Select(e => e.Offset);
        Assert.Equal([3, 5, 7], shown);
    }
}

/// <summary>The index on a shipped map: its home cells are DOS's x &gt;&gt; 10, z &gt;&gt; 10.</summary>
public class AutomapRealDataTests
{
    [SkyNet.Game.Tests.Behaviour.BehaviourDataFact]
    public void MapTwoTenHasMeshesInTheirOwnCells()
    {
        var data = SkyNet.Game.Tests.Behaviour.BehaviourData.Data;
        var map = data.LoadMap(210)!;
        var index = AutomapIndex.Build(map, data.LoadModel);
        Assert.NotEmpty(index.Entries);
        foreach (var e in index.Entries)
        {
            var record = map.Entities.First(x => x.FileOffset == e.Offset);
            Assert.Equal((record.X >> 10, record.Z >> 10), (e.CellX, e.CellZ));
            Assert.False(record.IsActor);
        }
    }
}

/// <summary>The automap's look: DOS's extra directional light (0x14d7f7, intensity 24 of 32) and
/// the flat face mode at RENDER DETAIL LOW / MED (DOS's mode 4).</summary>
public class AutomapLookTests
{
    [Fact]
    public void FacesTowardTheLightAreBrightest()
    {
        var top = AutomapLight.Shade(Vector3.UnitY);
        var side = AutomapLight.Shade(Vector3.UnitX);
        var under = AutomapLight.Shade(-Vector3.UnitY);
        Assert.True(top > side && side >= under, $"{top} {side} {under}");
        Assert.Equal(SkyNet.Formats.HazeTable.Steps - 1, top);

        // Away from it: the ambient only; no normal: as if facing it.
        Assert.Equal(AutomapLight.AmbientRow, under);
        Assert.Equal(top, AutomapLight.Shade(Vector3.Zero));
    }

    [Theory]
    [InlineData(Detail.Low, true)]
    [InlineData(Detail.Medium, true)]
    [InlineData(Detail.High, false)]
    public void LowAndMediumDetailDrawFlat(Detail detail, bool flat) => Assert.Equal(flat, AutomapLight.StyleOf(detail) == AutomapStyle.Flat);
}
