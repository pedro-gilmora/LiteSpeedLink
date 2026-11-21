using System.Buffers.Binary;
using System.IO.Pipelines;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using FluentAssertions;
using SourceCrafter.LiteSpeedLink;
using SourceCrafter.LiteSpeedLink.Client;
using Xunit;

namespace LiteSpeedLink.Tests;

/// <summary>Revision de seguridad (rama security/hardening): un test por hallazgo corregido.</summary>
public class SecurityHardeningTest
{
    const string Cert = "global::System.Security.Cryptography.X509Certificates.X509Certificate2";

    static X509Certificate2 OtherCert()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        // Recargado desde PFX: SChannel no usa claves efimeras como certificado de cliente.
        return X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pfx, ""), "", X509KeyStorageFlags.Exportable);
    }

    static CancellationToken Within(int ms = 5000) => new CancellationTokenSource(ms).Token;

    /// <summary>Pinning por SHA-256 y modos de validacion fijados en compilacion.</summary>
    [Fact]
    public void ValidatorHonorsTheConfiguredCheck()
    {
        using var pinned = Constants.GetDevCert();
        using var other = OtherCert();

        var pinnedOnly = TlsPolicy.Validator(pinned, CertificateCheck.Pinned);
        pinnedOnly(this, pinned, null, SslPolicyErrors.RemoteCertificateChainErrors).Should().BeTrue();
        pinnedOnly(this, other, null, SslPolicyErrors.None).Should().BeFalse();

        var chainOrPinned = TlsPolicy.Validator(pinned, CertificateCheck.ChainOrPinned);
        chainOrPinned(this, other, null, SslPolicyErrors.None).Should().BeTrue();
        chainOrPinned(this, other, null, SslPolicyErrors.RemoteCertificateChainErrors).Should().BeFalse();

        TlsPolicy.Validator(null, CertificateCheck.Chain)(this, pinned, null, SslPolicyErrors.RemoteCertificateChainErrors).Should().BeFalse();
    }

    /// <summary>Hallazgo 1: el cliente QUIC aceptaba cualquier certificado; ahora valida el host y el host exige el del cliente.</summary>
    [Fact]
    [System.Runtime.Versioning.RequiresPreviewFeatures]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public async Task QuicRejectsAnUnpinnedHostAndAClientWithoutCertificate()
    {
        if (!QuicListener.IsSupported) return;

        var cert = Constants.GetDevCert();
        await using var server = await Server.StartQuicServerAsync(0, (op, ctx, _) => ctx.ReturnAsync(1), () => { }, cert);
        var endpoint = new DnsEndPoint("localhost", server.LocalEndPoint.Port);

        using var other = OtherCert();
        await using (var wrongPin = endpoint.AsQuicConnection(other))
        {
            var call = async () => await wrongPin.GetAsync<int>(0, Within());
            await call.Should().ThrowAsync<Exception>();
        }

        await using var withoutClientCert = endpoint.AsQuicConnection(cert, tls: TlsMode.Server);
        var anonymous = async () => await withoutClientCert.GetAsync<int>(0, Within());
        await anonymous.Should().ThrowAsync<Exception>();
    }

    /// <summary>TLS solo del host (fijado en compilacion): el cliente no presenta certificado y valida el pin.</summary>
    [Fact]
    public async Task TcpServerModeAuthenticatesOnlyTheHost()
    {
        var cert = Constants.GetDevCert();
        using var server = Server.StartTcpServer(0, (op, ctx, _) => ctx.ReturnAsync(ctx.Get<int>() * 2), () => { }, cert, tls: TlsMode.Server, check: CertificateCheck.Pinned);
        var endpoint = new DnsEndPoint("localhost", ((IPEndPoint)server.LocalEndpoint).Port);

        await using (var pinned = endpoint.AsTcpConnection(cert, TlsMode.Server, CertificateCheck.Pinned))
            (await pinned.GetAsync<int, int>(0, 21, Within())).Should().Be(42);

        using var other = OtherCert();
        await using var wrongPin = endpoint.AsTcpConnection(other, TlsMode.Server, CertificateCheck.Pinned);
        var call = async () => await wrongPin.GetAsync<int, int>(0, 1, Within());
        await call.Should().ThrowAsync<Exception>();
    }

    /// <summary>Hallazgo 3: una trama a medias sin bytes nuevos cierra la conexion; una conexion ociosa no.</summary>
    [Fact]
    public async Task APartialFrameTimesOutButAnIdleConnectionDoesNot()
    {
        var input = new Pipe();
        var output = new Pipe();
        var serving = Server.ServePipeAsync(input.Reader, output.Writer, new DelegateRequestHandler((op, ctx, _) => ctx.ReturnAsync(0)), default, partialFrameTimeoutMs: 200);

        await Task.Delay(600);
        serving.IsCompleted.Should().BeFalse();

        var header = new byte[6];
        BinaryPrimitives.WriteInt32LittleEndian(header, 100);
        await input.Writer.WriteAsync(header);

        await serving.WaitAsync(TimeSpan.FromSeconds(5));
    }

    /// <summary>Hallazgo 3: con el limite de conexiones lleno, la siguiente espera en el backlog hasta que se libera una.</summary>
    [Fact]
    public async Task TheConnectionCapQueuesExtraConnections()
    {
        using var server = Server.StartTcpServer(0, (op, ctx, _) => ctx.ReturnAsync(ctx.Get<int>()), () => { }, maxConnections: 1);
        var endpoint = new IPEndPoint(IPAddress.Loopback, ((IPEndPoint)server.LocalEndpoint).Port);

        var first = new TcpConnection(endpoint);
        (await first.GetAsync<int, int>(0, 1, Within())).Should().Be(1);

        await using var second = new TcpConnection(endpoint);
        var pending = second.GetAsync<int, int>(0, 2, Within(10_000)).AsTask();

        await Task.Delay(500);
        pending.IsCompleted.Should().BeFalse();

        await first.DisposeAsync();
        (await pending.WaitAsync(TimeSpan.FromSeconds(5))).Should().Be(2);
    }

    /// <summary>Hallazgo 6: un corrId repetido con su stream vivo se rechaza sin pisar ni filtrar el CTS del primero, que sigue cancelable.</summary>
    [Fact]
    public async Task ADuplicateStreamIdIsRejected()
    {
        var input = new Pipe();
        var output = new Pipe();
        var serving = Server.ServePipeAsync(input.Reader, output.Writer, new DelegateRequestHandler((op, ctx, _) => ctx.EnumerateAsync(Forever())), default);

        await input.Writer.WriteAsync(Frame(7, Framing.OpIdSize));
        await input.Writer.WriteAsync(Frame(7, Framing.OpIdSize));

        var statuses = new List<ResponseStatus>();
        while (!statuses.Contains(ResponseStatus.Failed))
        {
            var result = await output.Reader.ReadAsync(Within());
            var buffer = result.Buffer;

            while (Framing.TryReadFrame(ref buffer, out int corrId, out var content))
            {
                corrId.Should().Be(7);
                statuses.Add(Framing.ReadStatus(content));
            }

            output.Reader.AdvanceTo(buffer.Start, buffer.End);
        }

        // Cancelacion del cliente: si el CTS del primero se hubiera pisado, su productor no pararia y el drenaje colgaria.
        await input.Writer.WriteAsync(Frame(7, 0));
        await input.Writer.CompleteAsync();
        await serving.WaitAsync(TimeSpan.FromSeconds(5));

        static byte[] Frame(int corrId, int contentLength)
        {
            var frame = new byte[Framing.FrameHeaderSize + contentLength];
            Framing.WriteFrameHeader(frame, corrId, contentLength);
            if (contentLength > 0) Framing.WriteOpId(frame.AsSpan(Framing.FrameHeaderSize), 1);
            return frame;
        }

        static async IAsyncEnumerable<int> Forever([EnumeratorCancellation] CancellationToken token = default)
        {
            yield return 1;
            await Task.Delay(Timeout.Infinite, token);
        }
    }

    /// <summary>Hallazgo 5: fuera de Windows el socket de Local vive en un directorio 0700; uno existente que no lo sea se rechaza.</summary>
    [Fact]
    public void LocalSocketDirectoryMustBePrivate()
    {
        if (OperatingSystem.IsWindows())
        {
            LocalSocket.GetPath("probe").Should().Be(Path.Combine(Path.GetTempPath(), "probe.lsl"));
            return;
        }

        const UnixFileMode Private = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        var ensure = typeof(LocalSocket).GetMethod("EnsurePrivate", BindingFlags.NonPublic | BindingFlags.Static)!;
        var dir = Path.Combine(Path.GetTempPath(), "lsl-test-" + Guid.NewGuid().ToString("N"));

        try
        {
            ensure.Invoke(null, [dir]);
            File.GetUnixFileMode(dir).Should().Be(Private);

            File.SetUnixFileMode(dir, Private | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            var reuse = () => ensure.Invoke(null, [dir]);
            reuse.Should().Throw<TargetInvocationException>().WithInnerException<UnauthorizedAccessException>();
        }
        finally
        {
            Directory.Delete(dir);
        }
    }

    const string Source = """
        using SourceCrafter.DependencyInjection.Attributes;
        using SourceCrafter.LiteSpeedLink;

        namespace Probe;

        public interface IContract : IServiceUnit
        {
            int Echo(int value);
        }

        public sealed class Contract : IContract
        {
            public int Echo(int value) => value;
        }

        [ServiceHost(ServiceConnectionType.TRANSPORT TLS)]
        [ServiceProvider]
        [Singleton<IContract, Contract>]
        public sealed partial class Host;

        [ServiceClient(ServiceConnectionType.TRANSPORT TLS)]
        [ServiceUnit<IContract>]
        [ServiceProvider]
        public sealed partial class Client;
        """;

    static GeneratorHarness.Result Generate(string transport, string tls = "", string? target = "net10.0-windows") => GeneratorHarness.Run(
        Source.Replace("TRANSPORT", transport).Replace("TLS", tls),
        target is null ? null : new Dictionary<string, string> { ["build_property.TargetFramework"] = target });

    static string HostOf(GeneratorHarness.Result r) => r.Sources.Single(s => s.Key.Contains("Host.host")).Value;

    static string ClientOf(GeneratorHarness.Result r) => r.Sources.Single(s => s.Key.Contains("Client.client")).Value;

    /// <summary>TLS fijado en compilacion: firma exacta (certificado obligatorio) y constantes para Kestrel.</summary>
    [Fact]
    public void MutualPinnedTcpIsFixedAtCompileTime()
    {
        var r = Generate("Tcp", ", Tls = TlsMode.Mutual, Check = CertificateCheck.Pinned");

        r.Errors.Should().BeEmpty();
        HostOf(r).Should().Contain(Cert + " certificate,").And.NotContain(Cert + "? certificate")
            .And.Contain("IHostedLiteSpeedLink.Tls => global::SourceCrafter.LiteSpeedLink.TlsMode.Mutual")
            .And.Contain("tls: global::SourceCrafter.LiteSpeedLink.TlsMode.Mutual, check: global::SourceCrafter.LiteSpeedLink.CertificateCheck.Pinned");
        ClientOf(r).Should().Contain("(string hostname, int port, " + Cert + " certificate)");
    }

    /// <summary>Solo el host con validacion por cadena: el cliente no recibe certificado.</summary>
    [Fact]
    public void ServerChainClientTakesNoCertificate()
    {
        var r = Generate("Tcp", ", Tls = TlsMode.Server, Check = CertificateCheck.Chain");

        r.Errors.Should().BeEmpty();
        ClientOf(r).Should().Contain("(string hostname, int port)")
            .And.Contain(".AsTcpConnection(tls: global::SourceCrafter.LiteSpeedLink.TlsMode.Server");
    }

    /// <summary>Hallazgo 1: el cliente QUIC generado recibe el certificado (pin y certificado de cliente).</summary>
    [Fact]
    public void QuicClientTakesTheCertificate()
    {
        var r = Generate("Quic");

        r.Errors.Should().BeEmpty();
        ClientOf(r).Should().Contain("(string hostname, int port, " + Cert + " certificate)")
            .And.Contain(".AsQuicConnection(certificate, tls: global::SourceCrafter.LiteSpeedLink.TlsMode.Default");
    }

    [Fact]
    public void QuicWithoutTlsIsRejected()
        => Generate("Quic", ", Tls = TlsMode.None").HasDiagnostic("SCLSL022").Should().BeTrue();

    /// <summary>Hallazgo 4: quien arranca el host UDP puede fijar el limite de amplificacion.</summary>
    [Fact]
    public void UdpHostExposesMaxAmplification()
    {
        var r = Generate("Udp");

        r.Errors.Should().BeEmpty();
        HostOf(r).Should().Contain("int maxAmplification = 0").And.Contain("maxAmplification: maxAmplification");
    }

    /// <summary>Hallazgo 5: Local fuera de Windows deriva la ruta del directorio privado.</summary>
    [Fact]
    public void LocalOnLinuxUsesThePrivateSocketPath()
    {
        var r = Generate("Local", target: "net10.0-linux");

        r.Errors.Should().BeEmpty();
        HostOf(r).Should().Contain("global::SourceCrafter.LiteSpeedLink.LocalSocket.GetPath(name)");
        ClientOf(r).Should().Contain("global::SourceCrafter.LiteSpeedLink.LocalSocket.GetPath(name)");
    }
}
