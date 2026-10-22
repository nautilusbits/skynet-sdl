using System.Text;

namespace SkyNet.Formats.Tests;

public class NetLevelListTests
{
    private const string Sample = """
        ;# netlevel.prs
        [level0]
        name            = Highway/City       ; Description
        map             = 605                ; Map number
        maxplayers      = 0
        timesetting	= night
        goal            = kills
        scoring_kill    = 500
        scoring_death   = 400
        scoring_hit     = 100
        jeeps           = 3
        bullets         = 5
        rockets         = 3
        replenish       = yes

        [level1]
        name = Sewer
        map = 609
        maxplayers = 4
        timesetting = morning
        replenish = no
        """;

    [Fact]
    public void Sections_keys_and_comments()
    {
        var levels = NetLevelList.Parse(Encoding.ASCII.GetBytes(Sample.Replace("\n", "\r\n")));
        Assert.Equal(2, levels.Length);
        var a = levels[0];
        Assert.Equal("Highway/City", a.Name);
        Assert.Equal(605, a.Map);
        Assert.Equal("MAP.605", a.MapName);
        Assert.Equal("night", a.TimeSetting);
        Assert.Equal("kills", a.Goal);
        Assert.Equal((500, 400, 100), (a.ScoringKill, a.ScoringDeath, a.ScoringHit));
        Assert.Equal(3, a.Count("jeeps"));
        Assert.Equal(5, a.Count("bullets"));
        Assert.Equal(4, a.Count("lasers")); // a key the section leaves out: the Godot port's 4
        Assert.True(a.Replenish);
        var b = levels[1];
        Assert.Equal(("Sewer", 609, 4, "morning", false), (b.Name, b.Map, b.MaxPlayers, b.TimeSetting, b.Replenish));
    }

    [Fact]
    public void Map_names_and_fallbacks()
    {
        Assert.Equal(605, NetLevelList.ParseMapName("MAP.605"));
        Assert.Equal(605, NetLevelList.ParseMapName("map.605"));
        Assert.Equal(601, NetLevelList.ParseMapName("601"));
        Assert.Null(NetLevelList.ParseMapName("MAP.x"));
        Assert.Null(NetLevelList.ParseMapName("-5"));
        Assert.Equal(10, NetLevelList.BuiltIn().Length);
        Assert.Equal("MAP.777", NetLevelList.For([], 777).Name);
    }

    [DataFact]
    public void The_shipped_file_lists_the_ten_arenas()
    {
        var levels = NetLevelList.Load(TestData.Open());
        Assert.Equal(10, levels.Length);
        Assert.Equal(Enumerable.Range(600, 10), levels.Select(l => l.Map).Order());
        Assert.All(levels, l => Assert.Equal("kills", l.Goal));
        Assert.Equal("Highway/City", levels[0].Name);
    }
}
