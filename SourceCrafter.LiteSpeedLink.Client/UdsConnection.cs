using System.Net.Sockets;

namespace SourceCrafter.LiteSpeedLink.Client;

/// <summary>Cliente sobre Unix Domain Socket. Mismo canal multiplexado que TCP.</summary>
public sealed class UdsConnection(string path, bool coalesceStreams = true) : StreamConnection(coalesceStreams)
{
    private Socket? _socket;

    private protected override async Task<(Stream, string)> OpenAsync()
    {
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(path)).ConfigureAwait(false);
        }
        catch
        {
            socket.Dispose();
            throw;
        }

        _socket = socket;
        return (new NetworkStream(socket), path);
    }

    private protected override void Close() => _socket?.Dispose();
}
