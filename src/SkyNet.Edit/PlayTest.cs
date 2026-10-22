using System.Globalization;
using System.Numerics;

namespace SkyNet.Edit;

/// <summary>
/// Play > Play-test in game window (docs/plans/editor-vr-menus.md, section 5): the edited document written
/// to a scratch mod (<see cref="ModName"/> in the editor's mods folder, never the mod a Save goes to, gone
/// when the editor closes) and the game exe started on the edited map over the user's mods and that one.
/// </summary>
public static class PlayTest
{
    /// <summary>The scratch mod's folder name.</summary>
    public const string ModName = "_playtest";

    /// <summary>The game's exe beside the editor's.</summary>
    public static string GameExe => OperatingSystem.IsWindows() ? "skynet-sdl.exe" : "skynet-sdl";

    /// <summary>The scratch mod's folder in <paramref name="modsDir"/>.</summary>
    public static string Folder(string modsDir) => Path.Combine(modsDir, ModName);

    /// <summary>Writes <paramref name="doc"/> to the scratch mod; the paths written, relative to it.</summary>
    public static IReadOnlyList<string> Write(EditDocument doc, string modsDir) => doc.WriteCopy(Folder(modsDir));

    /// <summary>Removes the scratch mod (that one folder only).</summary>
    public static void Delete(string modsDir)
    {
        var folder = Folder(modsDir);
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>The game's command line: the data and mods folders, <paramref name="userMods"/> in their order
    /// with the scratch mod on top, MAP.<paramref name="map"/> without its briefing, the editor's link port;
    /// with <paramref name="eye"/> the soldier's eye there (noclip) looking along <paramref name="yawDegrees"/>.</summary>
    public static List<string> Arguments(string? dataDir, string modsDir, IEnumerable<string> userMods, int map, int linkPort,
        Vector3? eye = null, float? yawDegrees = null)
    {
        var c = CultureInfo.InvariantCulture;
        List<string> mods = [.. userMods.Where(m => !m.Equals(ModName, StringComparison.OrdinalIgnoreCase)), ModName];
        var args = new List<string>();
        if (dataDir != null)
        {
            args.AddRange(["--data", dataDir]);
        }

        args.AddRange(["--mods-dir", modsDir, "--mod", string.Join(',', mods), "--map", map.ToString(c), "--no-briefing",
            "--editor-link", linkPort.ToString(c)]);
        if (eye is { } e)
        {
            args.AddRange(["--pos", string.Create(c, $"{e.X:0.##},{e.Y:0.##},{e.Z:0.##}")]);
        }

        if (yawDegrees is { } y)
        {
            args.AddRange(["--yaw", y.ToString("0.##", c)]);
        }

        return args;
    }
}
