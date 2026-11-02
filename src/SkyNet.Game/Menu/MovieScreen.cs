using System.Drawing;
using System.Runtime.InteropServices;
using RetroEng.Audio;
using SkyNet.Formats;

namespace SkyNet.Game.Menu;

/// <summary>Where a movie sits: centred on black, or as the BRIEFING tab's (FUN_0012dcad) under the
/// tab bar (clip top y = 14, [0x33abc] = 0xE) over FAKEHEAD.IMG.</summary>
public enum MoviePlace { Centred, Briefing }

/// <summary>
/// A movie (.VID) over the whole screen: the 320 × 200 picture letterboxed as the menus, its sound
/// on the interface bus, any press (a key, a click or tap, a pad button) skips it. The sound drives
/// the pictures (<see cref="MovieClock"/>); frames are decoded as they come due.
/// <code>
///   Opened: first picture ─► first Tick: sound starts ─► Tick: clock ─► Next() until the due frame ─► Draw: upload, blit
///   the end or a press ─► sound stopped, file closed, Pop, done()
/// </code>
/// </summary>
public sealed class MovieScreen : MenuScreen
{
    // What the briefing's movie is drawn over (FUN_0012dcad's picture, string at 0x4de91).
    private const string BriefingBackdrop = "FAKEHEAD.IMG";

    /// <summary>Presses in the movie's first moment do not skip it (the press that won the mission or opened the tab).</summary>
    public const float ArmSeconds = 0.5f;

    private readonly VidFile _vid;
    private readonly Mixer? _mixer;
    private readonly Action? _done;
    private readonly MovieClock _clock = new();
    private VoiceHandle _voice = VoiceHandle.None;
    private bool _started;
    // The first Tick came: the sound and the clock run.
    private bool _playing;
    private bool _ended;
    // Seconds shown: skips count from ArmSeconds on.
    private double _age;

    /// <summary>Plays <paramref name="vid"/> (owned from here on); <paramref name="mixer"/> null plays it silent;
    /// <paramref name="done"/> runs once when it ends or is skipped, after the screen closed.</summary>
    public MovieScreen(VidFile vid, Mixer? mixer, Action? done)
    {
        (_vid, _mixer, _done) = (vid, mixer, done);
    }

    public override string Name => "movie";

    public MoviePlace Place { get; init; }

    /// <summary>The sound played instead of the file's (Future Shock's cue track, <see cref="Audio.MovieCues"/>); null: the file's.</summary>
    public Sound? Soundtrack { get; init; }

    /// <summary>Runs once when the movie lets go, however it closed (ended, skipped or replaced by the menus).</summary>
    public Action? Released { get; init; }

    /// <summary>Full screen: the game below is hidden.</summary>
    public override bool OverGame => false;

    public override void Opened()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        _vid.Next();
    }

    public override void Tick(float seconds)
    {
        if (_ended)
        {
            return;
        }

        // Pushed before a load (a briefing's autoplay, a won mission's movie): the sound starts on the
        // first frame after it, and that frame's time (the load's) is not the movie's.
        if (!_playing)
        {
            _playing = true;
            StartSound();
            return;
        }

        _age += seconds;
        // The sound's position while its voice plays; the wall clock before, after and without it.
        double? audio = _mixer != null && !_voice.IsNone && _mixer.IsPlaying(_voice) ? _mixer.PlaybackTime(_voice) : null;
        _clock.Advance(seconds, audio);
        while (_vid.Frame + 1 < _vid.FrameCount && _clock.Now >= _vid.FrameTime(_vid.Frame + 1))
        {
            _vid.Next();
        }

        if (_clock.Now >= _vid.Seconds)
        {
            End();
        }
    }

    private void StartSound()
    {
        if (_mixer != null && Soundtrack != null)
        {
            _voice = _mixer.Play(Soundtrack, bus: Bus.Interface);
            return;
        }

        if (_mixer == null || _vid.AudioRate == 0)
        {
            return;
        }

        var pcm = ImmutableCollectionsMarshal.AsArray(_vid.Audio)!;
        _voice = _mixer.Play(Sound.FromPcm(pcm, 1, _vid.AudioRate, 8, Looping.Once), bus: Bus.Interface);
    }

    /// <summary>Every input is the movie's: any new press skips it (DOS: any key), from <see cref="ArmSeconds"/> on.
    /// Accept alone is the keyboard's auto-repeat (a key named no control): a held key does not skip.</summary>
    public override bool Intercept(in MenuInput input)
    {
        var key = input.Control != "" && input.Wheel == 0f;
        if (_age >= ArmSeconds && (key || input.Click || input.PadControl != ""))
        {
            End();
        }

        return true;
    }

    public override void Back() => End();

    public override void Draw(MenuPainter p)
    {
        if (_ended)
        {
            return;
        }

        // Shorter movies (the SEQs are 186 high) sit centred on the black screen, a briefing's under its tab bar.
        p.FillWindow(MenuPainter.Black);
        var top = (MenuFrame.ScreenHeight - _vid.Height) / 2;
        if (Place == MoviePlace.Briefing)
        {
            p.Image(BriefingBackdrop, MenuLayout.BriefPalette, 0, 0);
            top = MenuLayout.BriefPicture.Top;
        }

        p.Picture(_vid.Pixels, new Size(_vid.Width, _vid.Height), _vid.Palette, new Rectangle(0, top, MenuFrame.ScreenWidth, _vid.Height));
    }

    public override void Closed()
    {
        if (_ended)
        {
            return;
        }

        // Closed from outside (the menus replaced): no done(), the caller's flow was replaced too; Released still runs.
        _ended = true;
        Release();
    }

    private void End()
    {
        if (_ended)
        {
            return;
        }

        _ended = true;
        Release();
        if (Menu.Current == this)
        {
            Menu.Pop();
        }

        _done?.Invoke();
    }

    private void Release()
    {
        if (_mixer != null && !_voice.IsNone)
        {
            _mixer.Stop(_voice);
        }

        _voice = VoiceHandle.None;
        _vid.Dispose();
        Released?.Invoke();
    }
}

/// <summary>
/// A movie's time in seconds: the sound's position while it moves, the wall clock when there is
/// none (a silent movie, the voice ended or refused) or it stalls (no device mixing) for
/// <see cref="StallSeconds"/>. Before the voice's first block it holds at 0.
/// </summary>
internal sealed class MovieClock
{
    /// <summary>How long a voice may report one position before the wall clock takes over.</summary>
    public const double StallSeconds = 0.25;

    private double _audio = -1;
    private double _stalled;

    public double Now { get; private set; }

    public void Advance(double wall, double? audio)
    {
        if (audio is not { } a)
        {
            Now += wall;
            return;
        }

        if (a != _audio)
        {
            (_audio, _stalled, Now) = (a, 0, a);
            return;
        }

        _stalled += wall;
        if (_stalled > StallSeconds)
        {
            Now += wall;
        }
    }
}
