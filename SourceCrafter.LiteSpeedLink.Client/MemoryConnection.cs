using MemoryPack;
using SharedMemory;
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
public sealed class MemoryConnection(string contextId, int timeout = 100, System.Text.Encoding? encoding = null) : IConnectionAsync, IConnection, IDisposable
{
    private readonly string _contextId = contextId;
    private readonly int _timeout = timeout;
    public System.Text.Encoding Encoding { get; } = encoding ??= System.Text.Encoding.Default;
    private readonly Lock _lock = new();

    private RpcBuffer MemoryRpc
    {
        get
        {
            lock (_lock)
            {
                if (field?.DisposeFinished is null or true) field = new RpcBuffer(_contextId);
            }
            return field;
        }
    }

    public TOut? Get<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TIn, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TOut>
        (long op,
         TIn payload,
         CancellationToken token = default,
         [CallerMemberName] string name = "")
    {
        Console.WriteLine($"Sent: {payload}");
        if (MemoryRpc.RemoteRequest(SerializePayload(op, payload), _timeout, token) is { Success: true, Data: { Length: > 0 } responseData })
        {
            TOut? @out = Deserialize<TOut>(responseData);
            Console.WriteLine($"Response: {@out}");
            return @out;
        }
        Console.WriteLine($"Response: Nothing");

        return default;
    }

    public TOut? Get<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TOut>
        (long op,
         CancellationToken token = default,
         [CallerMemberName] string name = "")
    {
        if (MemoryRpc.RemoteRequest(BitConverter.GetBytes(op), _timeout, token) is { Success: true, Data: { Length: > 0 } responseData })
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
        return MemoryRpc.RemoteRequest(SerializePayload(op, payload), _timeout, token).Success;
    }

    public bool Send(
        long op,
        CancellationToken token = default,
        [CallerMemberName] string name = "")
    {
        return MemoryRpc.RemoteRequest(BitConverter.GetBytes(op), _timeout, token).Success;
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
        Console.WriteLine($"Sent {op}: {payload}");
        var response = await MemoryRpc.RemoteRequestAsync(SerializePayload(op, payload), _timeout, token);

        Console.WriteLine($"Received {response.Success}: {response.Data?.Length} bytes");

        if (response is { Success: true, Data: { Length: > 0 } responseData })
        {
            TOut? @out = Deserialize<TOut>(responseData);
            Console.WriteLine($"Response: {@out}");
            return @out;
        }
        Console.WriteLine($"Response: Nothing");

        return default;
    }

    public async ValueTask<TOut?> GetAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TOut>
        (long op,
         CancellationToken token = default,
         [CallerMemberName] string name = "")
    {

        if (await MemoryRpc.RemoteRequestAsync(BitConverter.GetBytes(op), _timeout, token) is { Success: true, Data: { Length: > 0 } responseData })
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
        await MemoryRpc.RemoteRequestAsync(SerializePayload(op, payload), _timeout, token);
    }

    public async ValueTask SendAsync(
        long op,
        CancellationToken token = default,
        [CallerMemberName] string name = "")
    {
        await MemoryRpc.RemoteRequestAsync(BitConverter.GetBytes(op), _timeout, token);
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
            MemoryRpc?.Dispose();
        }
    }
}
