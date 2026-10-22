using System.Numerics;
using SkyNet.Formats;
using SkyNet.Game.Baking;
using SkyNet.Game.Campaign;

namespace SkyNet.Edit.Tests;

/// <summary>The editor's robots stand where the game's do (a turret's base on the floor, not in it).</summary>
public sealed class EditorRobotsTests
{
    // One game tick may turn or step a walker a little; standing height is what differs when wrong.
    private const float Slack = 2f;

    [Theory]
    [InlineData(1, 210)]
    [InlineData(1, 214)]
    [InlineData(1, 217)]
    [InlineData(2, 220)]
    public void Editor_robots_stand_where_the_game_puts_them(int mission, int number)
    {
        if (EditDocumentTests.DataDir == null)
        {
            return;
        }

        var data = GameData.Open(EditDocumentTests.DataDir)!;
        var session = new GameSession(data);
        session.StartMission(session.Table.ByNumber(mission)!.Value);
        Assert.True(session.EnterMap(number, 0));
        // The first tick stands the walkers (Enemy.Settle); a flyer or a path machine it moves off at once:
        // those are compared where they start.
        var starts = session.Robots!.All.Where(r => r.OnPath || r.IsFlying).ToDictionary(r => r.MarkerOffset, r => r.Root.World.Translation);
        session.Tick(default, default, default, false);
        var game = session.Robots!.All.Where(r => r.MarkerOffset >= 0 && !r.Hidden).ToDictionary(r => r.MarkerOffset);

        var map = data.LoadMap(number)!;
        var block = MapBaker.Bake(data, number)!;
        var editor = EditorRobots.Place(data, map, block, map.IsOutdoor ? data.LoadWld(number) : null);
        var compared = 0;
        foreach (var r in editor.All.Where(r => r.MarkerOffset >= 0 && !r.Hidden))
        {
            Assert.True(game.TryGetValue(r.MarkerOffset, out var g), $"MAP.{number} robot @{r.MarkerOffset} not in the game");
            r.UpdateTransforms();
            g.UpdateTransforms();
            var (at, want) = (r.Root.World.Translation, starts.GetValueOrDefault(r.MarkerOffset, g.Root.World.Translation));
            Assert.True(Vector3.Distance(at, want) <= Slack, $"MAP.{number} {r.Name} @{r.MarkerOffset}: editor {at}, game {want}");
            compared++;
        }

        Assert.True(compared > 0, $"MAP.{number}: no robots compared");
    }
}
