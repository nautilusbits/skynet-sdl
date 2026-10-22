using System.Globalization;
using System.Numerics;
using RetroEng.Json;
using RetroEng.Content;
using RetroEng.Diagnostics;
using RetroEng.Models;
using RetroEng.Platform;

namespace SkyNet.Game.Mods;

/// <summary>How a replacement model is sized against the DOS model it stands in for.</summary>
public enum ModelFit { None, Height, Width, Box, Stretch }

/// <summary>Which point of the replacement goes where on the DOS model.</summary>
public enum ModelAlign { Base, Centre, Origin }

/// <summary>
/// A mod's <c>models/NAME.json</c>, the sidecar of <c>models/NAME.glb</c> (docs/modding.md): how the
/// glTF model (Y up, metres by convention) is turned, sized and placed in the DOS model's own space
/// (Y up, world units), and which DOS frames it covers. Every key is optional.
/// </summary>
/// <param name="Rotation">Degrees about X, then Y, then Z, applied to the glTF model first.</param>
/// <param name="Fit">Sizing to the DOS model's box (after the rotation): its height, its larger
/// horizontal side, the whole box (the smallest of the three ratios: it fits inside), each axis to
/// the box's (stretch: the box exactly, proportions not kept), or none.</param>
/// <param name="Scale">A factor after the fit; with <see cref="ModelFit.None"/>, world units per glTF unit.</param>
/// <param name="Align">Bottom centre on the DOS box's bottom centre, centre on centre, or the glTF
/// origin on the DOS model's origin.</param>
/// <param name="Offset">World units added last, in the DOS model's space (Y up).</param>
/// <param name="Frames">The DOS model's animation frames it stands in for (static props: frame 0);
/// see <see cref="AllFrames"/>.</param>
public sealed record ModelSidecar(Vector3 Rotation, ModelFit Fit, float Scale, ModelAlign Align, Vector3 Offset, int[] Frames)
{
    public static readonly ModelSidecar Default = new(Vector3.Zero, ModelFit.Height, 1f, ModelAlign.Base, Vector3.Zero, [0]);

    /// <summary>The longest frame range a sidecar may name ("0-9999" is refused).</summary>
    public const int MaxFrameRange = 1024;

    /// <summary><c>"frames": "all"</c>: it stands in for every frame (<see cref="Frames"/> is then empty).</summary>
    public bool AllFrames { get; init; }

    /// <summary><c>"frameModels"</c>: other glTF files of the mod's <c>models/</c> folder (named without
    /// the extension) standing in for some DOS frames each, in the order given (the first that covers a
    /// frame wins, before this model). Each is placed as this model is: authored in its space.</summary>
    public IReadOnlyList<FrameModel> FrameModels { get; init; } = [];

    /// <summary><c>"lod"</c>: when the model's levels of detail are drawn (<see cref="LodSettings"/>).</summary>
    internal LodSettings Lod { get; init; } = LodSettings.Default;

    /// <summary>A glTF file of a <c>"frameModels"</c> entry and the DOS frames it stands in for.</summary>
    public sealed record FrameModel(string Model, int[] Frames, bool All)
    {
        public bool Covers(int frame) => All || Frames.Contains(frame);
    }

    /// <summary>Reads a sidecar; an unknown key or a bad value throws <see cref="FormatException"/>.</summary>
    public static ModelSidecar Parse(byte[] json)
    {
        var root = JsonItem.Parse(json, lenient: true).AsObject();

        var s = Default;
        foreach (var p in root.Members)
        {
            s = p.Key switch
            {
                "rotation" => s with { Rotation = Vec(p.Value) },
                "offset" => s with { Offset = Vec(p.Value) },
                "scale" => s with { Scale = (float)p.Value.AsDouble() },
                "fit" => s with
                {
                    Fit = p.Value.AsString() switch
                    {
                        "none" => ModelFit.None,
                        "height" => ModelFit.Height,
                        "width" => ModelFit.Width,
                        "box" => ModelFit.Box,
                        "stretch" => ModelFit.Stretch,
                        var v => throw new FormatException($"fit '{v}' (none, height, width, box or stretch)"),
                    },
                },
                "align" => s with
                {
                    Align = p.Value.AsString() switch
                    {
                        "base" => ModelAlign.Base,
                        "centre" or "center" => ModelAlign.Centre,
                        "origin" => ModelAlign.Origin,
                        var v => throw new FormatException($"align '{v}' (base, centre or origin)"),
                    },
                },
                "frames" => FrameList(p.Value) switch { var (frames, all) => s with { Frames = frames, AllFrames = all } },
                "frameModels" => s with { FrameModels = p.Value.ToArray(FrameModelOf) },
                "lod" => s with { Lod = LodSettings.Parse(p.Value) },
                "comment" or "source" or "licence" or "license" => s,
                _ => throw new FormatException($"unknown key '{p.Key}'"),
            };
        }

        if (!(s.Scale > 0f) || !float.IsFinite(s.Scale))
        {
            throw new FormatException("scale must be a positive number");
        }

        return s;

        static FrameModel FrameModelOf(JsonItem e)
        {
            string? model = null;
            (int[] Frames, bool All)? frames = null;
            foreach (var m in e.AsObject().Members)
            {
                switch (m.Key)
                {
                    case "model": model = m.Value.AsString(); break;
                    case "frames": frames = FrameList(m.Value); break;
                    case "comment": break;
                    default: throw new FormatException($"frameModels: unknown key '{m.Key}'");
                }
            }

            if (string.IsNullOrWhiteSpace(model) || model.IndexOfAny(['/', '\\', '.']) >= 0)
            {
                throw new FormatException("frameModels: each needs a \"model\", a file name in models/ without folder or extension");
            }

            return frames is { } fr ? new FrameModel(model, fr.Frames, fr.All) : throw new FormatException($"frameModels: '{model}' needs \"frames\"");
        }

        // Frame numbers and "first-last" ranges in a list, one range alone, or "all".
        static (int[] Frames, bool All) FrameList(JsonItem e)
        {
            if (e.TryString(out var word))
            {
                return word == "all" ? ([], true) : ([.. Range(word)], false);
            }

            var frames = new List<int>();
            foreach (var x in e.AsArray().Items)
            {
                if (x.TryString(out var range))
                {
                    frames.AddRange(Range(range));
                }
                else
                {
                    frames.Add(x.AsInt(0, int.MaxValue));
                }
            }

            return ([.. frames.Distinct()], false);
        }

        static IEnumerable<int> Range(string range)
        {
            var dash = range.IndexOf('-', 1);
            if (dash < 0 || !int.TryParse(range[..dash], NumberStyles.None, CultureInfo.InvariantCulture, out var from)
                || !int.TryParse(range[(dash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var to)
                || to < from || to - from >= MaxFrameRange)
            {
                throw new FormatException($"frames '{range}' (\"first-last\", at most {MaxFrameRange} frames, or \"all\")");
            }

            return Enumerable.Range(from, to - from + 1);
        }

        static Vector3 Vec(JsonItem e)
        {
            var f = e.ToArray(x => (float)x.AsDouble());
            return f.Length == 3 ? new Vector3(f[0], f[1], f[2]) : throw new FormatException("a vector needs 3 numbers");
        }
    }

    /// <summary>The glTF model's space to the DOS model's (row vectors): the rotation, the fit and
    /// scale, the alignment, the offset. <paramref name="dos"/> is the DOS model's box (frame 0, Y up).</summary>
    public Matrix4x4 Placement(GltfModel gltf, (Vector3 Min, Vector3 Max) dos)
    {
        const float Degrees = MathF.PI / 180f;
        var turn = Matrix4x4.CreateRotationX(Rotation.X * Degrees) * Matrix4x4.CreateRotationY(Rotation.Y * Degrees)
            * Matrix4x4.CreateRotationZ(Rotation.Z * Degrees);
        var (min, max) = (new Vector3(float.MaxValue), new Vector3(float.MinValue));
        foreach (var p in gltf.Primitives.SelectMany(p => p.Positions))
        {
            var q = Vector3.Transform(p, turn);
            (min, max) = (Vector3.Min(min, q), Vector3.Max(max, q));
        }

        if (min.X > max.X)
        {
            (min, max) = (Vector3.Zero, Vector3.Zero);
        }

        var (size, dosSize) = (max - min, dos.Max - dos.Min);
        static float Finite(float f) => float.IsFinite(f) ? f : 1f;
        static float Ratio(float want, float have) => have > 1e-6f && want > 1e-6f ? want / have : float.PositiveInfinity;
        var fit = Fit switch
        {
            ModelFit.Height => Ratio(dosSize.Y, size.Y),
            ModelFit.Width => Ratio(MathF.Max(dosSize.X, dosSize.Z), MathF.Max(size.X, size.Z)),
            ModelFit.Box => MathF.Min(Ratio(dosSize.X, size.X), MathF.Min(Ratio(dosSize.Y, size.Y), Ratio(dosSize.Z, size.Z))),
            _ => 1f,
        };
        fit = Finite(fit);

        var scale = new Vector3(fit * Scale);
        if (Fit == ModelFit.Stretch)
        {
            scale = new Vector3(Finite(Ratio(dosSize.X, size.X)), Finite(Ratio(dosSize.Y, size.Y)), Finite(Ratio(dosSize.Z, size.Z))) * Scale;
        }

        var at = Align switch
        {
            ModelAlign.Base => new Vector3((dos.Min.X + dos.Max.X) / 2, dos.Min.Y, (dos.Min.Z + dos.Max.Z) / 2)
                - new Vector3((min.X + max.X) / 2, min.Y, (min.Z + max.Z) / 2) * scale,
            ModelAlign.Centre => (dos.Min + dos.Max) / 2 - (min + max) / 2 * scale,
            _ => Vector3.Zero,
        };
        return turn * Matrix4x4.CreateScale(scale) * Matrix4x4.CreateTranslation(at + Offset);
    }
}

/// <summary>A DOS model's replacement from a mod: the glTF model, its sidecar and its materials'
/// colours ready to upload (decoded once, shared by every level).</summary>
public sealed class ModelReplacement
{
    public required string Name { get; init; }
    public required GltfModel Model { get; init; }
    public required ModelSidecar Sidecar { get; init; }
    /// <summary>Per glTF material; the last one is the default material (a primitive with none).</summary>
    public required IReadOnlyList<ReplacementSurface> Surfaces { get; init; }

    /// <summary>glTF's default material (a primitive without one): white, opaque, one-sided.</summary>
    private static readonly GltfMaterial Default = new("", Vector4.One, null, GltfAlphaMode.Opaque, 0.5f, false);

    /// <summary>The surface a primitive draws with.</summary>
    public int SurfaceOf(GltfPrimitive p)
    {
        if (p.Material < 0)
        {
            return Surfaces.Count - 1;
        }

        // A level of detail draws with its main model's surfaces, matched by material.
        if (_materialMap is not { } map)
        {
            return p.Material;
        }

        return p.Material < map.Length && map[p.Material] >= 0 ? map[p.Material] : Surfaces.Count - 1;
    }

    /// <summary>A level of detail's materials to its main model's surfaces (−1: the default); null: its own.</summary>
    private int[]? _materialMap;

    /// <summary>The levels of detail from 1 (<c>NAME.lod1.glb</c>...), placed and drawn with this
    /// model's sidecar and surfaces.</summary>
    internal IReadOnlyList<ModelReplacement> Lods { get; private init; } = [];

    /// <summary>The <c>"frameModels"</c> that read, in the sidecar's order (placed as this model is).</summary>
    public IReadOnlyList<(ModelSidecar.FrameModel Entry, ModelReplacement Model)> FrameModels { get; init; } = [];

    /// <summary>Whether this model itself stands in for DOS frame <paramref name="frame"/>.</summary>
    public bool Covers(int frame) => Sidecar.AllFrames || Sidecar.Frames.Contains(frame);

    /// <summary>What stands in for DOS frame <paramref name="frame"/>: the first frame model that
    /// covers it, else this model if it does; null: the DOS frame is drawn.</summary>
    public ModelReplacement? ForFrame(int frame)
    {
        foreach (var (entry, model) in FrameModels)
        {
            if (entry.Covers(frame))
            {
                return model;
            }
        }

        return Covers(frame) ? this : null;
    }

    public static ModelReplacement? Load(VirtualFileSystem vfs, string name)
    {
        var replacement = Load(vfs, name, null);
        if (replacement == null || replacement.Sidecar.FrameModels.Count == 0)
        {
            return replacement;
        }

        // A frame model's own sidecar is not read: the main model's places it.
        var frameModels = new List<(ModelSidecar.FrameModel, ModelReplacement)>();
        foreach (var entry in replacement.Sidecar.FrameModels)
        {
            if (Load(vfs, entry.Model, replacement.Sidecar) is { } model)
            {
                frameModels.Add((entry, model));
            }
            else
            {
                Log.Warn("Mods", $"models/{name}.json: frame model '{entry.Model}' is missing or does not read; its frames are drawn as the others");
            }
        }

        return new ModelReplacement
        {
            Name = replacement.Name, Model = replacement.Model, Sidecar = replacement.Sidecar,
            Surfaces = replacement.Surfaces, FrameModels = frameModels, Lods = replacement.Lods,
        };
    }

    /// <summary><paramref name="main"/>'s levels of detail: <c>NAME.lod1.glb</c> up to the first
    /// missing (one that does not read is logged and ends the list).</summary>
    private static ModelReplacement[] LoadLods(VirtualFileSystem vfs, string name, ModelReplacement main)
    {
        var lods = new List<ModelReplacement>();
        for (var level = 1; level <= LodSettings.MaxLevels; level++)
        {
            var path = $"models/{LodSettings.FileName(name, level)}.glb";
            if (vfs.TryReadAllBytes(path) is not { } bytes)
            {
                break;
            }

            try
            {
                var model = Gltf.Load(bytes);
                lods.Add(new ModelReplacement
                {
                    Name = main.Name, Model = model, Sidecar = main.Sidecar, Surfaces = main.Surfaces,
                    _materialMap = MaterialMap(model.Materials, main.Model.Materials),
                });
                Log.Info("Mods", $"{path}: {model.Triangles} triangles");
            }
            catch (Exception e) when (!ModContent.Critical(e))
            {
                Log.Warn("Mods", $"{path}: {e.GetType().Name}: {e.Message}; levels from here not drawn");
                break;
            }
        }

        return [.. lods];
    }

    /// <summary>Each of <paramref name="lod"/>'s materials as the index of <paramref name="main"/>'s of
    /// the same name, else of the same index; −1: none.</summary>
    internal static int[] MaterialMap(IReadOnlyList<GltfMaterial> lod, IReadOnlyList<GltfMaterial> main)
    {
        var map = new int[lod.Count];
        for (var i = 0; i < lod.Count; i++)
        {
            var named = -1;
            for (var j = 0; j < main.Count && named < 0; j++)
            {
                if (lod[i].Name.Length > 0 && lod[i].Name == main[j].Name)
                {
                    named = j;
                }
            }

            map[i] = named >= 0 ? named : i < main.Count ? i : -1;
        }

        return map;
    }

    /// <summary>One glTF file and its sidecar (<paramref name="sidecar"/>: given for a frame model, the main model's).</summary>
    internal static ModelReplacement? Load(VirtualFileSystem vfs, string name, ModelSidecar? sidecar)
    {
        var dir = "models/";
        var (path, bytes) = vfs.TryReadAllBytes($"{dir}{name}.glb") is { } glb ? ($"{dir}{name}.glb", glb)
            : vfs.TryReadAllBytes($"{dir}{name}.gltf") is { } gltf ? ($"{dir}{name}.gltf", gltf)
            : (null, null);
        if (path == null || bytes == null)
        {
            return null;
        }

        try
        {
            var model = Gltf.Load(bytes, uri =>
            {
                try
                {
                    // Beside the file ("sprites/X" reads models/sprites/URI).
                    return vfs.TryReadAllBytes(dir + name[..(name.LastIndexOf('/') + 1)] + uri);
                }
                catch (ArgumentException)
                {
                    return null;
                }
            });
            sidecar ??= vfs.TryReadAllBytes($"{dir}{name}.json") is { } json ? ModelSidecar.Parse(json) : ModelSidecar.Default;
            var surfaces = model.Materials.Append(Default).Select(m => ReplacementSurface.Build(path, model, m)).ToArray();
            Log.Info("Mods", string.Create(CultureInfo.InvariantCulture,
                $"{path}: {model.Triangles} triangles, {model.Materials.Count} materials, fit {sidecar.Fit}, scale {sidecar.Scale}"));
            var main = new ModelReplacement { Name = name, Model = model, Sidecar = sidecar, Surfaces = surfaces };
            return new ModelReplacement
            {
                Name = name, Model = model, Sidecar = sidecar, Surfaces = surfaces, Lods = LoadLods(vfs, name, main),
            };
        }
        catch (Exception e) when (!ModContent.Critical(e))
        {
            // Any fault of the mod's file (a broken glTF, an image too big, a buffer it lies about)
            // leaves the DOS model in place; the game goes on.
            Log.Warn("Mods", $"{path}: {e.GetType().Name}: {e.Message}; the DOS model stays");
            return null;
        }
    }
}
