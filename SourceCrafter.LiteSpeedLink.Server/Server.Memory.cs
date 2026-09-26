using SharedMemory;
using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;

namespace SourceCrafter.LiteSpeedLink;

/// <summary>Devuelve siempre una respuesta <c>[estado][cuerpo]</c>, nunca <c>null</c>.</summary>
public delegate byte[] MemoryRequestHandler(long op, MemoryRequestContext ctx, CancellationToken token);
public delegate Task<byte[]> MemoryAsyncRequestHandler(long op, MemoryRequestContext ctx, CancellationToken token);

public static partial class Server
{
    [SupportedOSPlatform("windows")]
    public static RpcBuffer StartMemoryServer(
        string contextId,
        MemoryRequestHandler requestHandlers,
        Action? onFinalize = null,
        int timeout = 1000,
        CancellationToken cancelToken = default)
    {
        return new(contextId, (msgId, payload) =>
        {
            try
            {
                long op = Framing.ReadOpId(payload.AsSpan());

                return requestHandlers(op, new(payload, Framing.OpIdSize, timeout, cancelToken), cancelToken);
            }
            catch (Exception ex)
            {
                return MemoryResponse.Failed(ex);
            }
        });
    }

    [SupportedOSPlatform("windows")]
    public static RpcBuffer StartMemoryServerAsync(
        string contextId,
        MemoryAsyncRequestHandler requestHandlers,
        Action? onFinalize = null,
        int timeout = 1000,
        CancellationToken cancelToken = default)
    {
        return new(contextId, async (msgId, payload) =>
        {
            try
            {
                long op = Framing.ReadOpId(payload.AsSpan());

                return await requestHandlers(op, new(payload, Framing.OpIdSize, timeout, cancelToken), cancelToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return MemoryResponse.Failed(ex);
            }
        });
    }
}

/// <summary>
/// Respuestas del transporte en memoria. Se construyen sobre un buffer por hilo, de modo que la
/// unica asignacion es el array final que exige <see cref="RpcBuffer"/>.
/// </summary>
public static class MemoryResponse
{
    [ThreadStatic] private static ArrayBufferWriter<byte>? _writer;

    private static readonly byte[]
        _notFound = [(byte)ResponseStatus.NotFound],
        _streamEnd = [(byte)ResponseStatus.StreamEnd];

    public static byte[] NotFound => _notFound;

    public static byte[] StreamEnd => _streamEnd;

    public static byte[] Success<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T>(T? value) => Build(ResponseStatus.Success, value);

    public static byte[] Failed(Exception exception) => Build(ResponseStatus.Failed, exception.ToString());

    private static byte[] Build<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T>(ResponseStatus status, T? value)
    {
        var writer = _writer ??= new(256);

        writer.ResetWrittenCount();

        writer.GetSpan(Framing.StatusSize)[0] = (byte)status;
        writer.Advance(Framing.StatusSize);

        Serialize(writer, value);

        return writer.WrittenSpan.ToArray();
    }
}

#if !NETSTANDARD
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
#endif
public sealed class MemoryRequestContext(byte[] payload, int offset, int timeout, CancellationToken cancelToken) : BufferReader(payload)
{
    /// <summary>Cuerpo de la peticion, sin la cabecera de operacion. No copia el array original.</summary>
    private readonly ReadOnlyMemory<byte> _body = payload.AsMemory(offset);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TOut? Get<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TOut>() => Deserialize<TOut>(_body.Span);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte[] Return<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TIn>(TIn @in) => MemoryResponse.Success(@in);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte[] NotFound() => MemoryResponse.NotFound;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte[] Fail(Exception exception) => MemoryResponse.Failed(exception);

    /// <summary>
    /// Envia cada elemento por el canal de la sesion de stream y responde <see cref="ResponseStatus.StreamEnd"/>.
    /// Usa el token del servidor: el generador no inyecta otro.
    /// </summary>
    public byte[] Yield<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TData>(IEnumerable<TData> enumerate)
    {
        using RpcBuffer subAgent = new(StreamSessionId());

        foreach (var item in enumerate)
            subAgent.RemoteRequest(Serialize(item), timeoutMs: timeout, cancellationToken: cancelToken);

        subAgent.RemoteRequest(null, timeoutMs: timeout, cancellationToken: cancelToken);

        return MemoryResponse.StreamEnd;
    }

    public async Task<byte[]> Yield<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TData>(IAsyncEnumerable<TData> enumerate)
    {
        using RpcBuffer subAgent = new(StreamSessionId());

        await foreach (var item in enumerate.WithCancellation(cancelToken).ConfigureAwait(false))
            await subAgent.RemoteRequestAsync(Serialize(item), timeoutMs: timeout, cancellationToken: cancelToken).ConfigureAwait(false);

        await subAgent.RemoteRequestAsync(null, timeoutMs: timeout, cancellationToken: cancelToken).ConfigureAwait(false);

        return MemoryResponse.StreamEnd;
    }

    private string StreamSessionId() => Deserialize<string>(_body.Span)!;
}
