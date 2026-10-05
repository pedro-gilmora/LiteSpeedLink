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
using System.Threading.Channels;

namespace SourceCrafter.LiteSpeedLink.Client;


[SupportedOSPlatform("windows")]
public sealed class MemoryConnection(string contextId, int timeout = 5000, System.Text.Encoding? encoding = null) : IAsyncConnection, IConnection, IDisposable
{
    private readonly string _contextId = contextId;
    private readonly int _timeout = timeout;
    public System.Text.Encoding Encoding { get; } = encoding ??= System.Text.Encoding.Default;
    private readonly Lock _lock = new();
    private RpcBuffer? _rpc;

    private RpcBuffer MemoryRpc
    {
        get
        {
            lock (_lock)
            {
                if (_rpc?.DisposeFinished is null or true)
                {
                    // Un solo RpcBuffer multicliente por contextId: el cliente crea su anillo de respuestas y se registra solo.
                    _rpc?.Dispose();
                    try { _rpc = RpcBuffer.Connect(_contextId); }
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
        return ReadResponse<TOut>(MemoryRpc.Send((op, payload), WriteRequest, _timeout, token));
    }

    public TOut? Get<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TOut>
        (long op,
         CancellationToken token = default,
         [CallerMemberName] string name = "")
    {
        return ReadResponse<TOut>(MemoryRpc.RemoteRequest(SerializeOpId(op), _timeout, token));
    }

    public bool Send<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TIn>
        (long op,
         TIn payload,
         CancellationToken token = default,
         [CallerMemberName] string name = "")
    {
        ReadResponse<byte>(MemoryRpc.Send((op, payload), WriteRequest, _timeout, token));
        return true;
    }

    public ReadOnlyMemory<byte> GetRaw(long op, ReadOnlyMemory<byte> request, CancellationToken token = default) =>
        ReadRawResponse(MemoryRpc.Send((op, request), WriteRawRequest, _timeout, token));

    public async ValueTask<ReadOnlyMemory<byte>> GetRawAsync(long op, ReadOnlyMemory<byte> request, CancellationToken token = default) =>
        ReadRawResponse(await MemoryRpc.SendAsync((op, request), WriteRawRequest, _timeout, token).ConfigureAwait(false));

    private static void WriteRawRequest(IBufferWriter<byte> writer, (long op, ReadOnlyMemory<byte> request) state)
    {
        Framing.WriteOpId(writer.GetSpan(Framing.OpIdSize), state.op);
        writer.Advance(Framing.OpIdSize);
        writer.Write(state.request.Span);
    }

    private ReadOnlyMemory<byte> ReadRawResponse(RpcResponse response)
    {
        if (!response.Success) throw new TimeoutException($"No response from memory server '{_contextId}'.");

        if (response.Data is not { Length: > 0 } data) throw new InvalidDataException("Empty response.");

        return (ResponseStatus)data[0] is ResponseStatus.Success
            ? data.AsMemory(Framing.StatusSize)
            : throw ResponseError.Create((ResponseStatus)data[0], $"'{_contextId}'", data.AsSpan(Framing.StatusSize));
    }

    public bool Send(
        long op,
        CancellationToken token = default,
        [CallerMemberName] string name = "")
    {
        ReadResponse<byte>(MemoryRpc.RemoteRequest(SerializeOpId(op), _timeout, token));
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
        return OpenStream<TOut>(SerializeOpId(op), token).ToBlockingEnumerable(token);
    }

    public async ValueTask<TOut?> GetAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TIn, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TOut>
        (long op,
         TIn payload,
         CancellationToken token = default,
         [CallerMemberName] string name = "")
    {
        return ReadResponse<TOut>(await MemoryRpc.SendAsync((op, payload), WriteRequest, _timeout, token).ConfigureAwait(false));
    }

    public async ValueTask<TOut?> GetAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TOut>
        (long op,
         CancellationToken token = default,
         [CallerMemberName] string name = "")
    {
        return ReadResponse<TOut>(await MemoryRpc.RemoteRequestAsync(SerializeOpId(op), _timeout, token).ConfigureAwait(false));
    }

    public async ValueTask SendAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TIn>
        (long op,
         TIn payload,
         CancellationToken token = default,
         [CallerMemberName] string name = "")
    {
        ReadResponse<byte>(await MemoryRpc.SendAsync((op, payload), WriteRequest, _timeout, token).ConfigureAwait(false));
    }

    public async ValueTask SendAsync(
        long op,
        CancellationToken token = default,
        [CallerMemberName] string name = "")
    {
        ReadResponse<byte>(await MemoryRpc.RemoteRequestAsync(SerializeOpId(op), _timeout, token).ConfigureAwait(false));
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
        return OpenStream<TOut>(SerializeOpId(op), token);
    }

    /// <summary>
    /// Envia la peticion; los elementos llegan por el canal principal dirigidos a ella (<c>StreamItem</c>) y la respuesta
    /// cierra el stream. Un fallo (excepcion, <c>Failed</c>, <c>NotFound</c>) termina el enumerable con error.
    /// </summary>
    // ponytail: la respuesta llega al final del stream; un timeout de RpcBuffer no se trata como fallo (streams largos).
    private IAsyncEnumerable<TOut> OpenStream<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TOut>
        (byte[] request, CancellationToken token) =>
        OpenStream<TOut, byte[]>((0, request), static (w, s) => w.Write(s.payload), token);

    private IAsyncEnumerable<TOut> OpenStream<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TOut, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TIn>
        ((long op, TIn payload) request, CancellationToken token) =>
        OpenStream<TOut, TIn>(request, WriteRequest, token);

    private async IAsyncEnumerable<TOut> OpenStream<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TOut, TState>
        ((long op, TState payload) request, Action<IBufferWriter<byte>, (long op, TState payload)> write, [EnumeratorCancellation] CancellationToken token)
    {
        var buffer = Channel.CreateUnbounded<TOut>(new() { SingleReader = true, SingleWriter = true });

        // Timeout de inactividad: sin el, un mensaje perdido cuelga el stream para siempre. Por elemento solo se anota
        // la hora (sin reprogramar timers); un timer periodico cancela si pasa `_timeout` sin recibir nada.
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(token);
        long last = Environment.TickCount64;
        using var watchdog = new Timer(_ =>
        {
            if (Environment.TickCount64 - Volatile.Read(ref last) > _timeout) try { idle.Cancel(); } catch (ObjectDisposedException) { }
        }, null, _timeout, _timeout);

        _ = MemoryRpc.RemoteStreamAsync(request, write, (ReadOnlySpan<byte> payload) =>
        {
            Volatile.Write(ref last, Environment.TickCount64);
            // Lote [int32 len][item]... (ver MemoryRequestContext.Append)
            while (payload.Length > 0)
            {
                int len = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(payload);
                buffer.Writer.TryWrite(Deserialize<TOut>(payload.Slice(sizeof(int), len))!);
                payload = payload[(sizeof(int) + len)..];
            }
        }, idle.Token).ContinueWith((t, s) =>
        {
            var writer = (ChannelWriter<TOut>)s!;

            try
            {
                ReadResponse<byte>(t.Result);
                writer.TryComplete();
            }
            catch (Exception ex)
            {
                writer.TryComplete(ex is AggregateException a ? a.GetBaseException() : ex);
            }
        }, buffer.Writer, TaskContinuationOptions.ExecuteSynchronously);

        // ReadAllAsync relanza la excepcion con la que se completo el canal (fallo de la peticion).
        // Drena en lote: un solo despertar por rafaga en vez de uno por item.
        var reader = buffer.Reader;
        while (await reader.WaitToReadAsync(token).ConfigureAwait(false))
            while (reader.TryRead(out var item))
                yield return item;
    }

    /// <summary>Escribe <c>[opId][cuerpo]</c> directamente en el nodo de memoria compartida.</summary>
    private static void WriteRequest<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TIn>(IBufferWriter<byte> writer, (long op, TIn payload) state)
    {
        Framing.WriteOpId(writer.GetSpan(Framing.OpIdSize), state.op);
        writer.Advance(Framing.OpIdSize);

        Serialize(writer, state.payload);
    }

    [ThreadStatic] private static ArrayBufferWriter<byte>? _requestWriter;

    /// <summary>Construye <c>[opId][cuerpo]</c> sobre un buffer por hilo; sin asignar: RpcBuffer lo copia antes de devolver, asi que solo es valido hasta la siguiente llamada del hilo.</summary>
    private static ReadOnlyMemory<byte> SerializePayload<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TIn>(long op, TIn payload)
    {
        var writer = _requestWriter ??= new(256);

        writer.ResetWrittenCount();

        Framing.WriteOpId(writer.GetSpan(Framing.OpIdSize), op);
        writer.Advance(Framing.OpIdSize);

        Serialize(writer, payload);

        return writer.WrittenMemory;
    }

    private TOut? ReadResponse<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TOut>(RpcResponse response)
    {
        if (!response.Success) throw new TimeoutException($"No response from memory server '{_contextId}'.");

        if (response.Data is not { Length: > 0 } data) throw new InvalidDataException("Empty response.");

        return (ResponseStatus)data[0] switch
        {
            ResponseStatus.Success => Deserialize<TOut>(data.AsSpan(Framing.StatusSize)),
            ResponseStatus.StreamEnd => default,
            var status => throw ResponseError.Create(status, $"'{_contextId}'", data.AsSpan(Framing.StatusSize))
        };
    }

    private static byte[] SerializeOpId(long op)
    {
        var request = new byte[Framing.OpIdSize];

        Framing.WriteOpId(request, op);

        return request;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _rpc?.Dispose(); // envia Close: el host libera el anillo de este cliente
            _rpc = null;
        }
    }
}
