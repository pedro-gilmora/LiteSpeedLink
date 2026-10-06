using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
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
    private readonly FrameWriter _frames;
    private readonly SemaphoreSlim _gate = new(1, 1);
    // StreamSink para streams; UnarySink para unarias; ambos reutilizables.
    private readonly ConcurrentDictionary<int, object> _pending = new();
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
        _frames = new(writer);
        _remote = remote;
        _readLoop = ReadLoopAsync();
    }

    public async ValueTask<TOut?> GetAsync<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TIn,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TOut>(long op, TIn? payload, bool hasPayload, CancellationToken token)
    {
        using var response = await UnaryAsync(op, payload, hasPayload, token).ConfigureAwait(false);

        EnsureSuccess(response);

        return Deserialize<TOut>(response.Span);
    }

    public async ValueTask<ReadOnlyMemory<byte>> GetRawAsync(long op, ReadOnlyMemory<byte> request, CancellationToken token)
    {
        using var response = await UnaryAsync(op, new RawBody(request), true, token).ConfigureAwait(false);

        EnsureSuccess(response);

        // Copia unica: el cuerpo vive en un buffer del pool que se devuelve al salir.
        return response.Span.ToArray();
    }

    public async ValueTask SendAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TIn>(long op, TIn? payload, bool hasPayload, CancellationToken token)
    {
        using var response = await UnaryAsync(op, payload, hasPayload, token).ConfigureAwait(false);

        EnsureSuccess(response);
    }

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
            _pending.TryRemove(new(id, sink));
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

        public void Cancel(CancellationToken token) => TryFail(Volatile.Read(ref _armed), new OperationCanceledException(token));

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
    /// Unico objeto por llamada (antes: dos iteradores async con su caja); el sink es pooled. Perezoso como el iterador:
    /// la peticion sale en el primer <c>MoveNextAsync</c>. Los items de un lote se decodifican sin esperar; solo se entra
    /// en un metodo async (caja pooled) para enviar o cuando el sink esta vacio.
    /// </summary>
    private sealed class StreamEnumerable<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TIn,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TOut>(
        MultiplexedChannel channel, long op, TIn? payload, bool hasPayload, CancellationToken token)
        : IAsyncEnumerable<TOut?>, IAsyncEnumerator<TOut?>
    {
        private bool _opened, _done, _held;
        private CancellationToken _token = token;
        private CancellationTokenSource? _linked;
        private StreamSink? _sink;
        private int _id;
        private CancellationTokenRegistration _cancel;
        private Response _batch;
        private int _offset;
        private TOut? _current;

        public IAsyncEnumerator<TOut?> GetAsyncEnumerator(CancellationToken enumeratorToken = default)
        {
            // Re-enumeracion: peticion nueva, como un iterador async.
            if (_opened) return new StreamEnumerable<TIn, TOut>(channel, op, payload, hasPayload, token).GetAsyncEnumerator(enumeratorToken);

            _opened = true;

            // Mismo criterio que [EnumeratorCancellation]: se enlazan solo si ambos pueden cancelar.
            if (enumeratorToken.CanBeCanceled)
            {
                if (!_token.CanBeCanceled) _token = enumeratorToken;
                else if (_token != enumeratorToken) _token = (_linked = CancellationTokenSource.CreateLinkedTokenSource(_token, enumeratorToken)).Token;
            }

            return this;
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
                        _done = true;
                        more = false;
                        return true;
                    }

                    channel.EnsureSuccess(response);
                    _current = Deserialize<TOut>(response.Span);
                }

                return more = true;
            }
        }

        public ValueTask DisposeAsync()
        {
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
                channel._pending.TryRemove(new(_id, sink));
                sink.Disarm();
                channel._streams.Add(sink);
            }

            _linked?.Dispose();
            _linked = null;
            return default;
        }
    }

    private async ValueTask<int> SendRequestAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TIn>(
        long op, TIn? payload, bool hasPayload, object sink, CancellationToken token)
    {
        if (_fault is { } fault) throw new IOException($"Connection to {_remote} is closed.", fault);

        // 0 queda reservado como "desarmado" en UnarySink.
        int id;
        do id = Interlocked.Increment(ref _nextId); while (id == 0);

        if (sink is UnarySink unary) unary.Arm(id);
        else ((StreamSink)sink).Arm(id);

        _pending[id] = sink;

        try
        {
            await _gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                Framing.WriteOpId(_frames.BeginFrame(id, Framing.OpIdSize), op);

                if (hasPayload)
                {
                    try
                    {
                        if (typeof(TIn) == typeof(RawBody)) _frames.Write(Unsafe.As<TIn, RawBody>(ref payload!).Bytes.Span);
                        else Serialize(_frames, payload);
                    }
                    catch (Exception ex)
                    {
                        // La trama ya esta a medias en el writer: la conexion queda inservible.
                        await _writer.CompleteAsync(ex).ConfigureAwait(false);
                        throw new ArgumentException("Invalid parameters", ex);
                    }
                }

                _frames.EndFrame();

                // Sin token: cancelar un flush a medias partiria la trama.
                await _writer.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }
        catch
        {
            _pending.TryRemove(id, out _);
            throw;
        }

        return id;
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
        bool delivered = _pending.TryGetValue(id, out var pending) && pending switch
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

        foreach (var (id, pending) in _pending)
        {
            if (pending is StreamSink stream) stream.TryFail(id, ex);
            else ((UnarySink)pending).TryFail(ex);
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
