using RetroEng.Audio;
using RetroEng.Diagnostics;
using SkyNet.Formats;
using SkyNet.Game.Audio;
using SkyNet.Game.Campaign;
using SkyNet.Game.Menu;

// The movies (.VID): one full-screen MovieScreen over whatever runs, the music held meanwhile.
//
//   PlayMovie(name, then) ─► MovieScreen pushed (game paused, menus hidden) ─► end or a press ─► music back, then()
internal sealed partial class AppHost
{
    private const string MovieExtension = ".VID";
    // The fixed movies (docs/game-logic/fmv-sequences.md): program start, Future Shock's new game,
    // the campaign's end (SkyNET / Future Shock).
    private const string OpeningMovie = "OPENING";
    private const string BeginMovie = "BEGIN";
    private const string CreditsMovie = "CREDITS";
    private const string EndMovie = "END";
    // The front end's idle time before the credits play (DOS: 0x168 BIOS ticks, FUN_0011e27c).
    private const double IdleCreditsSeconds = 0x168 / 18.2065;

    // The won mission's movie (row +0x43), played before the next mission's briefing or the end.
    private readonly WinMovieSlot winMovie = new();
    // Seconds on the title with no input.
    private double titleIdle;

    /// <summary>Plays a movie of the data folder by name (OPENING, SEQ10…), then runs
    /// <paramref name="then"/>; a movie that is missing or does not parse runs it at once. False then.</summary>
    internal bool PlayMovie(string name, Action? then = null, MoviePlace place = MoviePlace.Centred)
    {
        var file = name.EndsWith(MovieExtension, StringComparison.OrdinalIgnoreCase) ? name : name + MovieExtension;
        var path = GameDataLocator.FindFile(data.Dir, file);
        var vid = path != null ? OpenMovie(path) : null;
        if (vid == null)
        {
            Log.Warn("Movie", $"{file}: {(path == null ? "not found" : "does not parse")}");
            then?.Invoke();
            return false;
        }

        // Future Shock's BEGIN / END have no sound blocks: their RAW cues instead.
        var cues = vid.AudioRate == 0 ? MovieCues.Soundtrack(Path.GetFileNameWithoutExtension(file), vid, n => sounds?.Get(n)) : null;
        var sound = cues != null ? cues.Frames / (double)cues.SampleRate : vid.Audio.Length / Math.Max(vid.AudioRate, 1.0);
        Log.Info("Movie", $"{file}: {vid.FrameCount} frames, {vid.Seconds:0.0} s, sound {sound:0.0} s{(cues != null ? " (cues)" : "")}");
        audio.Mixer.SetBusPaused(Bus.Music, true);
        // The music comes back however it closes; then() only when it ended or was skipped.
        menu.Push(new MovieScreen(vid, audio.Mixer, then)
        {
            Place = place,
            Soundtrack = cues,
            Released = () => audio.Mixer.SetBusPaused(Bus.Music, false),
        });
        ReleaseMouse();
        return true;
    }

    // A file that cannot be read is a movie that does not parse: null.
    private static VidFile? OpenMovie(string path)
    {
        try
        {
            return VidFile.Open(File.OpenRead(path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Log.Warn("Movie", $"{Path.GetFileName(path)}: {e.Message}");
            return null;
        }
    }

    // The campaign's movies play in a player's own game only: never under a demo, a recording, a
    // capture or a deathmatch (their ticks and hashes must not change).
    private bool MoviesAllowed() => playback == null && recording == null && capture == null && dm == null;

    // Program start, over the title (FUN_0011c900 0x11c95f); not in scripted or offscreen runs.
    private void PlayOpening()
    {
        if (!interactive || tool || options.Offscreen || !MoviesAllowed())
        {
            return;
        }

        PlayMovie(OpeningMovie);
    }

    // A new game: Future Shock's campaign opens with BEGIN (0x11cb23), then the mission starts.
    private void StartNewGame(Mission mission)
    {
        var first = session.Table.Missions.Count > 0 && session.Table.Missions[0].Number == mission.Number;
        if (data.Game != GameKind.FutureShock || !first || !MoviesAllowed())
        {
            session.StartMission(mission);
            return;
        }

        PlayMovie(BeginMovie, () => session.StartMission(mission));
    }

    // The session's and the menus' movie moments.
    private void WireMovies()
    {
        // BRIEFING tab: the mission's SEQnn under the tab bar.
        menuEvents.OnPlayMovie = (name, place) =>
        {
            if (MoviesAllowed())
            {
                PlayMovie(name, null, place);
            }
        };

        events.OnMissionComplete = done => winMovie.Won(MoviesAllowed() ? done.WinMovie : null);

        // The won mission's movie (0x11cff8), then the next mission's briefing.
        var started = events.OnMissionStarted;
        events.OnMissionStarted = (mission, briefing, restart) =>
        {
            var movie = winMovie.Take(restart ? MissionStart.Restart : MissionStart.Next);
            if (movie == null || !MoviesAllowed())
            {
                started?.Invoke(mission, briefing, restart);
                return;
            }

            PlayMovie(movie, () => started?.Invoke(mission, briefing, restart));
        };

        // The campaign's end (0x11d043): the last won movie, CREDITS (Future Shock: END), the title.
        events.OnCampaignComplete = () =>
        {
            var movie = winMovie.Take(MissionStart.Next);
            if (!MoviesAllowed())
            {
                return;
            }

            var end = data.Game == GameKind.FutureShock ? EndMovie : CreditsMovie;
            void ToTitle() => menuEvents.OnQuitToTitle?.Invoke();
            void PlayEnd() => PlayMovie(end, ToTitle);
            if (movie == null)
            {
                PlayEnd();
                return;
            }

            PlayMovie(movie, PlayEnd);
        };
    }

    // The title left alone ~20 s plays the credits (FUN_0011e27c 0x11e30c → FUN_0011d5b2).
    private void IdleCredits(in MenuInput input, float seconds)
    {
        var touched = input.Control != "" || input.PadControl != "" || input.Pointer != null || input.Wheel != 0f || input.Typed != "";
        if (touched || !WindowActive() || menu.InGame || menu.Current is not MainMenuScreen || !interactive || options.Offscreen || !MoviesAllowed())
        {
            titleIdle = 0;
            return;
        }

        titleIdle += seconds;
        if (titleIdle < IdleCreditsSeconds)
        {
            return;
        }

        titleIdle = 0;
        PlayMovie(CreditsMovie);
    }

    // The window has the desktop's focus and is not minimised: the title's idle time counts only then.
    private bool WindowActive() => window.HasFocus && !window.Minimised;

    // movie NAME: plays it over what runs (a check of the player); a data folder name only, never in a
    // demo, recording or deathmatch (a demo's replayed console lines must not open one).
    private void RegisterMovieCommand() =>
        commands.Add("movie", "movie NAME", args =>
        {
            if (args.Count == 0)
            {
                return "movie: a name (LOGO, OPENING, SEQ10…)";
            }

            if (!MoviesAllowed())
            {
                return "movie: not in a demo, recording or deathmatch";
            }

            if (args[0].Contains("..") || args[0].IndexOfAny(['/', '\\', ':']) >= 0)
            {
                return $"movie: {args[0]} is no movie name";
            }

            devConsole.Close();
            return PlayMovie(args[0]) ? "" : $"movie: {args[0]} not found";
        }, "plays a .VID movie full screen (any key skips it)", "vid");
}
