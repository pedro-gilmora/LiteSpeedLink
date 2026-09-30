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
    /// <summary>Espejo de <c>Client.QuicConnection.MuxMarker</c>: stream reutilizable multiplexado.</summary>
    internal const long MuxMarker = long.MinValue;

    [RequiresPreviewFeatures]
    [SupportedOSPlatform("windows")]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    public static ValueTask<QuicListener> StartQuicServerAsync(
        int port,
        RequestHandler handlers,
        Action onFinalize,
        X509Certificate2 cert,
        CancellationToken token = default,
        int streamBatch = 0,
        TimeSpan streamBatchMaxDelay = default)
        => StartQuicServerAsync(port, new DelegateRequestHandler(handlers), onFinalize, cert, token, streamBatch, streamBatchMaxDelay);

    [RequiresPreviewFeatures]
    [SupportedOSPlatform("windows")]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    public static async ValueTask<QuicListener> StartQuicServerAsync<THandler>(
        int port,
        THandler handlers,
        Action onFinalize,
        X509Certificate2 cert,
        CancellationToken token = default,
        int streamBatch = 0,
        TimeSpan streamBatchMaxDelay = default)
        where THandler : struct, IRequestHandler
    {
        var batch = BatchPolicy.Create(streamBatch, streamBatchMaxDelay);
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


        ListenConnections(listener, handlers, onFinalize, batch, token);

        return listener;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static async void ListenConnections(
            QuicListener listener,
            THandler handlers,
            Action onFinalize,
            BatchPolicy batch,
            CancellationToken token)
        {
            try
            {
            DO: HandleConnectionAsync(await listener.AcceptConnectionAsync(token).ConfigureAwait(false), handlers, batch, token); goto DO;
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
            THandler handlers,
            BatchPolicy batch,
            CancellationToken token)
        {
            await using (connection)
            {
                try
                {
                DO: _ = HandleStreamAsync(await connection.AcceptInboundStreamAsync(token).ConfigureAwait(false), handlers, batch, token); goto DO;
                }
                catch
                {
                    return;
                }
            }
        }

        static async Task HandleStreamAsync(
            QuicStream stream,
            THandler handlers,
            BatchPolicy batch,
            CancellationToken token)
        {
            await using (stream)
            {
                try
                {
                    var reader = PipeReader.Create(stream);
                    var head = await reader.ReadAtLeastAsync(Framing.OpIdSize, token).ConfigureAwait(false);
                    if (head.Buffer.Length >= Framing.OpIdSize && Framing.ReadOpId(head.Buffer) == MuxMarker)
                    {
                        reader.AdvanceTo(head.Buffer.GetPosition(Framing.OpIdSize));
                        await ServePipeAsync(reader, PipeWriter.Create(stream), handlers, token, batch: batch).ConfigureAwait(false);
                    }
                    else
                    {
                        reader.AdvanceTo(head.Buffer.Start);
                        await ServeQuicStreamAsync(reader, PipeWriter.Create(stream), handlers, token, batch).ConfigureAwait(false);
                    }
                }
                catch (Exception ex) when (ex is OperationCanceledException or IOException or QuicException)
                {
                }
            }
        }
    }

    /// <summary>
    /// Un stream QUIC es un RPC: la peticion <c>[opId][cuerpo]</c> la delimita el FIN y el propio
    /// stream empareja las respuestas <c>[len][estado][cuerpo]</c>, asi que no hace falta corrId.
    /// </summary>
    internal static async Task ServeQuicStreamAsync<THandler>(PipeReader reader, PipeWriter writer, THandler handlers, CancellationToken token, BatchPolicy batch = default)
        where THandler : struct, IRequestHandler
    {
        try
        {
            ReadResult result;

            while (!(result = await reader.ReadAsync(token).ConfigureAwait(false)).IsCompleted)
            {
                if (result.Buffer.Length > Framing.MaxFrameSize) throw new InvalidDataException($"Request too large: {result.Buffer.Length}.");

                reader.AdvanceTo(result.Buffer.Start, result.Buffer.End);
            }

            var request = result.Buffer;

            if (request.Length is < Framing.OpIdSize or > Framing.MaxFrameSize) throw new InvalidDataException($"Invalid request length: {request.Length}.");

            long op = Framing.ReadOpId(request);
            int length = (int)request.Length - Framing.OpIdSize;
            byte[] body = ArrayPool<byte>.Shared.Rent(length);
            request.Slice(Framing.OpIdSize).CopyTo(body);
            reader.AdvanceTo(request.End);

            await DispatchAsync(handlers, op, new(0, body.AsMemory(0, length), new ResponseChannel(writer, correlated: false, batch: batch), token), body, null, token).ConfigureAwait(false);
        }
        finally
        {
            await reader.CompleteAsync().ConfigureAwait(false);
            await writer.CompleteAsync().ConfigureAwait(false);
        }
    }
}


