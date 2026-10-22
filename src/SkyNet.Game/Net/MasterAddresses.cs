using RetroEng.Net.Master;

namespace SkyNet.Game.Net;

/// <summary>
/// Where the master server's address comes from (docs/multiplayer.md "Master server"). The repo holds
/// no real address: the default is empty. In order: the command line (<c>--master</c>) or server.json's
/// <c>master</c>, the settings file beside the exe (<c>net.master</c>), then <see cref="FileName"/>
/// beside the exe, which a release build may ship (one host[:port] per line; empty lines and comments
/// after '#' or "//" are skipped; the first usable line is the one used).
/// </summary>
public static class MasterAddresses
{
    public const string FileName = "masters.txt";

    /// <summary>Whether <paramref name="address"/> can be used (host[:port], the port 1-65535).</summary>
    public static bool Usable(string? address) =>
        MasterProtocol.TryParseAddress(address, MasterProtocol.DefaultPort, out _, out _);

    /// <summary>The usable lines of <paramref name="folder"/>/masters.txt (none when it is missing or unreadable).</summary>
    public static IReadOnlyList<string> FromFile(string folder)
    {
        var path = Path.Combine(folder, FileName);
        try
        {
            return File.Exists(path) ? Parse(File.ReadAllText(path)) : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public static IReadOnlyList<string> Parse(string text)
    {
        var list = new List<string>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw;
            foreach (var mark in new[] { "#", "//" })
            {
                var at = line.IndexOf(mark, StringComparison.Ordinal);
                if (at >= 0)
                {
                    line = line[..at];
                }
            }

            line = line.Trim();
            if (Usable(line) && !list.Contains(line, StringComparer.OrdinalIgnoreCase))
            {
                list.Add(line);
            }
        }

        return list;
    }

    /// <summary>The first usable of <paramref name="explicitly"/> (command line / server.json),
    /// <paramref name="settings"/> (net.master), then masters.txt in <paramref name="folder"/>; null: none set.</summary>
    public static string? Pick(string? explicitly, string? settings, string folder)
    {
        if (Usable(explicitly))
        {
            return explicitly!.Trim();
        }

        if (Usable(settings))
        {
            return settings!.Trim();
        }

        return FromFile(folder).FirstOrDefault();
    }
}
