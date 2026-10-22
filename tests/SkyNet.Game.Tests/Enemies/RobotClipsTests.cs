using SkyNet.Game.Enemies;

namespace SkyNet.Game.Tests.Enemies;

/// <summary>A whole-robot model's clip for an AIS block (docs/modding.md, Robot template): exact name
/// first, then loosely (case, exporter prefix, separators), then a numbered block's role.</summary>
public sealed class RobotClipsTests
{
    [Fact]
    public void An_exact_name_wins()
    {
        Assert.Equal(1, RobotClips.Match("fire 2", ["fire", "fire 2", "Fire_2"]));
    }

    [Theory]
    [InlineData("walk", "Walk")]
    [InlineData("walk", "Armature|walk")]
    [InlineData("fire 2", "FIRE_2")]
    [InlineData("fire 2", "fire.2")]
    [InlineData("once 1", "once1")]
    public void A_name_matches_loosely(string anim, string clip)
    {
        Assert.Equal(1, RobotClips.Match(anim, ["idle", clip]));
    }

    [Fact]
    public void A_numbered_block_falls_back_to_its_roles_clip()
    {
        string[] clips = ["walk", "Armature|Fire"];

        Assert.Equal(1, RobotClips.Match("fire 3", clips));
        Assert.Equal(-1, RobotClips.Match("loop", clips));
        Assert.Equal(-1, RobotClips.Match("once 1", clips));
        Assert.Null(RobotClips.Role("walk"));
        Assert.Equal("fire", RobotClips.Role("fire 12"));
    }
}
