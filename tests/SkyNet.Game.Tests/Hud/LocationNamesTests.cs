using SkyNet.Game.Campaign;
using SkyNet.Game.Hud;
using SkyNet.Game.Tests.Behaviour;

namespace SkyNet.Game.Tests.Hud;

/// <summary>"YOU ENTERED &lt;location&gt;" (FUN_00136768, table 0x53d00) and the frame's clear
/// index (FUN_00134067).</summary>
public class LocationNamesTests
{
    [Fact]
    public void TheLineIsThePrefixASpaceAndTheFirstMatchingRow()
    {
        var names = new LocationNames("YOU ENTERED", [(11, "FIRST"), (12, "OTHER"), (11, "SECOND")]);
        Assert.Equal("YOU ENTERED FIRST", names.EnteredLine(11));
        Assert.Equal("YOU ENTERED OTHER", names.EnteredLine(12));
        Assert.Null(names.EnteredLine(210));
        Assert.Null(LocationNames.Empty.EnteredLine(11));
    }

    [Fact]
    public void TheTableIsTheExes()
    {
        var names = LocationNames.Load();
        Assert.Equal("YOU ENTERED", names.Prefix);
        Assert.Equal(99, names.Rows.Length);
        Assert.Equal((11, "THE TIKI GRAND HOTEL"), names.Rows[0]);
        Assert.Equal((957, "MDM-TESTMAP 7"), names.Rows[^1]);
        Assert.Equal("TODD'S BROTHER'S HOUSE", names.NameOf(39));
        Assert.Equal("AN APARTMENT BUILDING", names.NameOf(340));
        // No SkyNET campaign map has a name.
        Assert.DoesNotContain(names.Rows, r => r.Map is >= 200 and < 300);
    }

    [BehaviourDataFact]
    public void EnteringANamedMapGivesTheLineAndAnUnnamedOneNone()
    {
        var session = new GameSession(BehaviourData.Data);
        session.StartAt(210);
        Assert.Null(session.EnteredLine);
        Assert.True(session.EnterMap(340, -1));
        Assert.Equal("YOU ENTERED AN APARTMENT BUILDING", session.EnteredLine);
        // Every entry, a return too.
        Assert.True(session.EnterMap(210, -1));
        Assert.Null(session.EnteredLine);
        Assert.True(session.EnterMap(340, -1));
        Assert.Equal("YOU ENTERED AN APARTMENT BUILDING", session.EnteredLine);
    }

    [Fact]
    public void TheLineIsQueuedInWhite()
    {
        var queue = new StatusQueue();
        Assert.True(queue.Push("YOU ENTERED THE TIKI GRAND HOTEL", StatusQueue.WhiteColour));
        Assert.Equal(("YOU ENTERED THE TIKI GRAND HOTEL", (byte)0xB3), queue.Entries.Single());
    }

    [BehaviourDataFact]
    public void NightAndDuskMapsClearToIndex0x11()
    {
        Assert.False(Level.IsDusk(210));
        Assert.True(Level.IsDusk(250));
        Assert.Equal(0x11, Level.ClearIndex(210));
        Assert.Equal(0x11, Level.ClearIndex(250));
        var palette = BehaviourData.Data.LoadPalette()!;
        var (r, g, b) = palette[0x11];
        Assert.Equal(new System.Numerics.Vector4(r / 255f, g / 255f, b / 255f, 1f), Level.ClearColour(palette));
    }
}
