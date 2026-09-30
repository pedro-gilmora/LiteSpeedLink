using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.IO.Pipelines;
using System.Net.Quic;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;

namespace SourceCrafter.LiteSpeedLink.Client;

/// <summary>
/// Un stream bidireccional por RPC: el stream empareja peticion y respuesta, asi que no viaja
/// corrId. Peticion <c>[opId][cuerpo]</c> delimitada por FIN; respuestas <c>[len][estado][cuerpo]</c>.
/// </summary>
[RequiresPreviewFeatures]
[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
public sealed class QuicConnection(QuicClientConnectionOptions options, int unaryStreams = QuicConnection.DefaultUnaryStreams) : IAsyncConnection, IAsyncDisposable
{
    /// <summary>Streams reutilizables para unarias (0 = un stream por llamada). Medido: -35% latencia, -69% memoria.</summary>
    public const int DefaultUnaryStreams = 4;

    /// <summary>Primer opId de un stream reutilizable: el servidor lo atiende como canal multiplexado (8 bytes una vez por stream).</summary>
    internal const long MuxMarker = long.MinValue;

    internal System.Net.Quic.QuicConnection? connection;
    private readonly SemaphoreSlim _init = new(1, 1);
    // Unarias sobre N streams multiplexados; los streams del usuario siguen con stream propio (aislamiento QUIC).
    private readonly Task<MultiplexedChannel>?[] _mux = new Task<MultiplexedChannel>?[unaryStreams];
    private int _next;

    public async ValueTask DisposeAsync()
    {
        foreach (var t in _mux)
            if (t is { IsCompletedSuccessfully: true }) await t.Result.DisposeAsync().ConfigureAwait(false);
        if (connection is not null)
            await connection.DisposeAsync();
    }

    private ValueTask<MultiplexedChannel> GetMuxAsync(CancellationToken token)
    {
        int i = (int)((uint)Interlocked.Increment(ref _next) % (uint)_mux.Length);
        var t = Volatile.Read(ref _mux[i]);
        if (t is { IsCompletedSuccessfully: true }) return new(t.Result);
        if (t is null or { IsFaulted: true } or { IsCanceled: true })
        {
            var created = OpenMuxAsync();
            t = Interlocked.CompareExchange(ref _mux[i], created, t) == t ? created : Volatile.Read(ref _mux[i])!;
        }
        return new(t.WaitAsync(token));
    }

    private async Task<MultiplexedChannel> OpenMuxAsync()
    {
        var conn = await TryInitializeAsync(default).ConfigureAwait(false);
        var stream = await conn.OpenOutboundStreamAsync(QuicStreamType.Bidirectional).ConfigureAwait(false);
        var writer = PipeWriter.Create(stream);
        Framing.WriteOpId(writer.GetSpan(Framing.OpIdSize), MuxMarker);
        writer.Advance(Framing.OpIdSize);
        await writer.FlushAsync().ConfigureAwait(false);
        return new(PipeReader.Create(stream), writer, options.RemoteEndPoint.ToString()!);
    }

    internal async ValueTask<System.Net.Quic.QuicConnection> TryInitializeAsync(CancellationToken token)
    {
        if (Volatile.Read(ref connection) is { } ready) return ready;

        await _init.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return connection ??= await System.Net.Quic.QuicConnection.ConnectAsync(options, token).ConfigureAwait(false);
        }
        finally
        {
            _init.Release();
        }
    }

    public async ValueTask<TOut?> GetAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TIn, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TOut>
        (long op, TIn payload, CancellationToken token = default, [CallerMemberName] string name = "")
    {
        if (_mux.Length > 0) return await (await GetMuxAsync(token).ConfigureAwait(false)).GetAsync<TIn, TOut>(op, payload, true, token).ConfigureAwait(false);
        await using var call = await StartAsync(op, payload, true, token).ConfigureAwait(false);
        return await call.ReadValueAsync<TOut>(token).ConfigureAwait(false);
    }

    public async ValueTask<TOut?> GetAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TOut>
        (long op, CancellationToken token = default, [CallerMemberName] string name = "")
    {
        if (_mux.Length > 0) return await (await GetMuxAsync(token).ConfigureAwait(false)).GetAsync<byte, TOut>(op, default, false, token).ConfigureAwait(false);
        await using var call = await StartAsync<byte>(op, default, false, token).ConfigureAwait(false);
        return await call.ReadValueAsync<TOut>(token).ConfigureAwait(false);
    }

    public async ValueTask SendAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TIn>
        (long op, TIn payload, CancellationToken token = default, [CallerMemberName] string name = "")
    {
        if (_mux.Length > 0) { await (await GetMuxAsync(token).ConfigureAwait(false)).SendAsync(op, payload, true, token).ConfigureAwait(false); return; }
        await using var call = await StartAsync(op, payload, true, token).ConfigureAwait(false);
        await call.ReadValueAsync<byte>(token, expectBody: false).ConfigureAwait(false);
    }

    public async ValueTask<ReadOnlyMemory<byte>> GetRawAsync(long op, ReadOnlyMemory<byte> request, CancellationToken token = default)
    {
        if (_mux.Length > 0) return await (await GetMuxAsync(token).ConfigureAwait(false)).GetRawAsync(op, request, token).ConfigureAwait(false);
        await using var call = await StartAsync(op, new RawBody(request), true, token).ConfigureAwait(false);
        return await call.ReadRawAsync(token).ConfigureAwait(false);
    }

    public async ValueTask SendAsync(long op, CancellationToken token = default, [CallerMemberName] string name = "")
    {
        if (_mux.Length > 0)
        {
            await (await GetMuxAsync(token).ConfigureAwait(false))
                .SendAsync<byte>(op, default, false, token).ConfigureAwait(false); 
            return;
        }
        await using var call = await StartAsync<byte>(op, default, false, token).ConfigureAwait(false);
        await call.ReadValueAsync<byte>(token, expectBody: false).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<TOut?> EnumerateAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TIn, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TOut>
        (long op, TIn payload, [EnumeratorCancellation] CancellationToken token = default, [CallerMemberName] string name = "")
    {
        await using var call = await StartAsync(op, payload, true, token).ConfigureAwait(false);

        await foreach (var item in call.ReadStreamAsync<TOut>(token).ConfigureAwait(false))
            yield return item;
    }

    public async IAsyncEnumerable<TOut?> EnumerateAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TOut>
        (long op, [EnumeratorCancellation] CancellationToken token = default, [CallerMemberName] string name = "")
    {
        await using var call = await StartAsync<byte>(op, default, false, token).ConfigureAwait(false);

        await foreach (var item in call.ReadStreamAsync<TOut>(token).ConfigureAwait(false))
            yield return item;
    }

    private async ValueTask<Call> StartAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TIn>(long op, TIn? payload, bool hasPayload, CancellationToken token)
    {
        var conn = await TryInitializeAsync(token).ConfigureAwait(false);
        var stream = await conn.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, token).ConfigureAwait(false);

        try
        {
            var writer = PipeWriter.Create(stream, new(leaveOpen: true));

            Framing.WriteOpId(writer.GetSpan(Framing.OpIdSize), op);
            writer.Advance(Framing.OpIdSize);

            if (hasPayload)
            {
                try
                {
                    if (typeof(TIn) == typeof(RawBody)) writer.Write(Unsafe.As<TIn, RawBody>(ref payload!).Bytes.Span);
                    else Serialize(writer, payload);
                }
                catch (Exception ex) { throw new ArgumentException("Invalid parameters", ex); }
            }

            await writer.FlushAsync(token).ConfigureAwait(false);
            await writer.CompleteAsync().ConfigureAwait(false);
            stream.CompleteWrites();

            return new(stream, options.RemoteEndPoint.ToString()!);
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private sealed class Call(QuicStream stream, string remote) : IAsyncDisposable
    {
        private readonly PipeReader _reader = PipeReader.Create(stream);

        public async ValueTask<TOut?> ReadValueAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TOut>(CancellationToken token, bool expectBody = true)
        {
            while (true)
            {
                var result = await _reader.ReadAsync(token).ConfigureAwait(false);
                var buffer = result.Buffer;

                if (Framing.TryReadFrame(ref buffer, out var frame))
                {
                    try
                    {
                        EnsureSuccess(frame);
                        return expectBody ? Deserialize<TOut>(frame.Slice(Framing.StatusSize)) : default;
                    }
                    finally
                    {
                        _reader.AdvanceTo(buffer.Start);
                    }
                }

                if (result.IsCompleted) throw new IOException($"Stream to {remote} closed without response.");

                _reader.AdvanceTo(buffer.Start, buffer.End);
            }
        }

        public async ValueTask<ReadOnlyMemory<byte>> ReadRawAsync(CancellationToken token)
        {
            while (true)
            {
                var result = await _reader.ReadAsync(token).ConfigureAwait(false);
                var buffer = result.Buffer;

                if (Framing.TryReadFrame(ref buffer, out var frame))
                {
                    try
                    {
                        EnsureSuccess(frame);
                        return frame.Slice(Framing.StatusSize).ToArray();
                    }
                    finally
                    {
                        _reader.AdvanceTo(buffer.Start);
                    }
                }

                if (result.IsCompleted) throw new IOException($"Stream to {remote} closed without response.");

                _reader.AdvanceTo(buffer.Start, buffer.End);
            }
        }

        public async IAsyncEnumerable<TOut?> ReadStreamAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TOut>([EnumeratorCancellation] CancellationToken token)
        {
            while (true)
            {
                var result = await _reader.ReadAsync(token).ConfigureAwait(false);
                var buffer = result.Buffer;

                while (Framing.TryReadFrame(ref buffer, out var frame))
                {
                    if (Framing.ReadStatus(frame) is ResponseStatus.StreamEnd) yield break;

                    if (Framing.ReadStatus(frame) is ResponseStatus.Batch)
                    {
                        // Lote: copia unica (el yield no puede retener el buffer del pipe) y items en orden.
                        int size = (int)frame.Length - Framing.StatusSize;
                        byte[] batch = ArrayPool<byte>.Shared.Rent(size);
                        frame.Slice(Framing.StatusSize).CopyTo(batch);
                        _reader.AdvanceTo(buffer.Start);

                        try
                        {
                            for (int offset = 0; offset < size;)
                            {
                                TOut? batchItem = default;
                                offset += Deserialize(batch.AsSpan(offset, size - offset), ref batchItem);
                                yield return batchItem;
                            }
                        }
                        finally
                        {
                            ArrayPool<byte>.Shared.Return(batch);
                        }

                        result = default;
                        goto NEXT;
                    }

                    EnsureSuccess(frame);

                    var item = Deserialize<TOut>(frame.Slice(Framing.StatusSize));

                    _reader.AdvanceTo(buffer.Start);

                    yield return item;

                    result = default;
                    goto NEXT;
                }

                if (result.IsCompleted) throw new IOException($"Stream to {remote} closed before end of stream.");

                _reader.AdvanceTo(buffer.Start, buffer.End);
            NEXT:;
            }
        }

        private void EnsureSuccess(in ReadOnlySequence<byte> frame)
        {
            if (Framing.ReadStatus(frame) is var status and not ResponseStatus.Success)
                throw ResponseError.Create(status, remote, frame.Slice(Framing.StatusSize));
        }

        public async ValueTask DisposeAsync()
        {
            // Consumir el FIN antes de cerrar: si no, QuicStream aborta la lectura y msquic
            // construye una QuicException con stack trace en cada llamada (~20 KB).
            if (stream.ReadsClosed.IsCompleted is false)
            {
                try
                {
                    ReadResult r;
                    while (!(r = await _reader.ReadAsync().ConfigureAwait(false)).IsCompleted) _reader.AdvanceTo(r.Buffer.End);
                }
                catch (Exception ex) when (ex is IOException or QuicException or OperationCanceledException) { }
            }

            await _reader.CompleteAsync().ConfigureAwait(false);
            await stream.DisposeAsync().ConfigureAwait(false);
        }
    }
}
