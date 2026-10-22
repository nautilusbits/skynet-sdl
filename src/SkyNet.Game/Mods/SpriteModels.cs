using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using RetroEng.Content;
using RetroEng.Diagnostics;
using RetroEng.Models;
using SkyNet.Data;
using SkyNet.Game.Menu;

namespace SkyNet.Game.Mods;

/// <summary>
/// A sprite a mod draws as a glTF model in the enhanced look (<c>tables/sprite_models.json</c>,
/// docs/modding.md#sprite-models): its row and the model read, static (<see cref="Static"/>) or
/// with clips (<see cref="Animated"/>, the first looped). The model stands on the sprite's foot.
/// </summary>
public sealed class SpriteModel
{
    /// <summary>Bobs a second.</summary>
    public const float BobRate = 0.5f;

    public required int Sprite { get; init; }
    public required SpriteModelRow Row { get; init; }
    /// <summary>The model without clips (drawn as a prop's replacement is); null: <see cref="Animated"/>.</summary>
    public ModelReplacement? Static { get; init; }
    /// <summary>The model with clips; null: <see cref="Static"/>.</summary>
    public AnimatedModel? Animated { get; init; }
    /// <summary>Per <see cref="Animated"/> material.</summary>
    public IReadOnlyList<ReplacementSurface> Surfaces { get; init; } = [];
    /// <summary><see cref="Animated"/>'s levels of detail (merged into it); null: <see cref="Static"/>.</summary>
    internal AnimatedLods? Lods { get; init; }
    /// <summary>The model's box at rest, in its own space.</summary>
    public required (Vector3 Min, Vector3 Max) Box { get; init; }

    /// <summary>
    /// The model's space to the world: its bottom centre on <paramref name="foot"/>, sized by the
    /// row's scale (0: to <paramref name="height"/>, the sprite's), turned by yaw + spin · t, lifted
    /// by offsetY + bob · sin. <paramref name="seed"/> (a MAP offset, a drop's id) staggers the turn
    /// and the bob of sprites side by side.
    /// </summary>
    public Matrix4x4 World(Vector3 foot, float height, float seconds, int seed)
    {
        const float Degrees = MathF.PI / 180f;
        var (min, max) = Box;
        var size = max.Y - min.Y;
        var scale = Row.Scale > 0 ? (float)Row.Scale : size > 1e-6f ? height / size : 1f;
        var phase = (uint)seed * 2654435761u / (float)uint.MaxValue;

        var yaw = (float)Row.Yaw + (float)Row.Spin * seconds + (Row.Spin != 0 ? phase * 360f : 0f);
        var lift = (float)Row.OffsetY + (float)Row.Bob * MathF.Sin(MathF.Tau * (seconds * BobRate + phase));
        var bottom = new Vector3((min.X + max.X) / 2f, min.Y, (min.Z + max.Z) / 2f);
        return Matrix4x4.CreateTranslation(-bottom) * Matrix4x4.CreateScale(scale) * Matrix4x4.CreateRotationY(yaw * Degrees)
            * Matrix4x4.CreateTranslation(foot + new Vector3(0f, lift, 0f));
    }
}

/// <summary>
/// The table <c>sprite_models</c> (a mod rule: no shipped file, the rows lie over an empty table)
/// and its models, read from the mounted mods (<see cref="GameTables.Mods"/> for the rows,
/// <see cref="ModContent.Files"/> for <c>models/</c>). Read again when either changes.
/// <code>
///   tables/sprite_models.json  {"sprite": "214/007", "model": "medkit.glb", ...}
///        └─► Drawn(27399, Enhanced) ─► SpriteModel ─► SpriteModelView draws it, no billboard
///            Drawn(27399, Original) ─► null ─► the DOS billboard
/// </code>
/// </summary>
public static class SpriteModels
{
    private const int RecordBits = 7, MaxRecord = 0x7F, MaxBank = 999;

    /// <summary>Where a row's <c>model</c> lies in a mod: apart from <c>models/NAME.glb</c>, which
    /// replaces the DOS model NAME.3D (a sprite model DRONE.glb would replace DRONE.3D too).</summary>
    public const string Folder = "models/sprites/";

    /// <summary><see cref="Folder"/> below <c>models/</c>, as <see cref="ModelReplacement.Load(VirtualFileSystem, string)"/> names it.</summary>
    private const string SubFolder = "sprites/";

    private static readonly string[] Extensions = [".glb", ".gltf"];

    private sealed record Loaded(VirtualFileSystem? Tables, VirtualFileSystem? Files, IReadOnlyDictionary<int, SpriteModel> Models);

    private static Loaded? _loaded;

    /// <summary>The empty table a mod's rows lie over (the editor's base).</summary>
    public static TableFile<ImmutableArray<SpriteModelRow>> Base => new(TableNames.SpriteModels, "", "mod rule", "", "SpriteModels", []);

    /// <summary>The rows of <paramref name="mods"/> over <see cref="Base"/>.</summary>
    public static ImmutableArray<SpriteModelRow> Rows(VirtualFileSystem? mods) =>
        GameTables.Layer(TableNames.SpriteModels, TableSchemas.SpriteModels, Base, mods).Rows;

    /// <summary>"BANK/RECORD" ("214/007") as the sprite index (bank &lt;&lt; 7 | record); false: not one.</summary>
    public static bool TryKey(string text, out int sprite)
    {
        sprite = 0;
        var parts = text.Split('/');
        if (parts.Length != 2
            || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var bank) || bank > MaxBank
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var record) || record > MaxRecord)
        {
            return false;
        }

        sprite = bank << RecordBits | record;
        return true;
    }

    /// <summary>The key of <paramref name="sprite"/>: 27399 → "214/007".</summary>
    public static string KeyOf(int sprite) => $"{sprite >> RecordBits:D3}/{sprite & MaxRecord:D3}";

    /// <summary>The model <paramref name="look"/> draws for <paramref name="sprite"/>; null: its
    /// billboard (always in the original look).</summary>
    public static SpriteModel? Drawn(int sprite, Look look)
    {
        if (look != Look.Enhanced)
        {
            return null;
        }

        return Current().TryGetValue(sprite, out var model) ? model : null;
    }

    private static IReadOnlyDictionary<int, SpriteModel> Current()
    {
        var (tables, files) = (GameTables.Mods, ModContent.Files);
        var cached = Volatile.Read(ref _loaded);
        if (cached != null && ReferenceEquals(cached.Tables, tables) && ReferenceEquals(cached.Files, files))
        {
            return cached.Models;
        }

        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        cached = new Loaded(tables, files, Load(tables, files));
        DevTools.LoadHitches.Since("sprite models read", started);
        Volatile.Write(ref _loaded, cached);
        return cached.Models;
    }

    /// <summary>The rows' models by sprite. A row that does not read (its key, its file) is logged
    /// and left out: that sprite stays a billboard.</summary>
    private static Dictionary<int, SpriteModel> Load(VirtualFileSystem? tables, VirtualFileSystem? files)
    {
        var models = new Dictionary<int, SpriteModel>();
        if (tables == null || files == null)
        {
            return models;
        }

        ImmutableArray<SpriteModelRow> rows;
        try
        {
            rows = Rows(tables);
        }
        catch (InvalidDataException e)
        {
            Log.Warn("Mods", $"{e.Message}; sprites stay billboards");
            return models;
        }

        // The files read on every core, kept in the rows' order (a later row for a sprite wins).
        var read = new SpriteModel?[rows.Length];
        RetroEng.Jobs.JobSystem.Shared.ParallelFor(rows.Length, 1, new RetroEng.Jobs.ActionFor(i =>
            read[i] = TryKey(rows[i].Sprite, out var sprite) ? Read(files, sprite, rows[i]) : null));
        for (var i = 0; i < rows.Length; i++)
        {
            if (!TryKey(rows[i].Sprite, out var sprite))
            {
                Log.Warn("Mods", $"Table {TableNames.SpriteModels}: sprite \"{rows[i].Sprite}\" is not BANK/RECORD; skipped");
                continue;
            }

            if (read[i] is { } model)
            {
                models[sprite] = model;
            }
        }

        if (models.Count > 0)
        {
            Log.Info("Mods", $"{models.Count} sprites drawn as models in the enhanced look");
        }

        return models;
    }

    /// <summary>The row's model file: with clips an animated model, else a static one.</summary>
    private static SpriteModel? Read(VirtualFileSystem files, int sprite, SpriteModelRow row)
    {
        var name = row.Model;
        var extension = Path.GetExtension(name);
        var path = Folder + name;
        if (name.IndexOfAny(['/', '\\']) >= 0 || !Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase)
            || files.TryReadAllBytes(path) is not { } bytes)
        {
            Log.Warn("Mods", $"Table {TableNames.SpriteModels}: {row.Sprite}: no model {path} (a .glb in the mod's {Folder}); skipped");
            return null;
        }

        try
        {
            var animated = Gltf.LoadAnimated(bytes, uri => files.TryReadAllBytes(Folder + uri));
            if (animated.Clips.Count > 0)
            {
                var lods = AnimatedLods.Merge(animated, AnimatedLevels(files, name[..^extension.Length]), LodSettings.Default, path);
                return new SpriteModel
                {
                    Sprite = sprite, Row = row, Animated = lods.Model, Box = RestBox(animated), Lods = lods,
                    Surfaces = [.. animated.Materials.Select(m => ReplacementSurface.Build(path, animated.Images, m))],
                };
            }
        }
        catch (Exception e) when (!ModContent.Critical(e))
        {
            // Read as a static model below, which says what is wrong if that fails too.
            Log.Info("Mods", $"{path}: not read as animated ({e.Message})");
        }

        if (ModelReplacement.Load(files, SubFolder + name[..^extension.Length]) is not { } replacement)
        {
            return null;
        }

        var corners = replacement.Model.Primitives.SelectMany(p => p.Positions).ToArray();
        var box = corners.Length > 0 ? (corners.Aggregate(Vector3.Min), corners.Aggregate(Vector3.Max)) : (Vector3.Zero, Vector3.Zero);
        return new SpriteModel { Sprite = sprite, Row = row, Static = replacement, Box = box };
    }

    /// <summary>An animated sprite model's levels of detail: <c>NAME.lod1.glb</c>... up to the first
    /// missing or broken one.</summary>
    private static List<AnimatedModel> AnimatedLevels(VirtualFileSystem files, string name)
    {
        var lods = new List<AnimatedModel>();
        for (var level = 1; level <= LodSettings.MaxLevels; level++)
        {
            if (files.TryReadAllBytes($"{Folder}{LodSettings.FileName(name, level)}.glb") is not { } bytes)
            {
                break;
            }

            try
            {
                lods.Add(Gltf.LoadAnimated(bytes));
            }
            catch (Exception e) when (!ModContent.Critical(e))
            {
                Log.Warn("Mods", $"{Folder}{LodSettings.FileName(name, level)}.glb: {e.Message}; levels from here not drawn");
                break;
            }
        }

        return lods;
    }

    /// <summary>The animated model's box at rest: each part's box through its joints (as the engine's
    /// AnimatedMesh.Posed measures it).</summary>
    private static (Vector3, Vector3) RestBox(AnimatedModel model)
    {
        var rest = new AnimatedInstance(model);
        var (min, max) = (new Vector3(float.MaxValue), new Vector3(float.MinValue));
        for (var p = 0; p < model.Parts.Count; p++)
        {
            var positions = model.Parts[p].Shapes.Count > 0 ? model.Parts[p].Shapes[0].Positions : [];
            var (first, count) = model.JointsOf(p);
            for (var j = first; j < first + count; j++)
            {
                foreach (var v in positions)
                {
                    var placed = Vector3.Transform(v, rest.Joints[j]);
                    (min, max) = (Vector3.Min(min, placed), Vector3.Max(max, placed));
                }
            }
        }

        return min.X <= max.X ? (min, max) : (Vector3.Zero, Vector3.Zero);
    }
}
