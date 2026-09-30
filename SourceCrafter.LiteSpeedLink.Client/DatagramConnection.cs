using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace SourceCrafter.LiteSpeedLink.Client;

/// <summary>
/// Socket UDP compartido por llamadas concurrentes: el datagrama delimita el mensaje y el corrId
/// empareja la respuesta. Peticion <c>[corrId][opId][cuerpo]</c>, respuesta <c>[corrId][estado][cuerpo]</c>.
/// </summary>
/// <remarks>ponytail: sin reintentos ni fragmentacion; un datagrama perdido deja la llamada esperando hasta que se cancele su token.</remarks>
public sealed class UdpConnection(EndPoint endpoint) : IDisposable, IAsyncConnection
{
    private const int RequestHeaderSize = Framing.CorrelationIdSize + Framing.OpIdSize;
    private const int ResponseHeaderSize = Framing.CorrelationIdSize + Framing.StatusSize;

    internal UdpClient connection = null!;
    private readonly ConcurrentDictionary<int, Channel<byte[]>> _pending = new();
    private readonly Lock _init = new();
    private int _nextId;

    public void Dispose()
    {
        if (connection is null) return;

        connection.Close();
        connection.Dispose();

        foreach (var (_, responses) in _pending)
            responses.Writer.TryComplete(new ObjectDisposedException(nameof(UdpConnection)));
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    internal void TryInitialize()
    {
        if (Volatile.Read(ref connection) is not null) return;

        lock (_init)
        {
            if (connection is not null) return;

            var client = new UdpClient();

            switch (endpoint)
            {
                case DnsEndPoint { Host: string host, Port: int port }: client.Connect(host, port); break;
                case IPEndPoint ip: client.Connect(ip); break;
                default: throw new Exception($"Unsuported endpoint: [{endpoint}]");
            }

            Volatile.Write(ref connection, client);

            _ = ReadLoopAsync(client);
        }
    }

    private async Task ReadLoopAsync(UdpClient client)
    {
        try
        {
            while (true)
            {
                var buffer = (await client.ReceiveAsync().ConfigureAwait(false)).Buffer;

                if (buffer.Length < ResponseHeaderSize) continue;

                // Una respuesta sin destinatario pertenece a una llamada ya cancelada.
                if (_pending.TryGetValue(BinaryPrimitives.ReadInt32LittleEndian(buffer), out var responses))
                    responses.Writer.TryWrite(buffer);
            }
        }
        catch (Exception ex)
        {
            foreach (var (_, responses) in _pending)
                responses.Writer.TryComplete(ex);
        }
    }

    public async ValueTask<TOut?> GetAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TIn, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TOut>
        (long op, TIn payload, CancellationToken token = default, [CallerMemberName] string name = "")
    {
        var (id, responses) = await SendRequestAsync(op, payload, true, token).ConfigureAwait(false);
        try { return Deserialize<TOut>(EnsureSuccess(await responses.Reader.ReadAsync(token).ConfigureAwait(false))); }
        finally { _pending.TryRemove(id, out _); }
    }

    public async ValueTask<TOut?> GetAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TOut>
        (long op, CancellationToken token = default, [CallerMemberName] string name = "")
    {
        var (id, responses) = await SendRequestAsync<byte>(op, default, false, token).ConfigureAwait(false);
        try { return Deserialize<TOut>(EnsureSuccess(await responses.Reader.ReadAsync(token).ConfigureAwait(false))); }
        finally { _pending.TryRemove(id, out _); }
    }

    public async ValueTask SendAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TIn>
        (long op, TIn payload, CancellationToken token = default, [CallerMemberName] string name = "")
    {
        var (id, responses) = await SendRequestAsync(op, payload, true, token).ConfigureAwait(false);
        try { EnsureSuccess(await responses.Reader.ReadAsync(token).ConfigureAwait(false)); }
        finally { _pending.TryRemove(id, out _); }
    }

    public async ValueTask<ReadOnlyMemory<byte>> GetRawAsync(long op, ReadOnlyMemory<byte> request, CancellationToken token = default)
    {
        var (id, responses) = await SendRequestAsync(op, new RawBody(request), true, token).ConfigureAwait(false);
        try
        {
            var response = await responses.Reader.ReadAsync(token).ConfigureAwait(false);
            EnsureSuccess(response);
            return response.AsMemory(ResponseHeaderSize);
        }
        finally { _pending.TryRemove(id, out _); }
    }

    public async ValueTask SendAsync(long op, CancellationToken token = default, [CallerMemberName] string name = "")
    {
        var (id, responses) = await SendRequestAsync<byte>(op, default, false, token).ConfigureAwait(false);
        try { EnsureSuccess(await responses.Reader.ReadAsync(token).ConfigureAwait(false)); }
        finally { _pending.TryRemove(id, out _); }
    }

    public IAsyncEnumerable<TOut?> EnumerateAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TIn, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TOut>
        (long op, TIn payload, CancellationToken token = default, [CallerMemberName] string name = "") =>
        EnumerateCoreAsync<TIn, TOut>(op, payload, true, token);

    public IAsyncEnumerable<TOut?> EnumerateAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TOut>
        (long op, CancellationToken token = default, [CallerMemberName] string name = "") =>
        EnumerateCoreAsync<byte, TOut>(op, default, false, token);

    private async IAsyncEnumerable<TOut?> EnumerateCoreAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TIn, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TOut>
        (long op, TIn? payload, bool hasPayload, [EnumeratorCancellation] CancellationToken token)
    {
        var (id, responses) = await SendRequestAsync(op, payload, hasPayload, token).ConfigureAwait(false);

        // items: [corrId][Success][seq][body]; fin: [corrId][StreamEnd][count]. Se libera solo el tramo contiguo.
        Dictionary<int, byte[]>? early = null;
        int next = 0, count = -1;

        try
        {
            while (count < 0 || next < count)
            {
                var response = await responses.Reader.ReadAsync(token).ConfigureAwait(false);
                var status = (ResponseStatus)response[Framing.CorrelationIdSize];

                if (status is ResponseStatus.StreamEnd)
                {
                    count = BinaryPrimitives.ReadInt32LittleEndian(response.AsSpan(ResponseHeaderSize));
                    continue;
                }

                if (status is not ResponseStatus.Success) EnsureSuccess(response);

                int seq = BinaryPrimitives.ReadInt32LittleEndian(response.AsSpan(ResponseHeaderSize));

                if (seq != next)
                {
                    (early ??= [])[seq] = response;
                    continue;
                }

                yield return Deserialize<TOut>(response.AsSpan(ResponseHeaderSize + sizeof(int)));
                next++;

                while (early != null && early.Remove(next, out var buffered))
                {
                    yield return Deserialize<TOut>(buffered.AsSpan(ResponseHeaderSize + sizeof(int)));
                    next++;
                }
            }
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private async ValueTask<(int, Channel<byte[]>)> SendRequestAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TIn>(long op, TIn? payload, bool hasPayload, CancellationToken token)
    {
        TryInitialize();

        int id = Interlocked.Increment(ref _nextId);

        var writer = new ArrayBufferWriter<byte>(64);
        var header = writer.GetSpan(RequestHeaderSize);

        BinaryPrimitives.WriteInt32LittleEndian(header, id);
        Framing.WriteOpId(header[Framing.CorrelationIdSize..], op);
        writer.Advance(RequestHeaderSize);

        if (hasPayload)
        {
            try
            {
                if (typeof(TIn) == typeof(RawBody)) writer.Write(Unsafe.As<TIn, RawBody>(ref payload!).Bytes.Span);
                else Serialize(writer, payload);
            }
            catch (Exception ex) { throw new ArgumentException("Invalid parameters", ex); }
        }

        var responses = Channel.CreateUnbounded<byte[]>(new() { SingleReader = true, SingleWriter = true });

        _pending[id] = responses;

        try
        {
            await connection.SendAsync(writer.WrittenMemory, token).ConfigureAwait(false);
        }
        catch
        {
            _pending.TryRemove(id, out _);
            throw;
        }

        return (id, responses);
    }

    private ReadOnlySpan<byte> EnsureSuccess(byte[] response)
    {
        var body = response.AsSpan(ResponseHeaderSize);

        var status = (ResponseStatus)response[Framing.CorrelationIdSize];

        return status is ResponseStatus.Success ? body : throw ResponseError.Create(status, endpoint.ToString()!, body);
    }
}
