using RetroEng.Loop;
using SkyNet.Formats;
using SkyNet.Game.Behaviour;
using SkyNet.Game.Campaign;
using SkyNet.Verify;

namespace SkyNet.Edit.Tests;

/// <summary>Play > Preview and Play > Fire trigger (editor plan step E9): the edited mission played by the
/// game's session and left with the document as it was; a fired chain doing what the verifier's row does.</summary>
[Collection(EditModsCollection.Name)]
public sealed class PreviewTests
{
    private const string Layer = "MAP.210";

    [MapDataFact]
    public void Preview_plays_the_edits_and_returns_leaving_identical_layer_bytes()
    {
        var data = GameData.Open(EditDocumentTests.DataDir!)!;
        var mission = MissionTable.For(data.Game).ByNumber(1)!.Value;
        var doc = EditDocument.Load(data, MissionGenerator.Build(data, mission), null);
        var moved = doc.Map(Layer).Entities.First(e => e.Variant == MapEntityVariant.Mesh && e.ActType == 0);
        doc.SetFields(Layer, moved.FileOffset, new Dictionary<string, int> { ["x"] = moved.X + 256 }, label: "Move");
        doc.EndDrag();

        var bytes = doc.Layers.ToDictionary(l => l, l => doc.Layer(l).ToBytes());
        var maps = doc.Layers.ToDictionary(l => l, doc.Map);
        var (dirty, missionBefore, resolver) = (doc.IsDirty, doc.Mission, GameData.LayerResolver);

        using (var preview = new PreviewSession(data, doc, mission, "210", doc.Mission!.World.Start.Arrival))
        {
            var session = preview.Session;
            Assert.Equal("210", session.Instance!.Id);
            Assert.True(session.Map!.TryGetEntityAt(moved.FileOffset, out var played));
            Assert.Equal(moved.X + 256, played.X);
            var forward = TickInput.Bit(preview.Controls.Forward);
            var use = TickInput.Bit(preview.Controls.Use);
            for (var t = 0; t < 120; t++)
            {
                preview.Tick(new TickInput(forward, t % 30 == 0 ? use : 0UL, 0UL, 0.5f, 0f, 0f, 0f, 0f));
            }

            Assert.Equal(SessionPhase.Playing, session.Phase);
            Assert.True(session.Clock > 1.5f);
        }

        Assert.Same(resolver, GameData.LayerResolver);
        Assert.Equal(dirty, doc.IsDirty);
        Assert.Same(missionBefore, doc.Mission);
        foreach (var (layer, before) in bytes)
        {
            Assert.Equal(before, doc.Layer(layer).ToBytes());
            Assert.Same(maps[layer], doc.Map(layer));
        }
    }

    /// <summary>MAP.210's gate lever (0x09551, use gate): fired once, it slides the two gate halves and plays
    /// the hint, exactly the effects the trigger lock lists and the verifier's row passes.</summary>
    [MapDataFact]
    public void Fire_trigger_on_the_MAP_210_gate_lever_matches_the_verifier_row()
    {
        const int lever = 0x09551;
        var data = GameData.Open(EditDocumentTests.DataDir!)!;
        var mission = MissionTable.For(data.Game).ByNumber(1)!.Value;
        var doc = EditDocument.Load(data, MissionGenerator.Build(data, mission), null);
        var map = doc.Map(Layer);

        var fired = TriggerFire.Fire(map, BehaviourModel.LoadTransfrm(data), lever)!;
        Assert.Equal("use", fired.Mode);
        Assert.Equal([lever, 0x078f7, 0x07991], fired.Touched);
        var log = TriggerFire.Describe(map, fired).ToList();
        Assert.StartsWith("Fire 09551 0xEF use gate by use: ", log[0], StringComparison.Ordinal);
        Assert.Contains(log, l => l.Contains("078f7 0x42", StringComparison.Ordinal));

        // The Godot trigger lock's line for the node (tests/rules/skynet.triggers.lock, as in the subset).
        var node = TriggerLock.Parse(["210 09551 EF use:eye/3d/r600/always/nolatch chain[09551* 0935b:E0 078f7:42 07991:41 079cb:E3 077c3:1D 07643:ED] "
            + "first[snd45 snd77 hint1 voice21029 move@078f7:slideX-128 move@07991:slideX+128] "
            + "second[snd45 snd77 voice21029 move@078f7:slideX+128 move@07991:slideX-128] 6c92c2dc"]).Single();
        var row = new TriggerVerifier(data, [node]).VerifyMap(210, map).Single(r => r.Id == lever);
        Assert.Equal(Row.Pass, row.Result);
        Assert.Equal("use", row.Mode);
        Assert.Equal("", Equiv.Why(node.First, fired.Effects));
        Assert.Equal(node.First.Order(StringComparer.Ordinal), fired.Effects.Order(StringComparer.Ordinal));
    }
}
