using System.IO.Compression;
using System.Text;
using RetroEng.Json;
using SkyNet.Game.Menu;
using SkyNet.Game.Mods;

namespace SkyNet.Game.Tests.Menu;

/// <summary>The MODS screen, its settings, the load-order rules, mod.json and the HI-RES TEXTURES
/// notice (no GPU, no game data).</summary>
public class ModsMenuTests : IDisposable
{
    private static readonly MenuFrame Frame = new(1280, 800);
    private readonly string _dir = Directory.CreateTempSubdirectory("skynet-modsmenu-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static ModEntry Mod(string name, bool system = false, ModCarries carries = ModCarries.None) =>
        new(name, system, !system, system, null, null, carries);

    // ---------------------------------------------------------------- settings

    [Fact]
    public void The_mod_list_and_the_notice_survive_the_settings_file()
    {
        var path = Path.Combine(_dir, SettingsStore.FileName);
        var s = new MenuSettings { Mods = ["cc0-props", "hires-esrgan", "Zip Mod"], HiresNoticeSeen = true };

        Assert.Equal(SaveResult.Saved, SettingsStore.Save(s, path));
        var back = SettingsStore.Load(path);

        Assert.Equal(["cc0-props", "hires-esrgan", "Zip Mod"], back.Mods);
        Assert.True(back.HiresNoticeSeen);
        // A copy does not share the list.
        var copy = back.Clone();
        copy.Mods.Add("x");
        Assert.Equal(3, back.Mods.Count);
    }

    [Fact]
    public void Defaults_are_no_mods_and_a_notice_not_yet_seen()
    {
        var path = Path.Combine(_dir, SettingsStore.FileName);
        File.WriteAllText(path, """{"format":"skynet-csharp-settings","version":1}""");

        var s = SettingsStore.Load(path);

        Assert.Empty(s.Mods);
        Assert.False(s.HiresNoticeSeen);
    }

    [Fact]
    public void A_settings_file_keeps_only_mod_names_once_each()
    {
        var path = Path.Combine(_dir, SettingsStore.FileName);
        File.WriteAllText(path, """
            {"format":"skynet-csharp-settings","version":1,
             "mods":["a", " b ", "A", "", "../evil", "c/d", ".hidden", 7, null, "e"]}
            """);

        Assert.Equal(["a", "b", "e"], SettingsStore.Load(path).Mods);
    }

    // ---------------------------------------------------------------- order and override

    [Fact]
    public void The_command_line_replaces_the_settings_list_and_system_mods_never_join()
    {
        string[] system = ["skynet", "shock"];

        Assert.Equal(["a", "b"], ModOrder.Effective(["a", "b", "SKYNET"], null, system));
        Assert.Equal(["c"], ModOrder.Effective(["a", "b"], ModOrder.Parse(" c ,skynet,,c"), system));
        // --no-mods (an empty --mod): none, whatever the settings say.
        Assert.Empty(ModOrder.Effective(["a", "b"], ModOrder.Parse(""), system));
    }

    [Fact]
    public void Turning_a_mod_on_puts_it_last_so_it_wins_and_off_takes_it_out()
    {
        List<string> on = ["a", "b"];

        ModOrder.Set(on, "c", true);
        ModOrder.Set(on, "A", true);
        Assert.Equal(["a", "b", "c"], on);
        ModOrder.Set(on, "B", false);
        Assert.Equal(["a", "c"], on);
    }

    [Fact]
    public void Moving_up_loads_later_and_skips_mods_that_are_gone()
    {
        // "gone" is in the settings but not in the folder: it keeps its place.
        List<string> on = ["a", "gone", "b", "c"];
        bool Present(string n) => n != "gone";

        Assert.True(ModOrder.Move(on, "a", up: true, Present));
        Assert.Equal(["b", "gone", "a", "c"], on);
        Assert.True(ModOrder.Move(on, "c", up: false, Present));
        Assert.Equal(["b", "gone", "c", "a"], on);
        Assert.False(ModOrder.Move(on, "a", up: true, Present));
        Assert.False(ModOrder.Move(on, "b", up: false, Present));
        Assert.False(ModOrder.Move(on, "nope", up: true, Present));
    }

    [Fact]
    public void Rows_show_the_winner_first_then_the_others_then_the_system_mods()
    {
        ModEntry[] found = [Mod("a"), Mod("b"), Mod("c"), Mod("d"), Mod("skynet", system: true)];

        var rows = ModOrder.Rows(["c", "missing", "a"], found);

        Assert.Equal(["a", "c", "b", "d", "skynet"], rows.Select(r => r.Name));
    }

    // ---------------------------------------------------------------- mod.json

    [Fact]
    public void Mod_json_gives_the_name_version_author_and_description()
    {
        var info = ModInfo.Parse("""
            // comments and a trailing comma are fine
            {"name":"CC0 props","version":"1.2","author":"Somebody","description":"Crates\nand a table ","comment":"x",}
            """u8);

        Assert.Equal(new ModInfo("CC0 props", "1.2", "Somebody", "Crates and a table"), info);
        Assert.Equal(new ModInfo(null, null, null, null), ModInfo.Parse("{}"u8));
        Assert.Null(ModInfo.Parse("""{"name":"  "}"""u8).Name);
    }

    [Theory]
    [InlineData("""{"title":"x"}""", "$.title", "unknown key")]
    [InlineData("""{"name":5}""", "$.name", "expected a string")]
    [InlineData("""{"version":"12345678901234567"}""", "$.version", "longer than 16")]
    [InlineData("""["name"]""", "$", "")]
    [InlineData("""{"name":"a",}x""", "", "")]
    [InlineData("""{"name":"a","name":"b"}""", "", "")]
    public void A_bad_mod_json_says_where(string json, string path, string message)
    {
        var e = Assert.Throws<JsonDataException>(() => ModInfo.Parse(Encoding.UTF8.GetBytes(json)));

        Assert.StartsWith(path, e.Message, StringComparison.Ordinal);
        Assert.Contains(message, e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_huge_mod_json_is_refused()
    {
        var json = Encoding.UTF8.GetBytes("{\"comment\":\"" + new string('x', ModInfo.MaxBytes) + "\"}");

        Assert.Contains("larger than", Assert.Throws<JsonDataException>(() => ModInfo.Parse(json)).Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- the mods folder

    [Fact]
    public void The_catalog_reads_folders_zips_mod_json_and_what_each_carries()
    {
        Write("tex/mod.json", """{"name":"Hi-res","version":"2","author":"Me","description":"Sharper"}""");
        Write("tex/textures/TEXTURE.210/003.png", "png");
        using (var zip = ZipFile.Open(Path.Combine(_dir, "pack.zip"), ZipArchiveMode.Create))
        {
            Entry(zip, "models/CRATE01.glb", "glb");
            Entry(zip, "tables/weapons.json", "{}");
            Entry(zip, "mod.json", """{"name":"From the zip"}""");
        }

        // The folder lies over the zip: its mod.json wins.
        Write("pack/mod.json", """{"name":"From the folder"}""");
        Write("broken/mod.json", """{"name":"x","colour":"red"}""");
        Write("broken/notes.txt", "nothing the game reads");
        File.WriteAllText(Path.Combine(_dir, "bad.zip"), "not a zip at all, not even close to one");
        File.WriteAllText(Path.Combine(_dir, "skynet.zip"), "the system mod is not opened");
        Directory.CreateDirectory(Path.Combine(_dir, ".git"));

        var mods = ModCatalog.Scan(_dir, ["skynet", "shock"]).ToDictionary(m => m.Name);

        Assert.Equal(["bad", "broken", "pack", "skynet", "tex"], mods.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(new ModInfo("Hi-res", "2", "Me", "Sharper"), mods["tex"].Info);
        Assert.Equal((ModCarries.Textures, true, false), (mods["tex"].Carries, mods["tex"].Folder, mods["tex"].Zip));
        Assert.Equal("From the folder", mods["pack"].Title);
        Assert.Equal((ModCarries.Models | ModCarries.Tables, true, true), (mods["pack"].Carries, mods["pack"].Folder, mods["pack"].Zip));
        Assert.Null(mods["broken"].Info);
        Assert.Equal("broken", mods["broken"].Title);
        Assert.StartsWith("mod.json: $.colour", mods["broken"].Problem, StringComparison.Ordinal);
        Assert.Equal(ModCarries.None, mods["broken"].Carries);
        Assert.StartsWith("does not open", mods["bad"].Problem, StringComparison.Ordinal);
        Assert.True(mods["skynet"].System);
        Assert.Null(mods["skynet"].Problem);
        Assert.Empty(ModCatalog.Scan(Path.Combine(_dir, "none"), []));
    }

    // ---------------------------------------------------------------- the screen

    private static MenuSystem OpenMods(MenuSettings settings, IReadOnlyList<ModEntry> found, string? path = null, IReadOnlyList<string>? overrideList = null)
    {
        var menu = new MenuSystem(new SettingsRecorder(), settings, new MenuContext { ModList = () => found, ModsOverride = overrideList }, path);
        menu.OpenTitle();
        foreach (var id in new[] { "options", "mods" })
        {
            menu.Current!.Select(id);
            menu.Update(new MenuInput { Accept = true }, Frame);
        }

        Assert.Equal("mods", menu.Current!.Name);
        return menu;
    }

    [Fact]
    public void Options_opens_mods_where_accept_turns_a_mod_on_and_it_is_saved()
    {
        var path = Path.Combine(_dir, SettingsStore.FileName);
        var settings = new MenuSettings();
        var menu = OpenMods(settings, [Mod("a"), Mod("b"), Mod("skynet", system: true)], path);
        var screen = (ModsScreen)menu.Current!;

        Assert.Equal(["a", "b", "skynet"], screen.Rows.Select(r => r.Name));
        Assert.Equal("row0", screen.Highlighted!.Id);
        menu.Update(new MenuInput { Down = true }, Frame);
        menu.Update(new MenuInput { Accept = true }, Frame);

        Assert.Equal(["b"], settings.Mods);
        Assert.Equal(["b"], SettingsStore.Load(path).Mods);
        // b is on: it heads the list and stays selected.
        Assert.Equal(["b", "a", "skynet"], screen.Rows.Select(r => r.Name));
        Assert.Equal("b", screen.Selected!.Name);
        Assert.Null(screen.Note);
    }

    [Fact]
    public void Up_and_down_move_the_selected_mod_and_system_mods_stay_locked()
    {
        var settings = new MenuSettings { Mods = ["a", "b"] };
        var menu = OpenMods(settings, [Mod("a"), Mod("b"), Mod("c"), Mod("skynet", system: true)]);
        var screen = (ModsScreen)menu.Current!;
        Assert.Equal(["b", "a", "c", "skynet"], screen.Rows.Select(r => r.Name));

        // a (second row) up: it loads last and wins.
        menu.Update(new MenuInput { Down = true }, Frame);
        menu.Current!.Select("up");
        menu.Update(new MenuInput { Accept = true }, Frame);
        Assert.Equal(["b", "a"], settings.Mods);
        Assert.Equal("a", screen.Selected!.Name);
        menu.Current!.Select("down");
        menu.Update(new MenuInput { Accept = true }, Frame);
        Assert.Equal(["a", "b"], settings.Mods);

        // Left from the buttons goes back to the mod they move.
        menu.Update(new MenuInput { Left = true }, Frame);
        Assert.Equal("a", screen.Selected!.Name);
        Assert.StartsWith("row", screen.Highlighted!.Id, StringComparison.Ordinal);

        // c is off: it cannot move; skynet is a system mod: it cannot be turned off.
        screen.Select(2, scroll: true);
        menu.Current!.Select("up");
        menu.Update(new MenuInput { Accept = true }, Frame);
        Assert.Equal("TURN THE MOD ON FIRST", menu.Toast);
        screen.Select(3, scroll: true);
        menu.Current!.Select("row3");
        menu.Update(new MenuInput { Accept = true }, Frame);
        Assert.Equal("SYSTEM MODS ARE ALWAYS ON", menu.Toast);
        Assert.Equal(["a", "b"], settings.Mods);
    }

    [Fact]
    public void A_long_list_scrolls_with_the_selection()
    {
        var found = Enumerable.Range(0, 12).Select(i => Mod($"m{i:D2}")).ToArray();
        var menu = OpenMods(new MenuSettings(), found);
        var screen = (ModsScreen)menu.Current!;

        for (var i = 0; i < 11; i++)
        {
            menu.Update(new MenuInput { Down = true }, Frame);
        }

        Assert.Equal("m11", screen.Selected!.Name);
        Assert.Equal(12 - ModsScreen.VisibleRows, screen.Top);
        Assert.Equal($"row{ModsScreen.VisibleRows - 1}", screen.Highlighted!.Id);
        menu.Update(new MenuInput { Accept = true }, Frame);
        // Turned on, it moves to the top and the list follows it.
        Assert.Equal(["m11"], menu.Settings.Mods);
        Assert.Equal(0, screen.Top);
        Assert.Equal("row0", screen.Highlighted!.Id);
    }

    /// <summary>A DOS point of the MODS panel (its own coordinates) as the pointer's canvas point.</summary>
    private static System.Numerics.Vector2 At(MenuSystem menu, float x, float y)
    {
        var origin = menu.Current!.Find("row0")!.Rect.Location - (System.Drawing.Size)ModsScreen.RowRect(0).Location;
        return menu.Current.FrameIn(Frame).CanvasPoint(origin.X + x, origin.Y + y);
    }

    private static MenuSystem OpenLong(int count) =>
        OpenMods(new MenuSettings(), Enumerable.Range(0, count).Select(i => Mod($"m{i:D2}")).ToArray());

    [Fact]
    public void The_wheel_scrolls_the_list()
    {
        var menu = OpenLong(12);
        var screen = (ModsScreen)menu.Current!;

        menu.Update(new MenuInput { Wheel = -1 }, Frame);
        Assert.True(screen.Top > 0);
        menu.Update(new MenuInput { Wheel = 10 }, Frame);
        Assert.Equal(0, screen.Top);
        menu.Update(new MenuInput { Wheel = -10 }, Frame);
        Assert.Equal(12 - ModsScreen.VisibleRows, screen.Top);
        // The selection stays in view.
        Assert.InRange(screen.Rows.ToList().IndexOf(screen.Selected!), screen.Top, screen.Top + ModsScreen.VisibleRows - 1);
    }

    [Fact]
    public void The_scrollbar_drags_and_its_track_pages()
    {
        var menu = OpenLong(20);
        var screen = (ModsScreen)menu.Current!;
        var track = ModsScreen.TrackRect;
        var x = track.X + track.Width / 2f;

        // A click on the track below the thumb pages down.
        menu.Update(new MenuInput { Pointer = At(menu, x, track.Bottom - 1), Click = true }, Frame);
        Assert.Equal(ModsScreen.VisibleRows - 1, screen.Top);
        menu.Update(new MenuInput { Pointer = At(menu, x, track.Y + 1), Click = true }, Frame);
        Assert.Equal(0, screen.Top);

        // The thumb dragged to the bottom: the list's end; let go, the pointer moves freely.
        menu.Update(new MenuInput { Pointer = At(menu, x, track.Y + 1), Click = true, Held = true }, Frame);
        menu.Update(new MenuInput { Pointer = At(menu, x, track.Bottom + 20), Held = true }, Frame);
        Assert.Equal(20 - ModsScreen.VisibleRows, screen.Top);
        menu.Update(new MenuInput { Pointer = At(menu, x, track.Y) }, Frame);
        Assert.Equal(20 - ModsScreen.VisibleRows, screen.Top);
    }

    [Fact]
    public void Dragging_the_rows_scrolls_them_as_a_finger_does()
    {
        var menu = OpenLong(20);
        var screen = (ModsScreen)menu.Current!;
        var x = ModsScreen.RowRect(0).X + ModsScreen.ToggleWidth + 40;
        var y = ModsScreen.RowRect(5).Y + 4;

        menu.Update(new MenuInput { Pointer = At(menu, x, y), Click = true, Held = true }, Frame);
        menu.Update(new MenuInput { Pointer = At(menu, x, y - 3 * ModsScreen.RowPitch), Held = true }, Frame);

        Assert.Equal(3, screen.Top);
        Assert.Empty(menu.Settings.Mods);
    }

    [Fact]
    public void Under_the_command_line_the_screen_says_so()
    {
        var menu = OpenMods(new MenuSettings(), [Mod("a")], overrideList: ["a"]);

        Assert.Equal(ModsScreen.OverrideNote, ((ModsScreen)menu.Current!).Note);
    }

    [Fact]
    public void An_empty_folder_leaves_exit()
    {
        var menu = OpenMods(new MenuSettings(), []);

        Assert.Equal("exit", menu.Current!.Highlighted!.Id);
        menu.Update(new MenuInput { Accept = true }, Frame);
        Assert.Equal("options", menu.Current!.Name);
    }

    [Fact]
    public void What_a_row_carries_and_is()
    {
        Assert.Equal("CARRIES TEXTURES, TABLES", ModsScreen.CarriesLine(Mod("a", carries: ModCarries.Textures | ModCarries.Tables)));
        Assert.Equal("CARRIES NOTHING THE GAME READS", ModsScreen.CarriesLine(Mod("a")));
        Assert.Equal("ZIP+DIR", ModsScreen.Kind(new ModEntry("a", false, true, true, null, null, ModCarries.None)));
        Assert.Equal("SYSTEM", ModsScreen.Kind(Mod("skynet", system: true)));
    }

    // ---------------------------------------------------------------- the notice

    private static MenuSystem Title(MenuSettings s, bool textures, string? path = null)
    {
        var menu = new MenuSystem(new SettingsRecorder(), s,
            new MenuContext { EnhancedAvailable = true, HiresTextures = () => textures, ModList = () => [] }, path);
        menu.OpenTitle();
        return menu;
    }

    [Fact]
    public void The_notice_comes_once_under_the_enhanced_look_without_texture_mods()
    {
        var path = Path.Combine(_dir, SettingsStore.FileName);
        var s = new MenuSettings { Look = Look.Enhanced };
        var menu = Title(s, textures: false, path);

        Assert.Equal("hires", menu.Current!.Name);
        Assert.Equal("ok", menu.Current.Highlighted!.Id);
        menu.Update(new MenuInput { Accept = true }, Frame);
        Assert.Equal("title", menu.Current!.Name);
        Assert.True(SettingsStore.Load(path).HiresNoticeSeen);

        // Seen: never again.
        Assert.Equal("title", Title(s, textures: false).Current!.Name);
    }

    [Fact]
    public void No_notice_under_the_original_look_or_with_a_texture_mod()
    {
        Assert.Equal("title", Title(new MenuSettings { Look = Look.Original }, textures: false).Current!.Name);
        Assert.Equal("title", Title(new MenuSettings { Look = Look.Enhanced }, textures: true).Current!.Name);
        // Tests and builds that do not say: never.
        var menu = new MenuSystem(new SettingsRecorder(), new MenuSettings { Look = Look.Enhanced }, new MenuContext { EnhancedAvailable = true });
        menu.OpenTitle();
        Assert.Equal("title", menu.Current!.Name);
    }

    [Fact]
    public void Turning_the_look_enhanced_brings_the_notice_and_mods_opens_the_screen()
    {
        var s = new MenuSettings { Look = Look.Original };
        var menu = Title(s, textures: false);
        Assert.Equal("title", menu.Current!.Name);

        s.Look = Look.Enhanced;
        menu.Changed();
        Assert.Equal("hires", menu.Current!.Name);
        menu.Current.Select("mods");
        menu.Update(new MenuInput { Accept = true }, Frame);

        Assert.True(s.HiresNoticeSeen);
        Assert.Equal("mods", menu.Current!.Name);
        menu.Update(new MenuInput { Back = true }, Frame);
        Assert.Equal("title", menu.Current!.Name);
    }

    private void Write(string relative, string text)
    {
        var path = Path.Combine(_dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private static void Entry(ZipArchive zip, string name, string text)
    {
        using var s = zip.CreateEntry(name).Open();
        s.Write(Encoding.UTF8.GetBytes(text));
    }
}
