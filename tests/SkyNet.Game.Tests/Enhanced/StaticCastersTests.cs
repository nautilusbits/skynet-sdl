using RetroEng.Render;

namespace SkyNet.Game.Tests.Enhanced;

/// <summary>The level's draws promised the same every frame (<see cref="Caster.Static"/>): their
/// depth stays in the lamps' still shadow maps; everything that moves or changes is drawn anew.
/// A mover (door, crate, lift) at rest is still: on a map of breakable crates the lamps' maps
/// otherwise redrew ~400 draws a frame with nothing moving.</summary>
public class StaticCastersTests
{
    [Theory]
    [InlineData(LevelDraw.World, 1, Caster.Static)]
    [InlineData(LevelDraw.Ground, 1, Caster.Static)]
    // An animated texture's frame changes the draw.
    [InlineData(LevelDraw.World, 4, Caster.Dynamic)]
    [InlineData(LevelDraw.Mover, 1, Caster.Dynamic)]
    [InlineData(LevelDraw.Sprite, 1, Caster.Dynamic)]
    [InlineData(LevelDraw.Replacement, 1, Caster.Dynamic)]
    [InlineData(LevelDraw.Robot, 1, Caster.Dynamic)]
    public void Only_still_level_geometry_is_static(LevelDraw draw, int frames, Caster caster) =>
        Assert.Equal(caster, Level.CasterOf(draw, frames));

    [Theory]
    [InlineData(LevelDraw.Mover, 1, Caster.Static)]
    [InlineData(LevelDraw.Mover, 4, Caster.Dynamic)]
    [InlineData(LevelDraw.Robot, 1, Caster.Dynamic)]
    [InlineData(LevelDraw.Sprite, 1, Caster.Dynamic)]
    [InlineData(LevelDraw.World, 1, Caster.Static)]
    public void A_mover_at_rest_is_static(LevelDraw draw, int frames, Caster caster) =>
        Assert.Equal(caster, Level.CasterOf(draw, frames, Motion.AtRest));
}
