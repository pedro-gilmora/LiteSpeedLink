using SharedMemory;
using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;

namespace SourceCrafter.LiteSpeedLink;

/// <summary>
/// La respuesta <c>[estado][cuerpo]</c> la escribe el contexto directamente en el nodo (<c>Return</c>, <c>Yield</c>, <c>NotFound</c>, <c>Fail</c>);
/// si el handler solo devuelve un estado, se responde <c>[estado]</c>.
/// </summary>
public delegate ResponseStatus MemoryRequestHandler(long op, MemoryRequestContext ctx, CancellationToken token);
public delegate ValueTask<ResponseStatus> MemoryAsyncRequestHandler(long op, MemoryRequestContext ctx, CancellationToken token);

public static partial class Server
{
    [SupportedOSPlatform("windows")]
    public static IDisposable StartMemoryServer(
        string contextId,
        MemoryRequestHandler requestHandlers,
        Action? onFinalize = null,
        CancellationToken cancelToken = default)
    {
        MemHandler handle = request =>
        {
            var ctx = new MemoryRequestContext(request, cancelToken);
            try
            {
                ctx.Complete(requestHandlers(Framing.ReadOpId(request.Payload), ctx, cancelToken));
            }
            catch (Exception ex)
            {
                ctx.Fail(ex);
            }

            return default;
        };

        return new MemoryHost(MemHost.Start(contextId, handle), onFinalize);
    }

    [SupportedOSPlatform("windows")]
    public static IDisposable StartMemoryServerAsync(
        string contextId,
        MemoryAsyncRequestHandler requestHandlers,
        Action? onFinalize = null,
        CancellationToken cancelToken = default)
    {
        MemHandler handle = async request =>
        {
            var ctx = new MemoryRequestContext(request, cancelToken);
            try
            {
                long op = Framing.ReadOpId(request.Payload);
                ctx.Complete(await requestHandlers(op, ctx, cancelToken).ConfigureAwait(false));
            }
            catch (Exception ex)
            {
                ctx.Fail(ex);
            }
        };

        return new MemoryHost(MemHost.Start(contextId, handle), onFinalize);
    }

    /// <summary>Un solo <see cref="MemHost"/> multicliente: cada cliente se libera con su propio Close, sin lobby ni Bye.</summary>
    private sealed class MemoryHost(MemHost host, Action? onFinalize) : IDisposable
    {
        public void Dispose()
        {
            host.Dispose();
            onFinalize?.Invoke();
        }
    }
}

#if !NETSTANDARD
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
#endif
public sealed class MemoryRequestContext
{
    private readonly MemRequest request;
    private readonly CancellationToken cancelToken;
    private bool _replied;

    internal MemoryRequestContext(MemRequest request, CancellationToken cancelToken)
    {
        this.request = request;
        this.cancelToken = cancelToken;
    }

    /// <summary>Cuerpo de la peticion, sin la cabecera de operacion. Buffer prestado: solo valido mientras dura el handler.</summary>
    public ReadOnlyMemory<byte> Body => request.PayloadMemory[Framing.OpIdSize..];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TOut? Get<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TOut>() => Deserialize<TOut>(Body.Span);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ResponseStatus Return<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TIn>(TIn @in) => Reply(ResponseStatus.Success, @in);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ResponseStatus NotFound() => Reply(ResponseStatus.NotFound);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ResponseStatus Fail(Exception exception) => Reply(ResponseStatus.Failed, exception.ToString());

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ResponseStatus Fail(string reason) => Reply(ResponseStatus.Failed, reason);

    /// <summary>Responde <c>[estado]</c> si el handler devolvio un estado sin responder.</summary>
    internal void Complete(ResponseStatus status) => Reply(status);

    // Una sola respuesta por peticion; se escribe directamente en el nodo del cliente. Si serializar falla, _replied
    // sigue en false y el Fail del llamador aun puede responder.
    private ResponseStatus Reply(ResponseStatus status)
    {
        if (_replied) return status;
        request.Reply(status, WriteStatus);
        _replied = true;
        return status;
    }

    private ResponseStatus Reply<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T>(ResponseStatus status, T value)
    {
        if (_replied) return status;
        request.Reply((status, value), WriteValue);
        _replied = true;
        return status;
    }

    private static void WriteStatus(IBufferWriter<byte> writer, ResponseStatus status)
    {
        writer.GetSpan(Framing.StatusSize)[0] = (byte)status;
        writer.Advance(Framing.StatusSize);
    }

    private static void WriteValue<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T>(IBufferWriter<byte> writer, (ResponseStatus status, T value) state)
    {
        WriteStatus(writer, state.status);
        Serialize(writer, state.value);
    }

    /// <summary>
    /// Envia cada elemento por el canal principal, dirigido a esta peticion (solo lo acepta el cliente que la hizo),
    /// y responde <see cref="ResponseStatus.StreamEnd"/>. Usa el token del servidor: el generador no inyecta otro.
    /// </summary>
    public ResponseStatus Yield<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TData>(IEnumerable<TData> enumerate)
    {
        var batch = RentBatch();
        foreach (var item in enumerate)
        {
            cancelToken.ThrowIfCancellationRequested();
            Append(batch, item);
        }

        Flush(batch);
        _batches.Add(batch);
        return Reply(ResponseStatus.StreamEnd);
    }

    public async ValueTask<ResponseStatus> Yield<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TData>(IAsyncEnumerable<TData> enumerate)
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
        return Reply(ResponseStatus.StreamEnd);
    }

    // ponytail
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
        // false = el cliente cancelo/abandono el stream o se desconecto: el productor para.
        if (!request.Item(batch, static (w, b) => w.Write(b.WrittenSpan))) throw new OperationCanceledException("Stream cancelled by the client.");
        batch.ResetWrittenCount();
    }
}
