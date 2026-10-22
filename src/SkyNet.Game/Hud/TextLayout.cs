using System.Text;

namespace SkyNet.Game.Hud;

/// <summary>DOS's message word-wrap (FUN_0012daa8, SKYNET.EXE v1.01).</summary>
public static class TextLayout
{
    /// <summary>
    /// Greedy wrap to <paramref name="widthPx"/>: line breaks and CRs become spaces; a break is
    /// <c>"\r\n"</c> after the last space of the line once the next character would reach the
    /// width; a word with no space before it breaks where it is. The result keeps DOS's
    /// <c>"\r\n"</c> breaks: the stream the typewriter types.
    /// </summary>
    public static string Wrap(string text, Func<char, int> advance, int widthPx)
    {
        var src = text.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ');
        var output = new StringBuilder(src.Length + 16);
        var line = new StringBuilder();
        var x = 0;
        var i = 0;
        while (i < src.Length)
        {
            var c = src[i];
            var adv = advance(c);
            if (c != ' ' && x + adv >= widthPx && line.Length > 0)
            {
                var s = line.ToString();
                var cut = s.LastIndexOf(' ');
                if (cut >= 0)
                {
                    output.Append(s, 0, cut + 1).Append("\r\n");
                    line.Clear().Append(s, cut + 1, s.Length - cut - 1);
                }
                else
                {
                    output.Append(s).Append("\r\n");
                    line.Clear();
                }

                x = 0;
                for (var k = 0; k < line.Length; k++)
                {
                    x += advance(line[k]);
                }

                continue; // this character is measured again
            }

            line.Append(c);
            x += adv;
            i++;
        }

        return output.Append(line).ToString();
    }

    /// <summary>The lines of a wrapped stream, breaks removed.</summary>
    public static string[] Lines(string wrapped) => wrapped.Split("\r\n");
}
