using System.IO.Pipelines;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace SourceCrafter.LiteSpeedLink;

public static partial class Server
{
    public static TcpListener StartTcpServer(
        int port,
        RequestHandler handlers,
        Action onFinalize,
        X509Certificate2? cert = default,
        CancellationToken token = default,
        int maxInFlightPerConnection = MaxInFlightPerConnection)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxInFlightPerConnection, 1);

        var tcpServer = TcpListener.Create(port);

        tcpServer.Server.NoDelay = true;

        tcpServer.Start();

        ListenClientsAsync(tcpServer, handlers, onFinalize, cert, maxInFlightPerConnection, token);

        return tcpServer;

        static async void ListenClientsAsync(
            TcpListener tcpServer,
            RequestHandler handlers,
            Action onFinalize,
            X509Certificate2? cert,
            int limit,
            CancellationToken token)
        {
            try
            {
                while (true)
                {
                    _ = HandleConnectionAsync(await tcpServer.AcceptTcpClientAsync(token).ConfigureAwait(false), handlers, cert, limit, token);
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
            RequestHandler handlers,
            X509Certificate2? cert,
            int limit,
            CancellationToken token)
        {
            using var client = tcpClient;

            client.NoDelay = true;

            try
            {
                Stream stream = client.GetStream();

                if (cert != null)
                {
                    var ssl = new SslStream(stream, false);

                    await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                    {
                        EnabledSslProtocols = SslProtocols.Tls12,
                        ServerCertificate = cert,
                        ApplicationProtocols = [Constants.protocol, Constants.protocolStream],
                        ClientCertificateRequired = true,
                        CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                        // Pinning: valido por cadena o si es exactamente el certificado configurado (autofirmado de desarrollo).
                        RemoteCertificateValidationCallback = (_, remote, _, errors) =>
                            errors == SslPolicyErrors.None || remote?.GetCertHashString() == cert.GetCertHashString()
                    }, token).ConfigureAwait(false);

                    stream = ssl;
                }

                await using (stream.ConfigureAwait(false))
                {
                    await ServePipeAsync(PipeReader.Create(stream), PipeWriter.Create(stream), handlers, token, limit).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or AuthenticationException)
            {
            }
        }
    }
}
