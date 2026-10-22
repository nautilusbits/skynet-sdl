namespace SkyNet.Game.Tests;

/// <summary>Where the repository's own files are, found from the test binaries.</summary>
internal static class RepoPaths
{
    /// <summary>The repository root: the first folder up that has <c>mods-samples</c>.</summary>
    internal static string Root()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            if (Directory.Exists(Path.Combine(d.FullName, "mods-samples")))
            {
                return d.FullName;
            }
        }

        throw new DirectoryNotFoundException("mods-samples");
    }
}
