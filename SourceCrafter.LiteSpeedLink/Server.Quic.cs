using MemoryPack;

using System.Buffers;
using System.IO.Pipelines;
using System.Net.Quic;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace SourceCrafter.LiteSpeedLink;

public static partial class Server
{
    [RequiresPreviewFeatures]
    [SupportedOSPlatform("windows")]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    public static async ValueTask<QuicListener> StartQuicServerAsync<TServiceProvider>(
        int port,
        TServiceProvider provider,
        Dictionary<int, RequestHandler<TServiceProvider>> handlers,
        X509Certificate2 cert,
        CancellationToken token = default) where TServiceProvider : IServiceProvider, IDisposable, IAsyncDisposable
    {
        QuicServerConnectionOptions connectionOptions = new()
        {
            IdleTimeout = TimeSpan.FromMinutes(5),
            MaxInboundBidirectionalStreams = 1000,
            MaxInboundUnidirectionalStreams = 10,
            DefaultStreamErrorCode = 0x0A,
            DefaultCloseErrorCode = 0x0B,
            ServerAuthenticationOptions = new()
            {
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                ApplicationProtocols = [Constants.protocol, Constants.protocolStream],
                ServerCertificate = cert,
                ClientCertificateRequired = false
            }
        };

        var listenerOptions = new QuicListenerOptions
        {
            ListenEndPoint = new System.Net.IPEndPoint(new System.Net.IPAddress(new byte[16]), port),
            ApplicationProtocols = [Constants.protocol, Constants.protocolStream],
            ConnectionOptionsCallback = (connection, sslHello, token) => new(connectionOptions)
        };

        var listener = await QuicListener
            .ListenAsync(listenerOptions, token)
            .ConfigureAwait(false);

        //Console.WriteLine("Server started...");

        ListenConnections(provider, handlers, token);

        return listener;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        async void ListenConnections(
            TServiceProvider provider,
            Dictionary<int, RequestHandler<TServiceProvider>> handlers,
            CancellationToken token)
        {
            //Console.WriteLine("Waiting clients...");
            try
            {
            DO: HandleConnectionAsync(await listener.AcceptConnectionAsync(token).ConfigureAwait(false), provider, handlers, token); goto DO;
            }
            catch
            {
                return;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        async void HandleConnectionAsync(
            QuicConnection connection,
            TServiceProvider provider,
            Dictionary<int, RequestHandler<TServiceProvider>> handlers,
            CancellationToken token)
        {
            //Console.WriteLine("Connected with client...");
            await using (connection)
            {
                try
                {
                DO: await HandleStreamAsync(await connection.AcceptInboundStreamAsync(token).ConfigureAwait(false), provider, handlers, token); goto DO;
                }
                catch
                {
                    return;
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static async ValueTask HandleStreamAsync(
            QuicStream stream,
            TServiceProvider provider,
            Dictionary<int, RequestHandler<TServiceProvider>> handlers,
            CancellationToken token)
        {
            await using (stream)
            {
                var reader = PipeReader.Create(stream);
                var writer = PipeWriter.Create(stream);
                try
                {
                    while (await reader.ReadAsync(token).ConfigureAwait(false) is { IsCompleted: false, IsCanceled: false, Buffer: { IsEmpty: false } buffer })
                    {
                        await HandleRequestAsync(provider, handlers, buffer, writer, reader, token);
                    }
                }
                catch
                {
                    reader.Complete();
                    writer.Complete();
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static ValueTask<FlushResult> HandleRequestAsync(TServiceProvider provider, Dictionary<int, RequestHandler<TServiceProvider>> handlers, ReadOnlySequence<byte> buffer, PipeWriter writer, PipeReader reader, CancellationToken token)
        {
            try
            {
                return handlers.TryGetValue(BitConverter.ToInt32(buffer.Slice(0, 4).FirstSpan), out var requestHandler)
                    ? requestHandler(new(provider, buffer.Slice(4), writer), token)
                    : NotFound(writer, token);
            }
            catch (Exception ex)
            {
                return writer.WriteAsync(
                    MemoryPackSerializer.Serialize(ex.ToString()), token);
            }
            finally
            {
                reader.AdvanceTo(buffer.End);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static ValueTask<FlushResult> NotFound(PipeWriter writer, CancellationToken token)
        {
            return writer.WriteAsync(MemoryPackSerializer.Serialize(false), token);
        }
    }
}


