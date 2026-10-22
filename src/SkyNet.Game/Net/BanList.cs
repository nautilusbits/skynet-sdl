using System.Net;
using System.Text;
using System.Text.Json;
using RetroEng.Json;

namespace SkyNet.Game.Net;

/// <summary>One banned address: who it was, why, when (UTC, ISO 8601).</summary>
public sealed record BanEntry(string Address, string Name, string Reason, string When);

/// <summary>
/// A dedicated server's bans by address, kept in a JSON file (<c>ban_file</c>) so they outlast the
/// server: <c>{ "bans": [ { "address": "203.0.113.7", "name": "...", "reason": "...", "when": "..." } ] }</c>.
/// No file: the bans last until the server stops.
/// </summary>
public sealed class BanList
{
    private readonly List<BanEntry> _bans = [];

    public BanList(string? path = null) => Path = string.IsNullOrEmpty(path) ? null : path;

    public string? Path { get; }

    public IReadOnlyList<BanEntry> Entries => _bans;

    /// <summary>Reads the file (none yet: an empty list); a broken file throws <see cref="JsonDataException"/>.</summary>
    public static BanList Load(string? path)
    {
        var list = new BanList(path);
        if (list.Path == null || !File.Exists(list.Path))
        {
            return list;
        }

        var root = JsonItem.Parse(File.ReadAllBytes(list.Path), lenient: true).AsObject();
        foreach (var e in root.Member("bans").AsArray().Items)
        {
            var address = e.Member("address").AsString().Trim();
            if (!IPAddress.TryParse(address, out _))
            {
                throw e.Member("address").Error($"expected an IP address, found \"{address}\"");
            }

            list._bans.Add(new BanEntry(address, e.Opt("name")?.AsString() ?? "", e.Opt("reason")?.AsString() ?? "", e.Opt("when")?.AsString() ?? ""));
        }

        return list;
    }

    public bool IsBanned(IPAddress address) => Find(address) != null;

    public BanEntry? Find(IPAddress address)
    {
        var a = Normal(address);
        return _bans.FirstOrDefault(b => IPAddress.TryParse(b.Address, out var x) && Normal(x).Equals(a));
    }

    /// <summary>Bans <paramref name="address"/> (once) and saves; the entry.</summary>
    public BanEntry Add(IPAddress address, string name, string reason)
    {
        if (Find(address) is { } known)
        {
            return known;
        }

        var e = new BanEntry(Normal(address).ToString(), name, reason, DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture));
        _bans.Add(e);
        Save();
        return e;
    }

    /// <summary>Lifts the ban on <paramref name="address"/>; false when there was none.</summary>
    public bool Remove(IPAddress address)
    {
        var a = Normal(address);
        if (_bans.RemoveAll(b => IPAddress.TryParse(b.Address, out var x) && Normal(x).Equals(a)) == 0)
        {
            return false;
        }

        Save();
        return true;
    }

    public void Save()
    {
        if (Path == null)
        {
            return;
        }

        var root = JsonItem.NewObject().Add("bans", JsonItem.NewArray(_bans, b => JsonItem.NewObject()
            .Add("address", b.Address).Add("name", b.Name).Add("reason", b.Reason).Add("when", b.When)));
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            root.WriteTo(w);
        }

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!);
        var tmp = Path + ".tmp";
        File.WriteAllBytes(tmp, [.. stream.ToArray(), .. Encoding.UTF8.GetBytes("\n")]);
        File.Move(tmp, Path, overwrite: true);
    }

    /// <summary>An IPv4 address mapped into IPv6 compares as the IPv4 one.</summary>
    private static IPAddress Normal(IPAddress a) => a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a;
}
