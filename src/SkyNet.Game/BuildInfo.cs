using System.Reflection;
using RetroEng.Diagnostics;

namespace SkyNet.Game;

/// <summary>The build's facts read from the assemblies at run time: a version without the commit suffix,
/// the UTC build date (Directory.Build.props stamps "BuildDate" into every SkyNET assembly) and the
/// RetroEng engine's version. E.g. "SkyNetEdit 0.35.0 (2026-10-18) · RetroEng 0.27.0".</summary>
public static class BuildInfo
{
    private const string BuildDateKey = "BuildDate";

    /// <summary>The game's version, e.g. "0.35.0".</summary>
    public static string Version { get; } = VersionOf(typeof(BuildInfo).Assembly);

    /// <summary>The game's build date, e.g. "2026-10-18"; "?" when not stamped.</summary>
    public static string BuildDate { get; } = BuildDateOf(typeof(BuildInfo).Assembly);

    /// <summary>The RetroEng engine's version, e.g. "0.27.0".</summary>
    public static string Engine => LogHeader.EngineVersion;

    /// <summary>The dev console's banner: "SkyNET 0.35.0 (2026-10-18) - RetroEng 0.28.0" (ASCII: the
    /// console font has no middle dot).</summary>
    public static string Banner { get; } = Line("SkyNET", typeof(BuildInfo).Assembly).Replace(" · ", " - ");

    /// <summary><paramref name="assembly"/>'s version, the "+abc…" commit suffix cut.</summary>
    public static string VersionOf(Assembly assembly)
    {
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString(3) ?? "?";
        var plus = version.IndexOf('+', StringComparison.Ordinal);
        return plus < 0 ? version : version[..plus];
    }

    /// <summary><paramref name="assembly"/>'s build date (yyyy-MM-dd); "?" when not stamped.</summary>
    public static string BuildDateOf(Assembly assembly) =>
        assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == BuildDateKey)?.Value ?? "?";

    /// <summary>"<paramref name="product"/> 0.35.0 (2026-10-18) · RetroEng 0.27.0" for <paramref name="assembly"/>.</summary>
    public static string Line(string product, Assembly assembly) =>
        $"{product} {VersionOf(assembly)} ({BuildDateOf(assembly)}) · RetroEng {Engine}";
}
