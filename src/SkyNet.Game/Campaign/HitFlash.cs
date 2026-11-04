using SkyNet.Formats;

namespace SkyNet.Game.Campaign;

/// <summary>
/// The red flash of a hit the soldier lives through (SKYNET.EXE v1.01): the hit sets bit 1 of
/// [0x5f17a] (FUN_00122b52), and every frame FUN_0013d6e3 remaps the 3D view through row
/// <c>round(level)</c> of SHADE.003, then adds 40 rows a second to the level (16.16, × the frame's
/// seconds [0x43200]). Past row 7 the flash ends and the level goes back to 4, so a map's first
/// flash runs rows 0-7 (175 ms) and every later one rows 4-7 (75 ms). Row 0 turns every colour
/// red, row 7 leaves the view almost as drawn.
/// <code>
///  level  0 ─1─2─3─ 4 ─5─6─ 7 │ end → level 4
///  row    all red  …  faint   │ (map entry: level 0)
/// </code>
/// </summary>
public sealed class HitFlash
{
    /// <summary>SkyNET's flash table: 8 rows of 256 palette indices.</summary>
    public const string TableFile = "SHADE.003";
    public const int Rows = 8;

    // 0x280000 per second in 16.16 rows.
    private const float RowsPerSecond = 40f;
    // The last row drawn; past it the flash ends.
    private const float LastRow = Rows - 1;
    // Where the next flash starts once one ended.
    private const float RestartRow = 4f;

    private float _level;

    /// <summary>SHADE.003, <see cref="Rows"/> × 256 bytes; Future Shock ships none and SKYNET.EXE
    /// reads SkyNET's, so SkyNET's copy there. Null when absent or not that size.</summary>
    public static byte[]? LoadTable(GameData data)
    {
        var path = data.FrontPath(TableFile);
        if (!File.Exists(path))
        {
            return null;
        }

        var table = File.ReadAllBytes(path);
        return table.Length == Rows * Palette.Colors ? table : null;
    }

    /// <summary>A flash runs.</summary>
    public bool Active { get; private set; }

    /// <summary>The SHADE.003 row the view goes through now; null: no flash.</summary>
    public int? Row => Active ? (int)MathF.Floor(_level + 0.5f) : null;

    /// <summary>How red the view is, 1 (row 0, all red) to 0 (row 7, as drawn): the enhanced look's
    /// overlay.</summary>
    public float Strength => Row is { } row ? 1f - row / LastRow : 0f;

    /// <summary>A hit the soldier lived through; a running flash runs on.</summary>
    public void Start() => Active = true;

    /// <summary>The frame's step, after its row was drawn.</summary>
    public void Tick(float seconds)
    {
        if (!Active)
        {
            return;
        }

        _level += RowsPerSecond * seconds;
        if (_level <= LastRow)
        {
            return;
        }

        _level = RestartRow;
        Active = false;
    }

    /// <summary>Map entry (FUN_0013d600): no flash, the next starts at row 0.</summary>
    public void Reset()
    {
        _level = 0f;
        Active = false;
    }
}
