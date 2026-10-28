using System.Runtime.ExceptionServices;
using RetroEng.Diagnostics;
using RetroEng.Loop;
using RetroEng.Platform;

// The loop's threads (--threads, docs/plans/engine-threads.md T6, B1 and decision 1). Single: today's
// loop, all on main. Split: three threads, the modes unchanged.
//
//   main          SDL events ─► queue          window requests ◄─ queue       present the built frames
//   simulation    PumpEvents(queue) → menus, console, Pump → FixedLoop ticks → Capture(snapshot) ─┐
//   frame build                                        BuildFrame(snapshot) → packet ◄────────────┘─► main
//
// The simulation hands each frame's snapshot over and runs the next ticks while it is drawn; a frame
// with the menus or the console up is drawn while it waits (they are read live). Before it changes
// what the build reads by reference (a level, the look, the game) it waits for the frames in flight
// (WaitForFrames), so ticks run in the single thread's order and demos replay as on one thread.
internal sealed partial class AppHost
{
    /// <summary>How long main waits for a built frame before it pumps the events again (ms).</summary>
    private const int MainWaitMs = 1;

    /// <summary>The snapshots pooled: one drawn while the next is caught.</summary>
    private const int SnapshotBuffers = 2;

    /// <summary>The frame-build thread of a split loop; null: single.</summary>
    private FrameBuildThread<FrameSnapshot>? frameBuild;

    /// <summary>The loop's frame callback; a split loop's frame build is started here.</summary>
    private Action<float> FrameCallback()
    {
        frameBuild = options.Threads == LoopThreads.Split ? new FrameBuildThread<FrameSnapshot>(SnapshotBuffers, () => new FrameSnapshot(), BuildFrame) : null;
        return DrawFrame;
    }

    /// <summary>The simulation (or main before the loop): waits until no frame is being drawn, before
    /// it changes what a frame build reads by reference (the level, the drawers, the renderer's scene state).</summary>
    private void WaitForFrames() => frameBuild?.Drain();

    /// <summary>Runs the loop to its end on the threads asked for; rethrows what a thread threw.</summary>
    private void RunLoop(LoopHost host)
    {
        if (frameBuild == null)
        {
            loop!.Run(host, options.Frames);
            return;
        }

        Exception? failure = null;
        var running = 1;
        ThreadGuard.TakeTurn();
        window.Events = EventSource.Queue;
        renderer.StartBuilding();
        var simulation = new Thread(() =>
        {
            try
            {
                ThreadGuard.TakeTurn();
                // The zone timer nests the zones of the thread that made it: the ticks' and the capture's.
                zoneTimes = new ZoneTimes();
                ApplyZoneTimer();
                loop!.Run(host, options.Frames);
                frameBuild.Drain();
            }
            catch (Exception e)
            {
                failure = e;
                // The frames in flight still need main to present them before the build can stop.
                try
                {
                    frameBuild.Drain();
                }
                catch (Exception)
                {
                    // The first failure is the one reported.
                }
            }
            finally
            {
                Volatile.Write(ref running, 0);
            }
        })
        { Name = "Simulation" };
        simulation.Start();

        // Main: the events, the window requests and the frames, until the simulation ends.
        while (Volatile.Read(ref running) == 1)
        {
            window.QueueEvents();
            window.RunRequests();
            if (renderer.PresentBuilt(MainWaitMs))
            {
                Presented();
                SampleGpu();
            }
        }

        simulation.Join();
        window.RunRequests();

        // The last frame published (a --screenshot's) is shown before the threads stop.
        while (renderer.PresentBuilt(0))
        {
        }

        frameBuild.Dispose();
        renderer.StopBuilding();
        window.Events = EventSource.Sdl;
        ThreadGuard.TakeTurn();
        Log.Info("App", $"Threads: split; the simulation waited {frameBuild.WaitMs * 1000.0:0.#} us a frame for the frame build ({frameBuild.Frames} frames)");
        if (failure != null)
        {
            ExceptionDispatchInfo.Throw(failure);
        }
    }
}
