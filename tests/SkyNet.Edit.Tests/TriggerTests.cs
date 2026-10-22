using System.Numerics;
using RetroEng.World;
using SkyNet.Formats;
using SkyNet.Game.Baking;
using SkyNet.Game.Behaviour;
using SkyNet.Game.Campaign;
using SkyNet.Game.Mods;
using SkyNet.Verify;

namespace SkyNet.Edit.Tests;

/// <summary>Triggers, links, exits and the mission panel (editor plan step E8): the act table's names, the
/// chain walk, mission commands with undo, and a two-room mod built with the editor's commands that the
/// headless verifier enumerates and the game's session plays through its exit.</summary>
[Collection(EditModsCollection.Name)]
public sealed class TriggerTests : IDisposable
{
    private readonly string _mods = Directory.CreateTempSubdirectory("skynet-edit-e8-").FullName;

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

    [Fact]
    public void Acts_are_named_from_the_handler_table()
    {
        Assert.Equal("0xEF use gate", TriggerChains.ActName(0xEF));
        Assert.Equal("0xF0 exit", TriggerChains.ActName(0xF0));
        Assert.Equal("0x3F slide X +64", TriggerChains.ActName(0x3F));
        Assert.Equal("0x26 objective M1", TriggerChains.ActName(0x26));
        Assert.Equal("0x1C hint 1", TriggerChains.ActName(0x1C));
        Assert.Equal("0x01 light toggle", TriggerChains.ActName(0x01));
        Assert.Equal("0xF2 proximity 1024", TriggerChains.ActName(0xF2));
        Assert.Contains(0xF0, TriggerChains.KnownActs);
        Assert.DoesNotContain(0x1A, TriggerChains.KnownActs);
        Assert.Equal(LinkFamily.Trigger, TriggerChains.Family(0xEF));
        Assert.Equal(LinkFamily.Mover, TriggerChains.Family(0x3F));
        Assert.Equal(LinkFamily.Exit, TriggerChains.Family(0xF0));
    }

    [MapDataFact]
    public void The_chain_walks_next_both_ways_and_stops_at_a_loop()
    {
        var data = GameData.Open(EditDocumentTests.DataDir!)!;
        var doc = EditDocument.Load(data, Mission1(data), null);
        const string layer = "MAP.210";
        var map = doc.Map(layer);
        var meshes = map.Entities.Where(e => e.Variant == MapEntityVariant.Mesh && TriggerChains.NextOf(map, e) < 0 && !TriggerChains.Incoming(map, e.FileOffset).Any())
            .Take(3).Select(e => e.FileOffset).ToArray();
        doc.EditLink(layer, meshes[0], act: ActRules.ProxGate, next: meshes[1]);
        doc.EditLink(layer, meshes[1], act: 0x3F, next: meshes[2]);
        map = doc.Map(layer);
        Assert.Equal(meshes, TriggerChains.Chain(map, meshes[1], out var at));
        Assert.Equal(1, at);
        Assert.Contains((meshes[0], meshes[1], LinkFamily.Trigger), TriggerChains.Links(map));

        // The last one back to the first: a loop, each record once.
        doc.EditLink(layer, meshes[2], next: meshes[0]);
        Assert.Equal(3, TriggerChains.Chain(doc.Map(layer), meshes[2], out _).Count);
        Assert.True(doc.Undo());
        Assert.True(doc.Undo());
        Assert.True(doc.Undo());
        Assert.Equal([meshes[1]], TriggerChains.Chain(doc.Map(layer), meshes[1], out _));
    }

    [MapDataFact]
    public void Mission_commands_undo_and_only_a_changed_mission_is_saved()
    {
        var data = GameData.Open(EditDocumentTests.DataDir!)!;
        var mission = Mission1(data);
        var doc = EditDocument.Load(data, mission, null);
        var changes = 0;
        doc.MissionChanged += () => changes++;
        var folder = Path.Combine(_mods, "m");
        Assert.DoesNotContain(MissionFile.PathOf(1), doc.Save(folder));

        var exit = mission.World.Links[0];
        doc.SetMission(MissionEdits.SetExit(doc.Mission!, exit.From, exit.Source, null, true, 3), "Exit");
        doc.SetMission(MissionEdits.SetGroup(doc.Mission!, exit.From, "yard"), "Group");
        doc.SetMission(MissionEdits.SetProperty(doc.Mission!, exit.From, MissionFile.MusicProperty, "T05.HMI"), "Music");
        doc.SetMission(MissionEdits.SetMode(doc.Mission!, PlayerMode.Jeep), "Vehicle");
        Assert.True(doc.IsDirty);
        Assert.Equal(4, changes);
        var link = doc.Mission!.World.Link(exit.From, exit.Source)!;
        Assert.True(link.Back);
        Assert.Equal(3, link.Arrival);
        Assert.Equal("yard", doc.Mission.World.Instance(exit.From)!.Group);
        Assert.Equal("T05.HMI", MissionFile.Music(doc.Mission.World.Instance(exit.From)!));
        Assert.Equal(PlayerMode.Jeep, doc.Mission.Mode);
        Assert.Throws<WorldFormatException>(() => MissionEdits.RemoveInstance(doc.Mission, doc.Mission.World.Start.Instance));
        Assert.Throws<WorldFormatException>(() => MissionEdits.SetProperty(doc.Mission, exit.From, MissionFile.VehicleProperty, "boat"));

        for (var i = 0; i < 4; i++)
        {
            Assert.True(doc.Undo());
        }

        Assert.Same(mission, doc.Mission);
        Assert.False(doc.IsDirty);
        Assert.True(doc.Redo());
        Assert.Equal([MissionFile.PathOf(1)], doc.Save(folder).ToArray());
        Assert.Equal(doc.Mission.ToJson(), File.ReadAllBytes(Path.Combine(folder, MissionFile.PathOf(1))));
        Assert.Empty(doc.Save(folder));
    }

    /// <summary>A saved mission loses nothing: changing one instance's music (or the vehicle mode) leaves every
    /// other value of the file as it was, the source hash, inputs and an unknown game field included.</summary>
    [MapDataFact]
    public void A_saved_mission_changes_only_what_was_edited()
    {
        var data = GameData.Open(EditDocumentTests.DataDir!)!;
        var generated = Mission1(data);
        generated.World.Game!.Add("custom", RetroEng.Json.JsonItem.NewObject().Add("kept", 7));
        var original = MissionFile.Parse(generated.ToJson());
        Assert.NotEmpty(original.World.SourceHash);
        var doc = EditDocument.Load(data, original, null);
        var id = original.World.Instances[1].Id;
        doc.SetMission(MissionEdits.SetProperty(doc.Mission!, id, MissionFile.MusicProperty, "T07.HMI"), "Music");
        var folder = Path.Combine(_mods, "music");
        Assert.Contains(MissionFile.PathOf(1), doc.Save(folder));

        static System.Text.Json.Nodes.JsonNode Node(byte[] b) => System.Text.Json.Nodes.JsonNode.Parse(b)!;
        var before = Node(original.ToJson());
        var saved = Node(File.ReadAllBytes(Path.Combine(folder, MissionFile.PathOf(1))));
        var props = saved["instances"]![1]!["properties"]!;
        Assert.Equal("T07.HMI", (string?)props["music"]);
        props["music"] = before["instances"]![1]!["properties"]!["music"]!.DeepClone();
        Assert.True(System.Text.Json.Nodes.JsonNode.DeepEquals(before, saved), saved.ToJsonString());

        // The vehicle mode: only game.vehicle differs, in its place.
        var jeep = Node(MissionEdits.SetMode(original, PlayerMode.Jeep).ToJson());
        Assert.Equal("jeep", (string?)jeep["game"]!["vehicle"]);
        jeep["game"]!["vehicle"] = before["game"]!["vehicle"]!.DeepClone();
        Assert.True(System.Text.Json.Nodes.JsonNode.DeepEquals(before, jeep), jeep.ToJsonString());
        Assert.Equal(original.ToJson(), MissionEdits.SetMode(original, original.Mode).ToJson());
    }

    /// <summary>The two-room mod: on MAP.210 (room1) a lever whose chain opens a gate, and a doorway whose
    /// exit link leads to MAP.212 (room2); built with the editor's commands, saved into a scratch mods
    /// folder, enumerated and checked by the verifier, and walked by the game's session.</summary>
    [MapDataFact]
    public void A_two_room_mod_with_a_lever_gate_chain_and_an_exit_plays()
    {
        var data = GameData.Open(EditDocumentTests.DataDir!)!;
        var generated = Mission1(data);
        var doc = EditDocument.Load(data, generated, null);

        // The mission: room1 (MAP.210, the start) and room2 (MAP.212), nothing else.
        doc.SetMission(MissionEdits.AddInstance(doc.Mission!, "room1", 210), "Add instance");
        doc.SetMission(MissionEdits.AddInstance(doc.Mission!, "room2", 212, "rooms"), "Add instance");
        doc.SetMission(MissionEdits.SetStart(doc.Mission!, "room1", -1), "Start");
        foreach (var other in generated.World.Instances)
        {
            doc.SetMission(MissionEdits.RemoveInstance(doc.Mission!, other.Id), "Remove instance");
        }

        Assert.Equal(["room1", "room2"], doc.Mission!.World.Instances.Select(i => i.Id));
        Assert.Empty(doc.Mission.World.Links);

        // Room1's records: a gate (slide X), a lever (use gate) linked to it, a doorway (exit).
        const string layer = "MAP.210";
        var map = doc.Map(layer);
        var door = map.Entities.First(e => map.EntityName(e) == "BIGDOOR");
        var doorway = map.Entities.First(e => e.ActType == ActRules.Exit && e.Variant == MapEntityVariant.Sprite);
        Dictionary<string, int> At(int dx, int dz, params (string, int)[] more)
        {
            var f = new Dictionary<string, int> { ["x"] = door.X + dx, ["y"] = door.Y, ["z"] = door.Z + dz };
            foreach (var (k, v) in more)
            {
                f[k] = v;
            }

            return f;
        }

        var gate = doc.AddEntity(layer, At(3000, 0, ("variant", 1), ("act", 0x3F), ("state", 0)), "BIGDOOR");
        var lever = doc.AddEntity(layer, At(3000, 1500, ("variant", 1), ("state", 0)), "BIGDOOR");
        doc.EditLink(layer, lever, act: ActRules.ProxGate, next: gate);
        var exit = doc.AddEntity(layer, At(-3000, 3000, ("variant", 3), ("sprite", doorway.SpriteIndex), ("act", ActRules.Exit),
            ("state", 0), ("param16", 212), ("param8", 0)));
        doc.SetMission(MissionEdits.SetExit(doc.Mission!, "room1", exit, "room2", false, 0), "Add exit");
        Assert.Equal([lever, gate], TriggerChains.Chain(doc.Map(layer), gate, out _));

        var folder = SaveTarget.Folder(_mods, "e8", ["skynet", "shock"], out _)!;
        Assert.Equal([ModInfo.FileName, MapLayer.PathOf(layer), MissionFile.PathOf(1)], doc.Save(folder).ToArray());

        // The game's view of the mod: the mission file from the mods, the layer behind LoadMap.
        ModContent.Mount(_mods, ["e8"]);
        using var store = new BlockStore(_mods, data);
        store.Mount();
        var file = store.LoadMission(1)!;
        Assert.Equal(doc.Mission.ToJson(), file.ToJson());
        var played = data.LoadMap(210)!;

        // The verifier enumerates the chain as the trigger graph does: the lever by the use key, the
        // gate by the chain, the doorway by touch.
        var nodes = TriggerVerifier.Nodes(played);
        var incoming = TriggerVerifier.Incoming(played);
        string Modes(int key) => string.Join(',', TriggerVerifier.Modes(nodes[key], TriggerVerifier.KindName(nodes[key]), incoming.Contains(key)));
        Assert.Equal("prox_gate use", TriggerVerifier.KindName(nodes[lever]) + " " + Modes(lever));
        Assert.Equal("mover chain", TriggerVerifier.KindName(nodes[gate]) + " " + Modes(gate));
        Assert.Equal("exit touch", TriggerVerifier.KindName(nodes[exit]) + " " + Modes(exit));

        // Its rows against what the chain must do: the key at the lever slides the gate open, again shut;
        // the doorway asks for MAP.212.
        var expect = new List<LockNode>
        {
            new(210, lever, ActRules.ProxGate, ["use"], 0, [Effect("move@{0:x5}:slideX+64", gate)], [Effect("move@{0:x5}:slideX-64", gate)]),
            new(210, gate, 0x3F, ["chain"], 0, [], []),
            new(210, exit, ActRules.Exit, ["touch"], 0, ["exit212/0"], []),
        };
        var verifier = new TriggerVerifier(data, expect);
        var rows = verifier.VerifyMap(210, played).Where(r => r.Id == lever || r.Id == gate || r.Id == exit).ToList();
        Assert.Equal(3, rows.Count);
        Assert.All(rows, r => Assert.True(r.Result is Row.Pass or Row.Skip, r.ToString()));
        Assert.Equal(Row.Pass, rows.Single(r => r.Id == lever).Result);
        Assert.Equal(Row.Pass, rows.Single(r => r.Id == exit).Result);
        Assert.DoesNotContain(verifier.Enumeration, line => new[] { lever, gate, exit }.Any(k => line.Contains($"{k:x5}", StringComparison.Ordinal)));

        // The game's session: room1 first; the use key on the doorway takes the mission link to room2.
        var session = new GameSession(data) { Missions = m => store.LoadMission(m.Number) };
        session.StartMission(MissionTable.For(data.Game).ByNumber(1)!.Value);
        Assert.Equal("room1", session.Instance!.Id);
        Assert.Contains(exit, session.Behaviour!.Exits);
        Assert.Contains(gate, session.Behaviour.Movers);
        var feet = BehaviourModel.Position(nodes[exit]);
        session.Behaviour.Tick(GameSession.Dt, new PlayerProbe(feet, feet + new Vector3(0f, 60f, 0f), UsePressed: true));
        session.Tick(default, default, default, false);
        Assert.Equal("room2", session.Instance!.Id);
        Assert.Equal(212, session.MapNumber);
        Assert.Equal("room1", session.PreviousInstance?.Id);
    }

    private static string Effect(string format, int key) => string.Format(System.Globalization.CultureInfo.InvariantCulture, format, key);

    private static MissionFile Mission1(GameData data) => MissionGenerator.Build(data, MissionTable.For(data.Game).ByNumber(1)!.Value);
}
