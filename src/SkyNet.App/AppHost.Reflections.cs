using System.Diagnostics;
using System.Numerics;
using RetroEng.Diagnostics;
using RetroEng.Render;
using SkyNet.Game;
using SkyNet.Game.Enhanced;
using SkyNet.Game.Menu;

// The ENHANCED look's reflection probes at a map's load: read from the user folder's cache, else
// captured (one frame a probe: the static level and its lamps, seen from the probe all around) and saved.
//
//   EnterLevel ─► LoadReflections ─► cache hit ─────────────────────────────► level.ReflectionProbes ─► Lighting
//                                  └ miss ─► ReflectionPlacement ─► ProbeFrame × n ─► save ─┘
internal sealed partial class AppHost
{
    // The capture frame's level state and lamps (the game frame's own are the frame build's).
    private readonly LevelFrame probeFrame = new();
    private readonly List<PointLight> probeLights = [];

    // How long a load waits for main to draw one capture frame (done never fires on an unlit frame).
    private static readonly TimeSpan ProbeWait = TimeSpan.FromSeconds(5);

    /// <summary>The map's probes in the ENHANCED look (none in the original); the lighting takes them.</summary>
    private void LoadReflections(Level map)
    {
        map.ReflectionProbes = null;
        if (renderer.Lighting == null)
        {
            return;
        }

        var clock = Stopwatch.StartNew();
        var file = ProbeFile(map.Number);
        var probes = file != null ? ReflectionFiles.Read(file) : null;
        var read = probes != null;
        probes ??= CaptureProbes(map);
        if (probes == null)
        {
            Log.Info("Render", $"Reflections on MAP.{map.Number:D3}: none");
            return;
        }

        var captured = clock.Elapsed.TotalMilliseconds;
        if (!read && file != null)
        {
            ReflectionFiles.Write(file, probes);
        }

        var size = file != null && File.Exists(file) ? new FileInfo(file).Length / 1024 : 0;
        Log.Info("Render", $"Reflections on MAP.{map.Number:D3}: {probes.Count} probes {(read ? "read" : "captured")} in {captured:0} ms, " +
            $"{size} KB ({(file != null ? Path.GetFileName(file) : "not cached")})");
        map.ReflectionProbes = probes;
        renderer.Lighting = LookLighting.For(map, settings);
    }

    /// <summary>The map's probe file in the user folder's cache; null when the data has no fingerprint.</summary>
    private string? ProbeFile(int map)
    {
        if (dataPrint is not { } print || print.Dir != data.Dir)
        {
            return null;
        }

        var mods = SkyNet.Game.Mods.ModContent.Files?.Mounts.Select(m => m.Name) ?? [];
        var look = ReflectionFiles.LookOf(mods, settings.Detail.ToString(), settings.Ambient, settings.Shadows.ToString());
        return ReflectionFiles.PathOf(SettingsStore.CacheFolder, data.Game, print.Print, look, map);
    }

    /// <summary>The map's probes captured, one frame each; null when it has no floor or a capture did not come back.</summary>
    private List<ReflectionProbe>? CaptureProbes(Level map)
    {
        var places = ReflectionPlacement.For(map.Block, map.Outdoor, map.Far);
        if (places.Count == 0)
        {
            return null;
        }

        var probes = new ReflectionProbe?[places.Count];
        for (var i = 0; i < places.Count; i++)
        {
            // A split loop's load only publishes the frame: main draws it later and runs done there
            // (issue #37). Wait for it, so the next capture does not reuse probeFrame too early.
            // Not disposed: a late done after a timeout still sets it.
            var slot = i;
            var shown = new ManualResetEventSlim();
            ProbeFrame(map, places[i], probe =>
            {
                probes[slot] = probe;
                shown.Set();
            });

            if (!shown.Wait(ProbeWait))
            {
                break;
            }
        }

        if (probes.Any(p => p == null))
        {
            Log.Warn("Render", $"Reflections on MAP.{map.Number:D3}: {probes.Count(p => p == null)} of {probes.Length} captures did not come back");
            return null;
        }

        return [.. probes.Select(p => p!)];
    }

    /// <summary>One lit frame of the static level and its lamps around <paramref name="place"/>, the
    /// capture asked for it; the window shows the loading frame meanwhile (the canvas is not captured).</summary>
    private void ProbeFrame(Level map, ProbeCapture place, Action<ReflectionProbe> done)
    {
        var eye = place.Position;
        map.Capture(probeFrame);
        map.Draw(probeFrame, eye, MathF.PI / 2f / place.Size);

        probeLights.Clear();
        effects.Collect(probeLights, map, data, 0f, eye, renderer.Lighting!.ShadowQuality,
            dm != null ? PlayKind.Network : PlayKind.Campaign, session.Behaviour, weapons);
        effects.Apply(renderer, probeLights, eye, map.Far, map.LightBudget);

        // The frame's own view: along +z from the probe (its shadows and lamps' grid are fitted to it).
        var aspect = renderer.AspectRatio;
        var view = Matrix4x4.CreateLookAt(eye, eye + Vector3.UnitZ, Vector3.UnitY);
        var projection = Projection.ReversedInfinite(FieldOfView, aspect, Near);
        Matrix4x4.Invert(view with { M41 = 0f, M42 = 0f, M43 = 0f } * projection, out var clipToDirection);
        var lens = new Lens(view, FieldOfView, aspect, Near, FarPlane);
        renderer.ReversedZ = true;

        PaintLoading("PREPARING THE MAP...", 5f / 6f);
        renderer.CaptureProbe(place, done);
        renderer.Present(new View(view * projection, clipToDirection, eye) { Lens = lens }, map.SkyColour);
    }
}
