using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Channels;

namespace SourceCrafter.LiteSpeedLink.Client;

/// <summary>
/// Cliente de una conexion multiplexada: cada peticion lleva un corrId y un unico lector reparte
/// las respuestas <c>[len][corrId][estado][cuerpo]</c> a quien las espera, de modo que las
/// llamadas concurrentes nunca reciben una respuesta ajena.
/// </summary>
internal sealed class MultiplexedChannel
{
    private readonly PipeReader _reader;
    private readonly PipeWriter _writer;
    // Escritura agrupada: cada llamada encola su trama bajo _send y solo una (la que encuentra _flushing a false)
    // la vuelca al PipeWriter y hace flush; las demas vuelven sin suspender y salen en ese mismo envio.
    private readonly Lock _send = new();
    private ArrayBufferWriter<byte> _queued = new(256), _spare = new(256);
    private bool _flushing;
    private Exception? _broken;
    // StreamSink para streams; UnarySink para unarias; ambos reutilizables. Slot = id & SlotMask; el ocupante
    // puede ser otra llamada con los mismos bits bajos: el sink compara su id armado antes de aceptar nada.
    // ponytail: tope de 4096 llamadas en vuelo por conexion (como MemChannel); llena -> InvalidOperationException. Subir SlotBits si hiciera falta.
    private const int SlotBits = 12, SlotMask = (1 << SlotBits) - 1;
    private readonly object?[] _pending = new object?[1 << SlotBits];
    private readonly ConcurrentBag<UnarySink> _sinks = [];
    private readonly ConcurrentBag<StreamSink> _streams = [];
    private readonly string _remote;
    private readonly Task _readLoop;
    private int _nextId;
    private Exception? _fault;
    private readonly bool _coalesce;

    private readonly record struct Response(ResponseStatus Status, byte[] Body, int Length) : IDisposable
    {
        public ReadOnlySpan<byte> Span => Body.AsSpan(0, Length);

        public void Dispose() => ArrayPool<byte>.Shared.Return(Body);
    }

    public MultiplexedChannel(PipeReader reader, PipeWriter writer, string remote, bool coalesce = false)
    {
        _coalesce = coalesce;
        _reader = reader;
        _writer = writer;
        _remote = remote;
        _readLoop = ReadLoopAsync();
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    public async ValueTask<TOut?> GetAsync<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TIn,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TOut>(long op, TIn? payload, bool hasPayload, CancellationToken token)
    {
        using var response = await UnaryAsync(op, payload, hasPayload, token).ConfigureAwait(false);

        EnsureSuccess(response);

        return Deserialize<TOut>(response.Span);
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    public async ValueTask<TOut> GetRawAsync<TState, TOut>(long op, TState state, Action<IBufferWriter<byte>, TState> write, RawReader<TOut> read, CancellationToken token)
    {
        using var response = await UnaryAsync(op, new RawBody<TState>(state, write), true, token).ConfigureAwait(false);

        EnsureSuccess(response);

        // Se decodifica sobre el buffer del pool antes de devolverlo: sin copia.
        return read(response.Span);
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
    public async ValueTask SendAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TIn>(long op, TIn? payload, bool hasPayload, CancellationToken token)
    {
        using var response = await UnaryAsync(op, payload, hasPayload, token).ConfigureAwait(false);

        EnsureSuccess(response);
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<Response> UnaryAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TIn>(long op, TIn? payload, bool hasPayload, CancellationToken token)
    {
        var sink = _sinks.TryTake(out var pooled) ? pooled : new();

        int id = await SendRequestAsync(op, payload, hasPayload, sink, token).ConfigureAwait(false);

        try
        {
            // Dispose espera al callback: tras el using ningun cancelado tardio toca el sink.
            using (token.UnsafeRegister(static (s, t) => ((UnarySink)s!).TryFail(new OperationCanceledException(t)), sink))
                return await sink.Task.ConfigureAwait(false);
        }
        finally
        {
            Unregister(id, sink);
            // Consumido (resultado, fallo o cancelacion): el id del armado impide que una entrega tardia lo toque.
            _sinks.Add(sink);
        }
    }

    /// <summary>Respuesta unaria sin canal: un unico SetResult por armado; el estado guarda el id armado.</summary>
    private sealed class UnarySink : System.Threading.Tasks.Sources.IValueTaskSource<Response>
    {
        private System.Threading.Tasks.Sources.ManualResetValueTaskSourceCore<Response> _core = new() { RunContinuationsAsynchronously = true };
        private int _armed;

        public ValueTask<Response> Task => new(this, _core.Version);

        public void Arm(int id)
        {
            _core.Reset();
            Volatile.Write(ref _armed, id);
        }

        public bool TrySet(int id, Response response)
        {
            if (Interlocked.CompareExchange(ref _armed, 0, id) != id) return false;
            _core.SetResult(response);
            return true;
        }

        public void TryFail(Exception ex)
        {
            int id = Volatile.Read(ref _armed);
            if (id != 0 && Interlocked.CompareExchange(ref _armed, 0, id) == id) _core.SetException(ex);
        }

        public Response GetResult(short token) => _core.GetResult(token);
        public System.Threading.Tasks.Sources.ValueTaskSourceStatus GetStatus(short token) => _core.GetStatus(token);
        public void OnCompleted(Action<object?> continuation, object? state, short token, System.Threading.Tasks.Sources.ValueTaskSourceOnCompletedFlags flags)
            => _core.OnCompleted(continuation, state, token, flags);
    }

    /// <summary>
    /// Cola de un stream reutilizable entre streams (antes: un Channel por stream). La cola conserva su capacidad;
    /// el id armado descarta entregas tardias de un stream anterior.
    /// </summary>
    private sealed class StreamSink : System.Threading.Tasks.Sources.IValueTaskSource<bool>
    {
        // ponytail: el consumidor corre inline en el lector (como AllowSynchronousContinuations del Channel previo).
        // Techo: una llamada sincrona dentro del await foreach bloquea al lector -> interbloqueo; usar await.
        private System.Threading.Tasks.Sources.ManualResetValueTaskSourceCore<bool> _core = new() { RunContinuationsAsynchronously = false };
        private readonly Queue<Response> _items = new();
        private int _armed;
        private bool _waiting;
        private Exception? _error;

        public void Arm(int id)
        {
            lock (_items) _armed = id;
        }

        public bool TryWrite(int id, Response response)
        {
            lock (_items)
            {
                if (_armed != id || _error is not null) return false;
                _items.Enqueue(response);
                if (!_waiting) return true;
                _waiting = false;
            }

            _core.SetResult(true);
            return true;
        }

        public void TryFail(int id, Exception ex)
        {
            lock (_items)
            {
                if (id == 0 || _armed != id || _error is not null) return;
                _error = ex;
                if (!_waiting) return;
                _waiting = false;
            }

            _core.SetResult(false);
        }

        public void Cancel(CancellationToken token) => TryFail(new OperationCanceledException(token));

        public void TryFail(Exception ex) => TryFail(Volatile.Read(ref _armed), ex);

        /// <summary>Lo pendiente sale antes que el error, como al completar un Channel con excepcion.</summary>
        public bool TryRead(out Response response)
        {
            lock (_items)
            {
                if (_items.TryDequeue(out response)) return true;
                if (_error is { } error) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(error);
                _core.Reset();
                _waiting = true;
                return false;
            }
        }

        public ValueTask<bool> WaitAsync() => new(this, _core.Version);

        public void Disarm()
        {
            lock (_items)
            {
                _armed = 0;
                _waiting = false;
                _error = null;
                while (_items.TryDequeue(out var left)) left.Dispose();
            }
        }

        public bool GetResult(short token) => _core.GetResult(token);
        public System.Threading.Tasks.Sources.ValueTaskSourceStatus GetStatus(short token) => _core.GetStatus(token);
        public void OnCompleted(Action<object?> continuation, object? state, short token, System.Threading.Tasks.Sources.ValueTaskSourceOnCompletedFlags flags)
            => _core.OnCompleted(continuation, state, token, flags);
    }

    public IAsyncEnumerable<TOut?> EnumerateAsync<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TIn,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TOut>(long op, TIn? payload, bool hasPayload, CancellationToken token)
        => new StreamEnumerable<TIn, TOut>(this, op, payload, hasPayload, token);

    /// <summary>
    /// Enumerable ligero sin pool (el consumidor puede guardarlo y reenumerarlo): cada <c>GetAsyncEnumerator</c> alquila un
    /// <see cref="StreamEnumerator{TIn, TOut}"/> del pool. Perezoso como el iterador: la peticion sale en el primer <c>MoveNextAsync</c>.
    /// </summary>
    private sealed class StreamEnumerable<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TIn,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TOut>(
        MultiplexedChannel channel, long op, TIn? payload, bool hasPayload, CancellationToken token)
        : IAsyncEnumerable<TOut?>
    {
        public IAsyncEnumerator<TOut?> GetAsyncEnumerator(CancellationToken enumeratorToken = default)
            => StreamEnumerator<TIn, TOut>.Rent(channel, op, payload, hasPayload, token, enumeratorToken);
    }

    /// <summary>
    /// Estado de una enumeracion; vuelve al pool (por tipo) en <c>DisposeAsync</c>. Los items de un lote se decodifican sin
    /// esperar; solo se entra en un metodo async (caja pooled) para enviar o cuando el sink esta vacio.
    /// </summary>
    // ponytail: usar el enumerador tras DisposeAsync toca la enumeracion que lo reutilice; await foreach nunca lo hace.
    private sealed class StreamEnumerator<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TIn,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TOut> : IAsyncEnumerator<TOut?>
    {
        private static readonly ConcurrentBag<StreamEnumerator<TIn, TOut>> s_pool = [];

        private MultiplexedChannel channel = null!;
        private long op;
        private TIn? payload;
        private bool hasPayload, _rented, _done, _held, _ended;
        private CancellationToken _token;
        private CancellationTokenSource? _linked;
        private StreamSink? _sink;
        private int _id;
        private CancellationTokenRegistration _cancel;
        private Response _batch;
        private int _offset;
        private TOut? _current;

        public static StreamEnumerator<TIn, TOut> Rent(MultiplexedChannel channel, long op, TIn? payload, bool hasPayload, CancellationToken token, CancellationToken enumeratorToken)
        {
            var e = s_pool.TryTake(out var pooled) ? pooled : new();
            (e.channel, e.op, e.payload, e.hasPayload, e._token) = (channel, op, payload, hasPayload, token);
            e._rented = true;
            e._done = false;
            e._ended = false;

            // Mismo criterio que [EnumeratorCancellation]: se enlazan solo si ambos pueden cancelar.
            if (enumeratorToken.CanBeCanceled)
            {
                if (!token.CanBeCanceled) e._token = enumeratorToken;
                else if (token != enumeratorToken) e._token = (e._linked = CancellationTokenSource.CreateLinkedTokenSource(token, enumeratorToken)).Token;
            }

            return e;
        }

        public TOut? Current => _current;

        public ValueTask<bool> MoveNextAsync()
        {
            if (_done) return new(false);
            if (_sink is null) return MoveNextSlowAsync(armed: false);

            try
            {
                // false = sink armado: esperar antes de volver a leer (un TryRead extra reiniciaria el core con un SetResult en vuelo).
                return TryNext(_sink, out bool more) ? new(more) : MoveNextSlowAsync(armed: true);
            }
            catch (Exception ex)
            {
                _done = true;
                return ValueTask.FromException<bool>(ex);
            }
        }

        [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
        private async ValueTask<bool> MoveNextSlowAsync(bool armed)
        {
            try
            {
                if (_sink is null)
                {
                    var sink = channel._streams.TryTake(out var pooled) ? pooled : new();
                    _id = await channel.SendRequestAsync(op, payload, hasPayload, sink, _token).ConfigureAwait(false);
                    _sink = sink;
                    // Se libera en DisposeAsync antes de devolver el sink al pool.
                    _cancel = _token.UnsafeRegister(static (s, t) => ((StreamSink)s!).Cancel(t), sink);
                }

                if (armed) await _sink.WaitAsync().ConfigureAwait(false);

                while (!TryNext(_sink, out _)) await _sink.WaitAsync().ConfigureAwait(false);

                return !_done;
            }
            catch
            {
                _done = true;
                throw;
            }
        }

        /// <summary>true si el resultado ya se conoce (item o fin); false si el sink quedo armado para esperar.</summary>
        private bool TryNext(StreamSink sink, out bool more)
        {
            while (true)
            {
                if (_held)
                {
                    if (_offset < _batch.Length)
                    {
                        // default antes: con ref, MemoryPack rellenaria la instancia ya entregada.
                        _current = default;
                        _offset += Deserialize(_batch.Body.AsSpan(_offset, _batch.Length - _offset), ref _current);
                        return more = true;
                    }

                    _held = false;
                    _batch.Dispose();
                }

                if (!sink.TryRead(out var response)) return more = false;

                if (response.Status is ResponseStatus.Batch)
                {
                    (_batch, _offset, _held) = (response, 0, true);
                    continue;
                }

                using (response)
                {
                    if (response.Status is ResponseStatus.StreamEnd)
                    {
                        _done = _ended = true;
                        more = false;
                        return true;
                    }

                    // Failed/NotFound tambien cierran el stream en el host: no hace falta avisarle.
                    _ended = response.Status is not ResponseStatus.Success;
                    channel.EnsureSuccess(response);
                    _current = Deserialize<TOut>(response.Span);
                }

                return more = true;
            }
        }

        public ValueTask DisposeAsync()
        {
            // Idempotente: un segundo Dispose no devuelve dos veces la instancia al pool.
            if (!_rented) return default;
            _rented = false;
            _done = true;

            if (_held)
            {
                _held = false;
                _batch.Dispose();
            }

            if (_sink is { } sink)
            {
                _sink = null;
                _cancel.Dispose();
                // Abandonado antes del final (break, cancelacion, fallo local): el productor del host para.
                if (!_ended) channel.CancelStream(_id);
                channel.Unregister(_id, sink);
                sink.Disarm();
                channel._streams.Add(sink);
            }

            _linked?.Dispose();
            _linked = null;
            (channel, payload, _current, _token) = (null!, default, default, default);
            s_pool.Add(this);
            return default;
        }
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<int> SendRequestAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TIn>(
        long op, TIn? payload, bool hasPayload, object sink, CancellationToken token)
    {
        if (_fault is { } fault) throw new IOException($"Connection to {_remote} is closed.", fault);
        token.ThrowIfCancellationRequested();

        int id = Register(sink);
        Exception? invalid = null;
        bool flush;

        lock (_send)
        {
            var queued = _queued;
            int start = queued.WrittenCount;
            const int header = Framing.FrameHeaderSize + Framing.OpIdSize;
            Framing.WriteOpId(queued.GetSpan(header)[Framing.FrameHeaderSize..header], op);
            queued.Advance(header);

            if (hasPayload)
            {
                try
                {
                    if (payload is IRawBody) ((IRawBody)payload).WriteTo(queued);
                    else Serialize(queued, payload);
                }
                catch (Exception ex)
                {
                    // La trama queda a medias en la cola: la conexion queda inservible (el flusher la cierra).
                    _broken ??= ex;
                    invalid = ex;
                }
            }

            // Por posicion: el buffer pudo realojarse al serializar.
            if (invalid is null)
            {
                var frame = MemoryMarshal.AsMemory(queued.WrittenMemory).Span[start..];
                Framing.WriteFrameHeader(frame, id, frame.Length - Framing.FrameHeaderSize);
            }

            flush = !_flushing;
            _flushing = true;
        }

        try
        {
            if (flush) await FlushQueuedAsync().ConfigureAwait(false);
        }
        catch when (invalid is null)
        {
            Unregister(id, sink);
            throw;
        }
        catch
        {
            // Se informa la causa (parametros invalidos), abajo.
        }

        if (invalid is not null)
        {
            Unregister(id, sink);
            throw new ArgumentException("Invalid parameters", invalid);
        }

        return id;
    }

    /// <summary>Vacia la cola hasta dejarla vacia; un fallo de envio cae a todas las llamadas en curso.</summary>
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
    private async ValueTask FlushQueuedAsync()
    {
        try
        {
            while (true)
            {
                ArrayBufferWriter<byte> batch;
                Exception? broken;

                lock (_send)
                {
                    if (_broken is null && _queued.WrittenCount == 0)
                    {
                        _flushing = false;
                        return;
                    }

                    batch = _queued;
                    (_queued, _spare) = (_spare, _queued);
                    broken = _broken;
                }

                if (broken is not null)
                {
                    await _writer.CompleteAsync(broken).ConfigureAwait(false);
                    throw new IOException($"Connection to {_remote} is closed.", broken);
                }

                _writer.Write(batch.WrittenSpan);
                batch.ResetWrittenCount();

                // Sin token: cancelar un flush a medias partiria la trama.
                await _writer.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            lock (_send)
            {
                _queued.ResetWrittenCount();
                _flushing = false;
            }

            Fail(ex);
            throw;
        }
    }

    private int Register(object sink)
    {
        for (int tries = 0; tries <= SlotMask + 1; tries++)
        {
            // 0 queda reservado como "desarmado" en los sinks.
            int id = Interlocked.Increment(ref _nextId);
            if (id == 0) continue;

            // Armado antes de publicarlo: quien lo vea en el slot ya puede comparar el id. Slot ocupado (p. ej. stream largo) -> id siguiente.
            if (sink is UnarySink unary) unary.Arm(id);
            else ((StreamSink)sink).Arm(id);

            if (Interlocked.CompareExchange(ref _pending[id & SlotMask], sink, null) is null) return id;
        }

        throw new InvalidOperationException($"Too many calls in flight on {_remote} (max {SlotMask + 1}).");
    }

    private void Unregister(int id, object sink) => Interlocked.CompareExchange(ref _pending[id & SlotMask], null, sink);

    /// <summary>
    /// Trama <c>[len=4][corrId]</c> sin opId (toda peticion lleva uno): el host cancela el productor de ese stream.
    /// Va por la cola de envio, detras de la peticion si aun no salio. Best-effort: sin conexion no hay productor que parar.
    /// </summary>
    private void CancelStream(int id)
    {
        if (_fault is not null) return;
        bool flush;

        lock (_send)
        {
            if (_broken is not null) return;
            Framing.WriteFrameHeader(_queued.GetSpan(Framing.FrameHeaderSize), id, 0);
            _queued.Advance(Framing.FrameHeaderSize);
            flush = !_flushing;
            _flushing = true;
        }

        if (flush) _ = FlushCancelAsync();
    }

    private async Task FlushCancelAsync()
    {
        // Un fallo de envio ya cae a las llamadas en curso dentro de FlushQueuedAsync.
        try { await FlushQueuedAsync().ConfigureAwait(false); }
        catch { }
    }

    private async Task ReadLoopAsync()
    {
        await Task.Yield();

        try
        {
            while (true)
            {
                var result = await _reader.ReadAsync().ConfigureAwait(false);
                var buffer = result.Buffer;
                Group group = default;

                while (Framing.TryReadFrame(ref buffer, out var id, out var content))
                {
                    var status = Framing.ReadStatus(content);
                    int length = (int)content.Length - Framing.StatusSize;

                    if (_coalesce && status is ResponseStatus.Success)
                    {
                        if (group.Body is not null && group.Id != id) Publish(ref group);

                        // Cota: cada trama restante trae >= 9 B de cabecera y aqui no se anade nada por item.
                        group.Body ??= ArrayPool<byte>.Shared.Rent(length + (int)buffer.Length);
                        group.Id = id;
                        content.Slice(Framing.StatusSize).CopyTo(group.Body.AsSpan(group.Length));
                        group.Length += length;
                        group.Count++;
                        continue;
                    }

                    Publish(ref group);

                    var body = ArrayPool<byte>.Shared.Rent(length);

                    content.Slice(Framing.StatusSize).CopyTo(body);

                    Deliver(id, new(status, body, length));
                }

                Publish(ref group);

                _reader.AdvanceTo(buffer.Start, buffer.End);

                if (result.IsCompleted || result.IsCanceled) break;
            }

            Fail(new IOException($"Connection to {_remote} was closed by the server."));
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
    }

    private struct Group
    {
        public int Id, Length, Count;
        public byte[]? Body;
    }

    /// <summary>Un solo item sale como Success (unarias intactas); varios, como un Batch de items MemoryPack concatenados.</summary>
    private void Publish(ref Group group)
    {
        if (group.Body is not { } body) return;

        Deliver(group.Id, new(group.Count == 1 ? ResponseStatus.Success : ResponseStatus.Batch, body, group.Length));

        group = default;
    }

    private void Deliver(int id, Response response)
    {
        // Una respuesta sin destinatario pertenece a una llamada ya cancelada.
        bool delivered = Volatile.Read(ref _pending[id & SlotMask]) switch
        {
            StreamSink stream => stream.TryWrite(id, response),
            UnarySink sink => sink.TrySet(id, response),
            _ => false,
        };

        if (!delivered) response.Dispose();
    }

    private void Fail(Exception ex)
    {
        _fault = ex;

        for (int i = 0; i < _pending.Length; i++)
        {
            var pending = Volatile.Read(ref _pending[i]);
            if (pending is StreamSink stream) stream.TryFail(ex);
            else (pending as UnarySink)?.TryFail(ex);
        }
    }

    private void EnsureSuccess(in Response response)
    {
        if (response.Status is not ResponseStatus.Success) throw ResponseError.Create(response.Status, _remote, response.Span);
    }

    public async ValueTask DisposeAsync()
    {
        await _writer.CompleteAsync().ConfigureAwait(false);

        // Desbloquea el ReadAsync pendiente aunque el socket siga abierto; el lector se completa cuando el bucle ha salido.
        _reader.CancelPendingRead();
        try { await _readLoop.ConfigureAwait(false); } catch { }

        await _reader.CompleteAsync().ConfigureAwait(false);
    }
}
