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
    private readonly ConcurrentDictionary<int, Channel<Response>> _pending = new();
    private readonly string _remote;
    private readonly Task _readLoop;
    private int _nextId;
    private Exception? _fault;

    private readonly record struct Response(ResponseStatus Status, byte[] Body, int Length)
    {
        public ReadOnlySpan<byte> Span => Body.AsSpan(0, Length);

        public void Release() => ArrayPool<byte>.Shared.Return(Body);
    }

    public MultiplexedChannel(PipeReader reader, PipeWriter writer, string remote)
    {
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
        var (id, responses) = await SendRequestAsync(op, payload, hasPayload, token).ConfigureAwait(false);

        try
        {
            var response = await responses.Reader.ReadAsync(token).ConfigureAwait(false);

            try
            {
                EnsureSuccess(response);

                return Deserialize<TOut>(response.Span);
            }
            finally
            {
                response.Release();
            }
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    public async ValueTask SendAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TIn>(long op, TIn? payload, bool hasPayload, CancellationToken token)
    {
        var (id, responses) = await SendRequestAsync(op, payload, hasPayload, token).ConfigureAwait(false);

        try
        {
            var response = await responses.Reader.ReadAsync(token).ConfigureAwait(false);

            try
            {
                EnsureSuccess(response);
            }
            finally
            {
                response.Release();
            }
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    public async IAsyncEnumerable<TOut?> EnumerateAsync<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TIn,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TOut>(long op, TIn? payload, bool hasPayload, [EnumeratorCancellation] CancellationToken token)
    {
        var (id, responses) = await SendRequestAsync(op, payload, hasPayload, token).ConfigureAwait(false);

        try
        {
            while (true)
            {
                var response = await responses.Reader.ReadAsync(token).ConfigureAwait(false);
                TOut? item;

                try
                {
                    if (response.Status is ResponseStatus.StreamEnd) yield break;

                    EnsureSuccess(response);

                    item = Deserialize<TOut>(response.Span);
                }
                finally
                {
                    response.Release();
                }

                yield return item;
            }
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private async ValueTask<(int, Channel<Response>)> SendRequestAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TIn>(
        long op, TIn? payload, bool hasPayload, CancellationToken token)
    {
        if (_fault is { } fault) throw new IOException($"Connection to {_remote} is closed.", fault);

        int id = Interlocked.Increment(ref _nextId);

        var responses = Channel.CreateUnbounded<Response>(new() { SingleReader = true, SingleWriter = true });

        _pending[id] = responses;

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
                        Serialize(_frames, payload);
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

                while (Framing.TryReadFrame(ref buffer, out var id, out var content))
                {
                    var status = Framing.ReadStatus(content);
                    int length = (int)content.Length - Framing.StatusSize;
                    var body = ArrayPool<byte>.Shared.Rent(length);

                    content.Slice(Framing.StatusSize).CopyTo(body);

                    // Una respuesta sin destinatario pertenece a una llamada ya cancelada.
                    if (!_pending.TryGetValue(id, out var responses) || !responses.Writer.TryWrite(new(status, body, length)))
                        ArrayPool<byte>.Shared.Return(body);
                }

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

    private void Fail(Exception ex)
    {
        _fault = ex;

        foreach (var (_, responses) in _pending)
            responses.Writer.TryComplete(ex);
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
