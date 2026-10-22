using System.Numerics;
using System.Text;
using RetroEng.Editor;
using SkyNet.Formats;
using SkyNet.Game.Baking;
using SkyNet.Game.Collision;

namespace SkyNet.Edit.Tests;

/// <summary>The selection's edits: duplicate and copy / paste with their links remapped (on the same map
/// and onto another, which gets the missing name slots), drop to floor onto the collision, align and
/// distribute; each one undo step, round-tripped through the layer file.</summary>
public sealed class ArrangeTests
{
    private const string A = "MAP.210", B = "MAP.214";

    private static readonly Dictionary<int, MapFile> Raw = [];

    private static MapFile RawMap(int number)
    {
        lock (Raw)
        {
            if (!Raw.TryGetValue(number, out var map))
            {
                var data = GameData.Open(EditDocumentTests.DataDir!)!;
                using var a = data.OpenMapArchive();
                Raw[number] = map = MapFile.Parse(GameData.MapBytes(a, number)!)!;
            }

            return map;
        }
    }

    private static EditDocument NewDoc()
    {
        var doc = new EditDocument();
        doc.AddLayer(A, 210, RawMap(210));
        doc.AddLayer(B, 214, RawMap(214));
        return doc;
    }

    private static string Snapshot(EditDocument doc)
    {
        var s = new StringBuilder();
        foreach (var layer in new[] { A, B })
        {
            s.Append(Convert.ToHexString(doc.Layer(layer).ToBytes())).Append('\n').AppendJoin('|', doc.Map(layer).Names).Append('\n');
            foreach (var e in doc.Map(layer).Entities)
            {
                s.Append(EditDocument.EntityText(e)).Append('\n');
            }
        }

        return s.ToString();
    }

    // The layer written and read back gives the same map.
    private static void RoundTrips(EditDocument doc, string layer, int number)
    {
        var back = SkyNet.Game.Mods.MapLayer.Parse(doc.Layer(layer).ToBytes());
        Assert.Equal(doc.Map(layer).Entities.Select(EditDocument.EntityText), back.Apply(number, RawMap(number)).Entities.Select(EditDocument.EntityText));
    }

    // A chain A -> B of MAP.210 (both linked onwards nowhere else) and an entity linked to one outside the copy.
    private static (MapEntity First, MapEntity Second, MapEntity Outside) Linked(MapFile map)
    {
        var first = map.Entities.First(e => e.LinkNext > 0 && e.LinkNext != e.FileOffset && map.TryGetEntityAt(e.LinkNext, out _));
        map.TryGetEntityAt(first.LinkNext, out var second);
        var outside = map.Entities.First(e => e.LinkNext > 0 && e.LinkNext != e.FileOffset && e.FileOffset != first.FileOffset
            && e.FileOffset != second!.FileOffset && e.LinkNext != first.FileOffset && e.LinkNext != second.FileOffset && map.TryGetEntityAt(e.LinkNext, out _));
        return (first, second!, outside);
    }

    [MapDataFact]
    public void Duplicate_keeps_the_layout_remaps_inner_links_and_is_one_step()
    {
        var doc = NewDoc();
        var before = Snapshot(doc);
        var (first, second, outside) = Linked(doc.Map(A));
        int[] keys = [first.FileOffset, second.FileOffset, outside.FileOffset];

        var copies = EntityClipboard.Duplicate(doc, A, keys, (64, 0, -32));
        Assert.Equal(1, doc.History.UndoCount);
        Assert.Equal(3, copies.Count);
        var map = doc.Map(A);
        var c = copies.Select(k => { map.TryGetEntityAt(k, out var e); return e!; }).ToList();
        for (var i = 0; i < 3; i++)
        {
            var o = new[] { first, second, outside }[i];
            Assert.Equal((o.X + 64, o.Y, o.Z - 32, o.Variant, o.NameIndex, o.ActType), (c[i].X, c[i].Y, c[i].Z, c[i].Variant, c[i].NameIndex, c[i].ActType));
        }

        // A' -> B' (not B); the outside link stays on the same layer.
        Assert.Equal(copies[1], c[0].LinkNext);
        Assert.Equal(outside.LinkNext, c[2].LinkNext);
        RoundTrips(doc, A, 210);

        var after = Snapshot(doc);
        Assert.True(doc.Undo());
        Assert.Equal(before, Snapshot(doc));
        Assert.True(doc.Redo());
        Assert.Equal(after, Snapshot(doc));
    }

    [MapDataFact]
    public void Paste_onto_another_map_adds_missing_names_and_drops_outside_links()
    {
        var doc = NewDoc();
        var before = Snapshot(doc);
        var (first, second, outside) = Linked(doc.Map(A));
        var a = doc.Map(A);
        var b = doc.Map(B);
        // A mesh whose model MAP.214 does not name.
        var mesh = a.Entities.First(e => e.Variant == MapEntityVariant.Mesh && !e.IsActor && e.NameIndex >= 0 && e.NameIndex < a.Names.Length
            && a.Names[e.NameIndex].Length > 0 && !b.Names.Contains(a.Names[e.NameIndex]));
        var name = a.Names[mesh.NameIndex];

        var clip = EntityClipboard.Copy(doc, A, [first.FileOffset, second.FileOffset, outside.FileOffset, mesh.FileOffset])!;
        var target = new Vector3(4000f, -100f, -4000f);
        var keys = clip.Paste(doc, B, clip.MoveTo(target));
        Assert.Equal(1, doc.History.UndoCount);
        Assert.True(doc.Layer(A).IsEmpty);

        var map = doc.Map(B);
        var c = keys.Select(k => { map.TryGetEntityAt(k, out var e); return e!; }).ToList();
        Assert.Equal(keys[1], c[0].LinkNext);
        Assert.Equal(0, c[2].LinkNext);
        Assert.Equal(name, map.Names[c[3].NameIndex]);
        var added = clip.Items.Select(i => i.Name).OfType<string>().Distinct().Count(n => !b.Names.Contains(n));
        Assert.Equal(b.Names.Length + added, map.Names.Length);
        Assert.Equal((mesh.HitPoints, mesh.StateByte, mesh.ActType), (c[3].HitPoints, c[3].StateByte, c[3].ActType));

        // The layout kept: offsets between the copies are the originals'; the anchor on the target.
        Assert.Equal((first.X - mesh.X, first.Z - mesh.Z), (c[0].X - c[3].X, c[0].Z - c[3].Z));
        var anchor = EntityClipboard.Copy(doc, B, keys)!.Anchor;
        Assert.Equal((4000, 100, 4000), anchor);
        RoundTrips(doc, B, 214);

        var after = Snapshot(doc);
        Assert.True(doc.Undo());
        Assert.Equal(before, Snapshot(doc));
        Assert.True(doc.Redo());
        Assert.Equal(after, Snapshot(doc));
    }

    [MapDataFact]
    public void Cut_deletes_in_one_step_and_pastes_back_in_place()
    {
        var doc = NewDoc();
        var (first, second, _) = Linked(doc.Map(A));
        var clip = EntityClipboard.Cut(doc, A, [first.FileOffset, second.FileOffset])!;
        Assert.False(doc.Map(A).TryGetEntityAt(first.FileOffset, out _));
        Assert.Equal(1, doc.History.UndoCount);
        var keys = clip.Paste(doc, A, (0, 0, 0));
        Assert.True(doc.Map(A).TryGetEntityAt(keys[0], out var p));
        Assert.Equal((first.X, first.Y, first.Z, keys[1]), (p.X, p.Y, p.Z, p.LinkNext));
        Assert.Equal(2, doc.History.UndoCount);
    }

    [MapDataFact]
    public void Alt_drag_duplicate_and_its_moves_are_one_step()
    {
        var doc = NewDoc();
        var before = Snapshot(doc);
        var e = doc.Map(A).Entities.First(x => x.Variant == MapEntityVariant.Mesh);
        var f = doc.Map(A).Entities.First(x => x.Variant == MapEntityVariant.Sprite);
        var copies = EntityClipboard.Duplicate(doc, A, [e.FileOffset, f.FileOffset], (0, 0, 0), DuplicateStep.Drag);
        for (var step = 1; step <= 5; step++)
        {
            EntityArrange.Move(doc, A, new Dictionary<int, (int, int, int)>
            {
                [copies[0]] = (e.X + step * 10, e.Y, e.Z), [copies[1]] = (f.X + step * 10, f.Y, f.Z),
            });
        }

        doc.EndDrag();
        Assert.Equal(1, doc.History.UndoCount);
        Assert.True(doc.Map(A).TryGetEntityAt(copies[1], out var moved));
        Assert.Equal(f.X + 50, moved.X);
        Assert.True(doc.Undo());
        Assert.Equal(before, Snapshot(doc));
    }

    [Fact]
    public void Align_and_distribute_maths()
    {
        Assert.Equal([0, 0, 0], EntityArrange.Aligned([0, 7, 100], AlignTo.Min));
        Assert.Equal([50, 50, 50], EntityArrange.Aligned([0, 7, 100], AlignTo.Centre));
        Assert.Equal([100, 100, 100], EntityArrange.Aligned([0, 7, 100], AlignTo.Max));
        Assert.Equal([0, 33, 67, 100], EntityArrange.Distributed([0, 10, 90, 100]));
        // Unsorted: each keeps its rank.
        Assert.Equal([100, 0, 50], EntityArrange.Distributed([100, -0, 20]));
        Assert.Equal([5, 1], EntityArrange.Distributed([5, 1]));
    }

    [MapDataFact]
    public void Align_distribute_snap_set_world_axes_in_one_step_each()
    {
        var doc = NewDoc();
        var keys = doc.Map(A).Entities.Where(e => e.Variant == MapEntityVariant.Sprite).Take(4).Select(e => e.FileOffset).ToList();
        int[] Ys() => [.. keys.Select(k => { doc.Map(A).TryGetEntityAt(k, out var e); return -e!.Y; })];
        int[] Xs() => [.. keys.Select(k => { doc.Map(A).TryGetEntityAt(k, out var e); return e!.X; })];
        var lowest = Ys().Min();

        Assert.True(EntityArrange.Align(doc, A, keys, ArrangeAxis.Y, AlignTo.Min));
        Assert.All(Ys(), y => Assert.Equal(lowest, y));
        var xs = Xs();
        Assert.True(EntityArrange.Distribute(doc, A, keys, ArrangeAxis.X));
        Assert.Equal(EntityArrange.Distributed(xs), Xs());
        Assert.True(EntityArrange.SnapToGrid(doc, A, keys, 64));
        Assert.All(Xs(), x => Assert.Equal(0, x % 64));
        Assert.Equal(3, doc.History.UndoCount);
        RoundTrips(doc, A, 210);
        Assert.True(doc.Undo() && doc.Undo() && doc.Undo());
        Assert.True(doc.Layer(A).IsEmpty);
    }

    [MapDataFact]
    public void Drop_to_floor_lands_on_the_collision_height()
    {
        var data = GameData.Open(EditDocumentTests.DataDir!)!;
        var raw = RawMap(214);
        var block = MapBaker.Bake(data, 214)!;
        var picking = new EntityPicking(raw, block);
        var doc = NewDoc();
        const int Raise = 300;

        float Foot(MapEntity e) => e.Variant == MapEntityVariant.Mesh && picking.TryGet(e.FileOffset, out var t) && !t.Triangles.IsEmpty
            ? -raw.Entities.First(x => x.FileOffset == e.FileOffset).Y - t.Min.Y
            : EntityArrange.Lift((int)e.Variant);

        // A pickup and a static prop, each raised, then dropped.
        var sprite = raw.Entities.First(e => e.Variant == MapEntityVariant.Sprite && !e.IsMarker
            && Snap.Floor(new Vector3(e.X, -e.Y + Raise - Foot(e), -e.Z), picking.Targets, e.FileOffset) != null);
        var mesh = raw.Entities.First(e => e.Variant == MapEntityVariant.Mesh && !e.IsActor && picking.TryGet(e.FileOffset, out var t) && !t.Triangles.IsEmpty
            && Snap.Floor(new Vector3(e.X, -e.Y - Foot(e), -e.Z), picking.Targets, e.FileOffset) is { } y && y < -e.Y - Foot(e) - 1f);
        foreach (var e in new[] { sprite, mesh })
        {
            doc.SetFields(B, e.FileOffset, new Dictionary<string, int> { ["y"] = e.Y - Raise });
            doc.EndDrag();
        }

        Assert.True(EntityArrange.Drop(doc, B, [sprite.FileOffset, mesh.FileOffset],
            (e, feet) => Snap.Floor(feet, picking.Targets, e.FileOffset), Foot));
        Assert.Equal(3, doc.History.UndoCount);

        foreach (var e in new[] { sprite, mesh })
        {
            var world = LevelCollision.Build(data, raw, null, x => x.FileOffset == e.FileOffset).World;
            var foot = Foot(e);
            var from = new Vector3(e.X, -(e.Y - Raise) - foot + 32f, -e.Z);
            Assert.True(world.Raycast(from, -Vector3.UnitY, 10000f, out var hit));
            Assert.True(doc.Map(B).TryGetEntityAt(e.FileOffset, out var dropped));
            Assert.Equal(-(int)MathF.Round(hit.Point.Y + foot), dropped.Y);
            Assert.Equal((e.X, e.Z), (dropped.X, dropped.Z));
        }
    }
}
