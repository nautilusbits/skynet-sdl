using System.Numerics;
using RetroEng.Diagnostics;
using RetroEng.Models;
using RetroEng.Render;

namespace SkyNet.Game.Mods;

/// <summary>
/// An animated model (a skinned robot template, an animated sprite model) and its levels of detail
/// as one model, so that one <see cref="AnimatedInstance"/> poses them all: the engine draws a mesh
/// only with an instance of its own model.
/// <code>
///   NAME.robot.glb ──► nodes, skins, materials, clips, parts 0..n-1      level 0: parts 0..n-1
///   NAME.robot.lod1.glb ──► its parts, onto the main nodes and skins    level 1: parts n..m-1
///                                                                       (+ main parts with morphs)
/// </code>
/// A level's file must have the main file's joints (by node name); its parts with morph targets
/// are left out and the main ones drawn instead (decimation keeps no shapes).
/// </summary>
internal sealed class AnimatedLods
{
    private readonly int[][] _parts;

    private AnimatedLods(AnimatedModel model, int[][] parts, LodSettings settings)
    {
        (Model, _parts, Settings) = (model, parts, settings);
    }

    /// <summary>The main model with every level's parts after its own.</summary>
    public AnimatedModel Model { get; }
    public LodSettings Settings { get; }
    public int Levels => _parts.Length;

    /// <summary>The parts level <paramref name="level"/> draws.</summary>
    public ReadOnlySpan<int> Parts(int level) => _parts[Math.Clamp(level, 0, _parts.Length - 1)];

    /// <summary>The level to draw posed in <paramref name="bounds"/> (the model's space) placed by
    /// <paramref name="world"/>, now drawing <paramref name="current"/> (−1: none yet).</summary>
    public int Pick(int current, in LodView view, in Matrix4x4 world, Bounds bounds)
    {
        if (_parts.Length == 1)
        {
            return 0;
        }

        var size = bounds.Max - bounds.Min;
        var scale = new Vector3(world.M11, world.M12, world.M13).Length();
        var side = MathF.Max(size.X, MathF.Max(size.Y, size.Z)) * scale;
        var fraction = view.Fraction(Vector3.Transform((bounds.Min + bounds.Max) / 2f, world), side);
        return Math.Min(Settings.Pick(current, fraction), _parts.Length - 1);
    }

    /// <summary><paramref name="main"/> with <paramref name="lods"/> (coarser in order) merged; a level
    /// that does not fit (another skeleton) is logged and ends the list. <paramref name="path"/>: for the log.</summary>
    public static AnimatedLods Merge(AnimatedModel main, IReadOnlyList<AnimatedModel> lods, LodSettings settings, string path)
    {
        var single = new AnimatedLods(main, [[.. Enumerable.Range(0, main.Parts.Count)]], settings);
        if (lods.Count == 0)
        {
            return single;
        }

        var b = new AnimatedModelBuilder();
        foreach (var n in main.Nodes)
        {
            b.AddNode(n.Name, n.Parent, n.Rest);
        }

        foreach (var i in main.Images)
        {
            b.AddImage(i);
        }

        foreach (var m in main.Materials)
        {
            b.AddMaterial(m);
        }

        foreach (var s in main.Skins)
        {
            b.AddSkin(s);
        }

        foreach (var p in main.Parts)
        {
            b.AddPart(p);
        }

        foreach (var c in main.Clips)
        {
            b.AddClip(c);
        }

        // Main parts with morph targets: every level draws them.
        var morphs = Enumerable.Range(0, main.Parts.Count).Where(p => main.Parts[p].Shapes.Count > 1).ToArray();
        List<int[]> levels = [[.. Enumerable.Range(0, main.Parts.Count)]];
        var next = main.Parts.Count;
        for (var l = 0; l < lods.Count; l++)
        {
            if (Remap(main, lods[l]) is not { } parts)
            {
                Log.Warn("Mods", $"{path}: level {l + 1} has other joints than the main file; levels from here not drawn");
                break;
            }

            foreach (var p in parts)
            {
                b.AddPart(p);
            }

            levels.Add([.. Enumerable.Range(next, parts.Count), .. morphs]);
            next += parts.Count;
        }

        if (levels.Count == 1)
        {
            return single;
        }

        try
        {
            return new AnimatedLods(b.Build(), [.. levels], settings);
        }
        catch (ArgumentException e)
        {
            Log.Warn("Mods", $"{path}: levels of detail do not fit ({e.Message}); not drawn");
            return single;
        }
    }

    /// <summary><paramref name="lod"/>'s parts without morph targets onto <paramref name="main"/>'s
    /// nodes, skins and materials (matched by name); null: a node or joint it has not.</summary>
    private static List<AnimatedPart>? Remap(AnimatedModel main, AnimatedModel lod)
    {
        var nodes = new int[lod.Nodes.Count];
        for (var n = 0; n < nodes.Length; n++)
        {
            nodes[n] = main.FindNode(lod.Nodes[n].Name);
        }

        var materials = ModelReplacement.MaterialMap(lod.Materials, main.Materials);
        var parts = new List<AnimatedPart>();
        foreach (var p in lod.Parts)
        {
            if (p.Shapes.Count > 1)
            {
                continue;
            }

            var material = p.Material >= 0 && p.Material < materials.Length ? materials[p.Material] : -1;
            var remapped = new AnimatedPart(p.Name, Math.Max(material, 0), p.Indices, p.Uvs, p.Shapes) { Tangents = p.Tangents };
            if (p.Skin < 0)
            {
                if (p.Node < 0 || nodes[p.Node] < 0)
                {
                    return null;
                }

                parts.Add(remapped with { Node = nodes[p.Node] });
                continue;
            }

            if (SkinOf(main, lod.Skins[p.Skin], nodes) is not var (skin, joints) || p.Joints is not { } pj)
            {
                return null;
            }

            var mapped = new ushort[pj.Length];
            for (var i = 0; i < pj.Length; i++)
            {
                mapped[i] = joints[pj[i]];
            }

            parts.Add(remapped with { Skin = skin, Joints = mapped, Weights = p.Weights });
        }

        return parts;
    }

    /// <summary>The main skin holding every joint of <paramref name="lod"/> and each joint's index in
    /// it; null: none.</summary>
    private static (int Skin, ushort[] Joints)? SkinOf(AnimatedModel main, AnimatedSkin lod, int[] nodes)
    {
        for (var s = 0; s < main.Skins.Count; s++)
        {
            var joints = new ushort[lod.Joints.Length];
            var all = true;
            for (var j = 0; j < joints.Length && all; j++)
            {
                var at = nodes[lod.Joints[j]] is var node and >= 0 ? Array.IndexOf(main.Skins[s].Joints, node) : -1;
                all = at >= 0;
                joints[j] = (ushort)Math.Max(at, 0);
            }

            if (all)
            {
                return (s, joints);
            }
        }

        return null;
    }
}
