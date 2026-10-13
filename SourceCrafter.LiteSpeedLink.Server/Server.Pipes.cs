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

    internal static async Task ServePipeAsync<THandler>(PipeReader reader, PipeWriter writer, THandler handlers, CancellationToken token, int maxInFlight = MaxInFlightPerConnection)
        where THandler : struct, IRequestHandler
    {
        var responses = new ResponseChannel(writer);
        var inflight = new Inflight();
        using var slots = new SemaphoreSlim(maxInFlight);

        try
        {
            while (true)
            {
                var result = await reader.ReadAsync(token).ConfigureAwait(false);
                var buffer = result.Buffer;

                while (Framing.TryReadFrame(ref buffer, out var correlationId, out var content))
                {
                    // [len][corrId] sin opId: el cliente abandono ese stream.
                    if (content.IsEmpty)
                    {
                        responses.CancelStream(correlationId);
                        continue;
                    }

                    if (content.Length < Framing.OpIdSize) throw new InvalidDataException($"Request too short: {content.Length}.");

                    // El lector avanza antes de que el handler termine: el cuerpo se copia a un
                    // buffer alquilado que vive lo que dura la peticion.
                    var op = Framing.ReadOpId(content);
                    int length = (int)content.Length - Framing.OpIdSize;
                    var body = ArrayPool<byte>.Shared.Rent(length);
                    content.Slice(Framing.OpIdSize).CopyTo(body);

                    await slots.WaitAsync(token).ConfigureAwait(false);

                    var ctx = RequestContext.Rent(correlationId, body.AsMemory(0, length), responses, token);
                    ctx.Track(body, slots, inflight);
                    ValueTask<ResponseStatus> request;
                    try { request = handlers.HandleAsync(op, ctx, token); }
                    catch (Exception ex) { request = ValueTask.FromException<ResponseStatus>(ex); }
                    ctx.Observe(request);
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
            await inflight.DrainAsync().ConfigureAwait(false);
            responses.DisposeStreams();

            await reader.CompleteAsync().ConfigureAwait(false);
            await writer.CompleteAsync().ConfigureAwait(false);
        }
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
    private static async ValueTask DispatchAsync<THandler>(THandler handlers, long op, RequestContext ctx, byte[] body, SemaphoreSlim? slots, CancellationToken token)
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
            ctx.Return();
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

/// <summary>Defecto de TCP/UDS/QUIC, como Memory: lote hasta 32 KB o hasta que el productor asincrono va a esperar.</summary>
internal readonly struct DefaultBatched : IStreamPolicy
{
    public static int Items => int.MaxValue;
    public static long MaxDelayTicks => long.MaxValue;
}

internal sealed class ResponseChannel(PipeWriter writer, bool correlated = true) : IThreadPoolWorkItem
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly FrameWriter _frames = new(writer, correlated);
    private int _flushScheduled;
    private int _flushWanted;

    // Streams en curso por corrId, para la trama de cancelacion del cliente; sin correlacion (QUIC dedicado) no hay trama.
    // Dictionary + lock: sin nodo por stream. Los CTS enlazados al token del servidor se reutilizan por conexion.
    private readonly Dictionary<int, CancellationTokenSource>? _streams = correlated ? [] : null;
    private Stack<CancellationTokenSource>? _spareStreams;

    /// <summary>Token del stream <paramref name="correlationId"/>: cancelable por el cliente y por el servidor.</summary>
    internal CancellationToken BeginStream(int correlationId, CancellationToken token, out CancellationTokenSource? cts)
    {
        if (_streams is not { } streams)
        {
            cts = null;
            return token;
        }

        lock (streams)
        {
            cts = _spareStreams is { Count: > 0 } spare ? spare.Pop() : CancellationTokenSource.CreateLinkedTokenSource(token);
            streams[correlationId] = cts;
        }

        return cts.Token;
    }

    internal void EndStream(int correlationId, CancellationTokenSource cts)
    {
        var streams = _streams!;

        lock (streams)
        {
            // Ausente: la trama de cancelacion se lo llevo; su work item lo cancela y lo libera.
            if (!streams.Remove(correlationId)) return;

            // Los CTS sobrantes siguen enlazados al token del servidor: DisposeStreams los suelta al cerrar la conexion.
            if (cts.TryReset())
            {
                (_spareStreams ??= new()).Push(cts);
                return;
            }
        }

        cts.Dispose();
    }

    /// <summary>Tras vaciar los handlers de la conexion: ya no queda ningun stream en curso.</summary>
    internal void DisposeStreams()
    {
        if (_spareStreams is not { } spare) return;
        while (spare.TryPop(out var cts)) cts.Dispose();
    }

    internal void CancelStream(int correlationId)
    {
        // Tardia (el stream ya acabo) o de un id desconocido: no hay nada que parar.
        // ponytail: si llega antes de que el handler alcance EnumerateAsync (await previo al stream), se ignora y el
        // productor corre hasta el final como antes. Cerrarlo pediria registrar toda peticion en el lector.
        if (_streams is not { } streams) return;

        CancellationTokenSource? cts;
        lock (streams) if (!streams.Remove(correlationId, out cts)) return;

        // Fuera del lector: Cancel ejecuta en linea las continuaciones del productor.
        ThreadPool.UnsafeQueueUserWorkItem(static c =>
        {
            try { c.Cancel(); }
            catch { }
            finally { c.Dispose(); }
        }, cts, preferLocal: false);
    }

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
    public ValueTask<ResponseStatus> WriteRawAsync(int correlationId, ResponseStatus status, ReadOnlyMemory<byte> body, CancellationToken token, bool deferFlush = true)
    {
        // Camino sincrono: candado libre -> sin maquina de estados por lote.
        if (!_gate.Wait(0)) return WriteRawSlowAsync(correlationId, status, body, token, deferFlush);

        bool release = true;
        try
        {
            WriteRawFrame(correlationId, status, body.Span);

            if (deferFlush && writer.CanGetUnflushedBytes && writer.UnflushedBytes < EagerFlushBytes)
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
    private async ValueTask<ResponseStatus> WriteRawSlowAsync(int correlationId, ResponseStatus status, ReadOnlyMemory<byte> body, CancellationToken token, bool deferFlush)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            WriteRawFrame(correlationId, status, body.Span);

            if (deferFlush && writer.CanGetUnflushedBytes && writer.UnflushedBytes < EagerFlushBytes)
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

/// <summary>Peticiones en curso de una conexion: contador (la lectura cuenta como una) y una unica senal de vaciado por conexion.</summary>
internal sealed class Inflight
{
    private int _count = 1;
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Enter() => Interlocked.Increment(ref _count);

    public void Exit()
    {
        if (Interlocked.Decrement(ref _count) == 0) _drained.TrySetResult();
    }

    /// <summary>Suelta la referencia de la lectura y espera a que terminen los handlers pendientes.</summary>
    public Task DrainAsync()
    {
        Exit();
        return _drained.Task;
    }
}

/// <summary>
/// Peticion en curso sobre una conexion multiplexada.
/// la peticion y usan el token de cancelacion del servidor.
/// </summary>
public sealed class RequestContext
{
    // ponytail: pool sin limite (= pico de peticiones concurrentes), mismo patron que _batches. El contexto solo es valido
    // mientras el handler no ha terminado; retenerlo despues (fire-and-forget) es un uso indebido.
    private static readonly System.Collections.Concurrent.ConcurrentBag<RequestContext> _pool = [];
    private int _correlationId;
    private ReadOnlyMemory<byte> _body;
    private ResponseChannel _responses = null!;
    private CancellationToken _token;
    private int _completed;
    private byte[]? _rented;
    private SemaphoreSlim? _slots;
    private Inflight? _inflight;
    private ValueTask<ResponseStatus> _pending;
    private Action? _onCompleted;

    private RequestContext() { }

    /// <summary>Recursos de la conexion que se liberan al terminar el handler (cuerpo alquilado, hueco y contador).</summary>
    internal void Track(byte[] body, SemaphoreSlim slots, Inflight inflight)
    {
        _rented = body;
        _slots = slots;
        _inflight = inflight;
        inflight.Enter();
    }

    /// <summary>
    /// Espera al handler sin caja ni Task propios: el contexto (ya pooled) es la continuacion, con delegado cacheado.
    /// Sustituye a DispatchAsync (caja por peticion) y al registro inflight con ContinueWith.
    /// </summary>
    internal void Observe(ValueTask<ResponseStatus> request)
    {
        if (request.IsCompleted)
        {
            Finish(request);
            return;
        }

        _pending = request;
        request.ConfigureAwait(false).GetAwaiter().UnsafeOnCompleted(_onCompleted ??= OnCompleted);
    }

    private void OnCompleted()
    {
        var request = _pending;
        _pending = default;
        Finish(request);
    }

    private void Finish(ValueTask<ResponseStatus> request)
    {
        try
        {
            request.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) when (_token.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (!IsCompleted)
        {
            _ = FailAndReleaseAsync(ex);
            return;
        }
        catch
        {
        }

        Release();
    }

    private async Task FailAndReleaseAsync(Exception ex)
    {
        try { await FailAsync(ex).ConfigureAwait(false); }
        catch { }
        finally { Release(); }
    }

    private void Release()
    {
        var (body, slots, inflight) = (_rented!, _slots, _inflight);
        _rented = null;
        _slots = null;
        _inflight = null;
        Return();
        ArrayPool<byte>.Shared.Return(body);
        slots?.Release();
        inflight?.Exit();
    }

    internal static RequestContext Rent(int correlationId, ReadOnlyMemory<byte> body, ResponseChannel responses, CancellationToken token)
    {
        var ctx = _pool.TryTake(out var pooled) ? pooled : new();
        ctx._correlationId = correlationId;
        ctx._body = body;
        ctx._responses = responses;
        ctx._token = token;
        ctx._completed = 0;
        return ctx;
    }

    /// <summary>Fin de la peticion: libera el lote y vuelve al pool.</summary>
    internal void Return()
    {
        Complete();
        _batched = 0;
        _body = default;
        _responses = null!;
        _token = default;
        _pool.Add(this);
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

    /// <summary>Responde <see cref="ResponseStatus.Success"/> con un cuerpo ya serializado ([ServerCache]).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask<ResponseStatus> ReturnRawAsync(ReadOnlyMemory<byte> body)
    {
        Complete();
        // Respuesta final: flush en linea como ReturnAsync; diferirlo anade un salto al pool por acierto de cache.
        return _responses.WriteRawAsync(_correlationId, ResponseStatus.Success, body, _token, deferFlush: false);
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

    /// <summary>Estudio #1: lo que emite el host generado; la secuencia llega directa, sin clausura. Lote por defecto.</summary>
    public ValueTask<ResponseStatus> EnumerateAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TData>(IAsyncEnumerable<TData> value)
        => EnumerateAsync<TData, DefaultBatched>(value);

    public ValueTask<ResponseStatus> EnumerateAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TData>(IEnumerable<TData> value)
        => EnumerateAsync<TData, DefaultBatched>(value);

    /// <summary>Politica fijada en el contrato (<c>[Stream(Batch, MaxDelayMs)]</c>): el host generado emite <typeparamref name="TPolicy"/> y el JIT la pliega.</summary>
    public async ValueTask<ResponseStatus> EnumerateAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TData, TPolicy>(IAsyncEnumerable<TData> value)
        where TPolicy : struct, IStreamPolicy
    {
        var token = _responses.BeginStream(_correlationId, _token, out var stream);

        try
        {
            if (TPolicy.Items == 0)
            {
                await foreach (var item in value.WithCancellation(token).ConfigureAwait(false))
                    await YieldAsync(item).ConfigureAwait(false);

                return await EndStreamingAsync().ConfigureAwait(false);
            }

            await using var e = value.GetAsyncEnumerator(token);

            while (true)
            {
                var next = e.MoveNextAsync();

                // El productor va a esperar: lo acumulado sale ya, no espera a llenar el lote.
                if (!next.IsCompleted) await FlushBatchAsync().ConfigureAwait(false);

                if (!await next.ConfigureAwait(false)) break;

                await AddToBatchAsync<TData, TPolicy>(e.Current).ConfigureAwait(false);
            }

            await FlushBatchAsync().ConfigureAwait(false);

            return await EndStreamingAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (Abandoned(stream))
        {
            return Abandon();
        }
        finally
        {
            if (stream is not null) _responses.EndStream(_correlationId, stream);
        }
    }

    public async ValueTask<ResponseStatus> EnumerateAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TData, TPolicy>(IEnumerable<TData> value)
        where TPolicy : struct, IStreamPolicy
    {
        var token = _responses.BeginStream(_correlationId, _token, out var stream);

        try
        {
            if (TPolicy.Items == 0)
            {
                foreach (var item in value)
                {
                    token.ThrowIfCancellationRequested();
                    await YieldAsync(item).ConfigureAwait(false);
                }
            }
            else
            {
                foreach (var item in value)
                {
                    token.ThrowIfCancellationRequested();
                    await AddToBatchAsync<TData, TPolicy>(item).ConfigureAwait(false);
                }

                await FlushBatchAsync().ConfigureAwait(false);
            }

            return await EndStreamingAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (Abandoned(stream))
        {
            return Abandon();
        }
        finally
        {
            if (stream is not null) _responses.EndStream(_correlationId, stream);
        }
    }

    /// <summary>Lo cancelo el cliente (trama de cancelacion), no el servidor.</summary>
    private bool Abandoned(CancellationTokenSource? stream) => stream is { IsCancellationRequested: true } && !_token.IsCancellationRequested;

    /// <summary>El cliente ya no escucha: sin respuesta final (la descartaria), solo se libera el lote.</summary>
    private ResponseStatus Abandon()
    {
        Complete();
        return ResponseStatus.Failed;
    }

    // ponytail: buffer reutilizado entre streams (mismo patron que Server.Memory); pool sin limite = streams concurrentes.
    private static readonly System.Collections.Concurrent.ConcurrentBag<ArrayBufferWriter<byte>> _batches = [];
    private ArrayBufferWriter<byte>? _batch;
    private int _batched;
    private long _batchStarted;

    private const int BatchFlushBytes = 32 * 1024;

    private ValueTask<ResponseStatus> AddToBatchAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TData, TPolicy>(TData item)
        where TPolicy : struct, IStreamPolicy
    {
        var w = _batch ??= _batches.TryTake(out var pooled) ? pooled : new(BatchFlushBytes);

        // Sin plazo (MaxDelayTicks == MaxValue) el JIT elimina la lectura del reloj.
        if (TPolicy.MaxDelayTicks != long.MaxValue && _batched == 0) _batchStarted = System.Diagnostics.Stopwatch.GetTimestamp();

        // MemoryPack delimita cada valor: el lote es la concatenacion, sin prefijo de longitud por item.
        Serialize(w, item);

        // ponytail: el plazo se comprueba al llegar cada item; un productor que bloquea sin await no se corta (sin timer).
        return ++_batched >= TPolicy.Items || w.WrittenCount >= BatchFlushBytes
            || (TPolicy.MaxDelayTicks != long.MaxValue && System.Diagnostics.Stopwatch.GetTimestamp() - _batchStarted >= TPolicy.MaxDelayTicks)
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
