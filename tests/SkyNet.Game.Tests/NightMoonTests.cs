using System.Numerics;
using RetroEng.Physics;
using SkyNet.Game.Weapons;

namespace SkyNet.Game.Tests;

/// <summary>The moon's joke (SKYNET.EXE v1.01 FUN_001261b1, FUN_0013a210): "OW!" within 15 px,
/// the fall from the 25th hit, the moon left below the horizon.</summary>
public class NightMoonTests
{
    private const float Dt = 1f / 60f;

    private static NightMoon Shown()
    {
        var moon = new NightMoon();
        moon.Reset(MoonSky.Shown);
        return moon;
    }

    /// <summary>The view's yaw and pitch towards <paramref name="d"/>.</summary>
    private static (float Yaw, float Pitch) Look(Vector3 d) => (MathF.Atan2(-d.X, -d.Z), MathF.Asin(d.Y));

    /// <summary>The view turned <paramref name="pixels"/> DOS pixels right of the moon.</summary>
    private static (Vector3 F, Vector3 R, Vector3 U) View(NightMoon moon, float pixels = 0f)
    {
        var (yaw, pitch) = Look(moon.Direction);
        var (f, r, u) = WeaponSystem.Basis(yaw, pitch);
        var turned = Vector3.Normalize(f + r * (pixels / NightMoon.FocalX));
        return (turned, Vector3.Normalize(Vector3.Cross(turned, u)), u);
    }

    [Fact]
    public void RestsAt29DegreesUp()
    {
        var moon = Shown();
        Assert.Equal(29.2f, MathF.Asin(moon.Direction.Y) * 180f / MathF.PI, 1);
        Assert.Equal(Level.MoonDirection(), moon.Direction);
    }

    [Theory]
    [InlineData(0f, true)]
    [InlineData(14.5f, true)]
    [InlineData(-14.5f, true)]
    [InlineData(15.5f, false)]
    [InlineData(-20f, false)]
    public void HitsWithin15Pixels(float pixels, bool hit)
    {
        var moon = Shown();
        var said = new List<(string, byte)>();
        moon.Said += (t, c) => said.Add((t, c));
        var (f, r, u) = View(moon, pixels);
        Assert.Equal(hit, moon.Shoot(f, r, u));
        Assert.Equal(hit ? 1 : 0, moon.Hits);
        Assert.Equal(hit ? [("OW!", (byte)8)] : [], said);
    }

    [Fact]
    public void NoMoonIndoorsOrAtDusk()
    {
        var moon = new NightMoon();
        moon.Reset(MoonSky.None);
        var (f, r, u) = View(moon);
        Assert.False(moon.Shoot(f, r, u));
        Assert.Equal(0, moon.Hits);
    }

    [Fact]
    public void The25thHitStartsTheFall()
    {
        var moon = Shown();
        for (var i = 0; i < NightMoon.HitsToFall - 1; i++)
        {
            moon.Hit();
        }

        moon.Tick(Dt);
        Assert.False(moon.Falling);
        Assert.Equal(0, moon.Drop);

        moon.Hit();
        moon.Tick(Dt);
        Assert.True(moon.Falling);
        Assert.True(moon.Drop > 0);
    }

    [Fact]
    public void FallsBelowTheHorizonInAbout4Point5Seconds()
    {
        var moon = Shown();
        moon.Fall();
        var t = 0f;
        while (moon.Falling && t < 10f)
        {
            moon.Tick(Dt);
            t += Dt;
        }

        // drop = accel / 4 · t²: 0x18800 / 4 · t² = 0x7d000 → t ≈ 4.52 s.
        Assert.InRange(t, 4.4f, 4.6f);
        Assert.True(moon.Down);
        Assert.Equal(0, moon.Hits);
        Assert.InRange(MathF.Asin(moon.Direction.Y) * 180f / MathF.PI, -30f, -29f);

        // It stays there.
        var drop = moon.Drop;
        moon.Tick(Dt);
        Assert.Equal(drop, moon.Drop);

        // A new map puts it back.
        moon.Reset(MoonSky.Shown);
        Assert.Equal(0, moon.Drop);
    }

    private static IEnumerable<Triangle> Wall(Vector3 at, Vector3 normal)
    {
        var a = Vector3.Normalize(Vector3.Cross(normal, Vector3.UnitY)) * 500f;
        var b = Vector3.Normalize(Vector3.Cross(normal, a)) * 500f;
        yield return new Triangle(at - a - b, at + a - b, at + a + b, 5);
        yield return new Triangle(at - a - b, at + a + b, at - a + b, 5);
    }

    [Fact]
    public void TheTriggerHitsItOnlyWithNothingInTheWay()
    {
        var moon = Shown();
        var (yaw, pitch) = Look(moon.Direction);
        var shooter = new Shooter(Vector3.Zero, yaw, pitch);

        var open = new WeaponSystem(WeaponTable.Shipped!, new CollisionWorld()) { Moon = moon };
        open.Tick(new WeaponInput(Fire: true, Throw: false, Run: false), shooter, Dt);
        Assert.Equal(1, moon.Hits);

        // A wall under the crosshair: DOS's [0x44604] is 2, no hit.
        var walled = new CollisionWorld().Add(new TriangleGrid(Wall(moon.Direction * 400f, -moon.Direction)));
        var blocked = new WeaponSystem(WeaponTable.Shipped!, walled) { Moon = moon };
        blocked.Tick(new WeaponInput(Fire: true, Throw: false, Run: false), shooter, Dt);
        Assert.Equal(1, moon.Hits);

        // The thrown items go through FUN_001263cc, which has no moon test.
        var thrower = new WeaponSystem(WeaponTable.Shipped!, new CollisionWorld()) { Moon = moon };
        thrower.Tick(new WeaponInput(Fire: false, Throw: true, Run: false, SelectThrown: 14), shooter, Dt);
        Assert.Equal(1, moon.Hits);
    }
}
