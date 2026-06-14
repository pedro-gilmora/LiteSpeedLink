using MemoryPack;
using SharedMemory;
using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading.Tasks.Dataflow;

namespace SourceCrafter.LiteSpeedLink.Client;


[SupportedOSPlatform("windows")]
public sealed class MemoryConnection(string contextId, int timeout = 100, System.Text.Encoding? encoding = null) : IDisposable
{
    private readonly string _contextId = contextId;
    private readonly int _timeout = timeout;
    public System.Text.Encoding Encoding { get; } = encoding ??= System.Text.Encoding.Default;
    private RpcBuffer? _rpcBuffer;
    private readonly Lock _lock = new();

    private RpcBuffer GetOrCreateRpcBuffer()
    {
        lock (_lock)
        {
            if (_rpcBuffer == null || _rpcBuffer.DisposeFinished)
            {
                _rpcBuffer = new RpcBuffer(_contextId);
            }
        }
        return _rpcBuffer;
    }

    public TOut? Get<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TIn, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TOut>
        (long op,
         TIn payload,
         CancellationToken token = default,
         [CallerMemberName] string name = "")
    {
        if (GetOrCreateRpcBuffer().RemoteRequest(SerializePayload(op, payload), _timeout, token) is { Success: true, Data: { Length: > 0 } responseData })
        {
            return Deserialize<TOut>(responseData);
        }

        return default;
    }

    public TOut? Get<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TOut>
        (long op,
         CancellationToken token = default,
         [CallerMemberName] string name = "")
    {
        if (GetOrCreateRpcBuffer().RemoteRequest(BitConverter.GetBytes(op), _timeout, token) is { Success: true, Data: { Length: > 0 } responseData })
        {
            return Deserialize<TOut>(responseData);
        }

        return default;
    }

    public bool Send<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TIn>
        (long op,
         TIn payload,
         CancellationToken token = default,
         [CallerMemberName] string name = "")
    {
        return GetOrCreateRpcBuffer().RemoteRequest(SerializePayload(op, payload), _timeout, token).Success;
    }

    public bool Send(
        long op,
        CancellationToken token = default,
        [CallerMemberName] string name = "")
    {
        return GetOrCreateRpcBuffer().RemoteRequest(BitConverter.GetBytes(op), _timeout, token).Success;
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

        _ = GetOrCreateRpcBuffer().RemoteRequestAsync(SerializePayload(op, (streamSessionId, payload)), _timeout, token);

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

        _ = GetOrCreateRpcBuffer().RemoteRequestAsync(SerializePayload(op, streamSessionId), _timeout, token);

        return buffer.ReceiveAllAsync(token).ToBlockingEnumerable();
    }

    public async ValueTask<TOut?> GetAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TIn, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TOut>
        (long op,
         TIn payload,
         CancellationToken token = default,
         [CallerMemberName] string name = "")
    {
        if (await GetOrCreateRpcBuffer().RemoteRequestAsync(SerializePayload(op, payload), _timeout, token) is { Success: true, Data: { Length: > 0 } responseData })
        {
            return Deserialize<TOut>(responseData);
        }

        return default;
    }

    public async ValueTask<TOut?> GetAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TOut>
        (long op,
         CancellationToken token = default,
         [CallerMemberName] string name = "")
    {

        if (await GetOrCreateRpcBuffer().RemoteRequestAsync(BitConverter.GetBytes(op), _timeout, token) is { Success: true, Data: { Length: > 0 } responseData })
        {
            return Deserialize<TOut>(responseData);
        }

        return default;
    }

    public async ValueTask SendAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TIn>
        (long op,
         TIn payload,
         CancellationToken token = default,
         [CallerMemberName] string name = "")
    {
        //Console.WriteLine($"{_contextId}: Sending request: {name} for operation: {op} with payload: {payload}");
        await GetOrCreateRpcBuffer().RemoteRequestAsync(SerializePayload(op, payload), _timeout, token);
    }

    public async ValueTask SendAsync(
        long op,
        CancellationToken token = default,
        [CallerMemberName] string name = "")
    {
        await GetOrCreateRpcBuffer().RemoteRequestAsync(BitConverter.GetBytes(op), _timeout, token);
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

        _ = GetOrCreateRpcBuffer().RemoteRequestAsync(SerializePayload(op, (streamSessionId, payload)), _timeout, token);

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

        _ = GetOrCreateRpcBuffer().RemoteRequestAsync(SerializePayload(op, streamSessionId), _timeout, token);

        return buffer.ReceiveAllAsync(token);
    }

    private static byte[]? SerializePayload<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TIn>(long op, TIn payload)
    {
        ReadOnlySpan<byte> body = Serialize(payload);

        int reqLen = body.Length + 8;

        Memory<byte> requestBuffer = new byte[reqLen];

        BitConverter.GetBytes(op).CopyTo(requestBuffer);

        body.CopyTo(requestBuffer.Span[8..]);

        return MemoryMarshal.TryGetArray((ReadOnlyMemory<byte>)requestBuffer, out var arr) ? arr.Array : null;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _rpcBuffer?.Dispose();
            _rpcBuffer = null;
        }
    }
}
