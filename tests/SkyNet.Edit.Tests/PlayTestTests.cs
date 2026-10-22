using System.Diagnostics;
using System.Numerics;
using SkyNet.Formats;
using SkyNet.Game.DevTools;
using SkyNet.Game.Mods;

namespace SkyNet.Edit.Tests;

/// <summary>Play > Play-test in game window: the scratch mod the editor writes, the game's command line,
/// and the reload's round trip over the editor link (both ends in this process).</summary>
public sealed class PlayTestTests
{
    [MapDataFact]
    public void The_play_test_mod_holds_every_layer_and_leaves_the_document_unsaved()
    {
        var data = GameData.Open(EditDocumentTests.DataDir!)!;
        MapFile raw;
        using (var a = data.OpenMapArchive())
        {
            raw = MapFile.Parse(a.Read("MAP.210")!)!;
        }

        var doc = new EditDocument();
        doc.AddLayer("MAP.210", 210, raw);
        doc.AddLayer("MAP.211", 210, raw);
        var key = raw.Entities[0].FileOffset;
        doc.SetField("MAP.210", key, "x", raw.Entities[0].X + 64);
        Assert.True(doc.IsDirty);

        var mods = Path.Combine(Path.GetTempPath(), "skynet-playtest-" + Guid.NewGuid().ToString("N"));
        try
        {
            var written = PlayTest.Write(doc, mods);
            var folder = PlayTest.Folder(mods);
            Assert.Equal(Path.Combine(mods, "_playtest"), folder);
            Assert.Contains(MapLayer.PathOf("MAP.210"), written);
            Assert.Contains(MapLayer.PathOf("MAP.211"), written);
            Assert.True(File.Exists(Path.Combine(folder, ModInfo.FileName)));
            Assert.Equal(doc.Layer("MAP.210").ToBytes(), File.ReadAllBytes(Path.Combine(folder, MapLayer.PathOf("MAP.210"))));
            Assert.True(MapLayer.Parse(File.ReadAllBytes(Path.Combine(folder, MapLayer.PathOf("MAP.211")))).IsEmpty);
            Assert.True(doc.IsDirty);

            // Written again (the hot reload): the new edit replaces the old file.
            doc.SetField("MAP.210", key, "x", raw.Entities[0].X + 128);
            PlayTest.Write(doc, mods);
            Assert.Equal(doc.Layer("MAP.210").ToBytes(), File.ReadAllBytes(Path.Combine(folder, MapLayer.PathOf("MAP.210"))));

            PlayTest.Delete(mods);
            Assert.False(Directory.Exists(folder));
            Assert.True(Directory.Exists(mods));
        }
        finally
        {
            if (Directory.Exists(mods))
            {
                Directory.Delete(mods, recursive: true);
            }
        }
    }

    [Fact]
    public void The_game_command_line_puts_the_play_test_mod_on_top_of_the_user_mods()
    {
        var args = PlayTest.Arguments(@"C:\data", @"C:\mods", ["hires", "_playtest", "mine"], 210, 50123);
        Assert.Equal(["--data", @"C:\data", "--mods-dir", @"C:\mods", "--mod", "hires,mine,_playtest", "--map", "210", "--no-briefing",
            "--editor-link", "50123"], args);

        var shifted = PlayTest.Arguments(null, @"C:\mods", [], 231, 1, new Vector3(1.5f, -20f, 300.25f), 90f);
        Assert.Equal(["--mods-dir", @"C:\mods", "--mod", "_playtest", "--map", "231", "--no-briefing", "--editor-link", "1",
            "--pos", "1.5,-20,300.25", "--yaw", "90"], shifted);
        Assert.Equal(OperatingSystem.IsWindows() ? "skynet-sdl.exe" : "skynet-sdl", PlayTest.GameExe);
    }

    [Fact]
    public void A_reload_goes_from_the_editor_to_the_game_and_its_answer_comes_back()
    {
        using var host = new EditorLinkHost();
        Assert.False(host.SendReload());
        using var game = new EditorLinkClient(host.Port);
        Assert.True(Until(() => host.Poll().Count == 0 && host.Connected));

        Assert.True(host.SendReload());
        Assert.True(host.SendReload());
        var reloads = 0;
        Assert.True(Until(() => (reloads += game.PollReloads()) == 2));

        game.Send($"{EditorLink.Reloaded} MAP.210");
        var lines = new List<string>();
        Assert.True(Until(() => { lines.AddRange(host.Poll()); return lines.Count > 0; }));
        Assert.Equal(["reloaded MAP.210"], lines);

        // A stray datagram is no reload.
        using var stray = new EditorLinkClient(host.Port);
        Assert.Equal(0, game.PollReloads());
    }

    private static bool Until(Func<bool> done)
    {
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < 5000)
        {
            if (done())
            {
                return true;
            }

            Thread.Sleep(5);
        }

        return false;
    }
}
