using System.Collections.Immutable;
using System.Text.Json;
using RetroEng.Content;
using RetroEng.Diagnostics;
using RetroEng.Editor;
using RetroEng.Json;
using SkyNet.Formats;
using SkyNet.Game.Mods;

namespace SkyNet.Edit;

/// <summary>An outliner toggle of an entity (Unreal's eye and lock).</summary>
public enum EntityFlag { Hidden, Locked }

/// <summary>A toggle's value.</summary>
public enum FlagValue { Off, On }

/// <summary>
/// The outliner's hidden and locked entities per layer: a hidden one is not drawn in the view, a hidden or
/// locked one is not picked there. Editor-only, an undo step each change, saved beside the graph layouts as
/// <c>editor/&lt;layer&gt;.outliner.json</c> (the game does not read it).
/// <code>
///   eye off ─► Hidden ─► the shown level baked without it (Without), skipped by picking
///   lock on ─► Locked ─► drawn, skipped by picking
/// </code>
/// </summary>
public sealed class OutlinerStates(UndoStack history)
{
    public const string FormatName = "skynet-outliner";
    public const int Version = 1;

    private sealed record State(ImmutableSortedSet<int> Hidden, ImmutableSortedSet<int> Locked)
    {
        public static readonly State None = new([], []);

        public bool IsEmpty => Hidden.IsEmpty && Locked.IsEmpty;

        public ImmutableSortedSet<int> Of(EntityFlag flag) => flag == EntityFlag.Hidden ? Hidden : Locked;

        public State With(EntityFlag flag, ImmutableSortedSet<int> keys) => flag == EntityFlag.Hidden ? this with { Hidden = keys } : this with { Locked = keys };
    }

    private sealed class Entry(State state)
    {
        public State Now { get; set; } = state;
        public State Saved { get; set; } = state;
    }

    private readonly Dictionary<string, Entry> _layers = new(StringComparer.Ordinal);

    /// <summary>Raised with the layer's name when its toggles change (a click, an undo).</summary>
    public event Action<string>? Changed;

    /// <summary>The file's path in a mod.</summary>
    public static string PathOf(string layer) => $"editor/{layer}.outliner.json";

    /// <summary>The keys of <paramref name="layer"/> with <paramref name="flag"/> on.</summary>
    public IReadOnlySet<int> Of(string layer, EntityFlag flag) => Get(layer).Now.Of(flag);

    /// <summary>Whether entity <paramref name="key"/> of <paramref name="layer"/> has <paramref name="flag"/> on.</summary>
    public bool Has(string layer, int key, EntityFlag flag) => Get(layer).Now.Of(flag).Contains(key);

    /// <summary>The keys a click in the view goes through: hidden or locked.</summary>
    public IReadOnlySet<int> Unpickable(string layer)
    {
        var s = Get(layer).Now;
        return s.Locked.IsEmpty ? s.Hidden : s.Hidden.Union(s.Locked);
    }

    /// <summary>Sets <paramref name="flag"/> of <paramref name="keys"/>, one undo step (none when nothing changes).</summary>
    public void Set(string layer, IEnumerable<int> keys, EntityFlag flag, FlagValue value)
    {
        var before = Get(layer).Now;
        var now = before.Of(flag);
        var next = value == FlagValue.On ? now.Union(keys) : now.Except(keys);
        if (next.SetEquals(now))
        {
            return;
        }

        var label = (flag, value) switch
        {
            (EntityFlag.Hidden, FlagValue.On) => "Hide",
            (EntityFlag.Hidden, _) => "Show",
            (_, FlagValue.On) => "Lock",
            _ => "Unlock",
        };
        history.Execute(new StateCommand(this, layer, label, before, before.With(flag, next)));
    }

    /// <summary>
    /// The map <paramref name="layer"/> plays without its <paramref name="hidden"/> entities (what the view shows):
    /// an added one's edits left out, a base one deleted. The whole map, logged, when that does not apply.
    /// </summary>
    public static MapFile Without(EditDocument doc, string layer, IReadOnlySet<int> hidden)
    {
        var l = doc.Layer(layer);
        var added = l.Edits.Where(e => e.Op == MapEditOp.Add).Select(e => e.Key).ToHashSet();
        var map = doc.Map(layer);
        var edits = l.Edits.RemoveAll(e => added.Contains(e.Key) && hidden.Contains(e.Key))
            .AddRange(hidden.Where(k => !added.Contains(k) && map.TryGetEntityAt(k, out _)).Select(MapEdit.Delete));
        try
        {
            return new MapLayer(l.Base, edits, l.Names).Apply(doc.MapNumber(layer), doc.BaseMap(layer));
        }
        catch (MapLayerException e)
        {
            Log.Warn("Edit", $"{layer}: the hidden entities stay drawn ({e.Message})");
            return map;
        }
    }

    /// <summary>Reads <paramref name="layer"/>'s file from the mods (none, or one that does not read: nothing toggled, logged).</summary>
    public void Load(VirtualFileSystem? vfs, string layer)
    {
        if (vfs == null || !MapLayer.IsLayerName(layer) || vfs.TryReadAllBytes(PathOf(layer)) is not { } bytes)
        {
            return;
        }

        try
        {
            _layers[layer] = new Entry(Parse(bytes));
        }
        catch (JsonDataException e)
        {
            Log.Warn("Edit", $"{PathOf(layer)}: {e.Message}; nothing hidden or locked");
        }
    }

    /// <summary>Writes the toggles into mod folder <paramref name="modFolder"/>: the changed layers, or every
    /// non-empty one; a layer toggled back to nothing has its file removed. The paths written.</summary>
    public IReadOnlyList<string> Save(string modFolder, LayoutSave which)
    {
        var written = new List<string>();
        foreach (var (layer, e) in _layers)
        {
            var changed = !ReferenceEquals(e.Now, e.Saved);
            if (!(changed || (which == LayoutSave.NonEmpty && !e.Now.IsEmpty)))
            {
                continue;
            }

            var path = Path.Combine(modFolder, PathOf(layer));
            if (e.Now.IsEmpty)
            {
                File.Delete(path);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path + ".tmp", ToBytes(layer, e.Now));
            File.Move(path + ".tmp", path, overwrite: true);
            written.Add(PathOf(layer));
        }

        return written;
    }

    /// <summary>The toggles were written as they are now.</summary>
    public void MarkSaved()
    {
        foreach (var e in _layers.Values)
        {
            e.Saved = e.Now;
        }
    }

    private static State Parse(byte[] bytes)
    {
        var root = JsonItem.Parse(bytes).AsObject();
        if (root.Member("format").AsString() != FormatName)
        {
            throw root.Member("format").Error($"expected \"{FormatName}\"");
        }

        if (root.Member("version").AsInt() != Version)
        {
            throw root.Member("version").Error($"version {Version} is the one this build reads");
        }

        ImmutableSortedSet<int> Keys(string name) => [.. root.Member(name).Items.Select(k => k.AsInt(0, int.MaxValue))];
        return new State(Keys("hidden"), Keys("locked"));
    }

    private static byte[] ToBytes(string layer, State s)
    {
        var root = JsonItem.NewObject()
            .Add("format", FormatName)
            .Add("version", Version)
            .Add("layer", layer)
            .Add("hidden", JsonItem.NewArray(s.Hidden, k => k))
            .Add("locked", JsonItem.NewArray(s.Locked, k => k));
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            root.WriteTo(w);
        }

        stream.WriteByte((byte)'\n');
        return stream.ToArray();
    }

    private Entry Get(string layer) => _layers.TryGetValue(layer, out var e) ? e : _layers[layer] = new Entry(State.None);

    private void Put(string layer, State value)
    {
        Get(layer).Now = value;
        Changed?.Invoke(layer);
    }

    private sealed class StateCommand(OutlinerStates owner, string layer, string label, State before, State after) : IUndoCommand
    {
        public string Label { get; } = label;

        public void Do() => owner.Put(layer, after);

        public void Undo() => owner.Put(layer, before);

        public bool TryMerge(IUndoCommand next) => false;
    }
}
