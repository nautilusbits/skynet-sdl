using RetroEng.Content;
using RetroEng.Diagnostics;
using RetroEng.Models;

namespace SkyNet.Game.Mods;

/// <summary>
/// A whole robot drawn as one skinned glTF model (docs/modding.md, Robot template):
/// <c>models/NAME.robot.glb</c> for the robots built on DOS model NAME, in the robot's own space
/// (Y up, ahead −Z, world units, no sidecar). Its joints <c>seg&lt;i&gt;_&lt;MODEL&gt;</c> follow the
/// segments' turns; its clips are picked by the AIS block playing (<see cref="Enemies.RobotAnims"/>).
/// Materials read as a prop's (<see cref="ReplacementSurface"/>): base colour, normal, metallic-roughness,
/// occlusion and emissive maps; tangents are the file's, else made from the texture coordinates.
/// Morph targets play as shapes (the template's <c>frame k</c> keys); a key mixing several targets
/// shows its dominant one.
/// </summary>
public sealed class RobotReplacement
{
    /// <summary>The file name's suffix before <c>.glb</c>.</summary>
    public const string Suffix = ".robot";

    public required string Path { get; init; }
    public required AnimatedModel Model { get; init; }
    /// <summary>Per model material.</summary>
    public required IReadOnlyList<ReplacementSurface> Surfaces { get; init; }
    /// <summary>The levels of detail (<c>NAME.robot.lod1.glb</c>...) merged into <see cref="Model"/>.</summary>
    internal AnimatedLods Lods { get; private init; } = null!;

    /// <summary><c>models/NAME.robot.glb</c> from <paramref name="vfs"/>; null when missing or broken (logged).</summary>
    public static RobotReplacement? Load(VirtualFileSystem vfs, string name)
    {
        var path = $"models/{name}{Suffix}.glb";
        if (vfs.TryReadAllBytes(path) is not { } bytes)
        {
            return null;
        }

        try
        {
            var main = Gltf.LoadAnimated(bytes);
            var surfaces = main.Materials.Select(m => ReplacementSurface.Build(path, main.Images, m)).ToArray();
            Log.Info("Mods", $"{path}: {main.Nodes.Count} nodes, {main.Parts.Count} parts, {main.Clips.Count} clips");
            var lods = AnimatedLods.Merge(main, LoadLods(vfs, name), LodOf(vfs, name), path);
            return new RobotReplacement { Path = path, Model = lods.Model, Surfaces = surfaces, Lods = lods };
        }
        catch (Exception e) when (!ModContent.Critical(e))
        {
            // A broken file leaves the DOS robot in place; the game goes on.
            Log.Warn("Mods", $"{path}: {e.GetType().Name}: {e.Message}; the DOS robot stays");
            return null;
        }
    }

    /// <summary><c>models/NAME.robot.lod1.glb</c>... up to the first missing (one that does not read
    /// is logged and ends the list).</summary>
    private static List<AnimatedModel> LoadLods(VirtualFileSystem vfs, string name)
    {
        var lods = new List<AnimatedModel>();
        for (var level = 1; level <= LodSettings.MaxLevels; level++)
        {
            var path = $"models/{LodSettings.FileName(name + Suffix, level)}.glb";
            if (vfs.TryReadAllBytes(path) is not { } bytes)
            {
                break;
            }

            try
            {
                lods.Add(Gltf.LoadAnimated(bytes));
                Log.Info("Mods", $"{path}: {lods[^1].Parts.Count} parts");
            }
            catch (Exception e) when (!ModContent.Critical(e))
            {
                Log.Warn("Mods", $"{path}: {e.GetType().Name}: {e.Message}; levels from here not drawn");
                break;
            }
        }

        return lods;
    }

    /// <summary>The template's <c>models/NAME.robot.json</c> (only <c>"lod"</c>); none or broken: the defaults.</summary>
    private static LodSettings LodOf(VirtualFileSystem vfs, string name)
    {
        var path = $"models/{name}{Suffix}.json";
        if (vfs.TryReadAllBytes(path) is not { } json)
        {
            return LodSettings.Default;
        }

        try
        {
            return LodSettings.ParseFile(json);
        }
        catch (Exception e) when (!ModContent.Critical(e))
        {
            Log.Warn("Mods", $"{path}: {e.Message}; default levels of detail");
            return LodSettings.Default;
        }
    }
}
