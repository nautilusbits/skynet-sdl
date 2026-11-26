using RetroEng.Diagnostics;

namespace SkyNet.Game.Audio;

/// <summary>
/// The .sf2 files MUSIC = SOUNDFONT can play (issue #70): those right in the <c>soundfonts/</c>
/// folder beside the game (next to <c>mods/</c>). Not a mod: a bank changes no game data. An id is
/// the file's name: <c>Roland.SC-55.sf2</c>.
/// </summary>
public static class SoundFonts
{
    public const string Folder = "soundfonts";
    public const string Extension = ".sf2";
    private const string Pattern = "*" + Extension;
    private const char OldSeparator = '/';
    private const long MegaByte = 1 << 20;

    /// <summary>Not a file name: a folder or drive in it.</summary>
    private static readonly char[] PathChars = ['/', '\\', ':'];

    /// <summary>Right in the folder, any case (<c>X.SF2</c> on Android too).</summary>
    private static readonly EnumerationOptions Listing = new() { MatchCasing = MatchCasing.CaseInsensitive, RecurseSubdirectories = false };

    /// <summary>The largest .sf2 read (a 2 GB bank would run the music thread out of memory):
    /// 256 MB, 128 MB on Android. GeneralUser GS is 31 MB.</summary>
    public static readonly long MaxBytes = (OperatingSystem.IsAndroid() ? 128 : 256) * MegaByte;

    /// <summary>Whether <paramref name="id"/> is one this class makes: a file name ending .sf2, no
    /// folder, no <c>..</c>, no slash, colon or control character (a hand-edited setting may hold anything).</summary>
    public static bool IsId(string id) =>
        id.Length > Extension.Length && id.EndsWith(Extension, StringComparison.OrdinalIgnoreCase)
        && !id.Contains("..", StringComparison.Ordinal) && id.IndexOfAny(PathChars) < 0 && !id.Any(char.IsControl);

    /// <summary>A saved setting as an id; builds that read the .sf2 from mods saved
    /// <c>mod/file.sf2</c>, which becomes <c>file.sf2</c>. "" when it is neither.</summary>
    public static string FromSetting(string stored)
    {
        if (IsId(stored))
        {
            return stored;
        }

        // The old form: one slash after a non-empty mod name.
        var slash = stored.IndexOf(OldSeparator);
        if (slash <= 0 || slash != stored.LastIndexOf(OldSeparator))
        {
            return "";
        }

        var file = stored[(slash + 1)..];
        return IsId(file) && !stored[..slash].Contains("..", StringComparison.Ordinal) ? file : "";
    }

    /// <summary>Every .sf2 right in <paramref name="folder"/> (subfolders are not read), by file
    /// name; empty when there is none or the folder does not read.</summary>
    public static IReadOnlyList<string> Scan(string folder)
    {
        try
        {
            return [.. Directory.EnumerateFiles(folder, Pattern, Listing)
                .Select(Path.GetFileName)
                .OfType<string>()
                .Where(IsId)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>The bytes of the .sf2 <paramref name="id"/> in <paramref name="folder"/>; null when the
    /// id is not one, the file is gone, does not read or is larger than <see cref="MaxBytes"/>.</summary>
    public static byte[]? Read(string folder, string id) => Read(folder, id, MaxBytes);

    /// <summary><see cref="Read(string, string)"/> with its own cap (tests).</summary>
    internal static byte[]? Read(string folder, string id, long maxBytes)
    {
        if (!IsId(id))
        {
            return null;
        }

        var path = Path.Combine(folder, id);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using var stream = File.OpenRead(path);
            return Capped(stream, maxBytes, id);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warn("Music", $"SoundFont {id} does not read: {e.Message}");
            return null;
        }
    }

    /// <summary>The name GAMEPLAY shows: the file's, without extension, upper case
    /// (<c>GeneralUser-GS.sf2</c> → <c>GENERALUSER-GS</c>).</summary>
    public static string Label(string id) => Path.GetFileNameWithoutExtension(id).ToUpperInvariant();

    /// <summary>A stream's bytes up to <paramref name="maxBytes"/>; null (and a warning) past it. A
    /// length it knows is checked first; else at most one byte over the cap is read (a ZIP entry
    /// inflates no further).</summary>
    private static byte[]? Capped(Stream stream, long maxBytes, string id)
    {
        if (stream.CanSeek && stream.Length > maxBytes)
        {
            return TooLarge(id, maxBytes);
        }

        using var copy = stream.CanSeek ? new MemoryStream((int)stream.Length) : new MemoryStream();
        var buffer = new byte[81920];
        int n;
        while ((n = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, maxBytes + 1 - copy.Length))) > 0)
        {
            copy.Write(buffer, 0, n);
            if (copy.Length > maxBytes)
            {
                return TooLarge(id, maxBytes);
            }
        }

        return copy.ToArray();
    }

    private static byte[]? TooLarge(string id, long maxBytes)
    {
        Log.Warn("Music", $"SoundFont {id} is larger than {maxBytes / MegaByte} MB; music plays the port's synth");
        return null;
    }
}
