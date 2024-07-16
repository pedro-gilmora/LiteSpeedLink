using MemoryPack;

using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.IO.Pipelines;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace SourceCrafter.LiteSpeedLink;

public static partial class Server
{
    public static TcpListener StartTcpServer<TServiceProvider>(
        int port,
        TServiceProvider provider,
        Dictionary<int, RequestHandler<TServiceProvider>> handlers,
        X509Certificate2? cert = default,
        CancellationToken token = default) where TServiceProvider : IServiceProvider, IDisposable, IAsyncDisposable
    {
        // Generate a self-signed certificate if in debug mode

        var tcpServer = TcpListener.Create(port);

        tcpServer.Server.NoDelay = true;

        tcpServer.Start();

        ListenClientsAsync(tcpServer, provider, handlers, cert, token);

        return tcpServer;

        static async void ListenClientsAsync(
            TcpListener tcpServer,
            TServiceProvider provider,
            Dictionary<int, RequestHandler<TServiceProvider>> handlers,
            X509Certificate2? cert = default,
            CancellationToken token = default)
        {
            try
            {
            DO: HandleConnectionAsync(await tcpServer.AcceptTcpClientAsync(token), provider, handlers, cert, token); goto DO;
            }
            catch (Exception ex)
                when (ex is SocketException { SocketErrorCode: SocketError.ConnectionAborted or SocketError.OperationAborted })
            {
            }
        }

        static async void HandleConnectionAsync(
            TcpClient tcpClient,
            TServiceProvider provider,
            Dictionary<int, RequestHandler<TServiceProvider>> handlers,
            X509Certificate2? cert = default,
            CancellationToken token = default)
        {
            //Console.WriteLine($"Connected with {tcpClient.Client.RemoteEndPoint}");

            Stream stream;

            if (cert == null)
            {
                stream = tcpClient.GetStream();
            }
            else
            {
                await ((SslStream)(stream = new SslStream(tcpClient.GetStream(), false)))
                    .AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                    {
                        EnabledSslProtocols = SslProtocols.Tls12,
                        ServerCertificate = cert,
                        ApplicationProtocols = [Constants.protocol, Constants.protocolStream],
                        ClientCertificateRequired = true,
                        CertificateRevocationCheckMode = X509RevocationMode.NoCheck // Adjust as necessary
                    }, token);
            }

            var reader = PipeReader.Create(stream);
            var writer = PipeWriter.Create(stream);

            while (await reader.ReadAsync(token).ConfigureAwait(false) is { IsCompleted: false, IsCanceled: false, Buffer: { IsEmpty: false, End: { } end } buffer })
            {
                HandleRequestAsync(provider, handlers, buffer, reader, writer, token);
            }
        }

        static async void HandleRequestAsync(
            TServiceProvider provider,
            Dictionary<int, RequestHandler<TServiceProvider>> handlers,
            ReadOnlySequence<byte> requestBuffer,
            PipeReader reader,
            PipeWriter writer,
            CancellationToken token)
        {
            try
            {
                await (handlers.TryGetValue(BitConverter.ToInt32(requestBuffer.Slice(0, 4).FirstSpan), out var requestHandler)
                    ? requestHandler(new(provider, requestBuffer.Slice(4), writer), token).ConfigureAwait(false)
                    : NotFound(writer, token).ConfigureAwait(false));
            }
            catch (Exception ex)
            {
                await writer.WriteAsync(MemoryPackSerializer.Serialize((-1, ex.ToString())), token);
            }
            finally
            {
                reader.AdvanceTo(requestBuffer.End);
            }
        }

        static ValueTask<FlushResult> NotFound(PipeWriter writer, CancellationToken token)
        {
            return writer.WriteAsync(MemoryPackSerializer.Serialize(false), token);
        }
    }
}

public delegate ValueTask<FlushResult> RequestHandler<TServiceProvider>(RequestContext<TServiceProvider> _, CancellationToken cancel) where TServiceProvider : IServiceProvider;

public class RequestContext<TServiceProvider>(TServiceProvider provider, ReadOnlySequence<byte> bytes, PipeWriter writer) where TServiceProvider : IServiceProvider
{
    internal static readonly ReadOnlyMemory<byte> streammingEnd = new([255, 255, 255, 255]);

    public TServiceProvider Provider { get; } = provider;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TOut? Get<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TOut>()
    {
        return MemoryPackSerializer.Deserialize<TOut>(bytes);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask<FlushResult> ReturnAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TOut>(TOut? payload, CancellationToken token = default)
    {
        return writer.WriteAsync(MemoryPackSerializer.Serialize(payload), token);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask<FlushResult> YieldAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TIn>(TIn payload, CancellationToken token = default)
    {
        Memory<byte> bytes = MemoryPackSerializer.Serialize((0, payload));
        BitConverter.GetBytes(bytes.Length - 4).CopyTo(bytes);
        return writer.WriteAsync(bytes, token);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask<FlushResult> EndStreamingAsync(CancellationToken token)
    {
        return writer.WriteAsync(streammingEnd, token);
    }
}