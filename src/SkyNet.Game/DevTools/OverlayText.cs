using System.Globalization;
using System.Numerics;
using System.Text;
using RetroEng.Diagnostics;
using RetroEng.Render;

namespace SkyNet.Game.DevTools;

/// <summary>The program's memory: the GC heap, the renderer's GPU memory, what the last frame
/// allocated and the collections per generation.</summary>
public readonly record struct MemorySample(long GcHeap, long AllocatedPerFrame, int Gen0, int Gen1, int Gen2, RenderMemory Gpu = default)
{
    /// <summary>The heap and the collections now (<paramref name="allocatedPerFrame"/> and
    /// <paramref name="gpu"/> the caller's measures: the GPU's is read on main).</summary>
    public static MemorySample Now(long allocatedPerFrame, RenderMemory gpu) =>
        new(GC.GetTotalMemory(false), allocatedPerFrame, GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2), gpu);
}

/// <summary>The renderer's last frame and its resources.</summary>
public readonly record struct RendererSample(int DrawCalls, int Triangles, float GpuWait, int Textures, int Meshes, int UploadSubmits, int UploadSubmitsTotal);

/// <summary>The GPU and the window.</summary>
public readonly record struct SystemSample(string Device, string Backend, string Driver, int Width, int Height, string WindowMode, bool VSync);

/// <summary>Where the soldier is and what he rides.</summary>
public readonly record struct PlayerSample(int Map, int Mission, string Phase, Vector3 Feet, float YawDegrees, float PitchDegrees,
    bool Noclip, string Vehicle, float Health, float Armour, string View = "");

/// <summary>The map's robots and its marker-path vehicles.</summary>
public readonly record struct RobotSample(int Alive, int Dead, int Waiting, int PathMoving, int PathFinished, int PathTotal);

/// <summary>Everything the debug overlay shows of one moment; a part left null is not shown.</summary>
public sealed record OverlaySample
{
    public FrameSummary Frame { get; init; }
    /// <summary>The frame's parts: tick, draw, present, audio (milliseconds a frame).</summary>
    public IReadOnlyList<(string Name, float Milliseconds)> Sections { get; init; } = [];
    public IReadOnlyList<ZoneTime> Zones { get; init; } = [];
    public MemorySample Memory { get; init; }
    public RendererSample Renderer { get; init; }
    public SystemSample? System { get; init; }
    public PlayerSample? Player { get; init; }
    public RobotSample? Robots { get; init; }
    public ObjectiveReport? Objectives { get; init; }
}

/// <summary>
/// The FPS readout's line and the debug overlay's lines (the MDK port's OverlayText and TechInfo,
/// with the Godot port's mission block), as text only: the view draws them.
/// <code>
///   FPS 60  frame 16.7 ms avg, 18.2 max (60 frames)
///   tick 1.20  draw 0.80  present 3.10  audio 0.05 ms
///   Tick 1.10 x1: Session 1.05 (Player 0.20, Weapons 0.05, Robots 0.40, Behaviour 0.30)
///   Render 4.00 x1: Draw level 0.50, Present 3.10
///   other threads: Mix 0.30 x4, Music synth 0.10 x1
///   GC heap 12.3 MB, GPU 300.0 MB textures + 20.5 MB meshes, allocated 1.2 KB/frame, collections 3/1/0
///   draw calls 120, triangles 45123, GPU wait 1.2 ms; textures 300, meshes 500, uploads 0/frame (12 total)
///   GPU NVIDIA GeForce RTX 3060, Vulkan, driver 581.57; window 1280x720 windowed, VSync on
///   MAP.210 mission 1 Playing; feet 1200 64 -3400; yaw 90 pitch 0
///   on foot; health 1000, armour 0 %
///   robots 12 alive, 3 dead, 2 waiting; path vehicles 1 moving, 0 finished of 3
///   mission 1: objectives 5, done 2, left 3
///     [M2] Destroy the radar dish.
///   > [M2] @1a2b3 act 27 &lt;- switch @1a000 act 2C trigger  1234 u
/// </code></summary>
public static class OverlayText
{
    private const double Megabyte = 1024.0 * 1024.0;
    /// <summary>A zone this quick that did not run in the last frame is left out.</summary>
    private const float Idle = 0.005f;

    private static readonly CultureInfo C = CultureInfo.InvariantCulture;

    /// <summary>The corner readout: <c>60 FPS  16.7 ms  max 18.2  120 draws</c>.</summary>
    public static string FpsReadout(FrameSummary f, int drawCalls) =>
        string.Create(C, $"{f.Fps:0} FPS  {f.AverageMilliseconds:0.0} ms  max {f.MaxMilliseconds:0.0}  {drawCalls} draws");

    public static IReadOnlyList<string> Lines(OverlaySample s)
    {
        var lines = new List<string> { Frame(s.Frame) };
        if (s.Sections.Count > 0)
        {
            lines.Add(Sections(s.Sections));
        }

        lines.AddRange(Zones(s.Zones));
        lines.Add(Memory(s.Memory));
        lines.Add(Renderer(s.Renderer));
        if (s.System is { } system)
        {
            lines.Add(System(system));
        }

        if (s.Player is { } player)
        {
            lines.AddRange(Player(player));
        }

        if (s.Robots is { } robots)
        {
            lines.Add(Robots(robots));
        }

        if (s.Objectives is { } objectives)
        {
            lines.AddRange(Objectives(objectives));
        }

        return lines;
    }

    public static string Frame(FrameSummary f) =>
        string.Create(C, $"FPS {f.Fps:0}  frame {f.AverageMilliseconds:0.0} ms avg, {f.MaxMilliseconds:0.0} max ({f.Frames} frames)");

    public static string Sections(IReadOnlyList<(string Name, float Milliseconds)> sections) =>
        string.Join("  ", sections.Select(p => string.Create(C, $"{p.Name} {p.Milliseconds:0.00}"))) + " ms";

    /// <summary>One line per outermost zone of the game's thread, its inner zones after it (theirs in
    /// brackets), then the other threads' zones; milliseconds a frame, averaged; xN is how often it
    /// ran in the last frame. A line longer than <paramref name="width"/> characters goes on indented.</summary>
    public static IReadOnlyList<string> Zones(IReadOnlyList<ZoneTime> zones, int width = DefaultWidth)
    {
        var lines = new List<string>();
        bool Shown(in ZoneTime z) => z.Milliseconds >= Idle || z.Calls > 0;
        string Ms(in ZoneTime z) => string.Create(C, $"{z.Name} {z.Milliseconds:0.00}");
        foreach (var root in zones)
        {
            if (root.Parent >= 0 || root.OtherThread || !Shown(root))
            {
                continue;
            }

            var parts = new List<string>();
            foreach (var child in zones)
            {
                if (child.Parent != root.Id || !Shown(child))
                {
                    continue;
                }

                var inner = zones.Where(g => g.Parent == child.Id && Shown(g)).Select(g => Ms(g)).ToList();
                parts.Add(inner.Count > 0 ? $"{Ms(child)} ({string.Join(", ", inner)})" : Ms(child));
            }

            Wrap(string.Create(C, $"{Ms(root)} x{root.Calls}") + (parts.Count > 0 ? ":" : ""), parts, width, lines);
        }

        var others = zones.Where(z => z.OtherThread && Shown(z)).Select(z => string.Create(C, $"{Ms(z)} x{z.Calls}")).ToList();
        if (others.Count > 0)
        {
            Wrap("other threads:", others, width, lines);
        }

        return lines;
    }

    /// <summary>The overlay's lines are cut into this many characters at most where they list parts.</summary>
    public const int DefaultWidth = 110;

    private const string Continued = "    ";

    /// <summary><paramref name="head"/> and the parts after it, comma separated, on as many lines of
    /// <paramref name="width"/> as they need.</summary>
    private static void Wrap(string head, List<string> parts, int width, List<string> into)
    {
        var line = new StringBuilder(head);
        for (var i = 0; i < parts.Count; i++)
        {
            var part = parts[i] + (i < parts.Count - 1 ? "," : "");
            if (line.Length + 1 + part.Length > width && line.Length > Continued.Length)
            {
                into.Add(line.ToString());
                line.Clear().Append(Continued).Append(part);
            }
            else
            {
                line.Append(' ').Append(part);
            }
        }

        into.Add(line.ToString());
    }

    public static string Memory(MemorySample m) =>
        string.Create(C, $"GC heap {m.GcHeap / Megabyte:0.0} MB, GPU {m.Gpu.TextureBytes / Megabyte:0.0} MB textures + {m.Gpu.MeshBytes / Megabyte:0.0} MB meshes, ") +
        string.Create(C, $"allocated {Bytes(m.AllocatedPerFrame)}/frame, collections {m.Gen0}/{m.Gen1}/{m.Gen2}");

    public static string Renderer(RendererSample r) =>
        string.Create(C, $"draw calls {r.DrawCalls}, triangles {r.Triangles}, GPU wait {r.GpuWait:0.0} ms; ") +
        string.Create(C, $"textures {r.Textures}, meshes {r.Meshes}, uploads {r.UploadSubmits}/frame ({r.UploadSubmitsTotal} total)");

    public static string System(SystemSample s) =>
        $"GPU {(s.Device.Length != 0 ? s.Device : "unknown")}, {s.Backend}{(s.Driver.Length != 0 ? $", driver {s.Driver}" : "")}; " +
        string.Create(C, $"window {s.Width}x{s.Height} {s.WindowMode}, VSync {(s.VSync ? "on" : "off")}");

    public static IReadOnlyList<string> Player(PlayerSample p) =>
    [
        string.Create(C, $"MAP.{p.Map:D3} mission {(p.Mission > 0 ? p.Mission.ToString(C) : "-")} {p.Phase}; ") +
            string.Create(C, $"feet {p.Feet.X:0} {p.Feet.Y:0} {p.Feet.Z:0}; yaw {p.YawDegrees:0} pitch {p.PitchDegrees:0}") +
            (p.View.Length != 0 ? "; " + p.View : ""),
        string.Create(C, $"{p.Vehicle}; health {p.Health:0}, armour {p.Armour * 100f:0} %{(p.Noclip ? "; noclip" : "")}"),
    ];

    public static string Robots(RobotSample r) =>
        string.Create(C, $"robots {r.Alive} alive, {r.Dead} dead, {r.Waiting} waiting; ") +
        string.Create(C, $"path vehicles {r.PathMoving} moving, {r.PathFinished} finished of {r.PathTotal}");

    /// <summary>The counter, the entries still to come, the pointer and its note.</summary>
    public static IReadOnlyList<string> Objectives(ObjectiveReport o)
    {
        if (o.Mission <= 0)
        {
            return ["no mission"];
        }

        var lines = new List<string>
        {
            string.Create(C, $"mission {o.Mission}: objectives {o.Total}, done {o.Done}, left {o.Left}{(o.Complete ? ", complete" : "")}{(o.Failed ? ", failed" : "")}"),
        };
        lines.AddRange(o.Pending.Select(p => $"  {p}"));
        if (o.Target is { } t)
        {
            lines.Add(Pointer(t));
        }

        if (o.Note.Length != 0)
        {
            lines.Add(o.Note);
        }

        return lines;
    }

    /// <summary>The pointer's line: <c>> what  1234 u</c>.</summary>
    public static string Pointer(ObjectiveTarget t) => string.Create(C, $"> {t.Text}  {t.Distance:0} u");

    private static string Bytes(long bytes) =>
        bytes >= 1024 * 1024 ? string.Create(C, $"{bytes / Megabyte:0.0} MB")
        : bytes >= 1024 ? string.Create(C, $"{bytes / 1024.0:0.0} KB")
        : string.Create(C, $"{bytes} B");
}
