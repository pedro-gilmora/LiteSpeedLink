using MemoryPack;

using System;
using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.IO.Pipelines;
using System.Net.Quic;
using System.Reflection;
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
             CancellationToken token = default, 
             [CallerMemberName] string name = "")
        {
            await TryInitializeAsync(token);

            ReadOnlySequence<byte> buffer = default;

            try
            {
                await writer!.WriteAsync(
                       BuildRequest(
                           MemoryPackSerializer.Serialize(op),
                           MemoryPackSerializer.Serialize(payload)),
                       token);

                buffer = (await reader!.ReadAsync(token)).Buffer;

                var result = MemoryPackSerializer.Deserialize<TOut>(buffer);

                reader.AdvanceTo(buffer.End);

                return result;
            }
            catch (MemoryPackSerializationException ex)
            {
                if (ex.StackTrace?.Contains("MemoryPack.MemoryPackSerializer.Serialize[") is true)
                {
                    throw new ArgumentException("Invalid parameters", ex);
                }
                else if (ex.StackTrace?.Contains("MemoryPack.MemoryPackSerializer.Deserialize[") is true && !buffer.IsEmpty)
                {
                    try
                    {
                        var (@__exResponseStatus, @__serverExceptionMessage) = MemoryPackSerializer.Deserialize<(ResponseStatus, string)>(buffer);

                        switch (@__exResponseStatus)
                        {
                            case ResponseStatus.NotFound:

                                throw new NotImplementedException(@$"Implementation is missing from {connection!.RemoteEndPoint}");

                            case ResponseStatus.Failed:

                                throw new InvalidOperationException(@$"Execution failed on {connection!.RemoteEndPoint}:
REASON:

{@__serverExceptionMessage}
");

                            default:

                                throw;
                        }
                    }
                    catch
                    {
                        throw;
                    }
                }
                else
                {
                    throw;
                }
            }
            catch
            {
                throw;
            }
        }

        public async ValueTask<TOut?> GetAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TOut>
            (int op,
             CancellationToken token = default,
             [CallerMemberName] string name = "")
        {
            await TryInitializeAsync(token);

            ReadOnlySequence<byte> buffer = default;

            try
            {
                MemoryPackSerializer.Serialize(writer!, op);

                await writer!.FlushAsync(token);

                buffer = (await reader!.ReadAsync(token)).Buffer;

                var result = MemoryPackSerializer.Deserialize<TOut>(buffer);

                reader.AdvanceTo(buffer.End);

                return result;
            }
            catch (MemoryPackSerializationException ex)
            {
                if (ex.StackTrace?.Contains("MemoryPack.MemoryPackSerializer.Serialize[") is true)
                {
                    throw new ArgumentException("Invalid parameters", ex);
                }
                else if (ex.StackTrace?.Contains("MemoryPack.MemoryPackSerializer.Deserialize[") is true && !buffer.IsEmpty)
                {
                    try
                    {
                        var (@__exResponseStatus, @__serverExceptionMessage) = MemoryPackSerializer.Deserialize<(ResponseStatus, string)>(buffer);

                        switch (@__exResponseStatus)
                        {
                            case ResponseStatus.NotFound:

                                throw new NotImplementedException(@$"Implementation is missing from {connection!.RemoteEndPoint}");

                            case ResponseStatus.Failed:

                                throw new InvalidOperationException(@$"Execution failed on {connection!.RemoteEndPoint}:
REASON:

{@__serverExceptionMessage}
");

                            default:

                                throw;
                        }
                    }
                    catch
                    {
                        throw;
                    }
                }
                else
                {
                    throw;
                }
            }
            catch
            {
                throw;
            }
        }

        public async ValueTask SendAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TIn>
            (int op,
             TIn payload,
             CancellationToken token = default,
             [CallerMemberName] string name = "")
        {
            await TryInitializeAsync(token);

            ReadOnlySequence<byte> buffer = default;

            try
            {
                await writer!.WriteAsync(
                    BuildRequest(
                        MemoryPackSerializer.Serialize(op),
                        MemoryPackSerializer.Serialize(payload)),
                    token);

                switch ((ResponseStatus)(buffer = (await reader!.ReadAsync(token)).Buffer).FirstSpan[0])
                {
                    case ResponseStatus.NotFound:

                        throw new NotImplementedException(@$"Implementation is missing from {connection!.RemoteEndPoint}");

                    case ResponseStatus.Failed:

                        throw new InvalidOperationException(@$"Execution failed on {connection!.RemoteEndPoint}:
REASON:

{MemoryPackSerializer.Deserialize<string>(buffer.Slice(1))}
");
                }
            }
            catch (MemoryPackSerializationException ex)
            {
                if (ex.StackTrace?.Contains("MemoryPack.MemoryPackSerializer.Serialize[") is true)
                {
                    throw new ArgumentException("Invalid parameters", ex);
                }
                else
                {
                    throw;
                }
            }
            catch
            {
                throw;
            }
            finally
            {
                if(buffer.IsEmpty) reader!.AdvanceTo(buffer.End);
            }
        }

        public async ValueTask SendAsync(
            int op, 
            CancellationToken token = default,
            [CallerMemberName] string name = "")
        {
            await TryInitializeAsync(token);

            ReadOnlySequence<byte> buffer = default;

            try
            {
                MemoryPackSerializer.Serialize(writer!, op);

                await writer!.FlushAsync(token);

                switch ((ResponseStatus)(buffer = (await reader!.ReadAsync(token)).Buffer).FirstSpan[0])
                {
                    case ResponseStatus.NotFound:

                        throw new NotImplementedException(@$"Implementation is missing from {connection!.RemoteEndPoint}");

                    case ResponseStatus.Failed:

                        throw new InvalidOperationException(@$"Execution failed on {connection!.RemoteEndPoint}:
REASON:

{MemoryPackSerializer.Deserialize<string>(buffer.Slice(1))}
");
                }
            }
            catch (MemoryPackSerializationException ex)
            {
                if (ex.StackTrace?.Contains("MemoryPack.MemoryPackSerializer.Serialize[") is true)
                {
                    throw new ArgumentException("Invalid parameters", ex);
                }
                else
                {
                    throw;
                }
            }
            catch
            {
                throw;
            }
            finally
            {
                if (buffer.IsEmpty) reader!.AdvanceTo(buffer.End);
            }
        }

        public async IAsyncEnumerable<TOut?> EnumerateAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TIn, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TOut>
            (int op,
             TIn payload,
             [EnumeratorCancellation] CancellationToken token = default,
             [CallerMemberName] string name = "")
        {
            await TryInitializeAsync(token);

            try
            {
                await writer!.WriteAsync(
                    BuildRequest(
                        MemoryPackSerializer.Serialize(op),
                        MemoryPackSerializer.Serialize(payload)),
                    token);
            }
            catch (MemoryPackSerializationException ex)
            {
                if (ex.StackTrace?.Contains("MemoryPack.MemoryPackSerializer.Serialize[") is true)
                {
                    throw new ArgumentException("Invalid parameters", ex);
                }
                else
                {
                    throw;
                }
            }

            int chunkLength;
            bool hasNext = false;

            TOut? item;

            while (await reader!.ReadAsync(token).ConfigureAwait(false) is { IsCompleted: false, IsCanceled: false, Buffer: { First: { } segment, IsEmpty: false, Start: { } position, End: { } end } buffer })
            {
                while (buffer.TryGet(ref position, out segment))
                {
                    int start = 0;

                    while (start < segment.Length && (hasNext = (chunkLength = BitConverter.ToInt32(segment.Span.Slice(Interlocked.Exchange(ref start, start + 4), 4))) > -1))
                    {
                        try
                        {
                            item = MemoryPackSerializer.Deserialize<TOut>(segment.Span.Slice(Interlocked.Exchange(ref start, start + chunkLength), chunkLength));
                        }
                        catch (MemoryPackSerializationException ex)
                        {
                            if (ex.StackTrace?.Contains("MemoryPack.MemoryPackSerializer.Deserialize[") is true && !buffer.IsEmpty)
                            {
                                try
                                {
                                    switch (MemoryPackSerializer.Deserialize<ResponseStatus>(buffer.Slice(0, 1)))
                                    {
                                        case ResponseStatus.NotFound:

                                            throw new NotImplementedException(@$"Implementation is missing from {connection!.RemoteEndPoint}");

                                        case ResponseStatus.Failed:

                                            throw new InvalidOperationException(@$"Execution failed on {connection!.RemoteEndPoint}:
REASON:

{MemoryPackSerializer.Deserialize<string>(buffer.Slice(1))}
");

                                        default:

                                            throw;
                                    }
                                }
                                catch
                                {
                                    throw;
                                }
                            }
                            else
                            {
                                throw;
                            }
                        }
                        yield return item;
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
             [EnumeratorCancellation] CancellationToken token = default,
             [CallerMemberName] string name = "")
        {
            await TryInitializeAsync(token);

            try
            {
                MemoryPackSerializer.Serialize(writer!, op);

                await writer!.FlushAsync(token);
            }
            catch (MemoryPackSerializationException ex)
            {
                if (ex.StackTrace?.Contains("MemoryPack.MemoryPackSerializer.Serialize[") is true)
                {
                    throw new ArgumentException("Invalid parameters", ex);
                }
                else
                {
                    throw;
                }
            }

            int chunkLength;
            bool hasNext = false;

            TOut? item;

            while (await reader!.ReadAsync(token).ConfigureAwait(false) is { IsCompleted: false, IsCanceled: false, Buffer: { First: { } segment, IsEmpty: false, Start: { } position, End: { } end } buffer })
            {
                while (buffer.TryGet(ref position, out segment))
                {
                    int start = 0;

                    while (start < segment.Length && (hasNext = (chunkLength = BitConverter.ToInt32(segment.Span.Slice(Interlocked.Exchange(ref start, start + 4), 4))) > -1))
                    {
                        try
                        {
                            item = MemoryPackSerializer.Deserialize<TOut>(segment.Span.Slice(Interlocked.Exchange(ref start, start + chunkLength), chunkLength));
                        }
                        catch (MemoryPackSerializationException ex)
                        {
                            if (ex.StackTrace?.Contains("MemoryPack.MemoryPackSerializer.Deserialize[") is true && !buffer.IsEmpty)
                            {
                                try
                                {
                                    switch (MemoryPackSerializer.Deserialize<ResponseStatus>(buffer.Slice(0, 1)))
                                    {
                                        case ResponseStatus.NotFound:

                                            throw new NotImplementedException(@$"Implementation is missing from {connection!.RemoteEndPoint}");

                                        case ResponseStatus.Failed:

                                            throw new InvalidOperationException(@$"Execution failed on {connection!.RemoteEndPoint}:
REASON:

{MemoryPackSerializer.Deserialize<string>(buffer.Slice(1))}
");

                                        default:

                                            throw;
                                    }
                                }
                                catch
                                {
                                    throw;
                                }
                            }
                            else
                            {
                                throw;
                            }
                        }
                        yield return item;
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