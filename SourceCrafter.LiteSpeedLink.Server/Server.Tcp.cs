using System.IO.Pipelines;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace SourceCrafter.LiteSpeedLink;

public static partial class Server
{
    /// <summary>Limite por defecto de conexiones simultaneas por servidor TCP/UDS; las demas esperan en el backlog del SO.</summary>
    public const int MaxConnections = 1024;

    /// <summary>
    /// Conexiones simultaneas por direccion remota (IPv6: por /64): una direccion ocupa como mucho 1/16 de <see cref="MaxConnections"/>,
    /// asi que bloquear a todos exige al menos 16 direcciones; un NAT con hasta 64 clientes (una conexion multiplexada cada uno) sigue entrando.
    /// </summary>
    public const int MaxConnectionsPerAddress = MaxConnections / 16;

    /// <summary>Plazo del handshake TLS: un cliente que no lo completa no retiene la conexion.</summary>
    public const int TlsHandshakeTimeoutMs = 10_000;

    public static TcpListener StartTcpServer(
        int port,
        RequestHandler handlers,
        Action onFinalize,
        X509Certificate2? cert = default,
        CancellationToken token = default,
        int maxInFlightPerConnection = MaxInFlightPerConnection,
        int maxConnections = MaxConnections,
        TlsMode tls = TlsMode.Default,
        CertificateCheck check = CertificateCheck.ChainOrPinned,
        int maxConnectionsPerAddress = MaxConnectionsPerAddress,
        int idleTimeoutMs = IdleTimeoutMs)
        => StartTcpServer(port, new DelegateRequestHandler(handlers), onFinalize, cert, token, maxInFlightPerConnection, maxConnections, tls, check, maxConnectionsPerAddress, idleTimeoutMs);

    public static TcpListener StartTcpServer<THandler>(
        int port,
        THandler handlers,
        Action onFinalize,
        X509Certificate2? cert = default,
        CancellationToken token = default,
        int maxInFlightPerConnection = MaxInFlightPerConnection,
        int maxConnections = MaxConnections,
        TlsMode tls = TlsMode.Default,
        CertificateCheck check = CertificateCheck.ChainOrPinned,
        int maxConnectionsPerAddress = MaxConnectionsPerAddress,
        int idleTimeoutMs = IdleTimeoutMs)
        where THandler : struct, IRequestHandler
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxInFlightPerConnection, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConnections, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConnectionsPerAddress, 1);

        // Una vez por servidor: cada conexion reutiliza las mismas opciones y el mismo validador.
        var options = TlsPolicy.ForServer(cert, tls, check, SslProtocols.Tls12);

        var tcpServer = TcpListener.Create(port);
        tcpServer.Server.NoDelay = true;

        tcpServer.Start();

        ListenClientsAsync(tcpServer, handlers, onFinalize, options, maxInFlightPerConnection, idleTimeoutMs, new SemaphoreSlim(maxConnections), new AddressLimiter(maxConnectionsPerAddress), token);

        return tcpServer;

        static async void ListenClientsAsync(
            TcpListener tcpServer,
            THandler handlers,
            Action onFinalize,
            SslServerAuthenticationOptions? options,
            int limit,
            int idle,
            SemaphoreSlim connections,
            AddressLimiter addresses,
            CancellationToken token)
        {
            try
            {
                while (true)
                {
                    // Lleno: no se acepta; el SO deja las conexiones nuevas en el backlog.
                    await connections.WaitAsync(token).ConfigureAwait(false);

                    TcpClient client;
                    try { client = await tcpServer.AcceptTcpClientAsync(token).ConfigureAwait(false); }
                    catch { connections.Release(); throw; }

                    // Direccion por encima de su limite: se cierra ya (esperar dejaria a una direccion parar el bucle de todos).
                    IPAddress? address;
                    try
                    {
                        if (!addresses.TryEnter(client.Client.RemoteEndPoint, out address))
                        {
                            client.Dispose();
                            connections.Release();
                            continue;
                        }
                    }
                    catch (SocketException)
                    {
                        // El cliente ya se fue (RemoteEndPoint de un socket reseteado).
                        client.Dispose();
                        connections.Release();
                        continue;
                    }

                    _ = HandleConnectionAsync(client, handlers, options, limit, idle, connections, addresses, address, token);
                }
            }
            catch (Exception ex)
                when (ex is OperationCanceledException or ObjectDisposedException
                    or SocketException { SocketErrorCode: SocketError.ConnectionAborted or SocketError.OperationAborted or SocketError.Interrupted })
            {
            }
            finally
            {
                onFinalize();
            }
        }

        static async Task HandleConnectionAsync(
            TcpClient tcpClient,
            THandler handlers,
            SslServerAuthenticationOptions? options,
            int limit,
            int idle,
            SemaphoreSlim connections,
            AddressLimiter addresses,
            IPAddress? address,
            CancellationToken token)
        {
            using var client = tcpClient;

            try
            {
                // Dentro del try: si falla (socket ya reseteado por el par), el finally devuelve los huecos.
                client.NoDelay = true;

                Stream stream = client.GetStream();

                if (options != null)
                {
                    var ssl = new SslStream(stream, false);

                    // Al vencer, el using cierra el socket y aborta el handshake pendiente.
                    await ssl.AuthenticateAsServerAsync(options, token).WaitAsync(TimeSpan.FromMilliseconds(TlsHandshakeTimeoutMs), token).ConfigureAwait(false);

                    stream = ssl;
                }

                await using (stream.ConfigureAwait(false))
                {
                    await ServePipeAsync(PipeReader.Create(stream), PipeWriter.Create(stream), handlers, token, limit, idleTimeoutMs: idle).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or AuthenticationException or TimeoutException or SocketException)
            {
            }
            finally
            {
                addresses.Exit(address);
                connections.Release();
            }
        }
    }
}
