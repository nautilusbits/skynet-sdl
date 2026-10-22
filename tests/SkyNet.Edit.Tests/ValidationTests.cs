using SkyNet.Formats;
using SkyNet.Game.Behaviour;
using SkyNet.Game.Campaign;
using SkyNet.Game.Enemies;
using Xunit.Abstractions;

namespace SkyNet.Edit.Tests;

/// <summary>The Validation panel's rows (editor plan step E10): every unmodified SkyNET mission has no errors,
/// and seeded broken layers (a dangling next, an exit to a map the mission lacks, an arrival set the target
/// lacks (a warning: the game falls back to its start), a floating robot, a second player start) each give their row; the trigger graph diff names the
/// chains an edit changes.</summary>
[Collection(EditModsCollection.Name)]
public sealed class ValidationTests(ITestOutputHelper output)
{
    private const string Layer = "MAP.210";

    [MapDataFact]
    public void Unmodified_missions_have_no_errors()
    {
        var data = GameData.Open(EditDocumentTests.DataDir!)!;
        var validator = new Validator(data);
        foreach (var mission in MissionGenerator.BuildAll(data))
        {
            var rows = validator.Run(EditDocument.Load(data, mission, null));
            foreach (var r in rows)
            {
                output.WriteLine($"M{mission.Number:D2} {r.Level} {r.Code} {r.Instance} {r.Entity} {r.Text}");
            }

            Assert.DoesNotContain(rows, r => r.Level == ValidationLevel.Error);
            Assert.DoesNotContain(rows, r => r.Code == "graph");
        }
    }

    [MapDataFact]
    public void Seeded_problems_give_their_rows()
    {
        var data = GameData.Open(EditDocumentTests.DataDir!)!;
        var mission = MissionGenerator.Build(data, MissionTable.For(data.Game).ByNumber(1)!.Value);
        var doc = EditDocument.Load(data, mission, null);
        var instance = mission.World.Instances.First(i => i.Layer == Layer).Id;
        var map = doc.Map(Layer);
        var validator = new Validator(data);

        // A dangling next: the graph diff names the changed chain too.
        var mesh = map.Entities.First(e => e.Variant == MapEntityVariant.Mesh && e.LinkNext <= 0 && e.ActType == 0);
        doc.EditLink(Layer, mesh.FileOffset, act: 0x3F, next: 0x7FFF0);
        // An exit with no mission link to a map the mission lacks.
        var exit = map.Entities.First(e => e.Variant == MapEntityVariant.Mesh && e.LinkNext <= 0 && e.ActType == 0 && e.FileOffset != mesh.FileOffset);
        doc.SetFields(Layer, exit.FileOffset, new Dictionary<string, int> { ["act"] = ActRules.Exit, ["param16"] = 999, ["param8"] = 1 });
        // An act the act table does not know.
        var odd = map.Entities.First(e => e.Variant == MapEntityVariant.Mesh && e.LinkNext <= 0 && e.ActType == 0 && e.FileOffset != mesh.FileOffset && e.FileOffset != exit.FileOffset);
        doc.EditLink(Layer, odd.FileOffset, act: 0x1A);
        // An exit link to an arrival set its target lacks.
        var link = mission.World.Links.First(l => l.To != null);
        doc.SetMission(MissionEdits.SetExit(doc.Mission!, link.From, link.Source, link.To, false, 77), "Exit arrival");
        // A robot lifted off its floor.
        var robot = map.Entities.First(e => e.MarkerType == 2 && e.EnemyType >= 0
            && SkyNet.Data.GameTables.Ais.Types.FirstOrDefault(t => t.Index == e.EnemyType) is { } t && !Enemy.FlyingNames.Contains(t.Name) && t.State != 11);
        doc.SetField(Layer, robot.FileOffset, "y", robot.Y - 3000);
        // A second player start.
        var start = map.Entities.First(e => e.Variant == MapEntityVariant.Sprite && e.MarkerType == 0);
        var second = doc.AddEntity(Layer, new Dictionary<string, int> { ["variant"] = 3, ["sprite"] = start.SpriteIndex, ["x"] = start.X + 200, ["y"] = start.Y, ["z"] = start.Z });

        var rows = validator.Run(doc);
        foreach (var r in rows)
        {
            output.WriteLine($"{r.Level} {r.Code} {r.Instance} {r.Entity} {r.Text}");
        }

        void Has(ValidationLevel level, string code, string inst, int key) =>
            Assert.Contains(rows, r => r.Level == level && r.Code == code && r.Instance == inst && r.Entity == key);
        Has(ValidationLevel.Error, Validator.DanglingNext, instance, mesh.FileOffset);
        Has(ValidationLevel.Error, Validator.ExitTarget, instance, exit.FileOffset);
        Has(ValidationLevel.Error, Validator.UnknownAct, instance, odd.FileOffset);
        Has(ValidationLevel.Warning, Validator.Arrival, link.From, link.Source);
        Has(ValidationLevel.Warning, Validator.Floating, instance, robot.FileOffset);
        Has(ValidationLevel.Error, Validator.DuplicateStart, instance, second);
        Has(ValidationLevel.Info, "graph", instance, mesh.FileOffset);

        var changes = Validator.GraphDiff(Layer, doc.BaseMap(Layer), doc.Map(Layer));
        Assert.Contains(changes, c => c.Head == mesh.FileOffset && c.Keys.Contains(mesh.FileOffset));
        Assert.Contains(changes, c => c.Keys.Contains(exit.FileOffset));

        // Undone, the layer is clean again.
        while (doc.Undo())
        {
        }

        Assert.DoesNotContain(validator.Run(doc), r => r.Level == ValidationLevel.Error);
    }
}
