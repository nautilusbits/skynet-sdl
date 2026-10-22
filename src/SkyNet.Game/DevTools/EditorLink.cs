using System.Net;
using System.Net.Sockets;
using System.Text;

namespace SkyNet.Game.DevTools;

/// <summary>
/// The play-test channel between SkyNetEdit and a game it started (docs/plans/editor-vr-menus.md, section 5):
/// one-line ASCII datagrams on 127.0.0.1. The editor listens (<see cref="EditorLinkHost"/>) and passes its
/// port as <c>--editor-link PORT</c>; the game (<see cref="EditorLinkClient"/>) says <see cref="Hello"/>, the
/// editor sends <see cref="Reload"/> after writing the layers again, the game answers <see cref="Reloaded"/>
/// (or <see cref="Failed"/>) with the map.
/// </summary>
public static class EditorLink
{
    public const string Hello = "hello", Reload = "reload", Reloaded = "reloaded", Failed = "failed";

    internal static byte[] Bytes(string message) => Encoding.ASCII.GetBytes(message);

    // A loopback socket on a free port; on Windows a send to a closed port no longer breaks the next receive.
    internal static UdpClient Open()
    {
        var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        if (OperatingSystem.IsWindows())
        {
            const int SioUdpConnReset = -1744830452;
            udp.Client.IOControl(SioUdpConnReset, [0, 0, 0, 0], null);
        }

        return udp;
    }

    // The datagrams waiting on the socket, without blocking.
    internal static List<(string Text, IPEndPoint From)> Drain(UdpClient udp)
    {
        var got = new List<(string, IPEndPoint)>();
        try
        {
            while (udp.Available > 0)
            {
                IPEndPoint? from = null;
                var bytes = udp.Receive(ref from);
                got.Add((Encoding.ASCII.GetString(bytes).Trim(), from));
            }
        }
        catch (SocketException)
        {
            // A port that went away (Windows reports the last send's ICMP here): nothing more to read.
        }

        return got;
    }
}

/// <summary>The editor's end: a socket on a free loopback port; the game's endpoint learned from its hello.</summary>
public sealed class EditorLinkHost : IDisposable
{
    private readonly UdpClient _udp = EditorLink.Open();
    private IPEndPoint? _game;

    /// <summary>The port the game is given (<c>--editor-link</c>).</summary>
    public int Port => ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;

    /// <summary>A game said hello (the reloads can reach it).</summary>
    public bool Connected => _game != null;

    /// <summary>Asks the game to reload its level; false when no game has said hello yet.</summary>
    public bool SendReload()
    {
        if (_game == null)
        {
            return false;
        }

        var b = EditorLink.Bytes(EditorLink.Reload);
        _udp.Send(b, b.Length, _game);
        return true;
    }

    /// <summary>The game's lines since the last call (its hello recorded, not returned).</summary>
    public List<string> Poll()
    {
        var lines = new List<string>();
        foreach (var (text, from) in EditorLink.Drain(_udp))
        {
            if (text == EditorLink.Hello)
            {
                _game = from;
            }
            else
            {
                lines.Add(text);
            }
        }

        return lines;
    }

    public void Dispose() => _udp.Dispose();
}

/// <summary>The game's end (<c>--editor-link PORT</c>): hello on start, the editor's reloads polled once a frame.</summary>
public sealed class EditorLinkClient : IDisposable
{
    private readonly UdpClient _udp = EditorLink.Open();
    private readonly IPEndPoint _editor;

    public EditorLinkClient(int editorPort)
    {
        _editor = new IPEndPoint(IPAddress.Loopback, editorPort);
        Send(EditorLink.Hello);
    }

    /// <summary>How many reloads the editor asked for since the last call (lines from elsewhere are ignored).</summary>
    public int PollReloads() =>
        EditorLink.Drain(_udp).Count(m => m.Text == EditorLink.Reload && m.From.Port == _editor.Port && IPAddress.IsLoopback(m.From.Address));

    /// <summary>Tells the editor a line (<see cref="EditorLink.Reloaded"/> or <see cref="EditorLink.Failed"/> with the map).</summary>
    public void Send(string line)
    {
        var b = EditorLink.Bytes(line);
        try
        {
            _udp.Send(b, b.Length, _editor);
        }
        catch (SocketException)
        {
            // The editor is gone: the game plays on.
        }
    }

    public void Dispose() => _udp.Dispose();
}
