using MemoryPack;
using System.Diagnostics.CodeAnalysis;
using System.Net.Sockets;
using System.Net;
using System.Runtime.CompilerServices;
using static SourceCrafter.LiteSpeedLink.Server;

namespace SourceCrafter.LiteSpeedLink;

public partial class Server
{
    public static UdpClient StartUdpServer(
    int port,
        Dictionary<int, UdpRequestHandler> requestHandlers,
        CancellationToken token = default)
    {

        var udpServer = new UdpClient(port);

        //Console.WriteLine($"UDP Server listening on port {port}");

        ListenAsync(udpServer, requestHandlers, token);
        return udpServer;

        static async void ListenAsync(UdpClient udpServer, Dictionary<int, UdpRequestHandler> requestHandlers, CancellationToken token)
        {
            try
            {
                while (await udpServer.ReceiveAsync(token) is { RemoteEndPoint: { } answerTo, Buffer: { } buffer })
                {
                    HandleRequestAsync(udpServer, requestHandlers, buffer, answerTo, token);
                }
            }
            catch (Exception ex)
                when (ex is SocketException { SocketErrorCode: SocketError.ConnectionAborted or SocketError.OperationAborted })
            {
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void HandleRequestAsync(UdpClient udpServer, Dictionary<int, UdpRequestHandler> requestHandlers, byte[] buffer, IPEndPoint answerTo, CancellationToken token)
        {
            _ = ((requestHandlers.TryGetValue(BitConverter.ToInt32(buffer[0..4]), out var requestHandler))
                ? requestHandler(new(udpServer, answerTo, buffer[4..]), default)
                : udpServer.SendAsync(MemoryPackSerializer.Serialize(false), answerTo));

        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static ValueTask<int> NotFound(UdpClient udpServer, CancellationToken token)
        {
            return udpServer.SendAsync(MemoryPackSerializer.Serialize(false), token);
        }
    }
}

public delegate ValueTask<int> UdpRequestHandler(UdpRequestContext _, CancellationToken _3);

public class UdpRequestContext(UdpClient client, IPEndPoint endpoint, byte[] bytes)
{

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TOut? Get<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TOut>()
    {
        return MemoryPackSerializer.Deserialize<TOut>(bytes);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask<int> ReturnAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TOut>(TOut? payload, CancellationToken token = default)
    {
        return client.SendAsync(MemoryPackSerializer.Serialize(payload), endpoint, token);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask<int> EndStreamingAsync(CancellationToken token = default)
    {
        return client.SendAsync(ReadOnlyMemory<byte>.Empty, endpoint, token);
    }
}


