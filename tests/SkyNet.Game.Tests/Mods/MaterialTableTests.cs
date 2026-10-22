using System.Text;
using RetroEng.Json;
using RetroEng.Render;
using SkyNet.Formats;
using SkyNet.Game.Enhanced;
using SkyNet.Game.Mods;

namespace SkyNet.Game.Tests.Mods;

/// <summary><c>materials.json</c> (<see cref="MaterialTable"/>): what it reads, which entry wins for a
/// record, how tables stack over the mods' load order (<see cref="Materials"/>), and the shipped one.</summary>
[Collection(ModsCollection.Name)]
public class MaterialTableTests
{
    private static MaterialTable Parse(string json) => MaterialTable.Parse(Encoding.UTF8.GetBytes(json));

    [Fact]
    public void A_record_takes_its_own_entry_then_the_last_pattern_then_its_bank_then_the_default()
    {
        var t = Parse("""
            { // comments and trailing commas are fine
              "default": "wood",
              "banks": { "210": "stone" },
              "textures": { "210/005": "metal", "230/14": { "preset": "painted", "roughness": 0.5, "specular": 2 } },
              "patterns": { "21?/00*": "concrete", "210/00?": "glass", },
            }
            """);

        Assert.Equal(new Surface(SurfacePreset.Metal), t.Resolve(210, 5));
        Assert.Equal(new Surface(SurfacePreset.Painted, 0.5f, 2f), t.Resolve(230, 14));
        // Both patterns match 210/003: the later one wins; 211/004 only the first.
        Assert.Equal(SurfacePreset.Glass, t.Resolve(210, 3).Preset);
        Assert.Equal(SurfacePreset.Concrete, t.Resolve(211, 4).Preset);
        Assert.Equal(SurfacePreset.Stone, t.Resolve(210, 20).Preset);
        Assert.Equal(SurfacePreset.Wood, t.Resolve(300, 1).Preset);
        Assert.Equal(SurfacePreset.Stone, t.ResolveBank(210).Preset);
    }

    [Fact]
    public void Without_any_entry_every_record_is_matte()
    {
        Assert.Equal(Surface.Matte, MaterialTable.Empty.Resolve(210, 1));
        Assert.Equal(Surface.Matte, Parse("{}").ResolveBank(302));
    }

    [Fact]
    public void A_later_table_wins_key_by_key()
    {
        var shipped = Parse("""{ "default": "stone", "banks": { "210": "wood" }, "textures": { "210/001": "metal", "210/002": "glass" } }""");
        var mod = Parse("""{ "textures": { "210/002": "matte" }, "patterns": { "210/*": "painted" } }""");
        var t = shipped.Over(mod);

        Assert.Equal(SurfacePreset.Stone, t.Default!.Value.Preset);
        Assert.Equal(SurfacePreset.Metal, t.Resolve(210, 1).Preset);
        Assert.Equal(SurfacePreset.Matte, t.Resolve(210, 2).Preset);
        // The mod's pattern lies over the shipped bank.
        Assert.Equal(SurfacePreset.Painted, t.Resolve(210, 3).Preset);
        Assert.Equal(SurfacePreset.Stone, t.Resolve(211, 3).Preset);
    }

    [Theory]
    [InlineData("""{ "colour": "red" }""")]
    [InlineData("""{ "default": "chrome" }""")]
    [InlineData("""{ "default": "3" }""")]
    [InlineData("""{ "banks": { "two": "stone" } }""")]
    [InlineData("""{ "textures": { "210-001": "stone" } }""")]
    [InlineData("""{ "textures": { "210/001": { "preset": "stone", "gloss": 1 } } }""")]
    [InlineData("""{ "textures": { "210/001": { "roughness": 1 } } }""")]
    [InlineData("""{ "textures": { "210/001": { "preset": "stone", "roughness": -1 } } }""")]
    [InlineData("""{ "patterns": { "210/a*": "stone" } }""")]
    public void A_bad_file_is_refused(string json) =>
        Assert.Throws<JsonDataException>(() => Parse(json));

    [Fact]
    public void Screen_is_glass_and_names_ignore_case()
    {
        Assert.Equal(SurfacePreset.Glass, MaterialTable.PresetOf("Screen"));
        Assert.Equal(SurfacePreset.Emissive, MaterialTable.PresetOf("EMISSIVE"));
        Assert.Null(MaterialTable.PresetOf("4"));
        Assert.Null(MaterialTable.PresetOf(""));
    }

    [Fact]
    public void Written_and_read_back_it_is_the_same_table()
    {
        var t = Parse("""
            { "default": "matte", "banks": { "210": "stone" },
              "textures": { "230/014": "metal", "230/015": { "preset": "painted", "roughness": 0.8 } },
              "patterns": { "23?/1*": "concrete" } }
            """);
        var again = MaterialTable.Parse(t.ToJson("a note"));

        Assert.Equal(t.Default, again.Default);
        Assert.Equal(t.Banks, again.Banks);
        Assert.Equal(t.Textures, again.Textures);
        Assert.Equal(t.Patterns, again.Patterns);
        Assert.Contains("\"230/014\": \"metal\"", Encoding.UTF8.GetString(t.ToJson()));
    }

    [Theory]
    [InlineData("23?/01*", "230/014", true)]
    [InlineData("23?/01*", "240/014", false)]
    [InlineData("23?/1*", "230/014", false)]
    [InlineData("*", "001/002", true)]
    [InlineData("210/00?", "210/0012", false)]
    public void Patterns_are_globs(string pattern, string key, bool matches) =>
        Assert.Equal(matches, MaterialTable.Matches(pattern, key));

    [Fact]
    public void The_mods_stack_in_load_order_over_the_shipped_table_and_a_bad_file_is_left_out()
    {
        var root = Directory.CreateTempSubdirectory("skynet-materials-").FullName;
        try
        {
            Write(root, "a", """{ "default": "stone", "textures": { "210/001": "wood", "210/002": "wood" } }""");
            Write(root, "b", """{ "textures": { "210/002": "metal" } }""");
            Write(root, "bad", """{ "textures": { "210/001": "chrome" } }""");
            var shipped = Parse("""{ "textures": { "210/003": "glass" } }""");

            ModContent.Mount(root, ["a", "bad", "b"]);
            var t = Materials.Stack(shipped, ModContent.Files);

            Assert.Equal(SurfacePreset.Wood, t.Resolve(210, 1).Preset);
            Assert.Equal(SurfacePreset.Metal, t.Resolve(210, 2).Preset);
            Assert.Equal(SurfacePreset.Glass, t.Resolve(210, 3).Preset);
            Assert.Equal(SurfacePreset.Stone, t.Resolve(210, 4).Preset);

            // In the other order a's entry wins.
            ModContent.Mount(root, ["b", "a"]);
            Assert.Equal(SurfacePreset.Wood, Materials.Stack(shipped, ModContent.Files).Resolve(210, 2).Preset);
        }
        finally
        {
            ModContent.Unmount();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Each_game_ships_a_table_of_its_own()
    {
        foreach (var game in new[] { GameKind.SkyNet, GameKind.FutureShock })
        {
            var shipped = Materials.Shipped(game);
            Assert.Equal(Surface.Matte, shipped.Default);
            Assert.NotEmpty(shipped.Textures);
            Assert.DoesNotContain(shipped.Textures.Values, s => s.Preset == SurfacePreset.Matte);
        }

        Assert.Empty(Materials.Shipped(GameKind.None).Textures);
    }

    [Fact]
    public void Future_Shock_mode_resolves_through_its_own_table()
    {
        ModContent.Unmount();

        // Same bank numbers, other pictures: FS's 204/000 is a drum, SkyNET's is not.
        Assert.Equal(SurfacePreset.Metal, Materials.Current(GameKind.FutureShock).Resolve(204, 0).Preset);
        Assert.Equal(SurfacePreset.Concrete, Materials.Current(GameKind.SkyNet).Resolve(204, 0).Preset);

        // FS's hand picks are in its table: a poster left matte, rock instead of the guess's wood.
        var fs = Materials.Current(GameKind.FutureShock);
        Assert.Equal(Surface.Matte, fs.Resolve(378, 2));
        Assert.Equal(SurfacePreset.Stone, fs.Resolve(300, 0).Preset);
    }

    [Theory]
    [InlineData(GameKind.SkyNet)]
    [InlineData(GameKind.FutureShock)]
    public void Bank_378s_framed_pictures_are_matte(GameKind game)
    {
        ModContent.Unmount();

        // 378/000-006: paintings and film posters in both games, not stone.
        var table = Materials.Current(game);
        for (var record = 0; record <= 6; record++)
        {
            Assert.Equal(Surface.Matte, table.Resolve(378, record));
        }
    }

    [Fact]
    public void The_guess_tells_lamps_screens_wood_and_metal_apart()
    {
        var none = new EmissionResult(false, 0f, default, 0, 0, null);
        var lamp = new EmissionResult(true, 0.03f, new System.Numerics.Vector3(1f, 0.95f, 0.8f), 30, 1000, null);
        var screen = lamp with { MeanLitColour = new System.Numerics.Vector3(0.2f, 1f, 0.3f) };
        var dullDark = new MaterialGuess.Stats(0.3f, 0.1f, 0.02f, 0.1f, -1f, 0.03f);

        Assert.Equal(SurfacePreset.Emissive, MaterialGuess.Guess(dullDark, lamp));
        Assert.Equal(SurfacePreset.Glass, MaterialGuess.Guess(dullDark, screen));
        Assert.Equal(SurfacePreset.Wood, MaterialGuess.Guess(new MaterialGuess.Stats(0.35f, 0.05f, 0.02f, 0.4f, 25f, -0.15f), none));
        Assert.Equal(SurfacePreset.Metal, MaterialGuess.Guess(new MaterialGuess.Stats(0.5f, 0.08f, 0.015f, 0.07f, -1f, 0.04f), none));
        // The same grey, warm: plaster, not metal.
        Assert.NotEqual(SurfacePreset.Metal, MaterialGuess.Guess(new MaterialGuess.Stats(0.5f, 0.08f, 0.015f, 0.07f, -1f, -0.05f), none));
        // A cut-out (an object's skin) stays matte.
        Assert.Equal(SurfacePreset.Matte, MaterialGuess.Guess(new MaterialGuess.Stats(0.5f, 0.08f, 0.015f, 0.07f, -1f, 0.04f, 0.4f), none));
    }

    private static void Write(string root, string mod, string json)
    {
        Directory.CreateDirectory(Path.Combine(root, mod));
        File.WriteAllText(Path.Combine(root, mod, MaterialTable.FileName), json);
    }
}
