using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using RetroEng.World;
using SkyNet.Formats;
using SkyNet.Game.Baking;
using SkyNet.Game.Behaviour;
using SkyNet.Game.Campaign;
using SkyNet.Game.Mods;
using SkyNet.Game.Tests.Behaviour;

namespace SkyNet.Game.Tests.Mods;

/// <summary>Map layers (maps/MAP.NNN.layer.json): the format, <see cref="MapFile.With"/> and the
/// resolver behind <see cref="GameData.LoadMap"/>. The mounted tests run alone (<see cref="ModsCollection"/>).</summary>
[Collection(ModsCollection.Name)]
public sealed class MapLayerTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("skynet-layers-").FullName;

    public void Dispose()
    {
        ModContent.Unmount();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private void Mount(params (string Layer, string Json)[] layers)
    {
        var maps = Path.Combine(_root, "m", "maps");
        Directory.CreateDirectory(maps);
        foreach (var (layer, json) in layers)
        {
            File.WriteAllText(Path.Combine(maps, layer + ".layer.json"), json);
        }

        ModContent.Mount(_root, ["m"]);
    }

    private static string Empty(int map) => $$"""{ "format": "skynet-layer", "version": 1, "base": "MAP.{{map:D3}}", "edits": [] }""";

    private static IEnumerable<(string How, string Line, int Map, int Key)> LockRows() =>
        TriggerLockTests.Rows.Select(r => ((string)r[0], (string)r[1])).Select(r => (r.Item1, r.Item2,
            int.Parse(r.Item2[..3], CultureInfo.InvariantCulture), int.Parse(r.Item2[4..9], NumberStyles.HexNumber, CultureInfo.InvariantCulture)));

    [BehaviourDataFact]
    public void A_no_op_layer_gives_a_byte_identical_block_and_the_same_trigger_lock()
    {
        var data = BehaviourData.Data;
        var plain = MapBaker.Bake(data, 210)!.ToBytes();
        var rows = LockRows().ToArray();
        Mount([.. rows.Select(r => r.Map).Distinct().Select(m => ($"MAP.{m:D3}", Empty(m)))]);
        Assert.NotNull(GameData.LayerResolver);
        Assert.Equal(plain, MapBaker.Bake(data, 210)!.ToBytes());
        foreach (var r in rows)
        {
            new TriggerLockTests().ActivationMatchesTheLock(r.How, r.Line);
        }
    }

    [BehaviourDataFact]
    public void A_layer_that_restates_values_rebuilds_the_map_and_keeps_the_trigger_lock()
    {
        var data = BehaviourData.Data;
        var rows = LockRows().ToArray();
        var layers = rows.GroupBy(r => r.Map).Select(g =>
        {
            var map = data.LoadMap(g.Key)!;
            var edits = g.Select(r => r.Key).Distinct().Select(k => map.TryGetEntityAt(k, out var e) ? e : throw new InvalidOperationException($"{k}"))
                .Select(e => $$"""{ "op": "set", "key": {{e.FileOffset}}, "x": {{e.X}}, "z": {{e.Z}}, "cellX": {{e.CellX}}, "cellZ": {{e.CellZ}}, "state": {{e.StateByte}} }""");
            return ($"MAP.{g.Key:D3}", $$"""{ "format": "skynet-layer", "version": 1, "base": "MAP.{{g.Key:D3}}", "edits": [ {{string.Join(", ", edits)}} ] }""");
        }).ToArray();
        Mount(layers);
        var edited = data.LoadMap(210)!;
        Assert.NotSame(edited, data.LoadMap(210));
        foreach (var r in rows)
        {
            new TriggerLockTests().ActivationMatchesTheLock(r.How, r.Line);
        }
    }

    [BehaviourDataFact]
    public void An_add_modify_remove_layer_round_trips_and_plays()
    {
        var data = BehaviourData.Data;
        var map = data.LoadMap(210)!;
        var moved = map.Entities.First(e => e.Variant == MapEntityVariant.Mesh && !e.IsActor);
        var gone = map.Entities.Last(e => e.Variant == MapEntityVariant.Mesh && e.FileOffset != moved.FileOffset);
        var layer = new MapLayer("MAP.210",
        [
            new MapEdit(MapEditOp.Set, moved.FileOffset, ImmutableSortedDictionary.CreateRange(StringComparer.Ordinal,
                new Dictionary<string, int> { ["x"] = moved.X + 2048, ["yaw"] = 512, ["hitPoints"] = 4 })),
            MapEdit.Delete(gone.FileOffset),
            new MapEdit(MapEditOp.Add, MapFile.FirstAddedKey + 1, ImmutableSortedDictionary.CreateRange(StringComparer.Ordinal,
                new Dictionary<string, int> { ["variant"] = 1, ["x"] = moved.X, ["y"] = moved.Y, ["z"] = moved.Z, ["act"] = 0x19, ["actor"] = 0 }), "CRATE04"),
            new MapEdit(MapEditOp.Add, MapFile.FirstAddedKey + 2, ImmutableSortedDictionary.CreateRange(StringComparer.Ordinal,
                new Dictionary<string, int> { ["variant"] = 3, ["sprite"] = MapFile.MarkerBank * 128 + 1, ["enemy"] = 5, ["x"] = -3000, ["z"] = 5000 })),
        ], ["EXTRA"]);

        var bytes = layer.ToBytes();
        var back = MapLayer.Parse(bytes);
        Assert.Equal(layer.Base, back.Base);
        Assert.Equal(layer.Names.ToArray(), back.Names.ToArray());
        Assert.Equal(layer.Edits.Length, back.Edits.Length);
        for (var i = 0; i < layer.Edits.Length; i++)
        {
            Assert.Equal(layer.Edits[i].Op, back.Edits[i].Op);
            Assert.Equal(layer.Edits[i].Key, back.Edits[i].Key);
            Assert.Equal(layer.Edits[i].Name, back.Edits[i].Name);
            Assert.Equal(layer.Edits[i].Fields.ToArray(), back.Edits[i].Fields.ToArray());
        }

        Assert.Equal(bytes, back.ToBytes());

        Mount(("MAP.210", Encoding.UTF8.GetString(bytes)));
        var e = data.LoadMap(210)!;
        Assert.Equal(map.Entities.Length + 1, e.Entities.Length);
        Assert.False(e.TryGetEntityAt(gone.FileOffset, out _));
        Assert.True(e.TryGetEntityAt(moved.FileOffset, out var m));
        Assert.Equal((moved.X + 2048, 512, 4, moved.Z), (m.X, m.Yaw, m.HitPoints, m.Z));
        Assert.Equal((int)Math.Floor((moved.X + 2048) / 1024.0), m.CellX);
        Assert.True(e.TryGetEntityAt(MapFile.FirstAddedKey + 1, out var crate));
        Assert.Equal(("CRATE04", 0x19, MapEntityVariant.Mesh), (e.EntityName(crate), crate.ActType, crate.Variant));
        Assert.Equal(map.Names.Length + 2, e.Names.Length);
        Assert.True(e.TryGetEntityAt(MapFile.FirstAddedKey + 2, out var marker));
        Assert.Equal((1, 5, -3, 4), (marker.MarkerType, marker.EnemyType, marker.CellX, marker.CellZ));

        // The edited map plays and bakes; its block records the layer, which a stored block lacks.
        _ = new LevelBehaviour(e, BehaviourData.Transfrm, new MissionProgress(5));
        var block = MapBaker.Bake(data, 210)!;
        Assert.Contains(block.Inputs, i => i.Name == BakeInputs.LayerPrefix + "MAP.210");
        Assert.DoesNotContain(MapBaker.Bake(data, 210, BakeTables.Shipped)!.Inputs, i => i.Name.StartsWith(BakeInputs.LayerPrefix, StringComparison.Ordinal));
    }

    [Fact]
    public void A_bad_layer_is_refused_and_the_map_plays_as_its_dos_file()
    {
        foreach (var json in new[]
        {
            """{ "format": "other", "version": 1, "base": "MAP.210" }""",
            """{ "format": "skynet-layer", "version": 2, "base": "MAP.210" }""",
            """{ "format": "skynet-layer", "version": 1, "base": "210" }""",
            """{ "format": "skynet-layer", "version": 1, "base": "MAP.210", "edits": [ { "op": "move", "key": 1 } ] }""",
            """{ "format": "skynet-layer", "version": 1, "base": "MAP.210", "edits": [ { "op": "set", "key": 1, "colour": 3 } ] }""",
            """{ "format": "skynet-layer", "version": 1, "base": "MAP.210", "edits": [ { "op": "delete", "key": 1, "x": 3 } ] }""",
            """{ "format": "skynet-layer", "version": 1, "base": "MAP.210", "edits": [ { "op": "add", "key": 16777217 } ] }""",
            """{ "format": "skynet-layer", "version": 1, "base": "MAP.210", "extra": 1 }""",
        })
        {
            Assert.Throws<MapLayerException>(() => MapLayer.Parse(Encoding.UTF8.GetBytes(json)));
        }

        if (BehaviourData.Dir == null)
        {
            return;
        }

        var data = BehaviourData.Data;
        var count = data.LoadMap(210)!.Entities.Length;
        foreach (var edit in new[] { """{ "op": "delete", "key": 7 }""", """{ "op": "add", "key": 5, "variant": 1 }""" })
        {
            Mount(("MAP.210", $$"""{ "format": "skynet-layer", "version": 1, "base": "MAP.210", "edits": [ {{edit}} ] }"""));
            Assert.Equal(count, data.LoadMap(210)!.Entities.Length);
        }

        Mount(("MAP.210", Empty(211)));
        Assert.Equal(count, data.LoadMap(210)!.Entities.Length);
    }

    [Fact]
    public void A_named_layer_runs_on_its_base_maps_block()
    {
        const string Head = """ "format": "retroeng-world", "version": 1, "id": "t", "start": { "instance": "1" }, """;
        const string Game = """ "game": { "data": "skynet", "version": 1, "mission": 1, "vehicle": "foot" } """;
        static string Mission(string block, string layer) =>
            "{" + Head + $$""" "instances": [ { "id": "1", "block": "{{block}}", "layer": "{{layer}}" } ], """ + Game + "}";

        Assert.Throws<WorldFormatException>(() => MissionFile.Parse(Encoding.UTF8.GetBytes(Mission("MAP.210", "GATE2"))));
        Mount(("GATE2", Empty(210)));
        var f = MissionFile.Parse(Encoding.UTF8.GetBytes(Mission("MAP.210", "GATE2")));
        Assert.Equal(210, MissionFile.LayerMap(f.World.Instances[0]));
        Assert.Throws<WorldFormatException>(() => MissionFile.Parse(Encoding.UTF8.GetBytes(Mission("MAP.211", "GATE2"))));
        Assert.Throws<WorldFormatException>(() => MissionFile.Parse(Encoding.UTF8.GetBytes(Mission("MAP.210", "../GATE2"))));
    }
}
