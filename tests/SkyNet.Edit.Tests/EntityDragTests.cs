using System.Numerics;
using RetroEng.Editor;
using SkyNet.Formats;
using SkyNet.Game.Behaviour;

namespace SkyNet.Edit.Tests;

/// <summary>A scripted gizmo drag (editor plan step E5): many pointer moves, one undo step, and the undo
/// gives back the very same layer.</summary>
public sealed class EntityDragTests
{
    private const string L = "MAP.210";
    /// <summary>DOS 11-bit angles: 2048 units a turn.</summary>
    private const int DosTurn = 2048;

    private static EditDocument NewDoc()
    {
        var data = GameData.Open(EditDocumentTests.DataDir!) ?? throw new InvalidOperationException("no game data");
        using var a = data.OpenMapArchive();
        var doc = new EditDocument();
        doc.AddLayer(L, 210, MapFile.Parse(a.Read("MAP.210")!)!);
        return doc;
    }

    private static MapEntity Prop(EditDocument doc) =>
        doc.Map(L).Entities.First(e => e.Variant == MapEntityVariant.Mesh && !e.IsActor);

    [MapDataFact]
    public void A_translate_drag_is_one_undo_step_that_restores_identical_layer_bytes()
    {
        var doc = NewDoc();
        var before = doc.Layer(L).ToBytes();
        var e = Prop(doc);
        var o = BehaviourModel.Position(e);
        PickRay At(float d) => new(o + new Vector3(d, 0f, 800f), -Vector3.UnitZ);
        var snap = new SnapSettings(Grid: true, GridSize: 64);
        var drag = EntityDrag.Begin(doc, L, e.FileOffset, GizmoMode.Translate, GizmoSpace.World, 0, At(10f), snap)!;
        foreach (var d in new[] { 20f, 90f, 150f, 300f, 333f })
        {
            drag.Update(At(d));
        }

        drag.End();
        Assert.Equal(1, doc.History.UndoCount);
        Assert.True(doc.Map(L).TryGetEntityAt(e.FileOffset, out var moved));
        Assert.Equal(Snap.Grid(e.X + 323, 64), moved.X);
        Assert.Equal((e.Y, e.Z), (moved.Y, moved.Z));

        // A second drag is a step of its own.
        var again = EntityDrag.Begin(doc, L, e.FileOffset, GizmoMode.Translate, GizmoSpace.World, 0, At(0f), snap)!;
        again.Update(At(200f));
        again.End();
        Assert.Equal(2, doc.History.UndoCount);

        Assert.True(doc.Undo());
        Assert.True(doc.Undo());
        Assert.Equal(before, doc.Layer(L).ToBytes());
        Assert.False(doc.IsDirty);
    }

    [MapDataFact]
    public void A_rotate_drag_snaps_the_yaw_to_the_step_and_undoes_in_one_step()
    {
        var doc = NewDoc();
        var before = doc.Layer(L).ToBytes();
        var e = Prop(doc);
        var o = BehaviourModel.Position(e);
        var (u, v) = TransformGizmo.PlaneBasis(Vector3.UnitY);
        PickRay At(float a) => new(o + (u * MathF.Cos(a) + v * MathF.Sin(a)) * 100f + new Vector3(0f, 500f, 0f), -Vector3.UnitY);
        var drag = EntityDrag.Begin(doc, L, e.FileOffset, GizmoMode.Rotate, GizmoSpace.World, 1, At(0f), new SnapSettings(Angle: true, AngleStep: 128))!;
        for (var i = 1; i <= 10; i++)
        {
            drag.Update(At(i * 0.1f));
        }

        drag.End();
        Assert.True(doc.Map(L).TryGetEntityAt(e.FileOffset, out var turned));
        Assert.Equal(Snap.Angle(e.Yaw + Snap.ToUnits(1.0f, DosTurn), 128, DosTurn), turned.Yaw);
        Assert.Equal(0, turned.Yaw % 128);
        Assert.Equal(1, doc.History.UndoCount);
        Assert.True(doc.Undo());
        Assert.Equal(before, doc.Layer(L).ToBytes());
    }

    [MapDataFact]
    public void A_drag_that_changes_nothing_records_nothing()
    {
        var doc = NewDoc();
        var e = Prop(doc);
        var o = BehaviourModel.Position(e);
        var drag = EntityDrag.Begin(doc, L, e.FileOffset, GizmoMode.Translate, GizmoSpace.World, 1, new PickRay(o + new Vector3(0f, 20f, 800f), -Vector3.UnitZ), default)!;
        drag.Update(new PickRay(o + new Vector3(0f, 20.2f, 800f), -Vector3.UnitZ));
        drag.End();
        Assert.Equal(0, doc.History.UndoCount);
    }
}
