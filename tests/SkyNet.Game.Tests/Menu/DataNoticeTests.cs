using SkyNet.Formats;
using SkyNet.Game.Menu;
using SkyNet.Game.Storage;

namespace SkyNet.Game.Tests.Menu;

/// <summary>UNKNOWN GAME DATA (issue #20): the notice over the title for data of an unknown release.</summary>
public sealed class DataNoticeTests
{
    private static readonly MenuFrame Frame = new(1280, 800);

    private sealed class QuitRecorder : IMenuActions
    {
        public bool Quit { get; private set; }
        public void StartGame(GameKind game, int mission, SkyNet.Game.Campaign.Pilot pilot) { }
        public void BeginMission() { }
        public void Resume() { }
        public void RestartMission() { }
        public void QuitToTitle() { }
        void IMenuActions.Quit() => Quit = true;
        public void LoadGame(int slot) { }
        public bool SaveGame(int slot) => true;
        public void SettingsChanged(MenuSettings settings) { }
        public void Sound(string name) { }
    }

    private static readonly DataCheck Shock = new(GameKind.FutureShock,
        [new("MDMDMAPS.BSA", 1, "a"), new("MDMDOBJS.BSA", 1, "b")], ["MDMDMAPS.BSA", "MDMDOBJS.BSA"]);

    private static MenuSystem Title(QuitRecorder actions, params DataCheck[] unknown) => Title(actions, new MenuSettings(), unknown);

    private static MenuSystem Title(QuitRecorder actions, MenuSettings settings, params DataCheck[] unknown)
    {
        var menu = new MenuSystem(actions, settings, new MenuContext { UnknownData = unknown });
        menu.OpenTitle();
        return menu;
    }

    [Fact]
    public void The_notice_names_the_game_and_files_and_continue_goes_on()
    {
        var menu = Title(new QuitRecorder(), Shock);

        Assert.Equal("data", menu.Current!.Name);
        var text = DataNotice.Text([Shock]);
        Assert.Contains("FUTURE SHOCK", text);
        Assert.Contains("MDMDMAPS.BSA, MDMDOBJS.BSA", text);
        Assert.Equal("continue", menu.Current.Highlighted!.Id);

        menu.Update(new MenuInput { Accept = true }, Frame);
        Assert.Equal("title", menu.Current!.Name);

        // Once a run: the title again has no notice.
        menu.OpenTitle();
        Assert.Equal("title", menu.Current!.Name);
    }

    [Fact]
    public void Quit_quits_and_known_data_brings_no_notice()
    {
        var actions = new QuitRecorder();
        var menu = Title(actions, Shock);
        menu.Current!.Select("quit");
        menu.Update(new MenuInput { Accept = true }, Frame);
        Assert.True(actions.Quit);

        Assert.Equal("title", Title(new QuitRecorder()).Current!.Name);
    }

    [Fact]
    public void Ignore_hides_the_notice_for_this_data_only()
    {
        var settings = new MenuSettings();
        var menu = Title(new QuitRecorder(), settings, Shock);
        menu.Current!.Select("ignore");
        menu.Update(new MenuInput { Accept = true }, Frame);
        Assert.Equal("title", menu.Current!.Name);
        Assert.Equal(DataNotice.Key([Shock]), settings.IgnoredData);

        // The next start with the same data: no notice. Other data: the notice again.
        Assert.Equal("title", Title(new QuitRecorder(), settings, Shock).Current!.Name);
        var other = Shock with { Files = [new("MDMDMAPS.BSA", 1, "c")] };
        Assert.Equal("data", Title(new QuitRecorder(), settings, other).Current!.Name);
    }

    [Fact]
    public void A_long_list_is_cut_short()
    {
        string[] names = ["A.BSA", "B.BSA", "C.BSA", "D.BSA", "E.BSA", "F.BSA"];
        var check = new DataCheck(GameKind.SkyNet, [], names);

        var text = DataNotice.Text([check]);
        Assert.Contains("SKYNET", text);
        Assert.Contains("A.BSA, B.BSA, C.BSA, D.BSA AND 2 MORE", text);
    }
}
