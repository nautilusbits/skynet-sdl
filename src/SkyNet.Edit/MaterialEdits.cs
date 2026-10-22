using System.Globalization;
using RetroEng.Commands;
using RetroEng.Editor;
using RetroEng.Render;
using SkyNet.Formats;
using SkyNet.Game.Mods;

namespace SkyNet.Edit;

/// <summary>
/// The Asset Viewer's "Material" combo (editor-tools plan, T1): a texture record's ENHANCED material
/// written into the save mod's <c>materials.json</c> at once, each edit a step of <see cref="History"/>
/// (the editor's: Edit > Undo takes it back). The combo and the commands material.get and material.set
/// do the same (a remote tool too).
/// <code>
///   combo / "material.set 230/014 metal" ─► Set ─► FileCommand: materials.json ─► Materials.Invalidate ─► Changed (viewport)
/// </code>
/// </summary>
public sealed class MaterialEdits(GameKind game)
{
    /// <summary>The word that removes the mod's own entry (the record then takes what is under it).</summary>
    public const string Inherit = "inherit";

    /// <summary>The undo stack the edits go on: the editor's, shared with the mission's edits (a step
    /// carries its file's path, so it undoes into the mod it wrote whichever is the save mod now).</summary>
    public UndoStack History { get; set; } = new();

    // The save mod's file as last read (the combo asks every frame); read again after a write.
    private (string? Folder, MaterialTable? Table)? _read;

    /// <summary>The mod folder edits go to; null: none chosen (nothing is written).</summary>
    public string? ModFolder { get; set; }

    /// <summary>Raised after every edit, undo and redo, once the file is written: the views apply the materials again.</summary>
    public event Action? Changed;

    /// <summary>What the record is made of now, over every mounted mod.</summary>
    public Surface Effective(int bank, int record) => Materials.Current(game).Resolve(bank, record);

    /// <summary>The save mod's own entry for the record; null when it has none.</summary>
    public Surface? Own(int bank, int record) =>
        Read() is { } table && table.Textures.TryGetValue((bank, record), out var s) ? s : null;

    /// <summary>Sets the save mod's entry for the record to <paramref name="preset"/> (its tweaks
    /// kept); null removes the entry. Returns what was done, or why not.</summary>
    public string Set(int bank, int record, SurfacePreset? preset)
    {
        var key = MaterialTable.Key(bank, record);
        if (ModFolder == null)
        {
            return $"{key}: no mod to write to (File > Save As picks one)";
        }

        var path = Path.Combine(ModFolder, MaterialTable.FileName);
        var before = File.Exists(path) ? File.ReadAllBytes(path) : null;
        MaterialTable table;
        try
        {
            table = before != null ? MaterialTable.Parse(before) : MaterialTable.Empty;
        }
        catch (Exception e) when (e is RetroEng.Json.JsonDataException or System.Text.Json.JsonException)
        {
            return $"{key}: {path} does not read ({e.Message}); not changed";
        }

        var own = table.Textures.TryGetValue((bank, record), out var s) ? s : (Surface?)null;
        var surface = preset is { } p ? (own ?? new Surface(p)) with { Preset = p } : (Surface?)null;
        if (surface == own)
        {
            return $"{key} is {Describe(surface)} already";
        }

        var after = table.WithTexture(bank, record, surface).ToJson();
        var label = $"Material {key} {Describe(surface)}";
        History.Execute(new FileCommand(this, path, before, after, label));
        return $"{label} in {path}";
    }

    /// <summary>"metal", "metal (roughness 0.8)", or <see cref="Inherit"/>.</summary>
    public static string Describe(Surface? surface)
    {
        if (surface is not { } s)
        {
            return Inherit;
        }

        var tweaks = new List<string>();
        if (s.Roughness != 1f)
        {
            tweaks.Add(string.Create(CultureInfo.InvariantCulture, $"roughness {s.Roughness:0.###}"));
        }

        if (s.Specular != 1f)
        {
            tweaks.Add(string.Create(CultureInfo.InvariantCulture, $"specular {s.Specular:0.###}"));
        }

        var name = MaterialTable.NameOf(s.Preset);
        return tweaks.Count == 0 || s.Preset == SurfacePreset.Matte ? name : $"{name} ({string.Join(", ", tweaks)})";
    }

    /// <summary>Adds material.get and material.set to <paramref name="registry"/>.</summary>
    public void Register(CommandRegistry registry)
    {
        var presets = Enum.GetValues<SurfacePreset>().Select(MaterialTable.NameOf).Append(Inherit).ToArray();
        registry.Add(new Command("material.get", "material.get <bank/record>", a =>
        {
            var (bank, record) = RecordOf(a);
            var own = Own(bank, record);
            return $"{MaterialTable.Key(bank, record)} {Describe(Effective(bank, record))}" + (own != null ? " (the mod's own)" : "");
        })
        { Help = "a texture record's ENHANCED material (230/014)" });
        registry.Add(new Command("material.set", "material.set <bank/record> <preset|inherit>", a =>
        {
            var (bank, record) = RecordOf(a);
            var word = a.Word(1);
            if (word.Equals(Inherit, StringComparison.OrdinalIgnoreCase))
            {
                return Set(bank, record, null);
            }

            return MaterialTable.PresetOf(word) is { } preset ? Set(bank, record, preset) : $"No preset {word}: {string.Join(", ", presets)}";
        })
        { Help = "sets a texture record's ENHANCED material in the save mod's materials.json", Arguments = i => i == 1 ? presets : [] });
    }

    // "230/014" (or "230 14").
    private static (int Bank, int Record) RecordOf(CommandArgs a)
    {
        if (a.Count < 1)
        {
            throw new CommandUsageException();
        }

        var parts = a[0].Split('/');
        if (parts.Length == 2 && CommandArgs.TryInt(parts[0], out var bank) && CommandArgs.TryInt(parts[1], out var record))
        {
            return (bank, record);
        }

        throw new CommandUsageException($"{a[0]} is not a record (230/014)");
    }

    private MaterialTable? Read()
    {
        if (_read is { } cached && cached.Folder == ModFolder)
        {
            return cached.Table;
        }

        var table = ReadFile();
        _read = (ModFolder, table);
        return table;
    }

    private MaterialTable? ReadFile()
    {
        var path = ModFolder != null ? Path.Combine(ModFolder, MaterialTable.FileName) : null;
        if (path == null || !File.Exists(path))
        {
            return null;
        }

        try
        {
            return MaterialTable.Parse(File.ReadAllBytes(path));
        }
        catch (Exception e) when (e is RetroEng.Json.JsonDataException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    // A materials.json written (or removed: it was not there before), then the views told.
    private sealed class FileCommand(MaterialEdits owner, string path, byte[]? before, byte[] after, string label) : IUndoCommand
    {
        public string Label => label;

        public void Do() => Put(after);

        public void Undo() => Put(before);

        public bool TryMerge(IUndoCommand next) => false;

        private void Put(byte[]? bytes)
        {
            if (bytes == null)
            {
                File.Delete(path);
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, bytes);
            }

            owner._read = null;
            Materials.Invalidate();
            owner.Changed?.Invoke();
        }
    }
}
