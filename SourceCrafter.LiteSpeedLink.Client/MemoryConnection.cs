using MemoryPack;
using SharedMemory;
using System.Buffers;
using System.Collections;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading.Tasks.Dataflow;

namespace SourceCrafter.LiteSpeedLink.Client;


[SupportedOSPlatform("windows")]
public sealed class MemoryConnection(string contextId, int timeout = 5000, System.Text.Encoding? encoding = null) : IConnectionAsync, IConnection, IDisposable
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
                if (_rpc?.DisposeFinished is null or true) _rpc = new RpcBuffer(_contextId);

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
        return ReadResponse<TOut>(MemoryRpc.RemoteRequest(SerializePayload(op, payload), _timeout, token));
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
        ReadResponse<byte>(MemoryRpc.RemoteRequest(SerializePayload(op, payload), _timeout, token));
        return true;
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
        var streamSessionId = $"{_contextId}_{Guid.NewGuid()}";

        BufferBlock<TOut> buffer = new(new DataflowBlockOptions { CancellationToken = token });

        RpcBuffer rpc = null!;

        rpc = new(streamSessionId, (id, payload) =>
        {
            if (payload?.Length > 0)
            {
                buffer.Post(Deserialize<TOut>(payload)!);
            }
            else
            {
                buffer.Complete();
                rpc.Dispose();
                rpc = null!;
            }
        });

        _ = MemoryRpc.RemoteRequestAsync(SerializePayload(op, (streamSessionId, payload)), _timeout, token);

        return buffer.ReceiveAllAsync(token).ToBlockingEnumerable();
    }

    public IEnumerable<TOut> Enumerate<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TOut>
        (long op,
        CancellationToken token = default,
        [CallerMemberName] string name = "")
    {
        var streamSessionId = $"{_contextId}_{Guid.NewGuid()}";

        BufferBlock<TOut> buffer = new(new DataflowBlockOptions { CancellationToken = token });

        RpcBuffer rpc = null!;

        rpc = new(streamSessionId, (id, payload) =>
        {
            if (payload?.Length > 0)
            {
                buffer.Post(Deserialize<TOut>(payload)!);
            }
            else
            {
                buffer.Complete();
                rpc.Dispose();
                rpc = null!;
            }
        });

        _ = MemoryRpc.RemoteRequestAsync(SerializePayload(op, streamSessionId), _timeout, token);

        return buffer.ReceiveAllAsync(token).ToBlockingEnumerable();
    }

    public async ValueTask<TOut?> GetAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TIn, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TOut>
        (long op,
         TIn payload,
         CancellationToken token = default,
         [CallerMemberName] string name = "")
    {
        return ReadResponse<TOut>(await MemoryRpc.RemoteRequestAsync(SerializePayload(op, payload), _timeout, token).ConfigureAwait(false));
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
        ReadResponse<byte>(await MemoryRpc.RemoteRequestAsync(SerializePayload(op, payload), _timeout, token).ConfigureAwait(false));
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
        var streamSessionId = $"{_contextId}_{Guid.NewGuid()}";

        BufferBlock<TOut> buffer = new(new DataflowBlockOptions { CancellationToken = token });

        RpcBuffer rpc = null!;

        rpc = new(streamSessionId, (id, payload) =>
        {
            if (payload?.Length > 0)
            {
                buffer.Post(Deserialize<TOut>(payload)!);
            }
            else
            {
                buffer.Complete();
                rpc.Dispose();
                rpc = null!;
            }
        });

        _ = MemoryRpc.RemoteRequestAsync(SerializePayload(op, (streamSessionId, payload)), _timeout, token);

        return buffer.ReceiveAllAsync(token);
    }

    public IAsyncEnumerable<TOut> EnumerateAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TOut>
        (long op,
        CancellationToken token = default,
        [CallerMemberName] string name = "")
    {
        var streamSessionId = $"{_contextId}_{Guid.NewGuid()}";

        BufferBlock<TOut> buffer = new(new DataflowBlockOptions { CancellationToken = token });

        RpcBuffer rpc = null!;

        rpc = new(streamSessionId, (id, payload) =>
        {
            if (payload?.Length > 0)
            {
                buffer.Post(Deserialize<TOut>(payload)!);
            }
            else
            {
                buffer.Complete();
                rpc.Dispose();
                rpc = null!;
            }
        });

        _ = MemoryRpc.RemoteRequestAsync(SerializePayload(op, streamSessionId), _timeout, token);

        return buffer.ReceiveAllAsync(token);
    }

    [ThreadStatic] private static ArrayBufferWriter<byte>? _requestWriter;

    /// <summary>Construye <c>[opId][cuerpo]</c> sobre un buffer por hilo; solo asigna el array que exige RpcBuffer.</summary>
    private static byte[] SerializePayload<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TIn>(long op, TIn payload)
    {
        var writer = _requestWriter ??= new(256);

        writer.ResetWrittenCount();

        Framing.WriteOpId(writer.GetSpan(Framing.OpIdSize), op);
        writer.Advance(Framing.OpIdSize);

        Serialize(writer, payload);

        return writer.WrittenSpan.ToArray();
    }

    private TOut? ReadResponse<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TOut>(RpcResponse response)
    {
        if (!response.Success) throw new TimeoutException($"No response from memory server '{_contextId}'.");

        if (response.Data is not { Length: > 0 } data) throw new InvalidDataException("Empty response.");

        switch ((ResponseStatus)data[0])
        {
            case ResponseStatus.Success: return Deserialize<TOut>(data.AsSpan(Framing.StatusSize));

            case ResponseStatus.StreamEnd: return default;

            case ResponseStatus.NotFound: throw new NotImplementedException($"Implementation is missing from '{_contextId}'");

            case ResponseStatus.Failed:
                throw new InvalidOperationException($"""
                    Execution failed on '{_contextId}':
                    REASON:

                    {Deserialize<string>(data.AsSpan(Framing.StatusSize))}
                    """);

            default: throw new InvalidDataException($"Unexpected response status {data[0]}.");
        }
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
            _rpc?.Dispose();
            _rpc = null;
        }
    }
}
