using System.Net;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;

namespace SourceCrafter.LiteSpeedLink.Client;

public static partial class ClientExtensions
{
    [RequiresPreviewFeatures]
    [SupportedOSPlatform("windows")]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static QuicConnection AsQuicConnection(this EndPoint ip, X509Certificate2? cert = default) => new(new()
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
    public static UdpConnection AsUdpConnection(this EndPoint ip) => new(ip);

    [RequiresPreviewFeatures]
    [SupportedOSPlatform("windows")]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static MemoryConnection ToMemoryConnection(this string ctxName, int timeout = 10, System.Text.Encoding? encoding = null) => new(ctxName, timeout, encoding);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static TcpConnection AsTcpConnection(this EndPoint ip, X509Certificate2? cert = null) => new(ip, cert);
}
