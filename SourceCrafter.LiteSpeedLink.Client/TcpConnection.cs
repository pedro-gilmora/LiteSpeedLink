using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace SourceCrafter.LiteSpeedLink.Client;

public sealed class TcpConnection(EndPoint endpoint, X509Certificate2? cert = default, bool coalesceStreams = true) : StreamConnection(coalesceStreams)
{
    private TcpClient? _client;

    private protected override async Task<(Stream, string)> OpenAsync()
    {
        var client = new TcpClient { NoDelay = true };
        try
        {
            string targetHost;

            switch (endpoint)
            {
                case DnsEndPoint { Host: string host, Port: int port }:
                    await client.ConnectAsync(host, port).ConfigureAwait(false);
                    targetHost = host;
                    break;

                case IPEndPoint ip:
                    await client.ConnectAsync(ip).ConfigureAwait(false);
                    targetHost = ip.Address.ToString();
                    break;

                default:
                    throw new Exception($"Unsupported endpoint: [{endpoint}]");
            }

            Stream stream = client.GetStream();

            if (cert is not null)
            {
                var ssl = new SslStream(stream, false);

                await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = targetHost,
                    ClientCertificates = [cert],
                    EnabledSslProtocols = SslProtocols.Tls12,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                    // Pinning: valido por cadena o si es exactamente el certificado configurado (autofirmado de desarrollo).
                    RemoteCertificateValidationCallback = (_, remote, _, errors) =>
                        errors == SslPolicyErrors.None || remote?.GetCertHashString() == cert.GetCertHashString()
                }).ConfigureAwait(false);

                stream = ssl;
            }

            _client = client;
            return (stream, endpoint.ToString()!);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private protected override void Close() => _client?.Dispose();
}
