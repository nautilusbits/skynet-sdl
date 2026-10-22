using RetroEng.Json;

namespace SkyNet.Edit;

/// <summary>A typed MCP tool's argument: name, JSON Schema type and what it means.
/// <c>"integer[]"</c> is an array of integers, <c>"string[]"</c> of strings.</summary>
internal readonly record struct McpArg(string Name, string Type, string Description, bool Required = false);

/// <summary>The typed MCP tools' plumbing: input schemas from <see cref="McpArg"/> lists and the arguments
/// read back (a wrong type or a missing required one throws <see cref="JsonDataException"/>, which the tool
/// answers as an error).</summary>
internal static class McpArgs
{
    private const string ArraySuffix = "[]";

    /// <summary>The JSON Schema object of <paramref name="args"/>, e.g. {"type":"object","properties":{"key":{"type":"integer"}}}.</summary>
    public static string Schema(params McpArg[] args)
    {
        var properties = JsonItem.NewObject();
        foreach (var a in args)
        {
            properties.Add(a.Name, Type(a.Type).Add("description", a.Description));
        }

        var schema = JsonItem.NewObject().Add("type", "object").Add("properties", properties);
        if (args.Any(a => a.Required))
        {
            schema.Add("required", JsonItem.NewArray(args.Where(a => a.Required), a => a.Name));
        }

        return schema.ToString();
    }

    // "integer[]": an array of integers.
    private static JsonItem Type(string type) => type.EndsWith(ArraySuffix, StringComparison.Ordinal)
        ? JsonItem.NewObject().Add("type", "array").Add("items", JsonItem.NewObject().Add("type", type[..^ArraySuffix.Length]))
        : JsonItem.NewObject().Add("type", type);

    public static int Int(this JsonItem a, string name) => a.Member(name).AsInt();

    public static int? OptInt(this JsonItem a, string name) => a.Opt(name)?.AsInt();

    public static float? OptFloat(this JsonItem a, string name) => a.Opt(name) is { } v ? (float)v.AsDouble() : null;

    public static float Float(this JsonItem a, string name) => (float)a.Member(name).AsDouble();

    public static string Text(this JsonItem a, string name) => a.Member(name).AsString();

    public static string? OptText(this JsonItem a, string name) => a.Opt(name)?.AsString();

    public static bool Flag(this JsonItem a, string name) => a.Opt(name)?.AsBool() == true;

    public static IReadOnlyList<string> Words(this JsonItem a, string name) => a.Opt(name)?.ToArray(v => v.AsString()) ?? [];

    /// <summary>"keys" (an array) or "key" (one): the entity keys a tool acts on.</summary>
    public static List<int> Keys(this JsonItem a) =>
        a.Opt("keys") is { } keys ? [.. keys.ToArray(k => k.AsInt())] : [a.Int("key")];
}
