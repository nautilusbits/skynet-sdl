using RetroEng.Audio;
using SkyNet.Game.Campaign;
using SkyNet.Formats;
using SkyNet.Game.Menu;

namespace SkyNet.Game.Tests.Menu;

public class MovieClockTests
{
    [Fact]
    public void Sound_drives_while_it_moves()
    {
        var clock = new MovieClock();
        clock.Advance(0.1, 0.05);
        Assert.Equal(0.05, clock.Now, 9);
        clock.Advance(0.1, 0.3);
        Assert.Equal(0.3, clock.Now, 9);
    }

    [Fact]
    public void Wall_clock_without_sound()
    {
        var clock = new MovieClock();
        clock.Advance(0.1, null);
        clock.Advance(0.2, null);
        Assert.Equal(0.3, clock.Now, 9);
    }

    [Fact]
    public void A_stalled_sound_hands_over_to_the_wall_clock()
    {
        var clock = new MovieClock();
        clock.Advance(0.1, 0.0);
        clock.Advance(0.1, 0.0);
        Assert.Equal(0.0, clock.Now, 9);

        // No device mixing: past the stall limit the wall clock moves the pictures on.
        clock.Advance(MovieClock.StallSeconds, 0.0);
        clock.Advance(0.1, 0.0);
        Assert.Equal(MovieClock.StallSeconds + 0.1, clock.Now, 9);
    }
}

public class MovieScreenTests
{
    private static readonly MenuFrame Frame = new(1280, 800);

    public enum Sounding { Silent, Loud }

    // Three frames of 4 ticks (740 samples each, 66.6 ms), silent; 40 ticks: 0.67 s each.
    // Loud: 0.1 s of a full-scale 11 111 Hz sound.
    private static VidFile Movie(ushort ticks = 4, Sounding sound = Sounding.Silent)
    {
        var b = new List<byte>();
        b.AddRange("VID\0"u8.ToArray());
        b.Add(2);
        foreach (var v in new ushort[] { 3, 2, 1, ticks, 14 })
        {
            b.AddRange(BitConverter.GetBytes(v));
        }

        b.Add(0x02);
        b.AddRange(new byte[768]);
        if (sound == Sounding.Loud)
        {
            const ushort Samples = 1111;
            b.AddRange([0x7C, 0, 0, 256 - 90]);
            b.AddRange(BitConverter.GetBytes(Samples));
            b.AddRange(Enumerable.Repeat((byte)255, Samples));
        }

        for (var i = 0; i < 3; i++)
        {
            b.AddRange([0x03, 0, 0, 0x82, (byte)(i + 1)]);
        }

        b.Add(0x14);
        return VidFile.Open(new MemoryStream([.. b]))!;
    }

    private static (MenuSystem Menu, Recorder Log) Make()
    {
        var log = new Recorder();
        return (new MenuSystem(log, new MenuSettings()), log);
    }

    [Fact]
    public void Plays_to_its_end_then_returns()
    {
        var (menu, _) = Make();
        menu.OpenTitle();
        var title = menu.Current;
        var ended = 0;
        var vid = Movie();
        menu.Push(new MovieScreen(vid, null, () => ended++));
        Assert.Equal(0, vid.Frame);

        menu.Update(new MenuInput(), Frame, 0f);
        menu.Update(new MenuInput(), Frame, 0.07f);
        Assert.Equal(1, vid.Frame);

        menu.Update(new MenuInput(), Frame, 0.2f);
        Assert.Equal((1, title), (ended, menu.Current));
    }

    [Theory]
    [InlineData("enter")]
    [InlineData("letter")]
    [InlineData("back")]
    [InlineData("click")]
    [InlineData("pad")]
    public void Any_press_skips(string press)
    {
        var (menu, _) = Make();
        menu.OpenTitle();
        var ended = 0;
        menu.Push(new MovieScreen(Movie(40), null, () => ended++));
        var input = press switch
        {
            "enter" => new MenuInput { Accept = true, Control = "Return" },
            "letter" => new MenuInput { Control = "Q" },
            "back" => new MenuInput { Back = true, Control = "Escape" },
            "click" => new MenuInput { Click = true, Control = "Left mouse" },
            _ => new MenuInput { PadControl = "A" },
        };

        menu.Update(new MenuInput(), Frame, 0f);
        menu.Update(new MenuInput(), Frame, MovieScreen.ArmSeconds);
        menu.Update(input, Frame);
        Assert.Equal(1, ended);
        Assert.IsNotType<MovieScreen>(menu.Current);

        // Closed once: a later frame does not end it again.
        menu.Update(input, Frame);
        Assert.Equal(1, ended);
    }

    // The press that won the mission or opened the tab, still down or repeating, does not skip it.
    [Fact]
    public void Presses_in_its_first_moment_do_not_skip()
    {
        var (menu, _) = Make();
        menu.OpenTitle();
        var ended = 0;
        menu.Push(new MovieScreen(Movie(40), null, () => ended++));

        menu.Update(new MenuInput { Click = true, Control = "Left mouse" }, Frame, 0.1f);
        menu.Update(new MenuInput { Accept = true, Control = "Return" }, Frame, 0.3f);
        Assert.Equal(0, ended);
        Assert.IsType<MovieScreen>(menu.Current);
    }

    // The keyboard's auto-repeat presses Accept again but names no control: no skip.
    [Fact]
    public void Auto_repeat_does_not_skip()
    {
        var (menu, _) = Make();
        menu.OpenTitle();
        var ended = 0;
        menu.Push(new MovieScreen(Movie(40), null, () => ended++));

        menu.Update(new MenuInput { Accept = true }, Frame, MovieScreen.ArmSeconds);
        menu.Update(new MenuInput { Accept = true }, Frame);
        Assert.Equal(0, ended);
    }

    // The menus replaced under it (the title opened): no done(), but Released still runs (the music back).
    [Fact]
    public void Replaced_releases_without_done()
    {
        var (menu, _) = Make();
        menu.OpenTitle();
        var (ended, released) = (0, 0);
        menu.Push(new MovieScreen(Movie(40), null, () => ended++) { Released = () => released++ });

        menu.OpenTitle();
        Assert.Equal((0, 1), (ended, released));
    }

    // Pushed before a load (the briefing's autoplay, the won mission's movie): the sound waits for
    // the first frame after it, and that frame's time (the load's) does not count.
    [Fact]
    public void Starts_on_its_first_frame_not_when_pushed()
    {
        var (menu, _) = Make();
        menu.OpenTitle();
        var mixer = new Mixer();
        var vid = Movie(sound: Sounding.Loud);
        menu.Push(new MovieScreen(vid, mixer, null));

        var block = new float[Mixer.BlockFrames * 2];
        mixer.Mix(block);
        Assert.All(block, v => Assert.Equal(0f, v));

        menu.Update(new MenuInput(), Frame, 5f);
        Assert.Equal(0, vid.Frame);
        Assert.IsType<MovieScreen>(menu.Current);
        mixer.Mix(block);
        Assert.Contains(block, v => v != 0f);
    }

    // A silent file with a soundtrack (Future Shock's cues) plays the soundtrack.
    [Fact]
    public void Plays_its_soundtrack()
    {
        var (menu, _) = Make();
        menu.OpenTitle();
        var mixer = new Mixer();
        var track = new Sound(Enumerable.Repeat(1f, 1111).ToArray(), 1, 11025, Looping.Once);
        menu.Push(new MovieScreen(Movie(), mixer, null) { Soundtrack = track });

        menu.Update(new MenuInput(), Frame, 0f);
        var block = new float[Mixer.BlockFrames * 2];
        mixer.Mix(block);
        Assert.Contains(block, v => v != 0f);
    }

    [Fact]
    public void Released_runs_once_on_skip()
    {
        var (menu, _) = Make();
        menu.OpenTitle();
        var released = 0;
        menu.Push(new MovieScreen(Movie(40), null, null) { Released = () => released++ });

        menu.Update(new MenuInput(), Frame, 0f);
        menu.Update(new MenuInput(), Frame, 1f);
        menu.Update(new MenuInput { PadControl = "A" }, Frame, 1f);
        Assert.Equal(1, released);
    }
}

/// <summary>The won mission's movie waits for the next mission only (U1).</summary>
public class WinMovieSlotTests
{
    [Fact]
    public void Plays_before_the_next_mission()
    {
        var slot = new WinMovieSlot();
        slot.Won("SEQ30A");
        Assert.Equal("SEQ30A", slot.Take(MissionStart.Next));
        Assert.Null(slot.Take(MissionStart.Next));
    }

    [Fact]
    public void A_restart_drops_it()
    {
        var slot = new WinMovieSlot();
        slot.Won("SEQ30A");
        Assert.Null(slot.Take(MissionStart.Restart));
        Assert.Null(slot.Take(MissionStart.Next));
    }

    // Leaving during the WELL DONE banner (title, new game, load) must not play it before another mission.
    [Fact]
    public void Leaving_drops_it()
    {
        var slot = new WinMovieSlot();
        slot.Won("SEQ30A");
        slot.Drop();
        Assert.Null(slot.Take(MissionStart.Next));
    }
}

/// <summary>When the campaign's movies play (docs/game-logic/fmv-sequences.md).</summary>
public class MovieMomentTests
{
    private static readonly MenuFrame Frame = new(1280, 800);

    [Fact]
    public void SkyNet_rows_name_their_movies()
    {
        var rows = SkyNet.Game.Campaign.MissionTable.SkyNet.Missions;
        Assert.Equal(["SEQ10", "SEQ20", "SEQ30", "SEQ40", "SEQ50", "SEQ60", "SEQ70", "SEQ80"], rows.Select(m => m.BriefingMovie));
        Assert.Equal([null, "1STSCENE", "SEQ30A", "SUBSINK", null, "SC2FINL", null, "SCENE3"], rows.Select(m => m.WinMovie));
    }

    [Fact]
    public void Future_shock_rows_name_none()
    {
        var rows = SkyNet.Game.Campaign.MissionTable.FutureShock.Missions;
        Assert.All(rows, m => Assert.Null(m.BriefingMovie));
        Assert.All(rows, m => Assert.Null(m.WinMovie));
    }

    [Fact]
    public void The_briefing_tab_plays_the_movie_then_the_first_page()
    {
        var brief = Briefing.Parse(System.Text.Encoding.ASCII.GetBytes("[EN]\nScout.\n%%%%\n[BR]\n[004]\nLine.\n####\n%%%%\n"));
        var log = new MovieRecorder();
        var menu = new MenuSystem(log, new MenuSettings());
        var screen = menu.OpenMission(new MissionBriefing(GameKind.SkyNet, 1, 210, brief) { Movie = "SEQ10" });
        Assert.Equal(["SEQ10"], log.Movies);

        menu.Update(new MenuInput { Down = true }, Frame);
        Assert.Equal(1, screen.Page);
        screen.Open(MissionTab.Briefing);
        Assert.Equal(["SEQ10", "SEQ10"], log.Movies);
        Assert.Equal(0, screen.Page);

        screen.Open(MissionTab.Tactical);
        Assert.Equal(2, log.Movies.Count);
    }

    // The first open of a mission's briefing plays its movie; a restart's does not; the next mission's does.
    [Fact]
    public void Briefing_movie_autoplays_once_per_mission()
    {
        var log = new MovieRecorder();
        var menu = new MenuSystem(log, new MenuSettings());
        MissionBriefing Brief(int mission, string movie) => new(GameKind.SkyNet, mission, 210, null) { Movie = movie };

        menu.OpenMission(Brief(1, "SEQ10"));
        menu.OpenMission(Brief(1, "SEQ10") with { Restart = true });
        Assert.Equal(["SEQ10"], log.Movies);

        menu.OpenMission(Brief(2, "SEQ20"));
        Assert.Equal(["SEQ10", "SEQ20"], log.Movies);

        // A new game: mission 1 again.
        menu.OpenMission(Brief(1, "SEQ10"));
        Assert.Equal(["SEQ10", "SEQ20", "SEQ10"], log.Movies);
    }

    [Fact]
    public void No_movie_without_a_name()
    {
        var log = new MovieRecorder();
        var menu = new MenuSystem(log, new MenuSettings());
        var screen = menu.OpenMission(new MissionBriefing(GameKind.FutureShock, 1, 10, null));
        screen.Open(MissionTab.Briefing);
        Assert.Empty(log.Movies);
    }

    private sealed class MovieRecorder : IMenuActions
    {
        public List<string> Movies { get; } = [];
        public void PlayMovie(string name, MoviePlace place) => Movies.Add(name);
        public void StartGame(GameKind game, int mission, SkyNet.Game.Campaign.Pilot pilot) { }
        public void BeginMission() { }
        public void Resume() { }
        public void RestartMission() { }
        public void QuitToTitle() { }
        public void Quit() { }
        public void LoadGame(int slot) { }
        public bool SaveGame(int slot) => false;
        public void SettingsChanged(MenuSettings settings) { }
        public void Sound(string name) { }
    }
}
