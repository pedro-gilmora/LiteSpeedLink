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
    // Channel<Response> para streams; UnarySink (reutilizable) para unarias.
    private readonly ConcurrentDictionary<int, object> _pending = new();
    private readonly ConcurrentBag<UnarySink> _sinks = [];
    private readonly string _remote;
    private readonly Task _readLoop;
    private int _nextId;
    private Exception? _fault;

    private readonly record struct Response(ResponseStatus Status, byte[] Body, int Length) : IDisposable
    {
        public ReadOnlySpan<byte> Span => Body.AsSpan(0, Length);

        public void Dispose() => ArrayPool<byte>.Shared.Return(Body);
    }

    private readonly bool _coalesce;

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

        var (id, _) = await SendRequestAsync(op, payload, hasPayload, sink, token).ConfigureAwait(false);

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

    public async IAsyncEnumerable<TOut?> EnumerateAsync<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TIn,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TOut>(long op, TIn? payload, bool hasPayload, [EnumeratorCancellation] CancellationToken token)
    {
        var (id, responses) = await SendRequestAsync(op, payload, hasPayload, null, token).ConfigureAwait(false);

        try
        {
            while (true)
            {
                var response = await responses!.Reader.ReadAsync(token).ConfigureAwait(false);

                if (response.Status is ResponseStatus.Batch)
                {
                    using (response)
                    {
                        for (int offset = 0; offset < response.Length;)
                        {
                            TOut? batchItem = default;
                            offset += Deserialize(response.Body.AsSpan(offset, response.Length - offset), ref batchItem);
                            yield return batchItem;
                        }
                    }

                    continue;
                }

                TOut? item;

                using (response)
                {
                    if (response.Status is ResponseStatus.StreamEnd) yield break;

                    EnsureSuccess(response);

                    item = Deserialize<TOut>(response.Span);
                }

                yield return item;
            }
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private async ValueTask<(int, Channel<Response>?)> SendRequestAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TIn>(
        long op, TIn? payload, bool hasPayload, UnarySink? sink, CancellationToken token)
    {
        if (_fault is { } fault) throw new IOException($"Connection to {_remote} is closed.", fault);

        // 0 queda reservado como "desarmado" en UnarySink.
        int id;
        do id = Interlocked.Increment(ref _nextId); while (id == 0);

        Channel<Response>? responses = null;

        if (sink is null)
        {
            // ponytail: el consumidor del stream corre inline en el lector (-90 us/1000 items, como UnsafePreferInlineScheduling de Kestrel).
            // Techo: una llamada sincrona (GetAwaiter().GetResult()) dentro del await foreach bloquea al lector -> interbloqueo; usar await.
            responses = Channel.CreateUnbounded<Response>(new() { SingleReader = true, SingleWriter = true, AllowSynchronousContinuations = true });
            _pending[id] = responses;
        }
        else
        {
            sink.Arm(id);
            _pending[id] = sink;
        }

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

        return (id, responses);
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
            Channel<Response> responses => responses.Writer.TryWrite(response),
            UnarySink sink => sink.TrySet(id, response),
            _ => false,
        };

        if (!delivered) response.Dispose();
    }

    private void Fail(Exception ex)
    {
        _fault = ex;

        foreach (var (_, pending) in _pending)
        {
            if (pending is Channel<Response> responses) responses.Writer.TryComplete(ex);
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
        await _reader.CompleteAsync().ConfigureAwait(false);

        try { await _readLoop.ConfigureAwait(false); } catch { }
    }
}
