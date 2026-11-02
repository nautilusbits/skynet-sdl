using System.Diagnostics;
using RetroEng.Diagnostics;
using SkyNet.Formats;
using SkyNet.Game.Storage;

// The data version check at the start (issue #20): SkyNET's data and Future Shock's beside it,
// against the known releases. An unknown one is logged file by file (for players to send in) and
// the title shows UNKNOWN GAME DATA (SkyNet.Game.Menu.DataNotice).
internal sealed partial class AppHost
{
    private const string DataLog = "Data";

    /// <summary>The checked games whose data is an unknown release.</summary>
    private static List<DataCheck> CheckData(params (string? Dir, GameKind Game)[] folders)
    {
        var cacheFile = Path.Combine(AppContext.BaseDirectory, "cache", DataVersions.CacheName);
        var unknown = new List<DataCheck>();
        foreach (var (dir, game) in folders)
        {
            if (dir == null)
            {
                continue;
            }

            if (Check(dir, game, cacheFile) is { Known: false } check)
            {
                unknown.Add(check);
            }
        }

        return unknown;
    }

    // One folder checked and logged; null when the check fails (logged, never fatal).
    private static DataCheck? Check(string dir, GameKind game, string cacheFile)
    {
        var clock = Stopwatch.StartNew();
        DataCheck check;
        try
        {
            check = DataVersions.Check(dir, game, cacheFile);
        }
        catch (Exception e)
        {
            // Advisory: never stops the start. The type only (a message may hold local paths).
            Log.Warn(DataLog, $"{game} data version not checked: {e.GetType().Name}");
            return null;
        }

        if (check.Known)
        {
            Log.Info(DataLog, $"{game} data: a known version ({clock.ElapsedMilliseconds} ms)");
            return check;
        }

        Log.Warn(DataLog, $"{game} data: an unknown version ({clock.ElapsedMilliseconds} ms); differs: {string.Join(", ", check.Differs)}");
        foreach (var f in check.Files)
        {
            Log.Warn(DataLog, $"  {f.Name} {f.Size} bytes sha256 {f.Sha256}");
        }

        foreach (var name in check.Skipped)
        {
            Log.Warn(DataLog, $"  {name} not hashed (over {DataVersions.MaxArchiveSize / (1024 * 1024)} MB or past {DataVersions.MaxArchives} archives)");
        }

        return check;
    }
}
