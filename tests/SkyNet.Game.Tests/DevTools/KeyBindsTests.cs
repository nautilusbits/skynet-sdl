using RetroEng.Commands;
using RetroEng.Platform;
using SkyNet.Game.Campaign;
using SkyNet.Game.DevTools;
using SkyNet.Game.Menu;
using SkyNet.Game.Net;

namespace SkyNet.Game.Tests.DevTools;

/// <summary>The console's bind: keys that run console lines in play, keys put on game actions
/// (+action), and what the settings file keeps of them.</summary>
public class KeyBindsTests
{
    private static (CampaignControls Controls, Input Input, KeyBinds Binds, CommandRegistry Registry, List<string> Ran) Make()
    {
        var controls = CampaignControls.Create();
        var input = new Input(controls.Actions);
        var binds = new KeyBinds(new InputBindings(input));
        var registry = new CommandRegistry();
        var ran = new List<string>();
        registry.Add("noclip", "noclip [on|off]", args =>
        {
            ran.Add("noclip " + args.Rest());
            return "noclip on";
        }, "flies");
        registry.Add("screenshot", "screenshot <file>", args =>
        {
            ran.Add("screenshot " + args.Rest());
            return "";
        }, "a shot");
        KeyBinds.Register(registry, binds);
        return (controls, input, binds, registry, ran);
    }

    [Fact]
    public void NoclipHasNoDefaultKey()
    {
        var c = CampaignControls.Create();
        var input = new Input(c.Actions);
        Assert.Empty(input.Bindings(c.Noclip));
    }

    [Fact]
    public void BindRunsTheLineWhenTheKeyIsPressed()
    {
        var (_, _, binds, registry, ran) = Make();
        registry.Run("bind f noclip");
        Assert.Equal("noclip", binds.Lines["F"]);
        Assert.Equal("noclip", binds.LineOf("F"));

        Assert.Equal(["noclip on"], binds.Run("F", registry));
        Assert.Equal(["noclip "], ran);
        Assert.Null(binds.Run("G", registry));

        // A quoted line keeps its words; key names are SDL's, any case.
        registry.Run("bind F9 \"screenshot shot.bmp\"");
        binds.Run("F9", registry);
        Assert.Equal("screenshot shot.bmp", ran[^1]);
        Assert.Equal("screenshot shot.bmp", binds.Lines["F9"]);
    }

    [Fact]
    public void BindShowsUnbindsAndLists()
    {
        var (c, input, binds, registry, _) = Make();
        registry.Run("bind f noclip");
        Assert.Equal(["F: noclip"], registry.Run("bind f"));
        Assert.Equal(["G is not bound"], registry.Run("bind g"));
        Assert.Equal(["W: +Forward"], registry.Run("bind w"));

        var list = string.Join('\n', registry.Run("bindlist"));
        Assert.Contains("F: noclip", list, StringComparison.Ordinal);
        Assert.Contains("W: +Forward", list, StringComparison.Ordinal);

        registry.Run("unbind f");
        Assert.Empty(binds.Lines);
        registry.Run("unbind w");
        Assert.Equal(["Up"], input.Bindings(c.Forward));

        registry.Run("bind f noclip");
        registry.Run("bind g noclip");
        registry.Run("unbindall");
        Assert.Empty(binds.Lines);
        Assert.Equal(["S", "Down"], input.Bindings(c.Back)); // the game's actions keep their keys
    }

    [Fact]
    public void ACommandBindTakesTheKeyOffTheActions()
    {
        var (c, input, binds, registry, _) = Make();
        registry.Run("bind w noclip");
        Assert.Equal(["Up"], input.Bindings(c.Forward));
        Assert.Equal("noclip", binds.LineOf("W"));
    }

    [Fact]
    public void BindPlusPutsTheKeyOnAGameAction()
    {
        var (c, input, binds, registry, _) = Make();
        registry.Run("bind f noclip");
        registry.Run("bind f +noclip");
        Assert.Equal(["F"], input.Bindings(c.Noclip));
        Assert.Null(binds.LineOf("F"));

        registry.Run("bind q \"+quick save\"");
        Assert.Contains("Q", input.Bindings(c.QuickSave));
        Assert.Equal(["Q: +Quick save"], registry.Run("bind q"));
        Assert.Contains("No action", registry.Run("bind q +bogus")[0], StringComparison.Ordinal);
    }

    [Fact]
    public void BadKeysAndCommandsAreRefused()
    {
        var (_, _, binds, registry, _) = Make();
        Assert.Contains("Unknown key", registry.Run("bind nosuchkey noclip")[0], StringComparison.Ordinal);
        Assert.Contains("Unknown command", registry.Run("bind f nosuchcommand")[0], StringComparison.Ordinal);
        Assert.Contains("stays", registry.Run("bind escape noclip")[0], StringComparison.Ordinal);
        Assert.Empty(binds.Lines);
        // Mouse buttons by name too.
        registry.Run("bind \"left mouse\" noclip");
        Assert.Equal("noclip", binds.LineOf("Left mouse"));
    }

    [Fact]
    public void ChangesAreReported()
    {
        var (_, _, binds, registry, _) = Make();
        var changes = 0;
        binds.Changed += () => changes++;
        registry.Run("bind f noclip");
        registry.Run("bind f");
        registry.Run("unbind f");
        Assert.Equal(2, changes);
    }

    [Fact]
    public void CompletionOffersCommandsAndBoundKeys()
    {
        var (_, _, _, registry, _) = Make();
        Assert.Contains("noclip", registry.Complete("bind f no").Matches);
        registry.Run("bind f9 noclip");
        Assert.Contains("F9", registry.Complete("unbind F").Matches);
    }

    [Fact]
    public void ACheatBindStaysGatedInANetworkGame()
    {
        var (_, _, binds, registry, ran) = Make();
        DmGame.Guard(registry, () => true, () => false);
        registry.Run("bind f noclip");
        var said = binds.Run("F", registry);
        Assert.Contains("not allowed cheats", said![0], StringComparison.Ordinal);
        Assert.Empty(ran);
    }

    [Fact]
    public void RestoreSkipsUnknownKeys()
    {
        var (_, _, binds, _, _) = Make();
        binds.Restore(new Dictionary<string, string> { ["f"] = "noclip", ["nosuchkey"] = "noclip", ["G"] = " " });
        Assert.Equal(["F"], binds.Lines.Keys);
    }
}
