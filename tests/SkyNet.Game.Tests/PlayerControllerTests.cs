using System.Numerics;
using RetroEng.Loop;
using RetroEng.Physics;
using SkyNet.Formats;
using SkyNet.Game.Collision;
using SkyNet.Game.Player;

namespace SkyNet.Game.Tests;

public class PlayerControllerTests
{
    private const float Dt = 1f / 60f;
    private static readonly PlayerActions Keys = new(Forward: 0, Back: 1, Left: 2, Right: 3, Run: 4, Jump: 5, Crouch: 6);

    private static TickInput Hold(params int[] actions)
    {
        ulong down = 0;
        foreach (var a in actions) down |= TickInput.Bit(a);
        return new TickInput(down, 0, 0, 0, 0, 0, 0, 0);
    }

    private static IEnumerable<Triangle> Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
    {
        yield return new Triangle(a, b, c);
        yield return new Triangle(a, c, d);
    }

    private static IEnumerable<Triangle> Floor(float y = 0f, float half = 2000f) =>
        Quad(new(-half, y, -half), new(half, y, -half), new(half, y, half), new(-half, y, half));

    /// <summary>A block across the walk (−Z) from z = −100 to −1000, <paramref name="height"/> high.</summary>
    private static IEnumerable<Triangle> Block(float height) =>
        Quad(new(-1000, 0, -100), new(1000, 0, -100), new(1000, height, -100), new(-1000, height, -100))
            .Concat(Quad(new(-1000, height, -100), new(1000, height, -100), new(1000, height, -1000), new(-1000, height, -1000)));

    private static PlayerController Standing(params IEnumerable<Triangle>[] parts)
    {
        var world = new CollisionWorld().Add(new TriangleGrid(parts.SelectMany(p => p)));
        var player = new PlayerController(world);
        player.Spawn(new Vector3(0, 1, 0), 0f);
        Run(player, 30, Hold());
        Assert.True(player.OnFloor);
        return player;
    }

    private static void Run(PlayerController p, int ticks, TickInput input)
    {
        for (var i = 0; i < ticks; i++) p.Step(input, Dt, Keys);
    }

    [Fact]
    public void WalksAtTheDosSpeedOnAFlatFloor()
    {
        var p = Standing(Floor());
        Run(p, 120, Hold(Keys.Forward));
        Assert.Equal(-250f, p.Velocity.Z, 2);
        Assert.Equal(0f, p.Position.Y, 0);
        Run(p, 60, Hold(Keys.Forward, Keys.Run));
        Assert.Equal(-400f, p.Velocity.Z, 2);
        Assert.True(p.OnFloor);
    }

    [Fact]
    public void WalkingIntoAWallAtAnAngleSlidesAlongIt()
    {
        // A wall in the plane x = 100, facing −X.
        var wall = Quad(new(100, -10, 1000), new(100, -10, -1000), new(100, 300, -1000), new(100, 300, 1000));
        var p = Standing(Floor(), wall);
        Run(p, 120, Hold(Keys.Forward, Keys.Right));
        Assert.InRange(p.Position.X, 84.5f, 85f);
        Assert.True(p.Position.Z < -300f, $"z {p.Position.Z}");
        Assert.True(p.OnFloor);
    }

    [Fact]
    public void AStepOf40Climbs()
    {
        var p = Standing(Floor(), Block(40));
        Run(p, 120, Hold(Keys.Forward));
        Assert.Equal(40f, p.Position.Y, 0);
        Assert.True(p.Position.Z < -200f, $"z {p.Position.Z}");
    }

    [Fact]
    public void AStepOf100Blocks()
    {
        var p = Standing(Floor(), Block(100));
        Run(p, 120, Hold(Keys.Forward));
        Assert.Equal(0f, p.Position.Y, 0);
        Assert.InRange(p.Position.Z, -100f + 15f, -100f + 15.5f);
    }

    [Fact]
    public void GravityPullsAt392()
    {
        var world = new CollisionWorld().Add(new TriangleGrid(Floor(-5000)));
        var p = new PlayerController(world);
        p.Spawn(new Vector3(0, 1000, 0), 0f);
        Run(p, 60, Hold());
        Assert.Equal(-PlayerController.Gravity, p.Velocity.Y, 2);
        // Semi-implicit Euler over 60 ticks: g·dt²·(1 + … + 60).
        Assert.Equal(1000f - PlayerController.Gravity * Dt * Dt * 60 * 61 / 2, p.Position.Y, 1);
    }

    [Fact]
    public void JumpApexAndAirTimeMatchTheConstants()
    {
        var p = Standing(Floor());
        var apex = 0f;
        var ticks = 0;
        p.Step(Hold(Keys.Jump), Dt, Keys);
        Assert.Equal(PlayerController.JumpSpeed, p.Velocity.Y, 3);
        do
        {
            apex = MathF.Max(apex, p.Position.Y);
            p.Step(Hold(), Dt, Keys);
            ticks++;
        }
        while (!p.OnFloor && ticks < 600);

        // v²/2g = 40 units, plus half a tick's rise from the stepping.
        var ideal = PlayerController.JumpSpeed * PlayerController.JumpSpeed / (2 * PlayerController.Gravity);
        Assert.InRange(apex, ideal, ideal + PlayerController.JumpSpeed * Dt);
        // 2v/g ≈ 0.9 s.
        Assert.InRange(ticks * Dt, 2 * PlayerController.JumpSpeed / PlayerController.Gravity - 3 * Dt,
            2 * PlayerController.JumpSpeed / PlayerController.Gravity + 3 * Dt);
        Assert.Equal(0f, p.FallDamage);
    }

    [Fact]
    public void CrouchedSoldierDoesNotJump()
    {
        var p = Standing(Floor());
        Run(p, 60, Hold(Keys.Crouch));
        Assert.Equal(PlayerController.CrouchEye, p.Eye);
        Run(p, 10, Hold(Keys.Crouch, Keys.Jump));
        Assert.Equal(0f, p.Position.Y, 0);
    }

    [Fact]
    public void ASteepSlopeIsAWall()
    {
        // 60° ramp rising towards −Z from z = −100: steeper than the 40° limit.
        var rise = MathF.Tan(60f * MathF.PI / 180f) * 400;
        var ramp = Quad(new(-1000, 0, -100), new(1000, 0, -100), new(1000, rise, -500), new(-1000, rise, -500));
        var p = Standing(Floor(), ramp);
        Run(p, 180, Hold(Keys.Forward));
        Assert.True(p.Position.Y < 30f, $"y {p.Position.Y}");
    }

    [Fact]
    public void AGentleSlopeIsWalkedUp()
    {
        var rise = MathF.Tan(25f * MathF.PI / 180f) * 900;
        var ramp = Quad(new(-1000, 0, -100), new(1000, 0, -100), new(1000, rise, -1000), new(-1000, rise, -1000));
        var p = Standing(Floor(), ramp);
        Run(p, 180, Hold(Keys.Forward));
        Assert.True(p.Position.Y > 100f, $"y {p.Position.Y}");
        Assert.True(p.OnFloor);
    }

    [Fact]
    public void FallingFarHurts()
    {
        var world = new CollisionWorld().Add(new TriangleGrid(Floor()));
        var p = new PlayerController(world);
        p.Spawn(new Vector3(0, 400, 0), 0f);
        Run(p, 180, Hold());
        Assert.True(p.OnFloor);
        // ~400 units: v ≈ √(2·392·400) ≈ 560 u/s → (560 − 396)·1113/256 ≈ 700 points.
        Assert.InRange(p.FallDamage, 600f, 800f);
    }

    /// <summary>A floor to z = −100, then a ramp rising towards −Z at <paramref name="degrees"/> to z = −3000.</summary>
    private static IEnumerable<Triangle> Ramp(float degrees)
    {
        var rise = MathF.Tan(degrees * MathF.PI / 180f) * 2900f;
        return Quad(new(-2000, 0, 2000), new(2000, 0, 2000), new(2000, 0, -100), new(-2000, 0, -100))
            .Concat(Quad(new(-2000, 0, -100), new(2000, 0, -100), new(2000, rise, -3000), new(-2000, rise, -3000)));
    }

    /// <summary>The same slope as a heightfield (the WLD ground): 256-unit cells, flat for z ≥ −100.</summary>
    private static HeightField Hill(float degrees)
    {
        const int n = 40;
        var tan = MathF.Tan(degrees * MathF.PI / 180f);
        var h = new float[n * n];
        for (var r = 0; r < n; r++)
        {
            for (var c = 0; c < n; c++)
            {
                h[r * n + c] = MathF.Max(0f, tan * (-(-8000f + r * 256f) - 100f));
            }
        }

        return new HeightField(h, n, n, 256f, new Vector2(-5000f, -8000f));
    }

    /// <summary>Walks forward from z = 300 onto the slope; the horizontal and vertical speed over the
    /// second second (well onto it).</summary>
    private static (float Across, float Up, PlayerController P) Climb(CollisionWorld world, bool run = false)
    {
        var p = new PlayerController(world);
        p.Spawn(new Vector3(0, 1, 300), 0f);
        Run(p, 30, Hold());
        var keys = run ? Hold(Keys.Forward, Keys.Run) : Hold(Keys.Forward);
        Run(p, 120, keys);
        var a = p.Position;
        Run(p, 60, keys);
        var b = p.Position;
        return (new Vector2(b.X - a.X, b.Z - a.Z).Length(), b.Y - a.Y, p);
    }

    [Theory]
    [InlineData(25f)]
    [InlineData(38f)]
    public void AMeshRampIsClimbedAtTheWalkSpeed(float degrees)
    {
        // DOS FUN_0012b18c moves the feet across at the full speed and re-finds the floor under them.
        var (across, up, p) = Climb(new CollisionWorld().Add(new TriangleGrid(Ramp(degrees))));
        Assert.InRange(across, 245f, 251f);
        Assert.InRange(up, across * MathF.Tan(degrees * MathF.PI / 180f) * 0.95f, across * MathF.Tan(degrees * MathF.PI / 180f) * 1.05f);
        Assert.True(p.OnFloor);
        var (runAcross, _, _) = Climb(new CollisionWorld().Add(new TriangleGrid(Ramp(degrees))), run: true);
        Assert.InRange(runAcross, 390f, 401f);
    }

    [Fact]
    public void ATerrainHillIsClimbedAtTheWalkSpeedToo()
    {
        // DOS's floor probe 0x138d00 hands the WLD ground over as one more polygon: no gradient
        // slowdown under the 40° limit, as on the meshes.
        var (across, _, p) = Climb(new CollisionWorld().Add(Hill(25f)));
        Assert.InRange(across, 245f, 251f);
        Assert.True(p.OnFloor);
    }

    [Theory]
    [InlineData(45f)]
    [InlineData(60f)]
    public void A45DegreeHillOrRampIsAWall(float degrees)
    {
        // Normal Y under 196/256 (0x12b192): a face rising ahead is a wall.
        var (_, _, ramp) = Climb(new CollisionWorld().Add(new TriangleGrid(Ramp(degrees))));
        Assert.True(ramp.Position.Y < 30f, $"ramp y {ramp.Position.Y}");
        var (_, _, hill) = Climb(new CollisionWorld().Add(Hill(degrees)));
        Assert.True(hill.Position.Y < 120f, $"hill y {hill.Position.Y}");
    }

    [Fact]
    public void MAP210TowerRampsAreWalkedAtNearlyTheWalkSpeed()
    {
        // 210TOWER at (47536, 914, −58096): a 64-wide ramp of three flights, 23.6°, 38.0° and
        // 36.9°, from the ground at 720 to the platform at 978. Before the fix the two steep
        // flights took about 65 u/s.
        var dir = Environment.GetEnvironmentVariable("SKYNET_DATA");
        if (string.IsNullOrWhiteSpace(dir) || !GameDataLocator.HasData(dir) || GameData.Open(dir) is not { } data
            || LevelCollision.Load(data, 210) is not { Wld: { } wld } level)
        {
            return;
        }

        var p = new PlayerController(level.World);
        p.Spawn(new Vector3(47445f, (float)wld.HeightAt(47445f, 57650f) + 5f, -57650f), 0f);
        Run(p, 30, Hold());
        Vector2[] flights = [new(47445, -57930), new(47700, -57930), new(47700, -58150)];
        foreach (var to in flights)
        {
            var from = p.Position;
            var ticks = 0;
            while (ticks < 600 && new Vector2(to.X - p.Position.X, to.Y - p.Position.Z).Length() >= 12f)
            {
                var d = new Vector2(to.X - p.Position.X, to.Y - p.Position.Z);
                p.Yaw = MathF.Atan2(-d.X, -d.Y);
                p.Step(Hold(Keys.Forward), Dt, Keys);
                ticks++;
            }

            var across = new Vector2(p.Position.X - from.X, p.Position.Z - from.Z).Length() / (ticks * Dt);
            Assert.True(across > 200f, $"towards {to}: {across:0} u/s");
        }

        Assert.InRange(p.Position.Y, 970f, 985f);
        Assert.True(p.OnFloor);
    }
}
