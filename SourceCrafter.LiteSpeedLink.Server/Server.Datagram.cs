using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Net.Sockets;
using System.Net;
using System.Runtime.CompilerServices;

namespace SourceCrafter.LiteSpeedLink;

public delegate ValueTask<ResponseStatus> UdpRequestHandler(long id, UdpRequestContext ctx, CancellationToken token);

public static partial class Server
{
    /// <summary>
    /// Cada datagrama es un mensaje completo, asi que no lleva longitud. El socket del cliente es
    /// compartido por llamadas concurrentes: peticion <c>[corrId][opId][cuerpo]</c> y respuesta
    /// <c>[corrId][estado][cuerpo]</c>.
    /// </summary>
    public static UdpClient StartUdpServer(
        int port,
        UdpRequestHandler requestHandlers,
        Action onFinalize,
        CancellationToken token = default,
        int maxInFlightPerEndpoint = MaxInFlightPerEndpoint,
        int maxAmplification = 0)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxInFlightPerEndpoint, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(maxAmplification);

        var udpServer = new UdpClient(port);

        ListenAsync(udpServer, requestHandlers, onFinalize, maxInFlightPerEndpoint, maxAmplification, token);

        return udpServer;

        static async void ListenAsync(UdpClient udpServer, UdpRequestHandler requestHandlers, Action onFinalize, int limit, int amplification, CancellationToken token)
        {
            var inflight = new System.Collections.Concurrent.ConcurrentDictionary<IPEndPoint, int>();

            try
            {
                while (await udpServer.ReceiveAsync(token).ConfigureAwait(false) is { RemoteEndPoint: { } answerTo, Buffer: { } buffer })
                {
                    if (buffer.Length < Framing.CorrelationIdSize + Framing.OpIdSize) continue;

                    // UDP no tiene back-pressure: por encima del limite el datagrama se descarta (el cliente lo ve como perdida).
                    if (inflight.AddOrUpdate(answerTo, 1, static (_, n) => n + 1) > limit)
                    {
                        Release(inflight, answerTo);
                        continue;
                    }

                    int correlationId = BinaryPrimitives.ReadInt32LittleEndian(buffer);
                    long op = Framing.ReadOpId(buffer.AsSpan(Framing.CorrelationIdSize));
                    var ctx = new UdpRequestContext(udpServer, answerTo, correlationId, buffer.AsMemory(Framing.CorrelationIdSize + Framing.OpIdSize), token,
                                            amplification == 0 ? long.MaxValue : (long)buffer.Length * amplification);

                    _ = DispatchUdpAsync(requestHandlers, op, ctx, inflight, answerTo, token);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
            }
            finally
            {
                onFinalize();
            }
        }

        static async Task DispatchUdpAsync(UdpRequestHandler handlers, long op, UdpRequestContext ctx,
            System.Collections.Concurrent.ConcurrentDictionary<IPEndPoint, int> inflight, IPEndPoint endpoint, CancellationToken token)
        {
            try
            {
                await handlers(op, ctx, token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && !ctx.IsCompleted)
            {
                try { await ctx.FailAsync(ex).ConfigureAwait(false); } catch { }
            }
            catch
            {
            }
            finally
            {
                Release(inflight, endpoint);
            }
        }

        static void Release(System.Collections.Concurrent.ConcurrentDictionary<IPEndPoint, int> inflight, IPEndPoint endpoint)
        {
            // Quita la entrada al llegar a 0 para no acumular endpoints (evita crecer sin limite con IPs falsas).
            if (inflight.AddOrUpdate(endpoint, 0, static (_, n) => n - 1) <= 0)
                inflight.TryRemove(new(endpoint, 0));
        }
    }

    /// <summary>Limite por defecto de peticiones UDP concurrentes por endpoint de origen; el exceso se descarta.</summary>
    public const int MaxInFlightPerEndpoint = 256;

    // maxAmplification (0 = sin limite, red de confianza): bytes enviados por peticion <= N x bytes recibidos.
    // Con IP de origen falsificada el atacante no puede usar el servidor como amplificador; lo que excede se descarta.
}

public sealed class UdpRequestContext
{
    private const int HeaderSize = Framing.CorrelationIdSize + Framing.StatusSize;

    private readonly UdpClient _client;
    private readonly IPEndPoint _endpoint;
    private readonly int _correlationId;
    private readonly ReadOnlyMemory<byte> _body;
    private readonly CancellationToken _token;
    private int _completed;
    private int _seq;
    private long _budget;

    internal UdpRequestContext(UdpClient client, IPEndPoint endpoint, int correlationId, ReadOnlyMemory<byte> body, CancellationToken token, long budget = long.MaxValue)
    {
        _budget = budget;
        _client = client;
        _endpoint = endpoint;
        _correlationId = correlationId;
        _body = body;
        _token = token;
    }

    internal bool IsCompleted => Volatile.Read(ref _completed) == 1;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TOut? Get<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TOut>() => Deserialize<TOut>(_body.Span);

    public ValueTask<ResponseStatus> ReturnAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TOut>(TOut? payload)
    {
        Complete();
        return SendAsync(ResponseStatus.Success, payload, true);
    }

    public ValueTask<ResponseStatus> YieldAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TData>(TData item) =>
        SendAsync(ResponseStatus.Success, item, true, _seq++);

    public ValueTask<ResponseStatus> EndStreamingAsync()
    {
        Complete();
        return SendAsync<byte>(ResponseStatus.StreamEnd, default, false, _seq);
    }

    public ValueTask<ResponseStatus> NotFoundAsync()
    {
        Complete();
        return SendAsync<byte>(ResponseStatus.NotFound, default, false);
    }

    public ValueTask<ResponseStatus> FailAsync(Exception exception)
    {
        Complete();
        return SendAsync(ResponseStatus.Failed, exception.Message, true);
    }

    public async ValueTask<ResponseStatus> EnumerateAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TData>(Func<IAsyncEnumerable<TData>> value)
    {
        await foreach (var item in value().WithCancellation(_token).ConfigureAwait(false))
            await YieldAsync(item).ConfigureAwait(false);

        return await EndStreamingAsync().ConfigureAwait(false);
    }

    public async ValueTask<ResponseStatus> EnumerateAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TData>(Func<IEnumerable<TData>> value)
    {
        foreach (var item in value())
            await YieldAsync(item).ConfigureAwait(false);

        return await EndStreamingAsync().ConfigureAwait(false);
    }

    private async ValueTask<ResponseStatus> SendAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T>(ResponseStatus status, T? body, bool hasBody, int seq = -1)
    {
        var writer = new ArrayBufferWriter<byte>(64);
        var header = writer.GetSpan(HeaderSize + sizeof(int));

        BinaryPrimitives.WriteInt32LittleEndian(header, _correlationId);
        header[Framing.CorrelationIdSize] = (byte)status;

        if (seq >= 0)
        {
            BinaryPrimitives.WriteInt32LittleEndian(header[HeaderSize..], seq);
            writer.Advance(HeaderSize + sizeof(int));
        }
        else writer.Advance(HeaderSize);

        if (hasBody) Serialize(writer, body);

        if (Interlocked.Add(ref _budget, -writer.WrittenCount) < 0) return status;

        await _client.SendAsync(writer.WrittenMemory, _endpoint, _token).ConfigureAwait(false);

        return status;
    }

    private void Complete() => Volatile.Write(ref _completed, 1);
}
