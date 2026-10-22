namespace SkyNet.Game.Hud;

/// <summary>
/// The panel's message window, the right-hand recess of PANEL0 where DOS types the mission's
/// hints and objectives (SKYNET.EXE v1.01; docs/game-logic/message-box.md):
/// <list type="bullet">
/// <item>a new message (FUN_0012fa3d) word-wraps to <see cref="WrapWidth"/> of the HUD font, clears the
/// box, plays sound 0x51 (<see cref="SoundRequested"/>) and restarts the typewriter;</item>
/// <item>the typewriter (FUN_0012fb4c) types one character at a time: <c>\r</c> shows nothing,
/// <c>\n</c> starts a line, and past <see cref="LineCount"/> lines the oldest scrolls off;</item>
/// <item>nothing expires it: the text stays until the next message; <see cref="Clear"/> at a level start.</item>
/// </list>
/// DOS types one character per rendered frame; like the Godot port this types a fixed
/// <see cref="CharsPerSecond"/>, so the speed does not follow the frame rate.
/// <para>The 320 × 200 HUD wraps to <see cref="WrapPx"/> of FONT0004 and shows <see cref="Lines"/>
/// lines; the 640 × 480 HUD to <see cref="HiResWrapPx"/> of FONT0010 and <see cref="HiResLines"/>
/// (<see cref="SetLayout"/>).</para>
/// </summary>
public sealed class MessageBox(Func<char, int> advance)
{
    public const int WrapPx = 0x62;
    public const int Lines = 5;
    /// <summary>0x62 through DOS's 640 × 480 scale (FUN_0012ad2e: x × 2).</summary>
    public const int HiResWrapPx = WrapPx * 2;
    /// <summary>The 640 × 480 box: 79 px tall (33 scaled), 12 px a line (FONT0010's 11 + 1).</summary>
    public const int HiResLines = 6;
    public const float CharsPerSecond = 30f;
    /// <summary>The sound a new message plays (FUN_0012fa3d).</summary>
    public const int SoundId = 0x51;

    private readonly List<string> _lines = [];
    private readonly List<string> _typed = [];
    private Func<char, int> _advance = advance;
    private string _text = "";
    private string _stream = "";
    private int _at;
    private float _accumulated;

    /// <summary>Raised with <see cref="SoundId"/> when a message starts with its sound.</summary>
    public event Action<int>? SoundRequested;

    /// <summary>The wrap width in font pixels (<see cref="WrapPx"/> or <see cref="HiResWrapPx"/>).</summary>
    public int WrapWidth { get; private set; } = WrapPx;

    /// <summary>The lines the box shows (<see cref="Lines"/> or <see cref="HiResLines"/>).</summary>
    public int LineCount { get; private set; } = Lines;

    /// <summary>
    /// A new font and box (the art set changed): the current message is wrapped again and typed
    /// to where it was (as far as the new stream reaches), without the sound.
    /// </summary>
    public void SetLayout(Func<char, int> advance, int wrapWidth, int lineCount)
    {
        if (wrapWidth == WrapWidth && lineCount == LineCount && advance == _advance)
        {
            return;
        }

        _advance = advance;
        WrapWidth = wrapWidth;
        LineCount = Math.Max(1, lineCount);
        if (_stream.Length == 0)
        {
            return;
        }

        var typed = _stream[.._at].Count(c => c is not '\r' and not '\n');
        Start(_text, false);
        var n = 0;
        while (_at < _stream.Length && n < typed)
        {
            if (_stream[_at] is not '\r' and not '\n')
            {
                n++;
            }

            Type(_stream[_at++]);
        }
    }

    /// <summary>The box's lines, top first (at most <see cref="LineCount"/>).</summary>
    public IReadOnlyList<string> Text => _lines;

    /// <summary>Every line typed of the message, top first, none scrolled off (the MODERN HUD's box).</summary>
    public IReadOnlyList<string> Typed => _typed;

    /// <summary>The wrapped stream being typed (<c>"\r\n"</c> breaks).</summary>
    public string Stream => _stream;

    public bool Typing => _at < _stream.Length;

    /// <summary>A hint (acts 0x1C-0x25, the border-box hint).</summary>
    public void ShowHint(string text) => Start(text, true);

    /// <summary>An objective (acts 0x26-0x2A): DOS shows it only when the text is longer than 2
    /// characters (0x137fd0). <paramref name="playSound"/> false when the caller already played 0x51.</summary>
    public void ShowObjective(string text, bool playSound = true)
    {
        if (text.Length > 2)
        {
            Start(text, playSound);
        }
    }

    /// <summary>A new message: the box clears and the typing starts over.</summary>
    public void Start(string text, bool playSound = true)
    {
        _text = text;
        _stream = TextLayout.Wrap(text, _advance, WrapWidth);
        _at = 0;
        _accumulated = 0f;
        _lines.Clear();
        _lines.Add("");
        _typed.Clear();
        _typed.Add("");
        if (playSound)
        {
            SoundRequested?.Invoke(SoundId);
        }
    }

    /// <summary>Empties the box (a level start, FUN_0012f745).</summary>
    public void Clear()
    {
        _text = "";
        _stream = "";
        _at = 0;
        _accumulated = 0f;
        _lines.Clear();
        _typed.Clear();
    }

    /// <summary>Types what <paramref name="seconds"/> are worth; true when the lines changed.</summary>
    public bool Tick(float seconds)
    {
        if (_at >= _stream.Length)
        {
            return false;
        }

        _accumulated += seconds * CharsPerSecond;
        var changed = false;
        while (_accumulated >= 1f && _at < _stream.Length)
        {
            _accumulated -= 1f;
            Type(_stream[_at++]);
            changed = true;
        }

        return changed;
    }

    private void Type(char c)
    {
        if (c == '\r')
        {
            return;
        }

        if (c == '\n')
        {
            _typed.Add("");
            _lines.Add("");
            if (_lines.Count > LineCount)
            {
                _lines.RemoveAt(0);
            }

            return;
        }

        _lines[^1] += c;
        _typed[^1] += c;
    }

    /// <summary>Types the rest at once (tests, screenshots).</summary>
    public void Finish() => Tick((_stream.Length - _at + 1) / CharsPerSecond);
}
