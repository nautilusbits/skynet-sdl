using System.Text;
using SkyNet.Formats;

namespace SkyNet.Game.Campaign;

/// <summary>
/// The player's name and call sign (SKYNET.EXE v1.01): two 20-byte buffers, the name at 0x4d4f6 and
/// the call sign right after it at 0x4d50a. The new-game dialog (0x135345, <see cref="Menu.PilotScreen"/>)
/// asks them before the first briefing of either campaign; the edit (0x13bd46) takes up to
/// <see cref="NameLength"/> letters A-Z (no digits, spaces or punctuation) shown upper case, and an
/// empty field takes the STRINGS.PRS default (DefaultName "noname", DefaultSign "ice", set at
/// start-up by 0x135300). Every copy goes through 0x135596, which capitalises: the first letter
/// upper case, the rest lower ("Noname"). DOS's save keeps both (the section at header +0x40); the
/// port's save format 4 does too. The multiplayer name is the same buffer (0x11a54a).
/// </summary>
public sealed record Pilot(string Name, string CallSign)
{
    /// <summary>The edit's maximum length (0x13bd46 called with 17) for either field.</summary>
    public const int NameLength = 17;

    public const int CallSignLength = 17;

    /// <summary>The DOS buffers' size: the longest a field can be (save sanity checks).</summary>
    public const int MaxLength = 20;

    /// <summary>STRINGS.PRS DefaultName / DefaultSign when the data has none.</summary>
    public const string DefaultName = "noname";

    public const string DefaultSign = "ice";

    /// <summary>No name given (a session started without the dialog: --mission, a console command).</summary>
    public static Pilot None { get; } = new("", "");

    /// <summary>DOS's start-up pair, capitalised: "Noname", "Ice".</summary>
    public static Pilot Default { get; } = Defaults(null);

    /// <summary>The pair an empty field falls back to: STRINGS.PRS DefaultName / DefaultSign.</summary>
    public static Pilot Defaults(PrsFile? strings) => new(
        Capitalise(strings?.Text("DefaultName", DefaultName) ?? DefaultName),
        Capitalise(strings?.Text("DefaultSign", DefaultSign) ?? DefaultSign));

    /// <summary>Does the edit take <paramref name="c"/> (letters only, as 0x13bd46 with filter 6)?</summary>
    public static bool Allowed(char c) => char.ToUpperInvariant(c) is >= 'A' and <= 'Z';

    /// <summary>A field's text as the dialog shows it: upper case, letters only, at most
    /// <paramref name="length"/> long.</summary>
    public static string Clean(string? text, int length)
    {
        var sb = new StringBuilder(length);
        foreach (var raw in text ?? "")
        {
            if (sb.Length < length && Allowed(raw))
            {
                sb.Append(char.ToUpperInvariant(raw));
            }
        }

        return sb.ToString();
    }

    /// <summary>0x135596: the first letter upper case, the rest lower.</summary>
    public static string Capitalise(string text) =>
        text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..].ToLowerInvariant();

    /// <summary>What the dialog stores for its two fields: each capitalised, an empty one the default's.</summary>
    public static Pilot FromFields(string name, string callSign, Pilot defaults) => new(
        Clean(name, NameLength) is { Length: > 0 } n ? Capitalise(n) : defaults.Name,
        Clean(callSign, CallSignLength) is { Length: > 0 } c ? Capitalise(c) : defaults.CallSign);

    /// <summary>
    /// FUN_0012daa8's codes: the letter after '#' is upper-cased, then #N is the name, #C the call
    /// sign, #P the subject player's name (0x31f5b) and #K the killer's (0x31f5f), both multiplayer
    /// (<paramref name="player"/>, <paramref name="killer"/>); any other '#' stays as it is. Used on
    /// the briefings, the STATISTICS header (Stat_Header "#N HERE ARE YOUR STATS:"), the multiplayer
    /// messages and the terminator HUD (#P TERMINATED), and the `whoami` cheat ("You are #N.");
    /// ordinary pickup and status lines are not expanded.
    /// </summary>
    public string Expand(string text, string player = "", string killer = "")
    {
        if (!text.Contains('#'))
        {
            return text;
        }

        var sb = new StringBuilder(text.Length + 16);
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '#' && i + 1 < text.Length)
            {
                string? with = char.ToUpperInvariant(text[i + 1]) switch
                {
                    'N' => Name,
                    'C' => CallSign,
                    'P' => player,
                    'K' => killer,
                    _ => null,
                };
                if (with != null)
                {
                    sb.Append(with);
                    i++;
                    continue;
                }
            }

            sb.Append(text[i]);
        }

        return sb.ToString();
    }
}
