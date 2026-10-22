namespace SkyNet.Game.Hud;

/// <summary>
/// The top-left status lines: DOS's queue at 0x51600 (SKYNET.EXE v1.01).
/// <para>FUN_0012f8f7 queues a line (text in EAX, colour in DL) only while the flag word 0x30a50
/// has bit 0x2000 (set at a level start). An entry is 0x2d bytes: <c>{i32 time, u8 colour,
/// char text[40]}</c>, time = the 70 Hz game clock 0x38c00 at the push, −1 = end of the list,
/// −2 = free. The push takes the first entry that is not live (a free one or the end), so the
/// list holds at most 21 entries (0x51600..0x519b0); when all 21 are live the new line is
/// dropped. At most 39 characters (0x27) are kept.</para>
/// <para>FUN_0012f953 draws them every frame: the live entries sorted by push time, oldest at the
/// top; an entry whose age reached 210 ticks (<c>time − clock ≤ −0xd2</c>) is freed and draws
/// nothing. There is no limit on the visible count other than the 21 entries.</para>
/// <para>The clock: FUN_00124c63 adds round(70 / fps) ticks a frame (0x460000 / fps·256), beside
/// the 1000 Hz clock 0x6aec0 (0x3e800 / fps·256) - 210 ticks are 3 s.</para>
/// </summary>
public sealed class StatusQueue
{
    /// <summary>Ticks of the 70 Hz clock a line shows (FUN_0012f953: 0xd2).</summary>
    public const int LifeTicks = 210;

    public const float ClockHz = 70f;

    public const float DefaultSeconds = LifeTicks / ClockHz;

    /// <summary>Entries of the 0x51600 list (0x519b0 is the last start a push accepts).</summary>
    public const int Capacity = 21;

    /// <summary>Characters FUN_0012f8f7 copies (0x27).</summary>
    public const int MaxChars = 39;

    /// <summary>The colour most lines are queued with: pickups, cheats, "YOU ENTERED ...".</summary>
    public const byte WhiteColour = 0xB3;

    /// <summary>Network events (player joined/left/died/in a jeep).</summary>
    public const byte NetworkColour = 0x0C;

    /// <summary>Asides: "OW!", "I DONT THINK SO !", the joystick camera, cheat validation.</summary>
    public const byte AsideColour = 0x08;

    private readonly List<(string Text, byte Colour, float Left)> _lines = [];

    /// <summary>The lines showing, oldest (top) first.</summary>
    public IEnumerable<string> Lines => _lines.Select(l => l.Text);

    /// <summary>The lines showing with the palette index each was queued with, oldest first.</summary>
    public IEnumerable<(string Text, byte Colour)> Entries => _lines.Select(l => (l.Text, l.Colour));

    public int Count => _lines.Count;

    /// <summary>Queues a line (FUN_0012f8f7); false when all <see cref="Capacity"/> entries are
    /// live and DOS would drop it.</summary>
    public bool Push(string text, byte colour = WhiteColour, float seconds = DefaultSeconds)
    {
        if (string.IsNullOrEmpty(text) || _lines.Count >= Capacity)
        {
            return false;
        }

        if (text.Length > MaxChars)
        {
            text = text[..MaxChars];
        }

        _lines.Add((text, colour, seconds));
        return true;
    }

    /// <summary>Ages the lines; one is gone once its time is used up (210 ticks in DOS).</summary>
    public void Tick(float seconds)
    {
        for (var i = _lines.Count - 1; i >= 0; i--)
        {
            var left = _lines[i].Left - seconds;
            if (left <= 1e-4f)
            {
                _lines.RemoveAt(i);
            }
            else
            {
                _lines[i] = (_lines[i].Text, _lines[i].Colour, left);
            }
        }
    }

    /// <summary>A level start (FUN_0012f745 writes −1 to the first entry).</summary>
    public void Clear() => _lines.Clear();

    /// <summary>The y of status line <paramref name="index"/> (FUN_0012f953): from 3, each line the
    /// font's height + 2 (0x33344 + 2) below the last; the text at x 4, its 0x7F shadow at +1,+1.</summary>
    public static int LineY(int index, int lineHeight) => 3 + index * (lineHeight + 2);

    public const int LineX = 4;
}
