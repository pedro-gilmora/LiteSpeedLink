using MemoryPack;

using System;
using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.IO.Pipelines;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace SourceCrafter.LiteSpeedLink;

public static partial class Server
{
    public static TcpListener StartTcpServer(
        int port,
        RequestHandler handlers,
        Action onFinalize,
        X509Certificate2? cert = default,
        CancellationToken token = default)
    {
        // Generate a self-signed certificate if in debug mode

        var tcpServer = TcpListener.Create(port);

        tcpServer.Server.NoDelay = true;

        tcpServer.Start();

        ListenClientsAsync(tcpServer, handlers, onFinalize, cert, token);

        return tcpServer;

        static async void ListenClientsAsync(
            TcpListener tcpServer,
            RequestHandler handlers,
            Action onFinalize,
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
            finally
            {
                onFinalize();
            }
        }

        static async void HandleConnectionAsync(
            TcpClient tcpClient,
            RequestHandler handlers,
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
            PipeWriter? writer = null;

            try
            {

                while (await reader.ReadAsync(token).ConfigureAwait(false) is { IsCompleted: false, IsCanceled: false, Buffer: { IsEmpty: false, End: { } end } buffer })
                {
                    HandleRequestAsync(handlers, buffer, reader, writer ??= PipeWriter.Create(stream), token);
                }

            }
            finally
            {
                reader.Complete();
                writer?.Complete();
            }
        }

        static async void HandleRequestAsync(
            RequestHandler handlers,
            ReadOnlySequence<byte> requestBuffer,
            PipeReader reader,
            PipeWriter writer,
            CancellationToken token)
        {
            try
            {
                long id = BitConverter.ToInt64(requestBuffer.Slice(0, 8).FirstSpan);


                await handlers(id, new RequestContext(requestBuffer.Slice(8), writer), token);
            }
            catch (Exception ex)
            {
                await writer.WriteAsync(Serialize((ResponseStatus.Failed, ex.ToString())), token);
            }
            finally
            {
                reader.AdvanceTo(requestBuffer.End);
            }
        }

        //static ValueTask<FlushResult> NotFound(PipeWriter writer, CancellationToken token)
        //{
        //    return writer.WriteAsync(MemoryPackSerializer.Serialize(false), token);
        //}
    }
}

public delegate ValueTask<FlushResult> YieldAsyncHandler<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TIn>(TIn payload, CancellationToken token = default);

public sealed class RequestContext(ReadOnlySequence<byte> bytes, PipeWriter writer) 
{
    internal static readonly ReadOnlyMemory<byte> streammingEnd = new([255, 255, 255, 255]);
    private readonly ReadOnlySequence<byte> bytes = bytes;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TOut? Get<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TOut>() => Deserialize<TOut>(bytes);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask<FlushResult> ReturnAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TOut>(TOut? payload, CancellationToken token = default) => writer.WriteAsync(Serialize(payload), token);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask<FlushResult> YieldAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TIn>(TIn payload, CancellationToken token = default)
    {
        Memory<byte> bytes = Serialize((0, payload));
        BitConverter.GetBytes(bytes.Length - 4).CopyTo(bytes);
        return writer.WriteAsync(bytes, token);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask<FlushResult> EndStreamingAsync(CancellationToken token) => writer.WriteAsync(streammingEnd, token);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public async ValueTask<FlushResult> EnumerateAsync<TData>(Func<IAsyncEnumerable<TData>> value, CancellationToken token = default)
    {
        await foreach (var item in value()) await YieldAsync(item, token).ConfigureAwait(true);

        return await EndStreamingAsync(token);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public async ValueTask<FlushResult> EnumerateAsync<TData>(Func<IEnumerable<TData>> value, CancellationToken token = default)
    {
        foreach (var item in value()) await YieldAsync(item, token).ConfigureAwait(true);

        return await EndStreamingAsync(token);
    }
}