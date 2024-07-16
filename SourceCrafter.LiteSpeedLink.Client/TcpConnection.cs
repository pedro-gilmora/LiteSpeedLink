using MemoryPack;
using System.Diagnostics.CodeAnalysis;
using System.Net.Sockets;
using System.Net;
using System.Runtime.CompilerServices;
using System.IO.Pipelines;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Buffers;

namespace SourceCrafter.LiteSpeedLink.Client;

public partial class ClientExtensions
{
    private sealed class TcpConnection(EndPoint endpoint, X509Certificate2? cert = default) : IConnection
    {
        internal TcpClient? connection;
        internal Stream? stream;
        internal PipeReader? reader;
        internal PipeWriter? writer;

        public async ValueTask DisposeAsync()
        {
            reader?.Complete();
            writer?.Complete();
            if (stream is not null)
                await stream.DisposeAsync();
            if (connection is not null)
                connection.Dispose();
        }

        internal async ValueTask<TcpConnection> TryInitializeAsync(CancellationToken token)
        {
            if (stream is not null) return this;

            connection ??= new TcpClient();

            switch (endpoint)
            {
                case DnsEndPoint { Host: string host, Port: int port }:

                    await connection.ConnectAsync(host, port, token);

                    stream = connection.GetStream();

                    if (cert is not null)
                    {
                        await ((SslStream)(stream = new SslStream(stream, false)))
                            .AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                            {
                                TargetHost = host,
                                ClientCertificates = [cert],
                                EnabledSslProtocols = SslProtocols.Tls12,
                                CertificateRevocationCheckMode = X509RevocationMode.NoCheck
                            }, token);
                    }

                    break;

                case IPEndPoint ip:

                    await connection.ConnectAsync(ip, token);

                    stream = connection.GetStream();

                    if (cert is not null)
                    {
                        await ((SslStream)(stream = new SslStream(stream, false)))
                            .AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                            {
                                TargetHost = ip.Address.ToString(),
                                ClientCertificates = [cert],
                                EnabledSslProtocols = SslProtocols.Tls12,
                                CertificateRevocationCheckMode = X509RevocationMode.NoCheck
                            }, token);
                    }

                    break;

                default:

                    throw new Exception($"Unsupported endpoint: [{endpoint.ToString()}]");
            }

            reader = PipeReader.Create(stream);
            writer = PipeWriter.Create(stream);

            return this;
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
                        switch (MemoryPackSerializer.Deserialize<ResponseStatus>(buffer.Slice(0, 1)))
                        {
                            case ResponseStatus.NotFound:

                                throw new NotImplementedException(@$"Implementation is missing from {connection!.Client.RemoteEndPoint}");

                            case ResponseStatus.Failed:

                                throw new InvalidOperationException(@$"Execution failed on {connection!.Client.RemoteEndPoint}:
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
                        switch (MemoryPackSerializer.Deserialize<ResponseStatus>(buffer.Slice(0,1)))
                        {
                            case ResponseStatus.NotFound:

                                throw new NotImplementedException(@$"Implementation is missing from {connection!.Client.RemoteEndPoint}");

                            case ResponseStatus.Failed:

                                throw new InvalidOperationException(@$"Execution failed on {connection!.Client.RemoteEndPoint}:
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

                switch (MemoryPackSerializer.Deserialize<ResponseStatus>(buffer = (await reader!.ReadAsync(token)).Buffer))
                {
                    case ResponseStatus.NotFound:

                        throw new NotImplementedException(@$"Implementation is missing from {connection!.Client.RemoteEndPoint}");

                    case ResponseStatus.Failed:

                        throw new InvalidOperationException(@$"Execution failed on {connection!.Client.RemoteEndPoint}:
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

                switch (MemoryPackSerializer.Deserialize<ResponseStatus>((buffer = (await reader!.ReadAsync(token)).Buffer).Slice(0, 1)))
                {
                    case ResponseStatus.NotFound:

                        throw new NotImplementedException(@$"Implementation is missing from {connection!.Client.RemoteEndPoint}");

                    case ResponseStatus.Failed:

                        throw new InvalidOperationException(@$"Execution failed on {connection!.Client.RemoteEndPoint}:
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

                                            throw new NotImplementedException(@$"Implementation is missing from {connection!.Client.RemoteEndPoint}");

                                        case ResponseStatus.Failed:

                                            throw new InvalidOperationException(@$"Execution failed on {connection!.Client.RemoteEndPoint}:
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
                                    switch (MemoryPackSerializer.Deserialize<ResponseStatus>(buffer.Slice(0,1)))
                                    {
                                        case ResponseStatus.NotFound:

                                            throw new NotImplementedException(@$"Implementation is missing from {connection!.Client.RemoteEndPoint}");

                                        case ResponseStatus.Failed:

                                            throw new InvalidOperationException(@$"Execution failed on {connection!.Client.RemoteEndPoint}:
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
            DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }
}
