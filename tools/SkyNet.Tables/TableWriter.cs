using System.Text;
using RetroEng.Json;
using SkyNet.Data;

namespace SkyNet.Tables;

/// <summary>
/// Writes a table file for diffs: the header and each row on lines of their own, a row (or a
/// nested object) on one line when it fits <see cref="Width"/> columns, every array of plain values
/// on one line. LF line ends and a final newline, so a regenerated file is byte-identical.
/// </summary>
public static class TableWriter
{
    public const int Width = 260;

    public static string Write<T>(TableFile<T> file, TableSchema<T> schema)
    {
        var sb = new StringBuilder();
        Value(sb, schema.Write(file), 0, top: true);
        return sb.Append('\n').ToString();
    }

    private static void Value(StringBuilder sb, JsonItem e, int indent, bool top = false)
    {
        if (!top && (IsPlainArray(e) || (e.Kind is JsonKind.Object or JsonKind.Array && Compact(e).Length + indent <= Width)))
        {
            sb.Append(Compact(e));
            return;
        }

        var pad = new string(' ', indent + 2);
        switch (e.Kind)
        {
            case JsonKind.Object:
            {
                sb.Append("{\n");
                var first = true;
                foreach (var p in e.Members)
                {
                    sb.Append(first ? "" : ",\n").Append(pad).Append(Str(p.Key)).Append(": ");
                    Value(sb, p.Value, indent + 2);
                    first = false;
                }

                sb.Append('\n').Append(' ', indent).Append('}');
                break;
            }
            case JsonKind.Array:
            {
                sb.Append("[\n");
                var first = true;
                foreach (var v in e.Items)
                {
                    sb.Append(first ? "" : ",\n").Append(pad);
                    Value(sb, v, indent + 2);
                    first = false;
                }

                sb.Append('\n').Append(' ', indent).Append(']');
                break;
            }
            default:
                sb.Append(Compact(e));
                break;
        }
    }

    private static bool IsPlainArray(JsonItem e) =>
        e.Kind == JsonKind.Array && e.Items.All(v => v.Kind is not (JsonKind.Object or JsonKind.Array));

    private static string Compact(JsonItem e) => e.Kind switch
    {
        JsonKind.Object => "{" + string.Join(", ", e.Members.Select(p => Str(p.Key) + ": " + Compact(p.Value))) + "}",
        JsonKind.Array => "[" + string.Join(", ", e.Items.Select(Compact)) + "]",
        JsonKind.String => Str(e.AsString()),
        JsonKind.Number => e.NumberText,
        JsonKind.True => "true",
        JsonKind.False => "false",
        _ => "null",
    };

    /// <summary>A JSON string escaping only what JSON requires (quotes, backslashes, control characters).</summary>
    private static string Str(string s)
    {
        var sb = new StringBuilder("\"");
        foreach (var c in s)
        {
            _ = c switch
            {
                '"' => sb.Append("\\\""),
                '\\' => sb.Append("\\\\"),
                '\n' => sb.Append("\\n"),
                '\t' => sb.Append("\\t"),
                < ' ' => sb.Append($"\\u{(int)c:x4}"),
                _ => sb.Append(c),
            };
        }

        return sb.Append('"').ToString();
    }
}
