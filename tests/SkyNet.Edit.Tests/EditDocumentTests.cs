using System.Text;
using SkyNet.Formats;
using SkyNet.Game.Mods;

namespace SkyNet.Edit.Tests;

/// <summary>A fact skipped unless SKYNET_DATA points at SkyNET's data folder.</summary>
public sealed class MapDataFactAttribute : FactAttribute
{
    public MapDataFactAttribute()
    {
        if (EditDocumentTests.DataDir == null)
        {
            Skip = "SKYNET_DATA is not set to a SkyNET data folder";
        }
    }
}

/// <summary>The editor's document (editor plan step E4): commands, undo and redo, merge and the save
/// point, on MAP.210 as parsed.</summary>
public sealed class EditDocumentTests
{
    private const string L = "MAP.210";

    public static string? DataDir
    {
        get
        {
            var d = Environment.GetEnvironmentVariable("SKYNET_DATA");
            return !string.IsNullOrWhiteSpace(d) && GameDataLocator.GameOf(d) == GameKind.SkyNet ? d : null;
        }
    }

    private static MapFile? _raw;

    private static MapFile Raw
    {
        get
        {
            if (_raw == null)
            {
                var data = GameData.Open(DataDir!) ?? throw new InvalidOperationException("no game data");
                using var a = data.OpenMapArchive();
                _raw = MapFile.Parse(a.Read("MAP.210")!)!;
            }

            return _raw;
        }
    }

    private static EditDocument NewDoc()
    {
        var doc = new EditDocument();
        doc.AddLayer(L, 210, Raw);
        return doc;
    }

    /// <summary>The written layer file and the map it builds (names and every entity), as one string.</summary>
    private static string Snapshot(EditDocument doc)
    {
        var s = new StringBuilder(Convert.ToHexString(doc.Layer(L).ToBytes())).Append('\n');
        var map = doc.Map(L);
        s.AppendJoin('|', map.Names).Append('\n');
        foreach (var e in map.Entities)
        {
            s.Append(EditDocument.EntityText(e)).Append('\n');
        }

        return s.ToString();
    }

    [MapDataFact]
    public void Random_do_undo_redo_sequences_return_to_identical_layers()
    {
        for (var seed = 1; seed <= 6; seed++)
        {
            var rng = new Random(seed);
            var doc = NewDoc();
            var states = new List<string> { Snapshot(doc) };
            for (var step = 0; step < 150; step++)
            {
                var map = doc.Map(L);
                var keys = map.Entities.Select(e => e.FileOffset).ToArray();
                var key = keys[rng.Next(keys.Length)];
                var before = Snapshot(doc);
                var depth = doc.History.UndoCount;
                try
                {
                    switch (rng.Next(10))
                    {
                        case 0:
                        case 1:
                        case 2:
                            var field = new[] { "x", "y", "z", "yaw", "hitPoints", "state" }[rng.Next(6)];
                            doc.SetField(L, key, field, field switch
                            {
                                "yaw" => rng.Next(2048),
                                "hitPoints" => rng.Next(65536),
                                "state" => rng.Next(256),
                                _ => rng.Next(-20000, 20000),
                            });
                            break;
                        case 3:
                            doc.EditLink(L, key, act: rng.Next(256), next: keys[rng.Next(keys.Length)]);
                            break;
                        case 4:
                            doc.AddEntity(L, new Dictionary<string, int> { ["variant"] = 3, ["sprite"] = rng.Next(40000), ["x"] = rng.Next(20000), ["z"] = rng.Next(20000) },
                                rng.Next(3) == 0 ? $"NEW{rng.Next(4)}" : null);
                            break;
                        case 5:
                            doc.DeleteEntity(L, key);
                            break;
                        case 6:
                        case 7:
                            if (doc.Undo())
                            {
                                Assert.Equal(states[doc.History.UndoCount], Snapshot(doc));
                            }

                            continue;
                        case 8:
                            if (doc.Redo())
                            {
                                Assert.Equal(states[doc.History.UndoCount], Snapshot(doc));
                            }

                            continue;
                        default:
                            doc.EndDrag();
                            continue;
                    }
                }
                catch (MapLayerException)
                {
                    Assert.Equal(before, Snapshot(doc));
                    Assert.Equal(depth, doc.History.UndoCount);
                    continue;
                }

                states.RemoveRange(doc.History.UndoCount, states.Count - doc.History.UndoCount);
                states.Add(Snapshot(doc));
                var parsed = MapLayer.Parse(doc.Layer(L).ToBytes()).Apply(210, Raw);
                Assert.Equal(doc.Map(L).Entities.Select(EditDocument.EntityText), parsed.Entities.Select(EditDocument.EntityText));
            }

            var end = Snapshot(doc);
            var top = doc.History.UndoCount;
            while (doc.Undo())
            {
                Assert.Equal(states[doc.History.UndoCount], Snapshot(doc));
            }

            Assert.Equal(states[0], Snapshot(doc));
            Assert.True(doc.Layer(L).IsEmpty);
            while (doc.Redo())
            {
            }

            Assert.Equal(top, doc.History.UndoCount);
            Assert.Equal(end, Snapshot(doc));
        }
    }

    [MapDataFact]
    public void A_drag_of_one_field_is_one_step_and_one_edit()
    {
        var doc = NewDoc();
        var key = Raw.Entities[5].FileOffset;
        for (var x = 0; x < 10; x++)
        {
            doc.SetField(L, key, "x", 100 * x);
        }

        Assert.Equal(1, doc.History.UndoCount);
        Assert.Single(doc.Layer(L).Edits);
        Assert.Equal(900, doc.Layer(L).Edits[0].Fields["x"]);
        doc.SetField(L, key, "z", 7);
        Assert.Equal(2, doc.History.UndoCount);
        Assert.Single(doc.Layer(L).Edits);
        doc.EndDrag();
        doc.SetField(L, key, "z", 8);
        Assert.Equal(3, doc.History.UndoCount);
        Assert.True(doc.Undo());
        Assert.Equal(7, doc.Layer(L).Edits[0].Fields["z"]);
        Assert.True(doc.Undo());
        Assert.False(doc.Layer(L).Edits[0].Fields.ContainsKey("z"));
        Assert.True(doc.Undo());
        Assert.True(doc.Layer(L).IsEmpty);
        Assert.Same(Raw, doc.Map(L));
    }

    [MapDataFact]
    public void Dirty_follows_the_save_point_and_adds_and_deletes_undo()
    {
        var doc = NewDoc();
        Assert.False(doc.IsDirty);
        var added = doc.AddEntity(L, new Dictionary<string, int> { ["variant"] = 3, ["sprite"] = 300 }, "CRATE04");
        Assert.True(added >= MapFile.FirstAddedKey);
        Assert.True(doc.Map(L).TryGetEntityAt(added, out _));
        doc.MarkSaved();
        Assert.False(doc.IsDirty);
        var saved = Snapshot(doc);
        var victim = Raw.Entities[0].FileOffset;
        doc.SetField(L, victim, "y", 1);
        doc.DeleteEntity(L, victim);
        Assert.False(doc.Map(L).TryGetEntityAt(victim, out _));
        Assert.Equal(MapEditOp.Delete, doc.Layer(L).Edits[^1].Op);
        Assert.DoesNotContain(doc.Layer(L).Edits, e => e.Key == victim && e.Op == MapEditOp.Set);
        Assert.True(doc.IsDirty);
        doc.Undo();
        doc.Undo();
        Assert.False(doc.IsDirty);
        Assert.Equal(saved, Snapshot(doc));
        doc.DeleteEntity(L, added);
        Assert.DoesNotContain(doc.Layer(L).Edits, e => e.Key == added);
        Assert.True(doc.IsDirty);
        doc.Undo();
        Assert.False(doc.IsDirty);
        doc.Undo();
        Assert.True(doc.IsDirty);
        Assert.True(doc.Layer(L).IsEmpty);
        Assert.Throws<MapLayerException>(() => doc.SetField(L, victim, "nope", 1));
        Assert.Throws<MapLayerException>(() => doc.SetField(L, 1, "x", 1));
        Assert.Equal(0, doc.History.UndoCount);
    }
}
