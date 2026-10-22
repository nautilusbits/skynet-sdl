using System.Numerics;
using SkyNet.Formats;
using SkyNet.Game.Behaviour;
using SkyNet.Game.Campaign;
using SkyNet.Game.Mods;
using SkyNet.Verify;

namespace SkyNet.Edit.Tests;

/// <summary>The trigger graph (editor plan step T8): MAP.210's gate lever chain and Future Shock MAP.013's
/// switch as nodes and edges, edits as undo steps through the layer file, the checks, the verify hook
/// against <c>SkyNet.Verify</c>, and the node layout file.</summary>
[Collection(EditModsCollection.Name)]
public sealed class TriggerGraphTests : IDisposable
{
    /// <summary>A fact skipped unless Future Shock's data sits beside SKYNET_DATA's.</summary>
    private sealed class ShockDataFactAttribute : FactAttribute
    {
        public ShockDataFactAttribute()
        {
            if (EditDocumentTests.DataDir is not { } dir || GameDataLocator.OtherGameDir(dir) == null)
            {
                Skip = "no Future Shock data beside SKYNET_DATA's SkyNET folder";
            }
        }
    }

    private const string L = "MAP.210";
    // MAP.210: the canyon's lever (LEVHAN01, 0xF1) and the 0xF2 behind the gate both lead into the gate's
    // leaves (BIGDOOR), its sound, the truck-box hint and a voice line.
    private const int Lever = 0x077f3, Behind = 0x07851, LeafA = 0x078f7, LeafB = 0x07991, Sound = 0x079cb, Hint = 0x077c3, Voice = 0x07643;

    private readonly string _mods = Directory.CreateTempSubdirectory("skynet-edit-t8-").FullName;

    public void Dispose()
    {
        ModContent.Unmount();
        try
        {
            Directory.Delete(_mods, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [MapDataFact]
    public void Map210_gate_lever_chain_is_a_graph()
    {
        var (_, doc) = Open();
        var g = TriggerGraph.Build(doc, L);

        var lever = g.Node(Lever)!;
        Assert.Equal(NodeBody.Mesh, lever.Body);
        Assert.Equal(ActKind.ProxChain, lever.Info.Kind);
        Assert.Equal(LinkFamily.Trigger, lever.Family);
        Assert.Equal(["prox"], lever.Modes);
        Assert.True(lever.IsSource);
        Assert.Empty(lever.In);
        Assert.Equal(LeafA, lever.Next);
        Assert.Equal(256, lever.Param("radius")!.Value);
        Assert.True(lever.Param("radius")!.ReadOnly);
        Assert.Equal(1, lever.Param("armed")!.Value);

        var leaf = g.Node(LeafA)!;
        Assert.Contains(Lever, leaf.In);
        Assert.Contains(Behind, leaf.In);
        Assert.Equal("slide X -128", leaf.Label);
        Assert.Equal("X", leaf.Param("axis")!.Text);
        Assert.Equal(128, leaf.Param("travel")!.Value);
        Assert.Equal(["chain"], leaf.Modes);

        Assert.Equal("voice 21029", g.Node(Voice)!.Label);
        Assert.Equal(NodeBody.Sprite, g.Node(Voice)!.Body);
        Assert.Contains(new GraphEdge(Lever, LeafA, LinkFamily.Trigger), g.Edges);
        Assert.Contains(new GraphEdge(LeafA, LeafB, LinkFamily.Mover), g.Edges);
        Assert.Contains(new GraphEdge(Sound, Hint, LinkFamily.Sound), g.Edges);

        // The cargo box doorway: gate → sound → exit, the exit by the mission's link.
        var exit = g.Node(0x076f7)!;
        Assert.Equal(ActKind.Exit, exit.Info.Kind);
        Assert.True(exit.Exit!.MissionLink);
        Assert.StartsWith("exit", exit.Label, StringComparison.Ordinal);

        // An unedited map has no error of its own; the act catalogue is the DOS table's.
        Assert.DoesNotContain(g.Issues, i => i.Level == ValidationLevel.Error);
        Assert.All(ActCatalog.All, a => Assert.Equal(ActOrigin.Dos, a.Origin));

        // Auto layout: the chain reads left to right.
        var at = doc.Layouts.Resolve(g);
        Assert.True(at[Lever].X < at[LeafA].X && at[LeafA].X < at[LeafB].X && at[LeafB].X < at[Sound].X && at[Sound].X < at[Voice].X);
        Assert.Equal(at[Lever].X, at[Behind].X);
        Assert.Equal(g.Nodes.Count, at.Values.Distinct().Count());
    }

    [ShockDataFact]
    public void Future_Shock_map013_switch_opens_both_doors()
    {
        var fs = GameData.Open(GameDataLocator.OtherGameDir(EditDocumentTests.DataDir!)!, EditDocumentTests.DataDir)!;
        const int Button = 0x03fb8, DoorA = 0x0408d, DoorB = 0x04046;
        var doc = new EditDocument();
        using (var archive = fs.OpenMapArchive())
        {
            doc.AddLayer("MAP.013", 13, MapFile.Parse(archive.Read("MAP.013")!)!);
        }

        var g = TriggerGraph.Build(doc, "MAP.013");
        var button = g.Node(Button)!;
        Assert.Equal(ActRules.ProxGate, button.Act);
        Assert.Equal(1, button.Param("wall button")!.Value);
        Assert.Contains("use", button.Modes);
        Assert.Equal([Button, 0x04119, DoorA, 0x040d1, 0x040f5, DoorB], TriggerChains.Chain(doc.Map("MAP.013"), Button, out _));
        Assert.Equal("slide Y +128", g.Node(DoorA)!.Label);

        // Pressed: both doors move, the doorway two storeys up is not taken.
        var v = GraphVerify.Chain(doc.Map("MAP.013"), BehaviourModel.LoadTransfrm(fs), Button);
        var run = v.Runs.Single(r => r.Source == Button);
        Assert.Equal("use", run.Mode);
        Assert.Contains(DoorA, run.Reached);
        Assert.Contains(DoorB, run.Reached);
        Assert.Empty(run.Missed);
        Assert.Empty(run.Exits);
    }

    [MapDataFact]
    public void Edits_undo_and_round_trip_through_the_layer_file()
    {
        var (data, doc) = Open();
        var original = Shape(TriggerGraph.Build(doc, L));

        // A new objective after the voice line; the hint cut out of the chain; the lever disarmed and back.
        var lever = doc.Map(L).Entities.First(e => e.FileOffset == Lever);
        var obj = GraphEdits.AddNode(doc, L, ActRules.ObjectiveFirst, NewNodeBody.Logic, lever.X, lever.Y, lever.Z);
        GraphEdits.Connect(doc, L, Voice, obj);
        GraphEdits.DeleteNode(doc, L, Hint, Rewire.Splice);
        GraphEdits.SetParam(doc, L, Lever, "armed", 0);
        GraphEdits.SetAct(doc, L, LeafB, 0x42);
        Assert.Equal(5, doc.History.UndoCount);

        var g = TriggerGraph.Build(doc, L);
        Assert.Equal(obj, g.Node(Voice)!.Next);
        Assert.Equal(Voice, g.Node(Sound)!.Next);
        Assert.Null(g.Node(Hint));
        Assert.Equal("objective M1", g.Node(obj)!.Label);
        Assert.Equal(0, g.Node(Lever)!.Param("armed")!.Value);
        Assert.Equal("slide X -128", g.Node(LeafB)!.Label);
        var edited = Shape(g);

        // Saved, then opened again with the mod: the same graph.
        var folder = SaveTarget.Folder(_mods, "t8", ["skynet", "shock"], out _)!;
        Assert.Contains(MapLayer.PathOf(L), doc.Save(folder));
        ModContent.Mount(_mods, ["t8"]);
        var reopened = EditDocument.Load(data, Mission1(data), ModContent.Files);
        Assert.Equal(edited, Shape(TriggerGraph.Build(reopened, L)));

        // Undo, one step each (the delete with its splice is one): the DOS graph again.
        for (var i = 0; i < 5; i++)
        {
            Assert.True(doc.Undo());
        }

        Assert.Equal(original, Shape(TriggerGraph.Build(doc, L)));
        Assert.True(doc.Redo());
        Assert.True(doc.Redo());
        Assert.True(doc.Redo());
        Assert.Null(TriggerGraph.Build(doc, L).Node(Hint));
        Assert.True(doc.Undo());
        Assert.Equal(Hint, TriggerGraph.Build(doc, L).Node(Sound)!.Next);
    }

    [MapDataFact]
    public void Checks_name_the_node()
    {
        var (_, doc) = Open();
        var lever = doc.Map(L).Entities.First(e => e.FileOffset == Lever);

        Assert.Throws<MapLayerException>(() => GraphEdits.SetAct(doc, L, LeafA, ActRules.Unknown1A));
        Assert.Throws<MapLayerException>(() => GraphEdits.SetParam(doc, L, LeafA, "travel", 64));
        Assert.Throws<MapLayerException>(() => GraphEdits.SetParam(doc, L, LeafA, "hit points", 70000));
        Assert.Throws<MapLayerException>(() => GraphEdits.Connect(doc, L, LeafA, LeafA));
        Assert.Throws<MapLayerException>(() => GraphEdits.Connect(doc, L, LeafA, 123));
        Assert.Equal(0, doc.History.UndoCount);

        // Raw layer edits the graph flags: a dangling next, an undecoded act, a light act on a mesh.
        doc.EditLink(L, Hint, next: 123);
        doc.EditLink(L, LeafB, act: ActRules.Unknown1A);
        doc.EditLink(L, Sound, act: ActRules.LightToggle);

        // Two new movers that lead into each other: a loop nothing starts; a mover nothing reaches.
        var a = GraphEdits.AddNode(doc, L, 0x3F, NewNodeBody.Mesh, lever.X, lever.Y, lever.Z, "BIGDOOR");
        var b = GraphEdits.AddNode(doc, L, 0x40, NewNodeBody.Mesh, lever.X, lever.Y, lever.Z + 100, "BIGDOOR");
        var lone = GraphEdits.AddNode(doc, L, 0x3F, NewNodeBody.Mesh, lever.X, lever.Y, lever.Z + 200, "BIGDOOR");
        GraphEdits.Connect(doc, L, a, b);
        GraphEdits.Connect(doc, L, b, a);

        var issues = TriggerGraph.Build(doc, L).Issues;
        void Has(ValidationLevel level, string code, int node) =>
            Assert.Contains(issues, i => i.Level == level && i.Code == code && i.Node == node);
        Has(ValidationLevel.Error, TriggerGraph.Dangling, Hint);
        Has(ValidationLevel.Error, TriggerGraph.UnknownAct, LeafB);
        Has(ValidationLevel.Warning, TriggerGraph.LightAct, Sound);
        Has(ValidationLevel.Warning, TriggerGraph.DeadLoop, Math.Min(a, b));
        Has(ValidationLevel.Warning, TriggerGraph.Unfired, lone);
        Assert.DoesNotContain(issues, i => i.Code == TriggerGraph.Unfired && (i.Node == a || i.Node == b));
        Assert.Equal(ValidationLevel.Error, issues[0].Level);
    }

    [MapDataFact]
    public void Verify_hook_matches_the_verifier_after_an_edit()
    {
        var (data, doc) = Open();
        var transfrm = BehaviourModel.LoadTransfrm(data);
        Assert.Empty(GraphVerify.Edited(doc, L, transfrm));

        var lever = doc.Map(L).Entities.First(e => e.FileOffset == Lever);
        var obj = GraphEdits.AddNode(doc, L, ActRules.ObjectiveFirst + 1, NewNodeBody.Logic, lever.X, lever.Y, lever.Z);
        GraphEdits.Connect(doc, L, Voice, obj);

        var verdict = Assert.Single(GraphVerify.Edited(doc, L, transfrm));
        Assert.Equal("changed", verdict.What);
        Assert.Contains(obj, verdict.Keys);
        var run = verdict.Runs.Single(r => r.Source == Lever);
        Assert.Equal("prox", run.Mode);
        Assert.Equal([2], run.Objectives);
        Assert.Contains(LeafA, run.Reached);
        Assert.Contains(LeafB, run.Reached);
        Assert.Empty(run.Missed);
        Assert.Contains("objective M2", run.Summary, StringComparison.Ordinal);
        Assert.Contains(verdict.Runs, r => r.Source == Behind);

        // SkyNet.Verify's row for the lever, told to expect exactly what the hook saw: PASS.
        var map = doc.Map(L);
        var node = TriggerGraph.Build(doc, L).Node(Lever)!;
        var verifier = new TriggerVerifier(data, [new LockNode(210, Lever, node.Act, node.Modes, 0, run.Effects, [])]);
        var row = verifier.VerifyMap(210, map).Single(r => r.Id == Lever);
        Assert.Equal(Row.Pass, row.Result);
    }

    [MapDataFact]
    public void Verify_covers_every_chain_of_the_level_not_only_the_edited()
    {
        // MAP.210's 0x07a05 (shot to death: its chain does nothing the verifier sees) is the DOS map's own.
        const int Broken = 0x07a05;
        var (data, doc) = Open();
        var transfrm = BehaviourModel.LoadTransfrm(data);
        var lever = doc.Map(L).Entities.First(e => e.FileOffset == Lever);
        var obj = GraphEdits.AddNode(doc, L, ActRules.ObjectiveFirst + 1, NewNodeBody.Logic, lever.X, lever.Y, lever.Z);
        GraphEdits.Connect(doc, L, Voice, obj);

        var edited = Assert.Single(GraphVerify.Edited(doc, L, transfrm));
        Assert.DoesNotContain(Broken, edited.Keys);

        var all = GraphVerify.All(doc, L, transfrm);
        Assert.Contains(all, v => v.What == "changed" && v.Keys.Contains(obj) && v.Passed);
        var broken = Assert.Single(all, v => v.Keys.Contains(Broken));
        Assert.Equal("chain", broken.What);
        Assert.False(broken.Passed);
        Assert.Contains(broken.Problems, p => p.Node == Broken);

        // Each record the player sets off is in a verified chain.
        var map = doc.Map(L);
        var verified = all.SelectMany(v => v.Runs.Select(r => r.Source)).ToHashSet();
        Assert.All(TriggerVerifier.Nodes(map).Keys.Where(k => TriggerFire.Mode(map.Entities.First(e => e.FileOffset == k)) != "chain"),
            k => Assert.Contains(k, verified));
    }

    [MapDataFact]
    public void Layout_moves_undo_and_persist_in_the_save_mod()
    {
        var (data, doc) = Open();
        var g = TriggerGraph.Build(doc, L);
        var auto = doc.Layouts.Resolve(g);

        doc.Layouts.Move(L, new Dictionary<int, Vector2> { [Lever] = new(10f, 20f) });
        doc.Layouts.Move(L, new Dictionary<int, Vector2> { [Lever] = new(30f, 40f) });
        Assert.Equal(1, doc.History.UndoCount);
        doc.EndDrag();
        doc.Layouts.Move(L, new Dictionary<int, Vector2> { [Voice] = new(-5f, 7f) });
        Assert.Equal(2, doc.History.UndoCount);
        Assert.Equal(new Vector2(30f, 40f), doc.Layouts.Resolve(g)[Lever]);
        Assert.Equal(auto[LeafA], doc.Layouts.Resolve(g)[LeafA]);

        var folder = SaveTarget.Folder(_mods, "t8l", ["skynet", "shock"], out _)!;
        Assert.Equal([ModInfo.FileName, GraphLayouts.PathOf(L)], doc.Save(folder).ToArray());
        Assert.False(doc.IsDirty);

        ModContent.Mount(_mods, ["t8l"]);
        var reopened = EditDocument.Load(data, Mission1(data), ModContent.Files);
        var placed = reopened.Layouts.Resolve(TriggerGraph.Build(reopened, L));
        Assert.Equal(new Vector2(30f, 40f), placed[Lever]);
        Assert.Equal(new Vector2(-5f, 7f), placed[Voice]);
        Assert.Equal(auto[LeafA], placed[LeafA]);

        Assert.True(doc.Undo());
        Assert.Equal(auto[Voice], doc.Layouts.Resolve(g)[Voice]);
        doc.Layouts.Reset(L);
        Assert.Empty(doc.Layouts.Stored(L));
        Assert.True(doc.Undo());
        Assert.Equal(new Vector2(30f, 40f), doc.Layouts.Stored(L)[Lever]);
    }

    // What a graph is, as comparable text: each node's key, act, next, links in and editable parameters.
    private static List<string> Shape(TriggerGraph g) =>
        [.. g.Nodes.Select(n => $"{n.Id} {n.Act:X2} {n.Next} [{string.Join(',', n.In.Order())}] "
            + string.Join(',', n.Params.Where(p => !p.ReadOnly).Select(p => $"{p.Name}={p.Value}")))];

    private static (GameData Data, EditDocument Doc) Open()
    {
        var data = GameData.Open(EditDocumentTests.DataDir!)!;
        return (data, EditDocument.Load(data, Mission1(data), null));
    }

    private static MissionFile Mission1(GameData data) => MissionGenerator.Build(data, MissionTable.For(data.Game).ByNumber(1)!.Value);
}
