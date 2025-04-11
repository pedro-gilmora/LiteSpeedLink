using System.ComponentModel;
using System.Diagnostics;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace SourceCrafter.LiteSpeedLink;

public readonly struct Constants
{
    public readonly static SslApplicationProtocol
        protocol = new("lsl"),
        protocolStream = new("lsl-stream");

    public static X509Certificate2 GetDevCert(string certName = "localhost", string storeName = "teststore", string passwd = "D34lW17h")
    {
        string path = Path.Combine(Directory.GetCurrentDirectory(), $"{certName}.pfx");

        Console.WriteLine("Cert path: " + path);

        X509Certificate2 certificate = null!;

        if (!File.Exists(path))
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell",
                Arguments = $@"-Command ""New-SelfSignedCertificate -DnsName '{certName}' -CertStoreLocation 'cert:\\LocalMachine\\My' | Export-PfxCertificate -FilePath '{path}' -Password (ConvertTo-SecureString -String '{passwd}' -AsPlainText -Force); Import-PfxCertificate -FilePath '{path}' -CertStoreLocation Cert:\\LocalMachine\\My -Password (ConvertTo-SecureString -String '{passwd}' -AsPlainText -Force)""",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            Process.Start(psi)?.WaitForExit();

            using X509Store store = new (storeName, StoreLocation.LocalMachine);

            certificate = new(path, passwd);

            store.Open(OpenFlags.ReadWrite);

            store.Add(certificate);

            store.Close();
        }

        return certificate ?? new(path, passwd);
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
