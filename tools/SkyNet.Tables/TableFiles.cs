namespace SkyNet.Tables;

/// <summary>Where the shipped table files and a SKYNET.EXE to check them against are.</summary>
public static class TableFiles
{
    /// <summary>data/tables of the repository holding <paramref name="from"/> (the folder with SkyNet.slnx, searched upward); null outside one.</summary>
    public static string? RepoTablesDir(string from)
    {
        for (var dir = new DirectoryInfo(Path.GetFullPath(from)); dir != null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "SkyNet.slnx")))
            {
                return Path.Combine(dir.FullName, "data", "tables");
            }
        }

        return null;
    }

    /// <summary>The text with LF line ends (a checkout may have turned them into CRLF).</summary>
    public static string Normalise(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);

    /// <summary>
    /// SKYNET.EXE v1.01 in a data folder or its parent (the DOS install), under the name SKYNET.EXE
    /// or GAME.EXE (an unofficial DOSBox patch's name for it), matched by MD5; null when none is.
    /// For the tests that re-extract the tables, never for the game.
    /// </summary>
    public static string? FindExe(string? dataDir)
    {
        if (string.IsNullOrWhiteSpace(dataDir) || !Directory.Exists(dataDir))
        {
            return null;
        }

        var full = Path.GetFullPath(dataDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        foreach (var dir in new[] { full, Path.GetDirectoryName(full) })
        {
            if (dir == null)
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(dir))
            {
                var name = Path.GetFileName(file);
                if ((name.Equals("SKYNET.EXE", StringComparison.OrdinalIgnoreCase) || name.Equals("GAME.EXE", StringComparison.OrdinalIgnoreCase))
                    && new FileInfo(file).Length == Extractor.V101Size && Extractor.Reject(File.ReadAllBytes(file)) == null)
                {
                    return file;
                }
            }
        }

        return null;
    }
}
