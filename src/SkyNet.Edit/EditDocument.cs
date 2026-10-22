using System.Collections;
using System.Collections.Immutable;
using System.Globalization;
using System.Reflection;
using System.Text;
using RetroEng.Content;
using RetroEng.Diagnostics;
using RetroEng.Editor;
using SkyNet.Formats;
using SkyNet.Game.Campaign;
using SkyNet.Game.Mods;

namespace SkyNet.Edit;

/// <summary>
/// The edited mission's map layers (docs/plans/editor-vr-menus.md, 1.5): each layer a
/// <see cref="MapLayer"/> of edits over its raw DOS map, changed only through reversible commands
/// (set fields, add, delete, edit link) on one <see cref="UndoStack"/>. A command keeps the layer
/// before and after it, so undo and redo give back the very same layer; a set merges into the
/// entity's previous edit when that applies identically, so a layer does not grow with every drag.
/// </summary>
public sealed class EditDocument
{
    private sealed class Entry(int number, MapFile baseMap, MapLayer layer)
    {
        public int Number { get; } = number;
        public MapFile Base { get; } = baseMap;
        public MapLayer Layer { get; set; } = layer;
        public MapLayer Saved { get; set; } = layer;
        public MapFile? Map { get; set; }
        public bool Changed => !ReferenceEquals(Layer, Saved);
    }

    private readonly Dictionary<string, Entry> _layers = new(StringComparer.Ordinal);
    private Dictionary<string, MapLayer>? _group;

    public EditDocument()
    {
        Layouts = new GraphLayouts(History);
        Outliner = new OutlinerStates(History);
    }

    /// <summary>The trigger graphs' node positions (editor-only files, saved with the layers).</summary>
    public GraphLayouts Layouts { get; }

    /// <summary>The outliner's hidden and locked entities (editor-only files, saved with the layers).</summary>
    public OutlinerStates Outliner { get; }

    /// <summary>The undo and redo history; its save point is the document's.</summary>
    public UndoStack History { get; } = new();

    /// <summary>Raised with the layer's name whenever a command (or its undo) changes it.</summary>
    public event Action<string>? LayerChanged;

    /// <summary>Raised whenever a mission command (or its undo) changes <see cref="Mission"/>.</summary>
    public event Action? MissionChanged;

    /// <summary>True when the layers or the mission differ from what was last saved (or loaded).</summary>
    public bool IsDirty => History.IsDirty;

    /// <summary>The mission file as it is now (instances, groups, start, exits, game properties); null: none.</summary>
    public MissionFile? Mission { get; private set; }

    private MissionFile? _savedMission, _loadedMission;

    /// <summary>Where <see cref="Save"/> writes the mission in the mod (<c>missions/MNN.json</c>); null: the
    /// mission is not saved (a deathmatch arena's world is made up by the editor).</summary>
    public string? MissionPath { get; set; }

    /// <summary>True when the mission differs from what was loaded or last saved.</summary>
    public bool MissionChangedSinceSave => !ReferenceEquals(Mission, _savedMission);

    /// <summary>The layer names, in the order they were added.</summary>
    public IEnumerable<string> Layers => _layers.Keys;

    /// <summary>Adds <paramref name="layer"/> over MAP.<paramref name="number"/> (raw, as parsed) with its
    /// existing edits (none: an empty layer). <see cref="MapLayerException"/> when they do not fit the map.</summary>
    public void AddLayer(string layer, int number, MapFile baseMap, MapLayer? edits = null)
    {
        var l = edits ?? new MapLayer($"MAP.{number:D3}", []);
        _ = l.Apply(number, baseMap);
        _layers[layer] = new Entry(number, baseMap, l);
    }

    /// <summary>A mission's layers: every instance's layer over its raw DOS map, with the mods' layer file
    /// when <paramref name="vfs"/> has one (one that does not read or fit starts empty, logged).</summary>
    public static EditDocument Load(GameData data, MissionFile mission, VirtualFileSystem? vfs)
    {
        var doc = new EditDocument { Mission = mission, _savedMission = mission, _loadedMission = mission, MissionPath = MissionFile.PathOf(mission.Number) };
        using var archive = data.OpenMapArchive();
        foreach (var instance in mission.World.Instances)
        {
            doc.LoadLayer(archive, instance, vfs);
        }

        return doc;
    }

    /// <summary>Adds the layer of <paramref name="instance"/> (one added to the mission) when the document
    /// does not have it yet: its raw DOS map with the mods' layer file. False when its map does not load.</summary>
    public bool EnsureLayer(GameData data, RetroEng.World.WorldInstance instance, VirtualFileSystem? vfs)
    {
        if (_layers.ContainsKey(instance.Layer))
        {
            return true;
        }

        using var archive = data.OpenMapArchive();
        LoadLayer(archive, instance, vfs);
        return _layers.ContainsKey(instance.Layer);
    }

    private void LoadLayer(BsaArchive archive, RetroEng.World.WorldInstance instance, VirtualFileSystem? vfs)
    {
        var number = MissionFile.LayerMap(instance);
        if (_layers.ContainsKey(instance.Layer) || number < 0
            || GameData.MapBytes(archive, number) is not { } bytes || MapFile.Parse(bytes) is not { } raw)
        {
            return;
        }

        try
        {
            AddLayer(instance.Layer, number, raw, MapLayers.Find(vfs, instance.Layer));
            Layouts.Load(vfs, instance.Layer);
            Outliner.Load(vfs, instance.Layer);
        }
        catch (MapLayerException e)
        {
            Log.Warn("Edit", $"{MapLayer.PathOf(instance.Layer)}: {e.Message}; editing it from empty");
            AddLayer(instance.Layer, number, raw);
        }
    }

    /// <summary>Makes <paramref name="after"/> (a <see cref="MissionEdits"/> result) the mission, one undo step.</summary>
    public void SetMission(MissionFile after, string label)
    {
        if (Mission is not { } before)
        {
            throw new InvalidOperationException("the document has no mission");
        }

        History.Execute(new MissionCommand(this, label, before, after));
    }

    private void PutMission(MissionFile value)
    {
        Mission = value;
        MissionChanged?.Invoke();
    }

    /// <summary>The layer's edits as they are now.</summary>
    public MapLayer Layer(string layer) => Get(layer).Layer;

    /// <summary>The layer's raw DOS map, without its edits.</summary>
    public MapFile BaseMap(string layer) => Get(layer).Base;

    /// <summary>The layer's map number.</summary>
    public int MapNumber(string layer) => Get(layer).Number;

    /// <summary>The map the layer plays: its edits applied to the raw map (built once per change).</summary>
    public MapFile Map(string layer)
    {
        var e = Get(layer);
        return e.Map ??= e.Layer.Apply(e.Number, e.Base);
    }

    private Entry Get(string layer) =>
        _layers.TryGetValue(layer, out var e) ? e : throw new KeyNotFoundException($"the document has no layer {layer}");

    // ---- the commands ----

    /// <summary>Sets one field of entity <paramref name="key"/>; consecutive sets of the same field merge
    /// into one step until <see cref="EndDrag"/>.</summary>
    public void SetField(string layer, int key, string field, int value) =>
        SetFields(layer, key, ImmutableSortedDictionary<string, int>.Empty.WithComparers(StringComparer.Ordinal).Add(field, value));

    /// <summary>Sets fields (JSON names, <see cref="MapLayer.FieldNames"/>) and optionally the name of
    /// entity <paramref name="key"/>. <see cref="MapLayerException"/> when the key or a value does not fit.</summary>
    public void SetFields(string layer, int key, IReadOnlyDictionary<string, int> fields, string? name = null, string? label = null)
    {
        var f = Fields(fields);
        var before = Layer(layer);
        var after = WithSet(layer, before, key, f, name);
        var merge = name == null ? $"set {key} {string.Join(',', f.Keys)}" : null;
        Run(layer, label ?? (f.Count == 1 && name == null ? $"Set {f.Keys.First()}" : "Set fields"), before, after, merge);
    }

    /// <summary>Changes entity <paramref name="key"/>'s link record (null: field unchanged): its act,
    /// next link, destroy type and parameter.</summary>
    public void EditLink(string layer, int key, int? act = null, int? next = null, int? destroyType = null, int? destroyParam = null)
    {
        var f = new Dictionary<string, int>();
        void Put(string k, int? v)
        {
            if (v is { } x)
            {
                f[k] = x;
            }
        }

        Put("act", act);
        Put("next", next);
        Put("destroyType", destroyType);
        Put("destroyParam", destroyParam);
        if (f.Count > 0)
        {
            SetFields(layer, key, f, label: "Edit link");
        }
    }

    /// <summary>Adds an entity (its fields need a variant or flags) under the next free key at or above
    /// <see cref="MapFile.FirstAddedKey"/>, the key it returns.</summary>
    public int AddEntity(string layer, IReadOnlyDictionary<string, int> fields, string? name = null)
    {
        var f = Fields(fields);
        if (!f.ContainsKey("variant") && !f.ContainsKey("flags"))
        {
            throw new MapLayerException("an add needs a variant (1 mesh, 2 light, 3 sprite)");
        }

        var before = Layer(layer);
        var key = Math.Max(MapFile.FirstAddedKey, Map(layer).Entities.Select(e => e.FileOffset + 1)
            .Concat(before.Edits.Select(e => e.Key + 1)).DefaultIfEmpty(0).Max());
        var after = new MapLayer(before.Base, before.Edits.Add(new MapEdit(MapEditOp.Add, key, f, name)), before.Names);
        Check(layer, after);
        Run(layer, "Add entity", before, after, null);
        return key;
    }

    /// <summary>Deletes entity <paramref name="key"/>: an added one loses its add, a base one gets a delete
    /// in place of its sets.</summary>
    public void DeleteEntity(string layer, int key)
    {
        var before = Layer(layer);
        if (!Map(layer).TryGetEntityAt(key, out _))
        {
            throw new MapLayerException($"delete {key}: the map has no entity under that key");
        }

        var added = before.Edits.Any(e => e.Key == key && e.Op == MapEditOp.Add);
        var edits = added
            ? before.Edits.RemoveAll(e => e.Key == key)
            : before.Edits.RemoveAll(e => e.Key == key && e.Op == MapEditOp.Set && e.Name == null).Add(MapEdit.Delete(key));
        var after = new MapLayer(before.Base, edits, before.Names);
        Check(layer, after);
        Run(layer, "Delete entity", before, after, null);
    }

    /// <summary>Runs <paramref name="body"/>'s layer commands as one undo step <paramref name="label"/>;
    /// when it throws, every layer it changed goes back and nothing is recorded. <paramref name="also"/>: an
    /// edit of another store already done (a table row added on a scratch stack), undone and redone with the
    /// step; undone when the body throws. <paramref name="merge"/>: the step's merge key, read once the body
    /// ran; later groups of the same key merge into it until <see cref="EndDrag"/> (Alt+drag: the copies'
    /// moves fold into their duplicate).</summary>
    public void Group(string label, Action body, IUndoCommand? also = null, Func<string>? merge = null) =>
        Grouped(label, () =>
        {
            body();
            return null;
        }, also, merge);

    /// <summary>As <see cref="Group(string, Action, IUndoCommand?, Func{string}?)"/>, the other store's edit done by
    /// <paramref name="body"/> last and returned (it needs what the layer commands made: an added node's
    /// key placed on the graph). One undo step.</summary>
    public void GroupWith(string label, Func<IUndoCommand?> body) => Grouped(label, body, null, null);

    private void Grouped(string label, Func<IUndoCommand?> body, IUndoCommand? also, Func<string>? merge)
    {
        if (_group != null)
        {
            if (also != null || body() != null)
            {
                throw new InvalidOperationException("a nested group takes no other store's edit");
            }

            return;
        }

        var group = new Dictionary<string, MapLayer>(StringComparer.Ordinal);
        _group = group;
        try
        {
            var made = body();
            also ??= made;
        }
        catch
        {
            foreach (var (layer, before) in group)
            {
                Put(layer, before);
            }

            also?.Undo();
            throw;
        }
        finally
        {
            _group = null;
        }

        if (group.Count > 0 || also != null)
        {
            History.Execute(new GroupCommand(this, label, [.. group.Select(g => (g.Key, g.Value, Layer(g.Key)))], also, merge?.Invoke()));
        }
    }

    /// <summary>Ends a drag: the next set is a step of its own.</summary>
    public void EndDrag() => History.EndMerge();

    public bool Undo() => History.Undo();

    public bool Redo() => History.Redo();

    /// <summary>The layers were written as they are now.</summary>
    public void MarkSaved()
    {
        foreach (var e in _layers.Values)
        {
            e.Saved = e.Layer;
        }

        _savedMission = Mission;
        Layouts.MarkSaved();
        Outliner.MarkSaved();
        History.MarkSaved();
    }

    /// <summary>The layers whose edits differ from what was loaded or last saved.</summary>
    public IEnumerable<string> ChangedLayers => _layers.Where(l => l.Value.Changed).Select(l => l.Key);

    /// <summary>
    /// Writes the layers to the folder mod <paramref name="modFolder"/> (<c>maps/&lt;layer&gt;.layer.json</c>,
    /// and a <c>mod.json</c> named after the folder when it has none) and marks them saved: the changed
    /// layers, or with <paramref name="all"/> (a save into another mod) every changed or non-empty one; and the
    /// mission file (<see cref="MissionPath"/>) when it changed, or with <paramref name="all"/> when it was edited
    /// since it was loaded. Each file is written beside the old one, then moved over it. The paths written,
    /// relative to the mod.
    /// </summary>
    public IReadOnlyList<string> Save(string modFolder, bool all = false)
    {
        var written = Begin(modFolder);
        foreach (var (layer, e) in _layers)
        {
            if (e.Changed || (all && !e.Layer.IsEmpty))
            {
                WriteFile(Path.Combine(modFolder, MapLayer.PathOf(layer)), e.Layer.ToBytes());
                written.Add(MapLayer.PathOf(layer));
            }
        }

        if (MissionPath != null && Mission != null && (MissionChangedSinceSave || (all && !ReferenceEquals(Mission, _loadedMission))))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(modFolder, MissionPath))!);
            WriteFile(Path.Combine(modFolder, MissionPath), Mission.ToJson());
            written.Add(MissionPath);
        }

        written.AddRange(Layouts.Save(modFolder, all ? LayoutSave.NonEmpty : LayoutSave.Changed));
        written.AddRange(Outliner.Save(modFolder, all ? LayoutSave.NonEmpty : LayoutSave.Changed));
        MarkSaved();
        return written;
    }

    /// <summary>
    /// The play-test's copy (Play > Play-test in game window): every layer the document holds, an empty one
    /// too (so it hides an older file of the same layer in a mod under it), and the mission file when it was
    /// edited, written to <paramref name="modFolder"/>; the document stays unsaved. The paths written.
    /// </summary>
    public IReadOnlyList<string> WriteCopy(string modFolder)
    {
        var written = Begin(modFolder);
        foreach (var (layer, e) in _layers)
        {
            WriteFile(Path.Combine(modFolder, MapLayer.PathOf(layer)), e.Layer.ToBytes());
            written.Add(MapLayer.PathOf(layer));
        }

        if (MissionPath != null && Mission != null && !ReferenceEquals(Mission, _loadedMission))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(modFolder, MissionPath))!);
            WriteFile(Path.Combine(modFolder, MissionPath), Mission.ToJson());
            written.Add(MissionPath);
        }

        return written;
    }

    // The mod's maps folder, and its mod.json when it has none (then in the list).
    private static List<string> Begin(string modFolder)
    {
        var written = new List<string>();
        Directory.CreateDirectory(Path.Combine(modFolder, "maps"));
        var info = Path.Combine(modFolder, ModInfo.FileName);
        if (!File.Exists(info))
        {
            var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(modFolder));
            WriteFile(info, Encoding.UTF8.GetBytes($"{{\n  \"name\": \"{System.Text.Json.JsonEncodedText.Encode(name)}\"\n}}\n"));
            written.Add(ModInfo.FileName);
        }

        return written;
    }

    private static void WriteFile(string path, byte[] bytes)
    {
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, bytes);
        File.Move(temp, path, overwrite: true);
    }

    private void Run(string layer, string label, MapLayer before, MapLayer after, string? merge)
    {
        // Inside a group: applied now, recorded as one step when the group ends.
        if (_group != null)
        {
            _group.TryAdd(layer, before);
            Put(layer, after);
            return;
        }

        History.Execute(new LayerCommand(this, layer, label, before, after, merge));
    }

    private void Put(string layer, MapLayer value)
    {
        var e = Get(layer);
        e.Layer = value;
        e.Map = null;
        LayerChanged?.Invoke(layer);
    }

    private static ImmutableSortedDictionary<string, int> Fields(IReadOnlyDictionary<string, int> fields)
    {
        var b = ImmutableSortedDictionary.CreateBuilder<string, int>(StringComparer.Ordinal);
        foreach (var (k, v) in fields)
        {
            b[k] = MapLayer.FieldNames.Contains(k) ? v : throw new MapLayerException($"\"{k}\" is not an entity field");
        }

        return b.ToImmutable();
    }

    private MapFile Check(string layer, MapLayer l)
    {
        var e = Get(layer);
        return l.Apply(e.Number, e.Base);
    }

    /// <summary>The layer with a set on <paramref name="key"/>: folded into the entity's last edit when that
    /// gives the same entity and names as a separate set, else a set of its own.</summary>
    private MapLayer WithSet(string layer, MapLayer before, int key, ImmutableSortedDictionary<string, int> fields, string? name)
    {
        if (!Map(layer).TryGetEntityAt(key, out _))
        {
            throw new MapLayerException($"set {key}: the map has no entity under that key");
        }

        var appended = new MapLayer(before.Base, before.Edits.Add(new MapEdit(MapEditOp.Set, key, fields, name)), before.Names);
        var want = Check(layer, appended);
        var last = before.Edits.Length - 1;
        while (last >= 0 && before.Edits[last].Key != key)
        {
            last--;
        }

        if (last < 0 || before.Edits[last].Op == MapEditOp.Delete)
        {
            return appended;
        }

        var old = before.Edits[last];
        var merged = old.Fields.SetItems(fields);
        if (name != null)
        {
            merged = merged.Remove("nameIndex");
        }

        var folded = new MapLayer(before.Base,
            before.Edits.SetItem(last, old with { Fields = merged, Name = name ?? (fields.ContainsKey("nameIndex") ? null : old.Name) }), before.Names);
        MapFile got;
        try
        {
            got = Check(layer, folded);
        }
        catch (MapLayerException)
        {
            return appended;
        }

        return got.Names.SequenceEqual(want.Names) && got.TryGetEntityAt(key, out var a) && want.TryGetEntityAt(key, out var b)
            && EntityText(a) == EntityText(b) ? folded : appended;
    }

    /// <summary>Every field of an entity as one line of text (byte blocks in hex): two entities with the
    /// same text are the same entity.</summary>
    public static string EntityText(MapEntity e)
    {
        var s = new StringBuilder();
        foreach (var p in typeof(MapEntity).GetProperties(BindingFlags.Public | BindingFlags.Instance).OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            var v = p.GetValue(e);
            s.Append(p.Name).Append('=').Append(v switch
            {
                ImmutableArray<byte> bytes => Convert.ToHexString(bytes.AsSpan()),
                IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
                IEnumerable list and not string => string.Join(",", list.Cast<object>()),
                null => "null",
                _ => v.ToString(),
            }).Append(';');
        }

        return s.ToString();
    }

    private sealed class MissionCommand(EditDocument doc, string label, MissionFile before, MissionFile after) : IUndoCommand
    {
        public string Label { get; } = label;

        public void Do() => doc.PutMission(after);

        public void Undo() => doc.PutMission(before);

        public bool TryMerge(IUndoCommand next) => false;
    }

    private sealed class GroupCommand(EditDocument doc, string label, List<(string Layer, MapLayer Before, MapLayer After)> layers, IUndoCommand? also,
        string? merge) : IUndoCommand
    {
        public string Label { get; } = label;
        private string? Merge { get; } = merge;
        private List<(string Layer, MapLayer Before, MapLayer After)> Layers { get; } = layers;
        private bool Other => also != null;

        // The other store first: a layer's rebuild may read it (a robot type the layer now names).
        public void Do()
        {
            also?.Do();
            Layers.ForEach(l => doc.Put(l.Layer, l.After));
        }

        public void Undo()
        {
            Layers.ForEach(l => doc.Put(l.Layer, l.Before));
            also?.Undo();
        }

        // A group of the same key: its layers' afters taken over (a layer new to this step keeps its before).
        public bool TryMerge(IUndoCommand next)
        {
            if (Merge == null || next is not GroupCommand g || g.Merge != Merge || g.Other)
            {
                return false;
            }

            foreach (var (layer, before, after) in g.Layers)
            {
                var i = Layers.FindIndex(l => l.Layer == layer);
                if (i < 0)
                {
                    Layers.Add((layer, before, after));
                    continue;
                }

                Layers[i] = Layers[i] with { After = after };
            }

            return true;
        }
    }

    private sealed class LayerCommand(EditDocument doc, string layer, string label, MapLayer before, MapLayer after, string? merge) : IUndoCommand
    {
        private MapLayer _after = after;

        public string Label { get; } = label;
        private string? Merge { get; } = merge;
        private string Name { get; } = layer;

        public void Do() => doc.Put(Name, _after);

        public void Undo() => doc.Put(Name, before);

        public bool TryMerge(IUndoCommand next)
        {
            if (Merge == null || next is not LayerCommand c || c.Merge != Merge || c.Name != Name)
            {
                return false;
            }

            _after = c._after;
            return true;
        }
    }
}
