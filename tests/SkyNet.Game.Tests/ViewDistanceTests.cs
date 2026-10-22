using System.Numerics;
using RetroEng.Render;

namespace SkyNet.Game.Tests;

/// <summary>The frame's depth cue and far clip (<see cref="Level.CueOf"/>): outdoors as before, indoors a
/// clip only when a view distance is set.</summary>
public sealed class ViewDistanceTests
{
    private const int Lit = Formats.HazeTable.Steps - 1;

    [Fact]
    public void OutdoorCueRunsToTheFarClip()
    {
        var colour = new Vector4(0.2f, 0.3f, 0.4f, 1f);
        var fog = Level.CueOf(cued: true, 720f, 1176f, clip: null, colour);

        Assert.NotNull(fog);
        Assert.Equal((720f, 1176f, Lit, 0f), (fog.Start, fog.End, fog.FirstLevel, fog.LastLevel));
        Assert.Equal((FogBeyond.Clip, colour), (fog.Beyond, fog.Colour));
    }

    [Fact]
    public void IndoorsDrawsEverythingWithoutAViewDistance()
    {
        Assert.Null(Level.CueOf(cued: false, 720f, 1176f, clip: null, default));
    }

    [Fact]
    public void IndoorViewDistanceClipsAtAnUnchangedShade()
    {
        var colour = new Vector4(0.1f, 0.1f, 0.2f, 1f);
        var fog = Level.CueOf(cued: false, 720f, 1176f, clip: 3000f, colour);

        Assert.NotNull(fog);
        Assert.Equal(3000f, fog.End);
        Assert.True(fog.Start < fog.End && fog.Start > fog.End - 2f);
        Assert.Equal((Lit, Lit), (fog.FirstLevel, fog.LastLevel));
        Assert.Equal((FogBeyond.Clip, colour), (fog.Beyond, fog.Colour));
    }
}
