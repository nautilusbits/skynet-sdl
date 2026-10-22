using RetroEng.Content;
using RetroEng.Diagnostics;
using RetroEng.Json;
using SkyNet.Formats;

namespace SkyNet.Game.Mods;

/// <summary>
/// The materials in effect (<see cref="MaterialTable"/>): the game's shipped table
/// (data/materials/skynet.json or futureshock.json, embedded), then each mounted mod's
/// <c>materials.json</c> in load order, a later one winning key by key. A mod's file that does not
/// read is logged under [Mods] and left out.
/// <code>
///   shipped ─► Over(mod 1) ─► Over(mod 2) ... ─► Resolve(bank, record) ─► TextureStore ─► Renderer.SetSurface
/// </code>
/// </summary>
public static class Materials
{
    // The stack and what it was made from, as one reference: read from any thread.
    private sealed record Loaded(MaterialTable Table, VirtualFileSystem? From, GameKind Game, int Version);

    private static Loaded? _box;
    private static int _version;

    /// <summary>The table for <paramref name="game"/> over the mounted mods (<see cref="ModContent.Files"/>).</summary>
    public static MaterialTable Current(GameKind game)
    {
        var vfs = ModContent.Files;
        var version = Volatile.Read(ref _version);
        var cached = Volatile.Read(ref _box);
        if (cached != null && ReferenceEquals(cached.From, vfs) && cached.Game == game && cached.Version == version)
        {
            return cached.Table;
        }

        var table = Stack(Shipped(game), vfs);
        Volatile.Write(ref _box, new Loaded(table, vfs, game, version));
        return table;
    }

    /// <summary>Reads the files again at the next <see cref="Current"/> (a mod's file was written).</summary>
    public static void Invalidate() => Interlocked.Increment(ref _version);

    /// <summary>The game's own table; empty when it ships none.</summary>
    public static MaterialTable Shipped(GameKind game)
    {
        if (ShippedResource(game) is not { } name)
        {
            return MaterialTable.Empty;
        }

        using var s = typeof(Materials).Assembly.GetManifestResourceStream(name);
        if (s == null)
        {
            return MaterialTable.Empty;
        }

        using var m = new MemoryStream();
        s.CopyTo(m);
        return MaterialTable.Parse(m.ToArray());
    }

    /// <summary>The game's shipped table's resource name (SkyNet.Game.csproj); null: none.</summary>
    public static string? ShippedResource(GameKind game) => game switch
    {
        GameKind.SkyNet => "materials/skynet.json",
        GameKind.FutureShock => "materials/futureshock.json",
        _ => null,
    };

    /// <summary><paramref name="shipped"/> with each mod's file of <paramref name="mods"/> over it,
    /// the lowest in the load order first.</summary>
    public static MaterialTable Stack(MaterialTable shipped, VirtualFileSystem? mods)
    {
        var table = shipped;
        var path = VfsPath.Normalize(MaterialTable.FileName);
        foreach (var mount in (mods?.Mounts ?? []).Reverse())
        {
            try
            {
                if (mount.Mount.TryReadAllBytes(path) is { } bytes)
                {
                    table = table.Over(MaterialTable.Parse(bytes));
                }
            }
            catch (Exception e) when (e is JsonDataException or System.Text.Json.JsonException or IOException or UnauthorizedAccessException)
            {
                Log.Warn("Mods", $"{mount.Name}:{MaterialTable.FileName}: {e.Message}; left out");
            }
        }

        return table;
    }
}
