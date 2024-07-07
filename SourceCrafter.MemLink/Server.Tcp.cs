using MemoryPack;

using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.IO.Pipelines;
using System.Net.Quic;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Runtime.Versioning;

namespace SourceCrafter.LiteSpeedLink;

public partial class Server
{
    public static TcpListener StartTcpServer(
        int port,
        Dictionary<int, RequestHandler> handlers,
        X509Certificate2? cert = default,
        CancellationToken token = default)
    {
        // Generate a self-signed certificate if in debug mode

        var tcpServer = TcpListener.Create(port);

        tcpServer.Server.NoDelay = true;

        tcpServer.Start();

        ListenClientsAsync(tcpServer, handlers, cert, token);

        return tcpServer;

        static async void ListenClientsAsync(
            TcpListener tcpServer,
            Dictionary<int, RequestHandler> handlers,
            X509Certificate2? cert = default,
            CancellationToken token = default)
        {
            try
            {
            DO: HandleConnectionAsync(await tcpServer.AcceptTcpClientAsync(token), handlers, cert, token); goto DO;
            }
            catch (Exception ex)
                when (ex is SocketException { SocketErrorCode: SocketError.ConnectionAborted or SocketError.OperationAborted })
            {
            }
        }

        static async void HandleConnectionAsync(
            TcpClient tcpClient,
            Dictionary<int, RequestHandler> handlers,
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
                    });
            }

            var reader = PipeReader.Create(stream);
            var writer = PipeWriter.Create(stream);

            while (await reader.ReadAtLeastAsync(4) is { IsCompleted: false, IsCanceled: false, Buffer: { IsEmpty: false, End: { } end } buffer })
            {
                HandleRequestAsync(handlers, buffer, reader, writer, token);
            }
        }

        static async void HandleRequestAsync(
            Dictionary<int, RequestHandler> handlers,
            ReadOnlySequence<byte> requestBuffer,
            PipeReader reader,
            PipeWriter writer,
            CancellationToken token)
        {
            try
            {
                await (handlers.TryGetValue(BitConverter.ToInt32(requestBuffer.Slice(0, 4).FirstSpan), out var requestHandler)
                    ? requestHandler(new(requestBuffer.Slice(4), writer), token)
                    : NotFound(writer, token));
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

public delegate ValueTask<FlushResult> RequestHandler(RequestContext _, CancellationToken cancel);

public class RequestContext(ReadOnlySequence<byte> bytes, PipeWriter writer)
{
    internal static readonly ReadOnlyMemory<byte> streammingEnd = new([255, 255, 255, 255]);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TOut? Read<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TOut>()
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