using System.Diagnostics;
using RetroEng.Diagnostics;
using RetroEng.Physics;

namespace SkyNet.Game.Navigation;

/// <summary>
/// Baked meshes kept in the exe's cache folder: a bake slower than <see cref="CacheAfterMs"/> is
/// saved and loaded next time unless the collision changed (map, mods) or the bake's settings did.
/// <code>
///   cache/nav/&lt;name&gt;.nav ─ fresh? ─► load
///                         └ stale / missing ─► bake ─► save (when slow)
/// </code>
/// </summary>
internal static class NavCache
{
    private const long CacheAfterMs = 300;
    private const string Folder = "nav", Extension = ".nav", Zone = "Nav";

    private static readonly string CacheDir = Path.Combine(AppContext.BaseDirectory, "cache", Folder);

    /// <summary>The mesh of <paramref name="world"/> for <paramref name="agent"/> at <paramref name="cells"/>:
    /// cached as <paramref name="name"/> (null: never cached).</summary>
    internal static NavMesh Get(string? name, CollisionWorld world, NavAgent agent, NavBuild cells)
    {
        var path = name != null ? Path.Combine(CacheDir, name + Extension) : null;
        if (path != null && Cached(path, world, agent, cells) is { } cached)
        {
            Log.Info(Zone, $"{name} from {Path.GetFileName(path)}");
            return cached;
        }

        var clock = Stopwatch.StartNew();
        var mesh = NavMesh.Build(world, agent, cells);
        var ms = clock.ElapsedMilliseconds;
        Log.Info(Zone, $"{name ?? "mesh"} baked in {ms} ms");
        if (path != null && ms > CacheAfterMs)
        {
            Save(path, mesh);
        }

        return mesh;
    }

    private static NavMesh? Cached(string path, CollisionWorld world, NavAgent agent, NavBuild cells)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using var file = File.OpenRead(path);
            var mesh = NavMesh.Load(file);
            return mesh != null && mesh.IsFor(world, agent, cells) ? mesh : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or EndOfStreamException)
        {
            Log.Warn(Zone, $"{Path.GetFileName(path)} unreadable ({e.Message}): baked again");
            return null;
        }
    }

    private static void Save(string path, NavMesh mesh)
    {
        try
        {
            Directory.CreateDirectory(CacheDir);
            using var file = File.Create(path);
            mesh.Save(file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warn(Zone, $"{Path.GetFileName(path)} not saved ({e.Message})");
        }
    }
}
