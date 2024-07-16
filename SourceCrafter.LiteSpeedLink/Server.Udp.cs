using MemoryPack;
using System.Diagnostics.CodeAnalysis;
using System.Net.Sockets;
using System.Net;
using System.Runtime.CompilerServices;

namespace SourceCrafter.LiteSpeedLink;

public static partial class Server
{
    public static UdpClient StartUdpServer<TServiceProvider>(
        int port,
        TServiceProvider provider,
        Dictionary<int, UdpRequestHandler<TServiceProvider>> requestHandlers,
        CancellationToken token = default) where TServiceProvider : IServiceProvider, IDisposable, IAsyncDisposable
    {

        var udpServer = new UdpClient(port);

        //Console.WriteLine($"UDP Server listening on port {port}");

        ListenAsync(udpServer, provider, requestHandlers, token);

        return udpServer;

        static async void ListenAsync(UdpClient udpServer, TServiceProvider provider, Dictionary< int, UdpRequestHandler<TServiceProvider>> requestHandlers, CancellationToken token)
        {
            try
            {
                while (await udpServer.ReceiveAsync(token) is { RemoteEndPoint: { } answerTo, Buffer: { } buffer })
                {
                    HandleRequestAsync(udpServer, provider, requestHandlers, buffer, answerTo, token);
                }
            }
            catch (Exception ex)
                when (ex is SocketException { SocketErrorCode: SocketError.ConnectionAborted or SocketError.OperationAborted })
            {
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static async void HandleRequestAsync(
            UdpClient udpServer, 
            TServiceProvider provider, 
            Dictionary<int, UdpRequestHandler<TServiceProvider>> requestHandlers, 
            byte[] buffer, IPEndPoint answerTo, CancellationToken token)
        {
            await (requestHandlers.TryGetValue(BitConverter.ToInt32(buffer.AsSpan()[0..4]), out var requestHandler)
                ? requestHandler(new(udpServer, provider, answerTo, buffer.AsMemory()[4..]), token)
                : udpServer.SendAsync(MemoryPackSerializer.Serialize(false), answerTo, token));

        }
    }
}

public delegate ValueTask<int> UdpRequestHandler<TServiceProvider>(UdpRequestContext<TServiceProvider> _, CancellationToken _3) where TServiceProvider : IServiceProvider;

public class UdpRequestContext<TServiceProvider>(
    UdpClient client, 
    TServiceProvider provider, 
    IPEndPoint endpoint, 
    ReadOnlyMemory<byte> bytes) where TServiceProvider : IServiceProvider
{
    public TServiceProvider Provider { get; } = provider;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TOut? Get<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TOut>()
    {
        return MemoryPackSerializer.Deserialize<TOut>(bytes.Span);
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


