using SkyNet.Game;

namespace SkyNet.Edit;

/// <summary>The editor's and the engine's versions and the build date, read from the assemblies at run
/// time (<see cref="BuildInfo"/>): the status bar's corner, the splash, Help > About.</summary>
public static class EditorVersions
{
    /// <summary>The editor's version, e.g. "0.35.0".</summary>
    public static string Editor { get; } = BuildInfo.VersionOf(typeof(EditorVersions).Assembly);

    /// <summary>The editor's build date, e.g. "2026-10-18".</summary>
    public static string BuildDate { get; } = BuildInfo.BuildDateOf(typeof(EditorVersions).Assembly);

    /// <summary>The RetroEng engine's version the editor runs on, e.g. "0.27.0".</summary>
    public static string Engine => BuildInfo.Engine;

    /// <summary>"SkyNetEdit 0.35.0 (2026-10-18) · RetroEng 0.27.0".</summary>
    public static string Line { get; } = BuildInfo.Line("SkyNetEdit", typeof(EditorVersions).Assembly);
}
