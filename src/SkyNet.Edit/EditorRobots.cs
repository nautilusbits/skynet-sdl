using RetroEng.Render;
using SkyNet.Formats;
using SkyNet.Game;
using SkyNet.Game.Baking;
using SkyNet.Game.Collision;
using SkyNet.Game.Enemies;

namespace SkyNet.Edit;

/// <summary>
/// The shown level's robots, placed and drawn by the game's own code: <see cref="Robots.Populate"/>
/// stands each on its floor (<see cref="Robots.StandOnFloor"/>), <see cref="Enemy.Settle"/> does the
/// first tick's walker snap, <see cref="EnemyRenderer"/> draws them. Never ticked: they stay as the
/// game shows them on entering the map.
/// <code>
///   MAP + block ─► collision world ─► Robots.Populate ─► Settle ─► EnemyRenderer.Draw (each frame)
/// </code>
/// The level is loaded with <c>staticRobots: false</c>: the bake's robots stand at their markers.
/// </summary>
public sealed class EditorRobots(Robots robots, EnemyRenderer view)
{
    public Robots Robots => robots;

    /// <summary>The robots of <paramref name="level"/>, drawn into <paramref name="renderer"/>.</summary>
    public static EditorRobots For(Renderer renderer, GameData data, Level level) =>
        new(Place(data, level.Map, level.Block, level.Wld), new EnemyRenderer(renderer, new TextureStore(renderer, data), level.Palette, level));

    /// <summary>The robots of <paramref name="map"/> where the game has them after its first tick.</summary>
    public static Robots Place(GameData data, MapFile map, MapBlock block, WldTerrain? wld)
    {
        var world = LevelCollision.FromBlock(block, map, wld).World;
        var placed = new Robots(SkyNet.Data.GameTables.Ais, new EnemyModels(data), world);
        placed.Populate(map);
        foreach (var r in placed.All)
        {
            r.Settle();
            r.UpdateTransforms();
        }

        return placed;
    }

    public void Draw(long nowMs) => view.Draw(robots, nowMs);
}
