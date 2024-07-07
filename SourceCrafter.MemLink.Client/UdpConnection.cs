using MemoryPack;
using System.Diagnostics.CodeAnalysis;
using System.Net.Sockets;
using System.Net;
using System.Runtime.CompilerServices;

namespace SourceCrafter.LiteSpeedLink.Client;

public static partial class ClientExtensions
{
    private sealed class UdpConnection(EndPoint endpoint) : IConnection
    {
        internal UdpClient? connection;

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
                    throw new Exception($"Unsuported endpoint: [{endpoint.ToString()}]");

            }

        }

        public async ValueTask<TOut?> GetAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TIn, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TOut>
            (int op,
             TIn payload,
             TOut? _,
             CancellationToken token = default)
        {
            TryInitialize();

            await connection!
                .SendAsync(
                    Build(
                        MemoryPackSerializer.Serialize(op),
                        MemoryPackSerializer.Serialize(payload)),
                    token)
                .ConfigureAwait(false);

            return MemoryPackSerializer.Deserialize<TOut>(
                (await connection.ReceiveAsync(token).ConfigureAwait(false)).Buffer.AsSpan());
        }

        public async ValueTask<TOut?> GetAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TOut>(int op, CancellationToken token = default)
        {
            TryInitialize();

            await connection!
                .SendAsync(MemoryPackSerializer.Serialize(op), token)
                .ConfigureAwait(false);

            return MemoryPackSerializer.Deserialize<TOut>(
                (await connection.ReceiveAsync(token).ConfigureAwait(false)).Buffer.AsSpan());
        }

        public async IAsyncEnumerable<TOut?> EnumerateAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TIn, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TOut>
            (int op,
             TIn payload,
             TOut? _,
             [EnumeratorCancellation] CancellationToken token = default)
        {
            TryInitialize();

            await connection!.SendAsync(
                Build(
                    MemoryPackSerializer.Serialize(op),
                    MemoryPackSerializer.Serialize(payload)),
                token).ConfigureAwait(false);

            while (await connection.ReceiveAsync(token).ConfigureAwait(false) is { Buffer: { Length: > 0 } buffer })
            {
                yield return MemoryPackSerializer.Deserialize<TOut>(buffer);
            }
        }

        public async IAsyncEnumerable<TOut?> EnumerateAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TOut>
            (int op,
             [EnumeratorCancellation] CancellationToken token = default)
        {
            TryInitialize();

            await connection!.SendAsync(MemoryPackSerializer.Serialize(op), token);

            while (await connection.ReceiveAsync(token) is { Buffer: { Length: > 0 } buffer })
            {
                yield return MemoryPackSerializer.Deserialize<TOut>(buffer);
            }
        }

        public async ValueTask SendAsync(int op, CancellationToken token = default)
        {
            TryInitialize();

            await connection!.SendAsync(MemoryPackSerializer.Serialize(op), token).ConfigureAwait(false);
        }

        public async ValueTask SendAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TIn>(int op, TIn payload, CancellationToken token = default)
        {
            TryInitialize();

            await connection!.SendAsync(
                Build(
                    MemoryPackSerializer.Serialize(op),
                    MemoryPackSerializer.Serialize(payload)),
                token).ConfigureAwait(false);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static ReadOnlyMemory<byte> Build(Span<byte> op, Span<byte> payload)
        {
            Span<byte> result = stackalloc byte[op.Length + payload.Length];

            op.CopyTo(result);
            payload.CopyTo(result[4..]);

            return new(result.ToArray());
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}