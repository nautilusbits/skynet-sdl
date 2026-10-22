using System.Collections.Concurrent;
using System.Diagnostics;
using RetroEng.Content;
using RetroEng.Diagnostics;
using RetroEng.Jobs;
using RetroEng.Models;
using RetroEng.Platform;
using SkyNet.Data;

namespace SkyNet.Game.Mods;

/// <summary>An HD texture as the enhanced look takes it: premultiplied RGBA8 (or opaque), rows
/// top-down, an animated record's frames stacked downwards.</summary>
public sealed record HdTexture(int Width, int Height, byte[] Rgba)
{
    /// <summary>The relief layer (<c>RRR_n.png</c> and <c>RRR_r.png</c>, docs/hires-textures.md) at
    /// the same size, as <see cref="RetroEng.Render.Renderer.ReplaceTexture"/> takes it: the
    /// tangent-space normal in rgb (stored as 128 + 127 n, so that the sRGB decode gives that byte /
    /// 255 back), the roughness in alpha; null: none.</summary>
    public byte[]? Relief { get; init; }
}

/// <summary>
/// The assets of the enabled user mods (docs/modding.md): HD textures
/// (<c>textures/TEXTURE.NNN/RRR.png</c>), replacement models (<c>models/NAME.glb</c> with
/// <c>models/NAME.json</c>) and table overrides (<c>tables/NAME.json</c>, handed to
/// <see cref="GameTables.Mods"/>), read through a <see cref="VirtualFileSystem"/> of those mods only, so a
/// mod works as a folder or a ZIP and a later mod in the list wins. Nothing is mounted unless the
/// game is started with mods (<see cref="Mount"/>): without one, every lookup is null and the game
/// draws exactly as before. Textures are decoded on worker threads when a level asks for its set
/// (<see cref="Prefetch"/>) and kept for that level; models are read once each.
/// </summary>
public static class ModContent
{
    /// <summary>What is mounted, published as one immutable snapshot (a worker decoding textures or
    /// baking reads one consistent set; a mount or prefetch replaces the whole snapshot). The caches
    /// belong to the snapshot, so a lookup racing a mount never leaves an old mod's entry behind.</summary>
    private sealed record Mounted(VirtualFileSystem? Files, VirtualFileSystem? Tables, IReadOnlyList<string> Enabled,
        IReadOnlyList<string> TablesFrom, bool HasTextures, bool HasModels,
        ConcurrentDictionary<(int Bank, int Record, bool Opaque), HdTexture?> Textures,
        ConcurrentDictionary<string, ModelReplacement?> Models, ConcurrentDictionary<string, RobotReplacement?> Robots);

    private static Mounted _mounted = new(null, null, [], [], false, false, new(), new(StringComparer.OrdinalIgnoreCase),
        new(StringComparer.OrdinalIgnoreCase));

    private static Mounted Now => Volatile.Read(ref _mounted);

    /// <summary>The mods mounted for textures and models, in load order (later wins).</summary>
    public static IReadOnlyList<string> Enabled => Now.Enabled;

    /// <summary>The mods the tables were last mounted from (<see cref="Enabled"/> unless a mount
    /// left the tables as they were).</summary>
    public static IReadOnlyList<string> TablesFrom => Now.TablesFrom;

    /// <summary>Whether any mounted mod has a <c>textures/</c> folder.</summary>
    public static bool HasTextures => Now.HasTextures;

    /// <summary>Whether any mounted mod has a <c>models/</c> folder.</summary>
    public static bool HasModels => Now.HasModels;

    /// <summary>Whether the tables come from a mod (one of <see cref="TablesFrom"/> has a <c>tables/</c> folder).</summary>
    public static bool HasTables => Now.Tables != null;

    /// <summary>The mounted mods' files (null: none mounted).</summary>
    public static VirtualFileSystem? Files => Now.Files;

    /// <summary>Mounts the user mods <paramref name="names"/> of <paramref name="modsDirectory"/>
    /// (folders or ZIPs; later wins), replacing what was mounted. A name not found, or a mod that
    /// does not open (a corrupt ZIP, a folder it may not read), is logged and skipped. With
    /// <paramref name="tables"/> false only the textures and models change: the tables stay those of
    /// the last mount that took them (the MODS screen's changes reach the tables at a game's start,
    /// the textures and models at the next map).</summary>
    public static void Mount(string modsDirectory, IReadOnlyList<string> names, bool tables = true)
    {
        var found = new ContentRoot(modsDirectory).Discover().ToDictionary(m => m.Name, VfsPath.Comparer);
        var vfs = new VirtualFileSystem();
        var mounted = new List<string>();
        for (var i = 0; i < names.Count; i++)
        {
            if (!found.TryGetValue(names[i], out var mod))
            {
                Log.Warn("Mods", $"Mod '{names[i]}' is not in the mods folder");
                continue;
            }

            try
            {
                vfs.Mount(ContentRoot.OpenMod(mod), i, mod.Name);
                mounted.Add(mod.Name);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                Log.Warn("Mods", $"Mod '{mod.Name}' does not open ({e.Message}); skipped");
            }
        }

        if (mounted.Count == 0)
        {
            vfs.Dispose();
            Mount((VirtualFileSystem?)null, [], tables);
            return;
        }

        Mount(vfs, mounted, tables);
    }

    /// <summary>An exception a mod's bad data must not be caught for (the process itself is in
    /// trouble); anything else a mod's file raises is the mod's fault, logged, and the DOS asset stays.</summary>
    internal static bool Critical(Exception e) =>
        e is AccessViolationException or System.Runtime.InteropServices.SEHException or ThreadInterruptedException;

    /// <summary>Mounts a ready VFS as the mods' files (tests and tools); null unmounts. With
    /// <paramref name="tables"/> false the tables stay as they were (see the other overload).
    /// Called on the game thread while no job reads the mods (a level is not being built).</summary>
    public static void Mount(VirtualFileSystem? vfs, IReadOnlyList<string>? names = null, bool tables = true)
    {
        var was = Now;
        var (oldFiles, oldTables) = (was.Files, was.Tables);
        var enabled = names ?? vfs?.Mounts.Select(m => m.Name).Reverse().ToArray() ?? [];
        var hasTextures = vfs?.Enumerate("textures/").Count > 0;
        var hasModels = vfs?.Enumerate("models/").Count > 0;
        var tableFiles = vfs?.Enumerate("tables/").Where(p => p.EndsWith(".json", StringComparison.OrdinalIgnoreCase)).ToArray() ?? [];
        var robotFiles = vfs?.Enumerate(ModRobots.Folder).Count(p => p.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) ?? 0;
        // The tables read through the same mods (the highest that has a table wins); without one the
        // shipped tables stay in effect untouched. Robot files (robots/*.json) are table rows too.
        var (tablesVfs, tablesFrom) = tables ? (tableFiles.Length + robotFiles > 0 ? vfs : null, enabled) : (oldTables, was.TablesFrom);
        Volatile.Write(ref _mounted, new Mounted(vfs, tablesVfs, enabled, tablesFrom, hasTextures, hasModels,
            new(), new(StringComparer.OrdinalIgnoreCase), new(StringComparer.OrdinalIgnoreCase)));
        MapLayers.Install(vfs);
        MapTerrains.Install(vfs);
        ModMaps.Install(vfs);
        ModMissions.Install(vfs);
        if (tables && (tablesVfs != null || (oldTables != null && ReferenceEquals(GameTables.Mods, oldTables))))
        {
            GameTables.Mods = tablesVfs;
        }

        // A file system goes when neither the textures and models nor the tables read it any more.
        foreach (var old in new[] { oldFiles, oldTables }.Distinct())
        {
            if (old != null && !ReferenceEquals(old, vfs) && !ReferenceEquals(old, tablesVfs))
            {
                old.Dispose();
            }
        }

        if (vfs != null)
        {
            Log.Info("Mods", $"Mods {string.Join(", ", enabled)}: {(hasTextures ? "textures" : "no textures")}, {(hasModels ? "models" : "no models")}, " +
                (!tables ? "tables unchanged" : tableFiles.Length > 0 ? $"tables {string.Join(", ", tableFiles.Select(t => Path.GetFileNameWithoutExtension(t)))}" : "no tables") +
                (tables && robotFiles > 0 ? $", robots {robotFiles}" : ""));
            NoteRetired(tableFiles);
        }
        else if (oldFiles != null)
        {
            Log.Info("Mods", tables || tablesVfs == null ? "No mods" : "No mods for textures and models; tables unchanged");
        }
    }

    // Tables the game no longer reads (tables/ai.json: the removed smart robots; tables/hazards.json:
    // the removed burning fires, DOS fires hurt nobody). A mod that has one still loads; the file is
    // ignored, said once per mount.
    private static readonly string[] RetiredTables = ["ai", "hazards"];

    private static void NoteRetired(string[] tableFiles)
    {
        foreach (var name in RetiredTables)
        {
            if (!tableFiles.Any(t => Path.GetFileNameWithoutExtension(t).Equals(name, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            Log.Warn("Mods", $"{GameTables.PathOf(name)} is no longer read; ignored");
        }
    }

    /// <summary>Unmounts every mod, the tables' too.</summary>
    public static void Unmount() => Mount((VirtualFileSystem?)null, [], tables: true);

    /// <summary>DETAIL's HD texture quality (<see cref="HdDetail"/>): what textures and model surfaces
    /// are decoded at. HIGH (full size) until the game says otherwise.</summary>
    public static Detail Detail { get; private set; } = Detail.High;

    /// <summary>Decodes the HD textures at <paramref name="detail"/>'s size from now on (a map's
    /// load, before its build): a change drops the decoded ones; a model surface makes its texture
    /// again on its next use (<see cref="ReplacementSurface.Colour"/>).</summary>
    public static void UseDetail(Detail detail)
    {
        if (detail == Detail)
        {
            return;
        }

        Detail = detail;
        var mounted = Now;
        Interlocked.CompareExchange(ref _mounted, mounted with { Textures = new() }, mounted);
        Log.Info("Mods", $"HD textures at DETAIL {detail}: 1/{HdDetail.Divisor(detail)} of each side");
    }

    /// <summary>A record's HD texture's path in a mod.</summary>
    public static string TexturePath(int bank, int record) => $"textures/TEXTURE.{bank:D3}/{record:D3}.png";

    /// <summary>Decodes the HD textures of <paramref name="wanted"/> on every core and keeps them (and
    /// only them, with any asked for since) for the level being built; the rest are dropped. Each
    /// with its record's width (<see cref="Texture"/>).</summary>
    public static void Prefetch(IEnumerable<(int Bank, int Record, bool Opaque, int Width)> wanted)
    {
        var mounted = Now;
        if (mounted.Files is not { } vfs || !mounted.HasTextures)
        {
            return;
        }

        var clock = Stopwatch.StartNew();
        var old = mounted.Textures;
        var next = new ConcurrentDictionary<(int, int, bool), HdTexture?>();
        var todo = new List<(int Bank, int Record, bool Opaque, int Width)>();
        foreach (var want in wanted.Distinct())
        {
            var key = (want.Bank, want.Record, want.Opaque);
            if (old.TryGetValue(key, out var kept))
            {
                next[key] = kept;
            }
            else if (vfs.Exists(TexturePath(key.Bank, key.Record)))
            {
                todo.Add(want);
            }
        }

        var detail = Detail;
        JobSystem.Shared.ParallelFor(todo.Count, 1, new ActionFor(i =>
        {
            var t = todo[i];
            next[(t.Bank, t.Record, t.Opaque)] = Decode(vfs, t.Bank, t.Record, t.Opaque, t.Width, detail);
        }));
        // Published only over the snapshot it was made for (a mount meanwhile has its own, empty set).
        Interlocked.CompareExchange(ref _mounted, mounted with { Textures = next }, mounted);
        if (todo.Count > 0)
        {
            Log.Info("Mods", $"{todo.Count} HD textures decoded in {clock.Elapsed.TotalMilliseconds:0} ms ({next.Count - todo.Count} kept)");
        }
    }

    /// <summary>HD textures given to the GPU for good (<see cref="Handed"/>): <see cref="TrimHanded"/> lets them go.</summary>
    private static readonly ConcurrentDictionary<(int Bank, int Record, bool Opaque), byte> _handed = new();

    /// <summary>The renderer has a record's HD texture and needs no copy (HdPixels.Released): the
    /// next <see cref="TrimHanded"/> drops it here too.</summary>
    internal static void Handed(int bank, int record, bool opaque) => _handed[(bank, record, opaque)] = 0;

    /// <summary>A map's load is done: the HD textures handed to the GPU are let go (a later map that
    /// wants one decodes it again in its <see cref="Prefetch"/>).</summary>
    public static void TrimHanded()
    {
        var textures = Now.Textures;
        foreach (var key in _handed.Keys)
        {
            textures.TryRemove(key, out _);
        }

        _handed.Clear();
    }

    /// <summary>A record's HD texture (decoded now when no <see cref="Prefetch"/> had it); null: none.
    /// Shrunk by <see cref="Detail"/> when <paramref name="width"/>, the record's width (an animated
    /// record's widest frame), is given (0: full size).</summary>
    public static HdTexture? Texture(int bank, int record, bool opaque, int width = 0)
    {
        var mounted = Now;
        if (mounted.Files is not { } vfs || !mounted.HasTextures)
        {
            return null;
        }

        var detail = Detail;
        return mounted.Textures.GetOrAdd((bank, record, opaque), k => Decode(vfs, k.Bank, k.Record, k.Opaque, width, detail));
    }

    /// <summary>The PNG decoded, made premultiplied (or opaque: alpha ignored, as an opaque face
    /// shows the stored RGB where the record has index 0), shrunk by <paramref name="detail"/> while
    /// still a whole multiple of the record's <paramref name="width"/> (<see cref="HdDetail.Factor"/>).</summary>
    private static HdTexture? Decode(VirtualFileSystem vfs, int bank, int record, bool opaque, int width, Detail detail)
    {
        var path = TexturePath(bank, record);
        if (vfs.TryReadAllBytes(path) is not { } bytes)
        {
            return null;
        }

        if (PngImage.Decode(bytes) is not { } image)
        {
            Log.Warn("Textures", $"{path}: not a PNG SDL can read");
            return null;
        }

        var (w, h, rgba) = image;
        var f = ShrinkBy(w, h, width, detail);
        (w, h, rgba) = HdDetail.Shrink(image, f, opaque ? Texels.Opaque : Texels.Colour);
        Premultiply(rgba, opaque);
        return new HdTexture(w, h, rgba) { Relief = Relief(vfs, bank, record, w * f, h * f, f) };
    }

    /// <summary>The divisor of a <paramref name="w"/> x <paramref name="h"/> HD texture at
    /// <paramref name="detail"/> over a record <paramref name="width"/> wide (0 or no whole scale: 1).</summary>
    private static int ShrinkBy(int w, int h, int width, Detail detail)
    {
        if (width <= 0 || w % width != 0)
        {
            return 1;
        }

        var f = HdDetail.Factor(w / width, detail);
        return h % f == 0 ? f : 1;
    }

    /// <summary>A record's relief maps' path in a mod (<see cref="HiresTextures.WriteRelief"/>):
    /// <c>RRR_n.png</c>, <c>RRR_r.png</c>.</summary>
    public static string ReliefPath(int bank, int record, string suffix) => $"textures/TEXTURE.{bank:D3}/{record:D3}{suffix}.png";

    /// <summary>The HD texture's relief layer (<see cref="HdTexture.Relief"/>) from its normal and
    /// roughness maps, when the mod has both at the texture's size (<paramref name="width"/> x
    /// <paramref name="height"/> in the file), shrunk by <paramref name="divisor"/> as the colour was.</summary>
    private static byte[]? Relief(VirtualFileSystem vfs, int bank, int record, int width, int height, int divisor)
    {
        if (vfs.TryReadAllBytes(ReliefPath(bank, record, HiresTextures.NormalSuffix)) is not { } nb
            || vfs.TryReadAllBytes(ReliefPath(bank, record, HiresTextures.RoughnessSuffix)) is not { } rb)
        {
            return null;
        }

        if (PngImage.Decode(nb) is not { } normal || PngImage.Decode(rb) is not { } rough
            || (normal.Width, normal.Height) != (width, height) || (rough.Width, rough.Height) != (width, height))
        {
            Log.Warn("Textures", $"{TexturePath(bank, record)}: its relief maps do not read or do not match its size");
            return null;
        }

        var relief = HdDetail.Shrink((width, height, normal.Rgba), divisor, Texels.Normal).Rgba;
        var roughness = HdDetail.Shrink((width, height, rough.Rgba), divisor, Texels.Data).Rgba;
        var encode = SrgbEncode;
        for (var i = 0; i + 3 < relief.Length; i += 4)
        {
            relief[i] = encode[relief[i]];
            relief[i + 1] = encode[relief[i + 1]];
            relief[i + 2] = encode[relief[i + 2]];
            relief[i + 3] = roughness[i];
        }

        return relief;
    }

    /// <summary>A byte stored so the colour texture's sRGB decode gives it back (byte / 255) as read.</summary>
    private static readonly byte[] SrgbEncode = [.. Enumerable.Range(0, 256).Select(v => RetroEng.Render.Srgb.FromLinear(v / 255f))];

    /// <summary>Straight RGBA made premultiplied in place, or opaque (alpha 255).</summary>
    public static void Premultiply(Span<byte> rgba, bool opaque)
    {
        for (var i = 0; i + 3 < rgba.Length; i += 4)
        {
            if (opaque)
            {
                rgba[i + 3] = 255;
                continue;
            }

            var a = rgba[i + 3];
            rgba[i] = (byte)((rgba[i] * a + 127) / 255);
            rgba[i + 1] = (byte)((rgba[i + 1] * a + 127) / 255);
            rgba[i + 2] = (byte)((rgba[i + 2] * a + 127) / 255);
        }
    }

    /// <summary>The replacement for the DOS model <paramref name="name"/> (without <c>.3D</c>):
    /// <c>models/NAME.glb</c> (or <c>.gltf</c>) and its sidecar; null when no mod has one or it does
    /// not read (logged once).</summary>
    public static ModelReplacement? Model(string name)
    {
        var mounted = Now;
        if (mounted.Files is not { } vfs || !mounted.HasModels || string.IsNullOrEmpty(name))
        {
            return null;
        }

        return mounted.Models.GetOrAdd(name, n => Timed("glb read", () => ModelReplacement.Load(vfs, n)));
    }

    /// <summary>The whole-robot replacement of a robot built on the DOS model <paramref name="name"/>
    /// (without <c>.3D</c>): <c>models/NAME.robot.glb</c>; null when no mod has one or it does not read
    /// (logged once).</summary>
    public static RobotReplacement? Robot(string name)
    {
        var mounted = Now;
        if (mounted.Files is not { } vfs || !mounted.HasModels || string.IsNullOrEmpty(name))
        {
            return null;
        }

        return mounted.Robots.GetOrAdd(name, n => Timed("robot glb read", () => RobotReplacement.Load(vfs, n)));
    }

    /// <summary>A read on first use, timed for <see cref="DevTools.LoadHitches"/>.</summary>
    private static T Timed<T>(string what, Func<T> read)
    {
        var started = Stopwatch.GetTimestamp();
        var value = read();
        DevTools.LoadHitches.Since(what, started);
        return value;
    }
}
