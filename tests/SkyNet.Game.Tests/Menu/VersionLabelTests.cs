using System.Reflection;
using SkyNet.Game.Menu;

namespace SkyNet.Game.Tests.Menu;

public sealed class VersionLabelTests
{
    /// <summary>The title's corner shows the build's version (Directory.Build.props), without the commit.</summary>
    [Fact]
    public void LabelIsTheAssemblyVersion()
    {
        var informational = typeof(MainMenuScreen).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
        var expected = "v" + informational.Split('+')[0];

        Assert.Equal(expected, MainMenuScreen.VersionLabel);
        Assert.DoesNotContain("+", MainMenuScreen.VersionLabel);
        Assert.Matches(@"^v\d+\.\d+\.\d+", MainMenuScreen.VersionLabel);
    }
}
