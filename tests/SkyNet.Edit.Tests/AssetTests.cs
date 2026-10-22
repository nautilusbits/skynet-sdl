using RetroEng.Editor;
using SkyNet.Data;
using SkyNet.Formats;
using SkyNet.Game.Mods;

namespace SkyNet.Edit.Tests;

/// <summary>The Assets panel's game side (editor-tools plan, T0): MAP.210's assets are listed, and
/// "Override in mod" writes files the game reads back from that mod. Skipped without the game data.</summary>
[Collection(EditModsCollection.Name)]
public sealed class AssetTests : IDisposable
{
    private const string ModName = "assets-test";
    private readonly string _mods = Directory.CreateTempSubdirectory("skynet-edit-assets-").FullName;

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

    private static GameData Data => GameData.Open(EditDocumentTests.DataDir!)!;

    [MapDataFact]
    public void Map_210s_assets_are_listed()
    {
        var assets = new GameAssets(Data);
        var browser = new AssetBrowser([assets]);
        var keys = assets.MapKeys(Data.LoadMap(210)!);
        browser.SetScope("MAP.210", keys);
        browser.ScopeOnly = true;

        // Every kind lists something; the map's props, their textures and its sprites are among them.
        foreach (var kind in browser.Kinds)
        {
            Assert.NotEmpty(browser.Entries(kind));
        }

        foreach (var kind in new[] { GameAssets.Models, GameAssets.Textures, GameAssets.Sprites })
        {
            browser.Kind = kind;
            Assert.NotEmpty(browser.Items);
            Assert.All(browser.Items, e => Assert.Contains(e.Key, keys));
        }

        // Every model the map names is in the data.
        var models = browser.Entries(GameAssets.Models).Select(e => e.Key).ToHashSet();
        Assert.All(keys.Where(k => k.StartsWith(GameAssets.Models + ":", StringComparison.Ordinal)), k => Assert.Contains(k, models));
        Assert.All(browser.Entries(GameAssets.Tables), e => Assert.Equal(AssetOrigin.Base, e.Origin));
    }

    [MapDataFact]
    public void A_texture_override_round_trips_through_a_mod()
    {
        var assets = new GameAssets(Data, modsDir: _mods);
        var browser = new AssetBrowser([assets]);
        var keys = assets.MapKeys(Data.LoadMap(210)!);
        var entry = browser.Entries(GameAssets.Textures).First(e => keys.Contains(e.Key));
        var folder = Path.Combine(_mods, ModName);
        var commands = new AssetCommands(browser) { ModFolder = folder };
        var written = new List<string>();
        commands.Overridden += (_, files) => written.AddRange(files);

        // The panel's menu and the asset.override command take this one path.
        Assert.Contains("overridden in", commands.Override(entry));

        // The game reads it from the mod: the record's own size (1x), the base pixels where index 0 is not.
        Assert.Single(written);
        Assert.True(File.Exists(Path.Combine(folder, written[0])));
        ModContent.Mount(_mods, [ModName]);
        var bank = int.Parse(entry.Id[..3]);
        var record = int.Parse(entry.Id[4..]);
        var hd = ModContent.Texture(bank, record, opaque: true);
        Assert.NotNull(hd);
        var rec = Data.LoadTextureBank(bank)!.Records[record];
        Assert.Equal(rec.Width, hd.Width);
        Assert.Equal(0, hd.Height % rec.Height);

        // Listed again, it is the mod's; models and sounds have no mod path.
        browser.Refresh();
        var again = browser.Entries(GameAssets.Textures).Single(e => e.Id == entry.Id);
        Assert.Equal(AssetOrigin.Mod, again.Origin);
        Assert.Equal(ModName, again.Mod);
        Assert.Equal(Path.Combine(folder, written[0].Replace('/', Path.DirectorySeparatorChar)), browser.PathOf(again));
        Assert.NotNull(browser.OverrideBlocker(browser.Entries(GameAssets.Models)[0], folder));
        Assert.NotNull(browser.OverrideBlocker(browser.Entries(GameAssets.Sounds)[0], folder));
    }

    [MapDataFact]
    public void A_table_override_round_trips_through_a_mod()
    {
        var browser = new AssetBrowser([new GameAssets(Data)]);
        var entry = browser.Entries(GameAssets.Tables).Single(e => e.Id == TableNames.Weapons);
        var folder = Path.Combine(_mods, ModName);

        Assert.Equal(["tables/weapons.json"], browser.Override(entry, folder));

        ModContent.Mount(_mods, [ModName]);
        Assert.Equal($"{ModName}:tables/weapons.json", GameTables.SourceOf(TableNames.Weapons));
        Assert.True(GameTables.Load(TableNames.Weapons, TableSchemas.Weapons, null).Rows.SequenceEqual(GameTables.Weapons));
        browser.Refresh();
        Assert.Equal(AssetOrigin.Mod, browser.Entries(GameAssets.Tables).Single(e => e.Id == TableNames.Weapons).Origin);
    }
}
