using MemoryPack;

using System.Buffers;
using System.IO.Pipelines;
using System.Net.Quic;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace SourceCrafter.LiteSpeedLink;

public delegate ValueTask<FlushResult> RequestHandler(long id, RequestContext ctx, CancellationToken token);

public static partial class Server
{
    [RequiresPreviewFeatures]
    [SupportedOSPlatform("windows")]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    public static async ValueTask<QuicListener> StartQuicServerAsync(
        int port,
        RequestHandler handlers, 
        Action onFinalize,
        X509Certificate2 cert,
        CancellationToken token = default)
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

        ListenConnections(listener, handlers, onFinalize, token);

        return listener;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static async void ListenConnections(
            QuicListener listener,
            RequestHandler handlers,
            Action onFinalize,
            CancellationToken token)
        {
            //Console.WriteLine("Waiting clients...");
            try
            {
            DO: HandleConnectionAsync(await listener.AcceptConnectionAsync(token).ConfigureAwait(false), handlers, token); goto DO;
            }
            catch
            {
                return;
            }
            finally 
            {
                onFinalize();
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static async void HandleConnectionAsync(
            QuicConnection connection,
            RequestHandler handlers,
            CancellationToken token)
        {
            //Console.WriteLine("Connected with client...");
            await using (connection)
            {
                try
                {
                DO: await HandleStreamAsync(await connection.AcceptInboundStreamAsync(token).ConfigureAwait(false), handlers, token); goto DO;
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
            RequestHandler handlers,
            CancellationToken token)
        {
            await using (stream)
            {
                var reader = PipeReader.Create(stream);
                PipeWriter? writer = null;
                try
                {
                    while (await reader.ReadAsync(token).ConfigureAwait(false) is { IsCompleted: false, IsCanceled: false, Buffer: { IsEmpty: false } buffer })
                    {
                        await HandleRequestAsync(handlers, buffer, writer ??= PipeWriter.Create(stream), reader, token);
                    }
                }
                catch
                {
                    reader.Complete();
                    writer?.Complete();
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static ValueTask<FlushResult> HandleRequestAsync(RequestHandler handlers, ReadOnlySequence<byte> buffer, PipeWriter writer, PipeReader reader, CancellationToken token)
        {
            try
            {
                RequestContext requestContext = new(buffer.Slice(8), writer);
                return handlers(Deserialize<long>(buffer.Slice(0, 8)), requestContext, token);
            }
            catch (Exception ex)
            {
                return writer.WriteAsync(Serialize(ex.ToString()), token);
            }
            finally
            {
                reader.AdvanceTo(buffer.End);
            }
        }
    }
}


