using System.Text;
using MemoryPack;
using SharedMemory;
using System.Buffers;
using System.Collections;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace SourceCrafter.LiteSpeedLink.Client;


[SupportedOSPlatform("windows")]
public sealed class MemoryConnection(string contextId, int timeout = 5000, System.Text.Encoding? encoding = null) : IAsyncConnection, IConnection, IDisposable, IAsyncDisposable
{
    private readonly string _contextId = contextId;
    private readonly int _timeout = timeout;
    public System.Text.Encoding Encoding { get; } = encoding ??= System.Text.Encoding.Default;
    private readonly Lock _lock = new();
    private MemClient? _rpc;

    private MemClient MemoryRpc
    {
        get
        {
            lock (_lock)
            {
                if (_rpc is not { IsAlive: true })
                {
                    // Un solo host multicliente por contextId: el cliente crea su anillo de respuestas y se registra solo.
                    _rpc?.Dispose();
                    try { _rpc = MemClient.Connect(_contextId); }
                    catch (FileNotFoundException ex) { throw new TimeoutException($"Memory server '{_contextId}' did not answer.", ex); }
                }

                return _rpc;
            }
        }
    }

    public TOut? Get<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TIn, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TOut>
        (long op,
         TIn payload,
         CancellationToken token = default,
         [CallerMemberName] string name = "")
    {
        return MemoryRpc.Call((op, payload), WriteRequest, ReadValue<TOut>, this, _timeout, token);
    }

    public TOut? Get<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TOut>
        (long op,
         CancellationToken token = default,
         [CallerMemberName] string name = "")
    {
        return MemoryRpc.Call(op, WriteOp, ReadValue<TOut>, this, _timeout, token);
    }

    public bool Send<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TIn>
        (long op,
         TIn payload,
         CancellationToken token = default,
         [CallerMemberName] string name = "")
    {
        MemoryRpc.Call((op, payload), WriteRequest, ReadValue<byte>, this, _timeout, token);
        return true;
    }

    public ReadOnlyMemory<byte> GetRaw(long op, ReadOnlyMemory<byte> request, CancellationToken token = default) =>
        MemoryRpc.Call((op, request), WriteRawRequest, ReadRaw, this, _timeout, token);

    public ValueTask<ReadOnlyMemory<byte>> GetRawAsync(long op, ReadOnlyMemory<byte> request, CancellationToken token = default) =>
        MemoryRpc.CallAsync((op, request), WriteRawRequest, ReadRaw, this, _timeout, token);

    private static void WriteRawRequest(IBufferWriter<byte> writer, (long op, ReadOnlyMemory<byte> request) state)
    {
        Framing.WriteOpId(writer.GetSpan(Framing.OpIdSize), state.op);
        writer.Advance(Framing.OpIdSize);
        writer.Write(state.request.Span);
    }

    // La respuesta solo es valida durante la lectura (puede apuntar al nodo): el raw se copia.
    private static ReadOnlyMemory<byte> ReadRaw(object? c, ReadOnlySpan<byte> response) =>
        (ResponseStatus)Status(response) is ResponseStatus.Success
            ? response[Framing.StatusSize..].ToArray()
            : throw ResponseError.Create((ResponseStatus)response[0], $"'{((MemoryConnection)c!)._contextId}'", response[Framing.StatusSize..]);

    public bool Send(
        long op,
        CancellationToken token = default,
        [CallerMemberName] string name = "")
    {
        MemoryRpc.Call(op, WriteOp, ReadValue<byte>, this, _timeout, token);
        return true;
    }

    public IEnumerable<TOut> Enumerate<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TIn, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TOut>
        (long op,
         TIn payload,
         CancellationToken token = default,
         [CallerMemberName] string name = "")
    {
        return OpenStream<TOut, TIn>((op, payload), token).ToBlockingEnumerable(token);
    }

    public IEnumerable<TOut> Enumerate<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TOut>
        (long op,
        CancellationToken token = default,
        [CallerMemberName] string name = "")
    {
        return OpenStream<TOut, long>(op, WriteOp, token).ToBlockingEnumerable(token);
    }

    public ValueTask<TOut?> GetAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TIn, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TOut>
        (long op,
         TIn payload,
         CancellationToken token = default,
         [CallerMemberName] string name = "")
    {
        return MemoryRpc.CallAsync((op, payload), WriteRequest, ReadValue<TOut>, this, _timeout, token);
    }

    public ValueTask<TOut?> GetAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TOut>
        (long op,
         CancellationToken token = default,
         [CallerMemberName] string name = "")
    {
        return MemoryRpc.CallAsync(op, WriteOp, ReadValue<TOut>, this, _timeout, token);
    }

    public async ValueTask SendAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TIn>
        (long op,
         TIn payload,
         CancellationToken token = default,
         [CallerMemberName] string name = "")
    {
        await MemoryRpc.CallAsync((op, payload), WriteRequest, ReadValue<byte>, this, _timeout, token).ConfigureAwait(false);
    }

    public async ValueTask SendAsync(
        long op,
        CancellationToken token = default,
        [CallerMemberName] string name = "")
    {
        await MemoryRpc.CallAsync(op, WriteOp, ReadValue<byte>, this, _timeout, token).ConfigureAwait(false);
    }

    public IAsyncEnumerable<TOut> EnumerateAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TIn, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TOut>
        (long op,
         TIn payload,
         CancellationToken token = default,
         [CallerMemberName] string name = "")
    {
        return OpenStream<TOut, TIn>((op, payload), token);
    }

    public IAsyncEnumerable<TOut> EnumerateAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TOut>
        (long op,
        CancellationToken token = default,
        [CallerMemberName] string name = "")
    {
        return OpenStream<TOut, long>(op, WriteOp, token);
    }

    /// <summary>
    /// Envia la peticion; los lotes llegan como <c>Item</c> dirigidos a ella y la respuesta cierra el stream. Un fallo
    /// (excepcion, <c>Failed</c>, <c>NotFound</c>) o el watchdog de inactividad (<c>_timeout</c> sin items, rearmado por
    /// cada lote) terminan el enumerable con error; cancelar o abandonar la enumeracion detiene al productor del host.
    /// </summary>
    private IAsyncEnumerable<TOut> OpenStream<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TOut, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TIn>
        ((long op, TIn payload) request, CancellationToken token) =>
        OpenStream<TOut, (long op, TIn payload)>(request, WriteRequest, token);

    private IAsyncEnumerable<TOut> OpenStream<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TOut, TState>
        (TState request, Action<IBufferWriter<byte>, TState> write, CancellationToken token) =>
        new StreamEnumerable<TOut, TState>(this, request, write, token);

    /// <summary>
    /// Unico objeto por llamada (como el de un iterador async); el resto (enumerador, buffers, slot) es pooled. Los lotes
    /// <c>[int32 len][item]...</c> (ver MemoryRequestContext.Append) se copian en el hilo lector y se deserializan en el
    /// del consumidor. Disponer antes del final (break/cancel) detiene al productor del host; un doble Dispose es inocuo.
    /// </summary>
    private sealed class StreamEnumerable<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TOut, TState>
        (MemoryConnection connection, TState request, Action<IBufferWriter<byte>, TState> write, CancellationToken token)
        : IAsyncEnumerable<TOut>, IAsyncEnumerator<TOut>
    {
        private MemPullEnumerator<TOut, byte>? _e;
        private bool _opened;

        public IAsyncEnumerator<TOut> GetAsyncEnumerator(CancellationToken enumeratorToken = default)
        {
            if (_opened) // re-enumeracion: una peticion nueva
                return new StreamEnumerable<TOut, TState>(connection, request, write, token).GetAsyncEnumerator(enumeratorToken);

            _opened = true;
            _e = MemPullEnumerator<TOut, byte>.Open(connection.MemoryRpc, request, write, ReadItem<TOut>, ReadValue<byte>, connection,
                records: true, idleTimeoutMs: connection._timeout, cancellationToken: token, enumeratorToken: enumeratorToken);
            return this;
        }

        public TOut Current => _e!.Current;

        public ValueTask<bool> MoveNextAsync() =>
            _e?.MoveNextAsync() ?? throw new ObjectDisposedException(nameof(StreamEnumerable<TOut, TState>));

        public ValueTask DisposeAsync()
        {
            var e = _e;
            _e = null;
            return e?.DisposeAsync() ?? default;
        }
    }

    private static TOut ReadItem<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TOut>(object? _, ReadOnlySpan<byte> item) =>
        Deserialize<TOut>(item)!;

    /// <summary>Escribe <c>[opId][cuerpo]</c> directamente en el nodo de memoria compartida.</summary>
    private static void WriteRequest<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TIn>(IBufferWriter<byte> writer, (long op, TIn payload) state)
    {
        Framing.WriteOpId(writer.GetSpan(Framing.OpIdSize), state.op);
        writer.Advance(Framing.OpIdSize);

        Serialize(writer, state.payload);
    }

    private static void WriteOp(IBufferWriter<byte> writer, long op)
    {
        Framing.WriteOpId(writer.GetSpan(Framing.OpIdSize), op);
        writer.Advance(Framing.OpIdSize);
    }

    private static byte Status(ReadOnlySpan<byte> response) =>
        response.Length > 0 ? response[0] : throw new InvalidDataException("Empty response.");

    /// <summary>Lee <c>[estado][cuerpo]</c> directamente del nodo (hilo lector): sin array de respuesta.</summary>
    private static TOut? ReadValue<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TOut>(object? c, ReadOnlySpan<byte> response) =>
        (ResponseStatus)Status(response) switch
        {
            ResponseStatus.Success => Deserialize<TOut>(response[Framing.StatusSize..]),
            ResponseStatus.StreamEnd => default,
            var status => throw ResponseError.Create(status, $"'{((MemoryConnection)c!)._contextId}'", response[Framing.StatusSize..])
        };

    public void Dispose()
    {
        lock (_lock)
        {
            _rpc?.Dispose(); // envia Close: el host libera el anillo de este cliente
            _rpc = null;
        }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return default;
    }
}
