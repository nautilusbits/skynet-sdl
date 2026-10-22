using System.Numerics;
using RetroEng.Diagnostics;
using RetroEng.Render;
using SkyNet.Game.DevTools;

namespace SkyNet.Game.Tests.DevTools;

/// <summary>The FPS readout's and the debug overlay's text, section by section (no GPU).</summary>
public class OverlayTextTests
{
    private static readonly FrameSummary Frame = new(60f, 16.67f, 18.2f, 60);

    [Fact]
    public void FpsReadoutAndFrameLine()
    {
        Assert.Equal("60 FPS  16.7 ms  max 18.2  915 draws", OverlayText.FpsReadout(Frame, 915));
        Assert.Equal("FPS 60  frame 16.7 ms avg, 18.2 max (60 frames)", OverlayText.Frame(Frame));
        Assert.Equal("tick 1.20  draw 0.80  present 3.10 ms", OverlayText.Sections([("tick", 1.2f), ("draw", 0.8f), ("present", 3.1f)]));
    }

    [Fact]
    public void ZonesNestUnderTheirOutermostAndOtherThreadsGoApart()
    {
        ZoneTime Z(int id, string name, int parent, float ms, int calls = 1, bool other = false) => new(id, name, parent, ms, ms, calls, other);
        IReadOnlyList<ZoneTime> zones =
        [
            Z(0, "Tick", -1, 1.5f, 2),
            Z(1, "Session", 0, 1.4f),
            Z(2, "Player", 1, 0.2f),
            Z(3, "Robots", 1, 1.1f),
            Z(4, "Weapons", 1, 0.001f, calls: 0),   // idle: left out
            Z(5, "HUD tick", 0, 0.01f),
            Z(6, "Map enter", -1, 0f, calls: 0),    // not this frame: left out
            Z(7, "Mix", -1, 0.3f, 4, other: true),
        ];
        Assert.Equal(
        [
            "Tick 1.50 x2: Session 1.40 (Player 0.20, Robots 1.10), HUD tick 0.01",
            "other threads: Mix 0.30 x4",
        ], OverlayText.Zones(zones));
        // A long line goes on indented, cut between the parts.
        Assert.Equal(
        [
            "Tick 1.50 x2: Session 1.40 (Player 0.20, Robots 1.10),",
            "    HUD tick 0.01",
            "other threads: Mix 0.30 x4",
        ], OverlayText.Zones(zones, width: 60));
    }

    [Fact]
    public void MemoryRendererAndSystem()
    {
        Assert.Equal("GC heap 12.0 MB, GPU 300.0 MB textures + 20.5 MB meshes, allocated 1.5 KB/frame, collections 3/1/0",
            OverlayText.Memory(new MemorySample(12 * 1024 * 1024, 1536, 3, 1, 0, new RenderMemory(300L << 20, 41L << 19))));
        Assert.Equal("GC heap 0.0 MB, GPU 0.0 MB textures + 0.0 MB meshes, allocated 0 B/frame, collections 0/0/0", OverlayText.Memory(default));
        Assert.Equal("draw calls 915, triangles 15263, GPU wait 8.5 ms; textures 454, meshes 133, uploads 2/frame (17 total)",
            OverlayText.Renderer(new RendererSample(915, 15263, 8.5f, 454, 133, 2, 17)));
        Assert.Equal("GPU Some GPU, Vulkan, driver 1.2.3; window 1280x720 windowed, VSync on",
            OverlayText.System(new SystemSample("Some GPU", "Vulkan", "1.2.3", 1280, 720, "windowed", true)));
        Assert.Equal("GPU unknown, Direct3D 12; window 640x480 fullscreen, VSync off",
            OverlayText.System(new SystemSample("", "Direct3D 12", "", 640, 480, "fullscreen", false)));
    }

    [Fact]
    public void PlayerAndRobots()
    {
        Assert.Equal(
        [
            "MAP.210 mission 1 Playing; feet 58159 1160 -53896; yaw 86 pitch -5",
            "jeep: speed 0; health 1000, armour 50 %; noclip",
        ], OverlayText.Player(new PlayerSample(210, 1, "Playing", new Vector3(58159.4f, 1160f, -53896f), 86.4f, -5f, true, "jeep: speed 0", 1000f, 0.5f)));
        Assert.Equal("MAP.299 mission - Playing; feet 0 0 0; yaw 0 pitch 0",
            OverlayText.Player(new PlayerSample(299, 0, "Playing", Vector3.Zero, 0f, 0f, false, "on foot", 1f, 0f))[0]);
        Assert.Equal("robots 19 alive, 2 dead, 3 waiting; path vehicles 1 moving, 0 finished of 2",
            OverlayText.Robots(new RobotSample(19, 2, 3, 1, 0, 2)));
    }

    [Fact]
    public void ObjectivesPendingPointerAndNote()
    {
        var report = new ObjectiveReport(1, 3, 1, false, false, ["[M2] Find the nuke.", "[M3] Let's roll!"],
            new ObjectiveTarget(Vector3.Zero, "exit @06d6d -> MAP.211", 3212.4f), "next objective on MAP.215: ...");
        Assert.Equal(
        [
            "mission 1: objectives 3, done 2, left 1",
            "  [M2] Find the nuke.",
            "  [M3] Let's roll!",
            "> exit @06d6d -> MAP.211  3212 u",
            "next objective on MAP.215: ...",
        ], OverlayText.Objectives(report));
        Assert.Equal("mission 2: objectives 1, done 1, left 0, complete",
            OverlayText.Objectives(new ObjectiveReport(2, 1, 0, true, false, [], null, ""))[0]);
        Assert.Equal(["no mission"], OverlayText.Objectives(ObjectiveReport.None));
    }

    [Fact]
    public void TheWholeOverlayInOrder()
    {
        var lines = OverlayText.Lines(new OverlaySample
        {
            Frame = Frame,
            Sections = [("tick", 1f)],
            Renderer = new RendererSample(1, 2, 0f, 3, 4, 0, 0),
            Robots = new RobotSample(1, 0, 0, 0, 0, 0),
            Objectives = ObjectiveReport.None,
        });
        Assert.Equal(
        [
            "FPS 60  frame 16.7 ms avg, 18.2 max (60 frames)",
            "tick 1.00 ms",
            "GC heap 0.0 MB, GPU 0.0 MB textures + 0.0 MB meshes, allocated 0 B/frame, collections 0/0/0",
            "draw calls 1, triangles 2, GPU wait 0.0 ms; textures 3, meshes 4, uploads 0/frame (0 total)",
            "robots 1 alive, 0 dead, 0 waiting; path vehicles 0 moving, 0 finished of 0",
            "no mission",
        ], lines);
    }
}
