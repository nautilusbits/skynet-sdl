using System.Runtime.InteropServices;

namespace SkyNet.Edit;

/// <summary>Help > About: whether it is open, and its text (the versions, the runtime, the GPU) as
/// "Copy version info" puts it on the clipboard for a bug report.</summary>
public sealed class AboutBox
{
    public const string Title = "SkyNET Mission Editor";
    public const string Description = "Edits SkyNET and Future Shock missions as the game loads them, ORIGINAL or ENHANCED.";
    public const string Notice = "Unofficial fan port. Not affiliated with Bethesda. Requires the original game data.";

    public bool IsOpen { get; private set; }

    public void Show() => IsOpen = true;

    public void Close() => IsOpen = false;

    /// <summary>The ImGui window's p_open: the X closes it too.</summary>
    public void Keep(bool open) => IsOpen = open;

    /// <summary>One line per fact: editor, engine, .NET, OS, graphics.</summary>
    public static string VersionInfo(string gpu, string backend) => string.Join('\n',
        $"SkyNetEdit {EditorVersions.Editor} ({EditorVersions.BuildDate})",
        $"RetroEng {EditorVersions.Engine}",
        RuntimeInformation.FrameworkDescription,
        $"{RuntimeInformation.OSDescription} ({RuntimeInformation.ProcessArchitecture})",
        $"{gpu} on {backend}");
}
