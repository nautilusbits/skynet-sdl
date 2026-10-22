using System.Numerics;
using RetroEng.Loop;
using RetroEng.Physics;
using SkyNet.Formats;
using SkyNet.Game.Behaviour;
using SkyNet.Game.Campaign;
using SkyNet.Game.Player;
using SkyNet.Game.Tests.Behaviour;
using SkyNet.Game.Water;

namespace SkyNet.Game.Tests;

public class WaterTests
{
    private const float Dt = 1f / 60f;
    private static readonly PlayerActions Keys = new(Forward: 0, Back: 1, Left: 2, Right: 3, Run: 4, Jump: 5, Crouch: 6);

    private static TickInput Hold(params int[] actions)
    {
        ulong down = 0;
        foreach (var a in actions) down |= TickInput.Bit(a);
        return new TickInput(down, 0, 0, 0, 0, 0, 0, 0);
    }

    private static PlayerController OnAFloor()
    {
        const float half = 4000f;
        var floor = new[]
        {
            new Triangle(new(-half, 0, -half), new(half, 0, -half), new(half, 0, half)),
            new Triangle(new(-half, 0, -half), new(half, 0, half), new(-half, 0, half)),
        };
        var p = new PlayerController(new CollisionWorld().Add(new TriangleGrid(floor)));
        p.Spawn(new Vector3(0, 1, 0), 0f);
        for (var i = 0; i < 30; i++) p.Step(Hold(), Dt, Keys);
        return p;
    }

    private static void Run(PlayerController p, int ticks, TickInput input)
    {
        for (var i = 0; i < ticks; i++) p.Step(input, Dt, Keys);
    }

    [Fact]
    public void WadingHalvesTheTopSpeeds()
    {
        var p = OnAFloor();
        p.WaterSurface = 40f;                       // knee deep: wading, the head out
        Assert.True(p.Wading);
        Assert.False(p.Submerged);
        Run(p, 120, Hold(Keys.Forward));
        Assert.Equal(-125f, p.Velocity.Z, 2);
        Run(p, 120, Hold(Keys.Forward, Keys.Run));
        Assert.Equal(-200f, p.Velocity.Z, 2);
        Assert.True(p.OnFloor);
    }

    [Fact]
    public void WadingAcceleratesAtAQuarter()
    {
        var p = OnAFloor();
        p.WaterSurface = 40f;
        Run(p, 6, Hold(Keys.Forward));              // 0.1 s at 375 u/s²
        Assert.Equal(-37.5f, p.Velocity.Z, 1);
    }

    [Fact]
    public void ShallowerThanFiveUnitsIsDry()
    {
        var p = OnAFloor();
        p.WaterSurface = 4f;
        Assert.False(p.Wading);
        Run(p, 120, Hold(Keys.Forward));
        Assert.Equal(-250f, p.Velocity.Z, 2);
    }

    [Fact]
    public void SubmergedSinksSlowerAndHopsLower()
    {
        var p = OnAFloor();
        p.WaterSurface = 500f;
        Assert.True(p.Submerged);
        p.Step(Hold(Keys.Jump), Dt, Keys);
        Assert.Equal(100f, p.Velocity.Y, 1);
        var top = 0f;
        for (var i = 0; i < 120; i++)
        {
            p.Step(Hold(), Dt, Keys);
            top = MathF.Max(top, p.Position.Y);
        }

        // v²/2g = 100² / 300 ≈ 33 (DOS's hop under water).
        Assert.InRange(top, 30f, 36f);
        Assert.True(p.OnFloor);
    }

    [Fact]
    public void BreathLastsTwelveSecondsThenDrownsAt85PerSecond()
    {
        var b = new WaterBreath();
        var damage = 0f;
        var firstHurt = -1f;
        var t = 0f;
        Assert.True(b.Tick(Dt, true).WentUnder);
        for (var i = 0; i < 60 * 20; i++)
        {
            t += Dt;
            var f = b.Tick(Dt, true);
            if (f.Damage > 0f && firstHurt < 0f) firstHurt = t;
            damage += f.Damage;
        }

        Assert.InRange(firstHurt, 11.95f, 12.05f);
        Assert.InRange(damage, 85f * (t - firstHurt) - 2f, 85f * (t - firstHurt) + 2f);
        var up = b.Tick(Dt, false);
        Assert.True(up.CameUp);
        Assert.True(up.GetAir);
    }

    [Fact]
    public void AShortDipComesUpWithoutAGasp()
    {
        var b = new WaterBreath();
        for (var i = 0; i < 20; i++) b.Tick(Dt, true);           // a third of a second
        var up = b.Tick(Dt, false);
        Assert.True(up.CameUp);
        Assert.False(up.GetAir);
    }

    [Fact]
    public void TheLevelGlidesToItsTarget()
    {
        var w = new WaterLevel(WaterLevel.MarkerClear, 100f);
        w.Move(170f, absolute: false);
        for (var i = 0; i < 60; i++) w.Tick(Dt);
        Assert.Equal(116f, w.Surface, 2);                          // 16 u/s
        for (var i = 0; i < 60 * 15; i++) w.Tick(Dt);
        Assert.Equal(270f, w.Surface, 2);
        w.Move(-40f, absolute: true);
        Assert.Equal(-40f, w.Target);
        var murky = new WaterLevel(WaterLevel.MarkerMurky, 0f);
        murky.Move(-100f, false);
        for (var i = 0; i < 60; i++) murky.Tick(Dt);
        Assert.Equal(-8f, murky.Surface, 2);                       // 8 u/s
    }

    [BehaviourDataTheory]
    [InlineData(240, 818f)]
    [InlineData(250, 816f)]
    [InlineData(252, -120f)]
    [InlineData(253, 208f)]
    [InlineData(254, 207f)]
    public void TheWaterMapsHaveTheirSurfaceSixteenAboveTheMarker(int map, float surface)
    {
        var file = BehaviourData.Data.LoadMap(map)!;
        var w = WaterLevel.Find(file);
        Assert.NotNull(w);
        Assert.Equal(surface, w.Surface);
    }

    [BehaviourDataTheory]
    [InlineData(210)]
    [InlineData(230)]
    [InlineData(251)]
    public void DryMapsHaveNone(int map) => Assert.Null(WaterLevel.Find(BehaviourData.Data.LoadMap(map)!));

    [BehaviourDataFact]
    public void TheLookFitsHaze005Row8AndSkyntwtr()
    {
        var data = BehaviourData.Data;
        var look = WaterLook.Load(data, data.LoadPalette()!, WaterLevel.Find(data.LoadMap(250)!)!);
        // ≈ 0.9·c + (10, 12, 19): a tenth of the way to (100, 120, 190).
        Assert.InRange(look.Surface.W, 0.07f, 0.13f);
        Assert.True(look.Surface.Z > look.Surface.X, $"{look.Surface}");
        // 0.5·c + (35, 35, 126).
        Assert.InRange(look.Underwater.W, 0.45f, 0.55f);
        Assert.InRange(look.Underwater.Z * 255f, 235f, 255f);
        Assert.InRange(look.Underwater.X * 255f, 60f, 80f);
    }

    /// <summary>The original look takes the row itself: index to index, 87 of 256 kept, the dark
    /// harbour colours stepped bluer (FUN_00120f85 copies HAZE.005 row 8).</summary>
    [BehaviourDataFact]
    public void TheOriginalLookRemapsThroughHaze005Row8Itself()
    {
        var data = BehaviourData.Data;
        var palette = data.LoadPalette()!;
        var look = WaterLook.Load(data, palette, WaterLevel.Find(data.LoadMap(250)!)!);
        var row = Assert.IsType<byte[]>(look.Remap);
        Assert.Equal(HazeTable.Parse(data.ReadLoose("HAZE.005")!)!.Row(8).ToArray(), row);
        Assert.Equal(87, Enumerable.Range(0, 256).Count(i => row[i] == i));
        Assert.Equal(205, row[120]);                 // (47, 47, 52) -> (55, 55, 75)
        Assert.Equal(234, row[58]);                  // (83, 71, 71) -> (79, 71, 91)
        var blueGain = Enumerable.Range(0, 256).Where(i => palette[i].B < 80).Average(i => palette[row[i]].B - palette[i].B);
        Assert.InRange(blueGain, 10.0, 25.0);
    }

    /// <summary>MAP.254's sewer: 254WALL1 (@0x8117) shot down opens 254HOLE1 (@0x6eca, act 0xD8)
    /// and the water drops 140.</summary>
    [BehaviourDataFact]
    public void ShootingTheSewerWallDropsTheWater()
    {
        var session = new GameSession(BehaviourData.Data);
        Assert.True(session.EnterMap(254, 0));
        var water = session.Water!;
        Assert.Equal(207f, water.Target);
        session.Behaviour!.Hit(0x8117, 10000f);
        for (var i = 0; i < 60 * 2; i++) session.Tick(default, Keys, default, false);
        Assert.Equal(207f - 140f, water.Target);
        Assert.True(water.Surface < 207f);
    }

    [BehaviourDataFact]
    public void ASessionDrownsTheSoldierUnderTheSubDeck()
    {
        var session = new GameSession(BehaviourData.Data);
        Assert.True(session.EnterMap(252, -1));
        var water = session.Water!;
        water.Set(5000f, 5000f);                    // flood the deck over his head
        var t = 0f;
        while (session.Soldier.Alive && t < 40f)
        {
            session.Tick(default, Keys, default, false);
            t += GameSession.Dt;
        }

        Assert.False(session.Soldier.Alive);
        Assert.InRange(t, 12f + 1000f / 85f - 0.5f, 12f + 1000f / 85f + 0.5f);
    }
}
