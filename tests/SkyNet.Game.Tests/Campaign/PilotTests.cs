using SkyNet.Game.Campaign;
using Xunit;

namespace SkyNet.Game.Tests.Campaign;

public class PilotTests
{
    private static readonly Pilot Test = new("Testpilot", "Ace");

    [Fact]
    public void HashNIsTheName()
    {
        Assert.Equal("Testpilot HERE ARE YOUR STATS:", Test.Expand("#N HERE ARE YOUR STATS:"));
        Assert.Equal("HERE ARE YOUR STATS:", MissionStats.Header(new StatSheet(new StatCounters(1, 1, 1, 1), default), null, Pilot.None));
        Assert.Equal("Testpilot HERE ARE YOUR STATS:", MissionStats.Header(new StatSheet(new StatCounters(1, 1, 1, 1), default), null, Test));
    }

    [Fact]
    public void EveryCodeOfFun0012daa8Expands()
    {
        // #N name, #C call sign, #P / #K the multiplayer subject and killer; the code letter is
        // upper-cased first; any other '#' stays.
        Assert.Equal("Testpilot, call sign Ace.", Test.Expand("#N, call sign #C."));
        Assert.Equal("Testpilot Ace", Test.Expand("#n #c"));
        Assert.Equal("ROBOT TERMINATED by GUNNER", Test.Expand("#P TERMINATED by #K", "ROBOT", "GUNNER"));
        Assert.Equal("#F 100% #", Test.Expand("#F 100% #"));
        Assert.Equal("You are Testpilot.", Test.Expand(SkyNet.Game.DevTools.SessionCommands.WhoAmI));
        Assert.Equal("plain", Test.Expand("plain"));
    }

    [Fact]
    public void TheDefaultsAndTheStoredCaseAreDos()
    {
        Assert.Equal(new Pilot("Noname", "Ice"), Pilot.Default);
        Assert.Equal("Testpilot", Pilot.Capitalise("TESTPILOT"));
        Assert.Equal(new Pilot("Testpilot", "Ice"), Pilot.FromFields("testpilot", "", Pilot.Default));
    }

    [Fact]
    public void TheFieldsTakeUpperCaseAllowedCharactersUpToTheirLength()
    {
        Assert.Equal("TESTPILOT", Pilot.Clean("test pilot", Pilot.NameLength));
        Assert.Equal(17, Pilot.NameLength);
        Assert.Equal(17, Pilot.CallSignLength);
        Assert.Equal(Pilot.NameLength, Pilot.Clean(new string('X', 40), Pilot.NameLength).Length);
        Assert.Equal(Pilot.CallSignLength, Pilot.Clean(new string('X', 40), Pilot.CallSignLength).Length);
        Assert.Equal("", Pilot.Clean(null, Pilot.NameLength));
        Assert.Equal("ABC", Pilot.Clean("a1b.c-_ 9", Pilot.NameLength));
        Assert.Equal("", Pilot.Clean("ÄÖ#", Pilot.NameLength));
    }
}
