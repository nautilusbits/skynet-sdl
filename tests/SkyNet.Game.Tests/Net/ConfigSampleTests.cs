using SkyNet.Game.Net;
using SkyNet.Master;

namespace SkyNet.Game.Tests.Net;

/// <summary>The tracked samples in config\ (copied beside the builds as *.sample.*): they read as
/// their programs read the real files, hold the defaults, name every key, and hold no real master.</summary>
public class ConfigSampleTests
{
    private static string Sample(string name) => Path.Combine(AppContext.BaseDirectory, "ConfigSamples", name);

    [Fact]
    public void The_server_sample_is_the_defaults_and_names_every_key()
    {
        var path = Sample("server.sample.json");
        var c = ServerConfig.Load(path);
        var d = new ServerConfig();
        Assert.Equal(
            (d.Name, d.Port, d.Bind, d.MaxPlayers, d.MaxPerAddress, d.PasswordKey, d.Cheats, d.Classes, d.Public, d.Master, d.Discovery, d.Motd),
            (c.Name, c.Port, c.Bind, c.MaxPlayers, c.MaxPerAddress, c.PasswordKey, c.Cheats, c.Classes, c.Public, c.Master, c.Discovery, c.Motd));
        Assert.Equal(
            (d.LogFile, d.BanFile, d.RecordDemos, d.DemoFolder, d.IdleKickSeconds, d.TimeoutSeconds, d.IntermissionSeconds, d.Bots, d.BotSkill, d.Goal, d.GoalLimit, d.TimeLimitMinutes),
            (c.LogFile, c.BanFile, c.RecordDemos, c.DemoFolder, c.IdleKickSeconds, c.TimeoutSeconds, c.IntermissionSeconds, c.Bots, c.BotSkill, c.Goal, c.GoalLimit, c.TimeLimitMinutes));
        Assert.Equal(d.Rotation.ToArray(), c.Rotation.ToArray());
        var text = File.ReadAllText(path);
        Assert.All(ServerConfig.Keys.Concat(ServerConfig.EntryKeys), k => Assert.Contains($"\"{k}\"", text));
    }

    [Fact]
    public void The_master_sample_is_the_defaults_and_names_every_key()
    {
        var path = Sample("master.sample.json");
        var (c, log) = MasterSettings.Load(path);
        Assert.Equal(new RetroEng.Net.Master.MasterConfig() with { Games = c.Games }, c);
        Assert.Empty(c.Games);
        Assert.Equal("", log);
        var text = File.ReadAllText(path);
        Assert.All(MasterSettings.Keys, k => Assert.Contains($"\"{k}\"", text));
    }

    [Fact]
    public void The_masters_sample_lists_no_master()
    {
        var text = File.ReadAllText(Sample("masters.sample.txt"));
        Assert.Empty(MasterAddresses.Parse(text));
        Assert.Contains("master.example.org", text);
    }
}
