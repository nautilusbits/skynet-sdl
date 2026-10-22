using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SkyNet.Edit;

/// <summary>
/// A <c>.gltf</c> and the files it names as one <c>.glb</c> (glTF 2.0, binary container): the game reads a
/// whole-robot replacement from one file. Its buffers become one BIN chunk (each 4-byte aligned, the buffer
/// views moved along), its images by URI buffer views at the end; <c>data:</c> URIs are decoded alike.
/// <code>
///   robot.gltf ─► buffers[i].uri  "robot.bin" / data:  ─┐
///                 images[j].uri   "skin.png"  / data:  ─┼─► BIN = buffer 0 | buffer 1 | ... | image views
///                                                       └─► JSON: one buffer, no URIs ─► GLB
/// </code>
/// </summary>
public static class GltfPack
{
    private const uint Magic = 0x46546C67, Version = 2, JsonChunk = 0x4E4F534A, BinChunk = 0x004E4942;
    private const int Align = 4, HeaderSize = 12, ChunkHeaderSize = 8;
    private const string DataUri = "data:", Base64 = ";base64,";

    /// <summary>The file <paramref name="path"/> as .glb bytes: a .glb as it is, a .gltf packed with the files
    /// beside it. <see cref="InvalidDataException"/> when a file it names is missing or the JSON is not glTF.</summary>
    public static byte[] Read(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (!path.EndsWith(".gltf", StringComparison.OrdinalIgnoreCase))
        {
            return bytes;
        }

        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        return Pack(bytes, uri => Beside(dir, uri));
    }

    /// <summary>A .gltf's JSON as a .glb, <paramref name="resolve"/> giving a relative URI's bytes.</summary>
    public static byte[] Pack(byte[] gltf, Func<string, byte[]> resolve)
    {
        JsonObject json;
        try
        {
            // A UTF-8 byte order mark (some editors write one) is not JSON.
            var text = gltf.AsSpan().StartsWith(Encoding.UTF8.Preamble) ? gltf.AsSpan(Encoding.UTF8.Preamble.Length) : gltf;
            json = JsonNode.Parse(text)?.AsObject() ?? throw new InvalidDataException("not a glTF JSON object");
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            throw new InvalidDataException($"not glTF JSON ({e.Message})", e);
        }

        using var bin = new MemoryStream();

        // Buffers one after another; each view moved by its buffer's start.
        var starts = new List<int>();
        foreach (var buffer in Array(json, "buffers"))
        {
            Pad(bin);
            starts.Add((int)bin.Length);
            var uri = (string?)buffer["uri"] ?? throw new InvalidDataException("a buffer without a URI (a .gltf has no BIN chunk)");
            bin.Write(Bytes(uri, resolve));
        }

        if (json["bufferViews"] is not JsonArray views)
        {
            views = [];
            json["bufferViews"] = views;
        }

        foreach (var view in views.OfType<JsonObject>())
        {
            var buffer = (int?)view["buffer"] ?? 0;
            if (buffer < 0 || buffer >= starts.Count)
            {
                throw new InvalidDataException($"a buffer view names buffer {buffer}");
            }

            view["byteOffset"] = ((int?)view["byteOffset"] ?? 0) + starts[buffer];
            view["buffer"] = 0;
        }

        // Images by URI: a view each at the end.
        foreach (var image in Array(json, "images"))
        {
            if ((string?)image["uri"] is not { } uri)
            {
                continue;
            }

            Pad(bin);
            var data = Bytes(uri, resolve);
            var view = new JsonObject { ["buffer"] = 0, ["byteOffset"] = (int)bin.Length, ["byteLength"] = data.Length };
            bin.Write(data);
            views.Add(view);
            image.Remove("uri");
            image["bufferView"] = views.Count - 1;
            image["mimeType"] ??= MimeOf(uri);
        }

        Pad(bin);
        json["buffers"] = new JsonArray(new JsonObject { ["byteLength"] = (int)bin.Length });
        return Glb(Encoding.UTF8.GetBytes(json.ToJsonString()), bin.ToArray());
    }

    // The objects of a top-level array (none: empty).
    private static IEnumerable<JsonObject> Array(JsonObject json, string name) =>
        json[name] is JsonArray a ? [.. a.OfType<JsonObject>()] : [];

    // A URI's bytes: a data: URI decoded, else the file beside the .gltf.
    private static byte[] Bytes(string uri, Func<string, byte[]> resolve)
    {
        if (!uri.StartsWith(DataUri, StringComparison.OrdinalIgnoreCase))
        {
            return resolve(Uri.UnescapeDataString(uri));
        }

        var at = uri.IndexOf(Base64, StringComparison.OrdinalIgnoreCase);
        if (at < 0)
        {
            throw new InvalidDataException("a data: URI that is not base64");
        }

        try
        {
            return Convert.FromBase64String(uri[(at + Base64.Length)..]);
        }
        catch (FormatException e)
        {
            throw new InvalidDataException("a data: URI that does not decode", e);
        }
    }

    // A file the .gltf names, kept inside its folder's tree.
    private static byte[] Beside(string dir, string uri)
    {
        var path = Path.GetFullPath(Path.Combine(dir, uri));
        if (!File.Exists(path))
        {
            throw new InvalidDataException($"{uri}: not found beside the .gltf");
        }

        return File.ReadAllBytes(path);
    }

    private static string MimeOf(string uri) =>
        uri.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || uri.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
            || uri.StartsWith("data:image/jpeg", StringComparison.OrdinalIgnoreCase)
            ? "image/jpeg"
            : "image/png";

    private static void Pad(MemoryStream s)
    {
        while (s.Length % Align != 0)
        {
            s.WriteByte(0);
        }
    }

    // Header, the JSON chunk (padded with spaces), the BIN chunk (padded with zeros).
    private static byte[] Glb(byte[] json, byte[] bin)
    {
        const int JsonAt = HeaderSize + ChunkHeaderSize;
        var jsonLength = (json.Length + Align - 1) & ~(Align - 1);
        var binAt = JsonAt + jsonLength;
        var total = binAt + ChunkHeaderSize + bin.Length;
        var glb = new byte[total];
        var span = glb.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(span, Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], Version);
        BinaryPrimitives.WriteUInt32LittleEndian(span[8..], (uint)total);
        BinaryPrimitives.WriteUInt32LittleEndian(span[12..], (uint)jsonLength);
        BinaryPrimitives.WriteUInt32LittleEndian(span[16..], JsonChunk);
        json.CopyTo(span[JsonAt..]);
        span.Slice(JsonAt + json.Length, jsonLength - json.Length).Fill((byte)' ');
        BinaryPrimitives.WriteUInt32LittleEndian(span[binAt..], (uint)bin.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(span[(binAt + 4)..], BinChunk);
        bin.CopyTo(span[(binAt + ChunkHeaderSize)..]);
        return glb;
    }
}
