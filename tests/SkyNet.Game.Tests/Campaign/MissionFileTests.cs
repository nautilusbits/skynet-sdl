using System.Security.Cryptography;
using System.Text;
using RetroEng.World;
using SkyNet.Formats;
using SkyNet.Game.Baking;
using SkyNet.Game.Campaign;
using SkyNet.Game.Tests.Behaviour;
using Xunit.Abstractions;

namespace SkyNet.Game.Tests.Campaign;

/// <summary>The mission file (docs/formats/mission.md): the generator's output, the variant group,
/// one block placed twice, a mod's override in a temp mods folder and saves keyed by instance.</summary>
public sealed class MissionFileTests(ITestOutputHelper output) : IDisposable
{
    /// <summary>A fact skipped unless Future Shock's data sits beside SKYNET_DATA's.</summary>
    private sealed class ShockDataFactAttribute : FactAttribute
    {
        public ShockDataFactAttribute()
        {
            if (BehaviourData.Dir is not { } dir || GameDataLocator.OtherGameDir(dir) == null)
            {
                Skip = "no Future Shock data beside SKYNET_DATA's SkyNET folder";
            }
        }
    }

    private readonly string _temp = Directory.CreateTempSubdirectory("skynet-missions-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_temp, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static Mission M(int n) => MissionTable.SkyNet.ByNumber(n)!.Value;

    private static WorldLink Link(MissionFile f, string from, string to) =>
        f.World.Links.First(l => l.From == from && l.To == to);

    [BehaviourDataFact]
    public void Mission1IsTheBaseItsInteriorsAndItsVariants()
    {
        var f = MissionGenerator.Build(BehaviourData.Data, M(1));
        output.WriteLine(Encoding.UTF8.GetString(f.ToJson()));
        Assert.Equal(GameKind.SkyNet, f.Game);
        Assert.Equal(1, f.Number);
        Assert.Equal("210.TXT", f.Briefing);
        Assert.Equal(PlayerMode.Foot, f.Mode);
        Assert.Equal(2, f.Next);
        Assert.Null(f.Torpedo);
        Assert.Equal(new WorldStart("210", -1), f.World.Start);
        Assert.Equal("skynet/M01", f.World.Id);
        foreach (var map in new[] { 210, 211, 212, 213, 214, 215, 216, 217, 218 })
        {
            var i = f.InstanceOf(map)!;
            Assert.Equal(map.ToString(System.Globalization.CultureInfo.InvariantCulture), i.Id);
            Assert.Equal($"MAP.{map}", i.Block);
            Assert.Equal($"MAP.{map}", i.Layer);
            Assert.Equal("foot", i.Property(MissionFile.VehicleProperty));
            Assert.NotNull(MissionFile.Music(i));
        }

        // docs/missions.md: 210 → 211/212/213/214/218 at set 0; 212 → 216 (12); 214 → 215 (0), 217 (14); 215 → 214 (10).
        foreach (var to in new[] { "211", "212", "213", "214", "218" })
        {
            Assert.Equal(0, Link(f, "210", to).Arrival);
        }

        Assert.Equal(12, Link(f, "212", "216").Arrival);
        Assert.Equal(0, Link(f, "214", "215").Arrival);
        Assert.Equal(14, Link(f, "214", "217").Arrival);
        Assert.Equal(10, Link(f, "215", "214").Arrival);
        Assert.Contains(f.World.Links, l => l.From == "211" && l.Back && l.Arrival == 11);

        // Every link is an 0xF0 record of its instance's map, with the record's own set.
        foreach (var l in f.World.Links)
        {
            var map = BehaviourData.Data.LoadMap(MissionFile.LayerMap(f.World.Instance(l.From)!))!;
            Assert.True(map.TryGetEntityAt(l.Source, out var e));
            Assert.Equal(0xF0, e.ActType);
            Assert.Equal(e.Param8, l.Arrival);
            Assert.Equal(e.Param16 == 0, l.Back);
        }

        // The JSON reads back to the same bytes, and the inputs give the same hash now.
        var json = f.ToJson();
        Assert.Equal(json, MissionFile.Parse(json).ToJson());
        using var inputs = new BakeInputs(BehaviourData.Data);
        Assert.Equal(f.World.SourceHash, MissionGenerator.CurrentSourceHash(inputs, M(1), 2, f.World.Inputs));
    }

    [BehaviourDataFact]
    public void Mission5HasTheTorpedoRideToTheSubmarine()
    {
        var f = MissionGenerator.Build(BehaviourData.Data, M(5));
        output.WriteLine(Encoding.UTF8.GetString(f.ToJson()));
        Assert.Equal(new WorldStart("252", -1), f.World.Start);
        Assert.Equal(new MissionTorpedo("250", "253", "254", 0), f.Torpedo);
        foreach (var map in new[] { 250, 252, 253, 254 })
        {
            Assert.NotNull(f.InstanceOf(map));
        }

        // MAP.253's exit drops the soldier on MAP.250 at set 10 (inside the tube).
        Assert.Equal(10, Link(f, "253", "250").Arrival);
        Assert.Equal(6, f.Next);
    }

    [BehaviourDataFact]
    public void VariantMapsAreOnePlaceWithTheirOwnBlocks()
    {
        var f = MissionGenerator.Build(BehaviourData.Data, M(1));
        var group = f.World.Instances.Where(i => i.Group == "210").Select(i => i.Id).Order().ToArray();
        Assert.Equal(["210", "216", "217"], group);

        // DOS ships the variants' ground twice: WLD.216 = WLD.217 byte for byte.
        Assert.Equal(Md5(BehaviourData.Data.ReadLoose("WLD.216")), Md5(BehaviourData.Data.ReadLoose("WLD.217")));
        Assert.Empty(f.World.SharedBlocks());

        var m3 = MissionGenerator.Build(BehaviourData.Data, M(3));
        Assert.Equal("230", m3.InstanceOf(234)?.Group);
        Assert.Equal("230", m3.InstanceOf(235)?.Group);

        // Interiors are shared between missions: MAP.211-215 by missions 1 and 2.
        var shared = MissionGenerator.SharedBlocks([f, MissionGenerator.Build(BehaviourData.Data, M(2))]);
        Assert.Contains(shared, s => s.Block == "MAP.214" && s.Uses.Select(u => u.Mission).Order().SequenceEqual([1, 2]));
    }

    /// <summary>A mission file that places MAP.214 twice: 210's exit to 214 leads to a second
    /// instance, "214b", whose state is its own.</summary>
    private static MissionFile TwiceOn214()
    {
        var f = MissionGenerator.Build(BehaviourData.Data, M(1));
        var i214 = f.World.Instance("214")!;
        var instances = f.World.Instances.Append(i214 with { Id = "214b" }).ToArray();
        var links = f.World.Links.Select(l => l.From == "210" && l.To == "214" ? l with { To = "214b" } : l)
            .Concat(f.World.Links.Where(l => l.From == "214").Select(l => l with { From = "214b" }))
            .ToArray();
        var g = f.World.Game!;
        return MissionFile.From(new WorldDefinition(f.World.Id, f.World.Start, instances, links, g));
    }

    [BehaviourDataFact]
    public void OneBlockPlacedTwiceKeepsTwoStates()
    {
        var file = TwiceOn214();
        Assert.Equal([("MAP.214", 2)], file.World.SharedBlocks().Select(s => (s.Block, s.Instances.Count)));
        var session = new GameSession(BehaviourData.Data) { Missions = _ => file };
        session.StartMission(M(1));
        Assert.Same(file, session.MissionFile);
        Assert.True(session.EnterInstance("214b", 0));
        var b = session.Behaviour;
        Assert.Equal(214, session.MapNumber);
        Assert.Equal(214, session.BlockMap);
        Assert.True(session.EnterInstance("214", 0));
        Assert.NotSame(b, session.Behaviour);
        Assert.Equal("214b", session.PreviousInstance?.Id);
        Assert.True(session.EnterInstance("214b", 0));
        Assert.Same(b, session.Behaviour);

        // A save keeps both and puts each back under its id.
        var save = SaveFile.Read(SaveFile.Write(session.Capture(DateTime.UnixEpoch)));
        Assert.Equal("214b", save.Instance);
        Assert.Equal("214", save.PreviousInstance);
        Assert.Equal(["210", "214", "214b"], save.Maps.Select(m => m.Instance!).Order());
        var loaded = new GameSession(BehaviourData.Data) { Missions = _ => file };
        loaded.Load(save);
        Assert.Equal("214b", loaded.Instance?.Id);
        Assert.Equal("214", loaded.PreviousInstance?.Id);
    }

    [BehaviourDataFact]
    public void TheSystemZipHoldsTheFilesAndAModOverridesOne()
    {
        var mods = Path.Combine(_temp, "mods");
        Directory.CreateDirectory(mods);
        using var store = new BlockStore(mods, BehaviourData.Data);
        Assert.Equal(8, store.StaleMissions().Count);
        Assert.Equal(8, store.BakeMissions(store.StaleMissions()));
        Assert.Empty(store.StaleMissions());
        store.Mount();
        var system = store.LoadMission(1)!;
        Assert.Equal(MissionGenerator.Build(BehaviourData.Data, M(1)).ToJson(), system.ToJson());

        // A mod's missions/M01.json wins: here mission 1 starts on MAP.214 at set 10.
        var changed = MissionFile.From(new WorldDefinition(system.World.Id, new WorldStart("214", 10), system.World.Instances,
            system.World.Links, system.World.Game));
        var modDir = Path.Combine(mods, "startinside", "missions");
        Directory.CreateDirectory(modDir);
        File.WriteAllBytes(Path.Combine(modDir, "M01.json"), changed.ToJson());
        store.Mount();
        Assert.Equal(new WorldStart("214", 10), store.LoadMission(1)!.World.Start);
        var session = new GameSession(BehaviourData.Data) { Missions = m => store.LoadMission(m.Number) };
        session.StartMission(M(1));
        Assert.Equal(214, session.MapNumber);

        // A mod file that does not validate is not used: the mission is generated.
        File.WriteAllText(Path.Combine(modDir, "M01.json"), """{ "format": "retroeng-world", "version": 1, "id": "x", "start": { "instance": "nowhere" }, "instances": [] }""");
        store.Mount();
        Assert.Null(store.LoadMission(1));
        session.StartMission(M(1));
        Assert.Equal(210, session.MapNumber);

        // The system ZIP's entry is fresh against the data; nothing else was written outside the temp folder.
        Assert.True(File.Exists(store.ZipPath));
    }

    [BehaviourDataFact]
    public void AnOlderSaveLoadsByMapNumber()
    {
        var a = new GameSession(BehaviourData.Data);
        a.StartMission(M(1));
        Assert.True(a.EnterMap(214, 0));
        var save = a.Capture(DateTime.UnixEpoch);
        Assert.Equal("214", save.Instance);
        var old = SaveFile.Read(SaveFile.Write(save, 5));
        Assert.Null(old.Instance);
        Assert.All(old.Maps, m => Assert.Null(m.Instance));
        var b = new GameSession(BehaviourData.Data);
        b.Load(old);
        Assert.Equal("214", b.Instance?.Id);
        Assert.Equal("210", b.PreviousInstance?.Id);
        Assert.Equal(214, b.MapNumber);
        Assert.Equal(210, b.PreviousMap);
        Assert.True(b.EnterMap(210, 0));
        Assert.Equal(SaveFile.Version, BitConverter.ToUInt16(SaveFile.Write(b.Capture(DateTime.UnixEpoch)), 8));
    }

    [Fact]
    public void InvalidFilesAreRefused()
    {
        static void Bad(string json) => Assert.Throws<WorldFormatException>(() => MissionFile.Parse(Encoding.UTF8.GetBytes(json)));
        const string Head = """ "format": "retroeng-world", "version": 1, "id": "t", "start": { "instance": "1" }, """;
        const string Game = """ "game": { "data": "skynet", "version": 1, "mission": 1, "vehicle": "foot" } """;
        const string One = """ "instances": [ { "id": "1", "block": "MAP.210", "layer": "MAP.210" } ] """;
        var good = "{" + Head + One + "," + Game + "}";
        Assert.Equal(1, MissionFile.Parse(Encoding.UTF8.GetBytes(good)).Number);
        Bad("not json");
        Bad("{" + Head.Replace("retroeng-world", "other", StringComparison.Ordinal) + One + "," + Game + "}");
        Bad("{" + Head + One + "}");                                               // no game section
        Bad("{" + Head + """ "instances": [ ], """ + Game + "}");                    // the start is not listed
        Bad("{" + Head + """ "instances": [ { "id": "1", "block": "MAP.210", "layer": "MAP.210" }, { "id": "1", "block": "MAP.211", "layer": "MAP.211" } ], """ + Game + "}");
        Bad("{" + Head + """ "instances": [ { "id": "1", "block": "MAP.210", "layer": "MAP.216" } ], """ + Game + "}");
        Bad("{" + Head + One + """, "links": [ { "from": "1", "source": 5, "to": "2", "arrival": 0 } ], """ + Game + "}");
        Bad("{" + Head + One + """, "links": [ { "from": "1", "source": 5, "to": null }, { "from": "1", "source": 5, "back": true } ], """ + Game + "}");
        Bad("{" + Head + """ "instances": [ { "id": "1", "block": "MAP.210", "layer": "MAP.210", "properties": { "vehicle": "boat" } } ], """ + Game + "}");
        Bad("{" + Head + One + """, "game": { "data": "skynet", "version": 1, "mission": 1, "vehicle": "foot", "torpedo": { "at": "1", "after": "9", "to": "1", "arrival": 0 } } }""");
    }

    [ShockDataFact]
    public void FutureShockMissionsAllGenerate()
    {
        var data = GameData.Open(GameDataLocator.OtherGameDir(BehaviourData.Dir!)!, BehaviourData.Dir)!;
        var files = MissionGenerator.BuildAll(data);
        Assert.Equal(17, files.Count);
        Assert.All(files, f =>
        {
            Assert.Equal(GameKind.FutureShock, f.Game);
            Assert.All(f.World.Instances, i => Assert.Null(i.Group));
            Assert.Equal(f.ToJson(), MissionFile.Parse(f.ToJson()).ToJson());
        });
        output.WriteLine(string.Join("\n", files.Select(f => $"M{f.Number:D2}: {f.World.Instances.Count} instances, {f.World.Links.Count} links")));
    }

    private static string Md5(byte[]? bytes) => bytes == null ? "" : Convert.ToHexStringLower(MD5.HashData(bytes));
}
