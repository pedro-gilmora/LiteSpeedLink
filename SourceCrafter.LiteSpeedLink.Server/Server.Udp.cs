using MemoryPack;
using System.Diagnostics.CodeAnalysis;
using System.Net.Sockets;
using System.Net;
using System.Runtime.CompilerServices;
using System.IO.Pipelines;
using System.Collections.Frozen;

namespace SourceCrafter.LiteSpeedLink;

public delegate ValueTask<int> UdpRequestHandler(long id, UdpRequestContext ctx, CancellationToken token);
public static partial class Server
{
    public static UdpClient StartUdpServer(
        int port,
        UdpRequestHandler requestHandlers,
        Action onFinalize,
        CancellationToken token = default)
    {
        var udpServer = new UdpClient(port);

        ListenAsync(udpServer, requestHandlers, onFinalize, token);

        return udpServer;

        static async void ListenAsync(UdpClient udpServer, UdpRequestHandler requestHandlers, Action onFinalize, CancellationToken token)
        {
            try
            {
                while (await udpServer.ReceiveAsync(token) is { RemoteEndPoint: { } answerTo, Buffer: { } buffer })
                {
                    await requestHandlers(BitConverter.ToInt64(buffer.AsSpan()[0..8]), new(udpServer, answerTo, buffer.AsMemory()[8..]), token);
                }
            }
            catch (Exception ex) when (ex is SocketException { SocketErrorCode: SocketError.ConnectionAborted or SocketError.OperationAborted })
            {
            }
            finally 
            {
                onFinalize();
            }
        }
    }
}

public sealed class UdpRequestContext(UdpClient client, IPEndPoint endpoint, ReadOnlyMemory<byte> bytes)
{

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TOut? Get<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TOut>() => Deserialize<TOut>(bytes.Span);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask<int> ReturnAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TOut>(TOut? payload, CancellationToken token = default) => client.SendAsync(Serialize(payload), endpoint, token);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask<int> EndStreamingAsync(CancellationToken token = default) => client.SendAsync(ReadOnlyMemory<byte>.Empty, endpoint, token);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public async ValueTask<int> EnumerateAsync<TData>(Func<IAsyncEnumerable<TData>> value, CancellationToken token = default)
    {
        await foreach (var item in value()) await ReturnAsync(item, token).ConfigureAwait(true);

        return await EndStreamingAsync(token);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public async ValueTask<int> EnumerateAsync<TData>(Func<IEnumerable<TData>> value, CancellationToken token = default)
    {
        foreach (var item in value()) await ReturnAsync(item, token).ConfigureAwait(true);

        return await EndStreamingAsync(token);
    }
}


