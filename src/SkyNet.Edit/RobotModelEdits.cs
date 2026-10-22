using RetroEng.Content;
using RetroEng.Diagnostics;
using RetroEng.Editor;
using RetroEng.Models;

namespace SkyNet.Edit;

/// <summary>A robot's whole-model replacement as the save mod has it, with the unsaved edits.</summary>
public enum RobotModelState
{
    /// <summary>The save mod has none (another mod may).</summary>
    None,
    /// <summary>Its file is in the save mod.</summary>
    Saved,
    /// <summary>A replacement File > Save writes.</summary>
    Unsaved,
    /// <summary>The saved file File > Save deletes (the DOS robot shown).</summary>
    Reverted,
}

/// <summary>
/// "Replace with glTF..." and "Revert to original" of a whole robot (docs/modding.md, Robot template), unsaved:
/// each one undo step held in memory, seen by the game at once through <see cref="Compose"/> (the save mod's
/// mount with the edits over it), written by <see cref="Save"/> (File > Save) as <c>models/NAME.robot.glb</c>.
/// <code>
///   Replace(X.glb | X.gltf) ─► pending[NAME] = glb ─┐            Save ─► mod/models/NAME.robot.glb
///   Revert                  ─► pending[NAME] = none ┼─► Changed ─► Compose ─► ModContent.Mount (preview)
///   Undo / Redo             ─► pending as it was   ─┘
/// </code>
/// A pending value equal to the save mod's file is dropped: nothing to save.
/// </summary>
public sealed class RobotModelEdits
{
    // Model name ─► the file Save leaves (null: none).
    private readonly Dictionary<string, byte[]?> _pending = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The save mod's folder (null: none chosen; nothing can be staged).</summary>
    public string? ModFolder { get; set; }

    /// <summary>Raised after each change of the pending edits (do, undo, save).</summary>
    public event Action? Changed;

    /// <summary>Whether File > Save has something to write.</summary>
    public bool Dirty => _pending.Count > 0;

    /// <summary><paramref name="model"/>'s replacement state in the save mod.</summary>
    public RobotModelState StateOf(string model)
    {
        var name = ModelReplace.NameOf(model);
        if (_pending.TryGetValue(name, out var pending))
        {
            return pending != null ? RobotModelState.Unsaved : RobotModelState.Reverted;
        }

        return OnDisk(name) != null ? RobotModelState.Saved : RobotModelState.None;
    }

    /// <summary>Stages <paramref name="file"/> (.glb, or .gltf packed with its files) as <paramref name="model"/>'s whole
    /// robot, one undo step. <see cref="InvalidDataException"/> when the game would not read it.</summary>
    public string Replace(UndoStack history, string model, string file)
    {
        var folder = Folder();
        var name = ModelReplace.NameOf(model);
        var glb = Checked(file);
        history.Execute(new Stage($"Replace robot {name}", this, name, Effective(name), glb));
        return $"{name} -> {ModelReplace.GlbPath(name, ModelTarget.Robot)} in {Path.GetFileName(folder)} (unsaved: File > Save writes it)";
    }

    /// <summary>Stages the save mod's replacement of <paramref name="model"/> out (the DOS robot), one undo step.</summary>
    public string Revert(UndoStack history, string model)
    {
        Folder();
        var name = ModelReplace.NameOf(model);
        if (Effective(name) is not { } before)
        {
            return $"{name}: the save mod has no replacement";
        }

        history.Execute(new Stage($"Revert robot {name}", this, name, before, null));
        return $"{name}: the original (unsaved: File > Save deletes {ModelReplace.GlbPath(name, ModelTarget.Robot)})";
    }

    /// <summary>Writes the pending edits into the save mod; returns the paths written or deleted.</summary>
    public IReadOnlyList<string> Save()
    {
        var folder = Folder();
        var done = new List<string>();
        foreach (var (name, glb) in _pending.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            var path = ModelReplace.GlbPath(name, ModelTarget.Robot);
            var disk = InFolder(folder, path);
            if (glb == null)
            {
                File.Delete(disk);
                done.Add(path);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(disk)!);
            File.WriteAllBytes(disk, glb);
            done.Add(path);
        }

        _pending.Clear();
        Changed?.Invoke();
        return done;
    }

    /// <summary>The mods <paramref name="enabled"/> of <paramref name="modsDir"/> (later wins) as the game mounts them,
    /// the save mod <paramref name="saveMod"/> with the pending edits over it (on top when it is not among them).</summary>
    public VirtualFileSystem Compose(string modsDir, IReadOnlyList<string> enabled, string saveMod)
    {
        var found = new ContentRoot(modsDir).Discover().ToDictionary(m => m.Name, VfsPath.Comparer);
        var vfs = new VirtualFileSystem();
        var pending = _pending.ToDictionary(p => ModelReplace.GlbPath(p.Key, ModelTarget.Robot), p => p.Value, StringComparer.OrdinalIgnoreCase);
        var overSave = false;
        for (var i = 0; i < enabled.Count; i++)
        {
            if (!found.TryGetValue(enabled[i], out var mod))
            {
                continue;
            }

            IVfsMount mount;
            try
            {
                mount = ContentRoot.OpenMod(mod);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                Log.Warn("Mods", $"Mod '{mod.Name}' does not open ({e.Message}); skipped");
                continue;
            }

            var save = VfsPath.Comparer.Equals(mod.Name, saveMod);
            overSave |= save;
            vfs.Mount(save ? new Over(mount, pending) : mount, i, mod.Name);
        }

        if (!overSave)
        {
            vfs.Mount(new Over(null, pending), enabled.Count, saveMod);
        }

        return vfs;
    }

    // What the robot is now: pending, else the save mod's file.
    private byte[]? Effective(string name) => _pending.TryGetValue(name, out var pending) ? pending : OnDisk(name);

    private byte[]? OnDisk(string name)
    {
        if (ModFolder is not { } folder)
        {
            return null;
        }

        var path = InFolder(folder, ModelReplace.GlbPath(name, ModelTarget.Robot));
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    // The robot's file as it should be after Save; equal to the disk: nothing pending.
    private void Set(string name, byte[]? glb)
    {
        var disk = OnDisk(name);
        var same = glb == null ? disk == null : disk != null && glb.AsSpan().SequenceEqual(disk);
        if (same)
        {
            _pending.Remove(name);
        }
        else
        {
            _pending[name] = glb;
        }

        Changed?.Invoke();
    }

    private string Folder() => ModFolder ?? throw new InvalidOperationException("No mod to write to (File > Save As picks one)");

    private static string InFolder(string folder, string path) => Path.Combine(folder, path.Replace('/', Path.DirectorySeparatorChar));

    // The picked file as one .glb the game reads as a whole robot.
    private static byte[] Checked(string file)
    {
        var extension = Path.GetExtension(file);
        if (!extension.Equals(".glb", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".gltf", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"{Path.GetFileName(file)} is not a .glb or .gltf");
        }

        var glb = GltfPack.Read(file);
        AnimatedModel model;
        try
        {
            model = Gltf.LoadAnimated(glb);
        }
        catch (Exception e) when (e is not (AccessViolationException or OutOfMemoryException or ThreadInterruptedException))
        {
            throw new InvalidDataException($"{Path.GetFileName(file)} does not read ({e.Message})", e);
        }

        // Nothing to draw would make the robot invisible.
        return model.Parts.Count > 0 ? glb : throw new InvalidDataException($"{Path.GetFileName(file)} has no mesh");
    }

    // One staged change: the robot's file before and after.
    private sealed class Stage(string label, RobotModelEdits edits, string name, byte[]? before, byte[]? after) : IUndoCommand
    {
        public string Label => label;

        public void Do() => edits.Set(name, after);

        public void Undo() => edits.Set(name, before);

        public bool TryMerge(IUndoCommand next) => false;
    }

    // The save mod as the game would read it after Save: a pending file served, a reverted one hidden. The edits
    // as they were composed (a change composes again), by path.
    private sealed class Over(IVfsMount? inner, Dictionary<string, byte[]?> pending) : IVfsMount
    {
        public string Source => inner?.Source ?? "unsaved";

        public bool Exists(string path) => pending.TryGetValue(path, out var glb) ? glb != null : inner?.Exists(path) == true;

        public Stream? TryOpen(string path)
        {
            if (!pending.TryGetValue(path, out var glb))
            {
                return inner?.TryOpen(path);
            }

            return glb != null ? new MemoryStream(glb, writable: false) : null;
        }

        public byte[]? TryReadAllBytes(string path) => pending.TryGetValue(path, out var glb) ? glb : inner?.TryReadAllBytes(path);

        public IEnumerable<string> Enumerate(string prefix)
        {
            var added = pending.Where(p => p.Value != null && p.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).Select(p => p.Key);
            return (inner?.Enumerate(prefix) ?? []).Where(p => !pending.ContainsKey(p)).Concat(added);
        }

        public void Dispose() => inner?.Dispose();
    }
}
