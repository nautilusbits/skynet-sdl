using System.Drawing;
using RetroEng.Physics;
using SkyNet.Formats;
using SkyNet.Game.Collision;

namespace SkyNet.Edit;

/// <summary>
/// The shown level's ground as one collision field that follows the terrain edits: a changed rect
/// rewrites only its corners (<see cref="HeightField.SetCorners"/>), so picks, floor snaps and drops
/// stand on the ground a stroke is shaping, dab by dab, with no rebuild.
/// <code>
///  model.TakeDirty (cells, grown by one) ─► corners col..endCol, row..endRow ─► SetCorners
/// </code>
/// </summary>
public sealed class LiveGround(HeightField ground)
{
    public HeightField Field => ground;

    /// <summary>The field of <paramref name="wld"/> as the game builds it (<see cref="LevelCollision.Ground"/>).</summary>
    public static LiveGround Of(WldTerrain wld) => new(LevelCollision.Ground(wld));

    /// <summary>The corners of <paramref name="cells"/> (a cell's four corners each) from the model.</summary>
    public void Apply(ITerrainModel model, GridRect cells)
    {
        var col0 = Math.Max(0, cells.Col);
        var row0 = Math.Max(0, cells.Row);
        var col1 = Math.Min(ground.CornersX, cells.EndCol + 1);
        var row1 = Math.Min(ground.CornersZ, cells.EndRow + 1);
        if (col1 <= col0 || row1 <= row0)
        {
            return;
        }

        var heights = new float[(col1 - col0) * (row1 - row0)];
        var i = 0;
        for (var row = row0; row < row1; row++)
        {
            for (var col = col0; col < col1; col++)
            {
                heights[i++] = model.Height(col, row);
            }
        }

        ground.SetCorners(new Rectangle(col0, row0, col1 - col0, row1 - row0), heights);
    }
}
