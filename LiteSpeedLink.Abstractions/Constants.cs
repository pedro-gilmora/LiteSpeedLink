using System.ComponentModel;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace SourceCrafter.LiteSpeedLink;

public readonly struct Constants
{
    public readonly static SslApplicationProtocol
        protocol = new("lsl"),
        protocolStream = new("lsl-stream");

    /// <summary>
    /// Obtiene (o genera) un certificado de desarrollo autofirmado.
    /// No depende de PowerShell ni del almac\u00e9n LocalMachine: es multiplataforma y no requiere elevaci\u00f3n.
    /// </summary>
    public static X509Certificate2 GetDevCert(string certName = "localhost", string storeName = "teststore", string passwd = "D34lW17h")
    {
        string path = Path.Combine(Directory.GetCurrentDirectory(), $"{certName}.pfx");

        if (File.Exists(path))
        {
            return X509CertificateLoader.LoadPkcs12FromFile(path, passwd, X509KeyStorageFlags.Exportable);
        }

        using RSA rsa = RSA.Create(2048);

        CertificateRequest request = new($"CN={certName}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));

        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));

        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));

        SubjectAlternativeNameBuilder sanBuilder = new();
        sanBuilder.AddDnsName(certName);
        request.CertificateExtensions.Add(sanBuilder.Build());

        var now = DateTimeOffset.UtcNow;

        using X509Certificate2 generated = request.CreateSelfSigned(now.AddDays(-1), now.AddYears(1));

        File.WriteAllBytes(path, generated.Export(X509ContentType.Pfx, passwd));

        return X509CertificateLoader.LoadPkcs12FromFile(path, passwd, X509KeyStorageFlags.Exportable);
    }
}
//public interface IMaybe<T>;

//public readonly ref struct Some<T>(T value) : IMaybe<T>
//{
//    public readonly T Value { get; } = value;
//}

//public readonly ref struct Nothing<T> : IMaybe<T>;
//public readonly ref struct Error<T>(Exception value) : IMaybe<T>
//{
//    public readonly Exception Value { get; } = value;
//}
