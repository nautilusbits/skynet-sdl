using System.Numerics;
using RetroEng.Render;
using SkyNet.Game.Enhanced;

namespace SkyNet.Game.Tests.Enhanced;

/// <summary>The map lamps that get the engine's shadowed slots: the ones that matter most to the
/// eye, kept until a rival clearly beats them, a slot left to the soldier's flash while firing.</summary>
public class LampShadowsTests
{
    private const float Range = 600f;
    /// <summary>The engine's slots before 0.59 (the rules below read easiest with two).</summary>
    private const int TwoSlots = 2;

    private static PointLight Lamp(float x, float energy = 1f) => new(new Vector3(x, 0, 0), Vector3.One, Range, energy);

    private static List<PointLight> Lamps(params float[] xs) => [.. xs.Select(x => Lamp(x))];

    private static float[] Shadowed(List<PointLight> lights) => [.. lights.Where(l => l.CastsShadow).Select(l => l.Position.X)];

    private static List<PointLight> Mark(LampShadows shadows, List<PointLight> lamps, Vector3 eye, float seconds = 0f,
        ShadowQuality quality = ShadowQuality.High, int weaponCasters = 0)
    {
        var lights = new List<PointLight>(lamps);
        var weapons = new List<PointLight>();
        for (var i = 0; i < weaponCasters; i++)
        {
            weapons.Add(new PointLight(eye, Vector3.One, Range, 1f) { CastsShadow = true });
        }

        lights.AddRange(weapons);
        shadows.Mark(lights, 0, lamps.Count, eye, seconds, quality);
        return lights;
    }

    [Fact]
    public void The_nearest_lamps_take_the_slots()
    {
        var lit = Mark(new LampShadows(TwoSlots), Lamps(-900, 100, 400, 2000), Vector3.Zero);
        Assert.Equal([100f, 400f], Shadowed(lit[..4]));
    }

    [Fact]
    public void A_brighter_lamp_outranks_a_nearer_one()
    {
        var lamps = new List<PointLight> { Lamp(300), Lamp(350), Lamp(500, 6f) };
        Assert.Equal([300f, 500f], Shadowed(Mark(new LampShadows(TwoSlots), lamps, Vector3.Zero)));
    }

    [Fact]
    public void No_shadows_off()
    {
        Assert.Empty(Shadowed(Mark(new LampShadows(TwoSlots), Lamps(100, 200), Vector3.Zero, quality: ShadowQuality.Off)));
    }

    [Fact]
    public void Low_shadows_the_lamps_too()
    {
        Assert.Equal(2, Shadowed(Mark(new LampShadows(TwoSlots), Lamps(100, 200, 300), Vector3.Zero, quality: ShadowQuality.Low)).Length);
    }

    /// <summary>Walking past two lamps the slot doesn't flip at the midpoint, only once the new one
    /// clearly matters more (each flip redraws a still map).</summary>
    [Fact]
    public void A_held_lamp_stays_until_clearly_beaten()
    {
        // Firing: one slot left to the lamps.
        var shadows = new LampShadows(TwoSlots);
        var lamps = Lamps(0, 1000);
        float[] At(float x) => Shadowed(Mark(shadows, lamps, new Vector3(x, 0, 0), weaponCasters: 1)[..2]);

        Assert.Equal([0f], At(100));
        // Past the midpoint: the far lamp is nearer now, but not by the margin yet.
        Assert.Equal([0f], At(550));
        // Close to the far lamp: it wins.
        Assert.Equal([1000f], At(700));
        Assert.Equal([1000f], At(450));
    }

    [Fact]
    public void A_lamp_switched_off_frees_its_slot()
    {
        var shadows = new LampShadows(TwoSlots);
        Mark(shadows, Lamps(100, 200, 900), Vector3.Zero);
        var lit = Mark(shadows, [Lamp(100), Lamp(200, 0f), Lamp(900)], Vector3.Zero);
        Assert.Equal([100f, 900f], Shadowed(lit[..3]));
    }

    /// <summary>The soldier's flash keeps its slot between shots: one lamp shadows while firing and
    /// for <see cref="LampShadows.WeaponHoldSeconds"/> after.</summary>
    [Fact]
    public void Firing_keeps_a_slot_for_the_flash()
    {
        var shadows = new LampShadows(TwoSlots);
        var lamps = Lamps(100, 200, 300);
        Assert.Single(Shadowed(Mark(shadows, lamps, Vector3.Zero, 1f, weaponCasters: 1)[..3]));
        Assert.Single(Shadowed(Mark(shadows, lamps, Vector3.Zero, 1.1f)[..3]));
        Assert.Equal(2, Shadowed(Mark(shadows, lamps, Vector3.Zero, 1f + LampShadows.WeaponHoldSeconds + 0.1f)[..3]).Length);
    }

    [Fact]
    public void A_flash_and_a_blast_leave_no_lamp_a_slot()
    {
        var lit = Mark(new LampShadows(TwoSlots), Lamps(100, 200), Vector3.Zero, weaponCasters: 2);
        Assert.Empty(Shadowed(lit[..2]));
        Assert.Equal(2, lit.Count(l => l.CastsShadow));
    }

    /// <summary>A new map's clock starts again: the hold of the last one's firing doesn't carry.</summary>
    [Fact]
    public void A_clock_going_back_forgets_the_firing()
    {
        var shadows = new LampShadows(TwoSlots);
        Mark(shadows, Lamps(100, 200), Vector3.Zero, 50f, weaponCasters: 1);
        Assert.Equal(2, Shadowed(Mark(shadows, Lamps(100, 200), Vector3.Zero, 0.5f)).Length);
    }

    /// <summary>The engine shadows up to <see cref="Lighting.MostPointShadows"/> lights: SHADOWS picks
    /// how many (a phone fewer), and the lamps fill what the weapons leave.</summary>
    [Fact]
    public void High_shadows_many_lamps()
    {
        var slots = LampShadows.SlotsFor(ShadowQuality.High);
        var lit = Mark(new LampShadows(), Lamps([.. Enumerable.Range(1, 40).Select(i => i * 50f)]), Vector3.Zero);
        Assert.Equal(slots, Shadowed(lit).Length);
        Assert.Equal(Enumerable.Range(1, slots).Select(i => i * 50f), Shadowed(lit));
    }

    [Fact]
    public void Slots_grow_with_the_quality_and_fit_the_engine()
    {
        Assert.Equal(0, LampShadows.SlotsFor(ShadowQuality.Off));
        Assert.InRange(LampShadows.SlotsFor(ShadowQuality.Low), 2, LampShadows.SlotsFor(ShadowQuality.High) - 1);
        Assert.InRange(LampShadows.SlotsFor(ShadowQuality.High), 3, Lighting.MostPointShadows);
    }

    [Fact]
    public void The_lighting_asks_the_engine_for_the_same_slots()
    {
        var lighting = LookLighting.For(LightPreset.Interior, null, 9000f, 1f, ShadowQuality.Low);
        Assert.Equal(LampShadows.SlotsFor(ShadowQuality.Low), lighting.PointShadowCount);
        Assert.True(lighting.PointShadowFade > 0f);
    }
}
