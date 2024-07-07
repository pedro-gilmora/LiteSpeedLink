using System.Net;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;

namespace SourceCrafter.LiteSpeedLink.Client;

public static partial class ClientExtensions
{
    [RequiresPreviewFeatures]
    [SupportedOSPlatform("windows")]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    public static IConnection AsQuicConnection(this EndPoint ip, X509Certificate2 cert) => new QuicConnection(new()
    {
        RemoteEndPoint = ip,
        DefaultStreamErrorCode = 0x0A,
        DefaultCloseErrorCode = 0x0B,
        ClientAuthenticationOptions = new()
        {
            ApplicationProtocols = [Constants.protocol],
            RemoteCertificateValidationCallback = (sender, certificate, chain, sslPolicyErrors) => true
        }
    });

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static IConnection AsUdpConnection(this EndPoint ip) => new UdpConnection(ip);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static IConnection AsTcpConnection(this EndPoint ip, System.Security.Cryptography.X509Certificates.X509Certificate2? cert = null) => new TcpConnection(ip, cert);
}
