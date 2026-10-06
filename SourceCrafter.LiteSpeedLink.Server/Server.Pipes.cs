using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;

namespace SourceCrafter.LiteSpeedLink;

public delegate ValueTask<ResponseStatus> RequestHandler(long id, RequestContext ctx, CancellationToken token);

/// <summary>Dispatch sin indireccion: el host generado lo implementa en un struct y el JIT especializa el servidor para el.</summary>
public interface IRequestHandler
{
    ValueTask<ResponseStatus> HandleAsync(long id, RequestContext ctx, CancellationToken token);
}

/// <summary>Adaptador para handlers escritos a mano (lambdas); los hosts generados no pasan por aqui.</summary>
public readonly struct DelegateRequestHandler(RequestHandler handler) : IRequestHandler
{
    public ValueTask<ResponseStatus> HandleAsync(long id, RequestContext ctx, CancellationToken token) => handler(id, ctx, token);
}

public static partial class Server
{
    /// <summary>
    /// Atiende una conexion multiplexada: cada peticion <c>[len][corrId][opId][cuerpo]</c> se despacha
    /// en paralelo y su respuesta <c>[len][corrId][estado][cuerpo]</c> sale con el mismo corrId, de
    /// modo que el cliente la empareja aunque las respuestas lleguen desordenadas.
    /// </summary>
    /// <summary>Limite por defecto de peticiones concurrentes por conexion; al llegar se deja de leer el socket (back-pressure TCP).</summary>
    public const int MaxInFlightPerConnection = 256;

    /// <summary>Por defecto TCP/UDS agrupan como Memory: lote hasta 32 KB o hasta que el productor va a esperar. 0 = item a item.</summary>
    public const int DefaultStreamBatch = int.MaxValue;

    internal static async Task ServePipeAsync<THandler>(PipeReader reader, PipeWriter writer, THandler handlers, CancellationToken token, int maxInFlight = MaxInFlightPerConnection, BatchPolicy batch = default)
        where THandler : struct, IRequestHandler
    {
        var responses = new ResponseChannel(writer, batch: batch);
        var inflight = new ConcurrentDictionary<Task, byte>();
        using var slots = new SemaphoreSlim(maxInFlight);

        try
        {
            while (true)
            {
                var result = await reader.ReadAsync(token).ConfigureAwait(false);
                var buffer = result.Buffer;

                while (Framing.TryReadFrame(ref buffer, out var correlationId, out var content))
                {
                    if (content.Length < Framing.OpIdSize) throw new InvalidDataException($"Request too short: {content.Length}.");

                    // El lector avanza antes de que el handler termine: el cuerpo se copia a un
                    // buffer alquilado que vive lo que dura la peticion.
                    var op = Framing.ReadOpId(content);
                    int length = (int)content.Length - Framing.OpIdSize;
                    var body = ArrayPool<byte>.Shared.Rent(length);
                    content.Slice(Framing.OpIdSize).CopyTo(body);

                    await slots.WaitAsync(token).ConfigureAwait(false);

                    var request = DispatchAsync(handlers, op, new(correlationId, body.AsMemory(0, length), responses, token), body, slots, token);

                    if (!request.IsCompleted && inflight.TryAdd(request, 0))
                        _ = request.ContinueWith(static (t, s) => ((ConcurrentDictionary<Task, byte>)s!).TryRemove(t, out _), inflight, TaskScheduler.Default);
                }

                reader.AdvanceTo(buffer.Start, buffer.End);

                if (result.IsCompleted || result.IsCanceled) break;
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidDataException)
        {
        }
        finally
        {
            try { await Task.WhenAll(inflight.Keys).ConfigureAwait(false); } catch { }

            await reader.CompleteAsync().ConfigureAwait(false);
            await writer.CompleteAsync().ConfigureAwait(false);
        }
    }

    private static async Task DispatchAsync<THandler>(THandler handlers, long op, RequestContext ctx, byte[] body, SemaphoreSlim? slots, CancellationToken token)
        where THandler : struct, IRequestHandler
    {
        try
        {
            await handlers.HandleAsync(op, ctx, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (!ctx.IsCompleted)
        {
            try { await ctx.FailAsync(ex).ConfigureAwait(false); } catch { }
        }
        catch
        {
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(body);
            slots?.Release();
        }
    }
}

/// <summary>
/// Unico punto de escritura de una conexion: serializa el acceso al <see cref="PipeWriter"/> para
/// que las tramas de respuestas concurrentes nunca se intercalen.
/// </summary>
/// <summary>Estudio #1: politica de lote como tipo; el host generado emite un struct por operacion y el JIT pliega los valores.</summary>
public interface IStreamPolicy
{
    /// <summary>0 = sin lotes (item a item).</summary>
    static abstract int Items { get; }
    /// <summary><see cref="long.MaxValue"/> = sin plazo.</summary>
    static abstract long MaxDelayTicks { get; }
}

/// <summary>Stream item a item, sin lotes.</summary>
public readonly struct Unbatched : IStreamPolicy
{
    public static int Items => 0;
    public static long MaxDelayTicks => long.MaxValue;
}

/// <summary>
/// Lotes de stream opt-in: se envia al llegar a <see cref="Items"/>, a 32 KB, al pasar el plazo desde el
/// primer item del lote, o en cuanto el productor asincrono va a esperar. <c>Items = 0</c> = sin lotes.
/// </summary>
internal readonly record struct BatchPolicy(int Items, long MaxDelayTicks)
{
    public static BatchPolicy Create(int items, TimeSpan maxDelay)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(items);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxDelay, TimeSpan.Zero);
        return new(items, maxDelay == TimeSpan.Zero ? long.MaxValue : (long)(maxDelay.TotalSeconds * System.Diagnostics.Stopwatch.Frequency));
    }
}

internal sealed class ResponseChannel(PipeWriter writer, bool correlated = true, BatchPolicy batch = default) : IThreadPoolWorkItem
{
    public BatchPolicy Batch => batch;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly FrameWriter _frames = new(writer, correlated);
    private int _flushScheduled;
    private int _flushWanted;

    // ponytail: umbral fijo; los items de stream se agrupan hasta 32 KB o hasta que corre el flush diferido.
    private const int EagerFlushBytes = 32 * 1024;

    public ValueTask<ResponseStatus> WriteAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T>(
        int correlationId, ResponseStatus status, T? body, CancellationToken token, bool deferFlush = false)
    {
        // Camino sincrono: candado libre y flush diferible -> sin maquina de estados ni TaskNode por item.
        if (deferFlush && _gate.Wait(0))
        {
            bool release = true;
            try
            {
                WriteFrame(correlationId, status, body);

                if (writer.CanGetUnflushedBytes && writer.UnflushedBytes < EagerFlushBytes)
                {
                    ScheduleFlush();
                    return new(status);
                }

                release = false;
                return FlushAndReleaseAsync(status);
            }
            finally
            {
                if (release) Release();
            }
        }

        return WriteSlowAsync(correlationId, status, body, token, deferFlush);
    }

    /// <summary>Flush con el candado tomado: si completa en linea no hay maquina de estados.</summary>
    private ValueTask<ResponseStatus> FlushAndReleaseAsync(ResponseStatus status)
    {
        ValueTask<FlushResult> flush;
        try
        {
            flush = writer.FlushAsync(CancellationToken.None);
        }
        catch
        {
            Release();
            throw;
        }

        if (!flush.IsCompleted) return AwaitFlushAndReleaseAsync(flush, status);

        try { flush.GetAwaiter().GetResult(); }
        finally { Release(); }

        return new(status);
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<ResponseStatus> AwaitFlushAndReleaseAsync(ValueTask<FlushResult> flush, ResponseStatus status)
    {
        try { await flush.ConfigureAwait(false); }
        finally { Release(); }

        return status;
    }

    private void WriteFrame<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T>(int correlationId, ResponseStatus status, T? body)
    {
        _frames.BeginFrame(correlationId, Framing.StatusSize)[0] = (byte)status;

        try
        {
            Serialize(_frames, body);
        }
        catch (Exception ex)
        {
            // La trama ya esta a medias en el writer y no se puede retirar: la conexion
            // queda inservible y se cierra para que el cliente no lea basura.
            writer.Complete(ex);
            throw;
        }

        _frames.EndFrame();
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<ResponseStatus> WriteSlowAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T>(
        int correlationId, ResponseStatus status, T? body, CancellationToken token, bool deferFlush)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            WriteFrame(correlationId, status, body);

            if (deferFlush && writer.CanGetUnflushedBytes && writer.UnflushedBytes < EagerFlushBytes)
            {
                // Items seguidos del mismo productor se acumulan; un unico flush en el pool los envia juntos.
                ScheduleFlush();
                return status;
            }

            // Sin token: cancelar un flush a medias partiria la trama.
            await writer.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            Release();
        }

        return status;
    }

    /// <summary>El propio canal es el work item del flush diferido: encolarlo no asigna (antes: caja async + TaskNode por flush).</summary>
    private void ScheduleFlush()
    {
        if (Interlocked.Exchange(ref _flushScheduled, 1) == 0) ThreadPool.UnsafeQueueUserWorkItem(this, preferLocal: false);
    }

    void IThreadPoolWorkItem.Execute()
    {
        if (!_gate.Wait(0))
        {
            // Candado ocupado: quien lo suelte reencola el flush (ver Release). Se reintenta tras marcar para no perder
            // una liberacion ocurrida entre el primer intento y la marca.
            Interlocked.Exchange(ref _flushWanted, 1);
            if (!_gate.Wait(0)) return;
            Volatile.Write(ref _flushWanted, 0);
        }

        bool release = true;
        try
        {
            Volatile.Write(ref _flushScheduled, 0);
            if (writer.UnflushedBytes > 0)
            {
                var flush = writer.FlushAsync(CancellationToken.None);
                if (flush.IsCompleted) flush.GetAwaiter().GetResult();
                else { release = false; _ = ReleaseAfterAsync(flush); }
            }
        }
        catch
        {
            // Conexion caida: el bucle de lectura la cierra.
        }
        finally
        {
            if (release) Release();
        }
    }

    private void Release()
    {
        _gate.Release();
        if (Volatile.Read(ref _flushWanted) != 0 && Interlocked.Exchange(ref _flushWanted, 0) != 0)
            ThreadPool.UnsafeQueueUserWorkItem(this, preferLocal: false);
    }

    private async Task ReleaseAfterAsync(ValueTask<FlushResult> flush)
    {
        try { await flush.ConfigureAwait(false); }
        catch { }
        finally { Release(); }
    }

    /// <summary>Trama con cuerpo ya codificado (lotes); flush diferido como los items sueltos.</summary>
    public ValueTask<ResponseStatus> WriteRawAsync(int correlationId, ResponseStatus status, ReadOnlyMemory<byte> body, CancellationToken token)
    {
        // Camino sincrono: candado libre -> sin maquina de estados por lote.
        if (!_gate.Wait(0)) return WriteRawSlowAsync(correlationId, status, body, token);

        bool release = true;
        try
        {
            WriteRawFrame(correlationId, status, body.Span);

            if (writer.CanGetUnflushedBytes && writer.UnflushedBytes < EagerFlushBytes)
            {
                ScheduleFlush();
                return new(status);
            }

            release = false;
            return FlushAndReleaseAsync(status);
        }
        finally
        {
            if (release) Release();
        }
    }

    private void WriteRawFrame(int correlationId, ResponseStatus status, ReadOnlySpan<byte> body)
    {
        _frames.BeginFrame(correlationId, Framing.StatusSize)[0] = (byte)status;
        body.CopyTo(_frames.GetSpan(body.Length));
        _frames.Advance(body.Length);
        _frames.EndFrame();
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<ResponseStatus> WriteRawSlowAsync(int correlationId, ResponseStatus status, ReadOnlyMemory<byte> body, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            WriteRawFrame(correlationId, status, body.Span);

            if (writer.CanGetUnflushedBytes && writer.UnflushedBytes < EagerFlushBytes)
            {
                ScheduleFlush();
                return status;
            }

            await writer.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            Release();
        }

        return status;
    }

    public ValueTask<ResponseStatus> WriteStatusAsync(int correlationId, ResponseStatus status, CancellationToken token)
    {
        if (!_gate.Wait(0)) return WriteStatusSlowAsync(correlationId, status, token);

        _frames.BeginFrame(correlationId, Framing.StatusSize)[0] = (byte)status;
        _frames.EndFrame();
        return FlushAndReleaseAsync(status);
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<ResponseStatus> WriteStatusSlowAsync(int correlationId, ResponseStatus status, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            _frames.BeginFrame(correlationId, Framing.StatusSize)[0] = (byte)status;
            _frames.EndFrame();

            await writer.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            Release();
        }

        return status;
    }
}

/// <summary>
/// Peticion en curso sobre una conexion multiplexada. Todas las respuestas salen con el corrId de
/// la peticion y usan el token de cancelacion del servidor.
/// </summary>
public sealed class RequestContext
{
    private readonly int _correlationId;
    private readonly ReadOnlyMemory<byte> _body;
    private readonly ResponseChannel _responses;
    private readonly CancellationToken _token;
    private int _completed;

    internal RequestContext(int correlationId, ReadOnlyMemory<byte> body, ResponseChannel responses, CancellationToken token)
    {
        _correlationId = correlationId;
        _body = body;
        _responses = responses;
        _token = token;
    }

    /// <summary>Ya se envio la respuesta final (valor, fin de stream o error).</summary>
    internal bool IsCompleted => Volatile.Read(ref _completed) == 1;

    /// <summary>Bytes de la peticion (sin cabecera): el host generado los lee con lectores tipados.</summary>
    public ReadOnlyMemory<byte> Body => _body;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TOut? Get<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TOut>() => Deserialize<TOut>(_body.Span);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask<ResponseStatus> ReturnAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TOut>(TOut? payload)
    {
        Complete();
        return _responses.WriteAsync(_correlationId, ResponseStatus.Success, payload, _token);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask<ResponseStatus> YieldAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TData>(TData item) =>
        _responses.WriteAsync(_correlationId, ResponseStatus.Success, item, _token, deferFlush: true);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask<ResponseStatus> EndStreamingAsync()
    {
        Complete();
        return _responses.WriteStatusAsync(_correlationId, ResponseStatus.StreamEnd, _token);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask<ResponseStatus> NotFoundAsync()
    {
        Complete();
        return _responses.WriteStatusAsync(_correlationId, ResponseStatus.NotFound, _token);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask<ResponseStatus> FailAsync(Exception exception) => FailAsync(exception.Message);

    /// <summary>Rechazo sin excepcion (pipelines): mismo Failed en la red.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask<ResponseStatus> FailAsync(string reason)
    {
        Complete();
        return _responses.WriteAsync(_correlationId, ResponseStatus.Failed, reason, _token);
    }

    public ValueTask<ResponseStatus> EnumerateAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TData>(Func<IAsyncEnumerable<TData>> value) => EnumerateAsync(value());

    public ValueTask<ResponseStatus> EnumerateAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TData>(Func<IEnumerable<TData>> value) => EnumerateAsync(value());

    /// <summary>Estudio #1: lo que emite el host generado; la secuencia llega directa, sin clausura.</summary>
    public ValueTask<ResponseStatus> EnumerateAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TData>(IAsyncEnumerable<TData> value)
        => EnumerateAsync(value, _responses.Batch);

    /// <summary>Politica fijada en el contrato (<c>[Stream(Batch, MaxDelayMs)]</c>): el host generado emite <typeparamref name="TPolicy"/> y el JIT la pliega.</summary>
    public async ValueTask<ResponseStatus> EnumerateAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TData, TPolicy>(IAsyncEnumerable<TData> value)
        where TPolicy : struct, IStreamPolicy
    {
        if (TPolicy.Items == 0)
        {
            await foreach (var item in value.WithCancellation(_token).ConfigureAwait(false))
                await YieldAsync(item).ConfigureAwait(false);

            return await EndStreamingAsync().ConfigureAwait(false);
        }

        await using var e = value.GetAsyncEnumerator(_token);

        while (true)
        {
            var next = e.MoveNextAsync();

            if (!next.IsCompleted) await FlushBatchAsync().ConfigureAwait(false);

            if (!await next.ConfigureAwait(false)) break;

            await AddToBatchAsync<TData, TPolicy>(e.Current).ConfigureAwait(false);
        }

        await FlushBatchAsync().ConfigureAwait(false);

        return await EndStreamingAsync().ConfigureAwait(false);
    }

    public async ValueTask<ResponseStatus> EnumerateAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TData, TPolicy>(IEnumerable<TData> value)
        where TPolicy : struct, IStreamPolicy
    {
        if (TPolicy.Items == 0)
        {
            foreach (var item in value) await YieldAsync(item).ConfigureAwait(false);
        }
        else
        {
            foreach (var item in value) await AddToBatchAsync<TData, TPolicy>(item).ConfigureAwait(false);
            await FlushBatchAsync().ConfigureAwait(false);
        }

        return await EndStreamingAsync().ConfigureAwait(false);
    }

    private ValueTask<ResponseStatus> AddToBatchAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TData, TPolicy>(TData item)
        where TPolicy : struct, IStreamPolicy
    {
        var w = _batch ??= _batches.TryTake(out var pooled) ? pooled : new(BatchFlushBytes);

        // Sin plazo (MaxDelayTicks == MaxValue) el JIT elimina la lectura del reloj.
        if (TPolicy.MaxDelayTicks != long.MaxValue && _batched == 0) _batchStarted = System.Diagnostics.Stopwatch.GetTimestamp();

        Serialize(w, item);

        return ++_batched >= TPolicy.Items || w.WrittenCount >= BatchFlushBytes
            || (TPolicy.MaxDelayTicks != long.MaxValue && System.Diagnostics.Stopwatch.GetTimestamp() - _batchStarted >= TPolicy.MaxDelayTicks)
            ? FlushBatchAsync()
            : new(ResponseStatus.Success);
    }

    private async ValueTask<ResponseStatus> EnumerateAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TData>(IAsyncEnumerable<TData> value, BatchPolicy policy)
    {
        _policy = policy;

        if (policy.Items == 0)
        {
            await foreach (var item in value.WithCancellation(_token).ConfigureAwait(false))
                await YieldAsync(item).ConfigureAwait(false);

            return await EndStreamingAsync().ConfigureAwait(false);
        }

        await using var e = value.GetAsyncEnumerator(_token);

        while (true)
        {
            var next = e.MoveNextAsync();

            // El productor va a esperar: lo acumulado sale ya, no espera a llenar el lote.
            if (!next.IsCompleted) await FlushBatchAsync().ConfigureAwait(false);

            if (!await next.ConfigureAwait(false)) break;

            await AddToBatchAsync(e.Current).ConfigureAwait(false);
        }

        await FlushBatchAsync().ConfigureAwait(false);

        return await EndStreamingAsync().ConfigureAwait(false);
    }

    public ValueTask<ResponseStatus> EnumerateAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TData>(IEnumerable<TData> value)
        => EnumerateAsync(value, _responses.Batch);

    private async ValueTask<ResponseStatus> EnumerateAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TData>(IEnumerable<TData> value, BatchPolicy policy)
    {
        _policy = policy;

        if (policy.Items == 0)
        {
            foreach (var item in value) await YieldAsync(item).ConfigureAwait(false);
        }
        else
        {
            foreach (var item in value) await AddToBatchAsync(item).ConfigureAwait(false);
            await FlushBatchAsync().ConfigureAwait(false);
        }

        return await EndStreamingAsync().ConfigureAwait(false);
    }

    // ponytail: buffer reutilizado entre streams (mismo patron que Server.Memory); pool sin limite = streams concurrentes.
    private static readonly System.Collections.Concurrent.ConcurrentBag<ArrayBufferWriter<byte>> _batches = [];
    private ArrayBufferWriter<byte>? _batch;
    private BatchPolicy _policy;
    private int _batched;
    private long _batchStarted;

    private const int BatchFlushBytes = 32 * 1024;

    private ValueTask<ResponseStatus> AddToBatchAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TData>(TData item)
    {
        var w = _batch ??= _batches.TryTake(out var pooled) ? pooled : new(BatchFlushBytes);

        if (_batched == 0) _batchStarted = System.Diagnostics.Stopwatch.GetTimestamp();

        // MemoryPack delimita cada valor: el lote es la concatenacion, sin prefijo de longitud por item.
        Serialize(w, item);

        var policy = _policy;

        // ponytail: el plazo se comprueba al llegar cada item; un productor que bloquea sin await no se corta (sin timer).
        return ++_batched >= policy.Items || w.WrittenCount >= BatchFlushBytes
            || System.Diagnostics.Stopwatch.GetTimestamp() - _batchStarted >= policy.MaxDelayTicks
            ? FlushBatchAsync()
            : new(ResponseStatus.Success);
    }

    private ValueTask<ResponseStatus> FlushBatchAsync()
    {
        if (_batched == 0) return new(ResponseStatus.Success);

        var write = _responses.WriteRawAsync(_correlationId, ResponseStatus.Batch, _batch!.WrittenMemory, _token);

        if (!write.IsCompletedSuccessfully) return AwaitBatchAsync(write);

        ResetBatch();
        return new(ResponseStatus.Success);
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<ResponseStatus> AwaitBatchAsync(ValueTask<ResponseStatus> write)
    {
        await write.ConfigureAwait(false);
        ResetBatch();
        return ResponseStatus.Success;
    }

    private void ResetBatch()
    {
        _batch!.ResetWrittenCount();
        _batched = 0;
    }

    private void Complete()
    {
        Volatile.Write(ref _completed, 1);

        if (_batch is { } b)
        {
            _batch = null;
            b.ResetWrittenCount();
            _batches.Add(b);
        }
    }
}
