using MemoryPack;

using System.Diagnostics.CodeAnalysis;
using System.IO.Pipelines;
using System.Net.Quic;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;

namespace SourceCrafter.LiteSpeedLink.Client;

public static partial class ClientExtensions
{
    [RequiresPreviewFeatures]
    [SupportedOSPlatform("windows")]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]

    private sealed class QuicConnection(QuicClientConnectionOptions options) : IConnection
    {
        internal System.Net.Quic.QuicConnection? connection;
        internal QuicStream? stream;
        internal PipeReader? reader;
        internal PipeWriter? writer;

        internal static readonly SemaphoreSlim roundtripLock = new(1);

        public async ValueTask DisposeAsync()
        {
            reader?.Complete();
            writer?.Complete();
            if (stream is not null)
                await stream.DisposeAsync();
            if (connection is not null)
                await connection.DisposeAsync();
        }

        internal async ValueTask TryInitializeAsync(CancellationToken token)
        {
            if (stream is not null) return;

            stream = await (connection ??= await System.Net.Quic.QuicConnection.ConnectAsync(options, token))
                .OpenOutboundStreamAsync(QuicStreamType.Bidirectional, token);

            reader = PipeReader.Create(stream);
            writer = PipeWriter.Create(stream);
        }

        public async ValueTask<TOut?> GetAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TIn, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TOut>
            (int op,
             TIn payload,
             TOut? _,
             CancellationToken token = default)
        {
            await TryInitializeAsync(token);

            await writer!.WriteAsync(
                BuildRequest(
                    MemoryPackSerializer.Serialize(op),
                    MemoryPackSerializer.Serialize(payload)),
                token);

            var buffer = (await reader!.ReadAsync(token)).Buffer;

            var result = MemoryPackSerializer.Deserialize<TOut>(buffer);

            reader.AdvanceTo(buffer.End);

            return result;
        }

        public async ValueTask<TOut?> GetAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TOut>
            (int op,
             CancellationToken token = default)
        {
            await TryInitializeAsync(token);

            MemoryPackSerializer.Serialize(writer!, op);

            await writer!.FlushAsync(token);

            var buffer = (await reader!.ReadAsync(token)).Buffer;

            var result = MemoryPackSerializer.Deserialize<TOut>(buffer);

            reader.AdvanceTo(buffer.End);

            return result;
        }

        public async ValueTask SendAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TIn>
            (int op,
             TIn payload,
             CancellationToken token = default)
        {
            await TryInitializeAsync(token);

            await writer!.WriteAsync(
                BuildRequest(
                    MemoryPackSerializer.Serialize(op),
                    MemoryPackSerializer.Serialize(payload)),
                token);
        }

        public async ValueTask SendAsync(int op, CancellationToken token = default)
        {
            await TryInitializeAsync(token);

            MemoryPackSerializer.Serialize(writer!, op);

            await writer!.FlushAsync(token);
        }

        public async IAsyncEnumerable<TOut?> EnumerateAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TIn, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TOut>
            (int op,
             TIn payload,
             TOut? _,
             [EnumeratorCancellation] CancellationToken token = default)
        {
            await TryInitializeAsync(token);

            await writer!.WriteAsync(
                BuildRequest(
                    MemoryPackSerializer.Serialize(op),
                    MemoryPackSerializer.Serialize(payload)),
                token);

            int chunkLength;
            bool hasNext = false;

            while (await reader!.ReadAsync(token).ConfigureAwait(false) is { IsCompleted: false, IsCanceled: false, Buffer: { First: { } segment, IsEmpty: false, Start: { } position, End: { } end } buffer })
            {
                while (buffer.TryGet(ref position, out segment))
                {
                    int start = 0;

                    while (start < segment.Length && (hasNext = ((chunkLength = BitConverter.ToInt32(segment.Span.Slice(Interlocked.Exchange(ref start, start + 4), 4))) > -1)))
                    {
                        yield return MemoryPackSerializer.Deserialize<TOut>(segment.Span.Slice(Interlocked.Exchange(ref start, start + chunkLength), chunkLength));
                    }

                    if (hasNext)
                    {
                        reader.AdvanceTo(position);
                    }
                    else
                    {
                        reader.AdvanceTo(end);
                        yield break;
                    }
                }

                reader.AdvanceTo(end);
            }
        }

        public async IAsyncEnumerable<TOut?> EnumerateAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TOut>
            (int op,
             [EnumeratorCancellation] CancellationToken token = default)
        {
            await TryInitializeAsync(token);

            MemoryPackSerializer.Serialize(writer!, op);

            await writer!.FlushAsync(token);

            int chunkLength;
            bool hasNext = false;

            while (await reader!.ReadAsync(token).ConfigureAwait(false) is { IsCompleted: false, IsCanceled: false, Buffer: { First: { } segment, IsEmpty: false, Start: { } position, End: { } end } buffer })
            {
                while (buffer.TryGet(ref position, out segment))
                {
                    int start = 0;

                    while (start < segment.Length && (hasNext = ((chunkLength = BitConverter.ToInt32(segment.Span.Slice(Interlocked.Exchange(ref start, start + 4), 4))) > -1)))
                    {
                        yield return MemoryPackSerializer.Deserialize<TOut>(segment.Span.Slice(Interlocked.Exchange(ref start, start + chunkLength), chunkLength));
                    }

                    if (hasNext)
                    {
                        reader.AdvanceTo(position);
                    }
                    else
                    {
                        reader.AdvanceTo(end);
                        yield break;
                    }
                }

                reader.AdvanceTo(end);
            }
        }


        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static ReadOnlyMemory<byte> BuildRequest(Span<byte> op, Span<byte> payload)
        {
            Span<byte> result = stackalloc byte[op.Length + payload.Length];

            op.CopyTo(result);
            payload.CopyTo(result[4..]);

            return new(result.ToArray());
        }

        public void Dispose()
        {
            DisposeAsync().ConfigureAwait(false).GetAwaiter().GetResult();
        }
    }
}