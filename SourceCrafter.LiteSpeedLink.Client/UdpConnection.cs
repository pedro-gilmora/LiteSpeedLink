using MemoryPack;
using System.Diagnostics.CodeAnalysis;
using System.Net.Sockets;
using System.Net;
using System.Runtime.CompilerServices;

namespace SourceCrafter.LiteSpeedLink.Client;

public sealed class UdpConnection(EndPoint endpoint) : IDisposable, IConnection
{
    internal UdpClient connection = null!;

    public void Dispose()
    {
        if (connection is null) return;

        connection.Close();
        connection.Dispose();
    }

    internal void TryInitialize()
    {
        if (connection is not null) return;

        connection = new() { };

        switch (endpoint)
        {
            case DnsEndPoint { Host: string host, Port: int port }:

                connection.Connect(host, port);

                break;

            case IPEndPoint ip:

                connection.Connect(ip);

                break;

            default:
                throw new Exception($"Unsuported endpoint: [{endpoint}]");

        }

    }

    public async IAsyncEnumerable<TOut?> EnumerateAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TIn, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TOut>
        (long op,
         TIn payload,
         [EnumeratorCancellation] CancellationToken token = default, [CallerMemberName] string name = "")
    {
        TryInitialize();

        await connection.SendAsync(BuildRequest(Serialize(op), Serialize(payload)), token).ConfigureAwait(false);

        while (await connection.ReceiveAsync(token).ConfigureAwait(false) is { Buffer: { Length: > 0 } buffer })
        {
            yield return Deserialize<TOut>(buffer);
        }
    }

    public async ValueTask SendAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TIn>(int op, TIn payload, CancellationToken token = default, [CallerMemberName] string name = "")
    {
        TryInitialize();

        await connection!.SendAsync(
            BuildRequest(
                Serialize(op),
                Serialize(payload)),
            token).ConfigureAwait(false);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static ReadOnlyMemory<byte> BuildRequest(Span<byte> op, Span<byte> payload)
    {
        Span<byte> result = stackalloc byte[8 + payload.Length];

        op.CopyTo(result);
        payload.CopyTo(result[op.Length..]);

        return new(result.ToArray());
    }


    public async ValueTask<TOut?> GetAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TIn, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TOut>
        (long op,
         TIn payload,
         CancellationToken token = default, [CallerMemberName] string name = "")
    {
        TryInitialize();

        byte[] buffer = [];

        try
        {
            await connection.SendAsync(BuildRequest(Serialize(op), Serialize(payload)), token).ConfigureAwait(false);

            return Deserialize<TOut>((buffer = (await connection.ReceiveAsync(token).ConfigureAwait(false)).Buffer).AsSpan());
        }
        catch (MemoryPackSerializationException ex)
        {
            if (ex.StackTrace?.Contains("MemoryPack.MemoryPackSerializer.Serialize[") is true)

                throw new ArgumentException("Invalid parameters", ex);

            else if (ex.StackTrace?.Contains("MemoryPack.MemoryPackSerializer.Deserialize[") is true && buffer.Length > 0)

                try
                {
                    switch ((ResponseStatus)buffer[0])
                    {
                        case ResponseStatus.NotFound: throw new NotImplementedException(@$"Implementation is missing from {connection!.Client.RemoteEndPoint}");

                        case ResponseStatus.Failed: throw new InvalidOperationException(@$"Execution failed on {connection!.Client.RemoteEndPoint}:
REASON:

{Deserialize<string>(buffer.AsSpan(1))}
");

                        default: throw;
                    }
                }
                catch { throw; }

            else throw;
        }
        catch { throw; }
    }

    public async ValueTask<TOut?> GetAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TOut>
        (long op,
         CancellationToken token = default, [CallerMemberName] string name = "")
    {
        byte[] buffer = [];

        try
        {
            TryInitialize();

            await connection.SendAsync(Serialize(op), token).ConfigureAwait(false);

            return Deserialize<TOut>((buffer = (await connection.ReceiveAsync(token).ConfigureAwait(false)).Buffer).AsSpan());
        }
        catch (MemoryPackSerializationException ex)
        {
            if (ex.StackTrace?.Contains("MemoryPack.MemoryPackSerializer.Deserialize[") is true && buffer.Length > 0)

                try
                {
                    switch ((ResponseStatus)buffer[0])
                    {
                        case ResponseStatus.NotFound: throw new NotImplementedException(@$"Implementation is missing from {connection!.Client.RemoteEndPoint}");

                        case ResponseStatus.Failed: throw new InvalidOperationException(@$"Execution failed on {connection!.Client.RemoteEndPoint}:
REASON:

{Deserialize<string>(buffer.AsSpan(1))}
");

                        default: throw;
                    }
                }
                catch { throw; }

            else throw;
        }
        catch { throw; }
    }

    public async ValueTask SendAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TIn>
        (long op,
         TIn payload,
         CancellationToken token = default, [CallerMemberName] string name = "")
    {
        byte[] buffer;

        TryInitialize();

        try
        {
            await connection.SendAsync(BuildRequest(Serialize(op), Serialize(payload)), token).ConfigureAwait(false);

            switch ((ResponseStatus)(buffer = (await connection.ReceiveAsync(token).ConfigureAwait(false)).Buffer)[0])
            {
                case ResponseStatus.NotFound: throw new NotImplementedException(@$"Implementation is missing from {connection!.Client.RemoteEndPoint}");

                case ResponseStatus.Failed: throw new InvalidOperationException(@$"Execution failed on {connection!.Client.RemoteEndPoint}:
REASON:

{Deserialize<string>(buffer.AsSpan(1))}
");
            }
        }
        catch (MemoryPackSerializationException ex)
        {
            if (ex.StackTrace?.Contains("MemoryPack.MemoryPackSerializer.Serialize[") is true)

                throw new ArgumentException("Invalid parameters", ex);

            else

                throw;
        }
        catch { throw; }
    }

    public async ValueTask SendAsync(
        long op,
        CancellationToken token = default, [CallerMemberName] string name = "")
    {
        TryInitialize();

        byte[] buffer;

        try
        {
            await connection!.SendAsync(Serialize(op), token).ConfigureAwait(false);

            switch ((ResponseStatus)(buffer = (await connection.ReceiveAsync(token).ConfigureAwait(false)).Buffer)[0])
            {
                case ResponseStatus.NotFound: throw new NotImplementedException(@$"Implementation is missing from {connection!.Client.RemoteEndPoint}");

                case ResponseStatus.Failed: throw new InvalidOperationException(@$"Execution failed on {connection!.Client.RemoteEndPoint}:
REASON:

{Deserialize<string>(buffer.AsSpan(1))}
");
            }
        }
        catch { throw; }
    }

    public async IAsyncEnumerable<TOut?> EnumerateAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TOut>
        (long op,
         [EnumeratorCancellation] CancellationToken token = default, [CallerMemberName] string name = "")
    {
        TryInitialize();

        try
        {
            await connection.SendAsync(Serialize(op), token).ConfigureAwait(false);
        }
        catch (MemoryPackSerializationException ex)
        {
            if (ex.StackTrace?.Contains("MemoryPack.MemoryPackSerializer.Serialize[") is true)
            {
                throw new ArgumentException("Invalid parameters", ex);
            }

            throw;
        }

        TOut? item;

        while (await connection!.ReceiveAsync(token).ConfigureAwait(false) is { Buffer: { Length: > 0 } buffer })
        {
            try
            {
                item = Deserialize<TOut>(buffer);
            }
            catch (MemoryPackSerializationException ex)
            {
                if (ex.StackTrace?.Contains("MemoryPack.MemoryPackSerializer.Deserialize[") is true)

                    try
                    {
                        switch ((ResponseStatus)buffer[0])
                        {
                            case ResponseStatus.NotFound: throw new NotImplementedException(@$"Implementation is missing from {connection!.Client.RemoteEndPoint}");

                            case ResponseStatus.Failed: throw new InvalidOperationException(@$"Execution failed on {connection!.Client.RemoteEndPoint}:
REASON:

{Deserialize<string>(buffer.AsSpan(1))}
");
                            default: throw;
                        }
                    }
                    catch { throw; }

                else throw;
            }

            yield return item;
        }
    }
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}