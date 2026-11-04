using System.Diagnostics;
using System.Globalization;
using RetroEng.Diagnostics;
using RetroEng.Platform;
using RetroEng.Render;
using SkyNet.Game;
using SkyNet.Game.Enhanced;
using SkyNet.Game.Menu;

// The ENHANCED look's bounced light baked at a map's first load, on a worker, while the load's thread
// shows its progress (main keeps answering the window: no ANR on Android), then kept in the cache.
//
//   EnterLevel ─► IrradianceOf ─► cache hit ──────────────────────────────────────► level.Irradiance
//                               └ miss ─► IrradianceJob ─► "PREPARING LIGHTING n%" ─► save ─┘
//                                          quit / back ─► abandoned: the even ambient
internal sealed partial class AppHost
{
    /// <summary>The level's bar the bake fills: up to its next stage ("Building the robots...").</summary>
    private const float LightingBar = 1f / 3f;
    private const string RateFile = "bake-rate.txt";

    // The bakes that failed this session (their files): not tried again at every load.
    private readonly HashSet<string> failedBakes = new(StringComparer.OrdinalIgnoreCase);
    private bool bakeRateRead;

    /// <summary>How a bake waited for ended.</summary>
    private enum BakeWait { Done, Abandoned }

    /// <summary>The map's volume baked on a worker while the loading frame shows its progress, then
    /// saved to <paramref name="file"/>; null (logged) when the bake fails, failed before this
    /// session, or the player quits or backs out meanwhile.</summary>
    private IrradianceVolume? BakeIrradiance(Level map, string file)
    {
        if (failedBakes.Contains(file))
        {
            Log.Info("Render", $"Baked light on MAP.{map.Number:D3}: its bake failed this session; the even ambient");
            return null;
        }

        var rateFile = Path.Combine(SettingsStore.CacheFolder, IrradianceFiles.Folder, RateFile);
        if (!bakeRateRead)
        {
            IrradianceJob.LoadRate(rateFile);
            bakeRateRead = true;
        }

        var clock = Stopwatch.StartNew();
        var job = IrradianceJob.Start(() => IrradianceScene.Build(data, map.Block, map.Map, map.Number));
        if (WaitForBake(job) == BakeWait.Abandoned)
        {
            // The worker finishes unseen (a background thread); the next load of the map bakes again.
            Log.Info("Render", $"Baked light on MAP.{map.Number:D3}: left after {clock.Elapsed.TotalSeconds:0.0} s; the even ambient");
            return null;
        }

        if (job.Result is not { } volume)
        {
            failedBakes.Add(file);
            Log.Warn("Render", $"Baked light on MAP.{map.Number:D3}: the bake failed ({job.Failure}); the even ambient");
            return null;
        }

        DrawLighting(1f);
        LogLoading($"MAP.{map.Number:D3}'s light bake");
        IrradianceJob.SaveRate(rateFile, IrradianceJob.Rate);
        var saved = IrradianceFiles.Write(volume, file) ? $"{new FileInfo(file).Length / 1024} KB saved" : "not saved";
        Log.Info("Render", string.Create(CultureInfo.InvariantCulture,
            $"Baked light on MAP.{map.Number:D3}: {volume.SizeX}x{volume.SizeY}x{volume.SizeZ} probes every {volume.Spacing:0} u, {job.Input!.Triangles} triangles, {job.Input.Lamps.Length} lamps, {IrradianceJob.Threads} threads; input {job.InputTime.TotalSeconds:0.00} s, bake {job.BakeTime.TotalSeconds:0.00} s, {clock.Elapsed.TotalSeconds:0.00} s in all; {saved}"));
        return volume;
    }

    /// <summary>The bake's progress shown until it is done; the window's events read meanwhile: a closed
    /// window (quit) or Esc / Android's back abandons it.</summary>
    private BakeWait WaitForBake(IrradianceJob job)
    {
        while (!job.Wait(LoadingFrameGap))
        {
            if (!window.PumpEvents(input))
            {
                quitting = true;
                return BakeWait.Abandoned;
            }

            if (input.WasPressed(pause) || input.WasPressed(MenuKey.Back))
            {
                return BakeWait.Abandoned;
            }

            DrawLighting(job.Progress);
        }

        return BakeWait.Done;
    }

    /// <summary>The bake's loading frame, e.g. "PREPARING LIGHTING 40%", its bar a slice of the level's
    /// (<see cref="LightingBar"/>). None behind a seamless swap (the last game frame stays).</summary>
    private void DrawLighting(float progress)
    {
        if (loadScreen == LoadScreen.Hidden)
        {
            return;
        }

        DrawLoading(string.Create(CultureInfo.InvariantCulture, $"PREPARING LIGHTING {(int)(progress * 100f)}%"), progress * LightingBar);
    }
}
