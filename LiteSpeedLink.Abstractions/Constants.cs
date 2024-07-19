using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace SourceCrafter.LiteSpeedLink;

public readonly struct Constants
{
    public readonly static SslApplicationProtocol
        protocol = new("lsl"),
        protocolStream = new("lsl-stream");

    public static X509Certificate2 GetDevCert()
    {
        string path = Path.Combine(Directory.GetCurrentDirectory(), "localhost.pfx");

        X509Certificate2 certificate = null!;

        if (!File.Exists(path))
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "powershell",
                Arguments = $@"-Command ""New-SelfSignedCertificate -DnsName 'localhost' -CertStoreLocation 'cert:\\LocalMachine\\My' | Export-PfxCertificate -FilePath '{path}' -Password (ConvertTo-SecureString -String 'D34lW17h' -AsPlainText -Force); Import-PfxCertificate -FilePath '{path}' -CertStoreLocation Cert:\\LocalMachine\\My -Password (ConvertTo-SecureString -String 'D34lW17h' -AsPlainText -Force)""",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            System.Diagnostics.Process.Start(psi)?.WaitForExit();

            using X509Store store = new ("teststore", StoreLocation.LocalMachine);

            certificate = new(path, "D34lW17h");

            store.Open(OpenFlags.ReadWrite);

            store.Add(certificate);

            store.Close();
        }

        return certificate ?? new(path, "D34lW17h");
    }
}