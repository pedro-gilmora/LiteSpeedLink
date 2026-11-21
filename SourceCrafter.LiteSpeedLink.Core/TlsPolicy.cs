using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace SourceCrafter.LiteSpeedLink;

/// <summary>
/// Opciones TLS construidas una vez por servidor o cliente (no por conexion) a partir de lo que el generador fija en
/// compilacion (<see cref="TlsMode"/>, <see cref="CertificateCheck"/>). El pinning compara SHA-256 en la pila, sin asignar.
/// </summary>
public static class TlsPolicy
{
    private const int Sha256Size = 32;

    /// <summary><c>null</c> = sin TLS (<see cref="TlsMode.None"/>, o <see cref="TlsMode.Default"/> sin certificado).</summary>
    public static SslServerAuthenticationOptions? ForServer(X509Certificate2? certificate, TlsMode tls, CertificateCheck check, SslProtocols protocols)
    {
        if (tls is TlsMode.None || (tls is TlsMode.Default && certificate is null)) return null;

        ArgumentNullException.ThrowIfNull(certificate);

        // Fallo al arrancar y no en cada handshake: sin clave privada SslStream no puede presentar el certificado.
        if (!certificate.HasPrivateKey) throw new ArgumentException($"The server certificate '{certificate.Subject}' has no private key.", nameof(certificate));

        bool mutual = tls is not TlsMode.Server;

        return new()
        {
            ServerCertificate = certificate,
            EnabledSslProtocols = protocols,
            ApplicationProtocols = [Constants.protocol, Constants.protocolStream],
            ClientCertificateRequired = mutual,
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            RemoteCertificateValidationCallback = mutual ? Validator(certificate, check) : null
        };
    }

    /// <summary><c>null</c> = sin TLS. <paramref name="certificate"/> es el pin y, en <see cref="TlsMode.Mutual"/>, el certificado del cliente.</summary>
    public static SslClientAuthenticationOptions? ForClient(EndPoint endpoint, X509Certificate2? certificate, TlsMode tls, CertificateCheck check, SslProtocols protocols, List<SslApplicationProtocol>? applicationProtocols = null)
    {
        if (tls is TlsMode.None || (tls is TlsMode.Default && certificate is null)) return null;

        bool mutual = tls is not TlsMode.Server;

        if (mutual)
        {
            ArgumentNullException.ThrowIfNull(certificate);

            if (!certificate.HasPrivateKey) throw new ArgumentException($"The client certificate '{certificate.Subject}' has no private key.", nameof(certificate));
        }

        return new()
        {
            TargetHost = endpoint switch
            {
                DnsEndPoint dns => dns.Host,
                IPEndPoint ip => ip.Address.ToString(),
                _ => throw new ArgumentException($"Unsupported endpoint: [{endpoint}]", nameof(endpoint))
            },
            ClientCertificates = mutual ? [certificate!] : null,
            EnabledSslProtocols = protocols,
            ApplicationProtocols = applicationProtocols,
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            RemoteCertificateValidationCallback = Validator(certificate, check)
        };
    }

    /// <summary>Validacion del certificado remoto segun <paramref name="check"/>; el pin se calcula aqui, una vez.</summary>
    public static RemoteCertificateValidationCallback Validator(X509Certificate2? pinned, CertificateCheck check)
    {
        if (check is CertificateCheck.Chain) return static (_, _, _, errors) => errors == SslPolicyErrors.None;

        ArgumentNullException.ThrowIfNull(pinned);

        byte[] pin = pinned.GetCertHash(HashAlgorithmName.SHA256);

        return check is CertificateCheck.Pinned
            ? (_, remote, _, _) => IsPinned(remote, pin)
            : (_, remote, _, errors) => errors == SslPolicyErrors.None || IsPinned(remote, pin);
    }

    private static bool IsPinned(X509Certificate? remote, byte[] pin)
    {
        Span<byte> hash = stackalloc byte[Sha256Size];

        return remote is not null
            && remote.TryGetCertHash(HashAlgorithmName.SHA256, hash, out int written)
            && hash[..written].SequenceEqual(pin);
    }
}
