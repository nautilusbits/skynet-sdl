using System.Numerics;
using RetroEng.Content;
using RetroEng.Editor;
using SkyNet.Edit;
using SkyNet.Formats;
using SkyNet.Game.Baking;
using SkyNet.Game.Collision;
using SkyNet.Game.Player;

namespace SkyNet.Edit.Tests;

/// <summary>SkyNetEdit's click picking (plan E3): fixed cameras on MAP.210 and MAP.214 (as the
/// editor's --click runs log them) pick fixed entities. Skipped without the game data (SKYNET_DATA).</summary>
public sealed class EntityPickingTests
{
    private const float FieldOfView = 70f * MathF.PI / 180f;
    private const float Width = 1600f, Height = 900f;

    private static GameData? Data()
    {
        var dir = Environment.GetEnvironmentVariable("SKYNET_DATA");
        return string.IsNullOrWhiteSpace(dir) || !GameDataLocator.HasData(dir) ? null : GameData.Open(dir);
    }

    private static PickRay Ray(Vector3 eye, float yawDeg, float pitchDeg, float x, float y)
    {
        var forward = PlayerController.Direction(yawDeg * MathF.PI / 180f, pitchDeg * MathF.PI / 180f);
        var view = Matrix4x4.CreateLookAt(eye, eye + forward, Vector3.UnitY);
        return PickRay.FromView(view, FieldOfView, Width / Height, PickRay.Ndc(x, y, Width, Height));
    }

    [Theory]
    // MAP.210's start camera: the rocks left of the canyon floor (a sprite); the floor itself is ground.
    [InlineData(210, 58159f, 1414f, -53896f, 86.367f, -14.324f, 500f, 550f, 36983)]
    [InlineData(210, 58159f, 1414f, -53896f, 86.367f, -14.324f, 800f, 450f, -1)]
    // MAP.214 above its first corridor: the corridor piece in front (a mesh, by its triangles) ...
    [InlineData(214, 6144f, 194f, -6392f, -180f, -14.324f, 830f, 600f, 10179)]
    // ... and the black void beside it: nothing.
    [InlineData(214, 6144f, 194f, -6392f, -180f, -14.324f, 450f, 300f, -1)]
    public void AFixedCameraPicksAFixedEntity(int number, float ex, float ey, float ez, float yaw, float pitch, float x, float y, int expected)
    {
        if (Data() is not { } data || data.LoadMap(number) is not { } map || MapBaker.Bake(data, number) is not { } block)
        {
            return;
        }

        var picking = new EntityPicking(map, block);
        var wld = map.IsOutdoor ? data.LoadWld(number) : null;
        var picked = picking.Pick(Ray(new Vector3(ex, ey, ez), yaw, pitch, x, y), wld != null ? LevelCollision.Ground(wld) : null);
        Assert.Equal(expected < 0 ? null : expected, picked);
    }

    [Fact]
    public void EveryEntityOfAMapHasAPickTarget()
    {
        if (Data() is not { } data || data.LoadMap(214) is not { } map || MapBaker.Bake(data, 214) is not { } block)
        {
            return;
        }

        var picking = new EntityPicking(map, block);
        foreach (var e in map.Entities)
        {
            Assert.True(picking.TryGet(e.FileOffset, out var t));
            Assert.True(t.Min.X <= t.Max.X && t.Min.Y <= t.Max.Y && t.Min.Z <= t.Max.Z);
        }
    }
}
