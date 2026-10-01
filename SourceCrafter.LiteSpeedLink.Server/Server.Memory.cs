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
    public static IDisposable StartMemoryServer(
        string contextId,
        MemoryRequestHandler requestHandlers,
        Action? onFinalize = null,
        CancellationToken cancelToken = default)
    {
        return new MemoryLobby(contextId, onFinalize, name =>
        {
        RpcBuffer rpc = null!;
        return rpc = new RpcBuffer(name, (msgId, payload) =>
        {
            try
            {
                long op = Framing.ReadOpId(payload.AsSpan());

                return requestHandlers(op, new(payload, Framing.OpIdSize, rpc, msgId, cancelToken), cancelToken);
            }
            catch (Exception ex)
            {
                return MemoryResponse.Failed(ex);
            }
        });
        });
    }

    [SupportedOSPlatform("windows")]
    public static IDisposable StartMemoryServerAsync(
        string contextId,
        MemoryAsyncRequestHandler requestHandlers,
        Action? onFinalize = null,
        CancellationToken cancelToken = default)
    {
        return new MemoryLobby(contextId, onFinalize, name =>
        {
        RpcBuffer rpc = null!;
        return rpc = new RpcBuffer(name, async (msgId, payload) =>
        {
            try
            {
                long op = Framing.ReadOpId(payload.AsSpan());

                return await requestHandlers(op, new(payload, Framing.OpIdSize, rpc, msgId, cancelToken), cancelToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return MemoryResponse.Failed(ex);
            }
        });
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
public sealed class MemoryRequestContext(byte[] payload, int offset, RpcBuffer rpc, ulong msgId, CancellationToken cancelToken) : BufferReader(payload)
{
    /// <summary>Cuerpo de la peticion, sin la cabecera de operacion. No copia el array original.</summary>
    private readonly ReadOnlyMemory<byte> _body = payload.AsMemory(offset);

    public ReadOnlyMemory<byte> Body => _body;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TOut? Get<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TOut>() => Deserialize<TOut>(_body.Span);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte[] Return<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TIn>(TIn @in) => MemoryResponse.Success(@in);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte[] NotFound() => MemoryResponse.NotFound;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte[] Fail(Exception exception) => MemoryResponse.Failed(exception);

    /// <summary>
    /// Envia cada elemento por el canal principal, dirigido a esta peticion (solo lo acepta el cliente que la hizo),
    /// y responde <see cref="ResponseStatus.StreamEnd"/>. Usa el token del servidor: el generador no inyecta otro.
    /// </summary>
    public byte[] Yield<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TData>(IEnumerable<TData> enumerate)
    {
        var batch = RentBatch();
        foreach (var item in enumerate)
        {
            cancelToken.ThrowIfCancellationRequested();
            Append(batch, item);
        }

        Flush(batch);
        _batches.Add(batch);
        return MemoryResponse.StreamEnd;
    }

    public async Task<byte[]> Yield<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TData>(IAsyncEnumerable<TData> enumerate)
    {
        var batch = RentBatch();
        await using var e = enumerate.GetAsyncEnumerator(cancelToken);
        for (; ; )
        {
            var next = e.MoveNextAsync();
            if (!next.IsCompleted) Flush(batch); // el productor va a esperar: no retener lo acumulado
            if (!await next.ConfigureAwait(false)) break;
            Append(batch, e.Current);
        }

        Flush(batch);
        _batches.Add(batch);
        return MemoryResponse.StreamEnd;
    }

    // ponytail: lote fijo de 16 KB (< nodo de 50 KB); items mayores van solos en su propio mensaje multipaquete.
    private const int BatchBytes = 16 * 1024;

    // ponytail: un buffer por stream en curso, reutilizado entre streams; pool sin limite (tantos como streams concurrentes).
    private static readonly System.Collections.Concurrent.ConcurrentBag<ArrayBufferWriter<byte>> _batches = [];

    private static ArrayBufferWriter<byte> RentBatch() => _batches.TryTake(out var b) ? b : new(BatchBytes);

    /// <summary>Agrupa <c>[int32 len][item]</c> en un solo StreamItem: una publicacion (y una senal) por lote, no por item.</summary>
    private void Append<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TData>(ArrayBufferWriter<byte> batch, TData item)
    {
        int at = batch.WrittenCount;
        batch.GetSpan(sizeof(int));
        batch.Advance(sizeof(int));
        Serialize(batch, item);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(
            System.Runtime.InteropServices.MemoryMarshal.AsMemory(batch.WrittenMemory).Span.Slice(at), batch.WrittenCount - at - sizeof(int));

        if (batch.WrittenCount >= BatchBytes) Flush(batch);
    }

    private void Flush(ArrayBufferWriter<byte> batch)
    {
        if (batch.WrittenCount == 0) return;
        if (!rpc.SendStreamItem(msgId, batch, static (w, b) => w.Write(b.WrittenSpan))) throw new IOException("Stream channel closed.");
        batch.ResetWrittenCount();
    }
}
