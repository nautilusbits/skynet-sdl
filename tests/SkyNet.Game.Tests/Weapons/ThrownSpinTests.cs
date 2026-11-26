using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using RetroEng.Render;
using SkyNet.Data;
using SkyNet.Game.Mods;
using SkyNet.Game.Tests.Behaviour;
using SkyNet.Game.Weapons;

namespace SkyNet.Game.Tests.Weapons;

// Issue #73: a thrown item's sprite cycles its frames in flight and after bounces. DOS's draw
// callback (0x123914) queues it with frame -1, which FUN_00149e00 turns into the free-running
// clock / ms % frames: TEXTURE.217's items have 7-9 frames of 71 ms.
public class ThrownSpinTests
{
    private const int Grenade = 3, Satchel = 20;
    private const int GrenadeFrames = 9, ItemMs = 71, SatchelMs = 214;

    private static AmmoRecord Ammo(int index) => WeaponTable.Shipped.Ammo[index];

    [BehaviourDataFact]
    public void A_thrown_grenade_cycles_its_frames_on_the_clock()
    {
        var view = Bare();

        Assert.Equal(0, view.LobbedFrame(Ammo(Grenade), 0));
        Assert.Equal(1, view.LobbedFrame(Ammo(Grenade), ItemMs));
        Assert.Equal(5, view.LobbedFrame(Ammo(Grenade), 5 * ItemMs + 10));
        Assert.Equal(0, view.LobbedFrame(Ammo(Grenade), GrenadeFrames * ItemMs));
    }

    // The satchel shares the callback: its item sprite (200/004) keeps its own slow cycle; only its
    // enhanced model does not tumble (it is dropped, not thrown).
    [BehaviourDataFact]
    public void A_satchel_keeps_its_item_cycle_and_does_not_tumble()
    {
        var view = Bare();

        Assert.Equal(0, view.LobbedFrame(Ammo(Satchel), SatchelMs - 1));
        Assert.Equal(1, view.LobbedFrame(Ammo(Satchel), SatchelMs));
        Assert.Equal(0f, view.TumbleAngle(Ammo(Satchel), 300));
    }

    // One turn a sprite cycle: the model turns as fast as the sprite's frames go round.
    [BehaviourDataFact]
    public void A_thrown_grenade_model_turns_once_a_sprite_cycle()
    {
        var view = Bare();
        var cycle = GrenadeFrames * ItemMs;

        // 639 ms a cycle: a quarter (159 ms) is about π/2, a cycle and a half is π.
        Assert.Equal(MathF.PI / 2f, view.TumbleAngle(Ammo(Grenade), cycle / 4), 1);
        Assert.Equal(MathF.PI, view.TumbleAngle(Ammo(Grenade), 3 * cycle / 2), 1);
        Assert.Equal(0f, view.TumbleAngle(Ammo(Grenade), cycle), 3);
    }

    [Fact]
    public void A_tumbling_model_rolls_its_top_forward_about_its_centre()
    {
        var model = new SpriteModel
        {
            Sprite = 0,
            Row = new SpriteModelRow("1/1", "a.glb", 0, 0, 0, 0, 0),
            Box = (new Vector3(-0.5f, 0, -0.5f), new Vector3(0.5f, 2, 0.5f)),
        };
        var centre = new Vector3(0, 100, 0);
        var flight = new Vector3(300, 50, 0);

        // Scale 0 fits the 40-unit sprite: the 2-unit model at 20 a unit, its middle on the centre.
        Near(centre, Vector3.Transform(new Vector3(0, 1, 0), model.Tumbled(centre, 40, flight, 0f)));
        Near(centre + new Vector3(0, 20, 0), Vector3.Transform(new Vector3(0, 2, 0), model.Tumbled(centre, 40, flight, 0f)));

        // A quarter turn: the top leads along the flight.
        Near(centre + new Vector3(20, 0, 0), Vector3.Transform(new Vector3(0, 2, 0), model.Tumbled(centre, 40, flight, MathF.PI / 2f)));
    }

    private static void Near(Vector3 expected, Vector3 actual) =>
        Assert.True(Vector3.Distance(expected, actual) < 1e-3f, $"expected {expected}, got {actual}");

    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;

    // Render-free, as WeaponRendererScopeTests builds it: a renderer-less store.
    private static WeaponRenderer Bare()
    {
        var view = (WeaponRenderer)RuntimeHelpers.GetUninitializedObject(typeof(WeaponRenderer));
        Field(view, "_textures").SetValue(view, new TextureStore(null, BehaviourData.Data));
        Field(view, "_sprites").SetValue(view, new Dictionary<(int, int), (GpuTexture, Material)?>());
        return view;
    }

    private static FieldInfo Field(WeaponRenderer view, string name) =>
        typeof(WeaponRenderer).GetField(name, Private) ?? throw new MissingFieldException(nameof(WeaponRenderer), name);
}
