using System.Numerics;
using SkyNet.Formats;

namespace SkyNet.Edit;

/// <summary>The Terrain mode's tools (Unreal's Landscape mode, cut down).</summary>
public enum TerrainTool
{
    Raise,
    Lower,
    Smooth,
    Flatten,
    Noise,
    Paint,
    Turn,
}

/// <summary>One application of the brush. <paramref name="Centre"/> is world (x, z); <paramref name="Radius"/>
/// world units; <paramref name="Falloff"/> how the weight fades to the rim (TerrainBrush.Weight).
/// <paramref name="Amount"/> per tool: Raise/Lower/Noise world units at the centre, Smooth/Flatten the
/// blend (0..1) toward the smoothed or <paramref name="Target"/> height, Paint/Turn 1.
/// <paramref name="Tile"/> is Paint's layer-2 byte (tile, turn), <paramref name="Seed"/> Noise's per-stroke seed.
/// Turn turns the tile of the cell under the centre a quarter.</summary>
public readonly record struct BrushDab(TerrainTool Tool, Vector2 Centre, float Radius, BrushFalloff Falloff, float Amount,
    float Target = 0f, int Tile = 0, int Seed = 0);

/// <summary>Corner heights (world units, Size × Size corners) not yet applied: an import or a generated field.</summary>
public sealed record HeightGrid(int Size, float[] Heights)
{
    public float At(int col, int row) => Heights[row * Size + col];
}

/// <summary>Generate…'s inputs: <paramref name="Roughness"/> 0 (rolling) .. 1 (jagged), heights in world units.</summary>
public readonly record struct GenerateSettings(int Seed, float Roughness, float MinHeight, float MaxHeight);

/// <summary>
/// What the Terrain mode needs of the terrain model (the WLD being edited). Kept narrow so the UI is
/// tested against a fake; the model owns the edits, their undo steps and the save.
/// <code>
///  SculptBrush ─ BeginStroke / Dab… / EndStroke ─► model ─► one undo step per stroke
///  TerrainModeView ◄─ TakeDirty (cells changed, undo/redo too) ─ model
///  TerrainActions ─ ImportPng / ImportMesh / Generate → HeightGrid → Apply ─► model
/// </code>
/// </summary>
public interface ITerrainModel
{
    /// <summary>Corners across (and down) the field; cells are one fewer.</summary>
    int Corners { get; }

    /// <summary>Corner height, world units (Y up).</summary>
    float Height(int col, int row);

    /// <summary>The cell's layer-2 byte (tile in bits 0-5, turn in 6-7).</summary>
    int Tile(int col, int row);

    /// <summary>The ground as the game loads it (drawing, picking, collision); null for a size it does not take.</summary>
    WldTerrain? Ground();

    /// <summary>Opens a stroke: its dabs are one undo step named <paramref name="label"/>.</summary>
    void BeginStroke(string label);

    void Dab(in BrushDab dab);

    void EndStroke();

    /// <summary>The cells changed since the last call (by strokes, applies, undo and redo).</summary>
    IReadOnlyList<GridRect> TakeDirty();

    /// <summary>A grayscale PNG's heights (throws IOException / InvalidDataException).</summary>
    HeightGrid ImportPng(string path);

    void ExportPng(string path);

    /// <summary>A glTF mesh's top surface as heights (throws IOException / InvalidDataException).</summary>
    HeightGrid ImportMesh(string path);

    HeightGrid Generate(GenerateSettings settings);

    /// <summary>Sets every corner height from <paramref name="grid"/>: one undo step.</summary>
    void Apply(HeightGrid grid, string label);

    /// <summary>The map number (WLD.NNN).</summary>
    int Map { get; }

    /// <summary>The WLD file's bytes when changed since loaded or saved; null when not.</summary>
    byte[]? Edited();

    /// <summary>Writes the terrain into the folder mod when it changed since loaded or saved; the path
    /// written relative to the mod, else null.</summary>
    string? Save(string modFolder);

    /// <summary>Writes the terrain into a scratch mod (play-test) when changed; the saved state stays.</summary>
    void WriteCopy(string modFolder);
}

/// <summary>The Terrain panel's file and generator commands over the model: an import or Generate…
/// gives a pending field shown as a preview, Apply puts it on the terrain (one undo step), Discard
/// drops it. Failures are kept in <see cref="Error"/> for the panel, not thrown.</summary>
public sealed class TerrainActions(Func<ITerrainModel?> model)
{
    public HeightGrid? Pending { get; private set; }
    public string? PendingLabel { get; private set; }
    public string? Error { get; private set; }

    public bool ImportPng(string path) => Stage(m => m.ImportPng(path), $"Import {Path.GetFileName(path)}");

    public bool ImportMesh(string path) => Stage(m => m.ImportMesh(path), $"Import {Path.GetFileName(path)}");

    public bool Generate(GenerateSettings settings) => Stage(m => m.Generate(settings), "Generate terrain");

    public bool ExportPng(string path) => Run(m =>
    {
        m.ExportPng(path);
        return true;
    });

    /// <summary>The pending field onto the terrain; false when none.</summary>
    public bool ApplyPending()
    {
        if (Pending is not { } grid || model() is not { } m)
        {
            return false;
        }

        m.Apply(grid, PendingLabel ?? "Apply heights");
        Discard();
        return true;
    }

    public void Discard() => (Pending, PendingLabel) = (null, null);

    // An operation that yields a field to preview.
    private bool Stage(Func<ITerrainModel, HeightGrid> make, string label) => Run(m =>
    {
        (Pending, PendingLabel) = (make(m), label);
        return true;
    });

    private bool Run(Func<ITerrainModel, bool> op)
    {
        Error = null;
        if (model() is not { } m)
        {
            Error = "No outdoor level shown";
            return false;
        }

        try
        {
            return op(m);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException)
        {
            Error = e.Message;
            return false;
        }
    }
}
