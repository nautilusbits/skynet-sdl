using System.Collections.Immutable;
using System.Diagnostics;
using System.Numerics;
using RetroEng.Net;
using SkyNet.Formats;
using SkyNet.Game.Campaign;
using SkyNet.Game.Mods;
using SkyNet.Game.Net;

namespace SkyNet.Edit.Tests;

/// <summary>Deathmatch arenas and Future Shock in the editor: an arena opens in the window, a spawn
/// marker moved and saved to a mod is where the deathmatch host puts its player, the arena palette
/// offers the deathmatch markers, and every Future Shock mission opens (--check-missions --game shock).</summary>
[Collection(EditModsCollection.Name)]
public sealed class ArenaTests : IDisposable
{
    private const int Map = 605;
    private readonly string _mods = Directory.CreateTempSubdirectory("skynet-edit-arena-").FullName;

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
    public void The_arena_palette_offers_the_deathmatch_markers()
    {
        var data = GameData.Open(EditDocumentTests.DataDir!)!;
        Assert.Contains(ArenaWorld.Levels(data), l => l.Map == Map);
        var doc = EditDocument.Load(data, ArenaWorld.Build(Map), null);
        var types = PlacePalette.Items(PlaceKind.Marker, doc.Map(MissionFile.MapName(Map)), deathmatch: true)
            .Select(i => i.Fields["sprite"] & 0x7F).ToHashSet();
        foreach (var t in DmArena.SpawnSets.SelectMany(s => new[] { s, s + 1 }).Append(DmArena.ItemMarker).Append(DmArena.JeepMarker).Append(DmArena.HkMarker))
        {
            Assert.Contains(t, types);
        }
    }

    [MapDataFact]
    public void A_moved_spawn_marker_saved_to_a_mod_is_where_the_host_spawns_its_player()
    {
        var data = GameData.Open(EditDocumentTests.DataDir!)!;
        var doc = EditDocument.Load(data, ArenaWorld.Build(Map), null);
        var layer = MissionFile.MapName(Map);
        var spawns = doc.Map(layer).Entities.Where(e => e.IsMarker && DmArena.SpawnSets.Contains(e.MarkerType)).ToList();
        Assert.True(spawns.Count > 1);

        // One spawn left, moved 150 units along X; the host has nowhere else to put its player.
        var kept = spawns.First(e => e.MarkerType >= 10);
        foreach (var e in spawns.Where(e => e.FileOffset != kept.FileOffset))
        {
            doc.DeleteEntity(layer, e.FileOffset);
        }

        doc.SetFields(layer, kept.FileOffset, new Dictionary<string, int> { ["x"] = kept.X + 150 }, label: "Move");
        doc.EndDrag();
        doc.Save(SaveTarget.Folder(_mods, "arena", ["skynet", "shock"], out _)!);
        Assert.True(File.Exists(Path.Combine(_mods, "arena", "maps", layer + ".layer.json")));

        ModContent.Mount(_mods, ["arena"]);
        var session = new GameSession(data);
        Assert.True(session.StartDeathmatch(Map));
        var net = new LoopbackNetwork();
        using var host = new DmHost(new NetEndpoint(net.Listen(0, DmProtocol.MaxPeers)!),
            new DmServer(new DmSettings { Map = Map, Bots = 0, Items = ImmutableDictionary<string, int>.Empty }, new Random(4)), "HOST", DmClass.Human);
        host.Server.SetArena(DmBots.Cleared(DmArena.Load(data, Map)!, session));
        host.ReportLevelReady();
        var at = host.Server.Players[host.LocalId].Position;
        var moved = new Vector2(kept.X + 150, -kept.Z);
        Assert.True(Vector2.Distance(new Vector2(at.X, at.Z), moved) < 40f, $"spawned at {at}, the marker is at {moved}");
    }

    [MapDataFact]
    public void An_arena_opens_in_the_editor() =>
        RunEditor("--arena", Map.ToString(System.Globalization.CultureInfo.InvariantCulture), "--frames", "3");

    [ShockDataFact]
    public void Every_Future_Shock_mission_opens() => RunEditor("--check-missions", "--game", "shock");

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

    private void RunEditor(params string[] args)
    {
        var exe = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "SkyNetEdit.exe" : "SkyNetEdit");
        Assert.True(File.Exists(exe), exe + " is not built beside the tests");
        var info = new ProcessStartInfo(exe) { RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var a in args.Concat(["--data", EditDocumentTests.DataDir!, "--mods-dir", _mods, "--prefs", Path.Combine(_mods, "prefs.json"),
            "--width", "640", "--height", "360", "--console-log"]))
        {
            info.ArgumentList.Add(a);
        }

        info.Environment["SKYNET_OFFSCREEN"] = "1";
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync();
        // A fresh mods dir bakes the whole 223 MB shock.zip first; with 19 missions that took
        // 121-234 s on the same build here, so 240 s failed at random.
        Assert.True(process.WaitForExit(480_000), "SkyNetEdit " + string.Join(' ', args) + " did not finish");
        Assert.True(process.ExitCode == 0, output.Result);
    }
}
