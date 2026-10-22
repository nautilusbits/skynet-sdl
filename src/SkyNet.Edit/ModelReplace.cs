using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using RetroEng.Editor;
using RetroEng.Json;
using RetroEng.Models;
using SkyNet.Game.Mods;

namespace SkyNet.Edit;

/// <summary>What a replacement stands in for: a DOS model (prop or robot segment) or a whole robot.</summary>
public enum ModelTarget
{
    /// <summary><c>models/NAME.glb</c> + its sidecar <c>models/NAME.json</c> (<see cref="ModelReplacement"/>).</summary>
    Prop,
    /// <summary><c>models/NAME.robot.glb</c>, no sidecar (<see cref="RobotReplacement"/>).</summary>
    Robot,
}

/// <summary>
/// "Replace with 3D model..." and "Remove replacement" without UI (docs/modding.md, Models): the editor's one
/// place writing a mod's model replacements, used by the Assets viewer and the level's Details panel.
/// Every write is one undo step on the history given; <c>changed</c> runs after each do and undo (the
/// app mounts the save mod again so the game's loaders read the files).
/// <code>
///   prop   X.glb ─► mod/models/NAME.glb + mod/models/NAME.json (defaults; kept when there)
///   robot  X.glb ─► mod/models/NAME.robot.glb
///   sprite X.glb ─► mod/models/sprites/X.glb + sprite_models row (Tables grid; Tables > Save writes it)
/// </code>
/// </summary>
public static class ModelReplace
{
    private const string Folder = "models/", GlbExtension = ".glb", SidecarExtension = ".json";

    /// <summary>"BOX.3D" or "BOX" ─► "BOX".</summary>
    public static string NameOf(string model) =>
        model.EndsWith(".3D", StringComparison.OrdinalIgnoreCase) ? model[..^3] : model;

    /// <summary>The replacement's model file in a mod: "models/BOX.glb", "models/T800PST.robot.glb".</summary>
    public static string GlbPath(string model, ModelTarget target) =>
        Folder + NameOf(model) + (target == ModelTarget.Robot ? RobotReplacement.Suffix : "") + GlbExtension;

    /// <summary>A prop replacement's sidecar in a mod: "models/BOX.json".</summary>
    public static string SidecarPath(string model) => Folder + NameOf(model) + SidecarExtension;

    /// <summary>The mod files of a replacement (model file first).</summary>
    public static IReadOnlyList<string> Paths(string model, ModelTarget target) =>
        target == ModelTarget.Prop ? [GlbPath(model, target), SidecarPath(model)] : [GlbPath(model, target)];

    /// <summary>Whether the mod folder holds the replacement's model file.</summary>
    public static bool Has(string modFolder, string model, ModelTarget target) => File.Exists(OnDisk(modFolder, GlbPath(model, target)));

    /// <summary>A new sidecar: fit to the DOS height, base on base; every frame of an animated model.</summary>
    public static ModelSidecar DefaultSidecar(int dosFrames) =>
        dosFrames > 1 ? ModelSidecar.Default with { Frames = [], AllFrames = true } : ModelSidecar.Default;

    /// <summary>Copies <paramref name="glb"/> in as <paramref name="model"/>'s replacement (a prop also gets a
    /// default sidecar when the mod has none) as one undo step. Throws <see cref="InvalidDataException"/> when
    /// it is not a .glb the game reads.</summary>
    public static string Replace(UndoStack history, string modFolder, string model, ModelTarget target, string glb,
        int dosFrames = 1, Action? changed = null)
    {
        var bytes = Checked(glb, target);
        var files = new List<(string, byte[]?)> { (GlbPath(model, target), bytes) };
        var sidecar = OnDisk(modFolder, SidecarPath(model));
        if (target == ModelTarget.Prop && !File.Exists(sidecar))
        {
            files.Add((SidecarPath(model), SidecarJson(DefaultSidecar(dosFrames))));
        }

        var label = $"Replace {(target == ModelTarget.Robot ? "robot" : "model")} {NameOf(model)}";
        history.Execute(FilesEdit.Of(label, modFolder, files, changed));
        return $"{NameOf(model)} -> {string.Join(" + ", files.Select(f => f.Item1))}";
    }

    /// <summary>Deletes the mod's replacement files as one undo step; "nothing" when it has none.</summary>
    public static string Remove(UndoStack history, string modFolder, string model, ModelTarget target, Action? changed = null)
    {
        var there = Paths(model, target).Where(p => File.Exists(OnDisk(modFolder, p))).ToList();
        if (there.Count == 0)
        {
            return $"{NameOf(model)}: the mod has no replacement";
        }

        var label = $"Remove replacement of {NameOf(model)}";
        history.Execute(FilesEdit.Of(label, modFolder, there.Select(p => (p, (byte[]?)null)), changed));
        return $"{NameOf(model)}: removed {string.Join(", ", there)}";
    }

    /// <summary>The mod's own sidecar of <paramref name="model"/> (defaults when only the model is there); null: no replacement.</summary>
    public static ModelSidecar? ReadSidecar(string modFolder, string model)
    {
        if (!Has(modFolder, model, ModelTarget.Prop))
        {
            return null;
        }

        var path = OnDisk(modFolder, SidecarPath(model));
        return File.Exists(path) ? ModelSidecar.Parse(File.ReadAllBytes(path)) : ModelSidecar.Default;
    }

    /// <summary>Writes <paramref name="sidecar"/> as the mod's <c>models/NAME.json</c> (its comment, source and licence
    /// kept) as one undo step; checked as the game reads it first.</summary>
    public static string SetSidecar(UndoStack history, string modFolder, string model, ModelSidecar sidecar, Action? changed = null)
    {
        var path = OnDisk(modFolder, SidecarPath(model));
        var bytes = SidecarJson(sidecar, File.Exists(path) ? File.ReadAllBytes(path) : null);
        ModelSidecar.Parse(bytes);
        history.Execute(FilesEdit.Of($"Edit {SidecarPath(model)}", modFolder, [(SidecarPath(model), bytes)], changed));
        return $"{SidecarPath(model)} written";
    }

    /// <summary>A sprite drawn as <paramref name="glb"/>: copied into models/sprites/, its <c>sprite_models</c> row
    /// pointed at it (one grid undo step; Tables > Save writes it). Returns what was said and the model's file name.</summary>
    public static (string Said, string Model) Sprite(TableGrid grid, UndoStack history, string modFolder, string sprite, string glb)
    {
        var model = SpriteModelEdit.CopyIn(glb, modFolder);
        return (SpriteModelEdit.SetRow(grid, history, sprite, model), model);
    }

    /// <summary>The sprite's <c>sprite_models</c> row back to the base (a billboard again unless a mod under maps it).</summary>
    public static string RemoveSprite(TableGrid grid, UndoStack history, string sprite) => grid.RevertRow(history, sprite);

    // ---- the sidecar's text ----

    /// <summary>A sidecar as JSON the game reads (<see cref="ModelSidecar.Parse"/>); the free-text keys of
    /// <paramref name="existing"/> (comment, source, licence) kept.</summary>
    public static byte[] SidecarJson(ModelSidecar s, byte[]? existing = null)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            Vector(w, "rotation", s.Rotation);
            w.WriteString("fit", s.Fit.ToString().ToLowerInvariant());
            w.WriteNumber("scale", s.Scale);
            w.WriteString("align", s.Align switch { ModelAlign.Centre => "centre", ModelAlign.Origin => "origin", _ => "base" });
            Vector(w, "offset", s.Offset);
            Frames(w, "frames", s.Frames, s.AllFrames);
            if (s.FrameModels.Count > 0)
            {
                w.WriteStartArray("frameModels");
                foreach (var f in s.FrameModels)
                {
                    w.WriteStartObject();
                    w.WriteString("model", f.Model);
                    Frames(w, "frames", f.Frames, f.All);
                    w.WriteEndObject();
                }

                w.WriteEndArray();
            }

            foreach (var kept in FreeText(existing))
            {
                w.WritePropertyName(kept.Key);
                kept.Value.WriteTo(w);
            }

            w.WriteEndObject();
        }

        stream.WriteByte((byte)'\n');
        return stream.ToArray();
    }

    /// <summary>A frame list as typed: "all", or numbers and ranges ("0, 2-5").</summary>
    public static string FramesText(int[] frames, bool all) => all ? "all" : string.Join(", ", Runs(frames).Select(r => r.Item1));

    /// <summary>Reads <see cref="FramesText"/>'s form back, as the game would; <see cref="FormatException"/> when it does not.</summary>
    public static (int[] Frames, bool All) ParseFrames(string text)
    {
        var word = text.Trim();
        string json;
        if (word.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            json = "\"all\"";
        }
        else
        {
            var items = word.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(i => i.Length > 0 && i.All(c => char.IsAsciiDigit(c) || c == '-') && i.Contains('-', StringComparison.Ordinal) ? $"\"{i}\""
                    : int.TryParse(i, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n.ToString(CultureInfo.InvariantCulture)
                    : throw new FormatException($"frames '{i}' (numbers and \"first-last\" ranges, or \"all\")"));
            json = "[" + string.Join(",", items) + "]";
        }

        var s = ModelSidecar.Parse(Encoding.UTF8.GetBytes("{\"frames\": " + json + "}"));
        return (s.Frames, s.AllFrames);
    }

    private static void Vector(Utf8JsonWriter w, string name, Vector3 v)
    {
        w.WriteStartArray(name);
        w.WriteNumberValue(v.X);
        w.WriteNumberValue(v.Y);
        w.WriteNumberValue(v.Z);
        w.WriteEndArray();
    }

    // "all", else the frames: runs of three or more as "first-last" ranges.
    private static void Frames(Utf8JsonWriter w, string name, int[] frames, bool all)
    {
        if (all)
        {
            w.WriteString(name, "all");
            return;
        }

        w.WriteStartArray(name);
        foreach (var (text, range) in Runs(frames))
        {
            if (range)
            {
                w.WriteStringValue(text);
                continue;
            }

            w.WriteNumberValue(int.Parse(text, CultureInfo.InvariantCulture));
        }

        w.WriteEndArray();
    }

    // Sorted frames as runs: [0, 2, 3, 4] ─► "0", "2-4" (a range at most MaxFrameRange long, as the game takes).
    private static IEnumerable<(string, bool)> Runs(int[] frames)
    {
        var sorted = frames.Distinct().Order().ToArray();
        for (var i = 0; i < sorted.Length;)
        {
            var end = i;
            while (end + 1 < sorted.Length && sorted[end + 1] == sorted[end] + 1 && end + 1 - i < ModelSidecar.MaxFrameRange)
            {
                end++;
            }

            if (end - i >= 2)
            {
                yield return (string.Create(CultureInfo.InvariantCulture, $"{sorted[i]}-{sorted[end]}"), true);
                i = end + 1;
                continue;
            }

            yield return (sorted[i].ToString(CultureInfo.InvariantCulture), false);
            i++;
        }
    }

    // The keys the game passes over, from a sidecar already there.
    private static IEnumerable<KeyValuePair<string, JsonItem>> FreeText(byte[]? existing)
    {
        if (existing == null)
        {
            return [];
        }

        try
        {
            return JsonItem.Parse(existing, lenient: true).AsObject().Members
                .Where(m => m.Key is "comment" or "source" or "licence" or "license").ToArray();
        }
        catch (JsonDataException)
        {
            return [];
        }
    }

    // The picked file, read as the game reads it.
    private static byte[] Checked(string glb, ModelTarget target)
    {
        if (!string.Equals(Path.GetExtension(glb), GlbExtension, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"{Path.GetFileName(glb)} is not a .glb");
        }

        var bytes = File.ReadAllBytes(glb);
        try
        {
            if (target == ModelTarget.Robot)
            {
                Gltf.LoadAnimated(bytes);
            }
            else
            {
                Gltf.Load(bytes);
            }
        }
        catch (Exception e) when (e is not (AccessViolationException or OutOfMemoryException or ThreadInterruptedException))
        {
            throw new InvalidDataException($"{Path.GetFileName(glb)} does not read ({e.Message})", e);
        }

        return bytes;
    }

    private static string OnDisk(string modFolder, string path) => Path.Combine(modFolder, path.Replace('/', Path.DirectorySeparatorChar));

    // Files set (or deleted: null) together, put back together.
    private sealed class FilesEdit(string label, IReadOnlyList<(string Path, byte[]? Before, byte[]? After)> files, Action? changed) : IUndoCommand
    {
        public static FilesEdit Of(string label, string modFolder, IEnumerable<(string Path, byte[]? After)> files, Action? changed) =>
            new(label, [.. files.Select(f => (OnDisk(modFolder, f.Path), f.After)).Select(f => (f.Item1, File.Exists(f.Item1) ? File.ReadAllBytes(f.Item1) : null, f.After))], changed);

        public string Label => label;

        public void Do() => Write(f => f.After);

        public void Undo() => Write(f => f.Before);

        public bool TryMerge(IUndoCommand next) => false;

        private void Write(Func<(string Path, byte[]? Before, byte[]? After), byte[]?> pick)
        {
            foreach (var f in files)
            {
                if (pick(f) is not { } bytes)
                {
                    File.Delete(f.Path);
                    continue;
                }

                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(f.Path)!);
                File.WriteAllBytes(f.Path, bytes);
            }

            changed?.Invoke();
        }
    }
}
