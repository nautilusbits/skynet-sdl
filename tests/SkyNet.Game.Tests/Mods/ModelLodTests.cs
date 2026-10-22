using System.Numerics;
using System.Text;
using RetroEng.Json;
using RetroEng.Models;
using SkyNet.Game.Mods;

namespace SkyNet.Game.Tests.Mods;

/// <summary>Levels of detail of mod models: the picking with its hysteresis, the sidecar, the file
/// names, the loading and the merge of animated levels.</summary>
public class ModelLodTests
{
    private static LodSettings Lod(string json) => LodSettings.Parse(JsonItem.Parse(Encoding.UTF8.GetBytes(json), lenient: true));

    [Theory]
    [InlineData(0.5f, 0)]
    [InlineData(0.25f, 0)]
    [InlineData(0.2f, 1)]
    [InlineData(0.08f, 1)]
    [InlineData(0.05f, 2)]
    public void The_first_pick_takes_the_thresholds_as_they_are(float fraction, int level) =>
        Assert.Equal(level, LodSettings.Default.Pick(-1, fraction));

    [Theory]
    // Level 1 holds until 0.25 · 1.1 = 0.275 going back to 0.
    [InlineData(1, 0.26f, 1)]
    [InlineData(1, 0.28f, 0)]
    // Level 0 holds until 0.25 · 0.9 = 0.225 going to 1.
    [InlineData(0, 0.23f, 0)]
    [InlineData(0, 0.22f, 1)]
    // Level 1 goes to 2 below 0.08 · 0.9 = 0.072; 2 comes back above 0.088.
    [InlineData(1, 0.075f, 1)]
    [InlineData(1, 0.07f, 2)]
    [InlineData(2, 0.085f, 2)]
    [InlineData(2, 0.09f, 1)]
    // A jump crosses several levels at once.
    [InlineData(0, 0.01f, 2)]
    [InlineData(2, 0.9f, 0)]
    public void A_switch_must_clear_the_hysteresis_band(int current, float fraction, int level) =>
        Assert.Equal(level, LodSettings.Default.Pick(current, fraction));

    [Fact]
    public void No_view_draws_the_model_itself()
    {
        Assert.Equal(float.PositiveInfinity, default(LodView).Fraction(Vector3.One, 10f));
        Assert.Equal(0, LodSettings.Default.Pick(2, float.PositiveInfinity));
    }

    [Fact]
    public void The_fraction_is_the_size_over_the_screen_height_at_its_distance()
    {
        // 90 degrees: the screen is 2 · distance tall; 10 across at 100 covers 10 / 200.
        var view = LodView.Of(Vector3.Zero, MathF.PI / 2f);

        Assert.Equal(0.05f, view.Fraction(new Vector3(0, 0, -100), 10f), 1e-5f);
    }

    [Fact]
    public void A_sidecar_sets_or_turns_off_the_levels()
    {
        var s = ModelSidecar.Parse(Encoding.UTF8.GetBytes("""{"lod":{"screen":[0.4,0.2,0.1],"hysteresis":0.2}}"""));

        Assert.Equal([0.4f, 0.2f, 0.1f], s.Lod.Screen);
        Assert.Equal(0.2f, s.Lod.Hysteresis);
        Assert.Same(LodSettings.Default, ModelSidecar.Parse(Encoding.UTF8.GetBytes("{}")).Lod);
        Assert.Equal(0, Lod("\"off\"").Pick(-1, 0.001f));
        Assert.Equal([0.25f, 0.08f], Lod("""{"hysteresis":0}""").Screen);
    }

    [Theory]
    [InlineData("""{"screen":[0.1,0.2]}""")]
    [InlineData("""{"screen":[1.5]}""")]
    [InlineData("""{"screen":[0]}""")]
    [InlineData("""{"screen":[0.5,0.4,0.3,0.2]}""")]
    [InlineData("""{"hysteresis":0.6}""")]
    [InlineData("""{"distance":100}""")]
    [InlineData("\"on\"")]
    public void A_bad_lod_is_refused(string json) => Assert.Throws<FormatException>(() => Lod(json));

    [Fact]
    public void A_robot_sidecar_holds_only_the_lod()
    {
        Assert.Equal([0.3f], LodSettings.ParseFile(Encoding.UTF8.GetBytes("""{"lod":{"screen":[0.3]},"comment":"x"}""")).Screen);
        Assert.Throws<FormatException>(() => LodSettings.ParseFile(Encoding.UTF8.GetBytes("""{"fit":"box"}""")));
    }

    [Theory]
    [InlineData("HUMMER", 1, "HUMMER.lod1")]
    [InlineData("SPIDBOT.robot", 2, "SPIDBOT.robot.lod2")]
    [InlineData("sprites/medkit", 1, "sprites/medkit.lod1")]
    public void A_level_is_named_after_its_model(string name, int level, string file) =>
        Assert.Equal(file, LodSettings.FileName(name, level));

    [Fact]
    public void A_level_draws_with_the_main_surfaces_matched_by_material_name()
    {
        var main = new[] { Material("metal"), Material("glass"), Material("paint") };
        var lod = new[] { Material("glass"), Material("metal"), Material("other"), Material("") };

        // By name; else by index while the main model has one; else none.
        Assert.Equal([1, 0, 2, -1], ModelReplacement.MaterialMap(lod, main));
    }

    private static GltfMaterial Material(string name) => new(name, Vector4.One, null, GltfAlphaMode.Opaque, 0.5f, false);

    [Fact]
    public void Animated_levels_ride_the_main_joints_by_name()
    {
        var main = Rig(["root", "a", "b"], [1, 2], skinJoint: 0, rigidNode: 1);
        // The level lists its nodes in another order: its joint 0 is "b" (main skin joint 1), its rigid part rides "b".
        var lod = Rig(["root", "b", "a"], [1, 2], skinJoint: 0, rigidNode: 1);

        var lods = AnimatedLods.Merge(main, [lod], LodSettings.Default, "test");

        Assert.Equal(2, lods.Levels);
        Assert.Equal([0, 1], lods.Parts(0).ToArray());
        Assert.Equal([2, 3], lods.Parts(1).ToArray());
        Assert.Equal(1, lods.Model.Parts[2].Joints![0]);
        Assert.Equal(2, lods.Model.Parts[3].Node);
        Assert.Equal(main.Nodes.Count, lods.Model.Nodes.Count);
    }

    [Fact]
    public void An_animated_level_of_another_skeleton_is_not_drawn()
    {
        var main = Rig(["root", "a", "b"], [1, 2], skinJoint: 0, rigidNode: 1);
        var other = Rig(["root", "x", "y"], [1, 2], skinJoint: 0, rigidNode: 1);

        var lods = AnimatedLods.Merge(main, [other], LodSettings.Default, "test");

        Assert.Equal(1, lods.Levels);
        Assert.Same(main, lods.Model);
    }

    /// <summary>A model of <paramref name="nodes"/> (the first the root, the rest its children): a
    /// skin of <paramref name="skin"/>, a skinned triangle on skin joint <paramref name="skinJoint"/>
    /// and a rigid one on <paramref name="rigidNode"/>.</summary>
    private static AnimatedModel Rig(string[] nodes, int[] skin, int skinJoint, int rigidNode)
    {
        var b = new AnimatedModelBuilder();
        for (var i = 0; i < nodes.Length; i++)
        {
            b.AddNode(nodes[i], i == 0 ? -1 : 0, Trs.Identity);
        }

        b.AddMaterial(Material("m"));
        b.AddSkin(new AnimatedSkin(skin, [.. skin.Select(_ => Matrix4x4.Identity)]));
        var shape = new AnimatedShape([Vector3.Zero, Vector3.UnitX, Vector3.UnitY], [Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ]);
        var joints = Enumerable.Repeat((ushort)skinJoint, 12).ToArray();
        var weights = Enumerable.Repeat(new Vector4(1, 0, 0, 0), 3).ToArray();
        b.AddPart(new AnimatedPart("skinned", 0, [0, 1, 2], null, [shape]) { Skin = 0, Joints = joints, Weights = weights });
        b.AddPart(new AnimatedPart("rigid", 0, [0, 1, 2], null, [shape]) { Node = rigidNode });
        return b.Build();
    }
}

/// <summary>A model's levels of detail read from a mounted mod.</summary>
[Collection(ModsCollection.Name)]
public class ModelLodLoadTests
{
    private static readonly GltfPrimitive Triangle =
        new([new(-0.5f, 0, -0.5f), new(0.5f, 0, -0.5f), new(0.5f, 2, 0.5f)], null, null, [0, 1, 2], -1);

    [Theory]
    [InlineData(new[] { "PROP.lod1", "PROP.lod2" }, 2)]
    [InlineData(new[] { "PROP.lod2" }, 0)]
    [InlineData(new string[0], 0)]
    public void Levels_are_read_up_to_the_first_missing(string[] files, int levels)
    {
        var root = Directory.CreateTempSubdirectory("skynet-lods-").FullName;
        try
        {
            var models = Path.Combine(root, "m", "models");
            Directory.CreateDirectory(models);
            foreach (var name in files.Append("PROP"))
            {
                File.WriteAllBytes(Path.Combine(models, name + ".glb"), Gltf.WriteGlb([Triangle], [], []));
            }

            ModContent.Mount(root, ["m"]);

            var prop = ModContent.Model("PROP")!;
            Assert.Equal(levels, prop.Lods.Count);
            Assert.All(prop.Lods, l => Assert.Same(prop.Surfaces, l.Surfaces));
        }
        finally
        {
            ModContent.Unmount();
            Directory.Delete(root, recursive: true);
        }
    }
}
