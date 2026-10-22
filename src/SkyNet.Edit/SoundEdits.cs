using System.Globalization;
using System.Text;
using RetroEng.Audio;
using RetroEng.Content;
using RetroEng.Editor;
using RetroEng.Json;
using SkyNet.Data;
using SkyNet.Game.Audio;
using SkyNet.Game.Mods;

namespace SkyNet.Edit;

/// <summary>
/// "Replace in mod..." (editor-tools plan, T4): a .wav copied into the save mod as <c>sounds/NAME.wav</c>
/// (NAME the archive file's stem: EXPLO1.RAW ─► sounds/EXPLO1.wav), one undo step that puts back the mod's
/// earlier file (or none).
/// </summary>
public static class SampleEdit
{
    private const string WavExtension = ".wav";

    /// <summary>The mod path of <paramref name="sample"/>'s replacement: "sounds/EXPLO1.wav" (<see cref="SoundMods.PathOf"/>).</summary>
    public static string ModPath(string sample) => SoundMods.PathOf(sample);

    /// <summary>The mounted mods' replacement of <paramref name="sample"/> as the game plays it (<see cref="SoundMods.Effective"/>):
    /// the sound, its origin ("mine:sounds/EXPLO1.wav") and its file; null: the DOS sample plays.</summary>
    public static (Sound Sound, string Origin, byte[] Bytes)? Modded(string sample)
    {
        var files = ModContent.Files;
        if (SoundMods.Effective(files, sample, _ => null) is not { Sound: { } sound } e || e.Origin == SoundMods.Dos)
        {
            return null;
        }

        var path = VfsPath.Normalize(ModPath(sample));
        var bytes = files!.Mounts.Where(m => e.Origin.StartsWith(m.Name + ":", StringComparison.Ordinal))
            .Select(m => m.Mount.TryReadAllBytes(path)).FirstOrDefault(b => b != null);
        return bytes != null ? (sound, e.Origin, bytes) : null;
    }

    /// <summary>Copies <paramref name="wav"/> in as <paramref name="sample"/>'s replacement on <paramref name="history"/>;
    /// <paramref name="changed"/> runs after each do and undo (the mod mounted again).</summary>
    public static string Replace(UndoStack history, string modFolder, string sample, string wav, Action? changed = null)
    {
        if (!string.Equals(Path.GetExtension(wav), WavExtension, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"{Path.GetFileName(wav)} is not a .wav");
        }

        // Checked as the game decodes it: PCM 8 or 16 bits, mono or stereo.
        var bytes = File.ReadAllBytes(wav);
        if (SoundBank.DecodeWav(bytes) == null)
        {
            throw new InvalidDataException($"{Path.GetFileName(wav)} is not PCM 8/16-bit mono or stereo");
        }

        var target = Path.Combine(modFolder, ModPath(sample).Replace('/', Path.DirectorySeparatorChar));
        var before = File.Exists(target) ? File.ReadAllBytes(target) : null;
        history.Execute(new FileEdit($"Replace sound {sample}", target, before, bytes, changed));
        return $"{sample} -> {ModPath(sample)}";
    }

    // One file's contents set, or put back (null: no file).
    private sealed class FileEdit(string label, string path, byte[]? before, byte[] after, Action? changed) : IUndoCommand
    {
        public string Label => label;

        public void Do() => Write(after);

        public void Undo() => Write(before);

        public bool TryMerge(IUndoCommand next) => false;

        private void Write(byte[]? bytes)
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

            changed?.Invoke();
        }
    }
}

/// <summary>
/// The moddable table <c>sounds</c> (<see cref="SoundEventRow"/>) in the Tables grid (editor-tools plan, T4): a
/// sample's playback, keyed by its name without extension. No shipped rows: the base is the mods under the
/// save mod over an empty table; "Edit sound settings" adds a row at <see cref="SoundEventRow.Default"/>.
/// <code>
///   sample ▲ | volume | range | pitch | priority | loop
///   EXPLO1   | 1      | 0     | 1     | 0        | yes      (volume 0-2, pitch 0.25-4; range 0: the game's own)
/// </code>
/// </summary>
public sealed class SoundSettingsSource : ITableSource
{
    public const string TableName = TableNames.Sounds;
    public const string Sample = "sample", Volume = "volume", Range = "range", Pitch = "pitch", Priority = "priority", Loop = "loop";
    private const long Off = 0, On = 1;

    private static readonly GridChoice[] LoopChoices = [new(Off, "no"), new(On, "yes")];

    private static readonly GridColumn[] Fixed =
    [
        new(Sample, CellKind.Text, ReadOnly: true), new(Volume, CellKind.Float), new(Range, CellKind.Float), new(Pitch, CellKind.Float),
        new(Priority, CellKind.Int), new(Loop, CellKind.Choice, LoopChoices),
    ];

    public string Name => TableName;

    public IReadOnlyList<GridColumn> Columns => Fixed;

    public bool CanAdd => false;

    /// <summary>The column of <paramref name="key"/>.</summary>
    public static int ColumnOf(string key) => Array.FindIndex(Fixed, c => c.Key == key);

    /// <summary>A sample's row key: "explo1.raw" → "EXPLO1".</summary>
    public static string KeyOf(string sample) => SoundEventRow.KeyOf(sample);

    /// <summary>A new row for <paramref name="sample"/>: the one that changes nothing.</summary>
    public static object?[] NewRow(string sample) => Cells(SoundEventRow.Default(KeyOf(sample)));

    /// <summary>A grid row as the table's row.</summary>
    public static SoundEventRow RowOf(object?[] r) =>
        new((string)r[0]!, Num(r[1]), Num(r[2]), Num(r[3]), (int)Num(r[4]), Num(r[5]) != Off);

    public (List<object?[]> Base, List<object?[]> Rows) Load(VirtualFileSystem? under, string? folder)
    {
        var table = GameTables.Layer(Name, TableSchemas.Sounds, SoundMods.Base, under);
        var file = folder != null ? PathOf(folder) : null;
        var own = file != null && File.Exists(file) ? TableSchemas.Sounds.Apply(table, JsonItem.Parse(File.ReadAllBytes(file))) : table;
        return ([.. table.Rows.Select(Cells)], [.. own.Rows.Select(Cells)]);
    }

    public IReadOnlyList<string> Save(string folder, IReadOnlyList<object?[]> baseRows, IReadOnlyList<object?[]> rows)
    {
        var relative = GameTables.PathOf(Name);
        var path = PathOf(folder);
        var under = baseRows.ToDictionary(TableGrid.KeyOf);
        var changed = rows.Where(r => under.GetValueOrDefault(TableGrid.KeyOf(r)) is not { } b || !b.SequenceEqual(r, CellComparer.Instance)).ToList();
        if (changed.Count == 0)
        {
            if (!File.Exists(path))
            {
                return [];
            }

            File.Delete(path);
            return [relative + " (removed)"];
        }

        // One row a line, loop a JSON bool; checked as the game reads it before anything is written.
        var lines = changed.Select(r => "    {" + string.Join(", ", Fixed.Select((c, i) => $"\"{c.Key}\": {Json(c, r[i])}")) + "}");
        var bytes = Encoding.UTF8.GetBytes($"{{\n  \"table\": \"{Name}\",\n  \"rows\": [\n{string.Join(",\n", lines)}\n  ]\n}}\n");
        try
        {
            TableSchemas.Sounds.Apply(SoundMods.Base, JsonItem.Parse(bytes));
        }
        catch (JsonDataException e)
        {
            throw new InvalidDataException($"Table {Name}: {e.Message}", e);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return [relative];
    }

    public object?[]? NewRow(object?[] from, IReadOnlyList<object?[]> rows) => null;

    private string PathOf(string folder) => Path.Combine(folder, GameTables.PathOf(Name));

    private static object?[] Cells(SoundEventRow r) => [r.Sample, r.Volume, r.Range, r.Pitch, (long)r.Priority, r.Loop ? On : Off];

    private static string Json(GridColumn c, object? cell) =>
        c.Key == Loop ? (Num(cell) != Off ? "true" : "false") : TableGrid.JsonOf(cell).ToString();

    private static double Num(object? cell) => cell is null or string ? 0.0 : Convert.ToDouble(cell, CultureInfo.InvariantCulture);
}
