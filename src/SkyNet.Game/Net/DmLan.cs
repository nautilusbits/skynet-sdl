using System.Net;
using RetroEng.Net;

namespace SkyNet.Game.Net;

/// <summary>A game on the LAN as the join screen lists it: where to connect and what its
/// discovery reply says.</summary>
public sealed record LanGame(IPAddress Address, int Port, string Name, string Map, int Players, int Bots, int Max, bool Password = false)
{
    /// <summary>The address field's text for it (ip:port).</summary>
    public string Endpoint => $"{Address}:{Port}";
}

/// <summary>The join screen's side of the LAN discovery (<see cref="DiscoveryBrowser"/>, the
/// Godot port's net_discovery.gd).</summary>
public static class DmLan
{
    /// <summary>A browser on the deathmatch's discovery port: every broadcast address (the Godot
    /// port's targets) and this machine's loopback, so a game hosted on the same computer shows
    /// too. Null without a socket.</summary>
    public static DiscoveryBrowser? StartBrowser() => StartBrowser(DmProtocol.DiscoveryPort, [.. LanDiscovery.BroadcastAddresses(), IPAddress.Loopback]);

    public static DiscoveryBrowser? StartBrowser(int discoveryPort, IReadOnlyList<IPAddress> targets, IPAddress? bind = null) =>
        DiscoveryBrowser.Start(DmProtocol.DiscoveryMagic, discoveryPort, bind, targets);

    /// <summary>The browser's servers read back, sorted by name then address; a game on this
    /// machine answering on its LAN address and the loopback is listed once, by the LAN address.</summary>
    public static List<LanGame> Games(DiscoveryBrowser browser)
    {
        var list = new List<LanGame>();
        foreach (var s in browser.Servers.Values.OrderBy(s => IPAddress.IsLoopback(s.From.Address) ? 1 : 0).ThenBy(s => s.From.ToString(), StringComparer.Ordinal))
        {
            if (DmProtocol.ReadDiscoveryInfo(s.Info) is not { } info || info.Port is < 1 or > 65535)
            {
                continue;
            }

            var game = new LanGame(s.From.Address, info.Port, info.Name, info.Map, info.Players, info.Bots, info.Max, info.Password);
            if (!IPAddress.IsLoopback(game.Address) || !list.Any(g => g.Name == game.Name && g.Map == game.Map && g.Port == game.Port))
            {
                list.Add(game);
            }
        }

        list.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name) is var c && c != 0 ? c : string.CompareOrdinal(a.Endpoint, b.Endpoint));
        return list;
    }
}
