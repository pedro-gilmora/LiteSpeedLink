using System.Buffers;
using System.Diagnostics;
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
        TlsMode tls = TlsMode.Default,
        CertificateCheck check = CertificateCheck.Pinned,
        int partialFrameTimeoutMs = PartialFrameTimeoutMs,
        int minDataRate = MinDataRateBytesPerSecond,
        int dataRateGraceMs = DataRateGraceMs)
        => StartQuicServerAsync(port, new DelegateRequestHandler(handlers), onFinalize, cert, token, tls, check, partialFrameTimeoutMs, minDataRate, dataRateGraceMs);

    /// <summary>Streams bidireccionales por conexion; al llegar, QUIC hace esperar al cliente (control de flujo, sin error).</summary>
    public const int MaxQuicStreamsPerConnection = 256;

    /// <summary>
    /// Plazos de lectura de un stream QUIC, los mismos que una trama a medias en TCP: sin bytes nuevos <see cref="PartialFrameTimeoutMs"/> y,
    /// en total, gracia + bytes recibidos / ritmo minimo. Un stream abierto que no envia nada ya no retiene uno de sus 256 huecos.
    /// </summary>
    private readonly record struct StreamDeadline(int TimeoutMs, int MinDataRate, int GraceMs);

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
        TlsMode tls = TlsMode.Default,
        CertificateCheck check = CertificateCheck.Pinned,
        int partialFrameTimeoutMs = PartialFrameTimeoutMs,
        int minDataRate = MinDataRateBytesPerSecond,
        int dataRateGraceMs = DataRateGraceMs)
        where THandler : struct, IRequestHandler
    {
        // QUIC siempre cifra: Default es TLS mutuo, como TCP con certificado.
        if (tls is TlsMode.None) throw new ArgumentException("QUIC always uses TLS.", nameof(tls));

        var deadline = new StreamDeadline(partialFrameTimeoutMs, minDataRate, dataRateGraceMs);

        QuicServerConnectionOptions connectionOptions = new()
        {
            IdleTimeout = TimeSpan.FromMinutes(5),
            MaxInboundBidirectionalStreams = MaxQuicStreamsPerConnection,
            MaxInboundUnidirectionalStreams = 10,
            DefaultStreamErrorCode = 0x0A,
            DefaultCloseErrorCode = 0x0B,
            ServerAuthenticationOptions = TlsPolicy.ForServer(cert, tls is TlsMode.Default ? TlsMode.Mutual : tls, check, SslProtocols.Tls12 | SslProtocols.Tls13)!
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


        ListenConnections(listener, handlers, onFinalize, deadline, token);

        return listener;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static async void ListenConnections(
            QuicListener listener,
            THandler handlers,
            Action onFinalize,
            StreamDeadline deadline,
            CancellationToken token)
        {
            try
            {
            DO: HandleConnectionAsync(await listener.AcceptConnectionAsync(token).ConfigureAwait(false), handlers, deadline, token); goto DO;
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
            StreamDeadline deadline,
            CancellationToken token)
        {
            await using (connection)
            {
                try
                {
                DO: _ = HandleStreamAsync(await connection.AcceptInboundStreamAsync(token).ConfigureAwait(false), handlers, deadline, token); goto DO;
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
            StreamDeadline deadline,
            CancellationToken token)
        {
            await using (stream)
            {
                try
                {
                    long started = Stopwatch.GetTimestamp();
                    var reader = PipeReader.Create(stream);
                    // Los 8 primeros bytes tambien tienen plazo: un stream abierto que no envia nada no retiene su hueco.
                    var head = await ReadWithinAsync(reader, Framing.OpIdSize, deadline, started, 0, token).ConfigureAwait(false);
                    if (head.Buffer.Length >= Framing.OpIdSize && Framing.ReadOpId(head.Buffer) == MuxMarker)
                    {
                        reader.AdvanceTo(head.Buffer.GetPosition(Framing.OpIdSize));
                        // Multiplexado: como una conexion TCP (ocioso sin plazo; trama a medias con plazo y ritmo minimo).
                        await ServePipeAsync(reader, PipeWriter.Create(stream), handlers, token,
                            partialFrameTimeoutMs: deadline.TimeoutMs, minDataRate: deadline.MinDataRate, dataRateGraceMs: deadline.GraceMs).ConfigureAwait(false);
                    }
                    else
                    {
                        reader.AdvanceTo(head.Buffer.Start);
                        await ServeQuicStreamAsync(reader, PipeWriter.Create(stream), handlers, deadline, started, token).ConfigureAwait(false);
                    }
                }
                // Al salir, await using aborta el stream: el cliente ve el corte.
                catch (Exception ex) when (ex is OperationCanceledException or IOException or QuicException or InvalidDataException or TimeoutException)
                {
                }
            }
        }
    }

    /// <summary>
    /// Lectura de un stream QUIC con plazo (<see cref="StreamDeadline"/>). Con datos ya disponibles no asigna nada; solo una lectura que espera
    /// arma un temporizador que la cancela (<c>CancelPendingRead</c>).
    /// </summary>
    private static ValueTask<ReadResult> ReadWithinAsync(PipeReader reader, int minimumSize, StreamDeadline deadline, long started, long received, CancellationToken token)
    {
        var read = minimumSize > 0 ? reader.ReadAtLeastAsync(minimumSize, token) : reader.ReadAsync(token);
        if (read.IsCompleted) return read;

        long timeoutMs = deadline.TimeoutMs < 0 ? int.MaxValue : deadline.TimeoutMs;

        if (deadline.MinDataRate > 0)
            timeoutMs = Math.Min(timeoutMs, deadline.GraceMs + received * 1000 / deadline.MinDataRate - (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);

        return timeoutMs >= int.MaxValue ? read : WaitAsync(reader, read, (int)Math.Max(1, timeoutMs));

        // CTS reutilizados (TryReset conserva su temporizador y su nodo de registro) y caja de la maquina pooled: sin asignar en regimen.
        // ponytail: pool sin limite (= pico de lecturas QUIC esperando a la vez), mismo patron que RequestContext.
        [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
        static async ValueTask<ReadResult> WaitAsync(PipeReader reader, ValueTask<ReadResult> read, int timeoutMs)
        {
            var timeout = s_deadlines.TryTake(out var pooled) ? pooled : new CancellationTokenSource();
            ReadResult result;
            bool expired;

            try
            {
                using (timeout.Token.UnsafeRegister(static r => ((PipeReader)r!).CancelPendingRead(), reader))
                {
                    timeout.CancelAfter(timeoutMs);
                    result = await read.ConfigureAwait(false);
                }

                expired = timeout.IsCancellationRequested;
            }
            finally
            {
                if (timeout.TryReset()) s_deadlines.Add(timeout);
                else timeout.Dispose();
            }

            // Vencido: aunque la lectura trajera datos, la cancelacion ya pendiente cortaria la siguiente; el plazo se cumplio igual.
            if (expired) throw new TimeoutException("QUIC stream below the minimum data rate.");

            return result;
        }
    }

    private static readonly System.Collections.Concurrent.ConcurrentBag<CancellationTokenSource> s_deadlines = [];

    /// <summary>
    /// Un stream QUIC es un RPC: la peticion <c>[opId][cuerpo]</c> la delimita el FIN y el propio
    /// stream empareja las respuestas <c>[len][estado][cuerpo]</c>, asi que no hace falta corrId.
    /// </summary>
    private static async Task ServeQuicStreamAsync<THandler>(PipeReader reader, PipeWriter writer, THandler handlers, StreamDeadline deadline, long started, CancellationToken token)
        where THandler : struct, IRequestHandler
    {
        try
        {
            ReadResult result;
            long received = 0;

            while (!(result = await ReadWithinAsync(reader, 0, deadline, started, received, token).ConfigureAwait(false)).IsCompleted)
            {
                received = result.Buffer.Length;

                // Limite de la operacion en cuanto se conoce (primeros 8 bytes): no se retiene un cuerpo mayor hasta el FIN.
                if (received >= Framing.OpIdSize)
                    CheckSize(handlers, Framing.ReadOpId(result.Buffer), (int)Math.Min(received - Framing.OpIdSize, int.MaxValue));

                reader.AdvanceTo(result.Buffer.Start, result.Buffer.End);
            }

            var request = result.Buffer;

            if (request.Length is < Framing.OpIdSize or > Framing.MaxFrameSize) throw new InvalidDataException($"Invalid request length: {request.Length}.");

            long op = Framing.ReadOpId(request);
            int length = (int)request.Length - Framing.OpIdSize;
            CheckSize(handlers, op, length);
            byte[] body = ArrayPool<byte>.Shared.Rent(length);
            request.Slice(Framing.OpIdSize).CopyTo(body);
            reader.AdvanceTo(request.End);

            await DispatchAsync(handlers, op, RequestContext.Rent(0, body.AsMemory(0, length), new ResponseChannel(writer, correlated: false), token), body, null, token).ConfigureAwait(false);
        }
        finally
        {
            await reader.CompleteAsync().ConfigureAwait(false);
            await writer.CompleteAsync().ConfigureAwait(false);
        }
    }
}


