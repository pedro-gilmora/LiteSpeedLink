using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace SourceCrafter.LiteSpeedLink.Client;

/// <summary>
/// <paramref name="cert"/>: pin del servidor y, en TLS mutuo, el certificado del cliente. <paramref name="tls"/>/<paramref name="check"/>
/// los fija el cliente generado en compilacion; las opciones TLS se construyen una vez, no en cada reconexion.
/// </summary>
public sealed class TcpConnection(EndPoint endpoint, X509Certificate2? cert = default, bool coalesceStreams = true, TlsMode tls = TlsMode.Default, CertificateCheck check = CertificateCheck.Pinned,
    Func<long, int>? maxMessageSize = null) : StreamConnection(coalesceStreams, maxMessageSize)
{
    private readonly SslClientAuthenticationOptions? _tls = TlsPolicy.ForClient(endpoint, cert, tls, check, SslProtocols.Tls12);
    private TcpClient? _client;

    private protected override async Task<(Stream, string)> OpenAsync()
    {
        var client = new TcpClient { NoDelay = true };
        try
        {
            switch (endpoint)
            {
                case DnsEndPoint { Host: string host, Port: int port }:
                    await client.ConnectAsync(host, port).ConfigureAwait(false);
                    break;

                case IPEndPoint ip:
                    await client.ConnectAsync(ip).ConfigureAwait(false);
                    break;

                default:
                    throw new Exception($"Unsupported endpoint: [{endpoint}]");
            }

            Stream stream = client.GetStream();

            if (_tls is not null)
            {
                var ssl = new SslStream(stream, false);

                await ssl.AuthenticateAsClientAsync(_tls).ConfigureAwait(false);

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
