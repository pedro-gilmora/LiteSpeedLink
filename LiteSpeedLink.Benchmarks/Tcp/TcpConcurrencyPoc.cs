using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using MemoryPack;
using SourceCrafter.LiteSpeedLink;

namespace LiteSpeedLink.Benchmarks.Tcp;

/// <summary>
/// Formato de trama del PoC (framing 3.3):
/// <c>[int32 len][int32 corrId][payload...]</c>, con <c>len</c> = bytes que siguen al prefijo.
/// El servidor devuelve la misma forma: <c>[len][corrId][status:byte][MemoryPack body]</c>.
/// </summary>
internal static class Frame
{
    public const int Header = Framing.LengthPrefixSize + sizeof(int);

    public static void Write(PipeWriter writer, int corrId, ReadOnlySpan<byte> payload)
    {
        var span = writer.GetSpan(Header + payload.Length);
        BinaryPrimitives.WriteInt32LittleEndian(span, sizeof(int) + payload.Length);
        BinaryPrimitives.WriteInt32LittleEndian(span[Framing.LengthPrefixSize..], corrId);
        payload.CopyTo(span[Header..]);
        writer.Advance(Header + payload.Length);
    }

    public static bool TryRead(ref ReadOnlySequence<byte> buffer, out int corrId, out ReadOnlySequence<byte> body)
    {
        corrId = 0;
        body = default;

        if (!Framing.TryReadFrame(ref buffer, out var frame)) return false;

        Span<byte> id = stackalloc byte[sizeof(int)];
        frame.Slice(0, sizeof(int)).CopyTo(id);
        corrId = BinaryPrimitives.ReadInt32LittleEndian(id);
        body = frame.Slice(sizeof(int));
        return true;
    }
}

/// <summary>
/// Servidor eco sobre loopback. Procesa cada trama en paralelo y responde en el orden en que
/// termina (no en el de llegada), que es lo que expone los bugs de un cliente sin correlación.
/// </summary>
internal sealed class EchoServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _accept;

    public int Port { get; }

    public EchoServer()
    {
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _accept = AcceptLoop();
    }

    private async Task AcceptLoop()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                client.NoDelay = true;
                _ = Serve(client);
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task Serve(TcpClient client)
    {
        using var owned = client;
        var stream = client.GetStream();
        var reader = PipeReader.Create(stream);
        var writer = PipeWriter.Create(stream);
        var writeLock = new SemaphoreSlim(1, 1);

        try
        {
            while (true)
            {
                var result = await reader.ReadAsync(_cts.Token);
                var buffer = result.Buffer;

                while (Frame.TryRead(ref buffer, out var corrId, out var body))
                {
                    var request = body.ToArray();
                    _ = Respond(corrId, request);
                }

                reader.AdvanceTo(buffer.Start, buffer.End);

                if (result.IsCompleted) break;
            }
        }
        catch (Exception) { }

        async Task Respond(int corrId, byte[] request)
        {
            // Latencia variable: hace que las respuestas salgan desordenadas.
            if ((corrId & 3) == 0) await Task.Yield();

            var echoed = MemoryPackSerializer.Deserialize<string>(request.AsSpan(Framing.OpIdSize));
            var body = MemoryPackSerializer.Serialize(echoed);

            await writeLock.WaitAsync();
            try
            {
                var span = writer.GetSpan(Frame.Header + 1 + body.Length);
                BinaryPrimitives.WriteInt32LittleEndian(span, sizeof(int) + 1 + body.Length);
                BinaryPrimitives.WriteInt32LittleEndian(span[Framing.LengthPrefixSize..], corrId);
                span[Frame.Header] = (byte)ResponseStatus.Success;
                body.CopyTo(span[(Frame.Header + 1)..]);
                writer.Advance(Frame.Header + 1 + body.Length);
                await writer.FlushAsync();
            }
            finally
            {
                writeLock.Release();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        try { await _accept; } catch { }
    }
}

/// <summary>Contrato común de los tres clientes del PoC.</summary>
/// <summary>Pool que nunca reutiliza: un doble retorno no puede afectar a nadie.</summary>
internal sealed class IsolatedPool : MemoryPool<byte>
{
    public static readonly IsolatedPool Instance = new();

    public override int MaxBufferSize => int.MaxValue;

    public override IMemoryOwner<byte> Rent(int minBufferSize = -1) =>
        new Owner(new byte[minBufferSize <= 0 ? 4096 : minBufferSize]);

    protected override void Dispose(bool disposing) { }

    private sealed class Owner(byte[] array) : IMemoryOwner<byte>
    {
        public Memory<byte> Memory => array;
        public void Dispose() { }
    }
}

internal interface IPocClient : IAsyncDisposable
{
    ValueTask<string?> CallAsync(string payload, CancellationToken token = default);
}

internal static class PocClient
{
    public static async Task<(NetworkStream, TcpClient)> ConnectAsync(int port)
    {
        var tcp = new TcpClient { NoDelay = true };
        await tcp.ConnectAsync(IPAddress.Loopback, port);
        return (tcp.GetStream(), tcp);
    }

    public static byte[] BuildPayload(string value)
    {
        var body = MemoryPackSerializer.Serialize(value);
        var payload = new byte[Framing.OpIdSize + body.Length];
        Framing.WriteOpId(payload, Payloads.OpId);
        body.CopyTo(payload.AsSpan(Framing.OpIdSize));
        return payload;
    }

    public static string? ReadBody(ReadOnlySequence<byte> body)
    {
        var status = (ResponseStatus)body.FirstSpan[0];
        if (status != ResponseStatus.Success) throw new InvalidOperationException(status.ToString());
        return MemoryPackSerializer.Deserialize<string>(body.Slice(1));
    }
}

/// <summary>
/// A) Estado actual de TcpConnection: PipeReader/PipeWriter compartidos sin protección.
/// No es seguro: PipeReader lanza si hay dos ReadAsync concurrentes, y aun sin excepción
/// un hilo puede consumir la respuesta de otro.
/// </summary>
internal sealed class UnsafeClient(NetworkStream stream, TcpClient tcp) : IPocClient
{
    // Uso concurrente de un PipeReader puede devolver el mismo segmento dos veces al pool. Con el
    // pool compartido eso corrompe a los demas clientes del proceso: se aisla en uno privado.
    private readonly PipeReader _reader = PipeReader.Create(stream, new(pool: IsolatedPool.Instance));
    private readonly PipeWriter _writer = PipeWriter.Create(stream, new(pool: IsolatedPool.Instance));

    public async ValueTask<string?> CallAsync(string payload, CancellationToken token = default)
    {
        Frame.Write(_writer, 0, PocClient.BuildPayload(payload));
        await _writer.FlushAsync(token);

        while (true)
        {
            var result = await _reader.ReadAsync(token);
            var buffer = result.Buffer;

            if (Frame.TryRead(ref buffer, out _, out var body))
            {
                var value = PocClient.ReadBody(body);
                _reader.AdvanceTo(buffer.Start);
                return value;
            }

            _reader.AdvanceTo(buffer.Start, buffer.End);
        }
    }

    public ValueTask DisposeAsync()
    {
        tcp.Dispose();
        return default;
    }
}

/// <summary>
/// B) Exclusión mutua: un SemaphoreSlim serializa el ciclo completo request→response.
/// Correcto y trivial, pero una sola operación en vuelo por conexión.
/// </summary>
internal sealed class SerializedClient(NetworkStream stream, TcpClient tcp) : IPocClient
{
    private readonly PipeReader _reader = PipeReader.Create(stream);
    private readonly PipeWriter _writer = PipeWriter.Create(stream);
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async ValueTask<string?> CallAsync(string payload, CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            Frame.Write(_writer, 0, PocClient.BuildPayload(payload));
            await _writer.FlushAsync(token);

            while (true)
            {
                var result = await _reader.ReadAsync(token);
                var buffer = result.Buffer;

                if (Frame.TryRead(ref buffer, out _, out var body))
                {
                    var value = PocClient.ReadBody(body);
                    _reader.AdvanceTo(buffer.Start);
                    return value;
                }

                _reader.AdvanceTo(buffer.Start, buffer.End);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        tcp.Dispose();
        return default;
    }
}

/// <summary>
/// C) Multiplexado: un único read-loop dueño del PipeReader, id de correlación por request y
/// un canal de escritura con un único consumidor dueño del PipeWriter. Nadie más toca los pipes,
/// así que no hace falta lock en ellos. Varias operaciones en vuelo sobre la misma conexión.
/// </summary>
internal sealed class MultiplexedClient : IPocClient
{
    private readonly TcpClient _tcp;
    private readonly PipeReader _reader;
    private readonly PipeWriter _writer;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<string?>> _pending = new();
    private readonly Channel<(int corrId, byte[] payload)> _outbox =
        Channel.CreateUnbounded<(int, byte[])>(new() { SingleReader = true });
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _readLoop, _writeLoop;
    private int _nextId;

    public MultiplexedClient(NetworkStream stream, TcpClient tcp)
    {
        _tcp = tcp;
        _reader = PipeReader.Create(stream);
        _writer = PipeWriter.Create(stream);
        _readLoop = ReadLoop();
        _writeLoop = WriteLoop();
    }

    public ValueTask<string?> CallAsync(string payload, CancellationToken token = default)
    {
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);

        _pending[id] = tcs;

        if (!_outbox.Writer.TryWrite((id, PocClient.BuildPayload(payload))))
        {
            _pending.TryRemove(id, out _);
            throw new ObjectDisposedException(nameof(MultiplexedClient));
        }

        return new(tcs.Task);
    }

    private async Task WriteLoop()
    {
        var reader = _outbox.Reader;

        try
        {
            while (await reader.WaitToReadAsync(_cts.Token))
            {
                // Coalescing: agrupa todo lo encolado en un único flush / syscall.
                while (reader.TryRead(out var item))
                    Frame.Write(_writer, item.corrId, item.payload);

                await _writer.FlushAsync(_cts.Token);
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task ReadLoop()
    {
        try
        {
            while (true)
            {
                var result = await _reader.ReadAsync(_cts.Token);
                var buffer = result.Buffer;

                while (Frame.TryRead(ref buffer, out var corrId, out var body))
                {
                    if (!_pending.TryRemove(corrId, out var tcs)) continue;

                    try { tcs.TrySetResult(PocClient.ReadBody(body)); }
                    catch (Exception ex) { tcs.TrySetException(ex); }
                }

                _reader.AdvanceTo(buffer.Start, buffer.End);

                if (result.IsCompleted) break;
            }
        }
        catch (Exception ex)
        {
            foreach (var tcs in _pending.Values) tcs.TrySetException(ex);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _outbox.Writer.TryComplete();
        _cts.Cancel();
        _tcp.Dispose();
        try { await Task.WhenAll(_readLoop, _writeLoop); } catch { }
    }
}
