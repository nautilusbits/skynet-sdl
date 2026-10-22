using System.Globalization;
using System.Numerics;
using System.Text;
using Hexa.NET.ImGui;
using RetroEng.Content;
using RetroEng.Editor;
using RetroEng.Render;
using SkyNet.Data;
using SkyNet.Formats;
using SkyNet.Game;
using SkyNet.Game.Audio;
using SkyNet.Game.Mods;
using SkyNet.Upscale;

namespace SkyNet.Edit;

/// <summary>The game's assets for the editor's Assets panel (editor-tools plan, T0): texture and sprite
/// records of the TEXTURE banks, the .3D models, the sound clips and the tables, each marked base or
/// mod by the mounted mods (<see cref="ModContent.Files"/>). "Override in mod" writes the files the game
/// loads from a mod (docs/modding.md): a record as <c>textures/TEXTURE.NNN/RRR.png</c>, a table as
/// <c>tables/name.json</c>. Models have no such path; a sound's is the sound viewer's (<c>sounds/NAME.wav</c>).
/// <code>
///   TEXTURE.NNN ─► Textures / Sprites ─► PNG (palette, index 0 a hole) ─► textures/TEXTURE.NNN/RRR.png
///   MDMDOBJS/ENMS.BSA ─► Models (turntable)          MDMDSFXS.BSA ─► Sounds (waveform)
///   T*.HMI, TITLE.HMI ─► Music (player)
///   tables (embedded or a mod's) ─► Tables (JSON) ─► tables/name.json
/// </code></summary>
public sealed class GameAssets(GameData data, Renderer? renderer = null, string? modsDir = null) : IAssetSource
{
    public const string Textures = "Textures", Sprites = "Sprites", Models = "Models", Sounds = "Sounds", Music = "Music", Tables = "Tables";
    private const string TexturePrefix = "TEXTURE.";
    private static readonly string[] ModelArchives = ["MDMDOBJS.BSA", "MDMDENMS.BSA"];
    private static readonly string[] ModelExtensions = [".glb", ".gltf"];
    private const string ModelExtension = ".3D";
    private const string NoModelExport = "A mod replaces a model with models/NAME.glb (docs/modding.md, Models): the viewer's Replace with 3D model... writes it.";
    private const string NoSoundPath = "A mod replaces a sound with sounds/NAME.wav: the sound viewer's Replace in mod... copies one in.";
    private const string NoMusicPath = "The game reads no music from mods.";
    private const string SongExtension = ".HMI";
    private const int WaveColumns = 512;
    /// <summary>The most thumbnails kept (GPU textures, model triangles); the oldest go first.</summary>
    private const int ThumbnailCap = 256;
    private const float ThumbnailYaw = 0.6f;
    /// <summary>The viewer's texture is blown up to about this many pixels across, at most this many times.</summary>
    private const int ViewerPixels = 512, MaxBlowUp = 16;

    private readonly Dictionary<int, List<TextureStack.Record>> _banks = [];
    private readonly Dictionary<(int Bank, int Record), Vector3> _averages = [];
    private Palette? _palette;
    // The preview shown: its entry and what was made for it.
    private string? _previewKey;
    private TextureHandle _image;
    private (int Width, int Height, int Frames, int MsPerFrame) _imageSize;
    private MeshPreview? _mesh;
    private float[]? _wave;
    private string? _text;
    // The list's thumbnails by entry key, oldest first in the queue.
    private readonly Dictionary<string, (TextureHandle Image, int Width, int Height, int Frames, MeshPreview? Mesh)> _thumbs = [];
    private readonly Queue<string> _thumbOrder = new();

    public IReadOnlyList<string> Kinds { get; } = [Textures, Sprites, Models, Sounds, Music, Tables];

    /// <summary>The texture viewer's "Material" combo acts through these; null: no combo.</summary>
    public MaterialEdits? Materials { get; set; }

    /// <summary>The model viewer a model's preview goes to (T2); null: the flat-colour turntable.</summary>
    public ModelViewer? Viewer { get; set; }

    /// <summary>The sprite viewer a sprite's preview goes to (T3); null: the plain image.</summary>
    public SpriteViewer? SpriteView { get; set; }

    /// <summary>The sound viewer a sound's preview goes to (T4); null: the plain waveform.</summary>
    public SoundViewer? SoundView { get; set; }

    /// <summary>The music player a song's preview goes to (T4); null: no preview.</summary>
    public MusicViewer? MusicView { get; set; }

    /// <summary>The model viewer's "Export glTF..." button acts through this (a model's name); null: no button.</summary>
    public Action<string>? ExportModel { get; set; }

    /// <summary>The combo's choices: the presets, then <see cref="MaterialEdits.Inherit"/>.</summary>
    private static readonly string[] MaterialChoices = [.. Enum.GetValues<SurfacePreset>().Select(MaterialTable.NameOf), MaterialEdits.Inherit];
    private const float MaterialComboWidth = 140f;

    public IEnumerable<AssetEntry> Enumerate(string kind) => kind switch
    {
        Textures or Sprites => Records(kind),
        Models => ModelEntries(),
        Sounds => SoundEntries(),
        Music => SongEntries(),
        Tables => TableEntries(),
        _ => [],
    };

    public string? OverrideBlocker(AssetEntry entry) => entry.Kind switch
    {
        Textures or Sprites or Tables => null,
        Models => NoModelExport,
        Sounds => NoSoundPath,
        Music => NoMusicPath,
        _ => "Unknown kind.",
    };

    public IReadOnlyList<string> Override(AssetEntry entry, string modFolder)
    {
        var (path, bytes) = entry.Kind switch
        {
            Textures or Sprites => TextureFile(entry),
            Tables => TableFile(entry.Id),
            _ => throw new InvalidOperationException(OverrideBlocker(entry)),
        };

        var target = Path.Combine(modFolder, path.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllBytes(target, bytes);
        return [path];
    }

    // ---- texture and sprite records ----

    /// <summary>The bank and record an entry id ("301/012") names.</summary>
    private static (int Bank, int Record) RecordOf(string id)
    {
        var slash = id.IndexOf('/');
        return (int.Parse(id.AsSpan(0, slash), CultureInfo.InvariantCulture), int.Parse(id.AsSpan(slash + 1), CultureInfo.InvariantCulture));
    }

    /// <summary>A record's entry id: "301/012".</summary>
    public static string RecordId(int bank, int record) => string.Create(CultureInfo.InvariantCulture, $"{bank:D3}/{record:D3}");

    private IEnumerable<AssetEntry> Records(string kind)
    {
        var sprite = kind == Sprites;
        foreach (var bank in Banks())
        {
            foreach (var r in Bank(bank).Where(r => r.Sprite == sprite))
            {
                var (origin, mod) = OriginOf(ModContent.TexturePath(r.Bank, r.Index));
                var detail = r.Frames > 1 ? $"{r.Width} x {r.Height}, {r.Frames} frames" : $"{r.Width} x {r.Height}";
                yield return new AssetEntry(kind, RecordId(r.Bank, r.Index), $"{TexturePrefix}{r.Bank:D3} #{r.Index:D3}",
                    $"{TexturePrefix}{r.Bank:D3}", origin, mod, detail);
            }
        }
    }

    /// <summary>The TEXTURE.NNN banks in the data folder, in number order.</summary>
    private IEnumerable<int> Banks() =>
        Directory.EnumerateFiles(data.Dir)
            .Select(Path.GetFileName)
            .Where(f => f!.Length == TexturePrefix.Length + 3 && f.StartsWith(TexturePrefix, StringComparison.OrdinalIgnoreCase))
            .Select(f => int.TryParse(f![TexturePrefix.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : -1)
            .Where(n => n >= 0)
            .Distinct()
            .Order();

    private List<TextureStack.Record> Bank(int bank)
    {
        if (!_banks.TryGetValue(bank, out var records))
        {
            records = data.ReadLoose($"{TexturePrefix}{bank:D3}") is { } bytes ? [.. TextureStack.Records(bank, bytes)] : [];
            _banks[bank] = records;
        }

        return records;
    }

    private TextureStack.Record? Record(string id)
    {
        var (bank, record) = RecordOf(id);
        return Bank(bank).FirstOrDefault(r => r.Index == record);
    }

    private Palette Palette => _palette ??= data.LoadPalette() ?? throw new InvalidDataException("no game palette");

    /// <summary>A record as RGBA, frames stacked downwards: the palette's colours, index 0 a hole
    /// (alpha 0) holding the haze colour, which an opaque face draws (docs/modding.md, Textures).</summary>
    private byte[] Rgba(TextureStack.Record r)
    {
        var rgb = Palette.Rgb;
        var rgba = new byte[r.Pixels.Length * 4];
        for (var i = 0; i < r.Pixels.Length; i++)
        {
            int index = r.Pixels[i], colour = index == 0 ? HiresTextures.HazeIndex : index;
            rgba[i * 4] = rgb[colour * 3];
            rgba[i * 4 + 1] = rgb[colour * 3 + 1];
            rgba[i * 4 + 2] = rgb[colour * 3 + 2];
            rgba[i * 4 + 3] = index == 0 ? (byte)0 : (byte)255;
        }

        return rgba;
    }

    /// <summary>The record's mod file: the mod's own when one has it, else the DOS record as a PNG.</summary>
    private (string Path, byte[] Bytes) TextureFile(AssetEntry entry)
    {
        var (bank, record) = RecordOf(entry.Id);
        var path = ModContent.TexturePath(bank, record);
        if (ModContent.Files?.TryReadAllBytes(VfsPath.Normalize(path)) is { } modded)
        {
            return (path, modded);
        }

        var r = Record(entry.Id) ?? throw new InvalidDataException($"{entry.Name} is not in the data");
        return (path, PngWriter.Encode(r.Width, r.Height * r.Frames, 4, Rgba(r)));
    }

    // ---- models, sounds, tables ----

    private IEnumerable<AssetEntry> ModelEntries()
    {
        foreach (var archive in ModelArchives.Where(data.Exists))
        {
            using var a = data.OpenArchive(archive);
            foreach (var e in a.Entries.Where(e => e.Name.EndsWith(ModelExtension, StringComparison.OrdinalIgnoreCase)).OrderBy(e => e.Name, StringComparer.Ordinal))
            {
                var name = e.Name[..^ModelExtension.Length];
                var (origin, mod) = ModelOrigin(name);
                yield return new AssetEntry(Models, name, e.Name, archive, origin, mod, $"{e.Size} bytes");
            }
        }
    }

    /// <summary>The .3D files of the model archives ("LASER1.3D"), for the tables' model combos.</summary>
    public IReadOnlyList<string> ModelFiles() => [.. ModelEntries().Select(e => e.Id + ModelExtension).Distinct(StringComparer.OrdinalIgnoreCase)];

    private static (AssetOrigin, string?) ModelOrigin(string name)
    {
        foreach (var ext in ModelExtensions)
        {
            if (OriginOf("models/" + name + ext) is (AssetOrigin.Mod, _) found)
            {
                return found;
            }
        }

        return (AssetOrigin.Base, null);
    }

    private IEnumerable<AssetEntry> SoundEntries()
    {
        if (!data.Exists(SoundBank.Archive))
        {
            yield break;
        }

        using var a = data.OpenArchive(SoundBank.Archive);
        foreach (var e in a.Entries.OrderBy(e => e.Name, StringComparer.Ordinal))
        {
            var seconds = string.Create(CultureInfo.InvariantCulture, $"{(double)e.Size / SoundBank.RawRate:0.00} s");
            var (origin, mod) = OriginOf(SampleEdit.ModPath(e.Name));
            yield return new AssetEntry(Sounds, e.Name, e.Name, "", origin, mod, seconds);
        }
    }

    // The loose songs: TITLE.HMI and the T*.HMI tracks.
    private IEnumerable<AssetEntry> SongEntries() =>
        Directory.EnumerateFiles(data.Dir)
            .Select(f => Path.GetFileName(f).ToUpperInvariant())
            .Where(f => f.EndsWith(SongExtension, StringComparison.Ordinal) && (f.StartsWith('T') || f == MusicTracks.Title))
            .Order(StringComparer.Ordinal)
            .Select(f => new AssetEntry(Music, f, f, "", AssetOrigin.Base, null, string.Join(", ", MusicUsage.Uses(f))));

    private static IEnumerable<AssetEntry> TableEntries()
    {
        foreach (var name in TableNames.All.Order(StringComparer.Ordinal))
        {
            var (origin, mod) = OriginOf(GameTables.PathOf(name));
            yield return new AssetEntry(Tables, name, name + ".json", "", origin, mod);
        }
    }

    /// <summary>A table's file as the game reads it: the winning mod's, else the shipped one.</summary>
    private static (string Path, byte[] Bytes) TableFile(string name)
    {
        var path = GameTables.PathOf(name);
        var bytes = ModContent.Files?.TryReadAllBytes(VfsPath.Normalize(path)) ?? GameTables.EmbeddedBytes(name)
            ?? throw new InvalidDataException($"Table {name} is not shipped");
        return (path, bytes);
    }

    /// <summary>Whether a mounted mod has <paramref name="path"/>, and which (the highest).</summary>
    private static (AssetOrigin Origin, string? Mod) OriginOf(string path) =>
        ModContent.Files?.Resolve(VfsPath.Normalize(path)) is { } mount ? (AssetOrigin.Mod, mount.Name) : (AssetOrigin.Base, null);

    // ---- what a map uses ----

    /// <summary>The asset keys MAP.<paramref name="map"/> uses: its props' models and their faces'
    /// texture records, its sprites. Robots and the ground are left out.</summary>
    public HashSet<string> MapKeys(MapFile map)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in map.Entities)
        {
            if (e.Variant == MapEntityVariant.Sprite && e.SpriteIndex >= 0 && !e.IsMarker)
            {
                AddRecord(keys, e.SpriteBank, e.SpriteRecord);
            }

            if (e.Variant != MapEntityVariant.Mesh || map.EntityName(e) is not { Length: > 0 } name || !keys.Add(Models + ":" + name))
            {
                continue;
            }

            foreach (var face in data.LoadModel(name + ModelExtension)?.Faces ?? [])
            {
                if (!face.IsSolidColour)
                {
                    AddRecord(keys, face.TextureBank, face.TextureRecord);
                }
            }
        }

        return keys;
    }

    // A record could be listed as a wall or as a sprite: both keys.
    private static void AddRecord(HashSet<string> keys, int bank, int record)
    {
        keys.Add(Textures + ":" + RecordId(bank, record));
        keys.Add(Sprites + ":" + RecordId(bank, record));
    }

    // ---- files on disk ----

    public string? PathOf(AssetEntry entry)
    {
        if (entry.Origin == AssetOrigin.Mod && entry.Mod != null && ModPath(entry) is { } inMod)
        {
            return ModFile(entry.Mod, inMod);
        }

        return entry.Kind switch
        {
            Textures or Sprites => data.PathOf($"{TexturePrefix}{RecordOf(entry.Id).Bank:D3}"),
            Models => data.PathOf(entry.Group),
            Sounds => data.PathOf(SoundBank.Archive),
            Music => data.PathOf(entry.Id),
            _ => null,
        };
    }

    /// <summary>The entry's path inside a mod (the one it resolves to for a model); null: none.</summary>
    private static string? ModPath(AssetEntry entry) => entry.Kind switch
    {
        Textures or Sprites => ModContent.TexturePath(RecordOf(entry.Id).Bank, RecordOf(entry.Id).Record),
        Models => ModelExtensions.Select(ext => "models/" + entry.Id + ext).FirstOrDefault(p => OriginOf(p).Origin == AssetOrigin.Mod),
        Tables => GameTables.PathOf(entry.Id),
        Sounds => SampleEdit.ModPath(entry.Id),
        _ => null,
    };

    /// <summary>A mod's file: in its folder when there, else its ZIP (the folder lies over the ZIP).</summary>
    private string? ModFile(string mod, string path)
    {
        if (modsDir == null)
        {
            return null;
        }

        var loose = Path.Combine(modsDir, mod, path.Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(loose) ? loose : Path.Combine(modsDir, mod + ".zip");
    }

    // ---- thumbnails ----

    /// <summary>Called with each row as the panel draws it, its row the last item: a drag of it begins there (a drop into the view).</summary>
    public Action<AssetEntry>? Dragging { get; set; }

    public bool DrawThumbnail(AssetEntry entry, Vector2 size)
    {
        Dragging?.Invoke(entry);
        if (entry.Kind is not (Textures or Sprites or Models))
        {
            return false;
        }

        if (!_thumbs.TryGetValue(entry.Key, out var thumb))
        {
            thumb = MakeThumbnail(entry);
            Keep(entry.Key, thumb);
        }

        if (thumb.Mesh != null)
        {
            thumb.Mesh.DrawStill(size, ThumbnailYaw);
            return true;
        }

        if (thumb.Image.IsNone)
        {
            return false;
        }

        AssetPreview.Image(thumb.Image, thumb.Width, thumb.Height * thumb.Frames, size, thumb.Frames);
        return true;
    }

    private (TextureHandle, int, int, int, MeshPreview?) MakeThumbnail(AssetEntry entry)
    {
        if (entry.Kind == Models)
        {
            return (default, 0, 0, 0, Mesh(entry.Id));
        }

        if (renderer == null || Record(entry.Id) is not { } r)
        {
            return (default, 0, 0, 0, null);
        }

        return (renderer.CreateRgbaTexture(r.Width, r.Height * r.Frames, Rgba(r), scoped: false), r.Width, r.Height, r.Frames, null);
    }

    // A thumbnail kept; past the cap the oldest is let go.
    private void Keep(string key, (TextureHandle Image, int Width, int Height, int Frames, MeshPreview? Mesh) thumb)
    {
        _thumbs[key] = thumb;
        _thumbOrder.Enqueue(key);
        while (_thumbOrder.Count > ThumbnailCap && _thumbOrder.TryDequeue(out var old))
        {
            if (_thumbs.Remove(old, out var gone) && !gone.Image.IsNone)
            {
                renderer?.Destroy(gone.Image);
            }
        }
    }

    // ---- previews ----

    public void DrawPreview(AssetEntry entry, Vector2 size)
    {
        if (_previewKey != entry.Key)
        {
            Prepare(entry);
        }

        switch (entry.Kind)
        {
            case Sprites when !_image.IsNone && SpriteView != null:
                var (bank, record) = RecordOf(entry.Id);
                SpriteView.Draw(bank, record, _image, _imageSize);
                break;
            case Textures or Sprites when !_image.IsNone:
                if (entry.Kind == Textures && Materials != null)
                {
                    MaterialCombo(Materials, entry);
                }

                var (w, h, frames, ms) = _imageSize;
                var frame = frames > 1 ? (int)(ImGui.GetTime() * 1000.0 / Math.Max(ms, GpuTexture.FallbackMsPerFrame)) : 0;
                AssetPreview.Image(_image, w, h * frames, size, frames, frame);
                break;
            case Models when Viewer != null:
                Viewer.ModelControls(entry.Name);
                break;
            case Models when _mesh != null:
                ImGui.TextDisabled($"{_mesh.Triangles} triangles, flat colours (drag to turn)");
                ExportButton(entry.Id);
                AssetPreview.Turntable(_mesh, ImGui.GetContentRegionAvail());
                break;
            case Sounds when SoundView != null:
                SoundView.Draw();
                break;
            case Music when MusicView != null:
                MusicView.Draw(entry.Id);
                break;
            case Sounds when _wave != null:
                ImGui.TextDisabled("No playback: the editor has no audio mixer.");
                Waveform(_wave, ImGui.GetContentRegionAvail());
                break;
            case Tables when _text != null:
                ImGui.TextUnformatted(_text);
                break;
            default:
                ImGui.TextDisabled("No preview: it does not load.");
                break;
        }
    }

    // The model as .glb files (GltfExport), into a folder the editor asks for.
    private void ExportButton(string model)
    {
        if (ExportModel == null || !ImGui.Button("Export glTF..."))
        {
            return;
        }

        ExportModel(model);
    }

    // The record's ENHANCED material: the save mod's own entry (or inherit) and what is in effect; Edit > Undo takes a change back.
    private static void MaterialCombo(MaterialEdits materials, AssetEntry entry)
    {
        var (bank, record) = RecordOf(entry.Id);
        var own = materials.Own(bank, record);
        var chosen = own is { } o ? (int)o.Preset : MaterialChoices.Length - 1;
        ImGui.BeginDisabled(materials.ModFolder == null);
        ImGui.SetNextItemWidth(MaterialComboWidth);
        if (ImGui.Combo("Material", ref chosen, MaterialChoices, MaterialChoices.Length))
        {
            var preset = chosen < MaterialChoices.Length - 1 ? (SurfacePreset)chosen : (SurfacePreset?)null;
            RetroEng.Diagnostics.Log.Info("Assets", materials.Set(bank, record, preset));
        }

        ImGui.EndDisabled();
        if (materials.ModFolder == null && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.SetTooltip("No mod to write to: File > Save As picks one.");
        }

        ImGui.TextDisabled($"In effect (ENHANCED look): {MaterialEdits.Describe(materials.Effective(bank, record))}");
    }

    /// <summary>What the preview of <paramref name="entry"/> draws, made once when it is selected.</summary>
    private void Prepare(AssetEntry entry)
    {
        ForgetPreview();
        _previewKey = entry.Key;
        try
        {
            switch (entry.Kind)
            {
                case Textures or Sprites when renderer != null && Record(entry.Id) is { } r:
                    // Blown up by whole pixels first: the UI samples linearly, so the art stays crisp.
                    var k = Math.Clamp(ViewerPixels / Math.Max(r.Width, r.Height), 1, MaxBlowUp);
                    _image = renderer.CreateRgbaTexture(r.Width * k, r.Height * r.Frames * k, BlowUp(Rgba(r), r.Width, r.Height * r.Frames, k), scoped: false);
                    _imageSize = (r.Width, r.Height, r.Frames, r.MsPerFrame);
                    break;
                case Models:
                    _mesh = Mesh(entry.Id);
                    break;
                case Sounds when SoundView != null:
                    SoundView.Select(entry.Id, SoundBytes(entry.Id) ?? []);
                    break;
                case Sounds:
                    _wave = Wave(entry.Id);
                    break;
                case Tables:
                    _text = Encoding.UTF8.GetString(TableFile(entry.Id).Bytes);
                    break;
            }
        }
        catch (Exception e) when (e is IOException or InvalidDataException)
        {
            RetroEng.Diagnostics.Log.Warn("Assets", $"{entry.Key}: no preview ({e.Message})");
        }
    }

    /// <summary>Lets the preview's and the thumbnails' GPU textures go (the panel closed, the editor quitting).</summary>
    public void Forget()
    {
        ForgetPreview();
        foreach (var thumb in _thumbs.Values.Where(t => !t.Image.IsNone))
        {
            renderer?.Destroy(thumb.Image);
        }

        _thumbs.Clear();
        _thumbOrder.Clear();
    }

    private void ForgetPreview()
    {
        if (!_image.IsNone)
        {
            renderer?.Destroy(_image);
        }

        (_previewKey, _image, _mesh, _wave, _text) = (null, default, null, null, null);
    }

    /// <summary>A model's frame 0 as triangles, each face in its texture's mean colour (or its palette colour).</summary>
    private MeshPreview? Mesh(string name)
    {
        if (data.LoadModel(name + ModelExtension) is not { } model)
        {
            return null;
        }

        var corners = new List<Vector3>();
        var colours = new List<Vector3>();
        var vertices = model.Vertices;
        foreach (var face in model.Faces)
        {
            var colour = face.IsSolidColour ? Colour(face.Texture & 0xFF) : Average(face.TextureBank, face.TextureRecord);
            foreach (var (a, b, c) in Model3D.TriangleFan(face))
            {
                int ia = face.Indices[a], ib = face.Indices[b], ic = face.Indices[c];
                if (ia >= vertices.Length || ib >= vertices.Length || ic >= vertices.Length)
                {
                    continue;
                }

                corners.Add(vertices[ia].ToYUp());
                corners.Add(vertices[ib].ToYUp());
                corners.Add(vertices[ic].ToYUp());
                colours.Add(colour);
            }
        }

        return new MeshPreview(corners, colours);
    }

    private Vector3 Colour(int index)
    {
        var (r, g, b) = Palette[index];
        return new Vector3(r, g, b) / 255f;
    }

    // A record's mean colour over its non-hole pixels.
    private Vector3 Average(int bank, int record)
    {
        if (_averages.TryGetValue((bank, record), out var mean))
        {
            return mean;
        }

        var sum = Vector3.Zero;
        var count = 0;
        var r = Bank(bank).FirstOrDefault(x => x.Index == record);
        foreach (var index in r?.Pixels ?? [])
        {
            if (index != 0)
            {
                sum += Colour(index);
                count++;
            }
        }

        mean = count > 0 ? sum / count : new Vector3(0.6f);
        _averages[(bank, record)] = mean;
        return mean;
    }

    /// <summary><paramref name="rgba"/> (<paramref name="width"/> x <paramref name="height"/>) with each pixel made <paramref name="k"/> x <paramref name="k"/>.</summary>
    private static byte[] BlowUp(byte[] rgba, int width, int height, int k)
    {
        if (k == 1)
        {
            return rgba;
        }

        var big = new byte[rgba.Length * k * k];
        for (var y = 0; y < height * k; y++)
        {
            for (var x = 0; x < width * k; x++)
            {
                Array.Copy(rgba, ((y / k) * width + x / k) * 4, big, (y * width * k + x) * 4, 4);
            }
        }

        return big;
    }

    /// <summary>A clip's bytes as the archive holds them; null when empty.</summary>
    private byte[]? SoundBytes(string name)
    {
        using var a = data.OpenArchive(SoundBank.Archive);
        return a.Read(name) is { Length: > 0 } bytes ? bytes : null;
    }

    /// <summary>A clip's peak level per column (unsigned 8-bit samples, 128 silence).</summary>
    private float[]? Wave(string name)
    {
        using var a = data.OpenArchive(SoundBank.Archive);
        if (a.Read(name) is not { Length: > 0 } bytes)
        {
            return null;
        }

        var wave = new float[Math.Min(WaveColumns, bytes.Length)];
        for (var c = 0; c < wave.Length; c++)
        {
            var (from, to) = (c * bytes.Length / wave.Length, (c + 1) * bytes.Length / wave.Length);
            for (var i = from; i < to; i++)
            {
                wave[c] = MathF.Max(wave[c], MathF.Abs(bytes[i] - 128) / 128f);
            }
        }

        return wave;
    }

    private static void Waveform(float[] wave, Vector2 size)
    {
        size = Vector2.Max(size, new Vector2(64f, 32f));
        var at = ImGui.GetCursorScreenPos();
        ImGui.Dummy(size);
        var list = ImGui.GetWindowDrawList();
        var colour = ImGui.GetColorU32(ImGuiCol.PlotLines);
        var middle = at.Y + size.Y * 0.5f;
        for (var c = 0; c < wave.Length; c++)
        {
            var x = at.X + (c + 0.5f) * size.X / wave.Length;
            var half = MathF.Max(wave[c] * size.Y * 0.5f, 0.5f);
            list.AddLine(new Vector2(x, middle - half), new Vector2(x, middle + half), colour);
        }
    }
}
