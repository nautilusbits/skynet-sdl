using System.Collections.Immutable;

namespace SkyNet.Formats;

/// <summary>One TRANSFRM.PRS section: a destructible's mesh and its damage stages (frame0 = intact).</summary>
public sealed record TransformObject(string Name, string Type, ImmutableArray<string> Frames);

/// <summary>
/// TRANSFRM.PRS (in MDMDBRIF.BSA): the destructible "transform" objects, ported from the Godot
/// port's transfrm_prs.gd. Sections <c>[objectN]</c> hold <c>name</c>, <c>type</c>, <c>frames</c>
/// and <c>frame0..</c> keys; keys are matched lower-case, names and frames are lower-cased, frames
/// are kept in file order (the <c>frames</c> count is not used). A section is kept only when it has
/// a name and more than one frame; a later section with the same name replaces an earlier one.
/// </summary>
public sealed class TransfrmPrs
{
    /// <summary>Lower-case intact mesh name → its object.</summary>
    public ImmutableDictionary<string, TransformObject> Objects { get; }

    TransfrmPrs(ImmutableDictionary<string, TransformObject> objects) => Objects = objects;

    public static TransfrmPrs Parse(ReadOnlySpan<byte> bytes)
    {
        var objs = ImmutableDictionary.CreateBuilder<string, TransformObject>(StringComparer.Ordinal);
        string name = "", type = "";
        var frames = ImmutableArray.CreateBuilder<string>();

        void Flush()
        {
            if (name.Length > 0 && frames.Count > 1)
                objs[name] = new TransformObject(name, type, frames.ToImmutable());
            name = "";
            type = "";
            frames.Clear();
        }

        foreach (var line in TextLines.Split(bytes))
        {
            if (line.Length == 0 || line[0] == ';')
                continue;
            if (line[0] == '[')
            {
                Flush();
                continue;
            }
            int eq = line.IndexOf('=');
            if (eq < 0)
                continue;
            string key = TextLines.Strip(line[..eq]).ToLowerInvariant();
            string val = TextLines.Strip(line[(eq + 1)..]);
            if (key == "name")
                name = val.ToLowerInvariant();
            else if (key == "type")
                type = val;
            else if (key.StartsWith("frame", StringComparison.Ordinal) && key != "frames")
                frames.Add(val.ToLowerInvariant());
        }
        Flush();
        return new TransfrmPrs(objs.ToImmutable());
    }

    /// <summary>The damage stages of a mesh (any case), or empty when it is not a transform object.</summary>
    public ImmutableArray<string> StagesOf(string mesh) =>
        Objects.TryGetValue(mesh.ToLowerInvariant(), out var o) ? o.Frames : [];
}
